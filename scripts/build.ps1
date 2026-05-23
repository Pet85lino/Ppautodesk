<#
.SYNOPSIS
    Script de compilación para Civil3D Intelligent Connector.
    Compatible con Civil 3D 2025 / 2026 / 2027.

.PARAMETER Version
    Versión de Civil 3D a compilar: 2025, 2026, 2027 o All (default).

.PARAMETER Configuration
    Debug o Release (default: Release).

.PARAMETER InstallPath
    Ruta personalizada de instalación de Civil 3D.

.EXAMPLE
    .\build.ps1 -Version 2026 -Configuration Debug
    .\build.ps1 -Version All -Configuration Release
#>
param(
    [ValidateSet("2025","2026","2027","All")]
    [string]$Version = "All",

    [ValidateSet("Debug","Release")]
    [string]$Configuration = "Release",

    [string]$InstallPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot  = Split-Path $PSScriptRoot -Parent
$SlnPath   = Join-Path $RepoRoot "Civil3DConnector.sln"
$OutputDir = Join-Path $RepoRoot "dist"

function Write-Header($text) {
    Write-Host "`n" -NoNewline
    Write-Host ("=" * 60) -ForegroundColor Cyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host ("=" * 60) -ForegroundColor Cyan
}

function Build-ForVersion($year) {
    Write-Header "Compilando para Civil 3D $year ($Configuration)"

    $config = "${Configuration}_${year}"
    $args = @(
        "build", $SlnPath,
        "--configuration", $config,
        "--no-restore",
        "-p:Platform=x64"
    )

    if ($InstallPath -ne "") {
        $args += "-p:Civil3DInstallPath=$InstallPath"
    }

    Write-Host "  dotnet $($args -join ' ')" -ForegroundColor Gray
    & dotnet @args

    if ($LASTEXITCODE -ne 0) {
        Write-Host "  [ERROR] Falló la compilación para Civil 3D $year" -ForegroundColor Red
        return $false
    }

    $binDir = Join-Path $RepoRoot "src\Civil3DConnector\bin\${config}"
    $dllSrc = Join-Path $binDir "Civil3DConnector.dll"

    if (Test-Path $dllSrc) {
        $distDir = Join-Path $OutputDir "Civil3D_$year"
        New-Item -ItemType Directory -Force -Path $distDir | Out-Null
        Copy-Item "$binDir\*" $distDir -Recurse -Force

        # Copiar scripts Python y Dynamo
        Copy-Item (Join-Path $RepoRoot "src\Python\*") (Join-Path $distDir "Python") -Recurse -Force -ErrorAction SilentlyContinue
        Copy-Item (Join-Path $RepoRoot "src\DynamoScripts\*") (Join-Path $distDir "DynamoScripts") -Recurse -Force -ErrorAction SilentlyContinue
        Copy-Item (Join-Path $RepoRoot "src\Civil3DConnector\Standards\*") (Join-Path $distDir "Standards") -Recurse -Force -ErrorAction SilentlyContinue

        Write-Host "  [OK] Dist: $distDir" -ForegroundColor Green
        return $true
    } else {
        Write-Host "  [WARN] DLL no encontrada en $binDir" -ForegroundColor Yellow
        return $false
    }
}

# ── Restaurar paquetes NuGet ──────────────────────────────────────────────
Write-Header "Restaurando paquetes NuGet"
dotnet restore $SlnPath
if ($LASTEXITCODE -ne 0) { throw "NuGet restore falló" }

# ── Compilar ─────────────────────────────────────────────────────────────
$versions = if ($Version -eq "All") { @("2025","2026","2027") } else { @($Version) }
$results  = @{}

foreach ($v in $versions) {
    $results[$v] = Build-ForVersion $v
}

# ── Resumen ───────────────────────────────────────────────────────────────
Write-Header "Resumen de compilación"
foreach ($v in $versions) {
    $status = if ($results[$v]) { "[OK]  " } else { "[FAIL]" }
    $color  = if ($results[$v]) { "Green"  } else { "Red"   }
    Write-Host "  $status  Civil 3D $v - $Configuration" -ForegroundColor $color
}

$failed = $results.Values | Where-Object { $_ -eq $false }
if ($failed.Count -gt 0) {
    Write-Host "`n  Algunas compilaciones fallaron.`n" -ForegroundColor Red
    exit 1
} else {
    Write-Host "`n  Todas las compilaciones exitosas.`n" -ForegroundColor Green
    Write-Host "  Archivos de distribución: $OutputDir`n" -ForegroundColor Cyan
}
