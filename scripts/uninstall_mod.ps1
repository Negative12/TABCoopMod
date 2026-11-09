# uninstall_mod.ps1
# Cleanly removes TABCoopMod files and restores the original TheyAreBillions.exe.config.

param(
    [string]$GameDir = "<SteamDir>\steamapps\common\They Are Billions"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$configPath = "$GameDir\TheyAreBillions.exe.config"
$backupPath = "$GameDir\TheyAreBillions.exe.config.bak"

if (Test-Path $backupPath) {
    Copy-Item $backupPath $configPath -Force
    Remove-Item $backupPath -Force
    Write-Host "Restored original TheyAreBillions.exe.config."
}

foreach ($f in @("TABCoopMod.dll", "TABCoopMod.pdb", "0Harmony.dll", "TABCoopMod.log")) {
    $p = Join-Path $GameDir $f
    if (Test-Path $p) {
        Remove-Item $p -Force
        Write-Host "Removed $f"
    }
}
Write-Host "TABCoopMod uninstalled cleanly."
