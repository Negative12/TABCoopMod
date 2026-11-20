param(
    [string]$GameDir = "",
    [int]$BootTimeoutSec = 45,
    [int]$LoadTimeoutSec = 75
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_resolve_paths.ps1")
$GameDir = Resolve-TabGameDir $GameDir
$savesDir = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "My Games\They Are Billions\Saves"

function Invoke-Control {
    param([int]$Port, [string]$Cmd, [hashtable]$Params = @{}, [int]$TimeoutMs = 90000)
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
    try { return Invoke-Control -Port $Port -Cmd $Cmd -Params $Params -TimeoutMs 8000 }
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

function Start-TabInstance {
    param([string]$Role, [int]$Port)
    $shotRoot = Join-Path (Get-Location) "tabcoop_shots"
    $roleShotDir = Join-Path $shotRoot $Role
    New-Item -ItemType Directory -Force -Path $roleShotDir | Out-Null
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = (Join-Path $GameDir "TheyAreBillions.exe")
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

Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

foreach ($sub in @("m4_host", "m4_client", "m4_audit")) {
    $logFile = Join-Path $GameDir "TABCoopMod.$sub.log"
    if (Test-Path $logFile) { Remove-Item $logFile -Force -ErrorAction SilentlyContinue }
}

try {
    # =====================================================================
    # Part 1: Dual-Client Lobby SpawnMode Sync + Clustered 2P End-to-End Match
    # =====================================================================
    Write-Host "[1/6] Launching Host (27901) & Client (27902) for Clustered 2P sync test..."
    $null = Start-TabInstance -Role "m4_host" -Port 27901
    Start-Sleep -Milliseconds 1400
    $null = Start-TabInstance -Role "m4_client" -Port 27902

    Wait-Until -TimeoutSec $BootTimeoutSec -Description "Both mainMenuReady" -Condition {
        $h = Try-Control 27901 "lobby_state"
        $c = Try-Control 27902 "lobby_state"
        return ($h -and $c -and $h.mainMenuReady -and $c.mainMenuReady)
    } | Out-Null

    Write-Host "[2/6] Switching Lobby -> MultiFaction + Clustered spawnMode and syncing Client..."
    Invoke-Control 27901 "open_lobby" | Out-Null
    Invoke-Control 27901 "lobby_action" @{ action = "host_loopback" } | Out-Null
    Invoke-Control 27901 "lobby_action" @{ action = "toggle_game_mode" } | Out-Null
    Invoke-Control 27901 "lobby_action" @{ action = "toggle_spawn_mode" } | Out-Null

    Invoke-Control 27902 "open_lobby" | Out-Null
    Invoke-Control 27902 "lobby_action" @{ action = "join_loopback" } | Out-Null

    Wait-Until -TimeoutSec 15 -Description "Client sees MultiFaction + Clustered" -Condition {
        $h = Invoke-Control 27901 "lobby_state"
        $c = Invoke-Control 27902 "lobby_state"
        return ($h.peers.Count -eq 2 -and $c.peers.Count -eq 2 -and $c.coopGameMode -eq "MultiFaction" -and $c.spawnMode -eq "Clustered")
    } | Out-Null

    Invoke-Control 27902 "lobby_action" @{ action = "toggle_ready" } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "Host canStart=True" -Condition {
        (Invoke-Control 27901 "lobby_state").canStart
    } | Out-Null

    Write-Host "[3/6] Generating Clustered 2P Survival match and syncing to Client..."
    Invoke-Control 27901 "lobby_action" @{ action = "open_new_survival" } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "Save name modal open" -Condition {
        $s = Invoke-Control 27901 "lobby_state"
        return ($s.anyModalOpen -and -not $s.lobbyWindowOpen -and $s.modalHasEditBox)
    } | Out-Null
    Invoke-Control 27901 "lobby_action" @{ action = "confirm_modal"; inputText = "tabcoop_m4_cluster" } | Out-Null

    Wait-Until -TimeoutSec 10 -Description "New Survival config modal open" -Condition {
        $s = Try-Control 27901 "lobby_state"
        return ($s -and $s.anyModalOpen -and -not $s.lobbyWindowOpen -and -not $s.modalHasEditBox)
    } | Out-Null
    Start-Sleep -Milliseconds 150
    Invoke-Control 27901 "lobby_action" @{ action = "confirm_modal" } | Out-Null

    Wait-Until -TimeoutSec $LoadTimeoutSec -IntervalMs 400 -Description "Host & Client enter Clustered match" -Condition {
        $h = Try-Control 27901 "lobby_state"
        $c = Try-Control 27902 "lobby_state"
        if ($h -and -not $h.inLevel) { Try-Control 27901 "lobby_action" @{ action = "kick_fade" } | Out-Null }
        if ($c -and -not $c.inLevel) { Try-Control 27902 "lobby_action" @{ action = "kick_fade" } | Out-Null }
        return ($h -and $c -and $h.inLevel -and -not $h.loading -and $c.inLevel -and -not $c.loading)
    } | Out-Null
    Start-Sleep -Milliseconds 1200

    $hg = Invoke-Control 27901 "game_state"
    $cg = Invoke-Control 27902 "game_state"
    $hMetrics = Invoke-Control 27901 "map_metrics" @{}
    Write-Host ("      Clustered 2P: NCells={0} CCs={1} spawnMode={2} tendency={3} allConnected={4}" -f $hMetrics.survivalNCells, $hMetrics.commandCenterCount, $hMetrics.spawnMode, $hMetrics.mutationTendency, $hMetrics.allCcsConnected)

    Assert-True ($hg.coopGameMode -eq "MultiFaction" -and $cg.coopGameMode -eq "MultiFaction") "Both must be MultiFaction"
    Assert-True ($hg.spawnMode -eq "Clustered" -and $cg.spawnMode -eq "Clustered") "Both must report Clustered spawnMode"
    Assert-True ($hMetrics.survivalNCells -eq 360) "2P Clustered map NCells should be 360 (got $($hMetrics.survivalNCells))"
    Assert-True ($hMetrics.commandCenterCount -eq 2 -and $cg.aliveCommandCenters -eq 2) "Expected 2 alive CCs on both peers"
    Assert-True ($hMetrics.allCcsConnected -eq $true) "Both Clustered CCs must be walkable-connected"
    Assert-True ($hMetrics.rangers -ge 8 -and $hMetrics.soldiers -ge 2) "Expected full 4R+1S per player (>=8 rangers, >=2 soldiers, got R=$($hMetrics.rangers) S=$($hMetrics.soldiers))"

    $sep = [double]$hMetrics.commandCenters[0].minOtherCcDist
    $zDistMin = [Math]::Min([double]$hMetrics.commandCenters[0].nearestZombieDist, [double]$hMetrics.commandCenters[1].nearestZombieDist)
    $stoneTotal = [int]$hMetrics.commandCenters[0].stone24 + [int]$hMetrics.commandCenters[1].stone24
    Write-Host ("      Clustered CC sep={0:F1}, nearestZombie={1:F1}, teamStone24={2}, doomed={3}" -f $sep, $zDistMin, $stoneTotal, $hMetrics.doomVillages)
    Assert-True ($sep -ge 12.0 -and $sep -le 36.0) "Clustered CC separation should be compact (~18-24 target, <=36 fallback), got $sep"
    Assert-True ($zDistMin -ge 27.5 -or $zDistMin -lt 0) "No zombies inside 28-cell safe ring (got min=$zDistMin)"
    Assert-True ($hMetrics.doomVillages -ge 2) "2P M4 DoomVillages floor is 2 (got $($hMetrics.doomVillages))"

    # Test synchronized P2 movement in Clustered match
    $p2Rangers = (Invoke-Control 27902 "list_entities" @{ typeContains = "Ranger"; onlyPlayer = $true }).entities | Where-Object { $_.owner -eq 2 }
    Assert-True ($p2Rangers.Count -ge 4) "Client P2 must own >= 4 starter Rangers"
    $r1 = $p2Rangers[0]
    $r2 = $p2Rangers[1]
    Invoke-Control 27902 "move_units" @{ ids = @($r1.id, $r2.id); x = ($r1.x + 3.0); y = ($r1.y + 2.0) } | Out-Null
    Start-Sleep -Milliseconds 1200

    Invoke-Control 27901 "screenshot" @{ tag = "01_m4_clustered_host" } | Out-Null
    Invoke-Control 27902 "screenshot" @{ tag = "02_m4_clustered_client" } | Out-Null

    Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 600

    # Assert StateChecksum MATCH and no DESYNC in dual logs
    foreach ($sub in @("m4_host", "m4_client")) {
        $lf = Join-Path $GameDir "TABCoopMod.$sub.log"
        Assert-True (Test-Path $lf) "Missing log $lf"
        $desync = Select-String -Path $lf -Pattern "DESYNC"
        Assert-True ($null -eq $desync) "DESYNC detected in $sub log!"
    }
    $matchLine = Select-String -Path (Join-Path $GameDir "TABCoopMod.m4_host.log") -Pattern "StateChecksum.*MATCH" | Select-Object -First 1
    Assert-True ($null -ne $matchLine) "Expected StateChecksum MATCH in m4_host log!"
    Write-Host "      Dual-Client Clustered match synced cleanly ($($matchLine.Line.Trim()))."

    # =====================================================================
    # Part 2: M4 MapGen + N-Stream Swarm & Late-Wave Compression Matrix
    # =====================================================================
    Write-Host "[4/6] Launching standalone instance for M4 multi-biome MapGen + Swarm Audit..."
    $null = Start-TabInstance -Role "m4_audit" -Port 27901
    Wait-Until -TimeoutSec $BootTimeoutSec -Description "Audit mainMenuReady" -Condition {
        $s = Try-Control 27901 "lobby_state"
        return ($s -and $s.mainMenuReady)
    } | Out-Null

    $cases = @(
        @{ label = "BR 2P Separated"; theme = "BR"; seed = 202611; players = 2; spawnMode = "Separated"; expectNCells = 360; expectDvMin = 2; triggerLive = $true },
        @{ label = "AL 3P Separated"; theme = "AL"; seed = 202631; players = 3; spawnMode = "Separated"; expectNCells = 416; expectDvMin = 3; triggerLive = $true },
        @{ label = "VO 3P Clustered"; theme = "VO"; seed = 202661; players = 3; spawnMode = "Clustered"; expectNCells = 416; expectDvMin = 3; triggerLive = $false },
        @{ label = "DS 2P Separated"; theme = "DS"; seed = 202641; players = 2; spawnMode = "Separated"; expectNCells = 360; expectDvMin = 2; triggerLive = $false }
    )

    $observedTendencies = @()
    foreach ($c in $cases) {
        Write-Host "[5/6] Generating & Auditing: $($c.label) (seed=$($c.seed))..."
        $modeStr = if ($c.players -ge 3) { "mf3p" } else { "mf2p" }
        $genRes = Invoke-Control 27901 "start_survival_gen" @{
            theme = $c.theme
            seed = $c.seed
            mode = $modeStr
            spawnMode = $c.spawnMode
            factorPop = 1.0
            factorDuration = 1.0
        }
        Assert-True ($genRes.ok) "start_survival_gen failed for $($c.label)"
        Wait-Until -TimeoutSec 90 -IntervalMs 300 -Description "$($c.label) generation complete" -Condition {
            $st = Try-Control 27901 "lobby_state"
            if ($st -and -not $st.survivalGenBusy) {
                if (-not $st.survivalGenOk) { throw "Survival gen error ($($c.label)): $($st.survivalGenError)" }
                return ($st.inLevel -and -not $st.loading)
            }
            return $false
        } | Out-Null
        Start-Sleep -Milliseconds 400

        $m = Invoke-Control 27901 "map_metrics" @{}
        Assert-True ($m.survivalNCells -eq $c.expectNCells) "$($c.label): expected NCells=$($c.expectNCells), got $($m.survivalNCells)"
        Assert-True ($m.commandCenterCount -eq $c.players) "$($c.label): expected $($c.players) CCs, got $($m.commandCenterCount)"
        Assert-True ($m.spawnMode -eq $c.spawnMode) "$($c.label): expected spawnMode=$($c.spawnMode), got $($m.spawnMode)"
        Assert-True ($m.allCcsConnected -eq $true) "$($c.label): all CCs must be walkable-connected!"
        Assert-True ($m.doomVillages -ge $c.expectDvMin) "$($c.label): expected DoomVillages >= $($c.expectDvMin), got $($m.doomVillages)"

        $minZ = 9999.0
        $sumStone = 0
        $minSep = 9999.0
        foreach ($cc in $m.commandCenters) {
            if ([double]$cc.nearestZombieDist -ge 0 -and [double]$cc.nearestZombieDist -lt $minZ) {
                $minZ = [double]$cc.nearestZombieDist
            }
            if ([double]$cc.minOtherCcDist -lt $minSep) {
                $minSep = [double]$cc.minOtherCcDist
            }
            $sumStone += [int]$cc.stone24
        }
        Assert-True ($minZ -ge 27.5) "$($c.label): zombie too close to starter CC! minZ=$minZ"
        if ($c.spawnMode -eq "Separated") {
            Assert-True ([double]$m.cc1DistFromCenter -ge 25.0) "$($c.label): Separated CC1 must be relocated off center (dist=$($m.cc1DistFromCenter))"
            Assert-True ($minSep -ge 50.0) "$($c.label): Separated CCs too close (minSep=$minSep)"
            Assert-True ($sumStone -ge 4) "$($c.label): Team-wide stone24 must be >= 4 (got $sumStone)"
        } else {
            Assert-True ($minSep -ge 12.0 -and $minSep -le 36.0) "$($c.label): Clustered minSep out of range: $minSep"
        }

        $swAudit = Invoke-Control 27901 "lobby_action" @{ action = "test_swarm_audit"; triggerLive = $c.triggerLive }
        Write-Host ("      SwarmAudit debug: events={0} periodic={1} final={2} edges={3} w1={4} w2={5} merged={6} liveDelta={7} reqs={8} reqUnits={9} uniqEvIds={10} pDict={11} fDict={12} tagged={13} compDelta={14} M={15} G={16}" -f $swAudit.levelEventsCount, $swAudit.foundPeriodicSwarm, $swAudit.foundFinalSwarm, $swAudit.edgeGeneratorCount, $swAudit.wave1Edges, $swAudit.wave2Edges, $swAudit.mergedWavesAcross23LateTrials, $swAudit.livePeriodicDeltaStreams, $swAudit.queuedGeneratorRequests, $swAudit.queuedTotalUnits, $swAudit.queuedUniqueEventIds, $swAudit.periodicDictUnits, $swAudit.finalSwarmDictUnits, $swAudit.taggedTargetPlayerCount, $swAudit.liveCompressedDelta, $swAudit.totalCompressedMutants, $swAudit.totalCompressedGiants)
        Assert-True ($swAudit.multiFactionActive -eq $true) "$($c.label): MultiWaveSwarm must be active"
        Assert-True ($swAudit.wave1Distinct -eq $true) "$($c.label): Wave 1 must split across distinct generators (got $($swAudit.wave1Edges))"
        Assert-True ($swAudit.wave2Distinct -eq $true) "$($c.label): Wave 2 must split across distinct generators (got $($swAudit.wave2Edges))"
        Assert-True ($swAudit.mergedWavesAcross23LateTrials -ge 1) "$($c.label): Expected >=1 merged wave across Waves 3..25 trials (got $($swAudit.mergedWavesAcross23LateTrials))"

        if ($c.triggerLive) {
            Assert-True ($swAudit.livePeriodicDeltaStreams -ge $c.players) "$($c.label): Live periodic wave trigger must dispatch >= $($c.players) streams (got $($swAudit.livePeriodicDeltaStreams))"
            Assert-True ($swAudit.taggedTargetPlayerCount -ge $c.players) "$($c.label): Tagged CUnitGenerator target players should be >= $($c.players) (got $($swAudit.taggedTargetPlayerCount))"
            Assert-True ($swAudit.liveCompressedDelta -ge 1) "$($c.label): Expected FinalSwarm cap compression to trigger (got delta=$($swAudit.liveCompressedDelta))"
            Assert-True ($swAudit.totalCompressedMutants -ge 1 -and $swAudit.totalCompressedGiants -ge 1) "$($c.label): Compressed FinalSwarm should condense Mutant+Giant (M=$($swAudit.totalCompressedMutants), G=$($swAudit.totalCompressedGiants))"
        }

        $observedTendencies += $m.mutationTendency
        Write-Host ("      PASS $($c.label): tendency=$($m.mutationTendency) minSep={0:F1} minZ={1:F1} stone24Sum=$sumStone DV=$($m.doomVillages) T3(C/V/H=$($m.chubbyZombies)/$($m.venomZombies)/$($m.harpyZombies)) Giants=$($m.giants) Mutants=$($m.mutants) SwAudit=(W1:$($swAudit.wave1Edges),W2:$($swAudit.wave2Edges),Merged:$($swAudit.mergedWavesAcross23LateTrials),Comp:$($swAudit.totalCompressedWaves))" -f $minSep, $minZ)
    }

    Write-Host "[6/6] Observed tendencies across test seeds: $($observedTendencies -join ', ')"
    Write-Host "ALL M4 VERIFICATION & SWARM AUDIT ASSERTIONS PASSED!"
}
finally {
    Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-ChildItem -Path $savesDir -Filter "tabcoop_m4_*" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}
