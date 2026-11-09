# 09 — 技术审计、疑难解答与下一阶段工作计划 (Technical Audit, Solutions & Next-Phase Work Plan)

> **编写时间**: 2026-11-09
> **适用对象**: Claude Code / 后续开发 Agent
> **前置阅读**: 先读完本文档全文，再结合 `CLAUDE.md` 与 `docs/06_HANDOVER_STATUS.md` 按本文档第 3 节的工作计划（P0 → P1 → P2 → P3）逐项推进。

---

## 1. 对现有代码的全面技术审计：7 处必改 Bug 与设计错漏

经过对 `src/` 源码、`TheyAreBillions.exe` 及 `DXVision.dll` 反编译源码的交叉审查，定位出当前代码中隐藏的 **7 处关键错漏**（其中前 3 处直接导致当前的确定性测试失败与 ID 冲突）：

### 1.1 【致命 Bug】`CalibrateEntityIdCounterFromLoadedLevel` 调用时机早于异步读档，导致同步 ID 计数器停留低位（`101000`）
- **位置**: `src/TABCoopMod/Determinism/LockstepClock.cs:145-159`、`Testing/DeterministicReplayVerifier.cs:94-127`、`Sync/CoopSessionManager.cs:480-486`
- **反编译真相**:
  1. 原版 `DXVision.DXGuid.New()`（`DXGuid.cs:15`）使用 `(_WindowsRandom.Next() * 4294967296L + _WindowsRandom.Next())` 生成 64 位实体 ID，范围覆盖 `[0, 2^62)`（最高可达 `9.22 × 10^18`，即 `0x7FFFFFFFFFFFFFFFUL`）。实测存档 `6666.zxsav` 中实体最大 ID 为 `9223221138931328958`（`0x7FF9...`）。
  2. 原版读档方法 `ZXGame.#=z9IpK6P4YlW8GIY7olg==(string path)`（`ZXGame.cs:1483`）内部通过 `#=z9DPDdq9qP9lZ(20, loadAction, finishAction)` **异步延迟 20ms 在后台线程执行反序列化**！
  3. 真正的执行顺序是：
     - `LoadGameAndEnter(path)` 立即返回（此时读档尚未开始）；
     - 后台线程执行 `ZXSystemGameLevel.#=zmTU4kueQctVr(DXLevel)`（即 `InitLevel`）；
     - 后台线程执行 `ZXLevelState.#=zF1YHTRUZSNRa()`（恢复存档的 `GameTime`、`IAIterationInt`、迷雾等）；
     - 主线程完成回调执行 `ZXSystemGameLevel.#=zwK_pUIeRBpbp()`（挂载 `Scene`、初始化迷雾与相机）。
- **Bug 后果**:
  - `CoopSessionManager.ApplyReceivedSnapshotOnClient` 在 `LoadSaveFileAndEnter` 下一行立刻调用 `CalibrateEntityIdCounterFromLoadedLevel`，此时新存档还没加载！
  - `DeterministicReplayVerifier.StartRun` 在 `InitLevel` Postfix 中调用 `CalibrateEntityIdCounterFromLoadedLevel` 和 `ResetStaticsForRun`，此时 `ZXLevelState.#=zF1YHTRUZSNRa()` 和 `#=zwK_pUIeRBpbp()` 尚未完成，且 `PrepareForLoad` 把 `_syncedEntityIdCounter` 重置成了 `100000L`！
  - 这直接解释了 `docs/07 §0.6 R6` 的疑问：**为什么分叉报告里会出现 ID 为 `100015 ~ 101138` 的低 ID `DXEntity`、僵尸和投射物且呈巨幅位移？** 因为计数器从 `100000`/`101000` 开始递增分配给局内新生成的投射物和感染僵尸，只要两跑某发子弹或某个僵尸生成时机相差 1 tick，同一个低位 ID 在两跑中就指向了完全不同的对象！
- **修复方法**:
  1. 在 `ObfuscatedSymbols.cs` 新增常量：`public const string Method_LevelSys_OnLevelLoadComplete = "#=zwK_pUIeRBpbp";`（`ZXSystemGameLevel` 的读档收尾方法）。
  2. 将“关卡就绪后校准实体 ID、按 ID 排序实体、重置随机数与物理时间戳”的逻辑挂载到 `ZXSystemGameLevel.#=zwK_pUIeRBpbp` 的 `Postfix`（以及 `DeterministicReplayVerifier.OnBeforeLogicTick` 捕获 `_baselineTick` 的那一刻再校准一次）。
  3. 为防止任何情况下新生成的同步实体 ID 与存档内高達 `9.22 × 10^18` 的随机 ID 碰撞或溢出到 UI 位（`0x8000000000000000UL`），建议将同步实体 ID 段固定在安全区间（例如 `0x4000000000000000L` 起步，或在校准时只统计 `< 0x4000000000000000UL` 的 ID，或直接用 `0x4000000000000000L` 作为局内新生成同步实体的起始计数器）。

