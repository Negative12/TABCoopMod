# 02 — 核心技术架构设计 (Technical Architecture Design)

> **状态**: 已实机验证定型（`v0.1.7` 基线：Gate A1/A2/B1/B2/C/D、Chaos 故障注入、生存新局平衡与异构算力压测全通过）

---

## 1. 架构选型总览：Deterministic Lockstep + Native Save-Stream Snapshot

《亿万僵尸》（TAB）在生存中后期同屏存在 **10,000 ~ 33,000+ 个实体**（如 `6666.zxsav` 含 33,555 实体）：
- **纯状态同步不可行**：全量实体状态广播带宽高达数 MB/s，远超 Steam P2P 转发能力。
- **纯指令帧同步痛点**：无法低成本支持开局随机地图同步与极偶发算力漂移自愈。

因此本项目采用 **“确定性指令帧同步为主 + 原生存档流快照秒传为辅”** 的混合架构：

```text
+-----------------------------------------------------------------------------------+
|                              TABCoopMod 运行时架构                                 |
+-----------------------------------------------------------------------------------+
|                                                                                   |
|  [无损注入层 Bootstrap]                                                            |
|   TheyAreBillions.exe.config -> ModAppDomainManager (AssemblyLoad)                |
|   -> <Module>..cctor -> GameBridge.InitializeAndValidate() -> Harmony 逐类容错加载  |
|   -> Patch_DXRender_VirtualDisplayFallback (无物理显示器 Direct3DEx 兼容)           |
|                                                                                   |
|  +-------------------------------+  +------------------------------------------+  |
|  | 确定性沙盒层 (Determinism)     |  | 指令拦截与无状态回放层 (Command Sync)     |  |
|  | - DXGame.IsIADeterministic=true| | - CommandInterceptorPatch (主入口+幽灵预扣)| |
|  | - DeterministicPathfindingPatch| | - PauseAndSpeedInputPatch (查询原版键位表)| |
|  | - DeterministicTaskPatch       | | - MayorSyncPatch (同池定种+按ID+自动关窗) | |
|  |   (InPhysicsStep + 步末1/256量化)| | - LevelEndSyncPatch (胜负结算权威同步)   | |
|  | - DXGuid / VisualVfx / Audio   | | - CommandExecutor (全 object+反射，不碰    | |
|  |   & DXRandom 多维隔离与线程边界 | |   CSelectable，槽位敏感+Shift状态模拟)    | |
|  +---------------+---------------+  +--------------------+---------------------+  |
|                  |                                        |                        |
|                  v                                        v                        |
|  +-----------------------------------------------------------------------------+  |
|  | 帧同步编排层 (CoopSessionManager & TurnInputTickGate & PauseSyncController)  |  |
|  | - 30Hz 固定步长 (33.33ms/tick)，TurnInputTickGate 门控 + 自适应延迟/反压流控 |  |
|  | - PausedBySession 冻结 SimTick，OnFrameTick 刷新暂停/弹窗微操 + 保活心跳     |  |
|  | - 浅栈快照分片 + 60-tick 均摊 StateHasher v3 + 控制权反向对齐 + 重同步保留编队|  |
|  +---------------------------------------+-------------------------------------+  |
|                                          |                                        |
|                                          v                                        |
|  +-----------------------------------------------------------------------------+  |
|  | 混合传输抽象层 (INetworkTransport & DirectFastPathManager)                    |  |
|  | - SteamP2PTransport: Steam Lobby 信令 + 3通道隔离 + 槽位复用 + 版本校验       |  |
|  | - DirectFastPath: 双栈 IPv4/IPv6 UDP 直连升级 (64-bit SACK) + 3s 无损回退     |  |
|  | - LocalTcpLoopbackTransport: 127.0.0.1:27099 (支持 ChaosTransport 故障注入)  |  |
|  +-----------------------------------------------------------------------------+  |
+-----------------------------------------------------------------------------------+
```

---

## 2. 无损注入与显示兼容（Bootstrap & Render Compat）

