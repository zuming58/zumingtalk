using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;
using Zumingtalk.Service.Commerce;
using Zumingtalk.Service.Data;
using Zumingtalk.Service.Security;
using Zumingtalk.Service.Payments;

namespace Zumingtalk.ServiceTests;

public sealed class CommerceApiTests(CommerceApiFactory factory) : IClassFixture<CommerceApiFactory>
{
    [Fact]
    public async Task InvitationCanActivateOnlyOneDeviceAndNeverPersistsPlainCredentials()
    {
        var inviteCode = await CreateInviteAsync();
        var activation = await ActivateAsync(inviteCode, "device-fingerprint-a");

        var repeat = await factory.CreateClient().PostAsJsonAsync("/api/activation", new ActivationRequest(inviteCode, "device-fingerprint-b"));
        var entitlement = await AuthenticatedGetAsync(activation.DeviceToken, "/api/me/entitlement");

        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        Assert.Equal(HttpStatusCode.OK, entitlement.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var storedInvite = await db.InviteCodes.SingleAsync(value => value.CodeHash == SecretHasher.Hash(inviteCode));
        var storedActivation = await db.DeviceActivations.SingleAsync(value => value.TokenHash == SecretHasher.Hash(activation.DeviceToken));
        Assert.NotEqual(inviteCode, storedInvite.CodeHash);
        Assert.NotEqual(activation.DeviceToken, storedActivation.TokenHash);
        Assert.Equal(64, storedInvite.CodeHash.Length);
        Assert.Equal(64, storedActivation.TokenHash.Length);
        Assert.Equal(600, await db.QuotaBuckets.Where(value => value.Kind == QuotaBucketKind.Trial).Select(value => value.RemainingSeconds).SingleAsync());
    }

    [Fact]
    public async Task DeviceResetRevokesTokenAndAllowsTheInvitationToBeRebound()
    {
        var inviteCode = await CreateInviteAsync();
        var activation = await ActivateAsync(inviteCode, "device-fingerprint-reset-a");
        var activationId = await GetActivationIdAsync(activation.DeviceToken);

        var reset = await AdminPostAsync($"/api/admin/devices/{activationId:D}/reset");
        var oldToken = await AuthenticatedGetAsync(activation.DeviceToken, "/api/me/entitlement");
        var rebound = await ActivateAsync(inviteCode, "device-fingerprint-reset-b");

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, oldToken.StatusCode);
        Assert.NotEmpty(rebound.DeviceToken);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var audit = await db.AdminAuditLogs.SingleAsync(value => value.Action == "device.reset");
        Assert.DoesNotContain(activation.DeviceToken, audit.Metadata, StringComparison.Ordinal);
        Assert.DoesNotContain(inviteCode, audit.Metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdministratorGrantsFixedProAndAddOnQuotasWithNonSensitiveAuditEntries()
    {
        var inviteCode = await CreateInviteAsync();
        var activation = await ActivateAsync(inviteCode, "device-fingerprint-grant");
        var activationId = await GetActivationIdAsync(activation.DeviceToken);

        var pro = await AdminPostAsync($"/api/admin/devices/{activationId:D}/pro");
        var addOn = await AdminPostAsync($"/api/admin/devices/{activationId:D}/add-on");

        Assert.Equal(HttpStatusCode.NoContent, pro.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, addOn.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var buckets = await db.QuotaBuckets.OrderBy(value => value.Kind).ToListAsync();
        Assert.Contains(buckets, value => value.Kind == QuotaBucketKind.ProMonthly && value.RemainingSeconds == 36_000 && value.ExpiresAt is not null);
        Assert.Contains(buckets, value => value.Kind == QuotaBucketKind.AddOn && value.RemainingSeconds == 36_000 && value.ExpiresAt is null);
        var auditEntries = await db.AdminAuditLogs.OrderBy(value => value.Action).ToListAsync();
        Assert.Contains(auditEntries, value => value.Action == "entitlement.grant_pro" && value.Metadata == "{\"days\":30,\"seconds\":36000}");
        Assert.Contains(auditEntries, value => value.Action == "quota.grant_add_on" && value.Metadata == "{\"seconds\":36000}");
        Assert.DoesNotContain(auditEntries, value => value.Metadata.Contains(inviteCode, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdministratorEndpointsRejectRequestsWithoutServerSideCredential()
    {
        var response = await factory.CreateClient().PostAsync("/api/admin/invites", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CloudSessionSettlementUsesMonthlyQuotaBeforeAddOnAndIsIdempotent()
    {
        var activationId = await CreateActivationWithBucketsAsync(10, 590);
        await using var scope = factory.Services.CreateAsyncScope();
        var quota = scope.ServiceProvider.GetRequiredService<QuotaSessionService>();
        var session = await quota.ReserveAsync(activationId, "wss://service.test/api/asr/stream", CancellationToken.None);
        await quota.RecordPcmAsync(session.SessionId, activationId, 32_000 * 600, CancellationToken.None);

        var finished = await quota.FinishAsync(session.SessionId, activationId, succeeded: true, CancellationToken.None);
        var repeated = await quota.FinishAsync(session.SessionId, activationId, succeeded: true, CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var buckets = await db.QuotaBuckets.Where(value => value.ActivationId == activationId).ToListAsync();

        Assert.Equal(600, finished.ChargedSeconds);
        Assert.Equal(finished, repeated);
        Assert.Contains(buckets, value => value.Kind == QuotaBucketKind.ProMonthly && value.RemainingSeconds == 0 && value.ReservedSeconds == 0);
        Assert.Contains(buckets, value => value.Kind == QuotaBucketKind.AddOn && value.RemainingSeconds == 0 && value.ReservedSeconds == 0);
        Assert.Single(await db.UsageLedger.Where(value => value.SessionId == session.SessionId.ToString("D")).ToListAsync());
    }

    [Fact]
    public async Task FailedCloudSessionReleasesQuotaAndInsufficientReservationIsRejected()
    {
        var activationId = await CreateActivationWithBucketsAsync(600, 0);
        await using var scope = factory.Services.CreateAsyncScope();
        var quota = scope.ServiceProvider.GetRequiredService<QuotaSessionService>();
        var session = await quota.ReserveAsync(activationId, "wss://service.test/api/asr/stream", CancellationToken.None);
        await quota.RecordPcmAsync(session.SessionId, activationId, 32_000 * 20, CancellationToken.None);
        var released = await quota.FinishAsync(session.SessionId, activationId, succeeded: false, CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();

        Assert.Equal(0, released.ChargedSeconds);
        Assert.Equal(600, await db.QuotaBuckets.Where(value => value.ActivationId == activationId).Select(value => value.RemainingSeconds).SingleAsync());
        Assert.Equal(0, await db.QuotaBuckets.Where(value => value.ActivationId == activationId).Select(value => value.ReservedSeconds).SingleAsync());
        await Assert.ThrowsAsync<QuotaUnavailableException>(() => quota.ReserveAsync(Guid.NewGuid(), "wss://service.test/api/asr/stream", CancellationToken.None));
    }

    [Fact]
    public async Task OrderUsesServerProductPriceAndRejectsUnknownProduct()
    {
        var activation = await ActivateAsync(await CreateInviteAsync(), $"payment-price-{Guid.NewGuid():N}");
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new { productId = "pro_month", amountFen = 1 })
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", activation.DeviceToken);

        var response = await client.SendAsync(request);
        var order = await response.Content.ReadFromJsonAsync<CreateOrderResponse>(JsonOptions);
        var unknown = await AuthenticatedPostAsync(activation.DeviceToken, "/api/orders", new CreateOrderRequest("not-a-product"));

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(2000, Assert.IsType<CreateOrderResponse>(order).AmountFen);
        Assert.Contains("https://openapi.alipaydev.com/gateway.do", order.CheckoutUrl, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task RepeatedVerifiedSuccessNotificationGrantsProExactlyOnce()
    {
        var activation = await ActivateAsync(await CreateInviteAsync(), $"payment-idempotent-{Guid.NewGuid():N}");
        var order = await CreateOrderAsync(activation.DeviceToken, "pro_month");
        var form = factory.CreateSignedNotification(order, "TRADE_SUCCESS", $"notify-{Guid.NewGuid():N}");

        var first = await PostAlipayNotificationAsync(form);
        var repeated = await PostAlipayNotificationAsync(form);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var notification = await db.PaymentNotifications.SingleAsync(value => value.ProviderNotificationId == form["notify_id"]);
        Assert.True(string.Equals("success", await first.Content.ReadAsStringAsync(), StringComparison.Ordinal), notification.ProcessingResult);
        Assert.Equal("success", await repeated.Content.ReadAsStringAsync());
        var storedOrder = await db.Orders.SingleAsync(value => value.OrderNo == order.OrderNo);
        Assert.Equal(OrderStatus.Paid, storedOrder.Status);
        Assert.Single(await db.Entitlements.Where(value => value.ActivationId == storedOrder.ActivationId && value.Plan == EntitlementPlan.Pro).ToListAsync());
        Assert.Single(await db.QuotaBuckets.Where(value => value.ActivationId == storedOrder.ActivationId && value.Kind == QuotaBucketKind.ProMonthly).ToListAsync());
        Assert.Single(await db.PaymentNotifications.Where(value => value.ProviderNotificationId == form["notify_id"]).ToListAsync());
    }

    [Fact]
    public async Task InvalidSignatureAmountMismatchAndUnknownOrderNeverGrantEntitlement()
    {
        var activation = await ActivateAsync(await CreateInviteAsync(), $"payment-invalid-{Guid.NewGuid():N}");
        var order = await CreateOrderAsync(activation.DeviceToken, "pro_month");
        var forged = factory.CreateSignedNotification(order, "TRADE_SUCCESS", $"notify-{Guid.NewGuid():N}");
        forged["sign"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(256));
        var amountMismatch = factory.CreateSignedNotification(order with { AmountFen = 1 }, "TRADE_SUCCESS", $"notify-{Guid.NewGuid():N}");
        var unknownOrder = factory.CreateSignedNotification(order with { OrderNo = "ZM-unknown-order" }, "TRADE_SUCCESS", $"notify-{Guid.NewGuid():N}");

        Assert.Equal("failure", await (await PostAlipayNotificationAsync(forged)).Content.ReadAsStringAsync());
        Assert.Equal("failure", await (await PostAlipayNotificationAsync(amountMismatch)).Content.ReadAsStringAsync());
        Assert.Equal("failure", await (await PostAlipayNotificationAsync(unknownOrder)).Content.ReadAsStringAsync());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var storedOrder = await db.Orders.SingleAsync(value => value.OrderNo == order.OrderNo);
        Assert.Equal(OrderStatus.PendingPayment, storedOrder.Status);
        Assert.Empty(await db.Entitlements.Where(value => value.ActivationId == storedOrder.ActivationId && value.Plan == EntitlementPlan.Pro).ToListAsync());
    }

    [Fact]
    public async Task ClosedOrderDoesNotGrantLatePaymentAndVerifiedRefundKeepsGrantedQuota()
    {
        var closedActivation = await ActivateAsync(await CreateInviteAsync(), $"payment-closed-{Guid.NewGuid():N}");
        var closedOrder = await CreateOrderAsync(closedActivation.DeviceToken, "add_on_10h");
        var close = await AuthenticatedPostAsync(closedActivation.DeviceToken, $"/api/orders/{closedOrder.OrderNo}/close", null);
        var lateNotify = factory.CreateSignedNotification(closedOrder, "TRADE_SUCCESS", $"notify-{Guid.NewGuid():N}");
        var late = await PostAlipayNotificationAsync(lateNotify);

        var refundActivation = await ActivateAsync(await CreateInviteAsync(), $"payment-refund-{Guid.NewGuid():N}");
        var refundOrder = await CreateOrderAsync(refundActivation.DeviceToken, "add_on_10h");
        await PostAlipayNotificationAsync(factory.CreateSignedNotification(refundOrder, "TRADE_SUCCESS", $"notify-{Guid.NewGuid():N}"));
        var refund = await AdminPostAsync($"/api/admin/orders/{refundOrder.OrderNo}/refund");

        Assert.Equal(HttpStatusCode.NoContent, close.StatusCode);
        Assert.Equal("success", await late.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, refund.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        Assert.Equal(OrderStatus.Closed, (await db.Orders.SingleAsync(value => value.OrderNo == closedOrder.OrderNo)).Status);
        var refunded = await db.Orders.SingleAsync(value => value.OrderNo == refundOrder.OrderNo);
        Assert.Equal(OrderStatus.Refunded, refunded.Status);
        Assert.Single(await db.QuotaBuckets.Where(value => value.ActivationId == refunded.ActivationId && value.Kind == QuotaBucketKind.AddOn).ToListAsync());
    }

    [Fact]
    public async Task RefundWithUnverifiedGatewayResponseKeepsOrderPaid()
    {
        var activation = await ActivateAsync(await CreateInviteAsync(), $"payment-refund-signature-{Guid.NewGuid():N}");
        var order = await CreateOrderAsync(activation.DeviceToken, "pro_month");
        await PostAlipayNotificationAsync(factory.CreateSignedNotification(order, "TRADE_SUCCESS", $"notify-{Guid.NewGuid():N}"));
        var gateway = factory.Services.GetRequiredService<IAlipayGatewayClient>() as FakeAlipayGatewayClient;
        Assert.NotNull(gateway);
        gateway.NextRefundResult = new AlipayRefundResult(false, false, null, "INVALID_SIGNATURE", "Invalid signature");

        var refund = await AdminPostAsync($"/api/admin/orders/{order.OrderNo}/refund");

        Assert.Equal(HttpStatusCode.Conflict, refund.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        Assert.Equal(OrderStatus.Paid, (await db.Orders.SingleAsync(value => value.OrderNo == order.OrderNo)).Status);
    }

    [Fact]
    public void AlipayCheckoutUrlUsesRsa2AndServerAmount()
    {
        using var merchantKey = RSA.Create(2048);
        using var alipayKey = RSA.Create(2048);
        var options = Options.Create(new AlipayOptions
        {
            AppId = "checkout-test-app",
            SellerId = "checkout-test-seller",
            NotifyUrl = "https://service.test/api/payments/alipay/notify",
            ReturnUrl = "https://service.test/payments/alipay/return",
            MerchantPrivateKeyPem = merchantKey.ExportPkcs8PrivateKeyPem(),
            AlipayPublicKeyPem = alipayKey.ExportSubjectPublicKeyInfoPem()
        });
        var client = new AlipayGatewayClient(new HttpClient(), options);

        var url = client.BuildCheckoutUrl("ZM-test-order", "pro_month", 2000);
        var query = QueryHelpers.ParseQuery(new Uri(url).Query)
            .ToDictionary(value => value.Key, value => value.Value.ToString(), StringComparer.Ordinal);

        Assert.Equal("RSA2", query["sign_type"]);
        Assert.True(AlipayRsa2.Verify(query, query["sign"], merchantKey.ExportSubjectPublicKeyInfoPem()));
        using var business = JsonDocument.Parse(query["biz_content"]);
        Assert.Equal("20.00", business.RootElement.GetProperty("total_amount").GetString());
        Assert.Equal("ZM-test-order", business.RootElement.GetProperty("out_trade_no").GetString());
    }

    private async Task<string> CreateInviteAsync()
    {
        var response = await AdminPostAsync("/api/admin/invites");
        response.EnsureSuccessStatusCode();
        var invite = await response.Content.ReadFromJsonAsync<CreateInviteResponse>(JsonOptions);
        return Assert.IsType<CreateInviteResponse>(invite).InviteCode;
    }

    private async Task<ActivationResponse> ActivateAsync(string inviteCode, string fingerprint)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/activation", new ActivationRequest(inviteCode, fingerprint));
        response.EnsureSuccessStatusCode();
        return Assert.IsType<ActivationResponse>(await response.Content.ReadFromJsonAsync<ActivationResponse>(JsonOptions));
    }

    private async Task<Guid> GetActivationIdAsync(string deviceToken)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var tokenHash = SecretHasher.Hash(deviceToken);
        return await db.DeviceActivations.Where(value => value.TokenHash == tokenHash).Select(value => value.Id).SingleAsync();
    }

    private async Task<Guid> CreateActivationWithBucketsAsync(int monthlySeconds, int addOnSeconds)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var activation = new DeviceActivation
        {
            DeviceFingerprintHash = Guid.NewGuid().ToString("N"),
            TokenHash = Guid.NewGuid().ToString("N"),
            InviteCodeId = (await db.InviteCodes.Select(value => value.Id).FirstOrDefaultAsync())
        };
        if (activation.InviteCodeId == Guid.Empty)
        {
            var invite = new InviteCode { CodeHash = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow };
            db.InviteCodes.Add(invite);
            activation.InviteCodeId = invite.Id;
        }

        db.DeviceActivations.Add(activation);
        if (monthlySeconds > 0)
        {
            db.QuotaBuckets.Add(new QuotaBucket { ActivationId = activation.Id, Kind = QuotaBucketKind.ProMonthly, RemainingSeconds = monthlySeconds, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1), CreatedAt = DateTimeOffset.UtcNow });
        }

        if (addOnSeconds > 0)
        {
            db.QuotaBuckets.Add(new QuotaBucket { ActivationId = activation.Id, Kind = QuotaBucketKind.AddOn, RemainingSeconds = addOnSeconds, CreatedAt = DateTimeOffset.UtcNow });
        }

        await db.SaveChangesAsync();
        return activation.Id;
    }

    private async Task<HttpResponseMessage> AdminPostAsync(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Admin-Key", CommerceApiFactory.AdminKey);
        request.Headers.Add("X-Admin-Actor", "test-administrator");
        return await factory.CreateClient().SendAsync(request);
    }

    private async Task<CreateOrderResponse> CreateOrderAsync(string deviceToken, string productId)
    {
        var response = await AuthenticatedPostAsync(deviceToken, "/api/orders", new CreateOrderRequest(productId));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return Assert.IsType<CreateOrderResponse>(await response.Content.ReadFromJsonAsync<CreateOrderResponse>(JsonOptions));
    }

    private async Task<HttpResponseMessage> AuthenticatedPostAsync(string deviceToken, string path, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", deviceToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await factory.CreateClient().SendAsync(request);
    }

    private Task<HttpResponseMessage> PostAlipayNotificationAsync(Dictionary<string, string> form) =>
        factory.CreateClient().PostAsync("/api/payments/alipay/notify", new FormUrlEncodedContent(form));

    private async Task<HttpResponseMessage> AuthenticatedGetAsync(string deviceToken, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", deviceToken);
        return await factory.CreateClient().SendAsync(request);
    }

    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);
}

public sealed class CommerceApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminKey = "test-admin-key-not-a-production-secret";
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly RSA alipaySigningKey = RSA.Create(2048);
    private readonly RSA merchantSigningKey = RSA.Create(2048);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Service:AdminApiKey"] = AdminKey,
                ["ConnectionStrings:Zumingtalk"] = "Host=not-used-by-tests",
                ["Alipay:AppId"] = "test-alipay-app",
                ["Alipay:SellerId"] = "test-seller",
                ["Alipay:NotifyUrl"] = "https://service.test/api/payments/alipay/notify",
                ["Alipay:ReturnUrl"] = "https://service.test/payments/alipay/return",
                ["Alipay:MerchantPrivateKeyPem"] = merchantSigningKey.ExportPkcs8PrivateKeyPem(),
                ["Alipay:AlipayPublicKeyPem"] = alipaySigningKey.ExportSubjectPublicKeyInfoPem(),
                ["Alipay:ProMonthAmountFen"] = "2000",
                ["Alipay:AddOnAmountFen"] = "2000"
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ServiceDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ServiceDbContext>>();
            services.RemoveAll<IAlipayGatewayClient>();
            services.AddDbContext<ServiceDbContext>(options => options.UseSqlite(connection));
            services.AddSingleton<IAlipayGatewayClient, FakeAlipayGatewayClient>();
        });
    }

    public Dictionary<string, string> CreateSignedNotification(CreateOrderResponse order, string tradeStatus, string notificationId)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app_id"] = "test-alipay-app",
            ["seller_id"] = "test-seller",
            ["notify_id"] = notificationId,
            ["out_trade_no"] = order.OrderNo,
            ["trade_no"] = $"trade-{Guid.NewGuid():N}",
            ["trade_status"] = tradeStatus,
            ["total_amount"] = (order.AmountFen / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            ["sign_type"] = "RSA2"
        };
        values["sign"] = AlipayRsa2.Sign(values, alipaySigningKey.ExportPkcs8PrivateKeyPem());
        return values;
    }

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await connection.DisposeAsync();
        alipaySigningKey.Dispose();
        merchantSigningKey.Dispose();
        Dispose();
    }
}

public sealed class FakeAlipayGatewayClient : IAlipayGatewayClient
{
    public AlipayRefundResult NextRefundResult { get; set; } = new(true, true, "test-trade", "10000", "Success");

    public string BuildCheckoutUrl(string orderNo, string productId, int amountFen) =>
        $"https://openapi.alipaydev.com/gateway.do?out_trade_no={Uri.EscapeDataString(orderNo)}";

    public Task<AlipayRefundResult> RefundAsync(string orderNo, string refundRequestNo, int amountFen, CancellationToken cancellationToken)
    {
        var result = NextRefundResult with { TradeNo = NextRefundResult.TradeNo ?? $"trade-{orderNo}" };
        NextRefundResult = new AlipayRefundResult(true, true, "test-trade", "10000", "Success");
        return Task.FromResult(result);
    }
}
