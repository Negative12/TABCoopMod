# 13 — 对齐重载频率优化与异步增量对齐设计规划（`v0.1.8+` 演进方案）

> **整理日期**: 2026-11-19  
> **基准版本**: `v0.1.8`（`async_align` 混合同步基线）  
> **定位**: 本文档系统分析当前全量快照重载（`Auto-resync`）的触发瓶颈与真实诱因，提出“**Phase 1 零副作用治本降噪 → Phase 2 经济与生产队列轻量异步化 → Phase 3 深度战斗与动态实体增量补齐**”的三阶段详细设计规划，并评估各异步修正项的视觉效果与平滑护栏。

---

## 1. 背景与现状基线（`v0.1.8` `async_align` 架构盘点）

### 1.1 已有异步对齐机制
截至 `v0.1.8`，[`CoopSessionManager.cs`](../src/TABCoopMod/Sync/CoopSessionManager.cs) 与 [`LockstepClock.cs`](../src/TABCoopMod/Determinism/LockstepClock.cs) 已建立起基础的“确定性 Lockstep + 后台增量软纠偏”双层架构：
- **动态位置软对齐（`8s` 周期 + `180 tick` 单实体冷却）**：由 Host 发送偏差桶内的 `EntityPosSample`（`PosDriftSyncChunk`），Client 对超过死区阈值（静止玩家单位 `0.50f`、移动玩家单位 `1.80f`、告警僵尸 `0.55~0.85f`、游荡僵尸 `1.25f`）的动态实体按阻尼拉回，并经 `RenderVisualInterpolationPatch.RecordVisualSnapOffset` 在 `600ms`（`τ = 180ms`）内做指数视觉平滑衰减。
- **控制权差分反向对齐（`docs/11`）**：基于 `_unitOwnership`（`OwnershipLeaseTicks = 300` / `OwnershipIdleGraceTicks = 90`），Client 对自控单位在 `< 3.5` 格内免受 Host 拉回，并随 `StateChecksumReport` 捎带自控活跃单位坐标由 Host 反向对齐。
- **血量与零星补杀（`2s` 周期）**：通过 `LifeFixes` 在同 Epoch Tick 上对齐 `CLife.Life` 差值（保留自校验点后的本地增减），并在 `missingOnHost <= 8 && missingOnClient <= 4` 时由 Client 补杀残留动态僵尸。
- **容差吸收**：对裸 `DXEntity`（血迹/弹花）与 `Raven`（环境飞鸟）跳过哈希；对瞬时投射物差（`projDiff`）和网格内轻度仇恨波动（`softAggroDrift`）不立即中断对局。

### 1.2 仍触发全量快照重载（`Auto-resync`）的 5 条路径
每次 `Auto-resync` 需经历「Host 导出 `.zxsav` + Host 自重载 + 分片传输 + Client 重载」，会带来 **8 ~ 15 秒全屏黑屏与交互中断**。目前 [`CoopSessionManager.cs`](../src/TABCoopMod/Sync/CoopSessionManager.cs) 中仍有以下 **5 条路径** 会强行触发全量重载：

| 重载触发规则 | 判定逻辑（每 `EpochTicks = 60` ≈ `2s` 检查一次） | 冷却时间 | 实机日志中的常见诱因 |
| :--- | :--- | :---: | :--- |
| **1. `EconReloadStreak = 3`** | `Gold/Wood/Stone/Iron/Oil`、`ReservationChecksum`（食物/电力/工人/人口）、`ProductionChecksum`（训练队列）或 `GameTimeMs` 连续 **3 次（6s）** 不一致 | `120s`（前 3 轮可豁免） | **最高频诱因**：建造预览供电污染、临界敌军距离或临界资源导致 `PlaceBuilding`/`Train`/`Upgrade` 单边失败；经济建筑完建/感染时机差 1 跳 |
| **2. `MissingOnClientReloadStreak = 4`** | Client 缺失 Host 实体 `MissingOnClient >= 10` 连续 **4 次（8s）** | `30s` | 建筑感染爆僵尸、兵营出兵、局部事件刷怪在客机漏触发或 ID 偏移 |
| **3. `StructuralReloadStreak = 6`** | 全图实体总数差 `\|EntityDelta\| > entityTol`（`Clamp(EntityCount/1000, 12..30)`）连续 **6 次（12s）** | `30s` | 同上，或表现层市民 `Worker_A`/`Worker_B` 随建筑批量启停产生计数波动 |
| **4. `WidePosReloadStreak = 10`** | `AsyncPosAlign` 拉回后仍持续 `rmsDelta >= 2.5f && drifted5 >= 25` 连续 **10 次（20s）** | `120s` | `AsyncPosAlign` 仅改坐标未改仇恨目标/寻路点，单位被拉回后继续向错误目标行军并扩散碰撞偏差 |
| **5. `LongPeriodRealignMs = 300,000ms`** | `econCoreDiff \|\| lifeDiff \|\| absEntityDelta > 2` 持续满 **5 分钟** 未归零至 `FullHash MATCH` | `120s` | 后期大波尸潮混战持续超 5 分钟，每轮总有正在掉血的僵尸存在瞬时 `lifeDiff`，计时器始终无法清零 |

