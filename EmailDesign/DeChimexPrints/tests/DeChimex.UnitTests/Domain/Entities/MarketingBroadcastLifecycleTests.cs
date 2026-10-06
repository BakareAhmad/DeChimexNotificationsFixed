using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;
using Xunit;

namespace DeChimex.UnitTests.Domain.Entities;

public class MarketingBroadcastLifecycleTests
{
    private static MarketingBroadcast Draft() => MarketingBroadcast.Create(
        "Spring update", "A spring update", "A short preview", "DE-CHIMEX PRINT HUB", null,
        MarketingAudienceSegment.AllSubscribed, "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Hello\"}]}",
        "<p>Hello</p>", "Hello", "owner@example.com");

    private static IReadOnlyList<(Guid CustomerId, string Email, string FirstName)> Recipients()
        => new[] { (Guid.NewGuid(), "customer@example.com", "Alex") };

    [Fact]
    public void Schedule_QueuesOnlyWhenDue_AndPreservesTheRecipientSnapshot()
    {
        var campaign = Draft();
        var now = new DateTime(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc);
        var sendAt = now.AddMinutes(15);
        var recipients = Recipients();
        campaign.Schedule(sendAt, recipients, now);

        Assert.Equal(MarketingBroadcastStatus.Scheduled, campaign.Status);
        Assert.Equal(sendAt, campaign.ScheduledAtUtc);
        Assert.Equal(1, campaign.RecipientCount);
        Assert.False(campaign.QueueIfDue(sendAt.AddTicks(-1)));
        Assert.True(campaign.QueueIfDue(sendAt));
        Assert.Equal(MarketingBroadcastStatus.Queued, campaign.Status);
        Assert.Equal(sendAt, campaign.QueuedAtUtc);
        Assert.Equal(1, campaign.RecipientCount);
    }

    [Fact]
    public void RescheduleAndCancel_AreLimitedToScheduledCampaigns()
    {
        var campaign = Draft();
        var now = DateTime.UtcNow;
        campaign.Schedule(now.AddHours(1), Recipients(), now);
        var revised = now.AddHours(2);
        campaign.Reschedule(revised, now);
        Assert.Equal(revised, campaign.ScheduledAtUtc);

        campaign.CancelScheduled();
        Assert.Equal(MarketingBroadcastStatus.Cancelled, campaign.Status);
        Assert.Null(campaign.ScheduledAtUtc);
        Assert.Throws<BusinessRuleValidationException>(() => campaign.CancelScheduled());
        Assert.Throws<BusinessRuleValidationException>(() => campaign.Reschedule(revised.AddHours(1), now));
    }

    [Fact]
    public void QueueAndEdit_GuardsPreventDuplicateSendsAndPostConfirmationChanges()
    {
        var campaign = Draft();
        campaign.Queue(Recipients(), DateTime.UtcNow);
        Assert.Equal(MarketingBroadcastStatus.Queued, campaign.Status);
        Assert.Throws<BusinessRuleValidationException>(() => campaign.Queue(Recipients(), DateTime.UtcNow));
        Assert.Throws<BusinessRuleValidationException>(() => campaign.UpdateDraft(
            "Changed", "Changed subject", null, "Sender", null, MarketingAudienceSegment.AllSubscribed,
            "{}", "<p>Changed</p>", "Changed"));
    }

    [Fact]
    public void Schedule_RejectsPastTimesAndEmptyAudiences()
    {
        var campaign = Draft();
        var now = DateTime.UtcNow;
        Assert.Throws<BusinessRuleValidationException>(() => campaign.Schedule(now, Recipients(), now));
        Assert.Throws<BusinessRuleValidationException>(() => Draft().Schedule(now.AddHours(1), Array.Empty<(Guid, string, string)>(), now));
    }
}