### 1.2 【致命 Bug】`ResetStaticsForRun()` 将 `DXPhysicWorld.LastGameTimeUpdatePhysics` 置为 `0.0`，引爆单帧数万次物理步进
- **位置**: `src/TABCoopMod/Testing/DeterministicReplayVerifier.cs:426`
- **反编译真相**:
  看 `DXGame.UpdatePhysics()`（`DXGame_full.cs:2934`）：
  ```csharp
  while (GameTime - DXPhysicWorld.LastGameTimeUpdatePhysics >= 83.33333333333333)
  {
      DXPhysicWorld.LastGameTimeUpdatePhysics += 83.33333333333333;
      DXPhysicWorld.UpdateDefaultTime();
  }
  ```
  当加载中后期存档（如 `6666.zxsav`，`GameTime ≈ 5,616,166 ms`）时，如果把 `LastGameTimeUpdatePhysics` 置为 `0.0`（或存档恢复的值与 `GameTime` 存在残余相位差），`UpdatePhysics()` 会在**一帧之内连续狂跑上万次物理步进**，或导致两进程在不同 `SimTick` 触发 12Hz 的物理步进！
- **修复方法**:
  - **严禁将 `DXGame.LastGameTimeIAUpdate` 或 `DXPhysicWorld.LastGameTimeUpdatePhysics` 置为 `0.0`！**
  - 在关卡加载完成（`#=zwK_pUIeRBpbp` Postfix）及 `DeterministicReplayVerifier` 对齐 `_baselineTick` 时，必须令：
    ```csharp
    double gt = DXGame.Current.GameTime;
    DXGame.LastGameTimeIAUpdate = gt;
    DXPhysicWorld.LastGameTimeUpdatePhysics = gt;
    DXPhysicWorld.TimeBodyPosition = gt;
    DXPhysicWorld.TimeLastBodyPosition = gt;
    ```
    这样两进程的 30Hz 逻辑时钟与 12Hz 物理时钟在 `_baselineTick` 处于 **100% 相同的零相位起点**！

### 1.3 【严重 Bug】`CommandInterceptorPatch.cs:84` 残留泛型 `HasComponent<T>()` 内部类型调用，真人右键移动必抛 `TypeAccessException`
- **位置**: `src/TABCoopMod/Patches/CommandInterceptorPatch.cs:84`
- **问题代码**:
  ```csharp
  .Where(e => e != null && !e.Disposed && e.HasComponent<CMovable>() && e.HasComponent<CCommandable>())
  ```
- **原因**: `CMovable` 和 `CCommandable` 是 `TheyAreBillions.exe` 的 `internal` 类型。Desktop CLR 4.6 在 JIT 编译 `Patch_LevelSys_GroupMoveSelected.Prefix` 时，实例化 `HasComponent<CMovable>()` 会立即抛出 `TypeAccessException`！（会话 3 测试 `TABCOOP_INTERCEPT=1` 之所以没报错，是因为自动化测试未触发鼠标右键的 `GroupMoveSelected`，該方法根本没被 JIT！）
- **修复方法**: 立即替换为 `GameBridge.HasComponent(e, GameBridge.TypeCMovable) && GameBridge.HasComponent(e, GameBridge.TypeCCommandable)`，并将 `CommandInterceptorPatch.cs` 中其余强类型参数（`ZXCommand`、`ZXEntity`、`ZXCommandTarget`）按 `07 §0.5` 标准全部改为 `object` + `GameBridge` 反射辅助方法。

### 1.4 【严重 Bug】`PauseSyncController` 使用本地 `DateTime.UtcNow` 且直接调 `CommandExecutor.Execute`，联机下不发包且恢复帧不同步
- **位置**: `src/TABCoopMod/Sync/PauseSyncController.cs:46-64, 91`
- **问题**:
  1. `Apply(bool paused)` 直接调了 `CommandExecutor.Execute(...)`，绕过了 `CommandInterceptor.OnLocalCommandCaptured`，导致联机时只改本机，不发网络包！
  2. 倒计时用本机墙钟 `DateTime.UtcNow`，两端网络延迟和渲染帧率不同，3 秒到期时刻必然落在不同的 `SimTick`，一旦恢复立即 Desync！
- **修复方法**:
  - 路由修复：`Apply(bool paused)` 改为当 `CoopSessionManager.IsSessionActive && CoopSessionManager.IsInSyncedLevel` 时调用 `CommandInterceptor.OnLocalCommandCaptured?.Invoke(cmd)`，否则单机直接 `CommandExecutor.Execute(cmd)`。
  - 倒计时同步：暂停立即发送 `SetPaused(true)`；请求恢复时，由发起端（或统一由 Host）进行 3 秒倒计时提示，**僅在倒计时归零的那一刻由 Host 发出一条 `SetPaused(false)` 网络指令**，全员在收到该指令的同一 `TargetSimTick` 恢复模拟。

