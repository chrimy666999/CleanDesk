param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$distRoot = Join-Path $repoRoot "dist"
$publishDir = Join-Path $distRoot "CleanDesk-portable"
$zipPath = Join-Path $distRoot "CleanDesk-portable-win-x64.zip"
$project = Join-Path $repoRoot "src\CleanDesk.App\CleanDesk.App.csproj"
$icon = Join-Path $repoRoot "src\CleanDesk.App\Assets\CleanDesk.ico"

if (-not (Test-Path $icon)) {
    throw "Missing application icon: $icon. Run: python scripts\generate_icon.py"
}

if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force -LiteralPath $publishDir
}

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

dotnet publish $project -c $Configuration -r $Runtime --self-contained true -o $publishDir /p:PublishSingleFile=false /p:PublishTrimmed=false

Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination (Join-Path $publishDir "README.md") -Force

$data = Join-Path $publishDir "portable-data"
New-Item -ItemType Directory -Force -Path (Join-Path $data "backups"), (Join-Path $data "logs"), (Join-Path $data "cache") | Out-Null

if (Test-Path $zipPath) {
    Remove-Item -Force -LiteralPath $zipPath
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

Write-Host "EXE: $publishDir\CleanDesk.exe"
Write-Host "ZIP: $zipPath"
