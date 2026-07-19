using System.Net.Http.Headers;
using System.Net.Http.Json;
using Zumingtalk.Domain.Services;
using Zumingtalk.Domain.Settings;

namespace Zumingtalk.Infrastructure.Asr;

public sealed class ZumingtalkCloudAccountClient(ISettingsRepository settingsRepository, IDeviceFingerprintProvider deviceFingerprintProvider) : ICloudAccountClient
{
    public async Task ActivateAsync(string serviceBaseUrl, string inviteCode, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(serviceBaseUrl, UriKind.Absolute, out var serviceUri) || serviceUri.Scheme is not ("https" or "http") || string.IsNullOrWhiteSpace(inviteCode))
        {
            throw new InvalidOperationException("请填写有效服务地址和邀请码。");
        }

        var localFingerprint = await deviceFingerprintProvider.GetOrCreateAsync(cancellationToken);
        using var client = new HttpClient { BaseAddress = serviceUri };
        using var response = await client.PostAsJsonAsync("/api/activation", new { inviteCode, deviceFingerprint = localFingerprint }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(response.StatusCode == System.Net.HttpStatusCode.Conflict ? "邀请码不可用，或此设备已激活。" : "设备激活失败。");
        }

        var activated = await response.Content.ReadFromJsonAsync<ActivationResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("激活响应无效。");
        await settingsRepository.SaveZumingtalkCloudCredentialsAsync(new ZumingtalkCloudCredentialSettings(serviceUri.ToString().TrimEnd('/'), activated.DeviceToken), cancellationToken);
    }

    public async Task<CloudEntitlementSnapshot> GetEntitlementAsync(CancellationToken cancellationToken)
    {
        using var client = await CreateAuthenticatedClientAsync(cancellationToken);
        using var response = await client.GetAsync("/api/me/entitlement", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("无法读取祖名云端权益。");
        }

        var entitlement = await response.Content.ReadFromJsonAsync<EntitlementResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("权益响应无效。");
        return new CloudEntitlementSnapshot(entitlement.Plan, entitlement.ServerTime, entitlement.QuotaBuckets.Select(value => new CloudQuotaBucket(value.Kind, value.RemainingSeconds, value.ExpiresAt)).ToList());
    }

    public async Task<CloudOrderSnapshot> CreateOrderAsync(string productId, CancellationToken cancellationToken)
    {
        using var client = await CreateAuthenticatedClientAsync(cancellationToken);
        using var response = await client.PostAsJsonAsync("/api/orders", new { productId }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("无法创建支付宝订单，请检查服务配置和设备权益。");
        }

        var order = await response.Content.ReadFromJsonAsync<CreateOrderResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("订单响应无效。");
        return new CloudOrderSnapshot(order.OrderNo, order.ProductId, order.AmountFen, "PendingPayment", order.CheckoutUrl, order.ExpiresAt);
    }

    public async Task<CloudOrderSnapshot> GetOrderAsync(string orderNo, CancellationToken cancellationToken)
    {
        using var client = await CreateAuthenticatedClientAsync(cancellationToken);
        using var response = await client.GetAsync($"/api/orders/{Uri.EscapeDataString(orderNo)}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("无法读取订单状态。");
        }

        var order = await response.Content.ReadFromJsonAsync<OrderStatusResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("订单状态响应无效。");
        return new CloudOrderSnapshot(order.OrderNo, order.ProductId, order.AmountFen, order.Status, null, order.ExpiresAt);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(CancellationToken cancellationToken)
    {
        var credentials = await settingsRepository.GetZumingtalkCloudCredentialsAsync(cancellationToken);
        if (!Uri.TryCreate(credentials.ServiceBaseUrl, UriKind.Absolute, out var serviceUri) || string.IsNullOrWhiteSpace(credentials.DeviceToken))
        {
            throw new InvalidOperationException("此设备尚未激活祖名云端。");
        }

        var client = new HttpClient { BaseAddress = serviceUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials.DeviceToken);
        return client;
    }

    private sealed record ActivationResponse(string DeviceToken, DateTimeOffset ServerTime);
    private sealed record EntitlementResponse(string Plan, DateTimeOffset ServerTime, IReadOnlyList<QuotaBucketResponse> QuotaBuckets);
    private sealed record QuotaBucketResponse(string Kind, int RemainingSeconds, DateTimeOffset? ExpiresAt);
    private sealed record CreateOrderResponse(string OrderNo, string ProductId, int AmountFen, string CheckoutUrl, DateTimeOffset ExpiresAt);
    private sealed record OrderStatusResponse(string OrderNo, string ProductId, int AmountFen, string Status, DateTimeOffset ExpiresAt);
}
