using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>Marketing permissions, audience consent, editor, confirmation, and unsubscribe flows.</summary>
public class MarketingBroadcastTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    public MarketingBroadcastTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() => _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private async Task<HttpClient> LoginAsOwnerAsync()
    {
        var client = CreateBrowser();
        var ok = await TryOwnerLoginAsync(client, "ChangeMe123!");
        if (!ok) ok = await TryOwnerLoginAsync(client, "Owner!Pass9");
        ok.Should().BeTrue("owner login should succeed");
        return client;
    }

    private static async Task<bool> TryOwnerLoginAsync(HttpClient client, string password)
    {
        var loginPage = await client.GetAsync("/Account/Login");
        var html = await loginPage.Content.ReadAsStringAsync();
        var token = ExtractToken(html);
        var loginResponse = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = "owner@dechimexprinthub.com",
            ["Password"] = password,
            ["__RequestVerificationToken"] = token
        }));
        if (loginResponse.StatusCode != HttpStatusCode.Redirect) return false;
        if ((loginResponse.Headers.Location?.ToString() ?? "").Contains("/admin/security/password"))
        {
            var page = await client.GetAsync("/admin/security/password");
            var changeToken = ExtractToken(await page.Content.ReadAsStringAsync());
            var changed = await client.PostAsync("/admin/security/password", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["CurrentPassword"] = password,
                ["NewPassword"] = "Owner!Pass9",
                ["ConfirmPassword"] = "Owner!Pass9",
                ["__RequestVerificationToken"] = changeToken
            }));
            changed.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }
        return true;
    }

    private static string ExtractToken(string html)
    {
        var tag = Regex.Match(html, "<input[^>]*__RequestVerificationToken[^>]*>").Value;
        return Regex.Match(tag, "value=\"([^\"]+)\"").Groups[1].Value;
    }

    private async Task<string> HarvestAntiForgeryTokenAsync(HttpClient client, string path)
    {
        var page = await client.GetAsync(path);
        page.StatusCode.Should().Be(HttpStatusCode.OK, $"page {path} should render");
        var token = ExtractToken(await page.Content.ReadAsStringAsync());
        token.Should().NotBeNullOrWhiteSpace($"an antiforgery token should be present on {path}");
        return token;
    }

    private static Dictionary<string, string> EditorFields(string name, string subject, string blocksJson) => new()
    {
        ["Name"] = name,
        ["Subject"] = subject,
        ["SenderName"] = "DE-CHIMEX PRINT HUB",
        ["AudienceSegment"] = "0",
        ["BlocksJson"] = blocksJson
    };

    private async Task<Guid> CreateCampaignAsync(HttpClient client, string name, string subject, string blocksJson)
    {
        var token = await HarvestAntiForgeryTokenAsync(client, "/admin/marketing/compose");
        var values = EditorFields(name, subject, blocksJson);
        values["__RequestVerificationToken"] = token;
        var response = await client.PostAsync("/admin/marketing/compose", new FormUrlEncodedContent(values));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return Guid.Parse(response.Headers.Location!.ToString().Split('/').Last());
    }

    [Fact]
    public async Task Unsubscribe_UnknownToken_ShowsInvalidLink()
    {
        var client = CreateBrowser();
        var response = await client.GetAsync($"/unsubscribe/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Link not recognised");
    }

    [Fact]
    public async Task Unsubscribe_ValidToken_OptsCustomerOutWithoutLogin()
    {
        var email = $"unsub{Guid.NewGuid():N}@test.local";
        await TestSeed.RegisterCustomerAsync(_factory.Services, email);
        Guid token;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var customer = await db.Customers.SingleAsync(c => c.Email!.Value == email);
            customer.MarketingSubscribed.Should().BeTrue();
            token = customer.UnsubscribeToken;
        }

        var client = CreateBrowser();
        var get = await client.GetAsync($"/unsubscribe/{token}");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await get.Content.ReadAsStringAsync();
        page.Should().Contain("Unsubscribe from marketing emails?");
        var post = await client.PostAsync($"/unsubscribe/{token}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractToken(page)
        }));
        post.StatusCode.Should().Be(HttpStatusCode.OK);
        (await post.Content.ReadAsStringAsync()).Should().ContainAny("You're unsubscribed", "You&#x27;re unsubscribed");
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        (await verifyDb.Customers.SingleAsync(c => c.Email!.Value == email)).MarketingSubscribed.Should().BeFalse();
    }

    [Fact]
    public async Task Compose_AsOwner_UsesVisualBlocksAndStoresSafeCompiledContent()
    {
        var client = await LoginAsOwnerAsync();
        var composePage = await client.GetAsync("/admin/marketing/compose");
        composePage.StatusCode.Should().Be(HttpStatusCode.OK);
        var form = await composePage.Content.ReadAsStringAsync();
        form.Should().Contain("Live email preview").And.Contain("Product announcement");
        form.Should().NotContain("Email content (HTML)").And.NotContain("name=\"HtmlContent\"");
        var token = ExtractToken(form);
        var fields = EditorFields("Autumn promo test", "Autumn sale", "{\"blocks\":[{\"type\":\"heading\",\"text\":\"Autumn sale!\"},{\"type\":\"paragraph\",\"text\":\"Big discounts <script>alert('x')</script>\"}]}");
        fields["__RequestVerificationToken"] = token;
        var response = await client.PostAsync("/admin/marketing/compose", new FormUrlEncodedContent(fields));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var id = Guid.Parse(response.Headers.Location!.ToString().Split('/').Last());

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var campaign = await db.MarketingBroadcasts.AsNoTracking().SingleAsync(b => b.Id == id);
        campaign.Name.Should().Be("Autumn promo test");
        campaign.Subject.Should().Be("Autumn sale");
        campaign.Status.Should().Be(MarketingBroadcastStatus.Draft);
        campaign.AudienceSegment.Should().Be(MarketingAudienceSegment.AllSubscribed);
        campaign.ContentJson.Should().Contain("Autumn sale!");
        campaign.HtmlContent.Should().Contain("<h2").And.Contain("&lt;script&gt;alert(").And.NotContain("<script");
    }

    [Fact]
    public async Task Compose_MissingSubject_RedrawsWithValidationError()
    {
        var client = await LoginAsOwnerAsync();
        var token = await HarvestAntiForgeryTokenAsync(client, "/admin/marketing/compose");
        var fields = EditorFields("Missing subject test", "", "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Content\"}]}");
        fields["__RequestVerificationToken"] = token;
        var response = await client.PostAsync("/admin/marketing/compose", new FormUrlEncodedContent(fields));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("A subject is required.");
    }

    [Fact]
    public async Task Compose_RejectsUnsafeImageLinkAndUnknownPersonalization()
    {
        var client = await LoginAsOwnerAsync();
        var token = await HarvestAntiForgeryTokenAsync(client, "/admin/marketing/compose");
        var fields = EditorFields("Unsafe campaign", "Safe subject", "{\"blocks\":[{\"type\":\"button\",\"text\":\"Open\",\"url\":\"javascript:alert(1)\"}]}");
        fields["__RequestVerificationToken"] = token;
        var response = await client.PostAsync("/admin/marketing/compose", new FormUrlEncodedContent(fields));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("secure HTTPS link");

        var token2 = await HarvestAntiForgeryTokenAsync(client, "/admin/marketing/compose");
        fields = EditorFields("Unknown token campaign", "Safe subject", "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Hello {{Email}}\"}]}");
        fields["__RequestVerificationToken"] = token2;
        var response2 = await client.PostAsync("/admin/marketing/compose", new FormUrlEncodedContent(fields));
        response2.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response2.Content.ReadAsStringAsync()).Should().Contain("First name personalization token");
    }

    [Fact]
    public async Task AudienceCounts_UsePaidOrderHistoryAndExcludeMarketingOptOuts()
    {
        var subscribed = $"audience{Guid.NewGuid():N}@test.local";
        var optedOut = $"optedout{Guid.NewGuid():N}@test.local";
        await TestSeed.RegisterCustomerAsync(_factory.Services, subscribed);
        await TestSeed.RegisterCustomerAsync(_factory.Services, optedOut);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var customer = await db.Customers.SingleAsync(c => c.Email!.Value == optedOut);
            customer.UnsubscribeFromMarketing();
            await db.SaveChangesAsync();
        }
        var client = await LoginAsOwnerAsync();
        async Task<int> Count(int segment)
        {
            var response = await client.GetAsync($"/admin/marketing/audience-count?segment={segment}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("count").GetInt32();
        }
        var all = await Count(0);
        var pastPurchasers = await Count(1);
        var noRecentPurchase = await Count(2);
        pastPurchasers.Should().Be(0, "this fixture has no paid orders");
        noRecentPurchase.Should().Be(all, "customers without paid-order history are in this available segment");
        all.Should().BeGreaterThanOrEqualTo(1);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var optedOutRow = await verifyDb.Customers.SingleAsync(c => c.Email!.Value == optedOut);
        optedOutRow.MarketingSubscribed.Should().BeFalse();
    }

    [Fact]
    public async Task Send_AsOwner_ConfirmsAudienceOnceAndRejectsARepeat()
    {
        var email1 = $"mark1{Guid.NewGuid():N}@test.local";
        var email2 = $"mark2{Guid.NewGuid():N}@test.local";
        await TestSeed.RegisterCustomerAsync(_factory.Services, email1);
        await TestSeed.RegisterCustomerAsync(_factory.Services, email2);
        var client = await LoginAsOwnerAsync();
        var id = await CreateCampaignAsync(client, "Duplicate-guard test", "Duplicate-guard test",
            "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Only one of me should ever arrive.\"}]}");

        var confirmResponse = await client.GetAsync($"/admin/marketing/{id}/confirm");
        confirmResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var confirmHtml = await confirmResponse.Content.ReadAsStringAsync();
        var confirmToken = ExtractToken(confirmHtml);
        var expectedCount = int.Parse(Regex.Match(confirmHtml,
            "name=\\\"ExpectedRecipientCount\\\"[^>]*value=\\\"(\\d+)\\\"").Groups[1].Value);
        expectedCount.Should().BeGreaterThanOrEqualTo(2);
        Dictionary<string, string> Confirmation() => new()
        {
            ["__RequestVerificationToken"] = confirmToken,
            ["BroadcastId"] = id.ToString(),
            ["ExpectedRecipientCount"] = expectedCount.ToString(),
            ["SendMode"] = "now",
            ["Confirm"] = "true"
        };
        var first = await client.PostAsync($"/admin/marketing/{id}/confirm", new FormUrlEncodedContent(Confirmation()));
        first.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var (status, recipientCount, queuedAt) = await ReadBroadcastAsync(id);
        status.Should().BeOneOf(MarketingBroadcastStatus.Queued, MarketingBroadcastStatus.Sending, MarketingBroadcastStatus.Completed);
        queuedAt.Should().NotBeNull();
        recipientCount.Should().Be(expectedCount);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var recipients = await db.MarketingBroadcastRecipients.Where(r => r.BroadcastId == id).Select(r => r.Email).ToListAsync();
            recipients.Should().Contain(new[] { email1, email2 });
        }

        var second = await client.PostAsync($"/admin/marketing/{id}/confirm", new FormUrlEncodedContent(Confirmation()));
        second.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await (await client.GetAsync(second.Headers.Location!)).Content.ReadAsStringAsync()).Should().Contain("no longer a draft");
        var (_, recipientCountAfter, _) = await ReadBroadcastAsync(id);
        recipientCountAfter.Should().Be(expectedCount);
    }

    [Fact]
    public async Task TestSend_FailureDoesNotAddAddressToCampaignAudience()
    {
        var client = await LoginAsOwnerAsync();
        var id = await CreateCampaignAsync(client, "Test-only campaign", "Preview subject",
            "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"A test message.\"}]}");
        var details = await client.GetAsync($"/admin/marketing/{id}");
        var token = ExtractToken(await details.Content.ReadAsStringAsync());
        var response = await client.PostAsync($"/admin/marketing/{id}/test", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["TestEmail"] = "tester@example.com"
        }));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        (await db.MarketingBroadcastRecipients.CountAsync(r => r.BroadcastId == id)).Should().Be(0,
            "a test address is sent alone and never copied into the audience ledger");
    }

    [Fact]
    public async Task Schedule_CanBeRescheduledAndCancelled_BeforeAnySend()
    {
        await TestSeed.RegisterCustomerAsync(_factory.Services, $"schedule{Guid.NewGuid():N}@test.local");
        var client = await LoginAsOwnerAsync();
        var id = await CreateCampaignAsync(client, "Scheduled campaign", "Scheduled subject",
            "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"A scheduled message.\"}]}");
        var confirmResponse = await client.GetAsync($"/admin/marketing/{id}/confirm");
        var confirmHtml = await confirmResponse.Content.ReadAsStringAsync();
        var token = ExtractToken(confirmHtml);
        var expected = Regex.Match(confirmHtml, "name=\\\"ExpectedRecipientCount\\\"[^>]*value=\\\"(\\d+)\\\"").Groups[1].Value;
        var scheduledAt = DateTimeOffset.UtcNow.AddHours(2).ToString("O");
        var scheduled = await client.PostAsync($"/admin/marketing/{id}/confirm", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["BroadcastId"] = id.ToString(),
            ["ExpectedRecipientCount"] = expected,
            ["SendMode"] = "schedule",
            ["ScheduledAtUtc"] = scheduledAt,
            ["Confirm"] = "true"
        }));
        scheduled.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var saved = await ReadScheduleAsync(id);
        saved.Status.Should().Be(MarketingBroadcastStatus.Scheduled);
        saved.ScheduledAtUtc.Should().NotBeNull();

        var details = await client.GetAsync($"/admin/marketing/{id}");
        var detailsHtml = await details.Content.ReadAsStringAsync();
        var rescheduleToken = ExtractToken(detailsHtml);
        var newSchedule = DateTimeOffset.UtcNow.AddHours(3).ToString("O");
        var reschedule = await client.PostAsync($"/admin/marketing/{id}/reschedule", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = rescheduleToken,
            ["ScheduledAtUtc"] = newSchedule
        }));
        reschedule.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await ReadScheduleAsync(id)).ScheduledAtUtc!.Value.Should().BeCloseTo(DateTime.Parse(newSchedule).ToUniversalTime(), TimeSpan.FromSeconds(1));

        var cancelToken = ExtractToken(await (await client.GetAsync($"/admin/marketing/{id}")).Content.ReadAsStringAsync());
        var cancelled = await client.PostAsync($"/admin/marketing/{id}/cancel", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = cancelToken
        }));
        cancelled.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var final = await ReadScheduleAsync(id);
        final.Status.Should().Be(MarketingBroadcastStatus.Cancelled);
        final.ScheduledAtUtc.Should().BeNull();
        final.RecipientCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task MarketingPage_WithoutMarketingPermission_ForbiddenForCustomer()
    {
        var email = $"noperm{Guid.NewGuid():N}@test.local";
        await TestSeed.RegisterCustomerAsync(_factory.Services, email);
        await TestSeed.VerifyCustomerAsync(_factory.Services, email);
        var client = CreateBrowser();
        var loginPage = await client.GetAsync("/Account/Login");
        var token = ExtractToken(await loginPage.Content.ReadAsStringAsync());
        await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = TestSeed.DefaultPassword,
            ["__RequestVerificationToken"] = token
        }));
        var response = await client.GetAsync("/admin/marketing");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "the Customer role must not carry marketing permissions");
        response.Headers.Location!.ToString().Should().Contain("/Account/AccessDenied");
    }

    private async Task<(MarketingBroadcastStatus Status, int RecipientCount, DateTime? QueuedAtUtc)> ReadBroadcastAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var broadcast = await db.MarketingBroadcasts.AsNoTracking().Include(b => b.Recipients).SingleAsync(b => b.Id == id);
        return (broadcast.Status, broadcast.Recipients.Count, broadcast.QueuedAtUtc);
    }

    private async Task<(MarketingBroadcastStatus Status, DateTime? ScheduledAtUtc, int RecipientCount)> ReadScheduleAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var broadcast = await db.MarketingBroadcasts.AsNoTracking().Include(b => b.Recipients).SingleAsync(b => b.Id == id);
        return (broadcast.Status, broadcast.ScheduledAtUtc, broadcast.Recipients.Count);
    }

    [Fact]
    public async Task BroadcastPreview_UsesSameRendererAndEscapesBlockText()
    {
        var client = await LoginAsOwnerAsync();
        var id = await CreateCampaignAsync(client, "Encoding preview test", "Mid-year update",
            "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"It's \\\"Mid-Year\\\" & Mega Sale <script>alert('x')</script>\"}]}");
        var details = await client.GetAsync($"/admin/marketing/{id}");
        details.StatusCode.Should().Be(HttpStatusCode.OK);
        var detailsHtml = await details.Content.ReadAsStringAsync();
        detailsHtml.Should().Contain("srcdoc=\"").And.Contain("title=\"Rendered campaign email\"")
            .And.Contain("sandbox").And.Contain("Mid-Year").And.Contain("&amp;lt;script");

        var preview = await client.GetAsync($"/admin/marketing/{id}/preview");
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await preview.Content.ReadAsStringAsync();
        html.Should().Contain("Mid-Year").And.Contain("&lt;script&gt;alert(").And.Contain("Unsubscribe");
        html.Should().NotContain("<script>alert").And.NotContain("&amp;lt;script");
    }
}
