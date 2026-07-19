using Microsoft.EntityFrameworkCore;
using Zumingtalk.Service.Data;

namespace Zumingtalk.Service.Commerce;

public sealed class CostBenchmarkService(ServiceDbContext db)
{
    public async Task<CostBenchmarkResponse> GetAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (from >= to || to - from > TimeSpan.FromDays(90))
        {
            throw new ArgumentOutOfRangeException(nameof(from), "Cost benchmark range must be positive and no longer than 90 days.");
        }

        var ledgers = (await db.UsageLedger.AsNoTracking().ToListAsync(cancellationToken))
            .Where(value => value.CreatedAt >= from && value.CreatedAt < to)
            .ToList();
        var sessions = (await db.AsrSessions.AsNoTracking().ToListAsync(cancellationToken))
            .Where(value => value.CreatedAt >= from && value.CreatedAt < to)
            .ToList();
        var orders = (await db.Orders.AsNoTracking().ToListAsync(cancellationToken))
            .Where(value => value.CreatedAt >= from && value.CreatedAt < to)
            .ToList();
        var notifications = (await db.PaymentNotifications.AsNoTracking().ToListAsync(cancellationToken))
            .Where(value => value.ReceivedAt >= from && value.ReceivedAt < to)
            .ToList();

        var pcmBytes = sessions.Sum(value => value.ReceivedPcmBytes);
        return new CostBenchmarkResponse(
            from,
            to,
            pcmBytes,
            pcmBytes / (double)QuotaSessionService.PcmBytesPerSecond,
            ledgers.Where(value => value.Outcome == "completed").Sum(value => value.Seconds),
            ledgers.Count(value => value.Outcome == "completed"),
            ledgers.Count(value => value.Outcome == "released"),
            sessions.Count,
            orders.Count(value => value.Status is OrderStatus.Paid or OrderStatus.Refunded),
            orders.Where(value => value.Status is OrderStatus.Paid or OrderStatus.Refunded).Sum(value => value.AmountFen),
            orders.Count(value => value.Status == OrderStatus.Refunded),
            notifications.Count,
            notifications.Count(value => !value.SignatureVerified));
    }
}

public sealed record CostBenchmarkResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    long ReceivedPcmBytes,
    double ReceivedAudioSeconds,
    int ChargedAudioSeconds,
    int CompletedSessions,
    int ReleasedSessions,
    int TotalSessions,
    int PaidOrderCount,
    int PaidOrderAmountFen,
    int RefundedOrderCount,
    int PaymentNotificationCount,
    int InvalidPaymentNotificationCount);