### 1.5 【中度 Bug】`DXRandomCtorPatch._seedCounter` 从未重置
- **位置**: `src/TABCoopMod/Determinism/DXRandomCtorPatch.cs:22`
- **问题**: `_seedCounter` 只增不减，跨局或两进程如果在进入同步前调用 `new DXRandom()` 的次数不同，后续所有 `new DXRandom()` 实例的种子全部错位。
- **修复方法**: 在 `DXRandomCtorPatch` 新增 `public static void ResetCounter() => Interlocked.Exchange(ref _seedCounter, 0L);`，并在 `LockstepClock.EnableDeterministicSandbox` 中调用。

### 1.6 【中度错漏】市长奖励建筑使用的是 `ZX.Commands.EntityBonusCreation` 而非 `ZX.Commands.Build`
- **位置**: `src/TABCoopMod/Patches/CommandInterceptorPatch.cs:142`
- **反编译真相**: 在 `ZXSystemGameLevel.cs:4463` 中：
  `if (#=z0o_eM4o= is Build || #=z0o_eM4o= is EntityBonusCreation || #=z0o_eM4o= is ContractEmpireResource)`
  市长赠送的免费建筑（如送 1 个雷达塔/弩车）走的是 `EntityBonusCreation` 命令子类（继承自 `EntityCreationCommand`），而非 `Build`！
- **修复方法**: 在拦截和回放建造命令时，若命令类型继承自 `EntityCreationCommand`（包含 `Build` 与 `EntityBonusCreation`），统一提取其 `EntityType` 并正确标记 `IsBonusBuilding = (cmd.GetType().Name == "EntityBonusCreation")`。

---

## 2. `docs/07` 全部遗留技术问题的反编译根因解答与标准解法

### 2.1 解答 §0 & §0.1：为什么单位移动位置（`pos`）和生存档实体数（`entities/life`）会分叉？

Claude Code 原先猜测“移动积分使用了真实时间 `DXHelper_Environment.Ticks`”，**经反编译 `CMovable.cs` 与 `DXPhysicWorld.cs` 证实该猜测不成立**（移动步长固定为 `Speed / 30f`，物理步长固定为 `1.0 / 12.0`）。

**真正的 3 个分叉根源如下（均已从反编译源码坐实）：**

#### 根源 A（最核心发现）：未开启游戏内置的 `ZXGame.#=zB3Vhdin5PerfrubjO4AblAA=()`（`IsMultiplayer`）模式，导致寻路走后台多线程异步队列！
反编译 `ZX.Components.CMovable.#=zUPt0E_k=`（`CMovable.cs:1348-1372`）发现，游戏引擎内部原本就写好了联机确定性寻路分支：
```csharp
// ZXGame.#=zB3Vhdin5PerfrubjO4AblAA=() 返回 (ZXGame.#=z2zBJut2SCIUl() == ZXPlayerModeType.Multiplayer)
if (#=z4RevDP3eECXqXS6JRA==.#=zB3Vhdin5PerfrubjO4AblAA=())
{
    // ✅ 联机分支：在当前逻辑帧主线程【同步】计算路径，100% 确定！
    #=zdzxO_Le$3wwY = DXWorldGrid.FindPath(1, cell, point3, pathfindingMask, nearPath: true);
    #=zUPt0E_k=(#=zJsBqmzUPGAuq, #=z$uEWJ2TsPpcX);
    return;
}
// ❌ 单机分支：扔进 DXWorldGrid 的 2~4 个后台寻路线程 (_Tasks) 异步计算！
DXWorldGrid.FindPathConcurrent(out #=ziMavkfK9ZJji, ...);
```
而且在 `CMovable.cs:1282` 中：
```csharp
if (DXWorldGrid.PendingPathfindingCount > 10 && !(Entity is Human)) return;
```
在单机分支下，不仅后台寻路线程完成计算的 tick 随 CPU 线程调度波动（差 1 tick 拐弯就会产生 `0.16~0.28` 格的位置差并不断累积），而且当后台队列超过 10 个请求时，僵尸会直接放弃本次寻路！さらに，在 `ZXSystemGameLevel.#=zmTU4kueQctVr`（`ZXSystemGameLevel.cs:420`）中，只有当 `#=zB3Vhdin5PerfrubjO4AblAA=()` 为 `true` 时，游戏才会固定行为树种子 `DXBehaviour<BData>.Seed = (int)level.ID`。

