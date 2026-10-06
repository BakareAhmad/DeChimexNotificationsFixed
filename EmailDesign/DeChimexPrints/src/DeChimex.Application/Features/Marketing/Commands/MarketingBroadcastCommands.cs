using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using DeChimex.Shared.Constants;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Marketing.Commands;

// ---- Create draft ----
public sealed record CreateMarketingBroadcastCommand(
    string Subject,
    string HtmlContent,
    string? PlainTextContent,
    string CreatedBy,
    string? CampaignName = null,
    string? Preheader = null,
    string? SenderName = null,
    string? ReplyToEmail = null,
    MarketingAudienceSegment AudienceSegment = MarketingAudienceSegment.AllSubscribed,
    string? ContentJson = null) : ICommand<Guid>;

public sealed class CreateMarketingBroadcastValidator : AbstractValidator<CreateMarketingBroadcastCommand>
{
    public CreateMarketingBroadcastValidator()
    {
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(200);
        RuleFor(x => x.HtmlContent).NotEmpty().When(x => string.IsNullOrWhiteSpace(x.ContentJson));
        RuleFor(x => x.CampaignName).MaximumLength(120);
        RuleFor(x => x.Preheader).MaximumLength(200);
        RuleFor(x => x.SenderName).MaximumLength(80);
        RuleFor(x => x.AudienceSegment).IsInEnum();
    }
}

public sealed class CreateMarketingBroadcastHandler(IMarketingRepository broadcasts, IUnitOfWork uow)
    : ICommandHandler<CreateMarketingBroadcastCommand, Guid>
{
    public async Task<Guid> Handle(CreateMarketingBroadcastCommand command, CancellationToken ct)
    {
        var (html, plain) = ResolveContent(command.ContentJson, command.HtmlContent, command.PlainTextContent);
        var name = string.IsNullOrWhiteSpace(command.CampaignName)
            ? command.Subject.Trim()[..Math.Min(command.Subject.Trim().Length, 120)]
            : command.CampaignName.Trim();
        var senderName = string.IsNullOrWhiteSpace(command.SenderName) ? "DE-CHIMEX PRINT HUB" : command.SenderName.Trim();
        ValidateReplyTo(command.ReplyToEmail);

        var broadcast = MarketingBroadcast.Create(name, command.Subject, command.Preheader, senderName,
            command.ReplyToEmail, command.AudienceSegment, command.ContentJson, html, plain, command.CreatedBy);
        await broadcasts.AddAsync(broadcast, ct);
        await uow.SaveChangesAsync(ct);
        return broadcast.Id;
    }

    internal static (string Html, string PlainText) ResolveContent(string? contentJson, string? htmlContent, string? plainText)
    {
        if (!string.IsNullOrWhiteSpace(contentJson))
        {
            var compiled = MarketingContentComposer.Build(contentJson);
            if (!compiled.Success)
                throw new ValidationAppException(new Dictionary<string, string[]> { ["BlocksJson"] = [compiled.Error ?? "Review the campaign blocks."] });
            return (compiled.Html, compiled.PlainText);
        }

        // Compatibility only for existing callers and legacy records. The admin editor
        // never accepts this value from a visible input.
        var sanitized = MarketingHtmlSanitizer.Sanitize(htmlContent ?? string.Empty);
        if (string.IsNullOrWhiteSpace(sanitized))
            throw new ValidationAppException(new Dictionary<string, string[]> { ["HtmlContent"] = ["The email content contains no readable text."] });
        return (sanitized, plainText ?? string.Empty);
    }

    internal static void ValidateReplyTo(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        try
        {
            var parsed = new MailAddress(address.Trim());
            if (!string.Equals(parsed.Address, address.Trim(), StringComparison.OrdinalIgnoreCase)) throw new FormatException();
        }
        catch
        {
            throw new ValidationAppException(new Dictionary<string, string[]> { ["ReplyToEmail"] = ["Enter a valid reply-to email address."] });
        }
    }
}

// ---- Edit draft ----
public sealed record UpdateMarketingBroadcastCommand(
    Guid BroadcastId,
    string Name,
    string Subject,
    string? Preheader,
    string SenderName,
    string? ReplyToEmail,
    MarketingAudienceSegment AudienceSegment,
    string ContentJson) : ICommand<Unit>;

