param(
    [string]$Configuration = "Release",
    [string]$Version = "0.7.0",
    [string]$OutputDirectory = "artifacts/publish/v0.7.0-win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src/Zumingtalk.App/Zumingtalk.App.csproj"
$output = Join-Path $repoRoot $OutputDirectory
$zip = Join-Path $repoRoot "artifacts/publish/Zumingtalk-v$Version-win-x64.zip"

if ((Test-Path $output) -and (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1)) {
    throw "Publish output is not empty. Use a new OutputDirectory so stale files cannot enter the package: $output"
}
if (Test-Path $zip) {
    throw "Release archive already exists. Use a new Version or remove that one explicit file manually: $zip"
}
New-Item -ItemType Directory -Force -Path $output | Out-Null

dotnet publish $project `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $output

$exe = Join-Path $output "Zumingtalk.App.exe"
if (-not (Test-Path $exe)) {
    throw "Publish did not produce $exe"
}

$hash = Get-FileHash -Algorithm SHA256 -Path $exe
Compress-Archive -Path (Join-Path $output "*") -DestinationPath $zip -CompressionLevel Optimal
$zipHash = Get-FileHash -Algorithm SHA256 -Path $zip
$hashLines = @(
    "Zumingtalk.App.exe SHA256 $($hash.Hash)",
    "$(Split-Path -Leaf $zip) SHA256 $($zipHash.Hash)"
)
$hashLines | Set-Content -Encoding UTF8 -Path (Join-Path $output "SHA256SUMS.txt")
$hashLines | ForEach-Object { Write-Output $_ }