### 2.1 `.NET AppDomainManager` 引导（`ModAppDomainManager.cs`）
由于 `TheyAreBillions.exe` 内置 Eazfuscator 完整性与 `StackTrace.GetFrame(3)` 校验，Mod 保持主程序磁盘字节 100% 不变，仅通过 `TheyAreBillions.exe.config` 注入 `ModAppDomainManager`：
1. 在 `InitializeNewDomain` 初始化日志与全局异常监听，订阅 `AppDomain.CurrentDomain.AssemblyLoad`。
2. 当 `TheyAreBillions` 加载完成时，先执行 `RuntimeHelpers.RunModuleConstructor(loaded.ManifestModule.ModuleHandle)` 注册内置资源解密器（`DXVision` / `Steamworks.NET` / `DXPlatform_Desktop`）。
3. 调用 `ModEntry.Initialize()` → `GameBridge.InitializeAndValidate()`（启动自检必须 `0 missing`）→ 逐类 `PatchClassProcessor.Patch()` 包 `try/catch` 应用全部 Harmony 补丁（实机 `Classes OK=64, failed-tolerant=0`）。

### 2.2 虚拟显示器 `Direct3DEx` 回退（`Patch_DXRender_VirtualDisplayFallback`）
在无物理显示器的虚拟显示环境（如远程虚拟屏适配器报错 `D3DERR_NOTAVAILABLE`）下，对纯 C# 封装类 `DXPlatform_Desktop.DXRender_SlimDX` 的 9 个方法应用 IL Transpiler：当 `Direct3D.GetDeviceCaps` 失败时自动回退至 `SlimDX.Direct3D9.Direct3DEx`，并将 `Pool.Managed` 纹理替换为 `Pool.Default | Usage.Dynamic`（避开直接补丁 C++/CLI 混合程序集 `SlimDX.dll` 导致的 `InvalidProgramException`）。

---

## 3. 确定性沙盒层（Determinism Sandbox）

核心代码位于 `src/TABCoopMod/Determinism/`（`LockstepClock.cs`、`DeterministicTaskPatch.cs`、`DXRandomCtorPatch.cs`、`TickGate.cs`、`StateHasher.cs`）：

### 3.1 固定步长时钟与 `TurnInputTickGate`
- **开启确定性主循环**：`DXGame.IsIADeterministic = true`，阻止 `LoadPhysics`/`LoadPhysicsOld` 启动异步计时线程。
- **固定步长推进与异步流水线**：在 `UpdateGameLogic_Prefix` 中检查 `LockstepClock.TickGate.IsTickReady(nextTick)`。
  - 若 `PausedBySession == true` 或 `!CurrentGameSystem.Enabled` 或 `IsLevelEnded()`：通过 `HoldClocks(__instance)` 冻结 `GameTime` 与 `LastGameTimeIAUpdate`，**真正冻结 `SimTick`**。
  - 若运行中且 Gate 就绪：按固定步长 `FixedStepMs = 33.333333333333336` 推进；单 tick 正常步进允许主线程渲染与工作线程 `_TaskLogic` 异步重叠（`canOverlapAsync`，仅在 `maxTicksThisFrame == 1` 时开启，慢客机即使单 tick `>16ms` 仍保留 1-tick 异步重叠；多 tick 追帧转为同步多步并放宽预算至 `28ms`），并在物理步后、校验和计算前统一排空 `PostMethods` 队列。
  - 关卡加载完成及基线对齐时通过 `GameBridge.AlignSimulationClocks()` 将 `LastGameTimeIAUpdate` 与 `DXPhysicWorld.LastGameTimeUpdatePhysics`、`TimeBodyPosition`、`TimeLastBodyPosition` 同步对齐到 `GameTime`；`GameSetGameTimePatch` 放行 `DXGame.SetGameTime(0.0)` 重置（防止新开生存局继承上一局天数）。
