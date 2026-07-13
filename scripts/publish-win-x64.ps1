param(
    [string]$Configuration = "Release",
    [string]$OutputDirectory = "artifacts/publish/win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src/Zumingtalk.App/Zumingtalk.App.csproj"
$output = Join-Path $repoRoot $OutputDirectory

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
$hashLine = "Zumingtalk.App.exe SHA256 $($hash.Hash)"
$hashLine | Set-Content -Encoding UTF8 -Path (Join-Path $output "SHA256SUMS.txt")
Write-Output $hashLine