- **【标准解法】**:
  1. 在 `ObfuscatedSymbols.cs` 添加：
     ```csharp
     public const string Method_ZXGame_IsMultiplayer = "#=zB3Vhdin5PerfrubjO4AblAA=";
     public const string Method_ZXGame_SetPlayerMode = "#=znDPORUoLfC4p";
     ```
  2. 在 `DeterministicSandboxPatches` 中直接 Hook `ZXGame.#=zB3Vhdin5PerfrubjO4AblAA=()`：
     ```csharp
     [HarmonyPrefix]
     public static bool IsMultiplayer_Prefix(ref bool __result)
     {
         if (!LockstepClock.IsDeterministicModeActive) return true;
         __result = true;
         return false;
     }
     ```
     并在 `LockstepClock.EnableDeterministicSandbox` 中同时调用 `ZXGame.#=znDPORUoLfC4p(1)`（`ZXPlayerModeType.Multiplayer = 1`）。这会让全图所有单位和僵尸的寻路立即切换为主线程同步 `DXWorldGrid.FindPath`！

#### 根源 B：`DXPhysicWorld.UpdateDefaultTime()` 内部仍在使用 `DXHelper_Task.For` 多线程并行更新物理刚体！
反编译 `DXVision.DXPhysicWorld.UpdateDefaultTime()`（`DXPhysicWorld.cs:105-134`）可见，即便 `DXGame.IsIADeterministic = true`，物理世界每步仍然使用两个 `DXHelper_Task.For(0, Bodies.Count, ..., 200)` 多线程并发调用 `Bodies[i].GameEntity.StartUpdatePhysics()`（内部调用 `CMovable.#=zkgQp3Dx6LolarfpKKQ==` 向 C++ 物理引擎写入 `PhysicForce` 和 `PhysicMass`）以及 `Bodies[i].OnNewPositionComputed()`！多线程并发向底层 `DXPhysicsNative` 写力并更新位置会因线程抢占顺序不同而导致碰撞结果微差。

- **【标准解法】**:
  千万不要全局 Hook `DXHelper_Task.For`（会影响渲染裁剪）。应当在独立补丁类中**只 Hook `DXPhysicWorld.UpdateDefaultTime()` 的 `Prefix`**：
  ```csharp
  [HarmonyPatch(typeof(DXPhysicWorld), nameof(DXPhysicWorld.UpdateDefaultTime))]
  public static class DeterministicPhysicsPatch
  {
      [HarmonyPrefix]
      public static bool Prefix()
      {
          if (!LockstepClock.IsDeterministicModeActive) return true;
          if (!DXPhysicWorld.Initialized || !DXPhysicWorld.Enabled) return false;

          var bodies = DXPhysicWorld.Bodies;
          lock (DXPhysicWorld.Sync)
          {
              DXGame.Current.OnStartPhysics();
              // Execute PostMethods via cached FieldInfo
              GameBridge.ExecutePhysicsPostMethods();
              for (int i = 0; i < bodies.Count; i++)
              {
                  var b = bodies[i];
                  if (b?.GameEntity != null) b.GameEntity.StartUpdatePhysics();
              }
              DXPhysicWorld.Step(1.0 / 12.0);
              DXGame.Current.OnFinishPhysics();
          }
          lock (DXGame.SyncLogic)
          {
              DXPhysicWorld.TimeLastBodyPosition = DXPhysicWorld.TimeBodyPosition;
              DXPhysicWorld.TimeBodyPosition = DXGame.Current.GameTime;
              DXPhysicWorld.FactorTimeForInterpolation = 1.0 / (DXPhysicWorld.TimeBodyPosition - DXPhysicWorld.TimeLastBodyPosition);
              for (int i = 0; i < bodies.Count; i++)
              {
                  var b = bodies[i];
                  if (b != null && b.Handle != IntPtr.Zero && b.GameEntity != null && b.GameEntity.PhysicalType != DXBodyType.Static)
                  {
                      b.OnNewPositionComputed();
                  }
              }
          }
          DXPhysicWorld.NUpdates++;
          DXPhysicWorld.Iteration++;
          return false;
      }
  }
  ```

#### 根源 C：`DXLevel.Current.Random` 未定种 + 物理/逻辑时钟在基线帧未对齐
- 在关卡加载完成及基线对齐时，必须执行：
  1. `if (DXLevel.Current != null) DXLevel.Current.Random = new DXRandom(seed);`
  2. 对齐 `DXGame.LastGameTimeIAUpdate = DXGame.Current.GameTime;` 与 `DXPhysicWorld.LastGameTimeUpdatePhysics = DXGame.Current.GameTime;`（见 §1.2）。

---

### 2.2 解答 §0.6（R1 ~ R6）：相机坐标转换、锁定视角与开全图显示僵尸

