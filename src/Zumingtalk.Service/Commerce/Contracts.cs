namespace Zumingtalk.Service.Commerce;

public sealed record ActivationRequest(string InviteCode, string DeviceFingerprint);
public sealed record ActivationResponse(string DeviceToken, DateTimeOffset ServerTime);
public sealed record CreateInviteResponse(string InviteCode);
public sealed record EntitlementResponse(string Plan, DateTimeOffset ServerTime, IReadOnlyList<QuotaBucketResponse> QuotaBuckets);
public sealed record QuotaBucketResponse(string Kind, int RemainingSeconds, DateTimeOffset? ExpiresAt);
public sealed record CreateAsrSessionResponse(Guid SessionId, int ReservedSeconds, DateTimeOffset ServerTime, string StreamUrl);
public sealed record FinishAsrSessionResponse(Guid SessionId, int ChargedSeconds, string Outcome);
public sealed record CreateOrderRequest(string ProductId);
public sealed record CreateOrderResponse(string OrderNo, string ProductId, int AmountFen, string CheckoutUrl, DateTimeOffset ExpiresAt);
public sealed record OrderStatusResponse(string OrderNo, string ProductId, int AmountFen, string Status, DateTimeOffset ExpiresAt);
public sealed record RefundOrderResponse(string OrderNo, string Status, string RefundRequestNo);
