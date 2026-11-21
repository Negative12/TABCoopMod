param(
    [string]$GameDir = "",
    [int]$BootTimeoutSec = 45,
    [int]$LoadTimeoutSec = 90
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_resolve_paths.ps1")
$GameDir = Resolve-TabGameDir $GameDir

function Invoke-Control {
    param([int]$Port, [string]$Cmd, [hashtable]$Params = @{})
    $client = New-Object System.Net.Sockets.TcpClient
    $client.ReceiveTimeout = 8000
    $client.SendTimeout = 5000
    $client.Connect("127.0.0.1", $Port)
    try {
        $stream = $client.GetStream()
        $writer = New-Object System.IO.StreamWriter($stream, [System.Text.Encoding]::UTF8)
        $writer.AutoFlush = $true
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
        $payload = @{ id = 1; tool = $Cmd; args = $Params } | ConvertTo-Json -Depth 6 -Compress
        $writer.WriteLine($payload)
        $line = $reader.ReadLine()
        if ([string]::IsNullOrEmpty($line)) { throw "Empty response from port $Port for $Cmd" }
        $obj = $line | ConvertFrom-Json
        if (-not $obj.ok) { throw "Control error ($Port/$Cmd): $($obj.error)" }
        return $obj.result
    }
    finally { $client.Close() }
}

function Try-Control {
    param([int]$Port, [string]$Cmd, [hashtable]$Params = @{})
    try { return Invoke-Control -Port $Port -Cmd $Cmd -Params $Params }
    catch { return $null }
}

function Wait-Until {
    param([scriptblock]$Condition, [int]$TimeoutSec = 30, [int]$IntervalMs = 250, [string]$Description = "condition")
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        $res = & $Condition
        if ($res) { return $res }
        Start-Sleep -Milliseconds $IntervalMs
    }
    throw "Timeout waiting for: $Description (${TimeoutSec}s)"
}

function Assert-True {
    param([bool]$Cond, [string]$Message)
    if (-not $Cond) { throw "ASSERT FAILED: $Message" }
}

Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

$shotRoot = Join-Path (Get-Location) "tabcoop_shots"
foreach ($sub in @("mfs_host", "mfs_client")) {
    $dir = Join-Path $shotRoot $sub
    if (Test-Path $dir) { Remove-Item "$dir\*.png" -Force -ErrorAction SilentlyContinue }
    $logFile = Join-Path $GameDir "TABCoopMod.$sub.log"
    if (Test-Path $logFile) { Remove-Item $logFile -Force -ErrorAction SilentlyContinue }
}

# Clean up temporary survival save if leftover from prior runs
$savesDir = Join-Path $env:USERPROFILE "Documents\My Games\They Are Billions\Saves"
Get-ChildItem -Path $savesDir -Filter "tabcoop_mfs_test*" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

$exePath = Join-Path $GameDir "TheyAreBillions.exe"
$hostProc = $null
$clientProc = $null

function Start-TabInstance {
    param([string]$Role, [int]$Port)
    $roleShotDir = Join-Path $shotRoot $Role
    New-Item -ItemType Directory -Force -Path $roleShotDir | Out-Null
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exePath
    $psi.WorkingDirectory = $GameDir
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables["TABCOOP_CONTROL_PORT"] = "$Port"
    $psi.EnvironmentVariables["TABCOOP_AUTO_SHOT"] = "1"
    $psi.EnvironmentVariables["TABCOOP_ROLE"] = $Role
    $psi.EnvironmentVariables["TABCOOP_LOG_NAME"] = "TABCoopMod.$Role.log"
    $psi.EnvironmentVariables["TABCOOP_SHOT_DIR"] = $roleShotDir
    $psi.EnvironmentVariables.Remove("TABCOOP_TEST")
    return [System.Diagnostics.Process]::Start($psi)
}

