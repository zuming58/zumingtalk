using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Zumingtalk.Service.Payments;

public sealed class AlipayGatewayClient(HttpClient httpClient, IOptions<AlipayOptions> options) : IAlipayGatewayClient
{
    private readonly AlipayOptions options = options.Value;

    public string BuildCheckoutUrl(string orderNo, string productId, int amountFen)
    {
        options.Validate();
        var parameters = CreateCommonParameters("alipay.trade.page.pay");
        parameters["notify_url"] = options.NotifyUrl;
        parameters["return_url"] = options.ReturnUrl;
        parameters["biz_content"] = JsonSerializer.Serialize(new
        {
            out_trade_no = orderNo,
            product_code = "FAST_INSTANT_TRADE_PAY",
            total_amount = FormatAmount(amountFen),
            subject = ProductSubject(productId),
            timeout_express = "30m"
        });
        parameters["sign"] = AlipayRsa2.Sign(parameters, options.GetMerchantPrivateKey());
        return $"{options.Gateway}?{BuildForm(parameters)}";
    }

    public async Task<AlipayRefundResult> RefundAsync(string orderNo, string refundRequestNo, int amountFen, CancellationToken cancellationToken)
    {
        options.Validate();
        var parameters = CreateCommonParameters("alipay.trade.refund");
        parameters["biz_content"] = JsonSerializer.Serialize(new
        {
            out_trade_no = orderNo,
            refund_amount = FormatAmount(amountFen),
            out_request_no = refundRequestNo,
            refund_reason = "Zumingtalk sandbox refund"
        });
        parameters["sign"] = AlipayRsa2.Sign(parameters, options.GetMerchantPrivateKey());

        using var content = new FormUrlEncodedContent(parameters);
        using var response = await httpClient.PostAsync(options.Gateway, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("alipay_trade_refund_response", out var payload) ||
            !document.RootElement.TryGetProperty("sign", out var signatureElement))
        {
            return new AlipayRefundResult(false, false, null, "INVALID_RESPONSE", "Alipay refund response is incomplete.");
        }

        var signature = signatureElement.GetString() ?? string.Empty;
        var signatureVerified = AlipayRsa2.VerifyContent(payload.GetRawText(), signature, options.GetAlipayPublicKey());
        var code = payload.TryGetProperty("code", out var codeElement) ? codeElement.GetString() ?? string.Empty : string.Empty;
        var message = payload.TryGetProperty("msg", out var messageElement) ? messageElement.GetString() ?? string.Empty : string.Empty;
        var tradeNo = payload.TryGetProperty("trade_no", out var tradeElement) ? tradeElement.GetString() : null;
        var fundChange = payload.TryGetProperty("fund_change", out var fundElement) ? fundElement.GetString() : null;
        return new AlipayRefundResult(signatureVerified && code == "10000" && fundChange == "Y", signatureVerified, tradeNo, code, message);
    }

    private Dictionary<string, string> CreateCommonParameters(string method) => new(StringComparer.Ordinal)
    {
        ["app_id"] = options.AppId,
        ["method"] = method,
        ["format"] = "JSON",
        ["charset"] = "utf-8",
        ["sign_type"] = "RSA2",
        ["timestamp"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        ["version"] = "1.0"
    };

    private static string BuildForm(IEnumerable<KeyValuePair<string, string>> values) =>
        string.Join("&", values.OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => $"{Uri.EscapeDataString(value.Key)}={Uri.EscapeDataString(value.Value)}"));

    private static string FormatAmount(int amountFen) => (amountFen / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    private static string ProductSubject(string productId) => productId switch
    {
        "pro_month" => "祖名闪电说 Pro 30 天",
        "add_on_10h" => "祖名闪电说云端识别 10 小时加量包",
        _ => throw new InvalidOperationException("Unknown product.")
    };
}