public sealed class UpdateMarketingBroadcastValidator : AbstractValidator<UpdateMarketingBroadcastCommand>
{
    public UpdateMarketingBroadcastValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Preheader).MaximumLength(200);
        RuleFor(x => x.SenderName).NotEmpty().MaximumLength(80);
        RuleFor(x => x.AudienceSegment).IsInEnum();
        RuleFor(x => x.ContentJson).NotEmpty().MaximumLength(20_000);
    }
}

public sealed class UpdateMarketingBroadcastHandler(IMarketingRepository broadcasts, IUnitOfWork uow)
    : ICommandHandler<UpdateMarketingBroadcastCommand, Unit>
{
    public async Task<Unit> Handle(UpdateMarketingBroadcastCommand command, CancellationToken ct)
    {
        CreateMarketingBroadcastHandler.ValidateReplyTo(command.ReplyToEmail);
        var compiled = MarketingContentComposer.Build(command.ContentJson);
        if (!compiled.Success)
            throw new ValidationAppException(new Dictionary<string, string[]> { ["BlocksJson"] = [compiled.Error ?? "Review the campaign blocks."] });

        var broadcast = await broadcasts.GetWithRecipientsAsync(command.BroadcastId, ct)
            ?? throw new DeChimex.Application.Common.Exceptions.NotFoundException("Marketing campaign", command.BroadcastId);
        broadcast.UpdateDraft(command.Name, command.Subject, command.Preheader, command.SenderName,
            command.ReplyToEmail, command.AudienceSegment, command.ContentJson, compiled.Html, compiled.PlainText);
        broadcasts.Update(broadcast);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ---- Immediate send ----
public sealed record SendMarketingBroadcastCommand(Guid BroadcastId, int? ExpectedRecipientCount = null) : ICommand<Unit>;

public sealed class SendMarketingBroadcastHandler(
    IMarketingRepository broadcasts,
    IUnitOfWork uow,
    IDateTimeProvider clock)
    : ICommandHandler<SendMarketingBroadcastCommand, Unit>
{
    public async Task<Unit> Handle(SendMarketingBroadcastCommand command, CancellationToken ct)
    {
        var broadcast = await broadcasts.GetWithRecipientsAsync(command.BroadcastId, ct)
            ?? throw new DeChimex.Application.Common.Exceptions.NotFoundException("Marketing campaign", command.BroadcastId);
        var recipients = await broadcasts.GetEligibleRecipientsAsync(broadcast.AudienceSegment, clock.UtcNow, ct);
        MarketingCommandGuards.EnsureAudienceUnchanged(command.ExpectedRecipientCount, recipients.Count);
        broadcast.Queue(recipients, clock.UtcNow);
        broadcasts.Update(broadcast);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ---- Schedule ----
public sealed record ScheduleMarketingBroadcastCommand(
    Guid BroadcastId,
    DateTime ScheduledAtUtc,
    int ExpectedRecipientCount) : ICommand<Unit>;

public sealed class ScheduleMarketingBroadcastHandler(
    IMarketingRepository broadcasts,
    IUnitOfWork uow,
    IDateTimeProvider clock)
    : ICommandHandler<ScheduleMarketingBroadcastCommand, Unit>
{
    public async Task<Unit> Handle(ScheduleMarketingBroadcastCommand command, CancellationToken ct)
    {
        var broadcast = await broadcasts.GetWithRecipientsAsync(command.BroadcastId, ct)
            ?? throw new DeChimex.Application.Common.Exceptions.NotFoundException("Marketing campaign", command.BroadcastId);
        var now = clock.UtcNow;
        var recipients = await broadcasts.GetEligibleRecipientsAsync(broadcast.AudienceSegment, now, ct);
        MarketingCommandGuards.EnsureAudienceUnchanged(command.ExpectedRecipientCount, recipients.Count);
        broadcast.Schedule(command.ScheduledAtUtc, recipients, now);
        broadcasts.Update(broadcast);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ---- Reschedule or cancel a scheduled campaign ----
public sealed record RescheduleMarketingBroadcastCommand(Guid BroadcastId, DateTime ScheduledAtUtc) : ICommand<Unit>;

public sealed class RescheduleMarketingBroadcastHandler(
    IMarketingRepository broadcasts,
    IUnitOfWork uow,
    IDateTimeProvider clock)
    : ICommandHandler<RescheduleMarketingBroadcastCommand, Unit>
{
    public async Task<Unit> Handle(RescheduleMarketingBroadcastCommand command, CancellationToken ct)
    {
        var broadcast = await broadcasts.GetByIdAsync(command.BroadcastId, ct)
            ?? throw new DeChimex.Application.Common.Exceptions.NotFoundException("Marketing campaign", command.BroadcastId);
        broadcast.Reschedule(command.ScheduledAtUtc, clock.UtcNow);
        broadcasts.Update(broadcast);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public sealed record CancelScheduledMarketingBroadcastCommand(Guid BroadcastId) : ICommand<Unit>;

public sealed class CancelScheduledMarketingBroadcastHandler(IMarketingRepository broadcasts, IUnitOfWork uow)
    : ICommandHandler<CancelScheduledMarketingBroadcastCommand, Unit>
{
    public async Task<Unit> Handle(CancelScheduledMarketingBroadcastCommand command, CancellationToken ct)
    {
        var broadcast = await broadcasts.GetByIdAsync(command.BroadcastId, ct)
            ?? throw new DeChimex.Application.Common.Exceptions.NotFoundException("Marketing campaign", command.BroadcastId);
        broadcast.CancelScheduled();
        broadcasts.Update(broadcast);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ---- Test send (one explicitly entered address, never added to the audience ledger) ----
public sealed record SendMarketingTestEmailCommand(Guid BroadcastId, string TestEmail) : ICommand<bool>;

public sealed class SendMarketingTestEmailHandler(
    IMarketingRepository broadcasts,
    IEmailTemplateRenderer renderer,
    IAppUrlProvider urls,
    IEmailService emailService)
    : ICommandHandler<SendMarketingTestEmailCommand, bool>
{
    public async Task<bool> Handle(SendMarketingTestEmailCommand command, CancellationToken ct)
    {
        var address = command.TestEmail?.Trim() ?? string.Empty;
        if (address.Length > 256 || !new EmailAddressAttribute().IsValid(address))
            throw new ValidationAppException(new Dictionary<string, string[]> { ["TestEmail"] = ["Enter a valid email address for the test send."] });

        var broadcast = await broadcasts.GetByIdAsync(command.BroadcastId, ct)
            ?? throw new DeChimex.Application.Common.Exceptions.NotFoundException("Marketing campaign", command.BroadcastId);
        if (broadcast.Status != MarketingBroadcastStatus.Draft)
            throw new DeChimex.Domain.Exceptions.BusinessRuleValidationException("marketing.testdraftonly", "Save or copy this campaign as a draft before sending a test.");

        var content = renderer.RenderMarketing(broadcast.Subject, broadcast.HtmlContent,
            new Dictionary<string, string>
            {
                ["FirstName"] = "Alex",
                ["UnsubscribeLink"] = urls.BuildMarketingUnsubscribeLink(Guid.Empty)
            }, broadcast.Preheader);

        try
        {
            return await emailService.TrySendAsync(new EmailMessage(
                address,
                "Campaign preview",
                $"[TEST] {content.Subject}",
                content.HtmlBody,
                content.PlainTextBody,
                Template: "MarketingTest",
                Category: "marketing-test",
                SenderName: broadcast.SenderName,
                ReplyToEmail: broadcast.ReplyToEmail), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}

// ---- Retry failed recipients ----
public sealed record RetryFailedRecipientsCommand(Guid BroadcastId) : ICommand<Unit>;

public sealed class RetryFailedRecipientsHandler(IMarketingRepository broadcasts, IUnitOfWork uow)
    : ICommandHandler<RetryFailedRecipientsCommand, Unit>
{
    public async Task<Unit> Handle(RetryFailedRecipientsCommand command, CancellationToken ct)
    {
        var broadcast = await broadcasts.GetWithRecipientsAsync(command.BroadcastId, ct)
            ?? throw new DeChimex.Application.Common.Exceptions.NotFoundException("Marketing campaign", command.BroadcastId);
        broadcast.RetryFailed();
        broadcasts.Update(broadcast);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

internal static class MarketingCommandGuards
{
    public static void EnsureAudienceUnchanged(int? expected, int actual)
    {
        if (actual == 0)
            throw new DeChimex.Domain.Exceptions.BusinessRuleValidationException("marketing.norecipients", "There are no eligible customers in this audience.");
        if (expected.HasValue && expected.Value != actual)
            throw new DeChimex.Domain.Exceptions.BusinessRuleValidationException(
                "marketing.audiencechanged", "The audience count changed. Review the updated count and confirm again.");
    }
}