try {
    Write-Host "[1/5] Launching Host (27901) & Client (27902)..."
    $hostProc = Start-TabInstance -Role "mfs_host" -Port 27901
    Start-Sleep -Milliseconds 1500
    $clientProc = Start-TabInstance -Role "mfs_client" -Port 27902

    Wait-Until -TimeoutSec $BootTimeoutSec -Description "Both mainMenuReady" -Condition {
        $h = Try-Control 27901 "lobby_state"
        $c = Try-Control 27902 "lobby_state"
        return ($h -and $c -and $h.mainMenuReady -and $c.mainMenuReady)
    } | Out-Null

    Write-Host "[2/5] Forming MultiFaction room with DefeatRule = AnyBaseLost..."
    Invoke-Control 27901 "open_lobby" | Out-Null
    Invoke-Control 27901 "lobby_action" @{ action = "host_loopback" } | Out-Null
    Invoke-Control 27901 "lobby_action" @{ action = "toggle_game_mode" } | Out-Null
    Invoke-Control 27901 "lobby_action" @{ action = "toggle_defeat_rule" } | Out-Null

    Invoke-Control 27902 "open_lobby" | Out-Null
    Invoke-Control 27902 "lobby_action" @{ action = "join_loopback" } | Out-Null

    Wait-Until -TimeoutSec 15 -Description "Client sees MultiFaction + AnyBaseLost" -Condition {
        $h = Invoke-Control 27901 "lobby_state"
        $c = Invoke-Control 27902 "lobby_state"
        return ($h.peers.Count -eq 2 -and $c.peers.Count -eq 2 -and $c.coopGameMode -eq "MultiFaction" -and $c.defeatRule -eq "AnyBaseLost")
    } | Out-Null

    Invoke-Control 27902 "lobby_action" @{ action = "toggle_ready" } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "Host canStart=True" -Condition {
        (Invoke-Control 27901 "lobby_state").canStart
    } | Out-Null

    Write-Host "[3/5] Generating New Survival match in MultiFaction mode..."
    Invoke-Control 27901 "lobby_action" @{ action = "open_new_survival" } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "Save name InputBox open" -Condition {
        $s = Invoke-Control 27901 "lobby_state"
        return ($s.anyModalOpen -and -not $s.lobbyWindowOpen -and $s.modalHasEditBox)
    } | Out-Null

    $conf1 = Invoke-Control 27901 "lobby_action" @{ action = "confirm_modal"; inputText = "tabcoop_mfs_test" }
    Assert-True ($conf1.ok) "Failed to confirm save name modal"

    Wait-Until -TimeoutSec 10 -Description "New Survival config window open" -Condition {
        $s = Try-Control 27901 "lobby_state"
        return ($s -and $s.anyModalOpen -and -not $s.lobbyWindowOpen -and -not $s.modalHasEditBox)
    } | Out-Null
    Start-Sleep -Milliseconds 150
    $conf2 = Invoke-Control 27901 "lobby_action" @{ action = "confirm_modal" }
    Assert-True ($conf2.ok) "Failed to activate Play button in New Survival modal"

    Wait-Until -TimeoutSec $LoadTimeoutSec -IntervalMs 400 -Description "Host & Client enter generated Survival level" -Condition {
        $h = Try-Control 27901 "lobby_state"
        $c = Try-Control 27902 "lobby_state"
        if ($h -and -not $h.inLevel) { Try-Control 27901 "lobby_action" @{ action = "kick_fade" } | Out-Null }
        if ($c -and -not $c.inLevel) { Try-Control 27902 "lobby_action" @{ action = "kick_fade" } | Out-Null }
        return ($h -and $c -and $h.inLevel -and -not $h.loading -and $c.inLevel -and -not $c.loading)
    } | Out-Null
    Start-Sleep -Milliseconds 1000

    Write-Host "[4/5] Verifying initial 2P Multi-Faction New Survival layout, ownership & camera centering..."
    $hg = Invoke-Control 27901 "game_state"
    $cg = Invoke-Control 27902 "game_state"
    Write-Host "      Host: CCs=$($hg.aliveCommandCenters) tracked=$($hg.trackedOwners) entities=$($hg.entityCount)"
    Write-Host "      Client: CCs=$($cg.aliveCommandCenters) tracked=$($cg.trackedOwners) entities=$($cg.entityCount)"
    Assert-True ($hg.coopGameMode -eq "MultiFaction" -and $cg.coopGameMode -eq "MultiFaction") "Must be MultiFaction"
    Assert-True ($hg.defeatRule -eq "AnyBaseLost" -and $cg.defeatRule -eq "AnyBaseLost") "Must be AnyBaseLost"
    Assert-True ($hg.aliveCommandCenters -eq 2 -and $cg.aliveCommandCenters -eq 2) "Expected 2 alive CommandCenters in New Survival"
    Assert-True ($hg.trackedOwners -eq $cg.trackedOwners -and $hg.trackedOwners -ge 12) "Tracked owner counts must match (>= 2 CCs + 10 starting units)"

    $hCCs = (Invoke-Control 27901 "list_entities" @{ typeContains = "CommandCenter"; onlyPlayer = $true }).entities
    $cc1 = $hCCs | Where-Object { $_.owner -eq 1 } | Select-Object -First 1
    $cc2 = $hCCs | Where-Object { $_.owner -eq 2 } | Select-Object -First 1
    Assert-True ($null -ne $cc1 -and $null -ne $cc2 -and $cc1.id -ne $cc2.id) "Expected distinct CC1 (owner=1) and CC2 (owner=2)"
    $ccSep = [Math]::Sqrt([Math]::Pow($cc1.x - $cc2.x, 2) + [Math]::Pow($cc1.y - $cc2.y, 2))
    Write-Host ("      CC1=({0:F1},{1:F1}) CC2=({2:F1},{3:F1}) separation={4:F1}" -f $cc1.x, $cc1.y, $cc2.x, $cc2.y, $ccSep)
    Assert-True ($ccSep -ge 15.0) "Two starter bases must have meaningful map separation"

    $hCam = Invoke-Control 27901 "get_camera"
    $cCam = Invoke-Control 27902 "get_camera"
    $hDist1 = [Math]::Sqrt([Math]::Pow($hCam.worldX - $cc1.x, 2) + [Math]::Pow($hCam.worldY - $cc1.y, 2))
    $cDist2 = [Math]::Sqrt([Math]::Pow($cCam.worldX - $cc2.x, 2) + [Math]::Pow($cCam.worldY - $cc2.y, 2))
    Assert-True ($hDist1 -lt 8.0) "Host camera must start centered on CC1 (dist=$hDist1)"
    Assert-True ($cDist2 -lt 8.0) "Client camera must start centered on CC2 (dist=$cDist2)"
    Invoke-Control 27901 "screenshot" @{ tag = "01_new_surv_host_cc1" } | Out-Null
    Invoke-Control 27902 "screenshot" @{ tag = "02_new_surv_client_cc2" } | Out-Null

    Write-Host "[5/5] Verifying AnyBaseLost immediate team defeat when CC2 is destroyed while CC1 lives..."
    Invoke-Control 27901 "lobby_action" @{ action = "test_kill_entity"; entityId = $cc2.id } | Out-Null
    Invoke-Control 27902 "lobby_action" @{ action = "test_kill_entity"; entityId = $cc2.id } | Out-Null

    Wait-Until -TimeoutSec 20 -IntervalMs 250 -Description "Team defeat (levelEnded && endStatus=2) triggers immediately under AnyBaseLost" -Condition {
        $hg2 = Try-Control 27901 "game_state"
        $cg2 = Try-Control 27902 "game_state"
        return ($hg2 -and $cg2 -and $hg2.levelEnded -and $cg2.levelEnded -and $hg2.endStatus -eq 2 -and $cg2.endStatus -eq 2)
    } | Out-Null
    $hgFinal = Invoke-Control 27901 "game_state"
    Write-Host "      Team defeat confirmed under AnyBaseLost with aliveCCs=$($hgFinal.aliveCommandCenters)!"

    Write-Host "ALL MULTI-FACTION NEW SURVIVAL + ANYBASELOST ASSERTIONS PASSED!"
}
finally {
    Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-ChildItem -Path $savesDir -Filter "tabcoop_mfs_test*" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Host ""
    foreach ($sub in @("mfs_host", "mfs_client")) {
        $logFile = Join-Path $GameDir "TABCoopMod.$sub.log"
        if (Test-Path $logFile) {
            Write-Host "--- TABCoopMod.$sub.log summary ---"
            Select-String -Path $logFile -Pattern "missing|Harmony|FactionOwnership|CoopBalance|StateChecksum|DESYNC|ERROR|Team defeat" | ForEach-Object { $_.Line }
        }
    }
}
