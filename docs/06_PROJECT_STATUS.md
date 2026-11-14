# 06 — 当前项目状态、门禁验收报告与下一步计划 (Project Status)

> **最后更新**: 2026-11-14（真人实机缺陷修复、`async_align` 客机低频平滑纠偏与双端并发指令实测完成）
> **构建基线**: `TABCoopMod.csproj` Release 编译 `0 警告 0 错误`；实机注入 `GameBridge 0 missing`，Harmony 补丁 **`Classes OK=54, failed-tolerant=0`**（含异步单 tick 流水线、视觉插值/平滑相机、`ZXBehaviour` 无状态行为随机作用域、输入去重建造保护与市长单次生效拦截）。
> **历史过程记录**: 早期排查过程与会话 1~6 演进流水已归档至 [`docs/archive/`](archive/SESSION_CHANGELOG_S1_TO_S6.md)。

---

## 1. 全门禁与自动化实测验收总表（2026-11-13 实测）

| 门禁 / 用例 | 测试存档与条件 | 实测关键指标与结论 | 状态 |
| :--- | :--- | :--- | :-: |
| **Gate A1（最新回归）** | `1_Backup.zxsav` / 120 ticks 双进程回放 | `[compare] PASS: 120 ticks identical`；`common=2125, moved(>0.01)=0, maxDx=0, maxDy=0` | ✅ 通过 |
| **Gate A2** | `6666.zxsav` / 180 ticks 双进程回放 | `[compare] PASS: 180 ticks identical`；`common=7842, moved(>0.01)=0, maxDx=0, maxDy=0`（加载仅耗时 ~7.87s） | ✅ 通过 |
| **Gate B2** | `6666.zxsav` / 65s Loopback (`latency=0`) | Tick `168484..169440`（956 ticks）连续 **17/17 次 `StateChecksum` 全部 `MATCH`**，`0 DESYNC`，`0 Auto-resync`，`0 ERROR` | ✅ 通过 |
| **Gate B1** | `1_Backup.zxsav` / 65s Loopback (`latency=0`) | Tick `31904..32880`（18 次）全部 `MATCH` → Tick `32940` 原生求解器 `0.002` 格 `Pos` 漂移触发 `Auto-resync`（约 `3.07s` 恢复至 Tick `33058 MATCH`）→ Tick `33058..33480`（9 次）全部 `MATCH`，`0 Client ERROR` | ✅ 通过 |
| **Chaos-1（故障注入）** | `1_Backup.zxsav` / 60s `ChaosTransport` (`latency=120;jitter=30;loss=0.02;disconnect=4000-5000;seed=42`) | `9924` 实体对齐；Tick `31904..32880` 连续 **18 次 `MATCH`** → Tick `32940` 触发一次 `Pos` 微偏 `Auto-resync`（约 `3.12s` 重建对齐 Tick `33075`）→ 继续连续 **6 次 `MATCH`**，`0 Client ERROR` | ✅ 通过 |
| **Chaos-2（故障注入）** | `6666.zxsav` / 60s `ChaosTransport` (`latency=80;jitter=20;loss=0.01;seed=7`) | `33597` 实体对齐；Tick `168484..169380` 连续 **16/16 次 `StateChecksum` 全部 `MATCH`**，`0 DESYNC`，`0 Auto-resync`，`0 ERROR` | ✅ 通过 |
| **Gate C** | `1_Backup.zxsav` / 控制通道 + 截图 | 双端独立相机位置/缩放同步运行；截图目视确认本地白框与队友彩色视野框（Host `DodgerBlue` / Client `SpringGreen`）在小地图正确渲染 | ✅ 通过 |
| **Gate D** | `1_Backup.zxsav` / 暂停微操 + 3s 倒计时 | Client 发起 `request_pause(true)` 同步冻结于 Tick `31972`（显示 `Player 2 Paused Game`）；暂停期间 `move_units` 派发与资源扣减（`gold 355→235`）双端一致；Host 恢复后顶居中 `Resume in 3...` 清晰可见，解冻后单位于双端同步抵达 `(149.49, 132.35)` / `(149.42, 132.01)`，全程 Tick `31904..32100` 连续 5 次 `MATCH` | ✅ 通过 |
| **真人联机缺陷修复** | 双开实机反馈修复与代码闭环 | ① 修复视角卡顿与逻辑阻塞（异步单 tick 流水线 + `RenderVisualInterpolationPatch` 视觉插值 + `SmoothCameraPanPatch`）；② 修复单位无法完成攻击动作（`GameTimeIsolationPatch` / `CSpritePullIsolationPatch` / `CMovableSpriteProgressSyncPatch` 隔离渲染时间污染）；③ 修复重叠放置建筑与无视资源不足（`Patch_ZXCommand_Execute` 预检 + `_recentLocalBuilds` 同面积去重，`CommandExecutor.ExecutePlaceBuilding` 校对 `CanExecuteCommandOnEntity` / `CanPlaceBuildingAtTarget` 并调用 `FlushStartFramePostMethods()`）；④ 修复双端各自选市长领双倍奖励（`MayorModalTrackerPatch` + `MayorSyncPatch` 按 `mayorLevel` / `MayorStates` 幂等去重并自动关闭对端弹窗） | ✅ 已修复 |
| **客机平滑与频次优化** | `1_Backup.zxsav`（120 tick）+ `900survival.zxsav`（`async_align`） | 新增 `ZXBehaviourRunRngScopePatch`（`DynamicMethod TABCoop_GetBehavOwnerEntityId`）按实体 ID + Tick 的无状态 SplitMix64 行为随机流，消除游荡僵尸消耗共享 RNG 引发的级联漂移；`CoopSessionManager` 解耦高频血量/击杀同步（`2s`）与低频软位置对齐（`8s` + 单实体 `180 tick` 冷却去抖），分级阈值 `MinAsyncPosSnapCells = 0.50f` + 移动中单位速度阻尼 + `600ms / τ=180ms` 视觉平滑衰减；`1_Backup` 回放 `120 ticks identical` | ✅ 通过 |
| **并发冲突与建造实测** | `900survival.zxsav`（18,074 实体 / 双端 ControlServer 同时发令） | 同批 3 单位同时反向 `move_units` 按确定性顺序收敛至同一目标（`Host/Client` 均抵达 `(132.89, 114.81)`）；同格 `(120.5, 134.5)` 并发 `ZX.Entities.WallWood` 仅建 **1 座**（`WallWoodDelta Host=1 Client=1`，木材 `20→17` / 金币 `799` 一致，双端同报 `Rejected PlaceBuilding`）；不同格 `(122.5, 132.5)` 与 `(122.5, 134.5)` 并发均建 **2 座**（`WallWoodDelta Host=2 Client=2`，木材 `14→8` / 金币 `769`）；两轮共 28/28 + 22/22 次 `StateChecksum` 全部 `MATCH`，`0 ERROR`，`0 AsyncPosAlign` 强制瞬移 | ✅ 通过 |