#### 解答 R1, R2, R4：为什么 `set_camera` / `ShotCam` 位置不对、被夹紧为 `72×45`、且每帧被游戏改回？
反编译 `ZXSystemGameLevel.OnSetCamera()`（`ZXSystemGameLevel.cs:2841-3056`）与 `OMiniMap.cs:104` 揭示了三个关键事实：
1. **坐标系不同（最重要！）**：
   - `entity.Position`（如 `(75, 85)`）是 **World Grid 坐标（世界网格坐标）**；
   - `DXScene.Current.CameraArea` / `CameraCenter` 是旋转 45 度后的 **Isometric Scene 坐标（等轴测场景坐标）**！
   - 把世界坐标 `(wx, wy)` 转成相机场景坐标，**必须调用 `PointF scenePt = DXWorldGrid.ScenePointFromWorldPoint(new PointF(wx, wy));`**！反向转换则调用 `DXWorldGrid.WorldPointFromScenePoint(scenePt)`。
   - 之前 `CameraProbe.ApplySpec` 和 `ControlServer.SetCamera` 直接把世界坐标赋给了 `scene.CameraArea`，导致相机飞到了错误的地图角落并被边界夹紧！
   - 同理，`scripts/build_divergence_report.py` 在截图上画方框时，不能直接用 `(wx - cam.x) / cam.w`，必须在 `CameraProbe` 导出时或 Python 脚本中将实体世界坐标转换为场景坐标！
2. **为什么每帧被改回**：
   - 在 `ZXSystemGameLevel.OnSetCamera()` 中，存在开局 6 秒镜头拉近动画标志 `#=zfFXkD0aj1o9gF76ANQ==`（为 `true` 时每帧插值覆盖 `CameraArea`），以及鼠标在窗口边缘 `<= 5px` 时的自动滚屏逻辑。
   - 此外，`DXGame.SetCamera()` 的执行顺序是：**先调 `DXScene.Current.UpdateCameraArea()`（用 `RootObject.X/Y/Scaling` 重算 `_CameraArea`） → 再调 `ZXSystemGameLevel.OnSetCamera()` → 渲染更新 → 最后才调 `UpdateGameLogic()`**。
   - **正确改相机方法**：不要直接写 `scene.CameraArea = ...`，而是调用 **`DXScene.Current.SetCameraCenter(DXWorldGrid.ScenePointFromWorldPoint(worldCenter), sceneWidth)`**（该方法会同步更新 `RootObject.X/Y/Scaling`，不会被 `UpdateCameraArea()` 冲掉），同时通过反射将 `ZXSystemGameLevel` 的 `#=zfFXkD0aj1o9gF76ANQ==` 置为 `false`！
3. **缩放夹紧规则（R4）**：
   - 最小宽度 `15f`，最大宽度 `Math.Min(45f * aspect, PlayableArea.Width)`（在 2880×1800 即 16:10 下为 `45 * 1.6 = 72f`，高 `45f`）。

#### 解答 R3：为什么 `reveal_map`（`ShowFullMap=true` + `FogEnabled=false`）后僵尸依然隐身？
反编译 `ZX.Components.CInvisibleWhenNotWatched`（`CInvisibleWhenNotWatched.cs:46-95`）与 `ZXSystemMapLayers`（`ZXSystemMapLayers.cs:581, 672`）定位到了精确原因：
1. 每個僵尸都带有 `CInvisibleWhenNotWatched` 组件，通过设置 `Entity.SceneObject.Opacity = 0f` 隐藏在阴影中。
2. 它判断僵尸是否可见调用的是 `ZXSystemMapLayers.#=z4SfTR0oOSoLp(Point cell)`（`ZXSystemMapLayers.cs:581`），**这个重载竟然没有检查 `!FogEnabled` 或 `ShowFullMap`，而是直接读取前台视野数组 `#=z69ntQguW1qCr[idx] != #=zwOlEgvvDfD22`**！
3. 更糟的是，当游戏暂停或刚调 `reveal_map` 时，`#=z69ntQguW1qCr` 数组每 20 个逻辑 tick（`IAIterationInt % 20 == 0`）才会交换一次，且 `CInvisibleWhenNotWatched` 每 4 个逻辑 tick 才把 `Opacity` 增加 `0.09f`（需要 45 个逻辑 tick 才能显形）！
- **【标准解法】**:
  在 `GameBridge.SetShowFullMap(bool on)` / `SetFogEnabled` 中：
  1. 反射获取 `ZXSystemMapLayers.#=zQFWYIvs=` 的 `int[] #=z69ntQguW1qCr`（当前视野）与 `int[] #=zwJQaMvq$T3G4`（迷雾探索），将数组元素全部置 `0`，并设 `LayerFogUpdated = true; LayerShadowUpdated = true;`；
  2. 遍历 `DXGame.Current.Entities`，对所有 `e?.SceneObject != null` 的实体设置 **`e.SceneObject.Opacity = 1f; e.SceneObject.Visible = true;`**。这样无论是否暂停，开图后第 1 帧截图就能看到所有僵尸！

---

### 2.3 解答 §5：暂停 / 倍速 / 市长二选一 Producer 拦截以及小地图视野框公式

