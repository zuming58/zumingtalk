param(
    [Parameter(Mandatory = $true)]
    [string]$ServiceBaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$AdminApiKey,

    [Parameter(Mandatory = $true)]
    [DateTimeOffset]$From,

    [Parameter(Mandatory = $true)]
    [DateTimeOffset]$To,

    [int]$BailianBillFen = 0,
    [int]$RelayCostFen = 0,
    [int]$DatabaseAndLoggingCostFen = 0,
    [int]$PaymentFeeFen = 0,
    [int]$SupportCostFen = 0,
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$baseUrl = $ServiceBaseUrl.TrimEnd("/")
$query = "from=$([Uri]::EscapeDataString($From.ToUniversalTime().ToString('O')))&to=$([Uri]::EscapeDataString($To.ToUniversalTime().ToString('O')))"
$summary = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/admin/cost/summary?$query" -Headers @{
    "X-Admin-Key" = $AdminApiKey
    "X-Admin-Actor" = "cost-benchmark-script"
}

$totalCostFen = $BailianBillFen + $RelayCostFen + $DatabaseAndLoggingCostFen + $PaymentFeeFen + $SupportCostFen
$hours = [double]$summary.receivedAudioSeconds / 3600.0
$complete = $summary.receivedAudioSeconds -ge 36000
$result = [ordered]@{
    generatedAt = [DateTimeOffset]::UtcNow.ToString("O")
    complete = $complete
    requiredAudioSeconds = 36000
    serviceSummary = $summary
    costsFen = [ordered]@{
        bailianBill = $BailianBillFen
        relay = $RelayCostFen
        databaseAndLogging = $DatabaseAndLoggingCostFen
        payment = $PaymentFeeFen
        support = $SupportCostFen
        total = $totalCostFen
    }
    costPerAudioHourFen = if ($hours -gt 0) { [Math]::Round($totalCostFen / $hours, 2) } else { $null }
    formalPriceMayBeConfirmed = $complete
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $OutputPath = Join-Path $repoRoot "artifacts/cost/cost-benchmark-$([DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')).json"
}

$directory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$result | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -Path $OutputPath
Write-Output "Benchmark complete: $complete"
Write-Output "Received audio seconds: $($summary.receivedAudioSeconds) / 36000"
Write-Output "Report: $OutputPath"

if (-not $complete) {
    exit 2
}