- **窗口焦点、线程边界与帧模数隔离**：联机与测试模式下强制 `DXGame.Focused/Visible = true`；通过精确的 `LogicThreadId`（由 `_TaskLogic` 首帧捕获）区分仿真工作线程与主渲染线程，确保 `ShowBannerOutsideDet` 清除外层 `InDeterministicContext`；`FrameSceneModuleDeterminismPatch` 将 `DXGame.IsFrameSceneModule(n)` 改为按 `IAIterationInt % n` 判定。

### 3.2 同步寻路接管（`DeterministicPathfindingPatch`）
- **根因**：原版 `ZXGame.#=zB3Vhdin5PerfrubjO4AblAA=()`（`IsMultiplayer`）仅 6~8 字节 IL，会被 x64 RyuJIT 内联进 `CMovable.#=zUPt0E_k=`，导致 `ZXGameMultiplayerModePatch` 被绕过、寻路落入后台多线程 `FindPathConcurrent` 产生 A/B 二值轨迹分叉。
- **架构**：直接拦截引擎层 `DXVision.DXWorldGrid.FindPathConcurrent` 与 `FindPathConcurrentWithHighPriority`（位于 `DXVision.dll`，不会被内联），在当前逻辑线程同步调用 `DXWorldGrid.FindPath`（保留单位体型 `cellSize` 与空路径字段 `#=ziMavkfK9ZJji` 清空逻辑，规避原版多人分支的体型硬编码与递归栈溢出 Bug）。

### 3.3 仅在物理步进串行化并行任务（`DeterministicTaskPatch`）
- 反编译全部 `DXHelper_Task.For` 调用点确认：读档期（`DXLevel.LoadTable`）与场景渲染裁剪（`DXScene.UpdateObjects`）是只读/独立任务，**只有 `DXPhysicWorld.UpdateDefaultTime()` 内的两次循环影响模拟状态**。
- 因此 `DeterministicTaskPatch` 仅在 `LockstepClock.IsDeterministicModeActive && LockstepClock.InPhysicsStep` 为 `true` 时将 `DXHelper_Task.For` 转为单线程顺序执行，同时在 `SortEntitiesById` 中剔除 `null` 与 `DXPhysicBody.Handle == IntPtr.Zero` 的空句柄并按 ID 升序排列。大存档 `6666.zxsav`（33,555 实体）得以保持 **~7.8 秒**原生级加载速度。
- 在 `UpdatePhysics_Exit` 中接入 `QuantizePhysicsBodiesPostStep()`（`TABCOOP_PHYSICS_QUANT=256`，即 `1/256` 格量化写回刚体坐标与速度，同时保留移动体 `Last*` 供渲染插值），大幅消除密集碰撞下的微步漂移。

### 3.4 `DXGuid` 双轨分流与 `DXRandom` 多维定种
1. **`DXGuid.New()` 双轨分流**：
   - 仅当处于确定性逻辑/命令上下文（`IsLogicWorkerActive() || IsPhysicsThreadActive() || InDeterministicContext` 且非加载/音频/视效隔离期）时消耗同步 ID 计数器（`_syncedEntityIdCounter`，支持校准与 `SnapshotBegin` / `StateChecksumReport` 校验同步）；
   - 加载期、本地 UI/渲染特效及受本地视野门控的 `ZXMapEvent`（`.ctor`/`OnPoolNew`）与 `OMessageQueue`（`VisualVfxIsolationPatch`）一律分配高位 ID（`0x8000000000000000UL | localCounter`），`StateHasher` 自动忽略高位 ID。