---

## 2. 已确认的工程边界与行为特性

1. **原生 Chipmunk 求解器极微小漂移 + `Auto-resync` 自愈**：
   - 在密集重叠僵尸群下，非托管 `DXPhysicsNative.Space_Step` 偶发 `1e-4` 级别速度微差，累积数十帧后可能产生约 `0.002` 格位置偏离（已通过 `PRE/POST DXPhysicWorld.Step` 验证全部托管输入完全一致）。
   - 已启用的 `Auto-resync` 能在检测到 `StateHash` 不一致后约 **3 秒内**自动重建并恢复一致状态继续锁步，且二次读档已无 `CShadow` NRE。
2. **纯视觉/环境实体不参与对局状态哈希**：
   - 裸 `DXVision.DXEntity`（受击飙血/弹花贴图）与 `Raven`（环境飞鸟）仅作用于视觉表现，在 `StateHasher` 与 `DeterministicReplayVerifier.DumpPositions` 中已明确跳过。
3. **同进程重载 (`inproc`) 仅用于崩溃/资源清理校验**：
   - `PrepareForProgrammaticLoad()` 已彻底消除关卡内二次读档 `CShadow` NRE，但 `inproc` 模式下引擎仍存在跨局静态残留实体总数偏差；确定性比对统一以**双进程模式**为准。
4. **新建游戏地图种子当前固定**：
   - 房主创建联机房间时即启用确定性沙盒并固定使用常量 `CoopSessionManager.SyncSeed = 20261109`，且每次 `ZXSystemGameLevel.InitLevel` 前会执行 `ResetSyncedRandomStreamsForInitLevel()` 与 `DXRandomCtorPatch` 重置；因此在为非自动化测试会话接入动态 `SyncSeed` 随机化之前，房主在联机状态下多次点选“新游戏”都会生成同一张地图（与正常退出还是强退无关）。
5. **未实机覆盖项（需人工桌面/双账号参与，不可假设已完成）**：
   - **真人键鼠操作全链路扩展**：大波尸潮混战下客机位置平滑体感、Shift 连续建造与全部建筑/部队热键组合的长期稳定性；
   - **Steam Lobby P2P 跨机联机**：`SteamP2PTransport`（内嵌 Steamworks.NET v10）需要双 Steam 账号或跨机实测。

---

## 3. 下一步最高优先级任务（Next Steps）

1. **P1 — 真人输入全流程联调（人工桌面测试）**：
   - 在游戏内进入单机拦截模式（`F8`）及双开 Loopback 联机模式，人工逐项验证：框选右键移动/攻击、小地图右键移动、Shift 连续建造与普通放置、兵营训练/取消队列、空格与右上角按钮暂停、`+`/`-` 倍速、市长二选一弹窗、`Alt+左键` 地面+小地图同步打点。
2. **P2 — Steam P2P 跨机联机验收（双 Steam 账号）**：
   - 使用双 Steam 账号/双机测试 `CoopLobbyWindow` 的创建房间、好友邀请、快照传输与 Steam P2P 全流程联机（`ChaosTransport` 故障注入与断连回归已在本地双开全通过）。
3. **P3 — 协同 UX 完善与 Phase 4 预研**：
   - 补全大地图地面队友彩色鼠标光标与建造预览框渲染；细化市长“客机意向高亮 + 房主拍板”交互；视情况启动 Phase 4 独立基地模式。
