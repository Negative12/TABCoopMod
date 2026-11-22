param(
    [string]$GameDir = "",
    [string]$SaveName = "new_survival",
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

function Pause-Synced {
    param([int]$TimeoutSec = 15, [string]$Reason = "Paused on Host & Client")
    Invoke-Control 27901 "set_paused" @{ paused = $true } | Out-Null
    Wait-Until -TimeoutSec $TimeoutSec -Description $Reason -Condition {
        $h = Invoke-Control 27901 "game_state"
        $c = Invoke-Control 27902 "game_state"
        return ($h.paused -and $c.paused)
    } | Out-Null
}

function Resume-Synced {
    param([int]$TimeoutSec = 15, [string]$Reason = "Resumed on Host & Client")
    Invoke-Control 27901 "set_paused" @{ paused = $false } | Out-Null
    Wait-Until -TimeoutSec $TimeoutSec -Description $Reason -Condition {
        $h = Invoke-Control 27901 "game_state"
        $c = Invoke-Control 27902 "game_state"
        return ((-not $h.paused) -and (-not $c.paused))
    } | Out-Null
}

function Grant-ResourcesSynced {
    param([int]$PlayerId, [int]$Gold = 0, [int]$Wood = 0, [int]$Stone = 0, [int]$Iron = 0, [int]$Oil = 0, [int]$ExtraStorage = 0, [int]$ExtraWorkers = 0, [int]$ExtraColonists = 0)
    Pause-Synced -Reason "Paused before synchronized resource grant for P$PlayerId"
    Invoke-Control 27901 "test_grant_resources" @{ playerId = $PlayerId; gold = $Gold; wood = $Wood; stone = $Stone; iron = $Iron; oil = $Oil; extraStorage = $ExtraStorage; extraWorkers = $ExtraWorkers; extraColonists = $ExtraColonists } | Out-Null
    Invoke-Control 27902 "test_grant_resources" @{ playerId = $PlayerId; gold = $Gold; wood = $Wood; stone = $Stone; iron = $Iron; oil = $Oil; extraStorage = $ExtraStorage; extraWorkers = $ExtraWorkers; extraColonists = $ExtraColonists } | Out-Null
    Resume-Synced -Reason "Resumed after synchronized resource grant for P$PlayerId"
}

function Place-NearCc {
    param([int]$Port, [uint64]$BuilderId, [int]$Cx, [int]$Cy, [string]$BuildingType, [int]$SizeW, [int]$SizeH, [int]$WaitMs = 3500)
    $shortType = $BuildingType -replace "^ZX\.Entities\.", ""
    $beforeList = (Invoke-Control $Port "list_entities" @{ typeContains = $shortType; onlyPlayer = $true; limit = 100 }).entities
    $beforeIds = @{}
    foreach ($be in $beforeList) { $beforeIds[$be.id] = $true }
    $spot = Invoke-Control $Port "find_build_spot" @{ buildingType = $BuildingType; cx = $Cx; cy = $Cy; sizeW = $SizeW; sizeH = $SizeH; maxRadius = 28 }
    if ($spot -and $spot.found) {
        $useW = if ($spot.PSObject.Properties["sizeW"]) { [int]$spot.sizeW } else { $SizeW }
        $useH = if ($spot.PSObject.Properties["sizeH"]) { [int]$spot.sizeH } else { $SizeH }
        Write-Host "      [Place-NearCc] Spot found for $BuildingType (${useW}x${useH}) at ($($spot.x), $($spot.y))"
        Invoke-Control $Port "place_building" @{ buildingType = $BuildingType; builderId = $BuilderId; x = $spot.x; y = $spot.y; sizeW = $useW; sizeH = $useH } | Out-Null
        $waitSw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($waitSw.Elapsed.TotalMilliseconds -lt $WaitMs) {
            Start-Sleep -Milliseconds 250
            $after = (Invoke-Control $Port "list_entities" @{ typeContains = $shortType; onlyPlayer = $true; limit = 100 }).entities
            if ($after.Count -gt $beforeList.Count) {
                $newEnt = $after | Where-Object { -not $beforeIds.ContainsKey($_.id) } | Select-Object -First 1
                return $newEnt
            }
        }
    } else {
        Write-Host "      [Place-NearCc] find_build_spot returned found=false for $BuildingType around ($Cx,$Cy)"
        return $null
    }
    return $null
}

function Place-NearCcAutoColonists {
    param([int]$Port, [uint64]$BuilderId, [int]$Cx, [int]$Cy, [string]$BuildingType, [int]$SizeW, [int]$SizeH, [int]$ColonistBoost = 600)
    $placed = Place-NearCc -Port $Port -BuilderId $BuilderId -Cx $Cx -Cy $Cy -BuildingType $BuildingType -SizeW $SizeW -SizeH $SizeH
    if ($null -ne $placed) { return $placed }
    # Retry inside a 60-tick safe window with temporary Colonists if MinColonistsRequisite blocked placement.
    Wait-Until -TimeoutSec 10 -IntervalMs 100 -Description "Align just past tick % 60 before temporary $ColonistBoost colonists for $BuildingType" -Condition {
        $gs = Invoke-Control 27901 "game_state"
        $rem = [int]$gs.tick % 60
        return ($rem -ge 2 -and $rem -le 12)
    } | Out-Null
    $targetPid = if ($Port -eq 27902) { 2 } else { 1 }
    Grant-ResourcesSynced -PlayerId $targetPid -ExtraColonists $ColonistBoost
    $placed = Place-NearCc -Port $Port -BuilderId $BuilderId -Cx $Cx -Cy $Cy -BuildingType $BuildingType -SizeW $SizeW -SizeH $SizeH -WaitMs 1400
    Grant-ResourcesSynced -PlayerId $targetPid -ExtraColonists (-$ColonistBoost)
    return $placed
}

function Ensure-BuildingCompleted {
    param([int]$Port, [uint64]$BuilderId, [int]$Cx, [int]$Cy, [string]$BuildingType, [int]$SizeW = 3, [int]$SizeH = 3)
    $shortType = $BuildingType -replace "^ZX\.Entities\.", ""
    $existing = (Invoke-Control $Port "list_entities" @{ typeContains = $shortType; onlyPlayer = $true; limit = 100 }).entities | Where-Object { $_.owner -eq 1 } | Select-Object -First 1
    if ($null -eq $existing) {
        $existing = Place-NearCcAutoColonists -Port $Port -BuilderId $BuilderId -Cx $Cx -Cy $Cy -BuildingType $BuildingType -SizeW $SizeW -SizeH $SizeH
        if ($null -eq $existing) { return $null }
    }
    $targetId = $existing.id
    Invoke-Control 27901 "set_game_speed" @{ speed = 12.0 } | Out-Null
    $script:ebPoll = 0
    Wait-Until -TimeoutSec 200 -Description "$shortType ($targetId) construction completes" -Condition {
        $script:ebPoll++
        $gsNow = Invoke-Control 27901 "game_state"
        if ($gsNow.paused -and -not $gsNow.levelEnded) {
            Invoke-Control 27901 "set_paused" @{ paused = $false } | Out-Null
            Invoke-Control 27901 "set_game_speed" @{ speed = 12.0 } | Out-Null
        }
        if (($script:ebPoll % 8) -eq 1) {
            $csNow = Invoke-Control 27902 "game_state"
            Write-Host ("      [Build $shortType] tH={0}(spd={1},frame={2}ms,logic={3}ms,phys={4}ms) tC={5}(frame={6}ms,logic={7}ms)" -f $gsNow.tick, $gsNow.gameSpeed, $gsNow.avgFrameMs, $gsNow.avgLogicMs, $gsNow.avgPhysMs, $csNow.tick, $csNow.avgFrameMs, $csNow.avgLogicMs)
        }
        $eNow = (Invoke-Control $Port "list_entities" @{ typeContains = $shortType; onlyPlayer = $true; limit = 100 }).entities | Where-Object { $_.id -eq $targetId } | Select-Object -First 1
        return ($null -ne $eNow -and $eNow.enabled)
    } | Out-Null
    Invoke-Control 27901 "set_game_speed" @{ speed = 1.0 } | Out-Null
    return $existing
}

function Research-TechOnBuilding {
    param([uint64]$BuildingId, [string]$TechCommandName)
    $fsPre = Invoke-Control 27901 "faction_state"
    $prevTechs = [int](($fsPre.players | Where-Object { $_.playerId -eq 1 }).onlyOneCmdCount)
    Write-Host "      Researching $TechCommandName on building $BuildingId (prevTechs=$prevTechs)..."
    Invoke-Control 27901 "issue_command" @{ type = 1; playerId = 1; commandName = $TechCommandName; ids = @($BuildingId) } | Out-Null
    Invoke-Control 27901 "set_game_speed" @{ speed = 12.0 } | Out-Null
    $script:rtPoll = 0
    Wait-Until -TimeoutSec 200 -Description "Tech $TechCommandName completes" -Condition {
        $script:rtPoll++
        $gsNow = Invoke-Control 27901 "game_state"
        if ($gsNow.paused -and -not $gsNow.levelEnded) {
            Invoke-Control 27901 "set_paused" @{ paused = $false } | Out-Null
            Invoke-Control 27901 "set_game_speed" @{ speed = 12.0 } | Out-Null
        }
        if (($script:rtPoll % 8) -eq 1) {
            Write-Host ("      [Tech $TechCommandName] tH={0}(spd={1},frame={2}ms,logic={3}ms)" -f $gsNow.tick, $gsNow.gameSpeed, $gsNow.avgFrameMs, $gsNow.avgLogicMs)
        }
        $fsNow = Invoke-Control 27901 "faction_state"
        $nowTechs = [int](($fsNow.players | Where-Object { $_.playerId -eq 1 }).onlyOneCmdCount)
        return ($nowTechs -gt $prevTechs)
    } | Out-Null
    Invoke-Control 27901 "set_game_speed" @{ speed = 1.0 } | Out-Null
}

Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

$savesDir = Join-Path $env:USERPROFILE "Documents\My Games\They Are Billions\Saves"
Get-ChildItem -Path $savesDir -Filter "tabcoop_m23_test*" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

$shotRoot = Join-Path (Get-Location) "tabcoop_shots"
foreach ($sub in @("mf23_host", "mf23_client")) {
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
    Write-Host "[1/7] Launching Host (27901) & Client (27902)..."
    $hostProc = Start-TabInstance -Role "mf23_host" -Port 27901
    Start-Sleep -Milliseconds 1500
    $clientProc = Start-TabInstance -Role "mf23_client" -Port 27902

    Wait-Until -TimeoutSec $BootTimeoutSec -Description "Both mainMenuReady" -Condition {
        $h = Try-Control 27901 "lobby_state"
        $c = Try-Control 27902 "lobby_state"
        return ($h -and $c -and $h.mainMenuReady -and $c.mainMenuReady)
    } | Out-Null

    Write-Host "[2/7] Creating MultiFaction room & loading '$SaveName'..."
    Invoke-Control 27901 "open_lobby" | Out-Null
    Invoke-Control 27901 "lobby_action" @{ action = "host_loopback" } | Out-Null
    Invoke-Control 27901 "lobby_action" @{ action = "toggle_game_mode" } | Out-Null
    Invoke-Control 27902 "open_lobby" | Out-Null
    Invoke-Control 27902 "lobby_action" @{ action = "join_loopback" } | Out-Null
    Wait-Until -TimeoutSec 15 -Description "Client joined in MultiFaction mode" -Condition {
        $c = Invoke-Control 27902 "lobby_state"
        return ($c.peers.Count -eq 2 -and $c.coopGameMode -eq "MultiFaction")
    } | Out-Null
    Invoke-Control 27902 "lobby_action" @{ action = "toggle_ready" } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "Host canStart" -Condition { (Invoke-Control 27901 "lobby_state").canStart } | Out-Null
    if ($SaveName -eq "new_survival") {
        Invoke-Control 27901 "lobby_action" @{ action = "open_new_survival" } | Out-Null
        Wait-Until -TimeoutSec 10 -Description "Save name InputBox open" -Condition {
            $s = Invoke-Control 27901 "lobby_state"
            return ($s.anyModalOpen -and -not $s.lobbyWindowOpen -and $s.modalHasEditBox)
        } | Out-Null
        $c1 = Invoke-Control 27901 "lobby_action" @{ action = "confirm_modal"; inputText = "tabcoop_m23_test" }
        Assert-True ($c1.ok) "Failed to confirm save name modal"
        Wait-Until -TimeoutSec 10 -Description "New Survival config window open" -Condition {
            $s = Try-Control 27901 "lobby_state"
            return ($s -and $s.anyModalOpen -and -not $s.lobbyWindowOpen -and -not $s.modalHasEditBox)
        } | Out-Null
        Start-Sleep -Milliseconds 150
        $c2 = Invoke-Control 27901 "lobby_action" @{ action = "confirm_modal" }
        Assert-True ($c2.ok) "Failed to start New Survival"
    } else {
        Invoke-Control 27901 "lobby_action" @{ action = "load_save"; save = $SaveName } | Out-Null
    }
    Wait-Until -TimeoutSec $LoadTimeoutSec -IntervalMs 500 -Description "Both in synced level" -Condition {
        $h = Try-Control 27901 "lobby_state"
        $c = Try-Control 27902 "lobby_state"
        if ($SaveName -eq "new_survival") {
            if ($h -and -not $h.inLevel) { Try-Control 27901 "lobby_action" @{ action = "kick_fade" } | Out-Null }
            if ($c -and -not $c.inLevel) { Try-Control 27902 "lobby_action" @{ action = "kick_fade" } | Out-Null }
        }
        return ($h -and $c -and $h.inLevel -and -not $h.loading -and $c.inLevel -and -not $c.loading)
    } | Out-Null
    Start-Sleep -Milliseconds 1200

    $ccs = (Invoke-Control 27901 "list_entities" @{ typeContains = "CommandCenter"; onlyPlayer = $true }).entities
    $cc1 = $ccs | Where-Object { $_.owner -eq 1 } | Select-Object -First 1
    $cc2 = $ccs | Where-Object { $_.owner -eq 2 } | Select-Object -First 1
    Assert-True ($null -ne $cc1 -and $null -ne $cc2) "Both CC1 and CC2 must exist"

    Write-Host "[3/7] Verifying per-player ledger isolation & matching Host/Client economyHash..."
    $fs0H = Invoke-Control 27901 "faction_state"
    $fs0C = Invoke-Control 27902 "faction_state"
    Assert-True ($fs0H.initialized -and $fs0C.initialized) "FactionContextManager must be initialized on both peers"
    Assert-True ($fs0H.economyHash -eq $fs0C.economyHash) "Initial economyHash must match ($($fs0H.economyHash) vs $($fs0C.economyHash))"
    $p1Before = $fs0H.players | Where-Object { $_.playerId -eq 1 } | Select-Object -First 1
    $p2Before = $fs0H.players | Where-Object { $_.playerId -eq 2 } | Select-Object -First 1
    Write-Host ("      Initial P1=(G:{0}, W:{1}, Food:{2}, Work:{3}, Eng:{4}) | P2=(G:{5}, W:{6}, Food:{7}, Work:{8}, Eng:{9})" -f `
        $p1Before.gold, $p1Before.wood, $p1Before.foodSupply, $p1Before.workersSupply, $p1Before.energySupply, `
        $p2Before.gold, $p2Before.wood, $p2Before.foodSupply, $p2Before.workersSupply, $p2Before.energySupply)

    $placedTent = Place-NearCc -Port 27902 -BuilderId $cc2.id -Cx ([Math]::Floor($cc2.x)) -Cy ([Math]::Floor($cc2.y)) -BuildingType "ZX.Entities.TentHouse" -SizeW 2 -SizeH 2
    Assert-True ($null -ne $placedTent) "P2 must be able to place TentHouse near CC2"
    Pause-Synced -Reason "Paused after P2 TentHouse before economyHash check"
    $syncPair = Wait-Until -TimeoutSec 10 -Description "Host & Client converge on identical paused tick & economyHash after P2 TentHouse" -Condition {
        $h = Invoke-Control 27901 "faction_state"
        $c = Invoke-Control 27902 "faction_state"
        if ($h.economyHash -eq $c.economyHash) { return @{ h = $h; c = $c } }
        return $null
    }
    $fs1H = $syncPair.h
    $fs1C = $syncPair.c
    Resume-Synced -Reason "Resumed after P2 TentHouse economyHash check"
    $p1AfterTent = $fs1H.players | Where-Object { $_.playerId -eq 1 } | Select-Object -First 1
    $p2AfterTent = $fs1H.players | Where-Object { $_.playerId -eq 2 } | Select-Object -First 1
    Assert-True ($p2AfterTent.gold -lt $p2Before.gold -or $p2AfterTent.wood -lt $p2Before.wood) "P2 TentHouse build must deduct from P2 ledger"
    Assert-True ($p1AfterTent.gold -ge $p1Before.gold -and $p1AfterTent.wood -ge $p1Before.wood) "P1 ledger must NOT be charged for P2 TentHouse"
    Assert-True ($fs1H.economyHash -eq $fs1C.economyHash) "EconomyHash must remain identical after P2 build ($($fs1H.economyHash) vs $($fs1C.economyHash))"

    Write-Host "[4/7] Verifying Market gate, 20% fee transfer, and CoopMayor_ZeroTransferFee (0% fee)..."
    # Raise storage/workers cleanly below overflow so +80 / +100 additions are never cap-clamped;
    # keep Colonists < 30 here so Survival Mayor modals don't auto-pop before CoopMayor_ZeroTransferFee is tested.
    Grant-ResourcesSynced -PlayerId 1 -Gold 6000 -Wood 300 -Stone 200 -Iron 200 -Oil 100 -ExtraStorage 450 -ExtraWorkers 120
    Grant-ResourcesSynced -PlayerId 2 -Gold 2000 -Wood 200 -Stone 50 -Iron 50 -Oil 20 -ExtraStorage 450 -ExtraWorkers 60

    $fsMkt = Invoke-Control 27901 "faction_state"
    $wwEnt = $null
    if (-not $fsMkt.hasAnyAliveMarket) {
        # Before any Market: transfer must be rejected cleanly
        $p2PreReject = ($fsMkt.players | Where-Object { $_.playerId -eq 2 }).wood
        Invoke-Control 27902 "transfer_resource" @{ toPlayer = 1; resource = "Wood"; amount = 10 } | Out-Null
        Start-Sleep -Milliseconds 1000
        $afterNoMkt = Invoke-Control 27901 "faction_state"
        $p2PostReject = ($afterNoMkt.players | Where-Object { $_.playerId -eq 2 }).wood
        Assert-True ($p2PostReject -ge $p2PreReject) "Transfer before any Market exists must be rejected"

        $placedMarket = Place-NearCc -Port 27901 -BuilderId $cc1.id -Cx ([Math]::Floor($cc1.x)) -Cy ([Math]::Floor($cc1.y)) -BuildingType "ZX.Entities.Market" -SizeW 3 -SizeH 3
        if ($null -eq $placedMarket) {
            # In New Survival, unlock TechMarket via WoodWorkshop after adding MillWood + TeslaTower room
            $null = Ensure-BuildingCompleted -Port 27901 -BuilderId $cc1.id -Cx ([Math]::Floor($cc1.x)) -Cy ([Math]::Floor($cc1.y)) -BuildingType "ZX.Entities.MillWood" -SizeW 2 -SizeH 2
            $null = Ensure-BuildingCompleted -Port 27901 -BuilderId $cc1.id -Cx ([Math]::Floor($cc1.x)) -Cy ([Math]::Floor($cc1.y)) -BuildingType "ZX.Entities.EnergyWoodTower" -SizeW 1 -SizeH 1
            $wwEnt = Ensure-BuildingCompleted -Port 27901 -BuilderId $cc1.id -Cx ([Math]::Floor($cc1.x)) -Cy ([Math]::Floor($cc1.y)) -BuildingType "ZX.Entities.WoodWorkshop" -SizeW 3 -SizeH 3
            Assert-True ($null -ne $wwEnt) "Must be able to build WoodWorkshop near CC1"
            Research-TechOnBuilding -BuildingId ([uint64]$wwEnt.id) -TechCommandName "ZX.Commands.TechMarket"
            $placedMarket = Place-NearCcAutoColonists -Port 27901 -BuilderId $cc1.id -Cx ([Math]::Floor($cc1.x)) -Cy ([Math]::Floor($cc1.y)) -BuildingType "ZX.Entities.Market" -SizeW 3 -SizeH 3 -ColonistBoost 250
        }
        Assert-True ($null -ne $placedMarket) "Must be able to place Market near CC1"
        Invoke-Control 27901 "set_game_speed" @{ speed = 12.0 } | Out-Null
        Wait-Until -TimeoutSec 150 -Description "P1 Market construction completes and unlocks team transfer" -Condition {
            $fs = Invoke-Control 27901 "faction_state"
            return $fs.hasAnyAliveMarket
        } | Out-Null
        Invoke-Control 27901 "set_game_speed" @{ speed = 1.0 } | Out-Null
        Start-Sleep -Milliseconds 500
        $fsMkt = Invoke-Control 27901 "faction_state"
    }

    Assert-True $fsMkt.hasAnyAliveMarket "Team Market must be active"
    $p1Mkt = ($fsMkt.players | Where-Object { $_.playerId -eq 1 })
    $p2Mkt = ($fsMkt.players | Where-Object { $_.playerId -eq 2 })
    Assert-True ($p1Mkt.nMarkets -ge 1 -and $p2Mkt.nMarkets -eq 0) "Only P1 owns a Market (P1=$($p1Mkt.nMarkets), P2=$($p2Mkt.nMarkets)), yet P2 can transfer"

    # P2 transfers 100 Gold -> P1 at 20% fee (P1 receives exact +80!)
    Pause-Synced -Reason "Paused before 20% fee Gold transfer snapshot"
    $preT1 = Invoke-Control 27901 "faction_state"
    $g1Pre = ($preT1.players | Where-Object { $_.playerId -eq 1 }).gold
    $g2Pre = ($preT1.players | Where-Object { $_.playerId -eq 2 }).gold
    Resume-Synced -Reason "Resumed before 20% fee Gold transfer"
    Invoke-Control 27902 "transfer_resource" @{ toPlayer = 1; resource = "Gold"; amount = 100 } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "20% fee transfer settles on both Host & Client" -Condition {
        $fs = Invoke-Control 27901 "faction_state"
        $g1Now = ($fs.players | Where-Object { $_.playerId -eq 1 }).gold
        return ($g1Now -ge ($g1Pre + 80))
    } | Out-Null
    $postT1 = Invoke-Control 27901 "faction_state"
    $g1Post = ($postT1.players | Where-Object { $_.playerId -eq 1 }).gold
    $g2Post = ($postT1.players | Where-Object { $_.playerId -eq 2 }).gold
    Write-Host "      20% fee transfer P2->P1 (100 Gold): P2 ($g2Pre -> $g2Post), P1 ($g1Pre -> $g1Post, net +$($g1Post - $g1Pre))"
    Assert-True ($g2Pre - $g2Post -ge 100) "P2 must pay 100 Gold gross"
    Assert-True ($g1Post - $g1Pre -eq 80) "P1 must receive exact +80 Gold under 20% Market fee (got +$($g1Post - $g1Pre))"

    # Apply custom Mayor CoopMayor_ZeroTransferFee -> verify fee drops to 0% (50 -> +50!)
    Invoke-Control 27901 "mayor_select" @{ mayorId = "CoopMayor_ZeroTransferFee" } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "ZeroTransferFee mayor synced on both peers" -Condition {
        $h = Invoke-Control 27901 "faction_state"
        $c = Invoke-Control 27902 "faction_state"
        return ($h.zeroTransferFeeActive -and $c.zeroTransferFeeActive)
    } | Out-Null
    Pause-Synced -Reason "Paused before 0% fee Wood transfer snapshot"
    $preT2 = Invoke-Control 27901 "faction_state"
    $w1Pre = ($preT2.players | Where-Object { $_.playerId -eq 1 }).wood
    $w2Pre = ($preT2.players | Where-Object { $_.playerId -eq 2 }).wood
    Resume-Synced -Reason "Resumed before 0% fee Wood transfer"
    Invoke-Control 27902 "transfer_resource" @{ toPlayer = 1; resource = "Wood"; amount = 50 } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "0% fee Wood transfer settles cleanly" -Condition {
        $fs = Invoke-Control 27901 "faction_state"
        $w1Now = ($fs.players | Where-Object { $_.playerId -eq 1 }).wood
        return ($w1Now -ge ($w1Pre + 50))
    } | Out-Null
    $postT2 = Invoke-Control 27901 "faction_state"
    $w1Post = ($postT2.players | Where-Object { $_.playerId -eq 1 }).wood
    Write-Host "      0% fee transfer P2->P1 (50 Wood): P1 ($w1Pre -> $w1Post, net +$($w1Post - $w1Pre))"
    Assert-True ($w1Post - $w1Pre -ge 50) "P1 must receive full 50 Wood (0% fee) after CoopMayor_ZeroTransferFee"

    Write-Host "[5/7] Verifying 2-Phase Functional Wonder Crowdfunding (Spire 1.5x scaling, withdraw, ReadyVoucher & Shared Energy Grid)..."
    $ws0 = Invoke-Control 27901 "wonder_status"
    $spire0 = $ws0.wonders | Where-Object { $_.kind -eq "Spire" } | Select-Object -First 1
    Assert-True ($ws0.playerCount -eq 2 -and $ws0.scaledSpireEnergy -eq 1200) "2P Spire energy must scale 1.5x -> 1200 (got $($ws0.scaledSpireEnergy))"
    Write-Host ("      Spire Targets (1.5x): Research=({0}G/{1}W/{2}S/{3}I/{4}O), Build=({5}G/{6}W/{7}S/{8}I/{9}O), ScaledEnergy={10}" -f `
        $spire0.researchTarget.gold, $spire0.researchTarget.wood, $spire0.researchTarget.stone, $spire0.researchTarget.iron, $spire0.researchTarget.oil, `
        $spire0.buildTarget.gold, $spire0.buildTarget.wood, $spire0.buildTarget.stone, $spire0.buildTarget.iron, $spire0.buildTarget.oil, $ws0.scaledSpireEnergy)

    # Grant enough Gold/Stone/Iron/Oil & Workers for 1.5x Wonder research + construction (both phases)
    Grant-ResourcesSynced -PlayerId 1 -Gold 26000 -Wood 400 -Stone 500 -Iron 500 -Oil 300 -ExtraStorage 400 -ExtraWorkers 120
    Grant-ResourcesSynced -PlayerId 2 -Gold 26000 -Wood 400 -Stone 500 -Iron 500 -Oil 300 -ExtraStorage 400 -ExtraWorkers 120

    # Ensure team has at least one completed Foundry before Spire ResearchFunding (required workshop gate)
    if (-not $spire0.hasRequiredWorkshop) {
        Invoke-Control 27901 "wonder_contribute" @{ wonder = "Spire"; resource = "Gold"; amount = 500 } | Out-Null
        Start-Sleep -Milliseconds 600
        $wsBlocked = (Invoke-Control 27901 "wonder_status").wonders | Where-Object { $_.kind -eq "Spire" } | Select-Object -First 1
        Assert-True ($wsBlocked.researchContributed.gold -eq 0) "Spire ResearchFunding must be blocked when team lacks a Foundry"

        $fsPreFoundry = Invoke-Control 27901 "faction_state"
        $p1PreFoundry = $fsPreFoundry.players | Where-Object { $_.playerId -eq 1 } | Select-Object -First 1
        $freeEnergy1 = [int]$p1PreFoundry.energySupply - [int]$p1PreFoundry.energyNeeded
        $millAttempt = 0
        while ($freeEnergy1 -lt 90 -and $millAttempt -lt 5) {
            $millAttempt++
            Write-Host "      P1 free energy=$freeEnergy1 (<90); building MillWood #$millAttempt near CC1 before Foundry..."
            $placedMill = Place-NearCc -Port 27901 -BuilderId $cc1.id -Cx ([Math]::Floor($cc1.x)) -Cy ([Math]::Floor($cc1.y)) -BuildingType "ZX.Entities.MillWood" -SizeW 2 -SizeH 2
            Assert-True ($null -ne $placedMill) "Must be able to place MillWood #$millAttempt near CC1 for Foundry energy"
            $supplyBeforeMill = [int]$p1PreFoundry.energySupply
            Invoke-Control 27901 "set_game_speed" @{ speed = 12.0 } | Out-Null
            Wait-Until -TimeoutSec 90 -Description "MillWood #$millAttempt completes and raises energySupply" -Condition {
                $gsNow = Invoke-Control 27901 "game_state"
                if ($gsNow.paused -and -not $gsNow.levelEnded) {
                    Invoke-Control 27901 "set_paused" @{ paused = $false } | Out-Null
                    Invoke-Control 27901 "set_game_speed" @{ speed = 12.0 } | Out-Null
                }
                $fsNow = Invoke-Control 27901 "faction_state"
                $p1Now = $fsNow.players | Where-Object { $_.playerId -eq 1 } | Select-Object -First 1
                $supNow = [int]$p1Now.energySupply
                $feNow = $supNow - [int]$p1Now.energyNeeded
                return ($supNow -gt $supplyBeforeMill -or $feNow -ge 90)
            } | Out-Null
            Invoke-Control 27901 "set_game_speed" @{ speed = 1.0 } | Out-Null
            $fsPreFoundry = Invoke-Control 27901 "faction_state"
            $p1PreFoundry = $fsPreFoundry.players | Where-Object { $_.playerId -eq 1 } | Select-Object -First 1
            $freeEnergy1 = [int]$p1PreFoundry.energySupply - [int]$p1PreFoundry.energyNeeded
            Write-Host "      After MillWood #${millAttempt}: supply=$($p1PreFoundry.energySupply), needed=$($p1PreFoundry.energyNeeded), free=$freeEnergy1"
        }
        Assert-True ($freeEnergy1 -ge 90) "P1 free energy must be >= 90 before StoneWorkshop/Foundry (got $freeEnergy1)"

        $placedFoundry = Place-NearCc -Port 27901 -BuilderId $cc1.id -Cx ([Math]::Floor($cc1.x)) -Cy ([Math]::Floor($cc1.y)) -BuildingType "ZX.Entities.Foundry" -SizeW 3 -SizeH 3
        if ($null -eq $placedFoundry) {
            if ($null -eq $wwEnt) {
                $wwEnt = Ensure-BuildingCompleted -Port 27901 -BuilderId $cc1.id -Cx ([Math]::Floor($cc1.x)) -Cy ([Math]::Floor($cc1.y)) -BuildingType "ZX.Entities.WoodWorkshop" -SizeW 3 -SizeH 3
                Assert-True ($null -ne $wwEnt) "Must have WoodWorkshop to research StoneWorkshop"
            }
            $swExisting = (Invoke-Control 27901 "list_entities" @{ typeContains = "StoneWorkshop"; onlyPlayer = $true; limit = 20 }).entities | Where-Object { $_.owner -eq 1 } | Select-Object -First 1
            if ($null -eq $swExisting) {
                Research-TechOnBuilding -BuildingId ([uint64]$wwEnt.id) -TechCommandName "ZX.Commands.TechStoneWorkshop"
                $swExisting = Ensure-BuildingCompleted -Port 27901 -BuilderId $cc1.id -Cx ([Math]::Floor($cc1.x)) -Cy ([Math]::Floor($cc1.y)) -BuildingType "ZX.Entities.StoneWorkshop" -SizeW 3 -SizeH 3
            } else {
                $swExisting = Ensure-BuildingCompleted -Port 27901 -BuilderId $cc1.id -Cx ([Math]::Floor($cc1.x)) -Cy ([Math]::Floor($cc1.y)) -BuildingType "ZX.Entities.StoneWorkshop" -SizeW 3 -SizeH 3
            }
            Assert-True ($null -ne $swExisting) "Must be able to build StoneWorkshop near CC1"
            Research-TechOnBuilding -BuildingId ([uint64]$swExisting.id) -TechCommandName "ZX.Commands.TechFoundry"
            $placedFoundry = Place-NearCc -Port 27901 -BuilderId $cc1.id -Cx ([Math]::Floor($cc1.x)) -Cy ([Math]::Floor($cc1.y)) -BuildingType "ZX.Entities.Foundry" -SizeW 3 -SizeH 3
        }
        Assert-True ($null -ne $placedFoundry) "Must be able to place Foundry near CC1 to unlock Spire research"
        Invoke-Control 27901 "set_game_speed" @{ speed = 12.0 } | Out-Null
        Wait-Until -TimeoutSec 180 -Description "Foundry construction completes and unlocks Spire research" -Condition {
            $s = (Invoke-Control 27901 "wonder_status").wonders | Where-Object { $_.kind -eq "Spire" } | Select-Object -First 1
            return $s.hasRequiredWorkshop
        } | Out-Null
        Invoke-Control 27901 "set_game_speed" @{ speed = 1.0 } | Out-Null
    }

    # Phase 1: Research Funding split P1 + P2 across all required resources
    foreach ($pair in @(
        @("Gold", $spire0.researchTarget.gold),
        @("Wood", $spire0.researchTarget.wood),
        @("Stone", $spire0.researchTarget.stone),
        @("Iron", $spire0.researchTarget.iron),
        @("Oil", $spire0.researchTarget.oil)
    )) {
        $rName = $pair[0]; $tot = [int]$pair[1]
        if ($tot -le 0) { continue }
        $p1Part = [Math]::Floor($tot / 2)
        $p2Part = $tot - $p1Part
        if ($p1Part -gt 0) { Invoke-Control 27901 "wonder_contribute" @{ wonder = "Spire"; resource = $rName; amount = $p1Part } | Out-Null }
        if ($p2Part -gt 0) { Invoke-Control 27902 "wonder_contribute" @{ wonder = "Spire"; resource = $rName; amount = $p2Part } | Out-Null }
    }
    Wait-Until -TimeoutSec 10 -Description "Spire Research funding completes -> Researching countdown" -Condition {
        $wsH = Invoke-Control 27901 "wonder_status"
        $wsC = Invoke-Control 27902 "wonder_status"
        $sH = $wsH.wonders | Where-Object { $_.kind -eq "Spire" } | Select-Object -First 1
        $sC = $wsC.wonders | Where-Object { $_.kind -eq "Spire" } | Select-Object -First 1
        return ($sH.phase -eq "Researching" -and $sC.phase -eq "Researching" -and $sH.researchTicksTotal -gt 0)
    } | Out-Null
    Invoke-Control 27901 "set_game_speed" @{ speed = 10.0 } | Out-Null
    Wait-Until -TimeoutSec 180 -Description "Spire Research countdown completes -> BuildFunding" -Condition {
        $wsH = Invoke-Control 27901 "wonder_status"
        $wsC = Invoke-Control 27902 "wonder_status"
        $sH = $wsH.wonders | Where-Object { $_.kind -eq "Spire" } | Select-Object -First 1
        $sC = $wsC.wonders | Where-Object { $_.kind -eq "Spire" } | Select-Object -First 1
        return ($sH.phase -eq "BuildFunding" -and $sC.phase -eq "BuildFunding" -and $sH.researchUnlocked -and $sC.researchUnlocked)
    } | Out-Null
    Invoke-Control 27901 "set_game_speed" @{ speed = 1.0 } | Out-Null

    # Phase 2: Withdraw verification
    Invoke-Control 27901 "wonder_contribute" @{ wonder = "Spire"; resource = "Gold"; amount = 200 } | Out-Null
    Wait-Until -TimeoutSec 8 -Description "Spire BuildFunding has 200 Gold" -Condition {
        $s = (Invoke-Control 27901 "wonder_status").wonders | Where-Object { $_.kind -eq "Spire" } | Select-Object -First 1
        return ($s.buildContributed.gold -eq 200)
    } | Out-Null
    Invoke-Control 27901 "wonder_withdraw" @{ wonder = "Spire"; resource = "Gold"; amount = 200 } | Out-Null
    Wait-Until -TimeoutSec 8 -Description "Spire BuildFunding withdraw back to 0 Gold" -Condition {
        $s = (Invoke-Control 27901 "wonder_status").wonders | Where-Object { $_.kind -eq "Spire" } | Select-Object -First 1
        return ($s.buildContributed.gold -eq 0)
    } | Out-Null

    # Open MultiFactionPanel for UI screenshot during active crowdfunding!
    Invoke-Control 27901 "toggle_mf_panel" @{ open = $true; tab = 0 } | Out-Null
    Start-Sleep -Milliseconds 350
    Invoke-Control 27901 "screenshot" @{ tag = "01_panel_market_tab" } | Out-Null
    Invoke-Control 27901 "toggle_mf_panel" @{ open = $true; tab = 1; wonder = "Spire" } | Out-Null
    Start-Sleep -Milliseconds 350
    Invoke-Control 27901 "screenshot" @{ tag = "02_panel_wonder_tab" } | Out-Null
    Invoke-Control 27901 "toggle_mf_panel" @{ open = $false } | Out-Null

    # Full Build Crowdfunding split between P1 & P2 -> ReadyVoucher
    foreach ($pair in @(
        @("Gold", $spire0.buildTarget.gold),
        @("Wood", $spire0.buildTarget.wood),
        @("Stone", $spire0.buildTarget.stone),
        @("Iron", $spire0.buildTarget.iron),
        @("Oil", $spire0.buildTarget.oil)
    )) {
        $rName = $pair[0]; $tot = [int]$pair[1]
        if ($tot -le 0) { continue }
        $p1Part = [Math]::Floor($tot / 2)
        $p2Part = $tot - $p1Part
        if ($p1Part -gt 0) { Invoke-Control 27901 "wonder_contribute" @{ wonder = "Spire"; resource = $rName; amount = $p1Part } | Out-Null }
        if ($p2Part -gt 0) { Invoke-Control 27902 "wonder_contribute" @{ wonder = "Spire"; resource = $rName; amount = $p2Part } | Out-Null }
    }
    Wait-Until -TimeoutSec 12 -Description "Spire Build crowdfunding completes -> ReadyVoucher" -Condition {
        $wsH = Invoke-Control 27901 "wonder_status"
        $wsC = Invoke-Control 27902 "wonder_status"
        $sH = $wsH.wonders | Where-Object { $_.kind -eq "Spire" } | Select-Object -First 1
        $sC = $wsC.wonders | Where-Object { $_.kind -eq "Spire" } | Select-Object -First 1
        return ($sH.phase -eq "ReadyVoucher" -and $sC.phase -eq "ReadyVoucher" -and $wsH.wonderHash -eq $wsC.wonderHash)
    } | Out-Null

    # P1 (or P2 if P1's crowded Day-11 base lacks a 4x4+margin plot in Tesla grid) places TheSpire using ReadyVoucher (0 extra resource cost!)
    $fsBeforeSpirePlace = Invoke-Control 27901 "faction_state"
    $g1BeforeSpire = ($fsBeforeSpirePlace.players | Where-Object { $_.playerId -eq 1 }).gold
    $spireBuilderPid = 1
    $placedSpire = Place-NearCcAutoColonists -Port 27901 -BuilderId $cc1.id -Cx ([Math]::Floor($cc1.x)) -Cy ([Math]::Floor($cc1.y)) -BuildingType "ZX.Entities.TheSpire" -SizeW 3 -SizeH 3
    if ($null -eq $placedSpire) {
        Write-Host "      CC1 area lacked open Spire footprint; placing TheSpire via P2 near CC2..."
        $spireBuilderPid = 2
        $placedSpire = Place-NearCcAutoColonists -Port 27902 -BuilderId $cc2.id -Cx ([Math]::Floor($cc2.x)) -Cy ([Math]::Floor($cc2.y)) -BuildingType "ZX.Entities.TheSpire" -SizeW 3 -SizeH 3
    }
    Assert-True ($null -ne $placedSpire) "P1 or P2 must be able to place TheSpire with ReadyVoucher"

    Invoke-Control 27901 "set_game_speed" @{ speed = 12.0 } | Out-Null
    $script:pollIdx = 0
    $script:pauseDiagTaken = $false
    Wait-Until -TimeoutSec 220 -IntervalMs 1500 -Description "TheSpire completes construction & activates SharedEnergyGrid (1200 Spire Energy)" -Condition {
        $script:pollIdx++
        $gs = Invoke-Control 27901 "game_state"
        $ws = Invoke-Control 27901 "wonder_status"
        $fsH = Invoke-Control 27901 "faction_state"
        $fsC = Invoke-Control 27902 "faction_state"
        $s = $ws.wonders | Where-Object { $_.kind -eq "Spire" } | Select-Object -First 1
        if (($script:pollIdx % 5) -eq 1 -or $gs.paused) {
            Write-Host "      [BuildPoll #$($script:pollIdx)] tick=$($gs.tick) paused=$($gs.paused) speed=$($gs.gameSpeed) modal=$($gs.inModal) ended=$($gs.levelEnded)/$($gs.endStatus) mayors=$($gs.mayorCount) phase=$($s.phase) shared=$($fsH.sharedEnergyGridActive)"
        }
        if ($gs.paused -and -not $script:pauseDiagTaken) {
            $script:pauseDiagTaken = $true
            Invoke-Control 27901 "screenshot" @{ tag = "paused_diag_host" } | Out-Null
            Invoke-Control 27902 "screenshot" @{ tag = "paused_diag_client" } | Out-Null
            $lm = Try-Control 27901 "list_mayors"
            if ($lm) { Write-Host ("      [PausedDiag] list_mayors: " + ($lm | ConvertTo-Json -Depth 4 -Compress)) }
            # If paused unexpectedly (not by WonderCompleted), unpause so construction can continue unless levelEnded
            if (-not $gs.levelEnded) {
                Invoke-Control 27901 "set_paused" @{ paused = $false } | Out-Null
                Invoke-Control 27901 "set_game_speed" @{ speed = 12.0 } | Out-Null
            }
        }
        return ($s.phase -eq "Completed" -and $fsH.sharedEnergyGridActive -and $fsC.sharedEnergyGridActive -and $fsH.economyHash -eq $fsC.economyHash)
    } | Out-Null
    Invoke-Control 27901 "set_game_speed" @{ speed = 1.0 } | Out-Null
    Start-Sleep -Milliseconds 600

    $fsFinalH = Invoke-Control 27901 "faction_state"
    $fsFinalC = Invoke-Control 27902 "faction_state"
    $p1Spire = $fsFinalH.players | Where-Object { $_.playerId -eq 1 } | Select-Object -First 1
    $p2Spire = $fsFinalH.players | Where-Object { $_.playerId -eq 2 } | Select-Object -First 1
    Write-Host ("      Spire COMPLETED! SharedPool={0}/{1}, P1 Energy={2} (own {3}), P2 Energy={4} (own {5}), Gold1Before={6}->After={7}" -f `
        $fsFinalH.sharedEnergyNeeded, $fsFinalH.sharedEnergySupply, $p1Spire.energySupply, $p1Spire.ownEnergySupply, $p2Spire.energySupply, $p2Spire.ownEnergySupply, $g1BeforeSpire, $p1Spire.gold)
    $builderSpire = if ($spireBuilderPid -eq 2) { $p2Spire } else { $p1Spire }
    Assert-True ($builderSpire.ownEnergySupply -ge 1200) "P$spireBuilderPid Spire must contribute scaled 1200 Energy (got $($builderSpire.ownEnergySupply))"
    Assert-True ($p1Spire.energySupply -eq $fsFinalH.sharedEnergySupply -and $p2Spire.energySupply -eq $fsFinalH.sharedEnergySupply) "Shared Energy Grid must pool EnergySupply across P1 & P2"
    Assert-True ($fsFinalH.economyHash -eq $fsFinalC.economyHash) "Final EconomyHash MUST match between Host & Client ($($fsFinalH.economyHash) vs $($fsFinalC.economyHash))"

    Write-Host "[6/7] Verifying M3 visuals & final checksum match across Host/Client..."
    Invoke-Control 27901 "screenshot" @{ tag = "03_host_final_rings" } | Out-Null
    Invoke-Control 27902 "screenshot" @{ tag = "04_client_spire_rings" } | Out-Null
    $hgEnd = Invoke-Control 27901 "game_state"
    $cgEnd = Invoke-Control 27902 "game_state"
    Write-Host ("      End Tick: Host=({0}, hash={1}) Client=({2}, hash={3})" -f $hgEnd.tick, $hgEnd.fullHash, $cgEnd.tick, $cgEnd.fullHash)

    Write-Host "[7/7] ALL M2 + M3 MULTI-FACTION ASSERTIONS PASSED!"
}
finally {
    if ($hostProc -and -not $hostProc.HasExited) { try { $hostProc.Kill() } catch {} }
    if ($clientProc -and -not $clientProc.HasExited) { try { $clientProc.Kill() } catch {} }
    Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-ChildItem -Path $savesDir -Filter "tabcoop_m23_test*" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    foreach ($logName in @("TABCoopMod.mf23_host.log", "TABCoopMod.mf23_client.log")) {
        $p = Join-Path $GameDir $logName
        if (Test-Path $p) {
            Write-Host "`n--- $logName summary ---"
            Select-String -Path $p -Pattern "ALL BINDINGS|Classes OK|ERROR|DESYNC|WonderCrowdfund|ZeroTransferFee|FactionContext|Transpiled" | Select-Object -First 25 | ForEach-Object { Write-Host $_.Line }
        }
    }
}