2. **`DXRandom` 全域隔离与按实体行为随机流**：
   - `DXRandomCtorPatch` 拆分同步模拟线程与非模拟/渲染线程的种子计数器，防止主线程动画插队污染逻辑序列。
   - `ZXBehaviourRunRngScopePatch`（`DynamicMethod TABCoop_GetBehavOwnerEntityId`）将 `ZXBehaviour.Run()` 内的行为随机调用拦截到基于 `(SyncedRandomSeed, SimTick, Entity.ID, drawIdx)` 的无状态 SplitMix64 序列，消除单个游荡僵尸消耗共享 RNG 造成的级联漂移。
   - `LockstepClock` 对 `DXRandom.Global` 按上下文分流（`synced` / `aux` / `render`）；同步重置 `DXLevel.Random`、`SysColonists` 游标与列表、`BHFarmer` 农田选格随机，并隔离音频（`AudioRngIsolationPatch`）、纯视觉特效与告警（`VisualVfxIsolationPatch`）、帧时间冻结（`GameTimeIsolationPatch`）、视口精灵拉取（`CSpritePullIsolationPatch` / `CMovableSpriteProgressSyncPatch`）的污染；同时通过 `SetCameraVisualPassPatch` / `UpdateVisibleCellsVisualPassPatch` 全场景插值（含 `CShadow`、Z 深度与视觉 `GameTime` 余数，位置纠偏残差按 `VisualSnapExpireMs = 600.0`、`VisualSnapTauMs = 180.0` 指数衰减）与 `SmoothCameraPanPatch` 保障渲染帧间移动与平移平滑。

### 3.5 `StateHasher` v3（60-Tick 均摊、顺序无关校验与非对局实体过滤）
- FNV-1a 64 位校验（`HashVersion = 3`），通过 `IncrementalEpochHasher` 按 `entity.ID % 60` 均摊到连续 60 个 `SimTick` 计算，覆盖资源、预留、实体坐标（精确与粗粒 `CoarsePos`）、生命值、僵尸仇恨签名（`AggroZombieCount` / `ZombieAggroChecksum`）、投射物、生产队列及 16 分桶摘要，输出 `HardHash` 与 `FullHash`。
- 跳过不参与模拟交互的裸 `DXVision.DXEntity`（受击飙血/弹花等纯视觉特效，依赖视野可见性）与 `Raven` 环境飞鸟。

---

## 4. 指令拦截与无状态回放系统（Command Interception & Replay）

### 4.1 拦截点（`Patches/`）
- **`CommandInterceptorPatch.cs`**：拦截 `Build`/`EntityBonusCreation` 建造放置与奖励单位取出、`ZXSystemGameLevel` 群体移动（`#=zQp2M56bpPd9jmiNkVA==` / `#=zgn6bDkSd9MGS3$WvAQ==`）、通用命令 `ZXCommand.#=zfmgzkuw=`、取消当前命令（`#=zWGiN6nrTv8Dp`）与取消队列槽位（`#=z8g7GC7xLrr8j`）。对建造拦截增加 `CanExecuteCommandOnEntity`、`CanPlaceBuildingAtTarget`、本地挂起建造幽灵框与 8 项资源预扣（`CanAffordWithPending`）、`_recentLocalBuilds` 短窗面积去重以及 0ms 单位语音确认（回放期屏蔽重复音效）；针对 `EntityBonusCreation` 与 `ExtractFromUnitHolderSlot` 携带真实 `NSlot`/`EntityTypeName` 实现槽位敏感回放；所有守卫均排除回放期、生产队列自动推进（`#=zrz2EI1A=`）、确定性逻辑帧内部 AI 调用以及非玩家阵营，且严禁对内部类型调用 `HasComponent<T>()`。
- **`PauseAndSpeedInputPatch.cs`**：从 `ZXSystemGameLevel.OnKeyDown` / `OnKeyUp` 拦截暂停键（通过 `GameBridge.IsKeyBoundToCommand(__0, ZXKeyboardCommand.Pause, DXKeys.Space)` 动态查询玩家当前的 `Standard`/`CameraWASD`/`Custom` 暂停键绑定，不硬编码空格）与 `+`/`-` 倍速键，交由 `PauseSyncController` 与 `CommandInterceptor.Dispatch` 处理。
- **`MayorSyncPatch.cs`**：Hook `ZXMayor.#=zPTw8CXGE0RqF` 与 `ShowMayorSelection`（`MayorModalTrackerPatch`），弹出前按 `(SyncedRandomSeed, mayorLevel)` 重设 `DXLevel.Current.Random` 确保双端候选池完全一致，带四重守卫与 `mayorLevel` / `MayorStates` 幂等去重；客机收到包含 `MayorVoteOrSelect` 的 `TurnInputBroadcast` 时立即调用 `CloseActiveMayorWindow()` 与 `NotifyRemoteMayorCommitted()` 关闭对端弹窗并抑制未弹出延迟窗口，配合 `OnFrameTick` 在本地模态暂停期主动冲刷已就绪指令。
- **`LevelEndSyncPatch.cs`**：Hook 房主 `TriggerGameEnd` / `ShowCampaignEnd`（并在 `OnFrameTick` 兜底轮询 `IsLevelEnded()`），权威广播 `PacketType.LevelEnded` 使客机同步进入 `游戏结束` 结算且抑制 `HostPauseKeepalive` / `Tick gate waiting` 误报；`LockstepClock` 在 `Paused` / `!CurrentGameSystem.Enabled` / `IsLevelEnded()` 时停步且 `ReturnToMainMenuPatch` 清理结束画面渐变特效 `#=z40N_lqqB8daJ` 与残留 `WaitCursor`，防止结束界面退出卡死与沙漏光标锁死。

