using System.Globalization;
using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Features.Marketing.Commands;
using DeChimex.Application.Features.Marketing.Queries;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;
using DeChimex.Host.Models.Admin;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>Visual marketing campaigns. Transactional messages remain on their separate path.</summary>
[Authorize(Policy = Permissions.Marketing.View)]
[Route("admin/marketing")]
public sealed class AdminMarketingController : MvcControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index(int page = 1)
    {
        ViewData["Title"] = "Marketing";
        ViewData["Heading"] = "Marketing campaigns";
        ViewData["ActiveNav"] = "marketing";

        var result = await Sender.Send(new GetMarketingBroadcastsQuery(
            new Application.Common.Models.PagedRequest { Page = page, PageSize = 20 }));
        ViewBag.Page = result.Page;
        ViewBag.TotalPages = result.TotalPages;
        ViewBag.HasPrevious = result.HasPrevious;
        ViewBag.HasNext = result.HasNext;
        ViewBag.EligibleRecipients = await Sender.Send(new GetMarketingRecipientCountQuery());
        return View(result.Items);
    }

    [HttpGet("audience-count")]
    public async Task<IActionResult> AudienceCount(MarketingAudienceSegment segment)
    {
        if (!Enum.IsDefined(segment)) return BadRequest(new { message = "Choose a supported audience." });
        var count = await Sender.Send(new GetMarketingRecipientCountQuery(segment));
        return Json(new { count });
    }

    [HttpGet("compose")]
    [Authorize(Policy = Permissions.Marketing.Manage)]
    public async Task<IActionResult> Compose()
    {
        var model = new MarketingBroadcastFormViewModel
        {
            Name = string.Empty,
            Subject = string.Empty,
            SenderName = "DE-CHIMEX PRINT HUB",
            AudienceSegment = MarketingAudienceSegment.AllSubscribed
        };
        await SetEditorPageAsync(model, "New campaign", "Create a campaign");
        return View("Compose", model);
    }

    [HttpPost("compose")]
    [Authorize(Policy = Permissions.Marketing.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Compose(MarketingBroadcastFormViewModel model)
    {
        await SetEditorPageAsync(model, "New campaign", "Create a campaign");
        if (!ModelState.IsValid) return View("Compose", model);

        try
        {
            var id = await Sender.Send(new CreateMarketingBroadcastCommand(
                model.Subject.Trim(), string.Empty, null, CurrentUser.Email ?? "admin",
                model.Name, model.Preheader, model.SenderName, model.ReplyToEmail,
                model.AudienceSegment, model.BlocksJson));
            TempData["Success"] = "Draft saved. Preview it and send a test before you confirm the audience.";
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (ValidationAppException ex)
        {
            AddValidationErrors(ex);
            await SetEditorPageAsync(model, "New campaign", "Create a campaign");
            return View("Compose", model);
        }
        catch (BusinessRuleValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View("Compose", model);
        }
    }

    [HttpGet("{id:guid}/edit")]
    [Authorize(Policy = Permissions.Marketing.Manage)]
    public async Task<IActionResult> Edit(Guid id)
    {
        var details = await Sender.Send(new GetMarketingBroadcastDetailsQuery(id));
        if (details is null) return NotFound();
        if (details.Broadcast.Status != nameof(MarketingBroadcastStatus.Draft))
        {
            TempData["Error"] = "Only draft campaigns can be edited.";
            return RedirectToAction(nameof(Details), new { id });
        }
        if (string.IsNullOrWhiteSpace(details.ContentJson))
        {
            TempData["Error"] = "This older campaign can still be previewed and sent, but it cannot be opened in the visual editor. Create a new visual draft to make changes.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var model = new MarketingBroadcastFormViewModel
        {
            Name = details.Broadcast.Name,
            Subject = details.Broadcast.Subject,
            Preheader = details.Preheader,
            SenderName = details.SenderName,
            ReplyToEmail = details.ReplyToEmail,
            AudienceSegment = Enum.TryParse<MarketingAudienceSegment>(details.Broadcast.AudienceSegment, out var segment)
                ? segment : MarketingAudienceSegment.AllSubscribed,
            BlocksJson = details.ContentJson
        };
        await SetEditorPageAsync(model, "Edit draft", "Edit campaign", id);
        return View("Compose", model);
    }

    [HttpPost("{id:guid}/edit")]
    [Authorize(Policy = Permissions.Marketing.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, MarketingBroadcastFormViewModel model)
    {
        await SetEditorPageAsync(model, "Edit draft", "Edit campaign", id);
        if (!ModelState.IsValid) return View("Compose", model);

        try
        {
            await Sender.Send(new UpdateMarketingBroadcastCommand(
                id, model.Name, model.Subject, model.Preheader, model.SenderName,
                model.ReplyToEmail, model.AudienceSegment, model.BlocksJson));
            TempData["Success"] = "Draft updated.";
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (ValidationAppException ex)
        {
            AddValidationErrors(ex);
            await SetEditorPageAsync(model, "Edit draft", "Edit campaign", id);
            return View("Compose", model);
        }
        catch (BusinessRuleValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View("Compose", model);
        }
    }

    [HttpPost("preview")]
    [Authorize(Policy = Permissions.Marketing.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PreviewDraft(
        [FromForm] string subject,
        [FromForm] string? preheader,
        [FromForm] string blocksJson)
    {
        var compiled = MarketingContentComposer.Build(blocksJson);
        if (!compiled.Success) return BadRequest(new { message = compiled.Error });
        var html = await Sender.Send(new PreviewMarketingCampaignQuery(subject ?? string.Empty, preheader ?? string.Empty, compiled.Html));
        return Content(html, "text/html; charset=utf-8");
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id)
    {
        var campaign = await Sender.Send(new GetMarketingBroadcastDetailsQuery(id));
        if (campaign is null) return NotFound();

        ViewData["Title"] = campaign.Broadcast.Name;
        ViewData["Heading"] = campaign.Broadcast.Name;
        ViewData["ActiveNav"] = "marketing";
        var segment = Enum.TryParse<MarketingAudienceSegment>(campaign.Broadcast.AudienceSegment, out var parsed)
            ? parsed : MarketingAudienceSegment.AllSubscribed;
        ViewBag.EligibleRecipientCount = await Sender.Send(new GetMarketingRecipientCountQuery(segment));
        ViewBag.RenderedPreviewHtml = await Sender.Send(new PreviewMarketingBroadcastQuery(id));
        return View(campaign);
    }

    /// <summary>Saved preview rendered by the same service used by test and audience sends.</summary>
    [HttpGet("{id:guid}/preview")]
    public async Task<IActionResult> Preview(Guid id)
    {
        var campaign = await Sender.Send(new GetMarketingBroadcastDetailsQuery(id));
        if (campaign is null) return NotFound();
        var rendered = await Sender.Send(new PreviewMarketingBroadcastQuery(id));
        return Content(rendered, "text/html; charset=utf-8");
    }

    [HttpGet("{id:guid}/confirm")]
    [Authorize(Policy = Permissions.Marketing.Manage)]
    public async Task<IActionResult> Confirm(Guid id)
    {
        var campaign = await Sender.Send(new GetMarketingBroadcastDetailsQuery(id));
        if (campaign is null) return NotFound();
        if (campaign.Broadcast.Status != nameof(MarketingBroadcastStatus.Draft))
        {
            TempData["Error"] = "Only a draft campaign can be sent or scheduled.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var segment = Enum.TryParse<MarketingAudienceSegment>(campaign.Broadcast.AudienceSegment, out var parsed)
            ? parsed : MarketingAudienceSegment.AllSubscribed;
        var count = await Sender.Send(new GetMarketingRecipientCountQuery(segment));
        ViewData["Title"] = "Confirm campaign";
        ViewData["Heading"] = "Review campaign send";
        ViewData["ActiveNav"] = "marketing";
        ViewBag.Campaign = campaign.Broadcast;
        ViewBag.AudienceLabel = AudienceLabel(segment);
        return View(new MarketingConfirmFormViewModel { BroadcastId = id, ExpectedRecipientCount = count });
    }

    [HttpPost("{id:guid}/confirm")]
    [Authorize(Policy = Permissions.Marketing.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(Guid id, MarketingConfirmFormViewModel model)
    {
        if (!model.Confirm)
        {
            TempData["Error"] = "Confirm the campaign and audience before it can be sent or scheduled.";
            return RedirectToAction(nameof(Confirm), new { id });
        }

        try
        {
            if (string.Equals(model.SendMode, "schedule", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseUtc(model.ScheduledAtUtc, out var scheduledAtUtc))
                {
                    TempData["Error"] = "Choose a valid future date and time.";
                    return RedirectToAction(nameof(Confirm), new { id });
                }
                await Sender.Send(new ScheduleMarketingBroadcastCommand(id, scheduledAtUtc, model.ExpectedRecipientCount));
                TempData["Success"] = "Campaign scheduled. Its audience will be checked again when sending begins.";
            }
            else if (string.Equals(model.SendMode, "now", StringComparison.OrdinalIgnoreCase))
            {
                await Sender.Send(new SendMarketingBroadcastCommand(id, model.ExpectedRecipientCount));
                TempData["Success"] = "Campaign queued. The email service will process it in batches.";
            }
            else
            {
                TempData["Error"] = "Choose whether to send now or schedule the campaign.";
                return RedirectToAction(nameof(Confirm), new { id });
            }
        }
        catch (BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
            if (ex.Code == "marketing.audiencechanged") return RedirectToAction(nameof(Confirm), new { id });
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/test")]
    [Authorize(Policy = Permissions.Marketing.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Test(Guid id, MarketingTestSendFormViewModel model)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault()
                ?? "Enter a valid email address for the test send.";
            return RedirectToAction(nameof(Details), new { id });
        }
        try
        {
            var accepted = await Sender.Send(new SendMarketingTestEmailCommand(id, model.TestEmail));
            TempData[accepted ? "Success" : "Error"] = accepted
                ? $"Test email accepted for {model.TestEmail.Trim()}. No campaign customers were contacted."
                : "The test email was not accepted. Check the address and email delivery setup, then try again. No campaign customers were contacted.";
        }
        catch (BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (ValidationAppException ex)
        {
            TempData["Error"] = ex.Errors.Values.SelectMany(x => x).FirstOrDefault() ?? "Check the test email address and try again.";
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/reschedule")]
    [Authorize(Policy = Permissions.Marketing.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reschedule(Guid id, MarketingRescheduleFormViewModel model)
    {
        if (!TryParseUtc(model.ScheduledAtUtc, out var scheduledAtUtc))
            TempData["Error"] = "Choose a valid future date and time.";
        else
        {
            try
            {
                await Sender.Send(new RescheduleMarketingBroadcastCommand(id, scheduledAtUtc));
                TempData["Success"] = "Scheduled send time updated.";
            }
            catch (BusinessRuleValidationException ex) { TempData["Error"] = ex.Message; }
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = Permissions.Marketing.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id)
    {
        try
        {
            await Sender.Send(new CancelScheduledMarketingBroadcastCommand(id));
            TempData["Success"] = "Scheduled campaign cancelled. No campaign emails were sent.";
        }
        catch (BusinessRuleValidationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/retry")]
    [Authorize(Policy = Permissions.Marketing.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Retry(Guid id)
    {
        try
        {
            await Sender.Send(new RetryFailedRecipientsCommand(id));
            TempData["Success"] = "Failed recipients requeued for delivery.";
        }
        catch (BusinessRuleValidationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task SetEditorPageAsync(MarketingBroadcastFormViewModel model, string title, string heading, Guid? id = null)
    {
        ViewData["Title"] = title;
        ViewData["Heading"] = heading;
        ViewData["ActiveNav"] = "marketing";
        ViewBag.EligibleRecipients = await Sender.Send(new GetMarketingRecipientCountQuery(model.AudienceSegment));
        ViewBag.EditId = id;
    }

    private static string AudienceLabel(MarketingAudienceSegment segment) => segment switch
    {
        MarketingAudienceSegment.PreviousPurchasers => "Previous purchasers",
        MarketingAudienceSegment.NoPurchaseInLast90Days => "No paid purchase in the last 90 days",
        _ => "All subscribed customers"
    };

    private static bool TryParseUtc(string? value, out DateTime utc)
    {
        utc = default;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) return false;
        utc = parsed.UtcDateTime;
        return utc > DateTime.UtcNow;
    }

    private void AddValidationErrors(ValidationAppException ex)
    {
        foreach (var (key, messages) in ex.Errors)
        {
            var field = key.StartsWith("Request.", StringComparison.Ordinal) ? key["Request.".Length..] : key;
            foreach (var message in messages) ModelState.AddModelError(field, message);
        }
    }
}
