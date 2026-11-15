# 11 — 控制权差分对齐与单位移动视觉平滑优化分析

> **整理日期**: 2026-11-15  
> **定位**: 本文档归档关于“按最后操控者差分切换异步位置对齐方向”、“移动指令本机先行计算的边界与替代方案”以及“单位移动视觉帧率低/非连续感底层根因与渲染插值层修复方案”的技术讨论结论，作为后续优化排期与实现的参考依据。

---

## 1. 按最后指令人（`PlayerId`）差分对齐单位位置

### 1.1 现状与痛点
- 在当前 `async_align` 模式下（见 [`CoopSessionManager.cs`](../src/TABCoopMod/Sync/CoopSessionManager.cs) 与 [`GameBridge.cs`](../src/TABCoopMod/Core/GameBridge.cs)），位置修正是固定的单向 **Host → Client**：主机定期下发偏差桶内实体的 `(Id, X, Y, Life)`，客机对超过阈值（移动中玩家单位 `1.80f` 格、静止玩家单位 `0.50f` 格）的单位做阻尼拉回与 `600ms` 视觉平滑衰减。
- 在执政官模式（共享阵营、独立操作）下，如果客机正在高频微操一队游侠，而两端因物理微小差异发生漂移时，主机向客机拉回会让客机操作者感受到非预期的单位位移或手感被扯回。

### 1.2 可行性与改造方案（难度：中等偏小，约 150~250 行）
在 [`CoopSessionManager.cs`](../src/TABCoopMod/Sync/CoopSessionManager.cs) 的 `FlushDueTurnCommands` 中，所有端都在同一 `TargetSimTick` 按完全一致的顺序执行 `NetCommand`，且每条指令天然携带 `cmd.PlayerId` 与 `cmd.ExecutorEntityIds`。因此可以基于以下三步实现**按操控者差分对齐**：

1. **维护确定性控制权表**：
   - 在 `FlushDueTurnCommands` 执行单位移动/攻击等指令时，两端同步更新 `Dictionary<ulong, (byte PlayerId, int LastOrderTick)>`（仅追踪 `IsPlayerEntity` 的可移动单位）。
2. **客机自控单位免拉回保护**：
   - 客机收到主机的 `PosDriftSyncChunk` 时，若某单位当前处于自己名下的有效控制租约期内，**忽略主机的常规软位置修正**（仅当偏差达到极端阈值如 `>= 3.5` 格时才保留兜底），从而使客机正在操作的单位 **0 拉回**。
3. **轻量反向对齐（Client → Host）**：
   - 全图 1~3 万实体绝大部分为僵尸和建筑，处于“客机正在操控且移动中”的玩家单位通常只有数个到数十个。
   - 客机每 `2s` 发送 `StateChecksumReport` 时，可顺带附加上这部分自控移动单位在当前校验 Tick 的 `(Id, X, Y)`（仅数百字节）；主机收到后以相同的阻尼系数与 `RenderVisualInterpolationPatch` 视觉平滑将主机上的对应单位对齐向客机（3 人局时再由主机中继给第三人）。

### 1.3 潜在副作用与必须设置的护栏

| 潜在副作用 | 根因分析 | 对应护栏设计 |
| :--- | :--- | :--- |
| **1. 人尸贴脸肉搏时的刚体互穿/弹射** | 玩家单位按客机对齐，但周围僵尸群仍按主机对齐。贴脸卡位时若主机把客机僵尸往左拉、客机同时把主机游侠往右拉，可能瞬间重叠进同一碰撞体并在下一物理步被 Chipmunk2D 剧烈弹开。 | 反向对齐必须沿用现有阻尼（`< 2.2` 格乘 `0.60~0.75`）与 `EntityPosRealignCooldownTicks = 180` 单实体冷却去抖，禁止贴脸推挤时直接硬瞬移。 |
| **2. 血量/受击判定的“隔空挨刀”视觉落差** | 血量与生死结算（`LifeFixes`）**绝不能分片交给客机**，必须保持 Host 单一权威。当位置听客机、扣血听主机时，若两端存在 `0.8` 格偏差，可能出现客机看似已拉出攻击范围却在 `LifeFixes` 同步后掉血的现象。 | 借助每 `2s`（60 ticks）随 `StateChecksumReport` 高频捎带自控单位坐标，将主客机上该小队的位置差持续压制在 `0.2~0.3` 格以内。 |
| **3. 控制权永久滞留（所有权僵尸化）** | 若客机曾框选过全军，单位永久归客机对齐，后续主机接管或单位闲置时反而会被客机旧状态拉扯。 | 控制权必须设置**租约 TTL**（例如最后下令后 `8~10s`，或单位停止移动进入待机超过 `3s` 后自动失效），到期自动将对齐基准归还主机。 |
| **4. 双人同时抢控同一单位的冲突** | 多人同回合或相邻回合对同一批单位下令。 | `FlushDueTurnCommands` 在全端执行顺序 100% 一致，天然满足“最后一条落盘指令的 `PlayerId` 胜出”，无需额外协商协议。 |

