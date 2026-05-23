<#
.SYNOPSIS
    Instala Civil3D Intelligent Connector en Autodesk Civil 3D.
    Copia el DLL y registra el autoload en el registro de Windows.

.PARAMETER Version
    Versión de Civil 3D donde instalar: 2025, 2026 o 2027.

.PARAMETER Uninstall
    Si se especifica, desinstala el conector.

.EXAMPLE
    .\install.ps1 -Version 2026
    .\install.ps1 -Version 2025 -Uninstall
#>
param(
    [Parameter(Mandatory)]
    [ValidateSet("2025","2026","2027")]
    [string]$Version,

    [switch]$Uninstall
)

#Requires -RunAsAdministrator
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path $PSScriptRoot -Parent

# Mapeo de versión a internal version y clave de registro
$versionMap = @{
    "2025" = @{ AcadVersion = "R24.0"; RegKey = "ACAD-8001:409" }
    "2026" = @{ AcadVersion = "R25.0"; RegKey = "ACAD-8001:409" }
    "2027" = @{ AcadVersion = "R26.0"; RegKey = "ACAD-8001:409" }
}

$info   = $versionMap[$Version]
$regRoot = "HKCU:\Software\Autodesk\AutoCAD\$($info.AcadVersion)\$($info.RegKey)\Applications\Civil3DConnector"
$dllSrc  = Join-Path $RepoRoot "dist\Civil3D_$Version\Civil3DConnector.dll"
$installDir = "C:\Civil3DConnector\$Version"

if ($Uninstall) {
    Write-Host "Desinstalando Civil3D Connector de Civil 3D $Version..." -ForegroundColor Yellow

    if (Test-Path $regRoot) {
        Remove-Item $regRoot -Recurse -Force
        Write-Host "  [OK] Registro eliminado" -ForegroundColor Green
    }

    if (Test-Path $installDir) {
        Remove-Item $installDir -Recurse -Force
        Write-Host "  [OK] Archivos eliminados: $installDir" -ForegroundColor Green
    }

    Write-Host "  Desinstalación completa. Reinicie Civil 3D $Version." -ForegroundColor Green
    return
}

# ── Verificar DLL ──────────────────────────────────────────────────────────
if (-not (Test-Path $dllSrc)) {
    Write-Host "[ERROR] DLL no encontrada: $dllSrc" -ForegroundColor Red
    Write-Host "  Ejecute primero: .\build.ps1 -Version $Version" -ForegroundColor Yellow
    exit 1
}

# ── Copiar archivos ────────────────────────────────────────────────────────
Write-Host "Instalando Civil3D Connector para Civil 3D $Version..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $installDir | Out-Null

$distDir = Join-Path $RepoRoot "dist\Civil3D_$Version"
Copy-Item "$distDir\*" $installDir -Recurse -Force
Write-Host "  [OK] Archivos instalados: $installDir" -ForegroundColor Green

# ── Registro de autoload ───────────────────────────────────────────────────
$dllDest = Join-Path $installDir "Civil3DConnector.dll"

New-Item -Path $regRoot -Force | Out-Null
Set-ItemProperty -Path $regRoot -Name "DESCRIPTION" -Value "Civil 3D Intelligent Connector v2.0 - Ecuador"
Set-ItemProperty -Path $regRoot -Name "LOADCTRLS"   -Value 2 -Type DWord
Set-ItemProperty -Path $regRoot -Name "LOADER"      -Value $dllDest
Set-ItemProperty -Path $regRoot -Name "MANAGED"     -Value 1 -Type DWord

Write-Host "  [OK] Registro de autoload creado" -ForegroundColor Green
Write-Host "`n  Instalación completa. Inicie Civil 3D $Version y escriba CIVILAYUDA." -ForegroundColor Green
Write-Host "  Ruta DLL: $dllDest`n" -ForegroundColor Cyan