### 4.2 无状态回放与暂停微操（`NetCommand.cs` & `CoopSessionManager.cs`）
- `CommandExecutor` 全量使用 `object` + `GameBridge` 反射，**绝不读取或修改 `CSelectable`**；执行期间临时模拟指令携带的 `IsShiftQueued` 按键状态并带有 `_executionDepth` 递归深度保护。`ExecuteSingleOrBatchCommand` 支持按 `BuildingTypeName + QueueSlotIndex` 精确解析槽位敏感指令并在取出奖励单位后立即 `FlushStartFramePostMethods()`；`ExecutePlaceBuilding` 为 `IsBonusBuilding` 校验剩余次数并调用 `ConsumeBonusEntityByType`，普通建造在执行前复验 `CanExecuteCommandOnEntity` + `CanPlaceBuildingAtTarget` 并在落盘后立即 `FlushStartFramePostMethods()` 刷新网格占位，保证并发建造不重复扣费。
- **暂停态实时微操**：当 `LockstepClock.PausedBySession == true` 或本地模态弹窗打开时，`UpdateGameLogic` 不触发，由 `CoopSessionManager.OnFrameTick()` 在 `RunInDeterministicContext` 保护下对冻结的当前 `pausedTick` 主动调用 `FlushDueTurnCommands(pausedTick)`，从而在暂停下即时执行建造/拉兵/训练/解暂/市长选择指令并立即渲染上屏。

---

## 5. 内存存档流同步、反压流控与 `Auto-resync`

1. **同名 `.zxcheck` 校验、重载屏障锁与乱序分片重组**：
   - 引擎 `ReadSaveFile` 校验存档时将 `.zxcheck` 与 `.zxsav` 文件名绑定。Host 与 Client 统一读写 **`coop_host_snapshot.zxsav` / `coop_host_snapshot.zxcheck`**。
   - 客机使用 `Dictionary<int, byte[]>` 按分片索引缓冲重组，容忍传输抖动重排；Host 与 Client 在快照导出、自重载与等待 `SnapshotLoadedAck` 的屏障期内统一锁定 `DXGame.InputEnabled = false` 并在指令拦截层拒绝操作，彻底杜绝等待队友期间建造引发开局即分叉。
2. **浅调用栈导出/加载与局内本地编队保留**：
   - `OnLevelLoadComplete`（`ZXSystemGameLevel.#=zwK_pUIeRBpbp`）或收到分片后只设置 `_pendingHostSnapshotExport` / `_pendingClientSnapshotLoadPath` 标志，统一延迟到下一帧 `CoopSessionManager.OnFrameTick()` 顶层浅栈执行 `WriteSaveFile` / `LoadSaveFileAndEnter`。
   - 为避免局内 `Auto-resync` 替换 `ZXLevelState` 冲掉玩家各自的 `1~8` 本地编队，`CoopSessionManager` 以 `_hasEnteredSyncedLevelInMatch` 门控仅在局内重同步前调用 `GameBridge.CaptureSelectionGroupEntityIds()` 备份，并在 `OnLevelLoadComplete` 调用 `RestoreSelectionGroupEntityIds()` 还原并刷新左下角编队 HUD 槽位。