---

## 2. 单位移动“本机先行计算”的可行性与替代方案

### 2.1 现有框架的延迟来源
- 当前 [`LockstepClock.cs`](../src/TABCoopMod/Determinism/LockstepClock.cs) 已实装单 tick 异步流水线与 `RenderVisualInterpolationPatch` 帧间插值，[`CommandInterceptorPatch.cs`](../src/TABCoopMod/Patches/CommandInterceptorPatch.cs) 已实装 0ms 本地地面指示器与 0ms 单位语音即时确认。
- 玩家感知到的“操作略滞后”，本质是确定性锁步的 `InputDelayTicks`（`1~4 ticks`，即 `33~133ms`）排期等待，而非渲染帧率本身。

### 2.2 为什么严禁在逻辑/物理层做 0ms 抢跑移动？
违反项目铁律 #5（任何改变游戏世界的操作都必须走 `NetCommand`），直接在逻辑层不等 `TargetSimTick` 让本机单位先行移动会导致 **3 个系统性副作用**：
1. **制造必然的永久性相位超前**：本机 `Tick 100` 点击即走，权威包在 `Tick 103` 落盘，本机单位会永远超前 3 帧（约 `0.4~0.6` 格），导致 `StateChecksum` 从大部分 `MATCH` 退化为只要微操就永久 `DESYNC`。
2. **权威回合包回传后的二次执行抽搐**：`Tick 103` 权威包到达时，若再次执行则重置寻路/分散目标点造成顿挫；若跳过则一旦有队友指令插入或地形变化，两端指令状态机立即分叉。
3. **全图寻路网格与随机数流级联污染**：单位移动会实时改写 `DXWorldGrid` 占格、触发 Chipmunk2D 推挤周围刚体，并改变进入僵尸索敌圈的帧号，导致周围未操作的单位和尸潮同步失步。

### 2.3 零副作用替代方案：纯渲染层起步预演（Visual-only Anticipation）
若需进一步优化点击瞬间的起步跟手度，可在**不触碰 `entity.Position`、`CMovable` 与 `DXPhysicBody`** 的前提下，仅在渲染层实现：
- 拦截到本地移动指令的 0ms 瞬间，在 `RenderVisualInterpolationPatch` 中为选中单位记录一个朝向目标点的**纯视觉微位移预演**；
- 在等待 `TargetSimTick` 落盘的 `1~3 ticks`（`33~100ms`）窗口内，让 `SceneObject` 先朝目标方向平滑迈出约 `0.15~0.3` 格；
- 待 `TargetSimTick` 到达、逻辑层正式起步后，将该视觉预演偏移平滑衰减归零，底层确定性与 `StateChecksum` 完全不受影响。

---

## 3. 单位移动视觉帧率低 / 非连续感的底层根因与修复方案

> **事实澄清**：[`docs/08_RTS_SYNC_COMPARISON_AND_FUTURE_OPTIMIZATIONS.md`](08_RTS_SYNC_COMPARISON_AND_FUTURE_OPTIMIZATIONS.md) 梯度二中提到的“物理步末浮点坐标/速度量化截断”目前**尚未实装**，当前代码库中所有物理坐标均为原生完整 `float` 精度，因此非连续感**并非**由抹除浮点尾数引起。

经过对 `DXVision.dll`（`DXGame` / `DXEntity` / `DXPhysicWorld` / `DXPhysicBody`）、`TheyAreBillions.exe`（`CSprite` / `CSelectable`）与 [`LockstepClock.cs`](../src/TABCoopMod/Determinism/LockstepClock.cs) 的对照排查，单位移动呈现“阶梯感/非连续”的根因来自以下 **4 个具体机制**：

### 3.1 底层物理本身为 12Hz，与 30Hz 逻辑帧呈 `2 / 3` 帧交替拍频
- 引擎中 30Hz 的逻辑帧（`33.33ms`）只施加移动推力，单位实际坐标由 `DXPhysicWorld.UpdateDefaultTime()` 以 **12Hz（每 `83.33ms`）** 步进 Chipmunk2D 计算并写回 `DXPhysicBody.PositionComputed`。
- 由于 `83.33ms / 33.33ms = 2.5`，确定性单线程步进时物理更新会以 **2 个逻辑 Tick（`66.7ms`）与 3 个逻辑 Tick（`100.0ms`）交替触发**，真实坐标每秒仅更新 12 次，高度依赖渲染插值补帧的稳定性。