### 1.3 日志溯源实锤（`TABCoopMod_client_4.log` 与 `TABCoopMod_host_2.log`）
结合归档实机日志可以清晰看到两类“本可避免”的重载放大器：
1. **非战斗市民 `Worker_A` 霸占位置漂移榜首**：在 [`logs/TABCoopMod_client_4.log`](../logs/TABCoopMod_client_4.log#L539-L674) 中，有长达一分多钟的周期几乎每次位置漂移最大值都指向基地内走动的 `Worker_A`（`MaxDelta = 3.28 ~ 7.93` 格，`Rms = 1.11 ~ 2.01`），直接把无关表现实体算进了 `Drifted5` 和 `RmsDelta`。
2. **单边拒建引发连锁 `econ x3` 与 `MissingOnClient`**：同一日志中连续出现 `[CommandExecutor] Rejected PlaceBuilding ZX.Entities.Farm` 与大批量 `[CommandExecutor] Rejected Upgrade ... CanExecute=false`，以及 [`logs/TABCoopMod_host_2.log`](../logs/TABCoopMod_host_2.log#L2105) 出现的 `long-period realign after 301s (Life=True Aggro=True Entity=True Econ=False)`（经济完全一致仍被 5 分钟定时器强制重载）。

---

## 2. Phase 1：零副作用治本降噪（优先消除假阳性漂移与单边拒建分叉）

Phase 1 不引入任何状态覆盖或网络包扩展，直接清除引擎表现层副作用导致的假漂移与误重载。

### 2.1 隔离表现层市民 `Colonist`（`Worker_A` / `Worker_B`）
- **逆向依据**：
  - 反编译 `ZX.Entities.Colonist`、`ZX.Entities.Worker_A` 与 `ZX.Entities.Building` 确认：当玩家建筑通电启用（`Enabled && Team == Player`）时，`Building.#=z0RIVd3YfBl848VD92A==()` 生成若干 `Colonist` 实体，停电或禁用时由 `Building.#=z8ftB$ICGkikKzzCP1g==()` 回收。
  - `Colonist` 仅挂载 `CSalvable`，无 `CLife` 血条组件、`_BodyCategoryMask = 512u`（不参与战斗也不阻挡建筑放置）、仅在建筑之间随机散步并在进入建筑时将 `SceneObject.Opacity` 渐变为 `0`，实际资源生产由 `Building` 自身逻辑驱动。
  - 然而在当前 [`StateHasher.cs`](../src/TABCoopMod/Determinism/StateHasher.cs#L214-L227) 中，`Colonist` 未被忽略，既推高了 `PosDrift` / `RmsDelta`，也在生成/释放时消耗 `_syncedEntityIdCounter` 并波动 `EntityCount`。
- **详细方案**：
  1. **哈希隔离**：在 `StateHasher.AccumulateEntity` 的类型标志缓存 `_entityTypeFlags` 初始化分支中，识别基类或类型名属于 `ZX.Entities.Colonist`（含 `Worker_A` / `Worker_B` 等子类）的实体，直接标记为 `FlagIgnore`（与 `DXEntity` / `Raven` 同级跳过）。
  2. **ID 轨道隔离**：在 [`LockstepClock.cs`](../src/TABCoopMod/Determinism/LockstepClock.cs) 的 `VisualVfxIsolationPatch`（或独立补丁）中挂钩 `ZX.Entities.Building.#=z0RIVd3YfBl848VD92A==` 的 `Prefix` / `Finalizer`，调用 `EnterVisualVfxContext()` / `ExitVisualVfxContext()`，使其生成的 `Colonist` 统一进入 `0x8000...` 本地视觉 ID 轨道，彻底不触碰同步 ID 计数器与物理/哈希链路。
  3. **同步排他检查**：确保 `DeterministicReplayVerifier.DumpPositions` 也跳过 `Colonist`，避免干扰回放对比。

### 2.2 剥离本地建造预览供电圈（`OCellAreaCursor`）对共享电网数组的污染
- **逆向依据（关键发现）**：
  - 反编译 `ZX.GUI.OCellAreaCursor` 与 `ZXSystemMapLayers`（`#=zJme8KFhmikprnkg_2CDeiQE=`）确认：当本地玩家进入建造模式且选中供电半径 `EnergyTransferRadius > 0` 的建筑（如特斯拉塔、风车、发电厂）时，`OCellAreaCursor.#=zwffgY1s=()` 会直接调用 `ZXSystemMapLayers.#=zuzVFgSx4Gz1THobHGA==(entity, point, radius, #=z4ug3TKTAjguD: true)`，**向全局共享电网计数数组 `#=zVXTH1ahoIxQNg$omKQ==[x, y]` 临时写入 `+1`**（并在光标移开或退出建造时通过 `#=zglihyOZQi982dUVN0g==()` 写 `-1` 还原）。
  - 在执政官双人联机下，玩家 A 把特斯拉塔悬停在基地外圈时，A 机器的真实电网已向该区域临时供电，而 B 机器的电网没有这段预览加成。若此时双方回放同一回合指令（玩家 B 恰好在边缘拍下屋子，或玩家 A 在预览生效期间发出并发指令），[`GameBridge.CanPlaceBuildingAtTarget`](../src/TABCoopMod/Core/GameBridge.cs#L2434-L2470) 在两台机器上会得出相反的 `CanPlaceAtCell` 结论，导致**单边建出建筑 → 瞬间 `EntityCount` 与 `Gold` 分叉 → 6 秒后 `econ x3` 重载**。
- **详细方案**：
  1. 在 [`ObfuscatedSymbols.cs`](../src/TABCoopMod/Core/ObfuscatedSymbols.cs) 与 [`GameBridge.cs`](../src/TABCoopMod/Core/GameBridge.cs) 补充绑定 `OCellAreaCursor` 的方法：
     - `Field_LevelSys_BuildAreaCursor`（已存在：`#=zZ$S0aaS3NQsgmtkOww==`）
     - `OCellAreaCursor.#=zglihyOZQi982dUVN0g==()`（临时撤销预览供电加成，保留当前建造状态，仅清理 `LastCellWithEnergyTransfer` 对电网数组的 `+1`）
     - `OCellAreaCursor.#=zwffgY1s=()`（恢复更新预览框及供电范围渲染）
     - 或更轻量地直接检测 `LastCellWithEnergyTransfer`（`#=zOgOjDSANK0WHtgVX_wzbxBg=`）是否有值：仅当有值时在回放前置零（调用 `#=zCxSW5$IejUeGjg9Ynw==` 扣回 `-1`），回放后重新调用 `#=zuzVFgSx4Gz1THobHGA==` 补回 `+1`。
  2. 在 [`CommandExecutor.Execute`](../src/TABCoopMod/Commands/NetCommand.cs#L191-L301) 的外层 `try / finally` 中（或 `FlushDueTurnCommands` 批处理外层包裹一次以降低重复开销）：回放回合指令前调用 `SuspendLocalBuildCursorEnergyPreview()`，`finally` 中调用 `RestoreLocalBuildCursorEnergyPreview()`，保证确定性逻辑与建造校验看到的电网与本机鼠标悬停完全无关。

### 2.3 权威回合 `PlaceBuilding` / `Upgrade` 回放端校验对齐
- **现状问题**：反编译 `ZX.Entities.Structure.#=z2IRuTX3OAzJgkAHNDKW65bN9UxnV`（即 `CanPlaceAtCell`）发现其包含动态实体检测 `HasNearEnemies`（`DXWorldGrid.AnyEntityInCellCircle(Cell, 4, 6291456u)`）。当一只游荡僵尸在 Host 距建筑 `4.05` 格（允许建造）、在 Client 因物理微差位于 `3.98` 格（禁止建造）时，[`CommandExecutor.ExecutePlaceBuilding`](../src/TABCoopMod/Commands/NetCommand.cs#L383-L458) 中的 `CanPlaceBuildingAtTarget` 会单边拒绝已经进入权威回合的建造指令。
- **详细方案**：
  - **拦截层保持严格**：本地玩家点击建造时，[`CommandInterceptorPatch.cs`](../src/TABCoopMod/Patches/CommandInterceptorPatch.cs) 与原版 `OCellAreaCursor` 依然执行完整检查（含近敌禁止、资源预扣）。
  - **回放层剥离动态微差否决**：在 `ExecutePlaceBuilding` 校验 `CanPlaceBuildingAtTarget` 期间引入局部线程/作用域标记 `InReplayBuildPlacementCheck`，在 `Structure.HasNearEnemies` 或对应校验路径仅对确定性静态地形与已占位建筑做碰撞冲突拦截，不再因为动态单位 `0.1` 格漂移单边否决建造。

### 2.4 收紧 5 分钟 `LongPeriodRealignMs` 计时条件
- **现状问题**：[`CoopSessionManager.cs#L3008`](../src/TABCoopMod/Sync/CoopSessionManager.cs#L3008) 的定义是：
  ```csharp
  bool substantiveDiff = econCoreDiff || lifeDiff || absEntityDelta > 2;
  ```
  这意味着在游戏后期长达 10 分钟的尸潮防御战中，只要持续交火导致每 2 秒总有几只僵尸处于 `lifeDiff` 状态（即使 `LifeFixes` 每 2 秒都在正确对齐且经济/建筑完全一致），`_divergedSinceTicks` 也永远不会清零，每到 300 秒就会强行黑屏重载一次。
- **详细方案**：
  - 将 `substantiveDiff` 收紧为**未被异步机制覆盖或超越可修复容差的持续偏差**：
    ```csharp
    bool substantiveDiff = econCoreDiff || absEntityDelta > entityTol;
    ```
  - 当 Phase 2 将经济资源与生产队列也纳入异步对齐后，仅当出现**无法自动收敛的大规模结构性分叉或时钟偏移**时，才累积 `LongPeriodRealignMs` 兜底计时器。

---

## 3. Phase 2：经济与生产队列的轻量异步化（消灭 70%+ `econ x3` 重载）

在所有引起全图重载的原因中，`EconReloadStreak = 3` 是最敏感、最容易被“1 个金币或 1 个训练槽位偏差”触发的规则。Phase 2 通过扩展现有的 `2s` 同步包，将经济与生产队列改为**主机权威的后台增量拉平**。

### 3.1 五维库存资源（`Gold / Wood / Stone / Iron / Oil`）同 Tick 差值异步对齐
- **设计原理**：不能直接把 Host 在 Tick $T$ 的绝对资源值赋给 Client 在当前帧 $T + \Delta t$ 的游戏状态（否则会吞掉 Client 在 $\Delta t$ 窗口内刚发生的正常跳钱或花费）。必须基于**同 Epoch Tick $T$ 采样快照做差值叠加**。
- **实现步骤**：
  1. **Client 缓存历史资源快照**：在 `CoopSessionManager.CacheTickPositions` 旁边扩展 `_tickEconCache`（同样保留最近 16 个 Epoch Tick），记录每个 Epoch Tick $T$ 的 `(Gold, Wood, Stone, Iron, Oil)`。
  2. **Host 随包下发权威资源**：扩展 `PosDriftSyncChunk` 的压缩头字段：
     - `flags` 增加 `bit 3 = flagApplyEcon`；
     - 当本轮检测到 `Gold/Wood/Stone/Iron/Oil` 不一致（或按需固定随包携带仅 20 字节）时，写入 Host 在 Tick $T$ 的 5 个 `int32` 资源值。
  3. **Client 主线程差值应用**：在 `DrainAsyncQueues()` 中取出对应的 `(Tick, HostEcon)`，查表得到客机在同 Tick $T$ 的资源 `ClientEcon(T)`，计算差值：
     $$\Delta R_k = R_{k,\text{Host}}(T) - R_{k,\text{Client}}(T)$$
     安全钳位后直接叠加到当前 `ZXLevelState` 的对应属性（经 `GameBridge.SetLevelProperty` 写入，下限钳位为 `0`）。
  4. **Host 状态机联动**：在 `VerifyTickChecksum` 中，若本次仅库存资源存在偏差且已成功触发/下发 `flagApplyEcon` 修正包，允许宽容窗口（如仅当连续 `>= 6` 轮修正后差值仍单调扩大或超过极大阈值时才升级为硬重载），并将单一库存微差从立即触发 `HARD DESYNC` 中解耦出来。

### 3.2 建筑生产/训练队列（`ProductionChecksum`：`CCommandable.OrdersQueued`）异步对齐
- **逆向依据**：
  - 反编译 `ZX.Components.CCommandable` 确认：每个生产/升级建筑的队列由当前执行命令 `#=z8dbvOmM=`（`IDCommand`）与排队列表 `OrdersQueued`（`#=zInqOyMYqRiTWp0BtDybJQZRXot_2`，类型 `List<ZXOrder>`，上限 `4` 槽）组成，每个 `ZXOrder` 持有对应 `ZXCommand`（`#=zE83e_h4=()`）与 `Target`。
- **实现步骤**：
  1. **差异检测与按需打包**：当 `VerifyTickChecksum` 连续 `2` 个 Epoch 发现 `host.ProductionChecksum != client.ProductionChecksum` 时，置 `flagApplyProd = true`。
  2. **数据载荷格式**（全图有生产队列的建筑极少，通常 $< 20$ 座，载荷仅数百字节）：
     - 遍历玩家阵营含 `CCommandable` 且 `OrdersQueued != null` 的建筑，记录 `(ulong EntityId, int CurrentCmdId, byte QueuedCount, int[] QueuedCmdIds)`，并在 Host Epoch 采样点同步缓存该快照。
  3. **Client 主线程重建队列**：
     - 定位 `DXGame.Current.GetEntityByID(EntityId)`，获取其 `CCommandable`；
     - 在保持建筑正在施工/生产状态的前提下，对比并对齐 `OrdersQueued` 中的 `ZXOrder` 列表（通过 `GameBridge.GetCommandById(cmdId)` 构造 `ZXOrder` 并写入 `OrdersQueued`，不额外重复扣费，因五维资源已由 3.1 同步对齐）；
     - 同步调用 `ZXSystemGameLevel.#=zR1qHCYY=()`（`Method_LevelSys_RefreshCmdPanel`），保证正在查看该建筑的玩家右下角面板立即刷新。

### 3.3 静态建筑差集（`Structure` 异步补建/补拆）与 `ReservationChecksum` 自愈
- **设计目标**：解决极少数因并发/边界条件造成的单边漏建或漏拆（`1 ~ 4` 座建筑差异，连带破坏 `EntityCount`、`Gold` 和食物/电力/工人保留量 `ReservationChecksum`）。
- **实现步骤**：
  1. **Host 差集扫描**：当 `absEntityDelta` 在 `1..8` 且主要由静态建筑引起（对比静态实体 ID 桶 `EntityBuckets`）时，Host 打包玩家侧静态 `Structure` 的精简清单 `(EntityId, TemplateId, CellX, CellY, Width, Height, Life, IsBeingBuilt)`。
  2. **Client 主线程补建/补拆**：
     - **Host 有而 Client 无（补建）**：通过 `DXProject.Current.EntityTemplateFromID(TemplateId)` 生成实例，强制绑定 `entity.ID = HostEntityId`（不消耗本地 ID 生成器），设置网格区域 `CellArea`、建筑进度 `CBuildable.BuildingFactor`（或 `IsBeingBuilt`）与 `Life`，执行占格更新与 `AddToScene()`，并触发 `Build` 同款 `200ms` 升起动画；
     - **Client 有而 Host 无（补拆回滚）**：调用安全销毁并清理占格与 `PostMethods`，退回的偏差已由 3.1 资源对齐统一校准；
     - **重算建筑保留资源与电网**：对变动建筑调用 `Building.#=z3rO8Jok=()`（重算 `Food/Energy/Workers` 供应）并刷新电网与 `SysColonists` 列表，从而同步拉平 `ReservationChecksum`。

---

## 4. Phase 3：深度战斗与动态实体异步化（压降 `WidePosReloadStreak` 与 `MissingOnClient`）

### 4.1 接战单位仇恨目标（`BData.TargetRef`）与移动终点异步对齐
- **痛点分析**：当前 `AsyncPosAlign` 只更新 `DXEntity.Position` 与 `DXPhysicBody.PhysicPosition`，不修正 `BData` 锁定的目标和 `CMovable` 的寻路终点。当僵尸群或巡逻单位在两端分别锁定了不同目标时，只拉坐标不改目标会导致被拉回的单位下一帧立刻掉头往原目标跑，反复拉扯直到触发 `WidePosReloadStreak = 10` 重载。
- **详细方案**：
  1. **增量采样**：在 `StateHasher.EntityPosSample`（或仅在 Host 生成 `PosDriftSyncChunk` 时按需补充）中，对 `isAlerted` 僵尸或处于移动中的动态实体，额外附带：
     - `ulong TargetEntityId`：来自 `BData.#=zP9AEykk6714q`（`ObfuscatedSymbols.Field_BData_TargetRef`）的 `IDEntity`；
     - `float MoveTargetX, MoveTargetY`（或 `NaN` 表示无移动目标）：来自 `CMovable.#=zx1L3mWRqDcT7`（已验证于 [`GameBridge.cs#L1973`](../src/TABCoopMod/Core/GameBridge.cs#L1973)）。
  2. **按偏差门槛精准改写**：在 Client `DrainAsyncQueues` 应用坐标修正时，仅当该实体的位置偏差 `dist >= 1.0f` 格（或目标实体 ID 不一致且在视野外/刚完成位置拉回）时：
     - 若 Host 的 `TargetEntityId != 0`，查本地 `DXGame.Current.GetEntityByID(TargetEntityId)`，若存在且存活，则写回 `BData.#=zP9AEykk6714q`；
     - 若 Host 的 `MoveTarget` 与本地不同，同步更新 `CMovable.#=zx1L3mWRqDcT7` 并标记重寻路，使单位“位置回来、目标也回来”。

### 4.2 小批量缺失动态实体（`0 < MissingOnClient <= 32`）异步补生
- **痛点分析**：目前仅处理了反向的 `MissingOnHost <= 8`（客机多出怪则补杀），未处理正向的 `MissingOnClient`（客机少怪：例如某建筑感染爆怪或兵营刚出兵时客机漏生成）。一旦缺失累积到 `>= 10` 并持续 8 秒，即触发全量重载。
- **详细方案**：
  1. **轻量差集协商（两步握手或直接附带动态索引）**：
     - 当 `PosDriftStatsReport` 上报 `0 < MissingOnClient <= 32` 且 `MissingOnHost <= 8` 时，Client 在该包尾部顺带附上自己本地当前动态实体的 `EntityId`（仅对比 Host 上次发出的差异桶，或直接发送缺失查询位图），Host 定位出具体的缺失实体列表（最多 32 个）；
     - 或者更直接地，当连续 2 次 `MissingOnClient` 在 `1..32` 之间时，Host 针对差异 `EntityBuckets` 打包动态实体的 `(EntityId, TemplateId, X, Y, Life, Team, TargetEntityId)` 精简描述（每个实体约 `30 Bytes`，32 个不到 `1 KB`）。
  2. **Client 主线程确定性补生**：
     - 通过 `DXProject.Current.EntityTemplateFromID(TemplateId)` 获取模板并用 `DXEntityPool.New`（或 `CreateInstance`）创建实体；
     - **关键**：强制将其 `DXEntity.ID` 设为 `HostEntityId`（绕过本地 `DXGuid.New()`），写入坐标、生命值、`Team`、仇恨目标，调用 `AddToScene()` 加入场景与物理世界（并在 `LockstepClock` 帧末统一重排序一次 `Bodies` 维持列表序）；
     - 若补生的是 `Unit`，且玩家阵营已配置 `SysColonists` / 编队无关属性，保持正常接管。

### 4.3 保留全量快照重载（`Auto-resync`）的不可替代底线
异步增量补齐不是万能的，以下场景必须继续保留 `Auto-resync` 作为最终兜底，以免在极端分叉下产生难以清理的僵尸状态：
1. **大规模结构性失步**：单次 `MissingOnClient > 64` 或 `absEntityDelta > 64`（如整波最终尸潮刷怪失步或全局 `DXGuid` 计数器彻底错位），逐个补生刚体的耗时与风险高于一次 `.zxsav` 全图同步；
2. **主时钟脱轨**：`GameTimeMs` 存在显著持续偏差（`> 100ms`）；
3. **异步修补失效兜底**：连续 3 轮（约 6 秒）下发异步经济/生产/建筑/补生修正后，对应校验和仍然无法收敛。

---

## 5. 异步执行的视觉效果、操作体感与视觉平滑方案

将原本会导致“全屏黑屏 10 秒”的重载拆分为后台静默执行后，对局连贯性将极大提升；但在“玩家镜头恰好盯着偏差发生区域”时，不同修正项仍会产生各自特有的**微弱视觉跳变**。下表逐项说明真实视觉表现及配套润色护栏：

| 异步对齐项 | 在玩家视野外 | 在玩家视野内“裸执行”的直接视觉效果 | 配套视觉平滑 / 润色方案及最终体感 |
| :--- | :--- | :--- | :--- |
| **1. 五维库存资源**<br>(`Gold/Wood/...`) | **0 感知** | 游戏场景画面完全无变化；仅屏幕底部资源数字在 2 秒同步点出现一次微跳（如金币 `1420 → 1404`）。若玩家正卡在临界钱数点击，可能遇到点击瞬时生效但下一秒回退或扣成 0。 | • 差值极小（如 `< 50`）时人眼基本感知为正常跳钱与消耗；<br>• 可选在 HUD 渲染层增加 `300~500ms` 数字滚动插值（可选），消除突变感。 |
| **2. 兵营生产队列**<br>(`OrdersQueued`) | **0 感知** | 大地图画面完全无变化；若玩家**正选中该兵营**盯着右下角面板，会看到排队槽位突然多出一个训练图标或某个未能合法入队的图标熄灭消失，进度条小幅对齐。 | 调用 `Method_LevelSys_RefreshCmdPanel` 刷新面板；从玩家视角看与“队友同时也在点这个兵营排兵/取消队列”几乎完全一致，无违和感。 |
| **3. 静态建筑补建/补拆**<br>(`Structure` 差集) | **0 感知**<br>（切屏过去时建筑已在） | • **补建**：空地上会延迟 `1~2s` 突然冒出一栋刚拍下的建筑脚手架；<br>• **补拆（回滚）**：本地刚拍下但被 Host 否决的建筑会在 `1~2s` 后突然消失、退回资源，类似高延迟网游“放方块被服务器吞掉”。 | • **复用原生动画**：补建时复用 `Build.#=zBTwSMLo=` 的 `200ms` `RangeOpacity (0→1)` 与 `RangeCenter` 升起动画，看起来像队友刚拍下建筑；<br>• Phase 1 修复供电预览污染与放宽回放 `HasNearEnemies` 后，此类建筑差集本身将趋近于 0。 |
| **4. 仇恨目标与移动转向**<br>(`TargetRef` / 寻路点) | **0 感知** | 改造前为周期性“往左滑一步 → 继续顽固往右跑 → 8 秒后再被拉回”的鬼畜滑步；<br>改造后为：**单位平滑滑回 Host 位置的同时，立刻转身朝向与 Host 一致的目标行军**，只发生一次“改主意转向”，此后轨迹完全重合。 | 与现有的 `RenderVisualInterpolationPatch.RecordVisualSnapOffset`（`VisualSnapExpireMs = 600.0`、`VisualSnapTauMs = 180.0`、`MaxVisualSnapOffsetCells = 3.5f`）协同，滑步过渡自然、无抽搐。 |
| **5. 小批量缺失单位补生**<br>(`MissingOnClient`) | **0 感知** | 裸调 `AddToScene()` 时，镜头内可能出现几只僵尸或新兵**“凭空闪现（Pop-in）”**。 | **透明淡入护栏**：补生时先将 `SceneObject.Opacity` 设为 `0f`，通过 `DXAnimation` 或插值表在 `300~400ms` 内线性淡入至 `1.0f`（混在尸潮与建筑旁几乎无突兀闪现感；若紧邻己方兵营，可附加 `400ms` 从门口滑出的初始视觉偏移）。 |

---

## 6. 分阶段落地路线图、代码落点与门禁验证矩阵

### 6.1 分阶段实施计划与代码落点

| 阶段 | 核心交付物 | 预计改动量 | 涉及代码文件 | 预期收益 |
| :--- | :--- | :---: | :--- | :--- |
| **Phase 1**<br>（零副作用治本） | ① `Colonist` 移出 `StateHasher` 与分配 `0x8000...` 本地 ID；<br>② `CommandExecutor` 回放外层剥离 `OCellAreaCursor` 预览供电加成；<br>③ `ExecutePlaceBuilding` 回放期剥离动态近敌微差否决；<br>④ 收紧 `LongPeriodRealignMs` 计时条件 | ~120 ~ 180 行 | [`StateHasher.cs`](../src/TABCoopMod/Determinism/StateHasher.cs)<br>[`LockstepClock.cs`](../src/TABCoopMod/Determinism/LockstepClock.cs)<br>[`GameBridge.cs`](../src/TABCoopMod/Core/GameBridge.cs)<br>[`NetCommand.cs`](../src/TABCoopMod/Commands/NetCommand.cs)<br>[`CoopSessionManager.cs`](../src/TABCoopMod/Sync/CoopSessionManager.cs) | 彻底清除 `Worker_A` 假位置漂移、预览供电引起的并发拒建与后期 5 分钟必发重载 |
| **Phase 2**<br>（经济与队列异步化） | ① `PosDriftSyncChunk` 附带同 Tick 五维资源差值同步；<br>② `OrdersQueued` 生产队列异步校准与面板刷新；<br>③ `1..4` 座静态建筑异步补建/补拆与 `Reservation` 重算 | ~300 ~ 450 行 | [`CoopSessionManager.cs`](../src/TABCoopMod/Sync/CoopSessionManager.cs)<br>[`GameBridge.cs`](../src/TABCoopMod/Core/GameBridge.cs)<br>[`ObfuscatedSymbols.cs`](../src/TABCoopMod/Core/ObfuscatedSymbols.cs) | 消除绝大部分因临界资源/并发边界引发的 `econ x3` 与 `Production` 全量重载 |
| **Phase 3**<br>（深度战斗增量补齐） | ① 大偏差动态单位携带 `BData.TargetRef` 与移动目标修正；<br>② `0 < MissingOnClient <= 32` 带 `300ms` 淡入异步补生；<br>③ 保留大规模分叉（`> 64` 实体）`Auto-resync` 底线 | ~350 ~ 500 行 | [`CoopSessionManager.cs`](../src/TABCoopMod/Sync/CoopSessionManager.cs)<br>[`GameBridge.cs`](../src/TABCoopMod/Core/GameBridge.cs)<br>[`LockstepClock.cs`](../src/TABCoopMod/Determinism/LockstepClock.cs) | 抑制尸潮混战轨迹雪崩与小规模漏怪累计，将整局长时对局向“几乎 0 黑屏重载”推进 |

### 6.2 验收门禁与回归测试标准
每完成一个阶段，必须通过以下全部自动化与实机校验门禁：
1. **编译与注入健康度**：`TABCoopMod.csproj` Release 编译 `0 警告 0 错误`；日志确认 `GameBridge ALL BINDINGS VERIFIED SUCCESSFULLY (0 missing)`、Harmony `failed-tolerant=0`。
2. **Gate A1 / A2 确定性回放基准**：`1_Backup.zxsav`（120 ticks）与 `6666.zxsav`（180 ticks）双进程回放均保持 `identical`（确保隔离 `Colonist` 与 `OCellAreaCursor` 后对基础确定性零破坏）。
3. **并发建造与临界冲突回归**：在 `900survival.zxsav` 下开启 Host 悬停特斯拉塔供电预览，双端同时在供电边界下单建造，验证双端决策完全一致（`0` 单边拒建、`0 econ x3`）。
4. **后期长局 Chaos + 异构压测**：在 `6666.zxsav` / `900survival.zxsav` 开启 `ChaosTransport`（含丢包/断连）及 `-ClientExtraTickMs 30` 跑满 `>= 10 分钟`，验证：
   - 正常接战与建造期间 **`Auto-resync = 0`**；
   - 资源偏差/队列偏差可在下一个 `2s` Epoch 周期内静默收敛至 `StateChecksum MATCH`；
   - 人工桌面目视验证：`Worker_A` 走动正常、单位转身滑步自然、补建/补生淡入无突兀闪烁。

---

## 7. 实装进度与门禁验收记录（2026-11-19）

Phase 1 ~ Phase 3 代码已全部实装并通过基线编译与自动化门禁验证：

| 阶段 | 状态 | 主要代码落点 |
| :--- | :-: | :--- |
| **Phase 1（零副作用治本）** | ✅ 已实装 | [`ObfuscatedSymbols.cs`](../src/TABCoopMod/Core/ObfuscatedSymbols.cs)（Section 17）、[`GameBridge.cs`](../src/TABCoopMod/Core/GameBridge.cs)（`IsColonistType`、`SuspendLocalBuildCursorEnergyPreview` / `RestoreLocalBuildCursorEnergyPreview`）、[`StateHasher.cs`](../src/TABCoopMod/Determinism/StateHasher.cs)、[`DeterministicReplayVerifier.cs`](../src/TABCoopMod/Testing/DeterministicReplayVerifier.cs)、[`LockstepClock.cs`](../src/TABCoopMod/Determinism/LockstepClock.cs)（`Building` 生成与 `StructureNearEnemiesReplayPatch`）、[`NetCommand.cs`](../src/TABCoopMod/Commands/NetCommand.cs)、[`CoopSessionManager.cs`](../src/TABCoopMod/Sync/CoopSessionManager.cs) |
| **Phase 2（经济/队列/建筑异步对齐）** | ✅ 已实装 | [`ObfuscatedSymbols.cs`](../src/TABCoopMod/Core/ObfuscatedSymbols.cs)（Section 18）、[`StateHasher.cs`](../src/TABCoopMod/Determinism/StateHasher.cs)（`FlagStructure`、`ProdQueueSample`）、[`GameBridge.cs`](../src/TABCoopMod/Core/GameBridge.cs)（`AddLevelResourceDelta`、`Read/ApplyOrdersQueuedCommandIds`、`RefreshCommandPanel`、`PlaceSyncedStructure`、`DestroySyncedExtraStructure`）、[`CoopSessionManager.cs`](../src/TABCoopMod/Sync/CoopSessionManager.cs)（`AsyncEconFallbackStreak = 6`、`flags` bit 3~5、`[AsyncEconAlign]` / `[AsyncProdAlign]` / `[AsyncStructAlign]`） |
| **Phase 3（战斗目标/小批量补生）** | ✅ 已实装 | [`GameBridge.cs`](../src/TABCoopMod/Core/GameBridge.cs)（`GetEntityCombatAndMoveTargets`、`TryAlignEntityCombatAndMoveTarget`、`TryCaptureDynamicSpawnMeta`、`SpawnSyncedDynamicEntity` 及 `350ms` `DXClip` 淡入 / 僵尸 `Opacity=0` + `CInvisibleWhenNotWatched`）、[`CoopSessionManager.cs`](../src/TABCoopMod/Sync/CoopSessionManager.cs)（`flags` bit 6~7、`1..32` 按 ID 升序补生、`TargetFixes` 保护本地直控单位、`[AsyncSpawnAlign]`） |

### 已跑通验证项（2026-11-19）
- **Release 编译**：`dotnet build src/TABCoopMod/TABCoopMod.csproj -c Release` — `0 警告 0 错误`。
- **实机注入与反射自检**：`[GameBridge] ALL BINDINGS VERIFIED SUCCESSFULLY (0 missing, verified=143)`；`All Harmony patches applied. Classes OK=65, failed-tolerant=0`。
- **Gate A1（`1_Backup.zxsav` / 120 ticks）**：`PASS: 120 ticks identical`，`common=2053, moved(>0.01)=0, maxDx=0, maxDy=0`。
- **Gate A2（`6666.zxsav` / 180 ticks）**：`PASS: 180 ticks identical`，`common=7487, moved(>0.01)=0, maxDx=0, maxDy=0`。
- **Chaos 双开（`6666.zxsav` / 30s / `latency=120;jitter=30;loss=0.02;disconnect=4000-5000;seed=42`）**：Tick `168484` 起 `StateChecksum MATCH (0xF2B068F2E3E59A76)`，持续 `30s` 至 Tick `169144` 全程 `0 DESYNC`、`0 Auto-resync`、`0 WARN/ERROR`。

> **注**：上述 Chaos 30s 验证中基础 Lockstep 即保持全周期 `MATCH`（未触发偏差分叉路径）；极端临界供电冲突与异构 `>= 10 分钟`长局对抗下 `[AsyncEconAlign]`/`[AsyncProdAlign]`/`[AsyncStructAlign]`/`[AsyncSpawnAlign]` 的触发收敛与视觉表现建议在人工桌面实玩 / 专项注入回归中进一步观察记录。
