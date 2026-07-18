using Microsoft.EntityFrameworkCore;
using Zumingtalk.Service.Data;

namespace Zumingtalk.Service.Commerce;

public sealed class EntitlementService(ServiceDbContext dbContext, TimeProvider timeProvider)
{
    public async Task<EntitlementResponse> GetAsync(Guid activationId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var entitlements = await dbContext.Entitlements.AsNoTracking()
            .Where(value => value.ActivationId == activationId)
            .ToListAsync(cancellationToken);
        var activePro = entitlements.Any(value => value.Plan == EntitlementPlan.Pro && value.StartsAt <= now && value.EndsAt > now);
        var buckets = (await dbContext.QuotaBuckets.AsNoTracking()
            .Where(value => value.ActivationId == activationId)
            .ToListAsync(cancellationToken))
            .Where(value => value.ExpiresAt is null || value.ExpiresAt > now)
            .OrderBy(value => value.Kind)
            .ThenBy(value => value.ExpiresAt)
            .Select(value => new QuotaBucketResponse(value.Kind.ToString(), value.RemainingSeconds, value.ExpiresAt))
            .ToList();
        return new EntitlementResponse(activePro ? "Pro" : "Trial", now, buckets);
    }
}