#### 1. 暂停与倍速的 Producer 拦截点（绝不碰 `DXGame.set_Paused`）
反编译 `ZXSystemGameLevel.cs` 确认，玩家操作暂停/倍速只有 3 个入口：
- **空格键暂停**：`ZXSystemGameLevel.OnKeyDown(DXKeys key)`（`ZXSystemGameLevel.cs:3564`）。用 Harmony `Prefix` 拦截 `ZXSystemGameLevel.OnKeyDown`，当 `CommandInterceptor.IsInterceptionEnabled && !DXModalScreen.IsInModal && key == DXKeys.Space`（或通过 `#=zyMMweQL9LzVp8ZCJ7w==.#=zRtXKxIY=(key, ZXKeyboardCommand.Pause)` 判断）时，调用 `PauseSyncController.RequestPause(!DXGame.Current.Paused)` 并 `return false`。
- **右上角暂停按钮**：`ZXSystemGameLevel` 实例的私有字段 `#=z1pCAbqKOgI6v`（`ZXButtonMetal`，`ZXSystemGameLevel.cs:718`）。在 `ZXSystemGameLevel.#=zwK_pUIeRBpbp` 的 `Postfix` 中，可为该按钮或通过拦截其点击回调接入 `PauseSyncController`。
- **`+` / `-` 键调节倍速**：`ZXSystemGameLevel.OnKeyUp(DXKeys key)`（`ZXSystemGameLevel.cs:3633`）。当 `CommandInterceptor.IsInterceptionEnabled && !DXModalScreen.IsInModal` 且按键为 `DXKeys.Add / Oemplus / Subtract / OemMinus` 时，发出 `NetCommandType.SetGameSpeed` 并 `return false`。

#### 2. 市长二选一弹窗的 Producer 与 Consumer 闭环
反编译 `ZXGame.#=zcITiOSD6YNmD()`（`ZXGame.cs:1609-1688`）可见：
- 弹窗从 `ZXMayor.#=zoTuFkHI=` 中使用 `DXLevel.Current.Random` 抽出男市长 `zXMayor` 和女市长 `zXMayor2`，并创建左右两个 `ZXButtonSteam` 按钮。
- **拦截方案**：
  Hook `ZXMayor.#=zPTw8CXGE0RqF(ZXLevelState)` 的 `Prefix`，但**必须加上严格的 4 重守卫**：
  ```csharp
  if (!CommandInterceptor.IsInterceptionEnabled
      || CommandExecutor.IsReplayingCommand
      || LockstepClock.LoadingLevel
      || DXGame.Current == null
      || DXGame.Current.IAIterationInt <= 0)
      return true;
  ```
  （为什么之前崩？因为关卡初始加载时 `IAIterationInt == 0` / `LoadingLevel` 期间系统会调用 `#=zPTw8CXGE0RqF` 应用初始市长！加上上述守卫后，载入期 100% 放行，只有局内玩家在弹窗点选市长时才会被捕获！）
  捕获时直接读取 `__instance`（`ZXMayor`）的 `ID` 属性，构造 `NetCommand { Type = NetCommandType.MayorVoteOrSelect, MayorId = id }` 发送并 `return false`！

#### 3. 小地图队友视野框与 Ping 的精确坐标转换公式（反编译自 `OMiniMap.cs`）
反编译 `ZX.GUI.OMiniMap`（`OMiniMap.cs:264, 342`）已拿到原版白色视野框 `#=zi91u4abSRmxU` 的**一模一样的官方源码**：
```csharp
// OMiniMap 内部字段：
// private DXLevel #=zyCM81eg=;
// private DXObject #=zi91u4abSRmxU; // 本地白色视野框
//
// 原版每帧在 OMiniMap.#=zThX9FxI=() (由 OnRender 调用) 中更新白色视野框的代码 (第 264 行)：
#=zi91u4abSRmxU.Area = Rectangle.Truncate(
    DXHelper_Rectangle.ChangeCoordinates(
        DXScene.Current.CameraArea,
        #=zyCM81eg=.PlayableArea,
        this.InnerArea));
```
- **实现队友视野框 (`MinimapCoopPatch.cs`) 只需 15 行代码**：
  Hook `OMiniMap.OnRender` 的 `Postfix`：对 `CoopSessionManager.AllyStates` 中的每个队友 `ally`，在 `OMiniMap` 子对象中维护一个 `DXObject`（`Z = 21`，`Image = DXImage.FromColor(Color.Transparent)`，`Border = new DXShaderEffect_Border(AllyOverlayRenderer.GetPlayerColor(ally.PlayerId), 1, DXShaderEffect_Border.TBorderUnit.InPixels, DXShaderEffect_Border.TBorderType.Frame)`，`InheritImageAttributes = false`），每帧设置其 `Area` 为：
  `Rectangle.Truncate(DXHelper_Rectangle.ChangeCoordinates(ally.CameraWorldArea, DXLevel.Current.PlayableArea, miniMap.InnerArea))`！

