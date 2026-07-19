param(
    [Parameter(Mandatory = $true)]
    [string]$ServiceBaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$DeviceToken,

    [ValidateSet("pro_month", "add_on_10h")]
    [string]$ProductId = "pro_month",

    [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = "Stop"
$baseUrl = $ServiceBaseUrl.TrimEnd("/")
$headers = @{ Authorization = "Bearer $DeviceToken" }
$order = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/orders" -Headers $headers -ContentType "application/json" -Body (@{ productId = $ProductId } | ConvertTo-Json)

Write-Host "Order: $($order.orderNo)"
Write-Host "Product: $($order.productId)"
Write-Host "Server amount: $($order.amountFen) fen"
Write-Host "Expires: $($order.expiresAt)"
Start-Process $order.checkoutUrl

$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
do {
    Start-Sleep -Seconds 3
    $status = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/orders/$($order.orderNo)" -Headers $headers
    Write-Host "Status: $($status.status)"
    if ($status.status -in @("Paid", "Closed", "Refunded")) {
        return $status
    }
} while ([DateTimeOffset]::UtcNow -lt $deadline)

throw "Timed out waiting for the server-side Alipay order state."
