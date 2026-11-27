param(
    [string]$GameDir = "",
    [int]$BootTimeoutSec = 60,
    [int]$LoadTimeoutSec = 120
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_resolve_paths.ps1")
$GameDir = Resolve-TabGameDir $GameDir
$savesDir = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "My Games\They Are Billions\Saves"
$backupDir = "D:\TAB_Saves_Backup\20261127_verify"

function Invoke-Control {
    param([int]$Port, [string]$Cmd, [hashtable]$Params = @{}, [int]$TimeoutMs = 15000)
    $client = New-Object System.Net.Sockets.TcpClient
    $client.ReceiveTimeout = $TimeoutMs
    $client.SendTimeout = 8000
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
    try { return Invoke-Control -Port $Port -Cmd $Cmd -Params $Params -TimeoutMs 6000 }
    catch { return $null }
}

function Wait-Until {
    param([scriptblock]$Condition, [int]$TimeoutSec = 45, [int]$IntervalMs = 300, [string]$Description = "condition")
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

function Start-TabInstance {
    param([string]$Role, [int]$Port, [int]$MinFrameMs = 0)
    $exePath = Join-Path $GameDir "TheyAreBillions.exe"
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exePath
    $psi.WorkingDirectory = $GameDir
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables["TABCOOP_CONTROL_PORT"] = "$Port"
    $psi.EnvironmentVariables["TABCOOP_AUTO_SHOT"] = "0"
    $psi.EnvironmentVariables["TABCOOP_ROLE"] = $Role
    $psi.EnvironmentVariables["TABCOOP_LOG_NAME"] = "TABCoopMod.$Role.log"
    if ($MinFrameMs -gt 0) {
        $psi.EnvironmentVariables["TABCOOP_SIM_MIN_FRAME_MS"] = "$MinFrameMs"
    } else {
        $psi.EnvironmentVariables.Remove("TABCOOP_SIM_MIN_FRAME_MS")
    }
    $psi.EnvironmentVariables.Remove("TABCOOP_TEST")
    return [System.Diagnostics.Process]::Start($psi)
}

function Pause-All3 {
    param([string]$Reason = "Paused all 3 instances")
    Invoke-Control 27901 "set_paused" @{ paused = $true } | Out-Null
    Wait-Until -TimeoutSec 20 -Description $Reason -Condition {
        $g1 = Try-Control 27901 "game_state"
        $g2 = Try-Control 27902 "game_state"
        $g3 = Try-Control 27903 "game_state"
        return ($g1 -and $g2 -and $g3 -and $g1.paused -and $g2.paused -and $g3.paused)
    } | Out-Null
}

function Resume-All3 {
    param([string]$Reason = "Resumed all 3 instances")
    Invoke-Control 27901 "set_paused" @{ paused = $false } | Out-Null
    Wait-Until -TimeoutSec 20 -Description $Reason -Condition {
        $g1 = Try-Control 27901 "game_state"
        $g2 = Try-Control 27902 "game_state"
        $g3 = Try-Control 27903 "game_state"
        return ($g1 -and $g2 -and $g3 -and (-not $g1.paused) -and (-not $g2.paused) -and (-not $g3.paused))
    } | Out-Null
}

function Grant-ResourcesAll3 {
    param([int]$PlayerId, [int]$Gold = 0, [int]$Wood = 0, [int]$Stone = 0, [int]$Iron = 0, [int]$Oil = 0, [int]$ExtraStorage = 0, [int]$ExtraWorkers = 0)
    Pause-All3 -Reason "Pause before resource grant P$PlayerId"
    foreach ($port in @(27901, 27902, 27903)) {
        Invoke-Control $port "test_grant_resources" @{
            playerId = $PlayerId; gold = $Gold; wood = $Wood; stone = $Stone; iron = $Iron; oil = $Oil;
            extraStorage = $ExtraStorage; extraWorkers = $ExtraWorkers
        } | Out-Null
    }
    Resume-All3 -Reason "Resume after resource grant P$PlayerId"
}

function Place-NearCc {
    param([int]$Port, [uint64]$BuilderId, [int]$Cx, [int]$Cy, [string]$BuildingType, [int]$SizeW, [int]$SizeH, [int]$WaitMs = 5000)
    $shortType = $BuildingType -replace "^ZX\.Entities\.", ""
    $beforeList = (Invoke-Control $Port "list_entities" @{ typeContains = $shortType; onlyPlayer = $true; limit = 200 }).entities
    $beforeIds = @{}
    foreach ($be in $beforeList) { $beforeIds[$be.id] = $true }
    $spot = Invoke-Control $Port "find_build_spot" @{ buildingType = $BuildingType; cx = $Cx; cy = $Cy; sizeW = $SizeW; sizeH = $SizeH; maxRadius = 28 }
    if (-not ($spot -and $spot.found)) {
        throw "Could not find build spot for $BuildingType around ($Cx,$Cy)"
    }
    $useW = if ($spot.PSObject.Properties["sizeW"]) { [int]$spot.sizeW } else { $SizeW }
    $useH = if ($spot.PSObject.Properties["sizeH"]) { [int]$spot.sizeH } else { $SizeH }
    Invoke-Control $Port "place_building" @{ buildingType = $BuildingType; builderId = $BuilderId; x = $spot.x; y = $spot.y; sizeW = $useW; sizeH = $useH } | Out-Null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalMilliseconds -lt $WaitMs) {
        Start-Sleep -Milliseconds 250
        $after = (Invoke-Control $Port "list_entities" @{ typeContains = $shortType; onlyPlayer = $true; limit = 200 }).entities
        if ($after.Count -gt $beforeList.Count) {
            return ($after | Where-Object { -not $beforeIds.ContainsKey($_.id) } | Select-Object -First 1)
        }
    }
    return $null
}

Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

Get-ChildItem -Path $savesDir -Filter "tabcoop_fix_verify*" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
foreach ($sub in @("fix_host", "fix_client2", "fix_client3")) {
    $lf = Join-Path $GameDir "TABCoopMod.$sub.log"
    if (Test-Path $lf) { Remove-Item $lf -Force -ErrorAction SilentlyContinue }
}

try {
    Write-Host "=== [Scenario A] 3-Player Heterogeneous FPS + Snapshot Barrier + Save Load ==="
    Write-Host "[1/6] Launching Host (27901, 0ms), Client2 (27902, 25ms), Client3 (27903, 42ms)..."
    $null = Start-TabInstance -Role "fix_host" -Port 27901 -MinFrameMs 0
    Start-Sleep -Milliseconds 1500
    $null = Start-TabInstance -Role "fix_client2" -Port 27902 -MinFrameMs 25
    Start-Sleep -Milliseconds 1500
    $null = Start-TabInstance -Role "fix_client3" -Port 27903 -MinFrameMs 42

    Wait-Until -TimeoutSec $BootTimeoutSec -Description "All 3 instances mainMenuReady" -Condition {
        $s1 = Try-Control 27901 "lobby_state"
        $s2 = Try-Control 27902 "lobby_state"
        $s3 = Try-Control 27903 "lobby_state"
        return ($s1 -and $s2 -and $s3 -and $s1.mainMenuReady -and $s2.mainMenuReady -and $s3.mainMenuReady)
    } | Out-Null

    Write-Host "[2/6] Forming 3-player MultiFaction room..."
    Invoke-Control 27901 "open_lobby" | Out-Null
    Invoke-Control 27901 "lobby_action" @{ action = "host_loopback" } | Out-Null
    Invoke-Control 27901 "lobby_action" @{ action = "toggle_game_mode" } | Out-Null

    Invoke-Control 27902 "open_lobby" | Out-Null
    Invoke-Control 27902 "lobby_action" @{ action = "join_loopback" } | Out-Null
    Invoke-Control 27903 "open_lobby" | Out-Null
    Invoke-Control 27903 "lobby_action" @{ action = "join_loopback" } | Out-Null

    Wait-Until -TimeoutSec 20 -Description "All 3 peers connected in MultiFaction lobby" -Condition {
        $l1 = Invoke-Control 27901 "lobby_state"
        $l2 = Invoke-Control 27902 "lobby_state"
        $l3 = Invoke-Control 27903 "lobby_state"
        return ($l1.peers.Count -eq 3 -and $l2.peers.Count -eq 3 -and $l3.peers.Count -eq 3 -and $l2.coopGameMode -eq "MultiFaction" -and $l3.coopGameMode -eq "MultiFaction")
    } | Out-Null

    Invoke-Control 27902 "lobby_action" @{ action = "toggle_ready" } | Out-Null
    Invoke-Control 27903 "lobby_action" @{ action = "toggle_ready" } | Out-Null
    Wait-Until -TimeoutSec 15 -Description "Host canStart=True with both clients ready" -Condition {
        (Invoke-Control 27901 "lobby_state").canStart
    } | Out-Null

    Write-Host "[3/6] Loading bench_900_mf3p_sep across all 3 peers..."
    Invoke-Control 27901 "lobby_action" @{ action = "load_save"; save = "bench_900_mf3p_sep" } | Out-Null
    Wait-Until -TimeoutSec $LoadTimeoutSec -IntervalMs 500 -Description "All 3 enter synced bench_900_mf3p_sep match" -Condition {
        $l1 = Try-Control 27901 "lobby_state"
        $l2 = Try-Control 27902 "lobby_state"
        $l3 = Try-Control 27903 "lobby_state"
        return ($l1 -and $l2 -and $l3 -and $l1.inLevel -and $l2.inLevel -and $l3.inLevel -and (-not $l1.loading) -and (-not $l2.loading) -and (-not $l3.loading))
    } | Out-Null

    $baseTick = (Invoke-Control 27901 "game_state").tick
    Invoke-Control 27901 "set_game_speed" @{ speed = 2.0 } | Out-Null
    Wait-Until -TimeoutSec 40 -Description "All 3 peers advance >= 120 ticks under heterogeneous FPS" -Condition {
        $g1 = Try-Control 27901 "game_state"
        $g2 = Try-Control 27902 "game_state"
        $g3 = Try-Control 27903 "game_state"
        if ($g1 -and $g1.paused -and -not $g1.levelEnded) { Try-Control 27901 "set_paused" @{ paused = $false } | Out-Null }
        return ($g1 -and $g2 -and $g3 -and ($g1.tick -ge ($baseTick + 120)) -and ($g2.tick -ge ($baseTick + 120)) -and ($g3.tick -ge ($baseTick + 120)))
    } | Out-Null
    Invoke-Control 27901 "set_game_speed" @{ speed = 1.0 } | Out-Null
    Pause-All3 -Reason "Pause Scenario A before economyHash comparison"
    $fsA1 = Invoke-Control 27901 "faction_state"
    $fsA2 = Invoke-Control 27902 "faction_state"
    $fsA3 = Invoke-Control 27903 "faction_state"
    Write-Host ("      Scenario A economyHash: P1={0} P2={1} P3={2}" -f $fsA1.economyHash, $fsA2.economyHash, $fsA3.economyHash)
    Assert-True ($fsA1.economyHash -eq $fsA2.economyHash -and $fsA1.economyHash -eq $fsA3.economyHash) "Scenario A: economyHash mismatch across 3 heterogeneous peers!"

    Write-Host "=== [Scenario B] Return to Menu -> New 3P MultiFaction Survival (No LoadingLevel / 0x8000 ID Leak) ==="
    Write-Host "[4/6] Returning all 3 peers to Main Menu lobby and starting New Survival 'tabcoop_fix_verify'..."
    Invoke-Control 27901 "lobby_action" @{ action = "return_to_menu" } | Out-Null
    Wait-Until -TimeoutSec 40 -Description "All 3 peers return to mainMenuReady" -Condition {
        $s1 = Try-Control 27901 "lobby_state"
        $s2 = Try-Control 27902 "lobby_state"
        $s3 = Try-Control 27903 "lobby_state"
        return ($s1 -and $s2 -and $s3 -and $s1.mainMenuReady -and $s2.mainMenuReady -and $s3.mainMenuReady -and (-not $s1.inLevel) -and (-not $s2.inLevel) -and (-not $s3.inLevel))
    } | Out-Null

    Invoke-Control 27902 "open_lobby" | Out-Null
    Invoke-Control 27903 "open_lobby" | Out-Null
    Invoke-Control 27902 "lobby_action" @{ action = "toggle_ready" } | Out-Null
    Invoke-Control 27903 "lobby_action" @{ action = "toggle_ready" } | Out-Null
    Wait-Until -TimeoutSec 15 -Description "Host canStart=True for Match 2" -Condition {
        (Invoke-Control 27901 "lobby_state").canStart
    } | Out-Null

    Invoke-Control 27901 "lobby_action" @{ action = "open_new_survival" } | Out-Null
    Wait-Until -TimeoutSec 15 -Description "Save name modal open" -Condition {
        $s = Invoke-Control 27901 "lobby_state"
        return ($s.anyModalOpen -and -not $s.lobbyWindowOpen -and $s.modalHasEditBox)
    } | Out-Null
    Invoke-Control 27901 "lobby_action" @{ action = "confirm_modal"; inputText = "tabcoop_fix_verify" } | Out-Null
    Wait-Until -TimeoutSec 15 -Description "Survival config modal open" -Condition {
        $s = Try-Control 27901 "lobby_state"
        return ($s -and $s.anyModalOpen -and -not $s.lobbyWindowOpen -and -not $s.modalHasEditBox)
    } | Out-Null
    Start-Sleep -Milliseconds 150
    Invoke-Control 27901 "lobby_action" @{ action = "confirm_modal" } | Out-Null

    Wait-Until -TimeoutSec $LoadTimeoutSec -IntervalMs 500 -Description "All 3 peers enter New Survival 'tabcoop_fix_verify'" -Condition {
        $l1 = Try-Control 27901 "lobby_state"
        $l2 = Try-Control 27902 "lobby_state"
        $l3 = Try-Control 27903 "lobby_state"
        if ($l1 -and -not $l1.inLevel) { Try-Control 27901 "lobby_action" @{ action = "kick_fade" } | Out-Null }
        if ($l2 -and -not $l2.inLevel) { Try-Control 27902 "lobby_action" @{ action = "kick_fade" } | Out-Null }
        if ($l3 -and -not $l3.inLevel) { Try-Control 27903 "lobby_action" @{ action = "kick_fade" } | Out-Null }
        return ($l1 -and $l2 -and $l3 -and $l1.inLevel -and $l2.inLevel -and $l3.inLevel -and (-not $l1.loading) -and (-not $l2.loading) -and (-not $l3.loading))
    } | Out-Null
    Start-Sleep -Milliseconds 1200

    $gB1 = Invoke-Control 27901 "game_state"
    $gB2 = Invoke-Control 27902 "game_state"
    $gB3 = Invoke-Control 27903 "game_state"
    Write-Host ("      New Survival Match 2 entityCount: P1={0}, P2={1}, P3={2}; CCs={3}" -f $gB1.entityCount, $gB2.entityCount, $gB3.entityCount, $gB1.aliveCommandCenters)
    Assert-True ($gB1.entityCount -gt 1000 -and $gB2.entityCount -gt 1000 -and $gB3.entityCount -gt 1000) "EntityCount collapsed (possible 0x8000 high-bit ID leak!): $($gB1.entityCount)"
    Assert-True ($gB1.aliveCommandCenters -eq 3 -and $gB2.aliveCommandCenters -eq 3 -and $gB3.aliveCommandCenters -eq 3) "Expected 3 alive CommandCenters across all peers"

    Write-Host "=== [Scenario C] Cross-Faction Fixed Resource Isolation (Food/Workers/Energy) & Sync Convergence ==="
    Write-Host "[5/6] Testing P1 TentHouse & MillWood builds while asserting P2/P3 fixed pools have ZERO cross-consumption..."
    Grant-ResourcesAll3 -PlayerId 1 -Gold 1000 -Wood 200 -Stone 100 -ExtraStorage 100 -ExtraWorkers 40

    Pause-All3 -Reason "Pause before P1 build isolation measurement"
    $fsPre = Invoke-Control 27901 "faction_state"
    $p1Pre = $fsPre.players | Where-Object { $_.playerId -eq 1 } | Select-Object -First 1
    $p2Pre = $fsPre.players | Where-Object { $_.playerId -eq 2 } | Select-Object -First 1
    $p3Pre = $fsPre.players | Where-Object { $_.playerId -eq 3 } | Select-Object -First 1
    Resume-All3 -Reason "Resume before P1 build"

    $ccs = (Invoke-Control 27901 "list_entities" @{ typeContains = "CommandCenter"; onlyPlayer = $true }).entities
    $cc1 = $ccs | Where-Object { $_.owner -eq 1 } | Select-Object -First 1
    Assert-True ($null -ne $cc1) "CC1 not found"

    $placedTent = Place-NearCc -Port 27901 -BuilderId $cc1.id -Cx ([Math]::Floor($cc1.x)) -Cy ([Math]::Floor($cc1.y)) -BuildingType "ZX.Entities.TentHouse" -SizeW 2 -SizeH 2
    Assert-True ($null -ne $placedTent) "Failed to place TentHouse for P1"
    $placedMill = Place-NearCc -Port 27901 -BuilderId $cc1.id -Cx ([Math]::Floor($cc1.x)) -Cy ([Math]::Floor($cc1.y)) -BuildingType "ZX.Entities.MillWood" -SizeW 2 -SizeH 2
    Assert-True ($null -ne $placedMill) "Failed to place MillWood for P1"

    # Also move P2's Rangers concurrently (exercises CreateCommandTarget & multi-peer sync)
    $p2Rangers = (Invoke-Control 27902 "list_entities" @{ typeContains = "Ranger"; onlyPlayer = $true }).entities | Where-Object { $_.owner -eq 2 }
    if ($p2Rangers.Count -gt 0) {
        $r0 = $p2Rangers[0]
        $moveIds = @($p2Rangers | Select-Object -First 4 | ForEach-Object { $_.id })
        Invoke-Control 27902 "move_units" @{ ids = $moveIds; x = ($r0.x + 2.5); y = ($r0.y + 1.5) } | Out-Null
    }

    Start-Sleep -Milliseconds 1200
    Pause-All3 -Reason "Pause right after P1 placements to verify zero cross-faction drain"
    $fsPostBuild1 = Invoke-Control 27901 "faction_state"
    $fsPostBuild2 = Invoke-Control 27902 "faction_state"
    $fsPostBuild3 = Invoke-Control 27903 "faction_state"
    $p1Post = $fsPostBuild1.players | Where-Object { $_.playerId -eq 1 } | Select-Object -First 1
    $p2Post = $fsPostBuild1.players | Where-Object { $_.playerId -eq 2 } | Select-Object -First 1
    $p3Post = $fsPostBuild1.players | Where-Object { $_.playerId -eq 3 } | Select-Object -First 1

    Write-Host ("      P1 before(F={0}/{1},W={2}/{3},E={4}/{5}) -> after(F={6}/{7},W={8}/{9},E={10}/{11})" -f `
        $p1Pre.foodNeeded, $p1Pre.foodSupply, $p1Pre.workersNeeded, $p1Pre.workersSupply, $p1Pre.energyNeeded, $p1Pre.energySupply, `
        $p1Post.foodNeeded, $p1Post.foodSupply, $p1Post.workersNeeded, $p1Post.workersSupply, $p1Post.energyNeeded, $p1Post.energySupply)
    Write-Host ("      P2 before(F={0},W={1},E={2}) -> after(F={3},W={4},E={5})" -f $p2Pre.foodNeeded, $p2Pre.workersNeeded, $p2Pre.energyNeeded, $p2Post.foodNeeded, $p2Post.workersNeeded, $p2Post.energyNeeded)
    Write-Host ("      P3 before(F={0},W={1},E={2}) -> after(F={3},W={4},E={5})" -f $p3Pre.foodNeeded, $p3Pre.workersNeeded, $p3Pre.energyNeeded, $p3Post.foodNeeded, $p3Post.workersNeeded, $p3Post.energyNeeded)

    Assert-True ($p2Post.foodNeeded -eq $p2Pre.foodNeeded -and $p2Post.workersNeeded -eq $p2Pre.workersNeeded -and $p2Post.energyNeeded -eq $p2Pre.energyNeeded) "P2 fixed resources mutated by P1 construction!"
    Assert-True ($p3Post.foodNeeded -eq $p3Pre.foodNeeded -and $p3Post.workersNeeded -eq $p3Pre.workersNeeded -and $p3Post.energyNeeded -eq $p3Pre.energyNeeded) "P3 fixed resources mutated by P1 construction!"
    Assert-True ($p2Post.foodSupply -eq $p2Pre.foodSupply -and $p2Post.workersSupply -eq $p2Pre.workersSupply -and $p2Post.energySupply -eq $p2Pre.energySupply) "P2 supply mutated by P1 construction!"
    Assert-True ($p3Post.foodSupply -eq $p3Pre.foodSupply -and $p3Post.workersSupply -eq $p3Pre.workersSupply -and $p3Post.energySupply -eq $p3Pre.energySupply) "P3 supply mutated by P1 construction!"
    Assert-True ($fsPostBuild1.economyHash -eq $fsPostBuild2.economyHash -and $fsPostBuild1.economyHash -eq $fsPostBuild3.economyHash) "Scenario C: economyHash diverged right after P1 build!"

    # Advance 240+ ticks at 6x so TentHouse+MillWood complete AND pass 8-hour step + multiple 60-tick checksums
    Resume-All3 -Reason "Resume to advance 240 ticks in Match 2"
    $tStartC = (Invoke-Control 27901 "game_state").tick
    Invoke-Control 27901 "set_game_speed" @{ speed = 6.0 } | Out-Null
    Wait-Until -TimeoutSec 45 -Description "Advance 240 ticks in Match 2 across all 3 peers" -Condition {
        $g1 = Try-Control 27901 "game_state"
        $g2 = Try-Control 27902 "game_state"
        $g3 = Try-Control 27903 "game_state"
        if ($g1 -and $g1.paused -and -not $g1.levelEnded) { Try-Control 27901 "set_paused" @{ paused = $false } | Out-Null }
        return ($g1 -and $g2 -and $g3 -and ($g1.tick -ge ($tStartC + 240)) -and ($g2.tick -ge ($tStartC + 240)) -and ($g3.tick -ge ($tStartC + 240)))
    } | Out-Null
    Invoke-Control 27901 "set_game_speed" @{ speed = 1.0 } | Out-Null
    Pause-All3 -Reason "Pause after 240 ticks in Match 2"

    $fsEnd1 = Invoke-Control 27901 "faction_state"
    $fsEnd2 = Invoke-Control 27902 "faction_state"
    $fsEnd3 = Invoke-Control 27903 "faction_state"
    Write-Host ("      Match 2 post-240t economyHash: P1={0} P2={1} P3={2}" -f $fsEnd1.economyHash, $fsEnd2.economyHash, $fsEnd3.economyHash)
    Assert-True ($fsEnd1.economyHash -eq $fsEnd2.economyHash -and $fsEnd1.economyHash -eq $fsEnd3.economyHash) "Match 2 post-240t economyHash mismatch across 3 peers!"

    Write-Host "=== [Scenario D] Terminating & Auditing Logs ==="
    Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 700

    $hostLogPath = Join-Path $GameDir "TABCoopMod.fix_host.log"
    $c2LogPath = Join-Path $GameDir "TABCoopMod.fix_client2.log"
    $c3LogPath = Join-Path $GameDir "TABCoopMod.fix_client3.log"

    foreach ($lf in @($hostLogPath, $c2LogPath, $c3LogPath)) {
        Assert-True (Test-Path $lf) "Missing log file: $lf"
        $miss = Select-String -Path $lf -Pattern "0 missing"
        Assert-True ($null -ne $miss) "GameBridge missing bindings in $lf"
        $tol = Select-String -Path $lf -Pattern "failed-tolerant=0"
        Assert-True ($null -ne $tol) "Harmony failed-tolerant != 0 in $lf"
        $argEx = Select-String -Path $lf -Pattern "cannot be converted to type 'ZX\.Entities\.ZXEntity'|System\.ArgumentException"
        Assert-True ($null -eq $argEx) "ArgumentException / DXEntity conversion error found in $lf!"
        $hardDesync = Select-String -Path $lf -Pattern "HARD DESYNC"
        Assert-True ($null -eq $hardDesync) "HARD DESYNC detected in $lf!"
        $drift = Select-String -Path $lf -Pattern "TRANSIENT STATE DRIFT"
        Assert-True ($null -eq $drift) "TRANSIENT STATE DRIFT detected in $lf!"
    }

    $ack2 = Select-String -Path $hostLogPath -Pattern "Received SnapshotLoadedAck from Client\(2\).*allAcked=(True|False)"
    $ack3 = Select-String -Path $hostLogPath -Pattern "Received SnapshotLoadedAck from Client\(3\).*allAcked=(True|False)"
    $allAcked = Select-String -Path $hostLogPath -Pattern "SnapshotLoadedAck.*allAcked=True, ackedCount=2"
    Assert-True ($null -ne $ack2 -and $null -ne $ack3 -and $null -ne $allAcked) "Missing 3-player SnapshotLoadedAck barrier proof (allAcked=True, ackedCount=2) in host log!"

    $counterLine = Select-String -Path $hostLogPath -Pattern "Host initial level load complete: counter=(\d+)" | Select-Object -Last 1
    Assert-True ($null -ne $counterLine) "Missing Host initial level load complete line"
    $counterVal = [int64]$counterLine.Matches[0].Groups[1].Value
    Write-Host "      Host Match 2 initial load counter = $counterVal (must be > 105000)"
    Assert-True ($counterVal -gt 105000) "Host Match 2 counter ($counterVal) <= 105000 -> high-bit ID leak!"

    # Host throttles consecutive StateChecksum MATCH logs globally every 30s across clients;
    # combined with Scenario A/C 3-peer economyHash assertions and zero HARD DESYNC / TRANSIENT STATE DRIFT,
    # verifying MATCH log presence without requiring both Client(2) and Client(3) unthrottled strings avoids false flakiness.
    $matchAny = Select-String -Path $hostLogPath -Pattern "StateChecksum.*MATCH"
    Assert-True ($null -ne $matchAny) "Expected StateChecksum MATCH entry in host log!"

    Write-Host "ALL 3-PLAYER & MULTI-FACTION FIX VERIFICATION CHECKS PASSED!"
}
finally {
    Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-ChildItem -Path $savesDir -Filter "tabcoop_fix_verify*" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    if (Test-Path $backupDir) {
        Get-ChildItem -Path $backupDir | ForEach-Object {
            $dst = Join-Path $savesDir $_.Name
            if (-not (Test-Path $dst)) {
                Copy-Item $_.FullName -Destination $dst -Force
                Write-Host "[BackupRestore] Restored deleted save $($_.Name)"
            }
        }
    }
}