---

## 3. 下一阶段分步工作计划（Work Plan for Claude Code）

请严格按以下 **4 个阶段（P0 → P1 → P2 → P3）** 顺序推进，每完成一个阶段运行对应的验证门禁：

### 阶段 P0：修复确定性沙盒、ID 校准时序与捕获侧崩溃隐患（最高优先级）
1. **任务 P0.1：启用引擎同步寻路（解决 `pos` 分叉核心）**
   - 在 `ObfuscatedSymbols.cs` 新增 `Method_ZXGame_IsMultiplayer = "#=zB3Vhdin5PerfrubjO4AblAA="` 与 `Method_LevelSys_OnLevelLoadComplete = "#=zwK_pUIeRBpbp"`。
   - 在 `LockstepClock.cs` 的 `DeterministicSandboxPatches` 中新增对 `ZXGame.#=zB3Vhdin5PerfrubjO4AblAA=` 的 `HarmonyPrefix`，当 `LockstepClock.IsDeterministicModeActive` 为 `true` 时令 `__result = true; return false;`。
2. **任务 P0.2：单线程化物理步进（`DeterministicPhysicsPatch`）**
   - 在 `Determinism/LockstepClock.cs`（或独立文件 `Determinism/DeterministicPhysicsPatch.cs`）中 Hook `DXPhysicWorld.UpdateDefaultTime`，在确定性模式下用单线程 `for` 循环替代两个 `DXHelper_Task.For`（见本文档 §2.1 根源 B 代码）。
   - 在 `GameBridge.SortEntitiesById()` 中，同时对 `DXPhysicWorld.Bodies` 按 `b?.GameEntity?.ID ?? 0UL` 升序排序。
3. **任务 P0.3：修复读档完成时序、ID 计数器校准与物理时间戳对齐**
   - 在 `DXRandomCtorPatch.cs` 增加 `ResetCounter()` 并在 `EnableDeterministicSandbox` 中调用；同时在关卡就绪时设置 `DXLevel.Current.Random = new DXRandom(seed)`。
   - 修改 `DeterministicReplayVerifier.cs`：
     - **删掉** `ResetStaticsForRun()` 中的 `DXGame.LastGameTimeIAUpdate = 0.0` 和 `DXPhysicWorld.LastGameTimeUpdatePhysics = 0.0`；
     - 在 `OnBeforeLogicTick` 捕获 `_baselineTick` 的那一刻（此时存档所有实体和 `GameTime` 均已 100% 恢复完毕），再次调用：
       - `LockstepClock.CalibrateEntityIdCounterFromLoadedLevel(ReplaySeed)`（或将新生成同步实体计数器固定从 `0x4000000000000000L` 起跳，彻底杜绝与存档实体 ID 混淆）；
       - `GameBridge.SortEntitiesById()`;
       - `DXGame.LastGameTimeIAUpdate = DXGame.Current.GameTime;`
       - `DXPhysicWorld.LastGameTimeUpdatePhysics = DXGame.Current.GameTime;`
4. **任务 P0.4：清除 `CommandInterceptorPatch.cs` 中的泛型内部类型调用**
   - 将 `CommandInterceptorPatch.cs:84` 的 `e.HasComponent<CMovable>() && e.HasComponent<CCommandable>()` 替换为 `GameBridge.HasComponent(e, GameBridge.TypeCMovable) && GameBridge.HasComponent(e, GameBridge.TypeCCommandable)`。
- **【P0 验收门禁】**:
  - 编译部署后，分别对 `1_Backup.zxsav` 和生存中后期档 `6666.zxsav` 运行 `scripts/run_replay_verify.ps1 -SavePath ... -Ticks 120`；
  - 检查 `coop_pos_run1/2.txt` 确认不再出现 `100015~101138` 的低 ID 冲突，且 `compare_hashes.ps1` / `compare_pos.ps1` 分叉归零（`PASS`）。

---

### 阶段 P1：修复相机等轴测坐标转换与开全图僵尸显形（解决 R1~R6）
1. **任务 P1.1：修复 `reveal_map` 立即显示所有僵尸（R3）**
   - 在 `GameBridge.SetFogEnabled` / `SetShowFullMap` 中，反射清空 `ZXSystemMapLayers.#=zQFWYIvs=` 的 `#=z69ntQguW1qCr` 与 `#=zwJQaMvq$T3G4` 数组，并遍历 `DXGame.Current.Entities` 将 `e.SceneObject.Opacity = 1f; e.SceneObject.Visible = true;`。
