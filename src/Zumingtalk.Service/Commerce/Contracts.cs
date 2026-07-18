namespace Zumingtalk.Service.Commerce;

public sealed record ActivationRequest(string InviteCode, string DeviceFingerprint);
public sealed record ActivationResponse(string DeviceToken, DateTimeOffset ServerTime);
public sealed record CreateInviteResponse(string InviteCode);
public sealed record EntitlementResponse(string Plan, DateTimeOffset ServerTime, IReadOnlyList<QuotaBucketResponse> QuotaBuckets);
public sealed record QuotaBucketResponse(string Kind, int RemainingSeconds, DateTimeOffset? ExpiresAt);
