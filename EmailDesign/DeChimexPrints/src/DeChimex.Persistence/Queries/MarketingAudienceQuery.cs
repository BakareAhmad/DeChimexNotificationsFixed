using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Persistence.Queries;

internal static class MarketingAudienceQuery
{
    public static IQueryable<Customer> EligibleForMarketing(
        this IQueryable<Customer> customers,
        DeChimexDbContext db,
        MarketingAudienceSegment segment,
        DateTime asOfUtc)
    {
        var eligible = customers.Where(c => !c.IsGuest && c.Email != null && c.MarketingSubscribed);
        var recentCutoff = asOfUtc.AddDays(-90);

        return segment switch
        {
            MarketingAudienceSegment.AllSubscribed => eligible,
            MarketingAudienceSegment.PreviousPurchasers => eligible.Where(c =>
                db.Orders.Any(o => o.CustomerId == c.Id
                    && o.PaidAtUtc != null
                    && o.Status != OrderStatus.Cancelled
                    && o.Status != OrderStatus.Refunded)),
            MarketingAudienceSegment.NoPurchaseInLast90Days => eligible.Where(c =>
                !db.Orders.Any(o => o.CustomerId == c.Id
                    && o.PaidAtUtc != null
                    && o.PaidAtUtc >= recentCutoff
                    && o.Status != OrderStatus.Cancelled
                    && o.Status != OrderStatus.Refunded)),
            _ => eligible.Where(_ => false)
        };
    }
}
