using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>
/// An admin-composed marketing campaign. The editable block document is stored beside
/// its compiled email-safe content so previews and delivery use the same saved markup.
/// Transactional email remains on the separate email-template path.
/// </summary>
public sealed class MarketingBroadcast : AggregateRoot
{
    private readonly List<MarketingBroadcastRecipient> _recipients = new();

    private MarketingBroadcast() { } // EF

    private MarketingBroadcast(
        string name,
        string subject,
        string preheader,
        string senderName,
        string? replyToEmail,
        MarketingAudienceSegment audienceSegment,
        string? contentJson,
        string htmlContent,
        string plainTextContent,
        string createdBy)
    {
        Name = name;
        Subject = subject;
        Preheader = preheader;
        SenderName = senderName;
        ReplyToEmail = replyToEmail;
        AudienceSegment = audienceSegment;
        ContentJson = contentJson;
        HtmlContent = htmlContent;
        PlainTextContent = plainTextContent;
        Status = MarketingBroadcastStatus.Draft;
        CreatedBy = createdBy;
    }

    public string Name { get; private set; } = "Legacy campaign";
    public string Subject { get; private set; } = default!;
    public string Preheader { get; private set; } = string.Empty;
    public string SenderName { get; private set; } = "DE-CHIMEX PRINT HUB";
    public string? ReplyToEmail { get; private set; }
    public MarketingAudienceSegment AudienceSegment { get; private set; } = MarketingAudienceSegment.AllSubscribed;

    /// <summary>Internal structured block document. Null only for pre-builder legacy campaigns.</summary>
    public string? ContentJson { get; private set; }
    public string HtmlContent { get; private set; } = default!;
    public string PlainTextContent { get; private set; } = string.Empty;

    public MarketingBroadcastStatus Status { get; private set; } = MarketingBroadcastStatus.Draft;
    public DateTime? ScheduledAtUtc { get; private set; }
    public DateTime? QueuedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    public int RecipientCount => _recipients.Count;
    public int SentCount => _recipients.Count(r => r.Status == MarketingRecipientStatus.Sent);
    public int FailedCount => _recipients.Count(r => r.Status == MarketingRecipientStatus.Failed);
    public int PendingCount => _recipients.Count(r => r.Status == MarketingRecipientStatus.Pending);

    public IReadOnlyCollection<MarketingBroadcastRecipient> Recipients => _recipients.AsReadOnly();

    /// <summary>Compatibility factory for legacy callers that already supply safe HTML.</summary>
    public static MarketingBroadcast Create(string subject, string htmlContent, string plainTextContent, string createdBy)
        => Create(subject, subject, string.Empty, "DE-CHIMEX PRINT HUB", null,
            MarketingAudienceSegment.AllSubscribed, null, htmlContent, plainTextContent, createdBy);

    public static MarketingBroadcast Create(
        string name,
        string subject,
        string? preheader,
        string senderName,
        string? replyToEmail,
        MarketingAudienceSegment audienceSegment,
        string? contentJson,
        string htmlContent,
        string? plainTextContent,
        string createdBy)
    {
        ValidateCampaign(name, subject, preheader, senderName, replyToEmail, audienceSegment, contentJson, htmlContent);
        return new MarketingBroadcast(
            name.Trim(), subject.Trim(), preheader?.Trim() ?? string.Empty, senderName.Trim(),
            NormalizeOptional(replyToEmail), audienceSegment, contentJson, htmlContent,
            plainTextContent ?? string.Empty, NormalizeCreator(createdBy));
    }

    /// <summary>Only an unsent draft may change. Queued or scheduled content is immutable.</summary>
    public void UpdateDraft(
        string name,
        string subject,
        string? preheader,
        string senderName,
        string? replyToEmail,
        MarketingAudienceSegment audienceSegment,
        string contentJson,
        string htmlContent,
        string? plainTextContent)
    {
        if (Status != MarketingBroadcastStatus.Draft)
            throw new BusinessRuleValidationException("marketing.notdraft", "Only a draft campaign can be edited.");

        ValidateCampaign(name, subject, preheader, senderName, replyToEmail, audienceSegment, contentJson, htmlContent);
        Name = name.Trim();
        Subject = subject.Trim();
        Preheader = preheader?.Trim() ?? string.Empty;
        SenderName = senderName.Trim();
        ReplyToEmail = NormalizeOptional(replyToEmail);
        AudienceSegment = audienceSegment;
        ContentJson = contentJson;
        HtmlContent = htmlContent;
        PlainTextContent = plainTextContent ?? string.Empty;
    }

    /// <summary>
    /// Queues an immediate send. Recipients are snapshotted once; the worker still checks
    /// each customer's current consent before it calls the email service.
    /// </summary>
    public void Queue(IReadOnlyList<(Guid CustomerId, string Email, string FirstName)> recipients, DateTime nowUtc)
    {
        EnsureDraftAndRecipients(recipients);
        Status = MarketingBroadcastStatus.Queued;
        ScheduledAtUtc = null;
        QueuedAtUtc = nowUtc;
        AddRecipients(recipients);
    }

