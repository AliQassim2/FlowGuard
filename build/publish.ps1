<#
.SYNOPSIS
    Builds a FlowGuard release: runs the tests, publishes the single-file exe and packs a zip.

.DESCRIPTION
    Output (under artifacts\):
      FlowGuard-<version>-win-x64\        FlowGuard.exe + WinDivert.dll + WinDivert64.sys +
                                          WinDivertSharp.dll + README.md + THIRD-PARTY-NOTICES.md +
                                          licenses\ (LGPL-3.0, GPL-3.0, GPL-2.0 texts)
      FlowGuard-<version>-win-x64.zip     the same folder, zipped
    The SHA-256 of the zip is printed at the end.

.EXAMPLE
    .\build\publish.ps1
    .\build\publish.ps1 -Version 1.0.1 -SkipTests
#>
[CmdletBinding()]
param(
    # Defaults to <Version> in Directory.Build.props.
    [string]$Version,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
}

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') { throw "Invalid version '$Version'." }

function Invoke-Step([string]$Name, [scriptblock]$Command) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit code $LASTEXITCODE)." }
}

if (-not $SkipTests) {
    Invoke-Step 'Running tests' { dotnet test FlowGuard.sln -c Release --nologo }
}

$publishDir = Join-Path $root 'artifacts\publish\win-x64'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

Invoke-Step "Publishing FlowGuard $Version" {
    dotnet publish src\FlowGuard.UI\FlowGuard.UI.csproj -c Release -p:PublishProfile=win-x64 "-p:Version=$Version" --nologo
}

$expected = 'FlowGuard.exe', 'WinDivert.dll', 'WinDivert64.sys', 'WinDivertSharp.dll'
$actual = Get-ChildItem $publishDir -File | ForEach-Object Name
$missing = $expected | Where-Object { $_ -notin $actual }
$extra = $actual | Where-Object { $_ -notin $expected }
if ($missing) { throw "Publish output is missing: $($missing -join ', ')" }
if ($extra) { Write-Warning "Unexpected files in publish output (not packaged): $($extra -join ', ')" }

$name = "FlowGuard-$Version-win-x64"
$stage = Join-Path $root "artifacts\$name"
$zip = Join-Path $root "artifacts\$name.zip"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory $stage | Out-Null

foreach ($file in $expected) { Copy-Item (Join-Path $publishDir $file) $stage }
Copy-Item README.md, THIRD-PARTY-NOTICES.md $stage
# Full LGPL/GPL texts required alongside the WinDivert components.
Copy-Item licenses (Join-Path $stage 'licenses') -Recurse

Write-Host '==> Packing zip' -ForegroundColor Cyan
# Entries are written one by one with '/' separators: Windows PowerShell 5.1 (Compress-Archive and
# ZipFile.CreateFromDirectory alike) writes '\', which non-Windows unzip tools keep as part of the name.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $stage -File -Recurse) {
        $entry = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry,
            [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $archive.Dispose() }

$exe = Get-Item (Join-Path $stage 'FlowGuard.exe')
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Write-Host ''
Write-Host "FlowGuard $Version" -ForegroundColor Green
Write-Host ("  exe     {0}  ({1:N1} MB, file version {2})" -f $exe.FullName, ($exe.Length / 1MB), $exe.VersionInfo.FileVersion)
Write-Host ("  zip     {0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
Write-Host "  sha256  $hash"
