# Steam P2P 多阵营实测 Bug 排查与修复方案（2026-11-20，v0.2.0）

> 对应联机日志：[TABCoopMod_host_5.log](file:///d:/ClaudeCodeProject/TABCoopMod/logs/TABCoopMod_host_5.log)（Host）、[TABCoopMod_client_5.log](file:///d:/ClaudeCodeProject/TABCoopMod/logs/TABCoopMod_client_5.log)（Client）
> 测试版本：`v0.2.0`（2 人 Steam P2P `MultiFaction` 多阵营合作模式，分支 `dev/multi-faction`）

## 问题总览

| 编号 | 现象描述 | 严重度 | 核心根因 | 目标代码位置 |
|---|---|---|---|---|
| **Bug 1** | 局内重同步（Auto-resync）及重连重载后，主客机 `1~8` 编队互串或丢失 | P1 | `GameBridge.RestoreSelectionGroupEntityIds` 先 `list.Clear()` 后直接引用 `internal` 类型 `ZX.Entities.ZXEntity`，触发 CLR 4.6 `TypeAccessException` 中断；且客机初次进局/重连未清零快照中自带的 Host 编队 | [GameBridge.cs](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Core/GameBridge.cs)<br>[CoopSessionManager.cs](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Sync/CoopSessionManager.cs) |
| **Bug 2** | `A` 键攻击移动无法从静止/待命状态直接发出，仅在右键移动状态下可用 | P1 | `ZXSystemGameLevel.#=zN0jTYBQMVgRf` 在 `#=zQp2M56bpPd9jmiNkVA==` 后会按单位旧 `CurrentCommand` 逐个调用 `#=zfmgzkuw=`；静止时旧指令为 `Stop`，而 `IsGroupRoutedCommand` 仅放行/吞掉 4 种移动类指令，导致同一 tick 内紧接广播 `ExecuteCommand(Stop)` 覆写刚发出的 `Attack` | [CommandInterceptorPatch.cs](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Patches/CommandInterceptorPatch.cs)<br>[GameBridge.cs](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Core/GameBridge.cs) |
| **Bug 3** | 中后期多波尸潮后出现连续 3 秒冻结死循环，且快照回退后彻底卡死（173.3s），必须客机退回重连 | **P0** | (3A) `DirectFastPath` 超时回退 Steam 后未推进 `AckBase`/同步收端 `ExpectedSeq`，`ProbePong` 几十毫秒内立即重连但客机 `Rx.ExpectedSeq` 卡在被绕过的 seq 上，累计超 64 个 chunk 后丢弃所有 UDP 数据块形成无限 3s 死循环；<br>(3B) 快照回退时将 66 块（约 2.16 MB）打成单个 `DirectFallbackBatch` 调用 `SendP2PPacket`，超过 Steam `k_EP2PSendReliable` 1 MB 上限被静默丢包且未检查返回值 | [DirectFastPath.cs](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Networking/DirectFastPath.cs)<br>[SteamP2PTransport.cs](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Networking/SteamP2PTransport.cs) |
| **Bug 4** | 中后期尸潮交战频繁出现 `ID COUNTER DRIFT`（`delta=±1~11`），诱发 `MissingOnClient` 频繁触发 `Auto-resync` 重载 | P1 | `ZXCommand.#=zEJihvZ1Lcs9a` 通过全局 `DXPool.New<Proyectile>()` 分配投射物；`Proyectile : DXEntity` 在对象池未命中时会调用 `DXGuid.New()` 消耗 `_syncedEntityIdCounter`，导致后续刷新的尸潮/单位实体 ID 整体偏移 | [LockstepClock.cs](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Determinism/LockstepClock.cs)<br>[StateHasher.cs](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Determinism/StateHasher.cs) |

> **附带观测（非 Bug）**：尾局 `21:39:32`~`21:47:06` 期间（Host 日志 `L1686-L1745`），`DirectFastPath` 直连通道运行健康（`directRtt=6.4..7.9ms`、`cmdFly=0`、`bulkFly=0`，零 stall 告警），Host 记录的 `rtt` 升至 `393.7ms`（`L1694`）、`clientLag=6t..9t`（`200ms..300ms`），系大波尸潮下 Client 端 CPU 单帧耗时上升触发的 Host 正常反压限速。对比说明中后期 `20:54~21:13` 的严重卡顿完全由 **Bug 3** 的直连序列号死循环引发，而非硬件或带宽不足。

---

## Bug 1：编队（1~8）在重载/重连时互串或丢失

### 1.1 日志证据

- Host 日志共有 **10 次**编队恢复抛错（`L210`, `L417`, `L701`, `L818`, `L872`, `L1072`, `L1176`, `L1232`, `L1475`, `L1627`），以及 **2 次** 空槽日志（`L1297`, `L1356`）：
  ```text
  [20:42:39.678] [WARN] [v0.2.0] [T1] [GameBridge] RestoreSelectionGroupEntityIds failed: 方法“TABCoopMod.Core.GameBridge.RestoreSelectionGroupEntityIds(UInt64[][])”尝试访问类型“ZX.Entities.ZXEntity”失败。
  [21:18:33.475] [INFO] [v0.2.0] [T1] [GameBridge] Restored 0 entity reference(s) across 9 local control-group slot(s).
  ```
- Client 日志共有 **6 次**相同抛错（`L274`, `L535`, `L1020`, `L1185`, `L1550`, `L1677`），以及重连后 **2 次** 空恢复日志（`L2187`, `L2358`）：
  ```text
  [20:42:35.348] [WARN] [v0.2.0] [T1] [GameBridge] RestoreSelectionGroupEntityIds failed: 方法“TABCoopMod.Core.GameBridge.RestoreSelectionGroupEntityIds(UInt64[][])”尝试访问类型“ZX.Entities.ZXEntity”失败。
  [21:33:43.395] [INFO] [v0.2.0] [T1] [GameBridge] Restored 0 entity reference(s) across 9 local control-group slot(s).
  ```

### 1.2 根因分析

1. **违反绝对铁律 #3（运行时跨程序集直接访问 `internal` 类型 `ZXEntity`）**：
   - 在 [GameBridge.cs#L1298-L1316](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Core/GameBridge.cs#L1298-L1316) 的 `RestoreSelectionGroupEntityIds(ulong[][] savedGroups)` 中，先对当前槽位 `i` 执行 `list.Clear()`（`L1302`），再在遍历 ID 时执行：
     ```csharp
     DXEntity ent = game.GetEntityByID(ids[j]);
     if (ent is ZXEntity zxEnt && !zxEnt.Disposed && !list.Contains(zxEnt))
     ```
   - `ZX.Entities.ZXEntity` 在 `TheyAreBillions.exe` 中是 `internal` 类型。Desktop CLR 4.6 下 `IgnoresAccessChecksTo` 仅对编译期生效，运行时执行到引用 `ZXEntity` 的代码即抛出 `TypeAccessException`（中文系统报错为“尝试访问类型 `ZX.Entities.ZXEntity` 失败”）。
   - **为何会“互串”而非单纯清空**：遇到第一个非空编队槽位 `i` 时，`list.Clear()` 已将其清空，随后因 `ent is ZXEntity` 抛异常跳出整个循环并被外层 `catch` 吞掉。结果：
     - 槽位 `i` 本身的编队丢失；
     - 槽位 `i+1 .. 8` 根本没有执行到 `list.Clear()`，**原封不动保留了 `coop_host_snapshot.zxsav` 内自带的 Host 编队**——导致 Client 直接继承 Host 的部分编队（“互串”），甚至出现 HUD 编队按钮未刷新、按编队键选到对方阵营单位的现象。
2. **初次进局 / 客机重连路径未清零快照自带编队**：
   - 见 [CoopSessionManager.cs#L1166-L1186](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Sync/CoopSessionManager.cs#L1166-L1186) 与 `BeginClientSnapshotLoad`（`L4719-L4727`）：`CaptureLocalControlGroupsForMidGameReload()` 在尚未进入已同步关卡（`!_hasEnteredSyncedLevelInMatch || GameBridge.CurrentLevelState == null`）时直接 `return`，`_pendingResyncSelectionGroups` 保持 `null`。
   - 当 Client 初次加入或退回主菜单重连加载 Host 快照时，`RestoreLocalControlGroupsAfterMidGameReload()` 因 `_pendingResyncSelectionGroups == null` 跳过恢复逻辑，使得 `coop_host_snapshot.zxsav` 中的 Host `1~8` 编队直接留在了 Client 本地中。

### 1.3 修复方案

1. **消除跨程序集 `ZXEntity` 直接类型引用**：
   - 将 [GameBridge.cs#L1310](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Core/GameBridge.cs#L1310) 的 `ent is ZXEntity zxEnt` 替换为反射类型判定 `TypeZXEntity != null && TypeZXEntity.IsInstanceOfType(ent)`（`DXEntity` 本身为 `public` 类型，可直接访问 `!ent.Disposed` 并通过 `list.Add(ent)` 写入编队集合）。
   - 同时接入本地阵营/控制权过滤（`MultiFaction` 下确保仅恢复归属本地玩家的实体，防止历史残留或异常 ID 跨阵营选控），并在恢复完成后触发编队 HUD 刷新。
2. **明确区分“初次加入/重连”与“局内重同步”**：
   - Client 端在开始接收/加载 Host 快照时（如 `SnapshotBegin` / `BeginClientSnapshotLoad`），若当前不在同局活跃状态或 `_pendingResyncSelectionGroups == null`，显式赋空二维数组 `new ulong[9][]`（表示 9 个槽位均清零），确保 `RestoreSelectionGroupEntityIds` 会对全 9 个槽位执行 `list.Clear()`，彻底剥离 Host 存档内携带的编队。

---

## Bug 2：`A` 键攻击移动（Attack-Move）无法直接发出，仅在右键移动状态下可用

### 2.1 根因分析

反编译引擎方法 `ZXSystemGameLevel.#=zN0jTYBQMVgRf`（即 [ObfuscatedSymbols.cs](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Core/ObfuscatedSymbols.cs) 中的 `Method_LevelSys_ExecutePendingCmd`）与拦截补丁对照如下：

1. **原版执行流 (`ZXSystemGameLevel.#=zN0jTYBQMVgRf`)**：
   - 当玩家下达需要群组目标路由的指令（例如 `A` 键攻击移动 `ZX.Commands.Attack`、右键移动 `Travel`、巡逻 `Patrol` 等）时，`#=zN0jTYBQMVgRf` 先调用 `#=zQp2M56bpPd9jmiNkVA==`（`GroupMoveSelected`）为选中单位分配并写入目标指令。
   - **紧接着在同一个函数内**，游戏遍历所有选中单位，调用：
     ```csharp
     cCommandable?.#=zE83e_h4=()?.#=zfmgzkuw=(...)
     ```
     注意此处 `#=zE83e_h4=()` 取的是单位身上的 **当前 `CurrentCommand`**（期望已被 `#=zQp2M56bpPd9jmiNkVA==` 更新），再调用其 `#=zfmgzkuw=`（`ZXCommand.Execute`）。
2. **Lockstep 拦截产生的状态错位**：
   - 在 [CommandInterceptorPatch.cs#L89-L135](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Patches/CommandInterceptorPatch.cs#L89-L135) 的 `Patch_LevelSys_GroupMoveSelected` Prefix 中，为了等待 Lockstep tick 再修改世界状态，Mod 设置 `CommandInterceptor.SuppressIndividualMoveExecution = true`（`L131`），采集并广播 `GroupMoveOrPatrol` 网络指令（`L132`），然后 `return false`（`L133`）跳过原版 `#=zQp2M56bpPd9jmiNkVA==`。
   - **关键副作用**：原版 `#=zQp2M56bpPd9jmiNkVA==` 未在本地执行，因此选中单位的 `CurrentCommand` **尚未改变**，仍然保留下达 `A` 键**之前**的旧指令：
     - **静止/待命状态** -> 单位当前指令是 `ZX.Commands.Stop`（或 `Hold` / 上一个目标 `Attack`）；
     - **右键移动状态** -> 单位当前指令是 `ZX.Commands.Travel`。
3. **随后的 `Patch_ZXCommand_Execute` 过滤列表过窄**：
   - [CommandInterceptorPatch.cs#L217-L219](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Patches/CommandInterceptorPatch.cs#L217-L219)：
     ```csharp
     if (CommandInterceptor.SuppressIndividualMoveExecution && GameBridge.IsGroupRoutedCommand(__instance))
         return false;
     ```
   - [GameBridge.cs#L2730-L2742](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Core/GameBridge.cs#L2730-L2742) 中的 `IsGroupRoutedCommand` 仅识别 4 个类型：
     - `ZX.Commands.Travel`
     - `ZX.Commands.Patrol`
     - `ZX.Commands.Chase`
     - `ZX.Commands.UseItem_Grenade`
   - **完整后果链**：
     - 当单位**静止**（`Stop`）、**原地警戒**（`Hold`）或**正在攻击**（`Attack`）时按下 `A` 键并左键点击，`IsGroupRoutedCommand(__instance)` 返回 **`false`**。于是 `Patch_ZXCommand_Execute` **没有吞掉** `#=zN0jTYBQMVgRf` 的后续循环调用，反而将其当成一条独立指令采集并发出 `ExecuteCommand(Stop/Hold)`！结果在同一 Lockstep tick 内，刚发出的 `Attack` 指令立刻被紧随其后的 `Stop` 指令覆盖取消，表现为空白点击/无反应。
     - 仅当单位处于**右键移动**（`ZX.Commands.Travel`）状态下，`Travel` 恰好命中上述 4 类型之一而被 `return false` 吞掉，从而使 `A` 键攻击移动得以保留并执行。

### 2.2 修复方案

1. **主修复（无条件吞掉组命令执行阶段的所有后续个体调用）**：
   - 在 [CommandInterceptorPatch.cs#L217-L219](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Patches/CommandInterceptorPatch.cs#L217-L219) 的 `Patch_ZXCommand_Execute` Prefix 中，将条件放宽为：只要 `CommandInterceptor.SuppressIndividualMoveExecution` 为 `true`，**直接无条件 `return false`**，无需再受限于单位此前旧 `CurrentCommand` 的类型（`Stop` / `Hold` / `Attack` / `Travel` 等）。
   - 同时确认 `SuppressIndividualMoveExecution` 的生命周期闭环：`Patch_LevelSys_ResetMoveSuppression`（`ExecutePendingCmd` Postfix/Finalizer）与 `Patch_ResetCommandGuardsOnStartFrame` 确保异常或退出时一定复位为 `false`，不会误伤下一帧的普通右键/个体指令。
2. **纵深防御（补全 `IsGroupRoutedCommand` 命令列表）**：
   - 在 [GameBridge.cs#L2735-L2741](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Core/GameBridge.cs#L2735-L2741) 的 `_groupRoutedCommandTypes` 中补充 `ZX.Commands.Attack`（以及在编码前通过反编译确认确切类名的 `Stop` / `Hold` 命令类型），避免其它代码路径依赖该判断时再次漏判。

---

## Bug 3：中后期 3 秒冻结死循环与 173 秒快照回退卡死

### 3.1 日志证据

本次测试中共观察到两个互相关联的重度网络卡死阶段，均只通过“Client 退出重连（建立新 UDP socket/peer）”才得以恢复：

1. **阶段 A：3 秒冻结 -> 回退 -> 秒级重激活的无限死循环（共发生 103 次高频循环）**：
   - 发生时段：`20:54:39.462`~`20:58:19.500`（Host `L441-L666`）与 `21:11:03.411`~`21:13:16.238`（Host `L901-L1030`）。全日志 Host 累计出现 **104 次** `Direct data channel stalled` 告警（含 1 次快照阶段）。
   - 典型循环序列（以 Host `L1021-L1030` 为例）：
     ```text
     [21:13:01.644] [WARN] [DirectFastPath] Direct data channel stalled (3005ms unacked, cmdFly=10, bulkFly=0) for P2; falling back cleanly to Steam P2P.
     [21:13:01.662] [INFO] [DirectFastPath] Candidate verified for P2: StunIPv4:218.74.238.229:57979 (RTT=7.3ms). Signaling Steam barrier cutover...
     [21:13:01.677] [INFO] [DirectFastPath] Direct Fast-Path ACTIVATED for P2 via StunIPv4:218.74.238.229:57979 (RTT=7.3ms)!
     [21:13:06.454] [WARN] [DirectFastPath] Direct data channel stalled (3004ms unacked, cmdFly=1, bulkFly=0) for P2; falling back cleanly to Steam P2P.
     ...
     [21:13:16.198] [WARN] [DirectFastPath] Direct data channel stalled (3008ms unacked, cmdFly=1, bulkFly=0) for P2; falling back cleanly to Steam P2P.
     [21:13:16.238] [INFO] [DirectFastPath] Direct Fast-Path ACTIVATED for P2 via StunIPv4:218.74.238.229:57979 (RTT=7.1ms)!
     ```
   - 期间 Host 统计出现 `cmdFly=10` 窗口满载（如 `L468-L485`）、`cmdAvg=3415.9ms`（`L449`）乃至 `cmdAvg=7468.6ms cmdP95=20661.0ms cmdMax=21519.1ms`（`L903`）。
   - 同期 Client 端 **0 次**记录 `Direct data channel stalled` 或重激活（每个 UDP 端口全期只激活 1 次），但持续触发 `Tick gate waiting 3.0s`（如 `L554`, `L563`, `L565`, `L569` 及 `L1221`, `L1224`, `L1226`）与 `118` 次 `Registered 7 candidate(s) for P1`。直到 Client 退回主菜单断开（Client `L1270`，Host `L1038` `Steam lobby member left`）并用新端口 `57979` 重连才解除。

2. **阶段 B：快照回退 Steam 静默丢包导致 173.3 秒永久卡死**：
   - `21:18:15`~`21:18:36`：Host 发送 2,278,305 字节 / 70 chunks 快照（Host `L1299`、Client `L1725`），Client 收到前 4 个 chunk 后停止（Client `L1729`：`buffered=4/70`）。
   - `[21:18:36.803]` Host `L1300` 触发快照批量回退：
     ```text
     [21:18:36.803] [WARN] [v0.2.0] [T23] [DirectFastPath] Direct data channel stalled (3010ms unacked, cmdFly=0, bulkFly=48) for P2; falling back cleanly to Steam P2P.
     ```
   - Host 随后在 `L1303` 等待 `SnapshotLoadedAck` 60 秒超时后独自恢复运行；Client 则因缺少 `4..69` 块一直停在 `Tick gate waiting` 直至 **`173.3s at tick 174005`**（Client `L1911`，`[21:20:58.920]`），被迫手动退回主菜单（`L1912`）重连。

### 3.2 根因分析

1. **Bug 3A：回退时未对齐 `Tx.AckBase` 与收端 `Rx.ExpectedSeq`，叠加 `ProbePong` 无冷却重激活**：
   - **序列号永久错位**：在 [DirectFastPath.cs#L1391-L1396](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Networking/DirectFastPath.cs#L1391-L1396) 触发 `dataStallMs >= DirectDataStallFallbackMs (3000ms)` 时，`DrainUnackedMessagesForFallback`（`L1432`）把 `UnackedMessages` 清空转为走 Steam P2P `DirectFallbackBatch`，并清零 `InFlight` / `PendingChunks`，**但既没有推进 `tx.AckBase`，也没有在切回时把发端的 `CmdTx.NextSeq`/`BulkTx.NextSeq` 同步给收端 `CmdRx.ExpectedSeq`/`BulkRx.ExpectedSeq`**。
   - **瞬间无保护重激活**：由于底层的 UDP 探测包 `ProbePing/ProbePong` 通畅（RTT 仅 `5~7ms`），[RegisterDynamicOrHeartbeatCandidate](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Networking/DirectFastPath.cs#L1184-L1196) 在 fallback 后仅仅 **17~40ms** 内就再次发起 `PeerDirectCutoverNeeded` 并重新启用直连。
   - **超窗静默丢包死循环**：被 Steam 绕过传输的那个 UDP chunk 永远不会再通过 UDP 到达，导致 Client 侧 `rx.ExpectedSeq` **永远卡在那个缺口 seq 上**。随着后续直连发包使 `streamSeq` 继续自增，一旦超过收端 SACK 接收窗口上限（[ProcessRxChunk](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Networking/DirectFastPath.cs#L1246-L1247)：`uint diff = streamSeq - rx.ExpectedSeq; if (diff < 64 ...)`），**Client 将静默丢弃之后的所有 Host UDP 数据包**！Host 发出最大在途窗口（`cmdFly=10`）后全部无人 ACK -> 必等 `3000ms` 触发 fallback -> 经 Steam 补发让游戏勉强推进 1 帧 -> 17ms 后 `ProbePong` 又切回损坏的 UDP 通道 -> 再次等待 3 秒……如此周而复始直至重建会话。
2. **Bug 3B：Steam 回退合批超 1 MB 限制且未检查 `SendP2PPacket` 返回值**：
   - 快照传输第 4~69 块回退时，`DrainUnackedMessagesForFallback` 排出了约 **66 个 chunk（合计约 2.16 MB）** 交由 [SteamP2PTransport.cs#L987-L1022](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Networking/SteamP2PTransport.cs#L987-L1022) 的 `SendFallbackBatchViaSteam` 发送。
   - `SendFallbackSlice`（`L1006-L1022`）将全部 `bulkMsgs` 序列化进**单个** `PacketType.DirectFallbackBatch` 帧并调用 `SteamNetworking.SendP2PPacket(..., EP2PSend.k_EP2PSendReliable, ...)`（[SteamP2PTransport.cs#L482](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Networking/SteamP2PTransport.cs#L482)）。
   - Steamworks `SendP2PPacket` 在 `k_EP2PSendReliable` 模式下硬性限制单包大小 **不超过 1 MB**。超过 2 MB 的 `DirectFallbackBatch` 被底层直接拒绝返回 `false`，但代码第 `L482` 行未检查返回值且无错误日志；同时 Host 在 `DirectFastPath` 中已经彻底移除了这些 `UnackedMessages`，造成 66 块快照永久丢失，Client 卡死在 `buffered=4/70`。

### 3.3 修复方案

1. **序列号对齐机制（消除 Bug 3A 状态裂痕）**：
   - **回退推进发端基准**：`DrainUnackedMessagesForFallback` 排空并转交 Steam 后，推进 `tx.AckBase = tx.NextSeq - 1`（或记录 cutover sync point），保证发端状态机与 `UnackedMessages` 一致。
   - **切回同步收端基准**：在 `DirectCutoverSync`（以及 `DirectFallbackBatch`）中携带发送端当前的 `CmdTx.NextSeq` 与 `BulkTx.NextSeq`（或安全重置点）；收端在启用新阶段直连或处理切回同步时，若本地 `rx.ExpectedSeq < syncedNextSeq`，将 `rx.ExpectedSeq` 推进至 `syncedNextSeq` 并清理旧 `rx.Buffered` 残留碎片，彻底避免 `diff >= 64` 死锁（同时保留 `WasMessageDeliveredViaDirect` 的 `msgId` 幂等去重逻辑）。
2. **回退冷却退避与熔断器（消除 3s 震荡）**：
   - 在 `PeerDirectState` 中记录每次因 `DirectDataStallFallbackMs` 触发回退的时间戳与连续次数；实施指数退避冷却（例如 10s -> 30s -> 60s），冷却期内禁止 `RegisterDynamicOrHeartbeatCandidate` 触发 `PeerDirectCutoverNeeded` 重激活直连。
   - 连续数据面 stall 达到阈值（例如 3 次）后触发**局内熔断**，本局剩余时间保持稳定走 Steam P2P，不再反复切换直连造成 3s 抖动。
3. **`SendFallbackSlice` 分片与返回值审计（修复 Bug 3B 快照丢包）**：
   - 在 [SteamP2PTransport.cs#L1006-L1022](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Networking/SteamP2PTransport.cs#L1006-L1022) 对 `DirectFallbackBatch` 按字节上限（例如单帧 `<= 128 KB` 或 `<= 256 KB`）分批切片发送，确保远低于 Steam `SendP2PPacket` 1 MB 上限。
   - 在 [SteamP2PTransport.cs#L482](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Networking/SteamP2PTransport.cs#L482) 捕获 `bool ok = SteamNetworking.SendP2PPacket(...)` 返回值，失败时输出带 `frame.Length` 与 `channel` 的 `[WARN]`/`[ERROR]` 日志（对于关键回退/快照消息必要时告警或重试）。

---

## Bug 4：中后期尸潮交战频繁 `ID COUNTER DRIFT` 诱发大量 `Auto-resync`

### 4.1 日志证据

日志中可见多次由微小 `ID COUNTER DRIFT` 直接引发的实体缺失型 `Auto-resync`（注意 `MissingOnClient` 与 `MissingOnHost` 数量往往十分接近，符合“同批实体 ID 集体顺移 N 位”的特征）：

- Host `L174`（Tick `129180`, `delta=-1`）-> `L185`（Tick `129360` `Auto-resync`, `MissingOnClient=48, MissingOnHost=54`）
- Host `L391`（Tick `145140` `Auto-resync`, `MissingOnClient=18, MissingOnHost=18`）
- Host `L783..L789`（Tick `162300` `delta=2`, Tick `162360` `delta=1`, Tick `162420` `delta=11`）-> `L792`（Tick `162420` `Auto-resync`, `MissingOnClient=22, MissingOnHost=21`）
- Host `L832..L844`（Tick `163920..164220`, `delta=-1..-2`）-> `L846`（Tick `164220` `Auto-resync`, `MissingOnClient=33, MissingOnHost=26`）
- Host `L1436`（Tick `186000`, `delta=-2`）-> `L1451`（Tick `186420` `Auto-resync`, `MissingOnClient=10, MissingOnHost=1`）
- Host `L1511..L1601`（Tick `191940..194580`, `delta=4..9`）-> `L1603`（Tick `194580` `Auto-resync`, `MissingOnClient=17, MissingOnHost=0`）

### 4.2 根因分析

1. **投射物通过全局对象池分配且未隔离**：
   - 反编译 `ZXCommand.#=zEJihvZ1Lcs9a` 可见，战斗释放投射物时调用 `DXPool.New<Proyectile>()`。
   - `Proyectile` 继承自 `DXEntity`。当 `DXPool<Proyectile>` 对象池命中（复用旧对象）时不会调用构造函数；而**当对象池未命中（新建实例）时**，`new Proyectile()` 触发 `DXEntity()` 基类构造，调用 `DXGuid.New()` -> 经 [LockstepClock.cs](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Determinism/LockstepClock.cs) 中的 `DXGuid_New_Prefix` 消耗 `_syncedEntityIdCounter++`！
   - 与 `VisualVfxIsolationPatch` 隔离的纯表现类不同，`Proyectile` 目前既未被隔离在独立的 ID 段中，其背后依赖的 `DXPool<Proyectile>` 又是进程级复用池（且经历不同步时间点、重载前后或渲染/本地池容量历史变化后，主客机的对象池剩余空闲数容易出现 `1~11` 个的差异）。
2. **微小池差异直接污染后续真实世界实体 ID**：
   - 只要一方的 `DXPool.New<Proyectile>()` 发生池未命中而另一方复用旧对象，双方的 `_syncedEntityIdCounter` 立即产生 `delta=±1..11` 的偏移。
   - 一旦发生 1 个偏移，随后 `CUnitGenerator` 刷出的每只僵尸、新训练单位或新建建筑的 `DXEntity.ID` 都会在两端各自错位，导致所有同帧/后续生成实体在 `StateHasher` 中均无法按 ID 匹配（表现为 `MissingOnClient ≈ MissingOnHost`），进而频繁触发全图 2~4 MB 快照重载。

### 4.3 修复方案

1. **为 `Proyectile` 分配独立 ID 命名空间（`0x4000000000000000UL`，Bit 62 隔离位）**：
   - 保留主模拟实体共享计数器 `_syncedEntityIdCounter`（用于建筑、单位、僵尸等核心实体，不受投射物干扰），新增独立确定性计数器 `_syncedProjectileIdCounter`。
   - 拦截 `Proyectile` 构造/池创建路径（或在 `DXGuid_New_Prefix` 中结合调用上下文/前置标志位识别 `Proyectile`），仅在非渲染/非 VFX（即未落入 `0x8000...` 高位）时签发 `0x4000000000000000UL | (_syncedProjectileIdCounter++)`。
   - **位掩码设计理由**：`0x4000...`（Bit 62）在有符号 `Int64` 中仍为正数，不会误触全工程既有的 `(id & 0x8000000000000000UL) != 0`（Bit 63 VFX/本地临时实体判定，如 [GameBridge.cs#L1266](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Core/GameBridge.cs#L1266) 及 `StateHasher` 视觉实体跳过逻辑）。
2. **同步修正校准与池状态**：
   - 更新 [LockstepClock.cs](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Determinism/LockstepClock.cs) 的 `CalibrateEntityIdCounterFromLoadedLevel`：按 `(id & 0xC000000000000000UL) == 0` 过滤并校准真实模拟实体 `_syncedEntityIdCounter`，同时按 `0x4000...` 段单独校准 `_syncedProjectileIdCounter`（或由快照头携带同步），并检查在 `LoadLevel` / `DXPhysicWorld.Clear` 时统一清理或重置 `DXPool<Proyectile>` 以保证池起始水线一致。
   - **实现前核对**：动工前需核对 [StateHasher.cs](file:///d:/ClaudeCodeProject/TABCoopMod/src/TABCoopMod/Determinism/StateHasher.cs)（`FlagProjectile` 目前已将投射物从 `EntityCount` 排除，需确认 `StateHasher` 的 ID 漂移判定及存档序列化对 `0x4000...` ID 的兼容）。

---

## 修复优先级与验证清单

| 优先级 | 修复目标 | 联机 / 本地验收要点 |
|---|---|---|
| **P0** | **Bug 3**（`DirectFastPath` 序列同步 + 冷却熔断 + `SendFallbackSlice` 分片与返回值检查） | 1. 人工/Chaos 制造 UDP 单向/双向丢包阻断触发 3s fallback：验证不会出现连续 3s 震荡，且切回直连后收发 seq 对齐无丢包；<br>2. 触发 >2 MB 快照传输过程中强制中断直连触发 `SendFallbackBatchViaSteam`：验证分片均 `< 1 MB`、`SendP2PPacket` 返回 `true`、Client 正常收齐全部 chunk 完成重同步（无 173s 卡死）。 |
| **P1** | **Bug 1**（`RestoreSelectionGroupEntityIds` 反射类型判断 + 初次进局/重连清零） | 1. 设置 `1~8` 多组编队后手动或通过异常触发 `Auto-resync`：主客机日志均无 `RestoreSelectionGroupEntityIds failed`，原有 `1~8` 编队完整保留；<br>2. Client 退回主菜单后重连 Host 已有编队的对局：Client 编队全空，不继承 Host 编队、不混选对方阵营。 |
| **P1** | **Bug 2**（`Patch_ZXCommand_Execute` 抑止逻辑放宽 + `Attack/Stop/Hold` 补全） | 选中空闲站立、原地 `Hold`、交火中、右键移动中 4 种状态的单位按 `A` 键左键攻击移动：4 种场景均 100% 发出攻击移动并正确向目标区域推进/接战，且下一帧普通右键与 `S`/`H` 热键不受影响。 |
| **P1** | **Bug 4**（`Proyectile` 独立 Bit 62 计数器 + `CalibrateEntityIdCounterFromLoadedLevel` 掩码同步） | 跑通中后期多波密集弓箭手/狙击手/喷火兵+尸潮交火：观测不再因 `Proyectile` 池波动出现 `ID COUNTER DRIFT` 连锁偏移，显著降低非真实状态分歧导致的 `Auto-resync` 次数。 |