    /// <summary>Schedules a future send and snapshots the selected audience at confirmation.</summary>
    public void Schedule(
        DateTime scheduledAtUtc,
        IReadOnlyList<(Guid CustomerId, string Email, string FirstName)> recipients,
        DateTime nowUtc)
    {
        EnsureDraftAndRecipients(recipients);
        if (scheduledAtUtc <= nowUtc)
            throw new BusinessRuleValidationException("marketing.schedulepast", "Choose a send time in the future.");

        Status = MarketingBroadcastStatus.Scheduled;
        ScheduledAtUtc = scheduledAtUtc;
        QueuedAtUtc = null;
        AddRecipients(recipients);
    }

    public void Reschedule(DateTime scheduledAtUtc, DateTime nowUtc)
    {
        if (Status != MarketingBroadcastStatus.Scheduled)
            throw new BusinessRuleValidationException("marketing.notreschedulable", "Only a scheduled campaign can be rescheduled.");
        if (scheduledAtUtc <= nowUtc)
            throw new BusinessRuleValidationException("marketing.schedulepast", "Choose a send time in the future.");
        ScheduledAtUtc = scheduledAtUtc;
    }

    public void CancelScheduled()
    {
        if (Status != MarketingBroadcastStatus.Scheduled)
            throw new BusinessRuleValidationException("marketing.notcancellable", "Only a scheduled campaign can be cancelled.");
        Status = MarketingBroadcastStatus.Cancelled;
        ScheduledAtUtc = null;
    }

    /// <summary>The existing polling worker promotes a due schedule into its queued lifecycle.</summary>
    public bool QueueIfDue(DateTime nowUtc)
    {
        if (Status != MarketingBroadcastStatus.Scheduled || ScheduledAtUtc is null || ScheduledAtUtc > nowUtc)
            return false;
        Status = MarketingBroadcastStatus.Queued;
        QueuedAtUtc = nowUtc;
        return true;
    }

    /// <summary>Called by the background worker when it starts processing.</summary>
    public void MarkSending()
    {
        if (Status == MarketingBroadcastStatus.Queued)
            Status = MarketingBroadcastStatus.Sending;
    }

    /// <summary>Completes the broadcast after every snapshotted recipient has a final outcome.</summary>
    public void MarkCompleted()
    {
        if (Status is MarketingBroadcastStatus.Queued or MarketingBroadcastStatus.Sending)
        {
            Status = MarketingBroadcastStatus.Completed;
            CompletedAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Requeues only failed recipients. Accepted sends are never sent again.</summary>
    public void RetryFailed()
    {
        if (Status != MarketingBroadcastStatus.Completed)
            throw new BusinessRuleValidationException("marketing.notretryable", "Only a completed campaign can retry failed recipients.");
        if (FailedCount == 0)
            throw new BusinessRuleValidationException("marketing.nofailures", "There are no failed recipients to retry.");

        Status = MarketingBroadcastStatus.Queued;
        foreach (var recipient in _recipients.Where(r => r.Status == MarketingRecipientStatus.Failed))
            recipient.Requeue();
    }

    private void EnsureDraftAndRecipients(IReadOnlyList<(Guid CustomerId, string Email, string FirstName)> recipients)
    {
        if (Status != MarketingBroadcastStatus.Draft)
            throw new BusinessRuleValidationException("marketing.alreadyqueued", "This campaign is no longer a draft.");
        if (recipients.Count == 0)
            throw new BusinessRuleValidationException("marketing.norecipients", "There are no eligible customers in this audience.");
    }

    private void AddRecipients(IReadOnlyList<(Guid CustomerId, string Email, string FirstName)> recipients)
    {
        foreach (var (customerId, email, firstName) in recipients)
            _recipients.Add(new MarketingBroadcastRecipient(Id, customerId, email, firstName));
    }

    private static void ValidateCampaign(
        string name,
        string subject,
        string? preheader,
        string senderName,
        string? replyToEmail,
        MarketingAudienceSegment audienceSegment,
        string? contentJson,
        string htmlContent)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120)
            throw new BusinessRuleValidationException("marketing.name", "Enter a campaign name of 120 characters or fewer.");
        if (string.IsNullOrWhiteSpace(subject) || subject.Trim().Length > 200)
            throw new BusinessRuleValidationException("marketing.subject", "Enter a subject of 200 characters or fewer.");
        if (preheader?.Length > 200)
            throw new BusinessRuleValidationException("marketing.preheader", "The preview line must be 200 characters or fewer.");
        if (string.IsNullOrWhiteSpace(senderName) || senderName.Trim().Length > 80)
            throw new BusinessRuleValidationException("marketing.sender", "Enter a sender name of 80 characters or fewer.");
        if (replyToEmail?.Length > 256)
            throw new BusinessRuleValidationException("marketing.replyto", "The reply-to address is too long.");
        if (!Enum.IsDefined(audienceSegment))
            throw new BusinessRuleValidationException("marketing.audience", "Choose a supported customer audience.");
        if (contentJson?.Length > 20_000)
            throw new BusinessRuleValidationException("marketing.content", "The campaign content is too large.");
        if (string.IsNullOrWhiteSpace(htmlContent))
            throw new BusinessRuleValidationException("marketing.content", "Add at least one content block before saving.");
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeCreator(string? value)
        => string.IsNullOrWhiteSpace(value) ? "admin" : value.Trim();
}
