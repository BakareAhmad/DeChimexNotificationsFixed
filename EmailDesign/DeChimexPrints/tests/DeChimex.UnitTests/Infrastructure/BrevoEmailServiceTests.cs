using System.Net;
using System.Text;
using System.Text.Json;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Infrastructure.Email;
using DeChimex.Infrastructure.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace DeChimex.UnitTests.Infrastructure;

public class BrevoEmailServiceTests
{
    [Fact]
    public async Task TrySendAsync_UsesCampaignSenderAndReplyToOverrides()
    {
        var handler = new CaptureHandler();
        var service = new BrevoEmailService(
            new HttpClient(handler),
            Options.Create(new BrevoOptions
            {
                Enabled = true,
                ApiKey = "test-only-key",
                ApiBaseUrl = "https://email.test.invalid/v3",
                SenderName = "Global sender",
                SenderEmail = "no-reply@example.com"
            }),
            NullLogger<BrevoEmailService>.Instance,
            Substitute.For<IIntegrationCallRecorder>());

        var accepted = await service.TrySendAsync(new EmailMessage(
            "test@example.com", "Test recipient", "Subject", "<p>Body</p>", "Body",
            Template: "MarketingTest", Category: "marketing-test",
            SenderName: "Campaign sender", ReplyToEmail: "hello@example.com"));

        Assert.True(accepted);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Campaign sender", json.RootElement.GetProperty("sender").GetProperty("name").GetString());
        Assert.Equal("no-reply@example.com", json.RootElement.GetProperty("sender").GetProperty("email").GetString());
        Assert.Equal("hello@example.com", json.RootElement.GetProperty("replyTo").GetProperty("email").GetString());
        Assert.Equal("test-only-key", handler.ApiKey);
    }

    [Fact]
    public async Task TrySendAsync_OmitsReplyToWhenCampaignDoesNotSetIt()
    {
        var handler = new CaptureHandler();
        var service = new BrevoEmailService(
            new HttpClient(handler),
            Options.Create(new BrevoOptions { Enabled = true, ApiKey = "test-only-key", SenderEmail = "no-reply@example.com" }),
            NullLogger<BrevoEmailService>.Instance,
            Substitute.For<IIntegrationCallRecorder>());

        await service.TrySendAsync(new EmailMessage("test@example.com", "Test", "Subject", "<p>Body</p>", SenderName: "Campaign sender"));
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Campaign sender", json.RootElement.GetProperty("sender").GetProperty("name").GetString());
        Assert.False(json.RootElement.TryGetProperty("replyTo", out _));
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? ApiKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ApiKey = request.Headers.GetValues("api-key").Single();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"messageId\":\"unit-test\"}", Encoding.UTF8, "application/json")
            };
        }
    }
}
