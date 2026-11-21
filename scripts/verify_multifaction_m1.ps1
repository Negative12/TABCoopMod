param(
    [string]$GameDir = "",
    [string]$SaveName = "1_Backup",
    [int]$BootTimeoutSec = 45,
    [int]$LoadTimeoutSec = 60
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
foreach ($sub in @("mf_host", "mf_client")) {
    $dir = Join-Path $shotRoot $sub
    if (Test-Path $dir) { Remove-Item "$dir\*.png" -Force -ErrorAction SilentlyContinue }
    $logFile = Join-Path $GameDir "TABCoopMod.$sub.log"
    if (Test-Path $logFile) { Remove-Item $logFile -Force -ErrorAction SilentlyContinue }
}

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
    Write-Host "[1/8] Launching Host (27901) & Client (27902)..."
    $hostProc = Start-TabInstance -Role "mf_host" -Port 27901
    Start-Sleep -Milliseconds 1500
    $clientProc = Start-TabInstance -Role "mf_client" -Port 27902

    Wait-Until -TimeoutSec $BootTimeoutSec -Description "Both mainMenuReady" -Condition {
        $h = Try-Control 27901 "lobby_state"
        $c = Try-Control 27902 "lobby_state"
        return ($h -and $c -and $h.mainMenuReady -and $c.mainMenuReady)
    } | Out-Null

    Write-Host "[2/8] Creating room, switching to MultiFaction (AllBasesLost), Client joining & readying..."
    Invoke-Control 27901 "open_lobby" | Out-Null
    Invoke-Control 27901 "lobby_action" @{ action = "host_loopback" } | Out-Null
    Invoke-Control 27901 "lobby_action" @{ action = "toggle_game_mode" } | Out-Null

    Invoke-Control 27902 "open_lobby" | Out-Null
    Invoke-Control 27902 "lobby_action" @{ action = "join_loopback" } | Out-Null

    Wait-Until -TimeoutSec 15 -Description "Client sees 2 peers & MultiFaction mode" -Condition {
        $h = Invoke-Control 27901 "lobby_state"
        $c = Invoke-Control 27902 "lobby_state"
        return ($h.peers.Count -eq 2 -and $c.peers.Count -eq 2 -and $c.coopGameMode -eq "MultiFaction" -and $c.defeatRule -eq "AllBasesLost")
    } | Out-Null

    Invoke-Control 27902 "lobby_action" @{ action = "toggle_ready" } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "Host canStart=True" -Condition {
        (Invoke-Control 27901 "lobby_state").canStart
    } | Out-Null
    Invoke-Control 27901 "screenshot" @{ tag = "01_lobby_multifaction" } | Out-Null

    Write-Host "[3/8] Host loading '$SaveName' in MultiFaction mode..."
    Invoke-Control 27901 "lobby_action" @{ action = "load_save"; save = $SaveName } | Out-Null
    Wait-Until -TimeoutSec $LoadTimeoutSec -IntervalMs 500 -Description "Both Host & Client in synced level" -Condition {
        $h = Try-Control 27901 "lobby_state"
        $c = Try-Control 27902 "lobby_state"
        return ($h -and $c -and $h.inLevel -and -not $h.loading -and $c.inLevel -and -not $c.loading)
    } | Out-Null
    Start-Sleep -Milliseconds 1500

    $hg = Invoke-Control 27901 "game_state"
    $cg = Invoke-Control 27902 "game_state"
    Write-Host "      Host: CCs=$($hg.aliveCommandCenters) tracked=$($hg.trackedOwners) entities=$($hg.entityCount)"
    Write-Host "      Client: CCs=$($cg.aliveCommandCenters) tracked=$($cg.trackedOwners) entities=$($cg.entityCount)"
    Assert-True ($hg.coopGameMode -eq "MultiFaction" -and $cg.coopGameMode -eq "MultiFaction") "Both must be in MultiFaction"
    Assert-True ($hg.aliveCommandCenters -eq 2 -and $cg.aliveCommandCenters -eq 2) "Expected 2 alive CCs on both sides"
    Assert-True ($hg.trackedOwners -eq $cg.trackedOwners -and $hg.trackedOwners -gt 0) "Tracked owner counts must match"

    Write-Host "[4/8] Verifying CommandCenter ownership & per-player initial camera centering..."
    $hCCs = (Invoke-Control 27901 "list_entities" @{ typeContains = "CommandCenter"; onlyPlayer = $true }).entities
    $cCCs = (Invoke-Control 27902 "list_entities" @{ typeContains = "CommandCenter"; onlyPlayer = $true }).entities
    Assert-True ($hCCs.Count -eq 2 -and $cCCs.Count -eq 2) "Expected 2 CommandCenters listed"
    $cc1 = $hCCs | Where-Object { $_.owner -eq 1 } | Select-Object -First 1
    $cc2 = $hCCs | Where-Object { $_.owner -eq 2 } | Select-Object -First 1
    Assert-True ($null -ne $cc1 -and $null -ne $cc2 -and $cc1.id -ne $cc2.id) "CC1 (owner=1) and CC2 (owner=2) must be distinct"

    $hCam = Invoke-Control 27901 "get_camera"
    $cCam = Invoke-Control 27902 "get_camera"
    Invoke-Control 27901 "screenshot" @{ tag = "02_host_cc1" } | Out-Null
    Invoke-Control 27902 "screenshot" @{ tag = "03_client_cc2" } | Out-Null
    Write-Host ("      Host cam=({0:F1},{1:F1} scene {2:F1},{3:F1} {4:F1}x{5:F1}) vs CC1=({6:F1},{7:F1})" -f $hCam.worldX, $hCam.worldY, $hCam.x, $hCam.y, $hCam.w, $hCam.h, $cc1.x, $cc1.y)
    Write-Host ("      Client cam=({0:F1},{1:F1} scene {2:F1},{3:F1} {4:F1}x{5:F1}) vs CC2=({6:F1},{7:F1})" -f $cCam.worldX, $cCam.worldY, $cCam.x, $cCam.y, $cCam.w, $cCam.h, $cc2.x, $cc2.y)
    $hDist1 = [Math]::Sqrt([Math]::Pow($hCam.worldX - $cc1.x, 2) + [Math]::Pow($hCam.worldY - $cc1.y, 2))
    $cDist2 = [Math]::Sqrt([Math]::Pow($cCam.worldX - $cc2.x, 2) + [Math]::Pow($cCam.worldY - $cc2.y, 2))
    Write-Host ("      Host cam dist to CC1={0:F2}; Client cam dist to CC2={1:F2}" -f $hDist1, $cDist2)
    Assert-True ($hDist1 -lt 8.0) "Host camera must start centered on CC1"
    Assert-True ($cDist2 -lt 8.0) "Client camera must start centered on CC2"

    Write-Host "[5/8] Verifying selection & command control isolation..."
    $hSelOwn = Invoke-Control 27901 "lobby_action" @{ action = "test_select"; entityId = $cc1.id }
    $hSelAlly = Invoke-Control 27901 "lobby_action" @{ action = "test_select"; entityId = $cc2.id }
    $cSelOwn = Invoke-Control 27902 "lobby_action" @{ action = "test_select"; entityId = $cc2.id }
    $cSelAlly = Invoke-Control 27902 "lobby_action" @{ action = "test_select"; entityId = $cc1.id }
    Assert-True ($hSelOwn.canSelect -and $hSelOwn.isEntitySelected) "Host must select CC1"
    Assert-True ((-not $hSelAlly.canSelect) -and (-not $hSelAlly.isEntitySelected)) "Host must NOT select CC2"
    Assert-True ($cSelOwn.canSelect -and $cSelOwn.isEntitySelected) "Client must select CC2"
    Assert-True ((-not $cSelAlly.canSelect) -and (-not $cSelAlly.isEntitySelected)) "Client must NOT select CC1"

    $rangers = (Invoke-Control 27902 "list_entities" @{ typeContains = "Ranger"; onlyPlayer = $true; limit = 30 }).entities
    $p2Ranger = $rangers | Where-Object { $_.owner -eq 2 } | Select-Object -First 1
    Assert-True ($null -ne $p2Ranger) "P2 must own at least one Ranger"

    # Attempt illegal move by Host on P2 Ranger, and legal move by Client on P2 Ranger
    $targetX = [Math]::Round($cc2.x + 4.0, 1)
    $targetY = [Math]::Round($cc2.y + 4.0, 1)
    Invoke-Control 27901 "move_units" @{ ids = @($p2Ranger.id); x = [Math]::Round($cc1.x, 1); y = [Math]::Round($cc1.y, 1) } | Out-Null
    Invoke-Control 27902 "move_units" @{ ids = @($p2Ranger.id); x = $targetX; y = $targetY } | Out-Null
    Start-Sleep -Milliseconds 2500

    $hR2 = (Invoke-Control 27901 "list_entities" @{ typeContains = "Ranger"; onlyPlayer = $true; limit = 30 }).entities | Where-Object { $_.id -eq $p2Ranger.id } | Select-Object -First 1
    $cR2 = (Invoke-Control 27902 "list_entities" @{ typeContains = "Ranger"; onlyPlayer = $true; limit = 30 }).entities | Where-Object { $_.id -eq $p2Ranger.id } | Select-Object -First 1
    $distH = [Math]::Sqrt([Math]::Pow($hR2.x - $targetX, 2) + [Math]::Pow($hR2.y - $targetY, 2))
    $distC = [Math]::Sqrt([Math]::Pow($cR2.x - $targetX, 2) + [Math]::Pow($cR2.y - $targetY, 2))
    Write-Host ("      P2 Ranger moved to H=({0},{1}) C=({2},{3}) dist=({4:F2},{5:F2})" -f $hR2.x, $hR2.y, $cR2.x, $cR2.y, $distH, $distC)
    Assert-True ($distH -lt 2.5 -and $distC -lt 2.5) "P2 Ranger must obey Client move and ignore Host move"

    Write-Host "[6/8] Verifying shared energy grid near CC2 (building TentHouse as P2)..."
    $beforeTents = (Invoke-Control 27902 "list_entities" @{ typeContains = "TentHouse"; onlyPlayer = $true; limit = 100 }).entities.Count
    $placedOk = $false
    foreach ($off in @(@(5, 0), @(-5, 0), @(0, 5), @(0, -5), @(5, 5), @(-5, -5))) {
        $bx = [Math]::Floor($cc2.x) + $off[0]
        $by = [Math]::Floor($cc2.y) + $off[1]
        Invoke-Control 27902 "place_building" @{ buildingType = "ZX.Entities.TentHouse"; builderId = $cc2.id; x = $bx; y = $by; sizeW = 2; sizeH = 2 } | Out-Null
        Start-Sleep -Milliseconds 1200
        $afterTents = (Invoke-Control 27902 "list_entities" @{ typeContains = "TentHouse"; onlyPlayer = $true; limit = 100 }).entities
        if ($afterTents.Count -gt $beforeTents) {
            $newTent = $afterTents | Where-Object { $_.owner -eq 2 } | Select-Object -First 1
            Assert-True ($null -ne $newTent) "New TentHouse built by P2 must be owned by P2"
            $placedOk = $true
            break
        }
    }
    Assert-True $placedOk "P2 must be able to build TentHouse inside CC2 shared energy perimeter"

    Write-Host "[7/8] Verifying AllBasesLost exile handover when CC2 is destroyed..."
    Invoke-Control 27901 "lobby_action" @{ action = "test_kill_entity"; entityId = $cc2.id } | Out-Null
    Invoke-Control 27902 "lobby_action" @{ action = "test_kill_entity"; entityId = $cc2.id } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "Client exiled & CC count drops to 1 without team defeat" -Condition {
        $h = Invoke-Control 27901 "game_state"
        $c = Invoke-Control 27902 "game_state"
        return ($h.aliveCommandCenters -eq 1 -and $c.aliveCommandCenters -eq 1 -and $c.localPlayerExiled -and (-not $h.levelEnded) -and (-not $c.levelEnded))
    } | Out-Null
    $p2RangerAfter = (Invoke-Control 27901 "list_entities" @{ typeContains = "Ranger"; onlyPlayer = $true; limit = 30 }).entities | Where-Object { $_.id -eq $p2Ranger.id } | Select-Object -First 1
    Assert-True ($p2RangerAfter.owner -eq 1) "Exiled P2 Ranger ownership must transfer to surviving P1 (got owner=$($p2RangerAfter.owner))"

    Write-Host "      Now destroying final CC1 -> verifying team defeat triggers on both..."
    Invoke-Control 27901 "lobby_action" @{ action = "test_kill_entity"; entityId = $cc1.id } | Out-Null
    Invoke-Control 27902 "lobby_action" @{ action = "test_kill_entity"; entityId = $cc1.id } | Out-Null
    Wait-Until -TimeoutSec 12 -Description "Both Host & Client trigger levelEnded after last CC destroyed" -Condition {
        $h = Invoke-Control 27901 "game_state"
        $c = Invoke-Control 27902 "game_state"
        return ($h.levelEnded -and $c.levelEnded -and $h.endStatus -eq 2 -and $c.endStatus -eq 2)
    } | Out-Null

    Write-Host "[8/8] ALL M1 MULTI-FACTION ASSERTIONS PASSED!"
}
finally {
    if ($hostProc -and -not $hostProc.HasExited) { try { $hostProc.Kill() } catch {} }
    if ($clientProc -and -not $clientProc.HasExited) { try { $clientProc.Kill() } catch {} }
    Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force

    foreach ($logName in @("TABCoopMod.mf_host.log", "TABCoopMod.mf_client.log")) {
        $p = Join-Path $GameDir $logName
        if (Test-Path $p) {
            Write-Host "`n--- $logName summary ---"
            Select-String -Path $p -Pattern "ALL BINDINGS|Classes OK|ERROR|DESYNC|FactionOwnership|EXILED" | ForEach-Object { Write-Host $_.Line }
        }
    }
}
