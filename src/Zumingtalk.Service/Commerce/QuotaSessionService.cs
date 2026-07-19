using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zumingtalk.Service.Data;

namespace Zumingtalk.Service.Commerce;

public sealed class QuotaUnavailableException : Exception
{
    public QuotaUnavailableException() : base("No active cloud quota is available.") { }
}

public sealed class QuotaSessionService(ServiceDbContext dbContext, TimeProvider timeProvider)
{
    public const int MaximumReservationSeconds = 600;
    public const int PcmBytesPerSecond = 32_000;

    public async Task<CreateAsrSessionResponse> ReserveAsync(Guid activationId, string streamUrl, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var buckets = await dbContext.QuotaBuckets
            .Where(value => value.ActivationId == activationId)
            .ToListAsync(cancellationToken);
        var allocations = buckets
            .Where(value => IsEligible(value, now))
            .OrderBy(value => BucketPriority(value.Kind))
            .ThenBy(value => value.ExpiresAt)
            .Select(value => new { Bucket = value, Available = Math.Max(0, value.RemainingSeconds - value.ReservedSeconds) })
            .Where(value => value.Available > 0)
            .ToList();
        var totalAvailable = allocations.Sum(value => value.Available);
        if (totalAvailable == 0)
        {
            throw new QuotaUnavailableException();
        }

        var session = new AsrSession { ActivationId = activationId, Source = "ZumingtalkCloud", CreatedAt = now };
        var reservationLimit = Math.Min(MaximumReservationSeconds, totalAvailable);
        var stillNeeded = reservationLimit;
        foreach (var allocation in allocations)
        {
            if (stillNeeded == 0)
            {
                break;
            }

            var reserved = Math.Min(stillNeeded, allocation.Available);
            allocation.Bucket.ReservedSeconds += reserved;
            session.Reservations.Add(new AsrSessionReservation { QuotaBucketId = allocation.Bucket.Id, ReservedSeconds = reserved });
            stillNeeded -= reserved;
        }

        dbContext.AsrSessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreateAsrSessionResponse(session.Id, reservationLimit, now, $"{streamUrl}?sessionId={session.Id:D}");
    }

    public async Task RecordPcmAsync(Guid sessionId, Guid activationId, int byteCount, CancellationToken cancellationToken)
    {
        if (byteCount <= 0 || byteCount % 2 != 0)
        {
            throw new InvalidOperationException("PCM frames must contain complete 16-bit samples.");
        }

        var session = await dbContext.AsrSessions.SingleOrDefaultAsync(value => value.Id == sessionId && value.ActivationId == activationId, cancellationToken)
            ?? throw new KeyNotFoundException("ASR session was not found.");
        if (session.Status is AsrSessionStatus.Finished or AsrSessionStatus.Released)
        {
            throw new InvalidOperationException("ASR session is already closed.");
        }

        var reservedSeconds = await dbContext.AsrSessionReservations.Where(value => value.SessionId == sessionId).SumAsync(value => value.ReservedSeconds, cancellationToken);
        if (session.ReceivedPcmBytes + byteCount > (long)reservedSeconds * PcmBytesPerSecond)
        {
            throw new InvalidOperationException("ASR session exceeded its maximum duration.");
        }

        session.Status = AsrSessionStatus.Streaming;
        session.ReceivedPcmBytes += byteCount;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<FinishAsrSessionResponse> FinishAsync(Guid sessionId, Guid activationId, bool succeeded, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var session = await dbContext.AsrSessions.Include(value => value.Reservations)
            .SingleOrDefaultAsync(value => value.Id == sessionId && value.ActivationId == activationId, cancellationToken)
            ?? throw new KeyNotFoundException("ASR session was not found.");
        if (session.Status is AsrSessionStatus.Finished or AsrSessionStatus.Released)
        {
            var priorLedger = await dbContext.UsageLedger.AsNoTracking().SingleOrDefaultAsync(value => value.SessionId == sessionId.ToString("D"), cancellationToken);
            return new FinishAsrSessionResponse(sessionId, priorLedger?.Seconds ?? 0, priorLedger?.Outcome ?? "released");
        }

        var reservations = (await dbContext.AsrSessionReservations.Include(value => value.QuotaBucket)
            .Where(value => value.SessionId == sessionId)
            .ToListAsync(cancellationToken))
            .OrderBy(value => BucketPriority(value.QuotaBucket!.Kind))
            .ToList();
        var charged = succeeded ? (int)Math.Ceiling(session.ReceivedPcmBytes / (double)PcmBytesPerSecond) : 0;
        var remainingCharge = charged;
        foreach (var reservation in reservations)
        {
            var release = reservation.ReservedSeconds;
            var consume = Math.Min(remainingCharge, reservation.ReservedSeconds);
            reservation.QuotaBucket!.ReservedSeconds -= release;
            reservation.QuotaBucket.RemainingSeconds -= consume;
            remainingCharge -= consume;
        }

        if (remainingCharge != 0)
        {
            throw new InvalidOperationException("Reserved quota could not cover the session charge.");
        }

        session.FinishedAt = now;
        session.Status = succeeded ? AsrSessionStatus.Finished : AsrSessionStatus.Released;
        dbContext.UsageLedger.Add(new UsageLedgerEntry
        {
            SessionId = sessionId.ToString("D"),
            ActivationId = activationId,
            Source = session.Source,
            Seconds = charged,
            Outcome = succeeded ? "completed" : "released",
            CreatedAt = now
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new FinishAsrSessionResponse(sessionId, charged, succeeded ? "completed" : "released");
    }

    private static bool IsEligible(QuotaBucket bucket, DateTimeOffset now) => bucket.ExpiresAt is null || bucket.ExpiresAt > now;

    private static int BucketPriority(QuotaBucketKind kind) => kind switch
    {
        QuotaBucketKind.Trial => 0,
        QuotaBucketKind.ProMonthly => 1,
        QuotaBucketKind.AddOn => 2,
        _ => 99
    };
}
