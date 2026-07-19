using Microsoft.EntityFrameworkCore;

namespace Zumingtalk.Service.Data;

public sealed class ServiceDbContext(DbContextOptions<ServiceDbContext> options) : DbContext(options)
{
    public DbSet<InviteCode> InviteCodes => Set<InviteCode>();
    public DbSet<DeviceActivation> DeviceActivations => Set<DeviceActivation>();
    public DbSet<Entitlement> Entitlements => Set<Entitlement>();
    public DbSet<QuotaBucket> QuotaBuckets => Set<QuotaBucket>();
    public DbSet<UsageLedgerEntry> UsageLedger => Set<UsageLedgerEntry>();
    public DbSet<AsrSession> AsrSessions => Set<AsrSession>();
    public DbSet<AsrSessionReservation> AsrSessionReservations => Set<AsrSessionReservation>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<PaymentNotification> PaymentNotifications => Set<PaymentNotification>();
    public DbSet<AdminAuditLog> AdminAuditLogs => Set<AdminAuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureInviteCode(modelBuilder.Entity<InviteCode>());
        ConfigureActivation(modelBuilder.Entity<DeviceActivation>());
        ConfigureEntitlement(modelBuilder.Entity<Entitlement>());
        ConfigureQuotaBucket(modelBuilder.Entity<QuotaBucket>());
        ConfigureUsageLedger(modelBuilder.Entity<UsageLedgerEntry>());
        ConfigureAsrSession(modelBuilder.Entity<AsrSession>());
        ConfigureAsrSessionReservation(modelBuilder.Entity<AsrSessionReservation>());
        ConfigureOrder(modelBuilder.Entity<Order>());
        ConfigurePaymentNotification(modelBuilder.Entity<PaymentNotification>());
        ConfigureAuditLog(modelBuilder.Entity<AdminAuditLog>());

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static void ConfigureInviteCode(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<InviteCode> entity)
    {
        entity.ToTable("invite_codes");
        entity.HasKey(value => value.Id);
        entity.Property(value => value.CodeHash).HasMaxLength(64).IsRequired();
        entity.HasIndex(value => value.CodeHash).IsUnique();
        entity.HasIndex(value => value.ActivatedDeviceId).IsUnique();
        entity.HasOne(value => value.ActivatedDevice).WithOne().HasForeignKey<InviteCode>(value => value.ActivatedDeviceId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureActivation(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<DeviceActivation> entity)
    {
        entity.ToTable("device_activations");
        entity.HasKey(value => value.Id);
        entity.Property(value => value.DeviceFingerprintHash).HasMaxLength(64).IsRequired();
        entity.Property(value => value.TokenHash).HasMaxLength(64).IsRequired();
        entity.HasIndex(value => value.DeviceFingerprintHash).IsUnique();
        entity.HasIndex(value => value.TokenHash).IsUnique();
        entity.HasOne(value => value.InviteCode).WithMany().HasForeignKey(value => value.InviteCodeId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureEntitlement(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Entitlement> entity)
    {
        entity.ToTable("entitlements");
        entity.HasKey(value => value.Id);
        entity.HasIndex(value => new { value.ActivationId, value.Plan, value.EndsAt });
        entity.HasOne(value => value.Activation).WithMany(value => value.Entitlements).HasForeignKey(value => value.ActivationId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureQuotaBucket(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<QuotaBucket> entity)
    {
        entity.ToTable("quota_buckets");
        entity.HasKey(value => value.Id);
        entity.HasIndex(value => new { value.ActivationId, value.Kind, value.ExpiresAt });
        entity.HasOne(value => value.Activation).WithMany(value => value.QuotaBuckets).HasForeignKey(value => value.ActivationId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(value => value.Entitlement).WithMany().HasForeignKey(value => value.EntitlementId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureUsageLedger(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<UsageLedgerEntry> entity)
    {
        entity.ToTable("usage_ledger");
        entity.HasKey(value => value.Id);
        entity.Property(value => value.SessionId).HasMaxLength(128).IsRequired();
        entity.Property(value => value.Source).HasMaxLength(64).IsRequired();
        entity.Property(value => value.Outcome).HasMaxLength(64).IsRequired();
        entity.HasIndex(value => value.SessionId).IsUnique();
    }

    private static void ConfigureAsrSession(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<AsrSession> entity)
    {
        entity.ToTable("asr_sessions");
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Source).HasMaxLength(64).IsRequired();
        entity.HasIndex(value => new { value.ActivationId, value.Status });
    }

    private static void ConfigureAsrSessionReservation(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<AsrSessionReservation> entity)
    {
        entity.ToTable("asr_session_reservations");
        entity.HasKey(value => value.Id);
        entity.HasIndex(value => new { value.SessionId, value.QuotaBucketId }).IsUnique();
        entity.HasOne(value => value.Session).WithMany(value => value.Reservations).HasForeignKey(value => value.SessionId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(value => value.QuotaBucket).WithMany().HasForeignKey(value => value.QuotaBucketId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureOrder(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Order> entity)
    {
        entity.ToTable("orders");
        entity.HasKey(value => value.Id);
        entity.Property(value => value.OrderNo).HasMaxLength(64).IsRequired();
        entity.Property(value => value.ProductId).HasMaxLength(32).IsRequired();
        entity.Property(value => value.ProviderTradeNo).HasMaxLength(128);
        entity.Property(value => value.RefundRequestNo).HasMaxLength(64);
        entity.HasIndex(value => value.OrderNo).IsUnique();
        entity.HasIndex(value => new { value.ActivationId, value.Status });
        entity.HasOne(value => value.Activation).WithMany().HasForeignKey(value => value.ActivationId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigurePaymentNotification(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PaymentNotification> entity)
    {
        entity.ToTable("payment_notifications");
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Provider).HasMaxLength(32).IsRequired();
        entity.Property(value => value.ProviderNotificationId).HasMaxLength(128).IsRequired();
        entity.Property(value => value.OrderNo).HasMaxLength(64).IsRequired();
        entity.Property(value => value.EventType).HasMaxLength(32).IsRequired();
        entity.Property(value => value.ProviderTradeNo).HasMaxLength(128);
        entity.Property(value => value.ProcessingResult).HasMaxLength(64).IsRequired();
        entity.HasIndex(value => new { value.Provider, value.ProviderNotificationId }).IsUnique();
    }

    private static void ConfigureAuditLog(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<AdminAuditLog> entity)
    {
        entity.ToTable("admin_audit_logs");
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Actor).HasMaxLength(128).IsRequired();
        entity.Property(value => value.Action).HasMaxLength(128).IsRequired();
        entity.Property(value => value.TargetId).HasMaxLength(64).IsRequired();
        entity.Property(value => value.Metadata).HasMaxLength(2048).IsRequired();
        entity.HasIndex(value => value.CreatedAt);
    }

    private static string ToSnakeCase(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0 &&
                (char.IsLower(value[index - 1]) || (index + 1 < value.Length && char.IsLower(value[index + 1]))))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
