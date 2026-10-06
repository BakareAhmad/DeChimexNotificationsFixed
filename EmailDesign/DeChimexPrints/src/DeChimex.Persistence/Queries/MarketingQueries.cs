using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Models;
using DeChimex.Contracts.Marketing;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Persistence.Queries;

public sealed class MarketingQueries(DeChimexDbContext db) : IMarketingQueries
{
    public async Task<PagedResult<MarketingBroadcastDto>> GetBroadcastsAsync(PagedRequest request, CancellationToken ct)
    {
        var query = db.MarketingBroadcasts.AsNoTracking().OrderByDescending(b => b.CreatedDateUtc);

        var total = await query.CountAsync(ct);
        var page = await query
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Include(b => b.Recipients)
            .ToListAsync(ct);

        var items = page.Select(ToDto).ToList();
        return new PagedResult<MarketingBroadcastDto>(items, total, request.Page, request.PageSize);
    }

    public async Task<MarketingBroadcastDetailsDto?> GetDetailsAsync(Guid id, CancellationToken ct)
    {
        var b = await db.MarketingBroadcasts.AsNoTracking()
            .Include(x => x.Recipients)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null) return null;

        return new MarketingBroadcastDetailsDto(
            ToDto(b),
            b.HtmlContent,
            b.ContentJson,
            b.Preheader,
            b.SenderName,
            b.ReplyToEmail,
            b.Recipients
                .OrderBy(r => r.Email)
                .Select(r => new MarketingRecipientDto(r.Email, r.FirstName, r.Status.ToString(), r.SentAtUtc, r.FailureReason))
                .ToList());
    }

    public Task<int> GetEligibleRecipientCountAsync(
        MarketingAudienceSegment segment, DateTime asOfUtc, CancellationToken ct)
        => db.Customers.AsNoTracking()
            .EligibleForMarketing(db, segment, asOfUtc)
            .CountAsync(ct);

    private static MarketingBroadcastDto ToDto(DeChimex.Domain.Entities.MarketingBroadcast b)
        => new(
            b.Id,
            b.Name,
            b.Subject,
            b.SenderName,
            b.ReplyToEmail,
            b.Status.ToString(),
            b.AudienceSegment.ToString(),
            b.RecipientCount,
            b.SentCount,
            b.FailedCount,
            b.CreatedDateUtc,
            b.CreatedBy,
            b.ScheduledAtUtc,
            b.QueuedAtUtc,
            b.CompletedAtUtc);
}
