using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zumingtalk.Service.Data;
using Zumingtalk.Service.Security;

namespace Zumingtalk.Service.Commerce;

public sealed class AdminCommerceService(ServiceDbContext dbContext, TimeProvider timeProvider)
{
    public async Task<CreateInviteResponse> CreateInviteAsync(string actor, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        InviteCode? invite = null;
        string? plainTextCode = null;
        for (var attempt = 0; attempt < 8 && invite is null; attempt++)
        {
            var candidate = GenerateInviteCode();
            var candidateHash = SecretHasher.Hash(candidate);
            var exists = await dbContext.InviteCodes.AnyAsync(value => value.CodeHash == candidateHash, cancellationToken);
            if (!exists)
            {
                plainTextCode = candidate;
                invite = new InviteCode { CodeHash = candidateHash, CreatedAt = now };
            }
        }

        if (invite is null || plainTextCode is null)
        {
            throw new InvalidOperationException("Unable to allocate an invitation code.");
        }

        dbContext.InviteCodes.Add(invite);
        AddAudit(actor, "invite.create", invite.Id, new { status = "available" }, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new CreateInviteResponse(plainTextCode);
    }

    public async Task<bool> ResetDeviceAsync(Guid activationId, string actor, CancellationToken cancellationToken)
    {
        var activation = await dbContext.DeviceActivations.Include(value => value.InviteCode)
            .SingleOrDefaultAsync(value => value.Id == activationId, cancellationToken);
        if (activation is null)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        activation.RevokedAt = now;
        if (activation.InviteCode is not null)
        {
            activation.InviteCode.Status = InviteCodeStatus.Available;
            activation.InviteCode.ActivatedDeviceId = null;
            activation.InviteCode.ActivatedAt = null;
        }

        AddAudit(actor, "device.reset", activationId, new { revoked = true }, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> GrantProAsync(Guid activationId, string actor, CancellationToken cancellationToken)
    {
        var activation = await dbContext.DeviceActivations.SingleOrDefaultAsync(value => value.Id == activationId && value.RevokedAt == null, cancellationToken);
        if (activation is null)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        var currentEnd = (await dbContext.Entitlements.AsNoTracking()
            .Where(value => value.ActivationId == activationId && value.Plan == EntitlementPlan.Pro)
            .Select(value => value.EndsAt)
            .ToListAsync(cancellationToken))
            .Where(value => value > now)
            .DefaultIfEmpty(now)
            .Max();
        var startsAt = currentEnd > now ? currentEnd : now;
        var entitlement = new Entitlement
        {
            ActivationId = activationId,
            Plan = EntitlementPlan.Pro,
            StartsAt = startsAt,
            EndsAt = startsAt.AddDays(30)
        };
        var quota = new QuotaBucket
        {
            ActivationId = activationId,
            EntitlementId = entitlement.Id,
            Kind = QuotaBucketKind.ProMonthly,
            RemainingSeconds = 36_000,
            ReservedSeconds = 0,
            ExpiresAt = entitlement.EndsAt,
            CreatedAt = now
        };
        dbContext.Entitlements.Add(entitlement);
        dbContext.QuotaBuckets.Add(quota);
        AddAudit(actor, "entitlement.grant_pro", activationId, new { days = 30, seconds = 36000 }, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> GrantAddOnAsync(Guid activationId, string actor, CancellationToken cancellationToken)
    {
        var active = await dbContext.DeviceActivations.AnyAsync(value => value.Id == activationId && value.RevokedAt == null, cancellationToken);
        if (!active)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        dbContext.QuotaBuckets.Add(new QuotaBucket
        {
            ActivationId = activationId,
            Kind = QuotaBucketKind.AddOn,
            RemainingSeconds = 36_000,
            ReservedSeconds = 0,
            ExpiresAt = null,
            CreatedAt = now
        });
        AddAudit(actor, "quota.grant_add_on", activationId, new { seconds = 36000 }, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private void AddAudit(string actor, string action, Guid targetId, object metadata, DateTimeOffset now) =>
        dbContext.AdminAuditLogs.Add(new AdminAuditLog
        {
            Actor = actor,
            Action = action,
            TargetId = targetId.ToString("D"),
            Metadata = JsonSerializer.Serialize(metadata),
            CreatedAt = now
        });

    private static string GenerateInviteCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(16);
        return string.Concat("ZT-", string.Create(16, bytes, static (buffer, source) =>
        {
            for (var index = 0; index < buffer.Length; index++)
            {
                buffer[index] = alphabet[source[index] % alphabet.Length];
            }
        }));
    }
}