### 3.2 `RenderVisualInterpolationPatch` 插值系数 `alpha` 的时序错位与平台期停顿
在 [`LockstepClock.cs`](../src/TABCoopMod/Determinism/LockstepClock.cs) 的 `StartFrame_CaptureMainThread()` 中：
```csharp
FlushPendingAsyncTick(DXGame.Current);          // 先结束异步逻辑+物理 -> 触发 OnPhysicsStepCompleted() 归零并计算 overshoot
RenderVisualInterpolationPatch.UpdateFrameAlpha(); // 后把刚刚流逝的上一帧 dtMs 加到新物理步上
```
这造成了两个直接的视觉顿挫：
1. **首帧跳变**：`OnPhysicsStepCompleted()` 计算 `overshoot` 时尚未计入本帧刚过去的 `dtMs`，导致 `overshoot` 几乎恒为 `0`；紧接着 `UpdateFrameAlpha()` 又把上一帧 `dtMs`（`16~33ms`）直接加到新物理步上，使新周期第 1 渲染帧直接从 `alpha ≈ 0.25~0.35` 起跳，跳过前半段过渡。
2. **末尾 `alpha = 1.0` 停顿**：在 3-Tick 物理间隔（`100ms`）或客机等待网络包抖动期间，`_accumPhysicsElapsedMs` 会提前触顶 `1.0`，单位在终点停顿 1~2 个渲染帧，待下一物理步完成后再突然向前滑行，形成“滑一下、停一下”的阶梯感。

### 3.3 脚下阴影（`CShadow`）、附加组件与等轴测 `Z` 深度未吃插值
- 当前 `RenderVisualInterpolationPatch` 仅挂钩了 `CSprite.#=zyeyG75rdugKq`（人物贴图）与 `CSelectable.#=zyeyG75rdugKq`（选中绿圈），在各自 Prefix 临时修改 `DXEntity._Position` 并在 Finalizer 立即还原。
- 因此在 `DXEntity.UpdateSceneObjectParallel()` 内的其他组件（尤其是紧贴单位脚底的阴影 `CShadow`）以及随后主线程 `DXEntity.UpdateSceneObject()` 计算的 `SceneObject.Z = GetZ()`，读到的依然是还原后的 **12Hz 离散坐标**。
- 人物贴图平滑滑动而脚底黑影按 12Hz 逐格跳跃，视觉叠加后会产生明显的抽帧感。

### 3.4 `GameTimeIsolationPatch` 将单位行走动画切帧锁定在 `<= 30Hz`
- 原版单机通过后台线程 `_TaskGameTime` 每 2ms 刷新 `DXGame.Current.GameTimeInt`，使 `CSprite` 的行走动画进度 `GameTimeInt - TimeAnimationStart` 随高刷渲染连续推进。
- 为保证确定性，[`LockstepClock.cs`](../src/TABCoopMod/Determinism/LockstepClock.cs) 中的 `GameTimeIsolationPatch` 锁定了 `GameTime` 仅在逻辑 Tick 步进时跳变 `+33.33ms`，导致渲染期读取的动画时间也呈 30Hz 阶梯跳变。

### 3.5 纯渲染插值层修复方案（不动同步协议与确定性物理）
1. **扩大位置插值覆盖面至完整场景更新期**：
   - 将插值投影从单个组件的 `CSprite / CSelectable` 外移到 `SetCamera` 的场景更新外层（覆盖 `DXEntity.UpdateSceneObjectParallel` 与 `UpdateSceneObject`），在进入前统一将可见动态实体的 `_Position` 投影为平滑插值位置，更新结束后统一还原为 `PositionComputed`，使身体贴图、选中圈、脚底阴影 `CShadow` 与 Z 轴深度同步平滑。
2. **修正 `UpdateFrameAlpha` 与 `OnPhysicsStepCompleted` 时序并固定 `83.33ms` 时钟基准**：
   - 先执行 `UpdateFrameAlpha()` 累加本帧 `dtMs`，再在物理步完成时从累加器扣除 `83.333ms` 保留精确 `overshoot` 余数，消除首帧跳变与末尾 `alpha = 1.0` 平台期停顿。
3. **渲染期临时提供带插值余数的视觉 `GameTimeInt`**：
   - 在 `SetCamera` 纯渲染场景更新阶段临时将 `GameTime` 加上当前帧渲染时间余数（逻辑期与物理期仍严格保持 `33.33ms` 确定性步长），恢复高刷下的行走序列帧流畅度。