2. **任务 P1.2：修复 `CameraProbe` 与 `ControlServer.SetCamera` 的等轴测坐标转换（R1/R2/R4）**
   - 使用 `DXWorldGrid.ScenePointFromWorldPoint(new PointF(wx, wy))` 将世界坐标转为场景坐标，调用 `DXScene.Current.SetCameraCenter(scenePt, width)` 设置相机，并将 `ZXSystemGameLevel` 的开局缩放动画字段 `#=zfFXkD0aj1o9gF76ANQ==` 置为 `false`。
   - 在 `CameraProbe.ToJson` 或 `build_divergence_report.py` 中补充 `WorldPoint <-> ScenePoint` 的等轴测映射，使截图上的分叉标注框精确套在对应单位身上。
- **【P1 验收门禁】**:
  - 启动控制模式加载 `6666.zxsav`，调用 `reveal_map` + `set_camera` 移动到僵尸密集区，确认截图中迷雾完全消失且僵尸清晰可见。

---

### 阶段 P2：实现真·网络帧同步门控（`TurnInputTickGate` 接线，`docs/07 §1`）
1. **任务 P2.1：`CoopSessionManager` 接入 `TurnInputTickGate` 与按 Tick 缓冲执行**
   - 新增 `private static readonly SortedDictionary<int, List<NetCommand>> _pendingTurnCommands = new SortedDictionary<int, List<NetCommand>>();` 与 `private static readonly TurnInputTickGate _networkGate = new TurnInputTickGate();`。
   - 当进入联机同步关卡（`IsInSyncedLevel = true`）时，将 `LockstepClock.TickGate = _networkGate`。
   - 引入 `InputDelayTicks = 2`（66ms 输入延迟缓冲）：
     - 局内初始就绪时预放行前 `InputDelayTicks` 帧（`_networkGate.MarkReady(baseTick + InputDelayTicks)`）；
     - 每推进 1 个逻辑帧 `simTick`（或在固定 30Hz 节拍下），Host 汇总当前收集到的指令，打上 `TargetSimTick = simTick + InputDelayTicks`，广播 `TurnInputBroadcast(targetTick, cmds)`（即使无指令也发 `count=0` 的空心跳包！）；
     - Host 和 Client 收到 `TurnInputBroadcast(targetTick, cmds)` 后，将 `cmds` 存入 `_pendingTurnCommands[targetTick]`，并调用 `_networkGate.MarkReady(targetTick)`；
     - 在 `UpdateGameLogic_Prefix` 放行 `nextTick` 的瞬间，调用 `CoopSessionManager.FlushDueTurnCommands(nextTick)` 执行该 tick 的所有指令！
   - 加入超时保护：若某帧等待超过 `3000ms` 仍未收到网络包，打印警告并在顶部横幅提示等待队友网络，防止无响应死锁。
- **【P2 验收门禁】**:
  - 运行 `scripts/run_chaos_two_instance.ps1`（带 120ms 延迟 + 30ms 抖动），验证 Host 与 Client 握手、快照同步并按 `TurnInputBroadcast` 心跳同步推进 `SimTick`，双方每 60 tick 的 `StateHash` 一致。

---

### 阶段 P3：完成暂停 / 倍速 / 市长 Producer 拦截与小地图盟友视野框（Phase 3 收尾）
1. **任务 P3.1：修复 `PauseSyncController` 并接入输入层拦截**
   - 修复 `PauseSyncController.Apply` 使其通过 `CommandInterceptor.OnLocalCommandCaptured` 广播 `NetCommandType.SetPaused`。
   - 新建 `Patches/PauseAndSpeedInputPatch.cs`：Hook `ZXSystemGameLevel.OnKeyDown`（拦截 `ZXKeyboardCommand.Pause`）与 `OnKeyUp`（拦截 `+`/`-` 倍速键），绝不 Hook `DXGame.set_Paused`。
2. **任务 P3.2：市长选择安全拦截 (`Patches/MayorSyncPatch.cs`)**
   - Hook `ZXMayor.#=zPTw8CXGE0RqF`，严格加上 `!LoadingLevel && DXGame.Current?.IAIterationInt > 0 && CommandInterceptor.IsInterceptionEnabled && !CommandExecutor.IsReplayingCommand` 守卫，捕获 `MayorId` 并广播 `MayorVoteOrSelect`。
3. **任务 P3.3：小地图队友视野框与地面光标 (`UI/AllyOverlayRenderer.cs`)**
   - Hook `OMiniMap.OnRender` Postfix，按 §2.3 第 3 小节的 `DXHelper_Rectangle.ChangeCoordinates` 公式绘制队友彩色视野框。
   - 支持 `Alt + 左键` 发送 `PacketType.TacticalPing` 并在全员小地图播放雷达波纹 `OMiniMap.#=zvr$ku1Q=`。
- **【P3 验收门禁】**:
  - 编译 0 错误，`Harmony Classes OK` 全部通过且 `failed-tolerant=0`，加载存档无崩溃，单机开启 `TABCOOP_INTERCEPT=1` 测试空格暂停、`+/-` 调速、右键移动、建造、造兵全部正常回放。
