namespace DeChimex.Contracts.Marketing;

/// <summary>Admin campaign list and delivery summary.</summary>
public sealed record MarketingBroadcastDto(
    Guid Id,
    string Name,
    string Subject,
    string SenderName,
    string? ReplyToEmail,
    string Status,
    string AudienceSegment,
    int RecipientCount,
    int SentCount,
    int FailedCount,
    DateTime CreatedDateUtc,
    string? CreatedBy,
    DateTime? ScheduledAtUtc,
    DateTime? QueuedAtUtc,
    DateTime? CompletedAtUtc);

public sealed record MarketingBroadcastDetailsDto(
    MarketingBroadcastDto Broadcast,
    string HtmlContent,
    string? ContentJson,
    string Preheader,
    string SenderName,
    string? ReplyToEmail,
    IReadOnlyList<MarketingRecipientDto> Recipients);

public sealed record MarketingRecipientDto(
    string Email,
    string FirstName,
    string Status,
    DateTime? SentAtUtc,
    string? FailureReason);
