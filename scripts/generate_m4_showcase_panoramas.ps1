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

$panoDir = Join-Path $OutputDir "panoramas_mf_m4"
New-Item -ItemType Directory -Force -Path $panoDir | Out-Null

Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$logFile = Join-Path $GameDir "TABCoopMod.pano_m4.log"
if (Test-Path $logFile) { Remove-Item $logFile -Force -ErrorAction SilentlyContinue }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = (Join-Path $GameDir "TheyAreBillions.exe")
$psi.WorkingDirectory = $GameDir
$psi.UseShellExecute = $false
$psi.EnvironmentVariables["TABCOOP_CONTROL_PORT"] = "27901"
$psi.EnvironmentVariables["TABCOOP_AUTO_SHOT"] = "0"
$psi.EnvironmentVariables["TABCOOP_ROLE"] = "pano_m4"
$psi.EnvironmentVariables["TABCOOP_LOG_NAME"] = "TABCoopMod.pano_m4.log"
$psi.EnvironmentVariables.Remove("TABCOOP_TEST")

$themes = @(
    @{ theme = "BR"; cnName = "暗黑荒原 (BR)"; enName = "The Dark Moorland"; seed = 202611; factorPop = 1.0; factorDuration = 1.0 },
    @{ theme = "AL"; cnName = "宁静低地 (AL)"; enName = "The Peaceful Lowlands"; seed = 202631; factorPop = 1.0; factorDuration = 1.0 },
    @{ theme = "TM"; cnName = "荒芜旷野 (TM)"; enName = "The Frozen Highlands"; seed = 202624; factorPop = 1.0; factorDuration = 1.0 },
    @{ theme = "DS"; cnName = "干旱高地 (DS)"; enName = "The Desolated Wasteland"; seed = 202641; factorPop = 1.0; factorDuration = 1.0 },
    @{ theme = "FA"; cnName = "深寒苔原 (FA)"; enName = "The Frozen Lake / Tundra"; seed = 202653; factorPop = 1.0; factorDuration = 1.0 },
    @{ theme = "VO"; cnName = "酸蚀之地 900% (VO)"; enName = "The Caustic Lands (900%)"; seed = 202661; factorPop = 1.5; factorDuration = 0.8 }
)

$configs = @(
    @{ key = "mf2p_sep"; mode = "mf2p"; playerCount = 2; spawnMode = "Separated" },
    @{ key = "mf2p_cls"; mode = "mf2p"; playerCount = 2; spawnMode = "Clustered" },
    @{ key = "mf3p_sep"; mode = "mf3p"; playerCount = 3; spawnMode = "Separated" },
    @{ key = "mf3p_cls"; mode = "mf3p"; playerCount = 3; spawnMode = "Clustered" }
)

$proc = $null
$reportEntries = @()

try {
    Write-Host "[1/3] Launching game instance for M4 showcase panorama batch (24 captures)..."
    $proc = [System.Diagnostics.Process]::Start($psi)

    $deadline = (Get-Date).AddSeconds($BootTimeoutSec)
    while ($true) {
        if ((Get-Date) -gt $deadline) { throw "Timed out waiting for mainMenuReady" }
        $st = Try-Control 27901 "lobby_state"
        if ($st -and $st.mainMenuReady) { break }
        Start-Sleep -Milliseconds 300
    }
    Write-Host "[2/3] Main menu ready. Generating & capturing 6 biomes x (2P/3P x Separated/Clustered)..."

    foreach ($t in $themes) {
        $themeEntry = @{
            theme = $t.theme
            cnName = $t.cnName
            enName = $t.enName
            seed = $t.seed
            cases = @()
        }

        foreach ($cfg in $configs) {
            $caseId = "$($t.theme.ToLowerInvariant())_$($cfg.key)"
            $imgName = "$caseId.jpg"
            $imgPath = Join-Path $panoDir $imgName
            if (Test-Path $imgPath) { Remove-Item $imgPath -Force }

            Write-Host "  -> Generating $caseId (theme=$($t.theme), seed=$($t.seed), $($cfg.playerCount)P $($cfg.spawnMode))..."
            $genRes = Invoke-Control 27901 "start_survival_gen" @{
                seed = $t.seed
                theme = $t.theme
                factorPop = $t.factorPop
                factorDuration = $t.factorDuration
                mode = $cfg.mode
                spawnMode = $cfg.spawnMode
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

            Start-Sleep -Milliseconds 450
            $capRes = Invoke-Control 27901 "capture_panorama" @{
                path = $imgPath
                resolution = $Resolution
                reveal = $true
            }
            $metrics = Invoke-Control 27901 "map_metrics" @{}
            $swarm = Invoke-Control 27901 "lobby_action" @{ action = "test_swarm_audit"; triggerLive = $false }

            $actualFile = $capRes.path
            if (-not (Test-Path $actualFile)) {
                $pngFallback = [System.IO.Path]::ChangeExtension($imgPath, ".png")
                if (Test-Path $pngFallback) { $actualFile = $pngFallback }
                else { throw "Panorama image missing at $imgPath" }
            }
            $relImg = "panoramas_mf_m4/" + [System.IO.Path]::GetFileName($actualFile)
            $fileBytes = (Get-Item $actualFile).Length

            $ccSummary = ($metrics.commandCenters | ForEach-Object { "P$($_.index)=($($_.x),$($_.y))" }) -join " "
            Write-Host "     Captured $relImg ($([Math]::Round($fileBytes / 1024.0, 1)) KB) | $($metrics.survivalNCells)g $($metrics.mutationTendency) [$ccSummary] Connected=$($metrics.allCcsConnected) W1=$($swarm.wave1Edges) T3=$($metrics.chubbyZombies)/$($metrics.venomZombies)/$($metrics.harpyZombies) G=$($metrics.giants) M=$($metrics.mutants)"

            $themeEntry.cases += @{
                id = $caseId
                configKey = $cfg.key
                seed = $t.seed
                mode = $cfg.mode
                spawnMode = $cfg.spawnMode
                playerCount = $cfg.playerCount
                image = $relImg
                sizeKB = [Math]::Round($fileBytes / 1024.0, 1)
                metrics = $metrics
                swarmAudit = $swarm
            }
        }
        $reportEntries += $themeEntry
    }

    $jsonPath = Join-Path $OutputDir "multifaction_m4_panorama_metrics.json"
    $reportEntries | ConvertTo-Json -Depth 12 | Set-Content -Path $jsonPath -Encoding UTF8
    Write-Host "[3/3] Done! Saved M4 Multi-Faction panorama metrics JSON to $jsonPath"
}
finally {
    if ($proc -and -not $proc.HasExited) { $proc | Stop-Process -Force -ErrorAction SilentlyContinue }
    Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
}
