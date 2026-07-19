using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zumingtalk.Service.Commerce;
using Zumingtalk.Service.Data;

namespace Zumingtalk.Service.Payments;

public sealed class PaymentService(
    ServiceDbContext db,
    TimeProvider timeProvider,
    IOptions<AlipayOptions> options,
    IAlipayGatewayClient gatewayClient)
{
    private readonly AlipayOptions options = options.Value;

    public async Task<CreateOrderResponse> CreateOrderAsync(Guid activationId, string productId, CancellationToken cancellationToken)
    {
        options.Validate();
        var amountFen = GetProductAmount(productId);
        var activationExists = await db.DeviceActivations.AnyAsync(
            value => value.Id == activationId && value.RevokedAt == null,
            cancellationToken);
        if (!activationExists)
        {
            throw new KeyNotFoundException("Activation was not found.");
        }

        await CloseExpiredOrdersAsync(activationId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var order = new Order
        {
            OrderNo = CreateOrderNo(now),
            ActivationId = activationId,
            ProductId = productId,
            AmountFen = amountFen,
            Status = OrderStatus.PendingPayment,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(30),
            UpdatedAt = now
        };
        var checkoutUrl = gatewayClient.BuildCheckoutUrl(order.OrderNo, order.ProductId, order.AmountFen);
        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        return new CreateOrderResponse(order.OrderNo, order.ProductId, order.AmountFen, checkoutUrl, order.ExpiresAt);
    }

    public async Task<OrderStatusResponse> GetOrderAsync(Guid activationId, string orderNo, CancellationToken cancellationToken)
    {
        await CloseExpiredOrdersAsync(activationId, cancellationToken);
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(
            value => value.ActivationId == activationId && value.OrderNo == orderNo,
            cancellationToken) ?? throw new KeyNotFoundException("Order was not found.");
        return ToResponse(order);
    }

    public async Task<bool> CloseOrderAsync(Guid activationId, string orderNo, CancellationToken cancellationToken)
    {
        var order = await db.Orders.SingleOrDefaultAsync(
            value => value.ActivationId == activationId && value.OrderNo == orderNo,
            cancellationToken);
        if (order is null)
        {
            return false;
        }

        if (order.Status == OrderStatus.PendingPayment)
        {
            var now = timeProvider.GetUtcNow();
            order.Status = OrderStatus.Closed;
            order.ClosedAt = now;
            order.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public async Task<PaymentNotificationResult> ProcessAlipayNotificationAsync(
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        options.Validate();
        if (!TryGetRequired(form, "notify_id", out var notificationId) ||
            !TryGetRequired(form, "out_trade_no", out var orderNo) ||
            !TryGetRequired(form, "trade_status", out var tradeStatus) ||
            !TryGetRequired(form, "sign", out var signature))
        {
            return PaymentNotificationResult.Failure("missing_fields");
        }

        var signatureVerified = AlipayRsa2.Verify(form, signature, options.GetAlipayPublicKey());
        var existing = await db.PaymentNotifications.AsNoTracking().SingleOrDefaultAsync(
            value => value.Provider == "alipay" && value.ProviderNotificationId == notificationId,
            cancellationToken);
        if (existing is not null)
        {
            return existing.SignatureVerified && existing.Processed
                ? PaymentNotificationResult.Success(existing.ProcessingResult)
                : PaymentNotificationResult.Failure(existing.ProcessingResult);
        }

        var amountFen = TryParseAmountFen(form.GetValueOrDefault("total_amount"));
        var notification = new PaymentNotification
        {
            Provider = "alipay",
            ProviderNotificationId = notificationId,
            OrderNo = orderNo,
            SignatureVerified = signatureVerified,
            EventType = tradeStatus,
            ProviderTradeNo = form.GetValueOrDefault("trade_no"),
            AmountFen = amountFen,
            Processed = false,
            ProcessingResult = signatureVerified ? "validation_pending" : "invalid_signature",
            ReceivedAt = timeProvider.GetUtcNow(),
            VerifiedAt = signatureVerified ? timeProvider.GetUtcNow() : null
        };
        db.PaymentNotifications.Add(notification);

        if (!signatureVerified ||
            !string.Equals(form.GetValueOrDefault("app_id"), options.AppId, StringComparison.Ordinal) ||
            !string.Equals(form.GetValueOrDefault("seller_id"), options.SellerId, StringComparison.Ordinal))
        {
            notification.ProcessingResult = signatureVerified ? "identity_mismatch" : "invalid_signature";
            await db.SaveChangesAsync(cancellationToken);
            return PaymentNotificationResult.Failure(notification.ProcessingResult);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var order = await db.Orders.SingleOrDefaultAsync(value => value.OrderNo == orderNo, cancellationToken);
        if (order is null)
        {
            notification.ProcessingResult = "unknown_order";
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PaymentNotificationResult.Failure(notification.ProcessingResult);
        }

        if (amountFen is null || amountFen != order.AmountFen)
        {
            notification.ProcessingResult = "amount_mismatch";
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PaymentNotificationResult.Failure(notification.ProcessingResult);
        }

        var now = timeProvider.GetUtcNow();
        var result = tradeStatus switch
        {
            "TRADE_SUCCESS" or "TRADE_FINISHED" => await MarkPaidAsync(order, notification.ProviderTradeNo, now, cancellationToken),
            "TRADE_CLOSED" => MarkClosed(order, now),
            _ => "ignored_status"
        };
        notification.Processed = true;
        notification.ProcessingResult = result;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return notification.Processed
            ? PaymentNotificationResult.Success(result)
            : PaymentNotificationResult.Failure(result);
    }

    public async Task<RefundOrderResponse> RefundAsync(string orderNo, CancellationToken cancellationToken)
    {
        var order = await db.Orders.SingleOrDefaultAsync(value => value.OrderNo == orderNo, cancellationToken)
            ?? throw new KeyNotFoundException("Order was not found.");
        if (order.Status == OrderStatus.Refunded)
        {
            return new RefundOrderResponse(order.OrderNo, order.Status.ToString(), order.RefundRequestNo!);
        }

        if (order.Status != OrderStatus.Paid)
        {
            throw new InvalidOperationException("Only a paid order can be refunded.");
        }

        var refundRequestNo = order.RefundRequestNo ?? $"R{Guid.NewGuid():N}";
        order.RefundRequestNo = refundRequestNo;
        await db.SaveChangesAsync(cancellationToken);

        var gatewayResult = await gatewayClient.RefundAsync(order.OrderNo, refundRequestNo, order.AmountFen, cancellationToken);
        if (!gatewayResult.SignatureVerified)
        {
            throw new InvalidOperationException("Alipay refund response signature verification failed.");
        }

        if (!gatewayResult.Succeeded)
        {
            throw new InvalidOperationException($"Alipay refund failed: {gatewayResult.Code} {gatewayResult.Message}");
        }

        var now = timeProvider.GetUtcNow();
        order.Status = OrderStatus.Refunded;
        order.ProviderTradeNo ??= gatewayResult.TradeNo;
        order.RefundedAt = now;
        order.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return new RefundOrderResponse(order.OrderNo, order.Status.ToString(), refundRequestNo);
    }

    private async Task<string> MarkPaidAsync(Order order, string? tradeNo, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (order.Status == OrderStatus.Paid || order.Status == OrderStatus.Refunded)
        {
            return "already_paid";
        }

        if (order.Status != OrderStatus.PendingPayment || order.ExpiresAt <= now)
        {
            return "invalid_transition";
        }

        order.Status = OrderStatus.Paid;
        order.ProviderTradeNo = tradeNo;
        order.PaidAt = now;
        order.UpdatedAt = now;
        if (order.ProductId == "pro_month")
        {
            var proEnds = await db.Entitlements
                .Where(value => value.ActivationId == order.ActivationId && value.Plan == EntitlementPlan.Pro)
                .Select(value => value.EndsAt)
                .ToListAsync(cancellationToken);
            var currentEnd = proEnds.Where(value => value > now).Select(value => (DateTimeOffset?)value).Max();
            var startsAt = currentEnd is null ? now : currentEnd.Value;
            var entitlement = new Entitlement
            {
                ActivationId = order.ActivationId,
                Plan = EntitlementPlan.Pro,
                StartsAt = startsAt,
                EndsAt = startsAt.AddDays(30)
            };
            db.Entitlements.Add(entitlement);
            db.QuotaBuckets.Add(new QuotaBucket
            {
                ActivationId = order.ActivationId,
                Entitlement = entitlement,
                Kind = QuotaBucketKind.ProMonthly,
                RemainingSeconds = 36_000,
                ExpiresAt = entitlement.EndsAt,
                CreatedAt = now
            });
        }
        else if (order.ProductId == "add_on_10h")
        {
            db.QuotaBuckets.Add(new QuotaBucket
            {
                ActivationId = order.ActivationId,
                Kind = QuotaBucketKind.AddOn,
                RemainingSeconds = 36_000,
                CreatedAt = now
            });
        }

        return "paid";
    }

    private static string MarkClosed(Order order, DateTimeOffset now)
    {
        if (order.Status == OrderStatus.Closed)
        {
            return "already_closed";
        }

        if (order.Status != OrderStatus.PendingPayment)
        {
            return "invalid_transition";
        }

        order.Status = OrderStatus.Closed;
        order.ClosedAt = now;
        order.UpdatedAt = now;
        return "closed";
    }

    private async Task CloseExpiredOrdersAsync(Guid activationId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var pending = await db.Orders
            .Where(value => value.ActivationId == activationId && value.Status == OrderStatus.PendingPayment)
            .ToListAsync(cancellationToken);
        var expired = pending.Where(value => value.ExpiresAt <= now).ToList();
        foreach (var order in expired)
        {
            MarkClosed(order, now);
        }

        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private int GetProductAmount(string productId) => productId switch
    {
        "pro_month" => options.ProMonthAmountFen,
        "add_on_10h" => options.AddOnAmountFen,
        _ => throw new UnknownProductException(productId)
    };

    private static string CreateOrderNo(DateTimeOffset now) => $"ZM{now:yyyyMMddHHmmss}{Guid.NewGuid():N}"[..40];

    private static bool TryGetRequired(IReadOnlyDictionary<string, string> form, string name, out string value)
    {
        value = form.GetValueOrDefault(name) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static int? TryParseAmountFen(string? value)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount < 0)
        {
            return null;
        }

        var fen = amount * 100m;
        return decimal.Truncate(fen) == fen && fen <= int.MaxValue
            ? decimal.ToInt32(fen)
            : null;
    }

    private static OrderStatusResponse ToResponse(Order order) =>
        new(order.OrderNo, order.ProductId, order.AmountFen, order.Status.ToString(), order.ExpiresAt);
}

public sealed record PaymentNotificationResult(bool Succeeded, string Result)
{
    public static PaymentNotificationResult Success(string result) => new(true, result);
    public static PaymentNotificationResult Failure(string result) => new(false, result);
}

public sealed class UnknownProductException(string productId) : Exception($"Unknown product: {productId}");
