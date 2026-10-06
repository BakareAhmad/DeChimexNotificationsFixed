using System.Net;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>Mark-all integration coverage runs with its own in-memory factory so the explicit
/// bulk mutation cannot race notification-count tests that share other fixtures.</summary>
public sealed class NotificationMarkAllTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public NotificationMarkAllTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    [Fact]
    public async Task MarkAllRead_MarksOnlyRowsVisibleToCurrentStaffAndKeepsHistory()
    {
        var run = "RUN-" + Guid.NewGuid().ToString("N")[..8];
        var email = $"invbulk-{run.ToLowerInvariant()}@example.com";
        await TestSeed.CreateStaffUserAsync(_factory.Services, email, "Staff123!", "InventoryStaff");
        var staff = CreateBrowser();
        (await TestSeed.LoginMvcAsync(staff, email, "Staff123!")).Should().BeTrue();

        Guid stockId;
        Guid legacyId;
        Guid hiddenId;
        Guid otherRecipientId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var stock = Notification.Create(NotificationType.LowStock, $"{run}-Stock", "Visible", null,
                "/admin/inventory", "inventory.view");
            var legacy = Notification.Create(NotificationType.System, $"{run}-Legacy", "Visible legacy row");
            var hidden = Notification.Create(NotificationType.NewOrder, $"{run}-Hidden", "Needs another permission",
                null, "/admin/orders", "orders.view");
            var otherRecipient = Notification.Create(NotificationType.UserActivity, $"{run}-OtherRecipient", "Direct to another user",
                Guid.NewGuid(), "/portal");
            db.Notifications.AddRange(stock, legacy, hidden, otherRecipient);
            await db.SaveChangesAsync();
            stockId = stock.Id;
            legacyId = legacy.Id;
            hiddenId = hidden.Id;
            otherRecipientId = otherRecipient.Id;
        }

        var page = await staff.GetAsync("/admin/notifications?unreadOnly=true");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = TestSeed.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
        token.Should().NotBeEmpty();
        var invalid = await staff.PostAsync("/admin/notifications/mark-all-read", new FormUrlEncodedContent(
            new Dictionary<string, string>()));
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var response = await staff.PostAsync("/admin/notifications/mark-all-read", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location?.ToString().Should().Contain("/admin/notifications");
        response.Headers.Location?.ToString().Should().NotContain("unreadOnly=true");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Notifications.AsNoTracking().FirstAsync(n => n.Id == stockId)).IsRead.Should().BeTrue();
            (await db.Notifications.AsNoTracking().FirstAsync(n => n.Id == legacyId)).IsRead.Should().BeTrue();
            (await db.Notifications.AsNoTracking().FirstAsync(n => n.Id == hiddenId)).IsRead.Should().BeFalse();
            (await db.Notifications.AsNoTracking().FirstAsync(n => n.Id == otherRecipientId)).IsRead.Should().BeFalse();
        }

        var history = await staff.GetAsync("/admin/notifications");
        var html = await history.Content.ReadAsStringAsync();
        html.Should().Contain($"{run}-Stock").And.Contain($"{run}-Legacy");
        html.Should().NotContain($"{run}-Hidden").And.NotContain($"{run}-OtherRecipient");
    }
}
