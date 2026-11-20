param(
    [string]$GameDir = "",
    [string]$OutputDir = "",
    [int]$Resolution = 2048,
    [int]$BootTimeoutSec = 45
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_resolve_paths.ps1")
$GameDir = Resolve-TabGameDir $GameDir
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path (Resolve-TabRepoRoot) "reports"
}

function Invoke-Control {
    param([int]$Port, [string]$Cmd, [hashtable]$CmdArgs = @{})
    $payload = @{ id = 1; tool = $Cmd; args = $CmdArgs } | ConvertTo-Json -Compress -Depth 6
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.ReceiveTimeout = 120000
        $client.SendTimeout = 10000
        $client.Connect("127.0.0.1", $Port)
        $stream = $client.GetStream()
        $writer = New-Object System.IO.StreamWriter($stream, [System.Text.Encoding]::UTF8)
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
        $writer.WriteLine($payload)
        $writer.Flush()
        $line = $reader.ReadLine()
        if ([string]::IsNullOrEmpty($line)) {
            throw "Empty response from control port $Port for $Cmd"
        }
        $obj = $line | ConvertFrom-Json
        if (-not $obj.ok) {
            throw "Control command '$Cmd' on port $Port failed: $($obj.error)"
        }
        return $obj.result
    }
    finally {
        $client.Close()
    }
}

function Try-Control {
    param([int]$Port, [string]$Cmd, [hashtable]$CmdArgs = @{})
    try { return Invoke-Control $Port $Cmd $CmdArgs } catch { return $null }
}

$panoDir = Join-Path $OutputDir "panoramas_mf"
New-Item -ItemType Directory -Force -Path $panoDir | Out-Null

Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$logFile = Join-Path $GameDir "TABCoopMod.pano_mf.log"
if (Test-Path $logFile) { Remove-Item $logFile -Force -ErrorAction SilentlyContinue }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = (Join-Path $GameDir "TheyAreBillions.exe")
$psi.WorkingDirectory = $GameDir
$psi.UseShellExecute = $false
$psi.EnvironmentVariables["TABCOOP_CONTROL_PORT"] = "27901"
$psi.EnvironmentVariables["TABCOOP_AUTO_SHOT"] = "0"
$psi.EnvironmentVariables["TABCOOP_ROLE"] = "pano_mf"
$psi.EnvironmentVariables["TABCOOP_LOG_NAME"] = "TABCoopMod.pano_mf.log"
$psi.EnvironmentVariables.Remove("TABCOOP_TEST")

$themes = @(
    @{ theme = "BR"; cnName = "暗黑荒原 (BR)"; enName = "The Dark Moorland"; seeds = @(202611, 202612) },
    @{ theme = "AL"; cnName = "宁静低地 / 林间 (AL)"; enName = "The Peaceful Lowlands"; seeds = @(202631, 202632) },
    @{ theme = "TM"; cnName = "荒芜旷野 / 冻地 (TM)"; enName = "The Frozen Highlands"; seeds = @(202621, 202622) },
    @{ theme = "DS"; cnName = "干旱高地 / 荒漠 (DS)"; enName = "The Desolated Wasteland"; seeds = @(202641, 202642) },
    @{ theme = "FA"; cnName = "深寒苔原 / 冰原 (FA)"; enName = "The Frozen Lake / Tundra"; seeds = @(202651, 202652) },
    @{ theme = "VO"; cnName = "酸蚀之地 (VO)"; enName = "The Caustic Lands"; seeds = @(202661, 202662) }
)

$modes = @("mf2p", "mf3p")

$proc = $null
$reportEntries = @()

