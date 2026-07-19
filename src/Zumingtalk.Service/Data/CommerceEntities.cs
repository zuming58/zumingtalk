namespace Zumingtalk.Service.Data;

public enum InviteCodeStatus
{
    Available = 0,
    Activated = 1
}

public enum EntitlementPlan
{
    Trial = 0,
    Pro = 1
}

public enum QuotaBucketKind
{
    Trial = 0,
    ProMonthly = 1,
    AddOn = 2
}

public enum OrderStatus
{
    Created = 0,
    PendingPayment = 1,
    Paid = 2,
    Closed = 3,
    Refunded = 4
}

public sealed class InviteCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string CodeHash { get; set; }
    public InviteCodeStatus Status { get; set; } = InviteCodeStatus.Available;
    public Guid? ActivatedDeviceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DeviceActivation? ActivatedDevice { get; set; }
}

public sealed class DeviceActivation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string DeviceFingerprintHash { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid InviteCodeId { get; set; }
    public InviteCode? InviteCode { get; set; }
    public List<Entitlement> Entitlements { get; } = [];
    public List<QuotaBucket> QuotaBuckets { get; } = [];
}

public sealed class Entitlement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ActivationId { get; set; }
    public DeviceActivation? Activation { get; set; }
    public EntitlementPlan Plan { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
}

public sealed class QuotaBucket
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ActivationId { get; set; }
    public DeviceActivation? Activation { get; set; }
    public Guid? EntitlementId { get; set; }
    public Entitlement? Entitlement { get; set; }
    public QuotaBucketKind Kind { get; set; }
    public int RemainingSeconds { get; set; }
    public int ReservedSeconds { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class UsageLedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string SessionId { get; set; }
    public Guid ActivationId { get; set; }
    public required string Source { get; set; }
    public int Seconds { get; set; }
    public required string Outcome { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum AsrSessionStatus
{
    Reserved = 0,
    Streaming = 1,
    Finished = 2,
    Released = 3
}

public sealed class AsrSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ActivationId { get; set; }
    public required string Source { get; set; }
    public AsrSessionStatus Status { get; set; } = AsrSessionStatus.Reserved;
    public long ReceivedPcmBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public List<AsrSessionReservation> Reservations { get; } = [];
}

public sealed class AsrSessionReservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public AsrSession? Session { get; set; }
    public Guid QuotaBucketId { get; set; }
    public QuotaBucket? QuotaBucket { get; set; }
    public int ReservedSeconds { get; set; }
}

public sealed class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string OrderNo { get; set; }
    public Guid ActivationId { get; set; }
    public required string ProductId { get; set; }
    public int AmountFen { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Created;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class PaymentNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Provider { get; set; }
    public required string ProviderNotificationId { get; set; }
    public required string OrderNo { get; set; }
    public bool SignatureVerified { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
}

public sealed class AdminAuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public required string TargetId { get; set; }
    public required string Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
