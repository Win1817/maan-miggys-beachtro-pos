<#
  Builds the Beachtro POS installer (IB-27, POS-DEC-011).
  Output in dist\: BeachtroPOS-<version>-win-x64.msi, its .sha256, and release.json.
  Usage (Windows PowerShell or pwsh, from any folder):  powershell -ExecutionPolicy Bypass -File build\package.ps1
#>
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Version not found in Directory.Build.props' }

$publish = Join-Path $root 'artifacts\publish'
$msiOut = Join-Path $root 'artifacts\installer'
$dist = Join-Path $root 'dist'
foreach ($dir in $publish, $msiOut) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }
New-Item -ItemType Directory -Force $dist | Out-Null

# 1. Self-contained app (no .NET install needed on the shop PC). The project already targets win-x64.
dotnet publish (Join-Path $root 'src\Beachtro.Desktop\Beachtro.Desktop.csproj') -c $Configuration -p:SelfContained=true -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
if (-not (Test-Path (Join-Path $publish 'BeachtroPOS.exe'))) { throw 'BeachtroPOS.exe missing from publish output' }

# 2. MSI from the publish folder.
dotnet build (Join-Path $root 'installer\Beachtro.Installer.wixproj') -c $Configuration -p:ProductVersion=$version -p:PublishDir="$publish\" -o $msiOut --nologo
if ($LASTEXITCODE -ne 0) { throw 'installer build failed' }

# 3. Name, checksum and release record.
$name = "BeachtroPOS-$version-win-x64.msi"
$msi = Join-Path $dist $name
Copy-Item (Get-ChildItem $msiOut -Filter *.msi | Select-Object -First 1).FullName $msi -Force
$hash = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$msi.sha256" -Value "$hash  $name" -Encoding ascii
$latestMigration = (Get-ChildItem (Join-Path $root 'src\Beachtro.Infrastructure\Persistence\Migrations') -Filter '2*.cs' |
    Where-Object { $_.Name -notlike '*.Designer.cs' } | Sort-Object Name | Select-Object -Last 1).BaseName
$commit = (git -C $root rev-parse --short HEAD 2>$null)
[ordered]@{
    product = 'Beachtro POS'
    version = $version
    file = $name
    sha256 = $hash
    sizeBytes = (Get-Item $msi).Length
    latestDatabaseMigration = $latestMigration
    sourceCommit = $commit
    builtUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    runtime = 'self-contained .NET 10 win-x64'
    profile = 'Test only (production activation disabled in this build)'
} | ConvertTo-Json | Set-Content -Path (Join-Path $dist 'release.json') -Encoding utf8

Write-Host "Built $msi"
Write-Host "SHA-256 $hash"