try {
    Write-Host "[1/3] Launching game instance for Multi-Faction panorama batch..."
    $proc = [System.Diagnostics.Process]::Start($psi)

    $deadline = (Get-Date).AddSeconds($BootTimeoutSec)
    while ($true) {
        if ((Get-Date) -gt $deadline) { throw "Timed out waiting for mainMenuReady" }
        $st = Try-Control 27901 "lobby_state"
        if ($st -and $st.mainMenuReady) { break }
        Start-Sleep -Milliseconds 300
    }
    Write-Host "[2/3] Main menu ready. Capturing Multi-Faction (2P + 3P) panoramas across all 6 biomes..."

    foreach ($t in $themes) {
        $themeEntry = @{
            theme = $t.theme
            cnName = $t.cnName
            enName = $t.enName
            cases = @()
        }

        for ($si = 0; $si -lt $t.seeds.Count; $si++) {
            $seed = $t.seeds[$si]
            foreach ($mode in $modes) {
                $caseId = "$($t.theme.ToLowerInvariant())_s$($si + 1)_$mode"
                $imgName = "$caseId.jpg"
                $imgPath = Join-Path $panoDir $imgName
                if (Test-Path $imgPath) { Remove-Item $imgPath -Force }

                Write-Host "  -> Generating $caseId (theme=$($t.theme), seed=$seed, mode=$mode)..."
                $genRes = Invoke-Control 27901 "start_survival_gen" @{
                    seed = $seed
                    theme = $t.theme
                    factorPop = 1.0
                    factorDuration = 1.0
                    mode = $mode
                }
                if (-not $genRes.ok) {
                    throw "start_survival_gen failed for $caseId"
                }

                $genDeadline = (Get-Date).AddSeconds(90)
                while ($true) {
                    if ((Get-Date) -gt $genDeadline) { throw "Timed out waiting for survival gen ($caseId)" }
                    Start-Sleep -Milliseconds 300
                    $st = Try-Control 27901 "lobby_state"
                    if ($st -and -not $st.survivalGenBusy) {
                        if (-not $st.survivalGenOk) {
                            throw "Survival gen error ($caseId): $($st.survivalGenError)"
                        }
                        if ($st.inLevel -and -not $st.loading) { break }
                    }
                }

                Start-Sleep -Milliseconds 500
                $capRes = Invoke-Control 27901 "capture_panorama" @{
                    path = $imgPath
                    resolution = $Resolution
                    reveal = $true
                }
                $metrics = Invoke-Control 27901 "map_metrics" @{}

                $actualFile = $capRes.path
                if (-not (Test-Path $actualFile)) {
                    $pngFallback = [System.IO.Path]::ChangeExtension($imgPath, ".png")
                    if (Test-Path $pngFallback) { $actualFile = $pngFallback }
                    else { throw "Panorama image missing at $imgPath" }
                }
                $relImg = "panoramas_mf/" + [System.IO.Path]::GetFileName($actualFile)
                $fileBytes = (Get-Item $actualFile).Length

                $ccSummary = ($metrics.commandCenters | ForEach-Object { "P$($_.index)=($($_.x),$($_.y))" }) -join " "
                Write-Host "     Captured $relImg ($([Math]::Round($fileBytes / 1024.0, 1)) KB) | NCells=$($metrics.survivalNCells) CCs=$($metrics.commandCenterCount) [$ccSummary] Zombies=$($metrics.zombiesTotal) DV=$($metrics.doomVillages)"

                $themeEntry.cases += @{
                    id = $caseId
                    seedIndex = $si + 1
                    seed = $seed
                    mode = $mode
                    playerCount = $(if ($mode -eq "mf3p") { 3 } else { 2 })
                    image = $relImg
                    sizeKB = [Math]::Round($fileBytes / 1024.0, 1)
                    metrics = $metrics
                }
            }
        }
        $reportEntries += $themeEntry
    }

    $jsonPath = Join-Path $OutputDir "multifaction_panorama_metrics.json"
    $reportEntries | ConvertTo-Json -Depth 12 | Set-Content -Path $jsonPath -Encoding UTF8
    Write-Host "[3/3] Done! Saved Multi-Faction panorama metrics JSON to $jsonPath"
}
finally {
    if ($proc -and -not $proc.HasExited) { $proc | Stop-Process -Force -ErrorAction SilentlyContinue }
    Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
}
