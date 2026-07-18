using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zumingtalk.Service.Data;
using Zumingtalk.Service.Security;

namespace Zumingtalk.Service.Commerce;

public enum ActivationStatus
{
    Success,
    InviteUnavailable,
    DeviceAlreadyActivated
}

public sealed record ActivationResult(ActivationStatus Status, ActivationResponse? Response);

public sealed class ActivationService(ServiceDbContext dbContext, TimeProvider timeProvider)
{
    public async Task<ActivationResult> ActivateAsync(ActivationRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var inviteHash = SecretHasher.Hash(request.InviteCode.Trim());
        var fingerprintHash = SecretHasher.Hash(request.DeviceFingerprint.Trim());

        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var invite = await dbContext.InviteCodes.SingleOrDefaultAsync(value => value.CodeHash == inviteHash, cancellationToken);
            if (invite is null || invite.Status != InviteCodeStatus.Available)
            {
                return new ActivationResult(ActivationStatus.InviteUnavailable, null);
            }

            var deviceAlreadyActivated = await dbContext.DeviceActivations.AnyAsync(
                value => value.DeviceFingerprintHash == fingerprintHash && value.RevokedAt == null,
                cancellationToken);
            if (deviceAlreadyActivated)
            {
                return new ActivationResult(ActivationStatus.DeviceAlreadyActivated, null);
            }

            var token = SecretHasher.GenerateDeviceToken();
            var activation = new DeviceActivation
            {
                DeviceFingerprintHash = fingerprintHash,
                TokenHash = SecretHasher.Hash(token),
                CreatedAt = now,
                InviteCodeId = invite.Id
            };
            var trial = new Entitlement
            {
                ActivationId = activation.Id,
                Plan = EntitlementPlan.Trial,
                StartsAt = now,
                EndsAt = now.AddDays(3)
            };
            var trialQuota = new QuotaBucket
            {
                ActivationId = activation.Id,
                EntitlementId = trial.Id,
                Kind = QuotaBucketKind.Trial,
                RemainingSeconds = 600,
                ReservedSeconds = 0,
                ExpiresAt = trial.EndsAt,
                CreatedAt = now
            };

            invite.Status = InviteCodeStatus.Activated;
            invite.ActivatedDeviceId = activation.Id;
            invite.ActivatedAt = now;
            dbContext.DeviceActivations.Add(activation);
            dbContext.Entitlements.Add(trial);
            dbContext.QuotaBuckets.Add(trialQuota);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ActivationResult(ActivationStatus.Success, new ActivationResponse(token, now));
        }
        catch (PostgresException exception) when (exception.SqlState is "23505" or "40001")
        {
            return new ActivationResult(ActivationStatus.InviteUnavailable, null);
        }
        catch (DbUpdateException)
        {
            return new ActivationResult(ActivationStatus.InviteUnavailable, null);
        }
    }
}