3. **无焦点淡入推进与 `CShadow` NRE 修复**：
   - 客机加载期间强制 `Focused = true` 并每帧调用 `GameBridge.KickLoadingFade()` 推进原生黑屏淡入动画，使 `loadAction` 顺利执行。
   - 调用读档前执行 `GameBridge.PrepareForProgrammaticLoad()`：显式将旧 `CurrentGameSystem.Enabled = false`、置 `Paused = true` 并调用 `ClearEntities()`，补齐原版 `LoadGameAndEnter` 第 229 行 `== null` 空判写反导致的二次读档 `CShadow` `NullReferenceException`。
4. **周期校验、自适应延迟、主机反压与 `Auto-resync`**：
   - Host 通过 `RttPing/RttPong`（优先采用 `DirectFastPath` 线速 RTT 与快降慢升 EWMA `0.40`/`0.14`）动态调整 `InputDelayTicks (1..4)`，并通过 `RttPong` / `AllyCursorAndCamera` / `ClientInputReport` 实时收集 `ClientSimTick`，由 `GetHostPaceAdjustment` 在客机落后 `>=5t`（`1.15x, max=2`）、`>=6t`（`1.38x, max=1`）、`>=9t`（暂停推进，带 `3.5s` 失联保护）实施平滑反压，防止慢机积压数秒操作延迟。
   - 每 60 逻辑帧比对 `StateHash`；默认 `async_align` 模式下，通过 `TrySendPosDriftSync` / `OnPosDriftSyncChunkReceived` 将高频血量/击杀修正（`2s`）与低频软位置对齐（`8s`、`MinAsyncPosSnapCells = 0.50f`、单实体 `180 tick` 冷却去抖）解耦，并结合 `_unitOwnership` 租约（300t TTL / 90t 闲置）保护客机自控单位免被拉回、由客机随 `StateChecksumReport` 捎带反向轻量对齐；同时仅当 `missingOnHost <= 8 && missingOnClient <= 4` 时执行补杀以防 ID 偏移误杀尸潮，收紧 `entityTol = Clamp(EntityCount/1000, 12..30)` 与 `MissingOnClient >= 10`（连续 4 次，`30s` 结构冷却）触发 `Auto-resync`。

---

## 6. 协同 UX 渲染（`UI/`）

- **联机等候大厅与菜单生命周期（`CoopLobbyWindow.cs` & `MainMenuPatch.cs`）**：在主菜单注入 `CO-OP MULTIPLAYER` 按钮、`MainMenuOnLoadCompletePatch` 与 `ReturnToMainMenuPatch`，基于原版 `ZXWindow(BigWindow)` 与 `ZXButtonSteam` 构建入口探测视图与 3 槽位等候房间（切换文本/颜色前先清理 `DXTextObject.Image` 避免 `Direct3DEx` 下 `ReLoadInGPU()` 纹理斜切），并在客机全员就绪后通过反射复用单人模式的读档/新建生存/自定义地图弹窗，取消选图或从局内返回主菜单时保留网络连接自动重开大厅。
- **小地图队友视野框（`AllyOverlayRenderer.cs` → `AllyMiniMapOverlayPatch`）**：Hook `OMiniMap.OnRender` Postfix，为队友创建带 `DXImage.FromColor(Color.Transparent)`、`Z = 1001`、`PixelAligned = true` 与 `2px` 边框的 `DXObject`，通过 `DXHelper_Rectangle.ChangeCoordinates(ally.CameraWorldArea, PlayableArea, miniMap.InnerArea)` 实时更新。
- **暂停提示与 3 秒恢复倒计时（`PauseSyncController.cs`）**：暂停横幅使用 `durationMs = 0` 确保暂停帧立即 100% 不透明显示；恢复倒计时在 `DXIsometricGridObject.Current` 上挂载顶居中固定像素 `DXTextObject`（`Resume in 3/2/1...`），解冻时立即移除。
