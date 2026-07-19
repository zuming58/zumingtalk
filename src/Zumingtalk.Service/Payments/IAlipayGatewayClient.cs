namespace Zumingtalk.Service.Payments;

public interface IAlipayGatewayClient
{
    string BuildCheckoutUrl(string orderNo, string productId, int amountFen);

    Task<AlipayRefundResult> RefundAsync(string orderNo, string refundRequestNo, int amountFen, CancellationToken cancellationToken);
}

public sealed record AlipayRefundResult(bool Succeeded, bool SignatureVerified, string? TradeNo, string Code, string Message);
