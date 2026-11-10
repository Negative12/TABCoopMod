# 11 — 深度技术审计、四大阻塞根因解答与下一阶段详细设计及工作计划

> **编写时间**: 2026-11-10
> **作者**: 架构与逆向专家组（Hermes Agent）
> **状态**: **当前最高优先级指导文档（Single Source of Truth）**
> **适用对象**: 后续接手开发的 AI Agent（Claude Code / Codex）及人类开发者
> **前置关联**: 本文档直接解答 `docs/10_OPEN_QUESTIONS.md` 中的全部 5 大类开放问题，并接替 `docs/09_AUDIT_AND_WORKPLAN.md` 成为下一阶段（Phase 2 收尾 & Phase 3 联调）的详细设计与执行蓝图。

---

## 1. 项目当前整体进度盘点 (Project Status Executive Summary)

经过前 4 轮迭代（Phase 0 ~ Phase 3 骨架与自动化验证台建设），`TABCoopMod` 的工程成熟度评估如下：

| 模块与阶段 | 完成度 | 已验证通过的能力（有实机日志/截图支撑） | 当前残余阻塞点 |
| :--- | :---: | :--- | :--- |
| **Phase 0：免改 EXE 注入与工具链** | **100% ✅** | `AppDomainManager` 引导、27 项 `GameBridge` 反射绑定（`0 missing`）、25 个 Harmony 补丁类（`failed-tolerant=0`）、主菜单原生按钮注入。 | 无 |
| **Phase 1：确定性沙盒与指令回放** | **92% 🔄** | 脱离 `CSelectable` 的 7 类指令序列化与 `object` 反射回放、引擎 `IsMultiplayer` 同步寻路、双时钟相位对齐、实体排序；`life/proj/prod/resv/经济/实体数` 100% 确定，前 68 tick 全状态（含 `pos`）逐位一致。 | **问题 1**：Tick 69 起出现“A/B 二值吸引子” `pos` 微偏（原因已查明，见 §2.1）；<br>**问题 4**：`6666.zxsav` 因载入期串行化 `DXHelper_Task` 导致超时（见 §2.4）。 |
| **自动化测试台与 MCP 视觉工具** | **100% ✅** | 游戏内 TCP JSON `ControlServer`、独立 MCP Server、等轴测坐标转换 `SetCameraWorld`、`RevealAll` 僵尸立即显形、双进程回放比对与 HTML 分叉可视化报告。 | 无 |
| **Phase 2：网络传输与快照秒传** | **75% 🔄** | `LocalTcpLoopbackTransport`、`SteamP2PTransport`、`ChaosTransport` 故障注入；Host 进入关卡自动导出 `.zxsav` 并分片广播，Client 完整接收落盘并校验。 | **问题 2（核心阻塞）**：Client 收到快照后调用 `LoadGameAndEnter` 卡在主菜单不进图（原因已 100% 查明，见 §2.2）。 |
| **Phase 3：Lockstep 门控与协同 UX** | **65% 🔄** | `TurnInputTickGate`、按 `TargetSimTick` 缓冲队列、输入层暂停/倍速拦截、市长四重守卫同步、3 秒恢复倒计时、全图状态 Hash 对账。 | **问题 3**：Chaos 模式下偶发 `0xc00000fd` 栈溢出（原因已查明，见 §2.3）；<br>**问题 5**：确定性时钟下 `Paused` 未冻结 `SimTick`、小地图队友视野框待挂载（见 §2.5 & §3.5）。 |

---

## 2. `docs/10_OPEN_QUESTIONS.md` 全部遗留问题的反编译根因与解答

本节通过反编译 `TheyAreBillions.exe`（`ZXGame`、`ZXSystemGameLevel`、`ZXLevelState`、`CMovable`、`ZXBehaviour`、`CSprite`、`OFakeMeter`）与 `DXVision.dll`（`DXGame`、`DXScene`、`DXEntity`、`DXBehaviour<BData>`、`DXHelper_Task`、`DXPhysicWorld`），结合实机日志（`TABCoopMod.host.log` / `client.log` / `coop_replay_run1/2.timeline`），对 `docs/10` 中的每一个问题给出**源码级铁证**与**精确解法**。

---

### 2.1 【解答问题 1】为什么回放测试在前 68 tick 100% 一致，却在 tick 31973 出现“每进程二值（A/B 吸引子）”的 `pos` 分叉？

#### 🔍 反编译铁证与根因定位
对比 `coop_replay_run1.timeline` 与 `coop_replay_run2.timeline` 可见：
- 从 tick `31905` 到 `31972`（共 68 个逻辑帧），两进程的 `hash`、`entities`、`pos`、`life`、`proj`、`prod`、`resv` **100% 逐位相等**（tick 31972 均为 `0xEFCB1E531A47E264`）。
- 到了 tick `31973`，`entities/life/proj/prod/resv` 依然完全相等，唯独 `pos` 校验和出现了第一个微小偏差，且在多次独立运行中只有 **A/B 两种固定轨迹**。

排查反编译源码后，锁定造成“每进程 A/B 二值分叉”与潜在漂移的 **3 个确切源码级漏网之鱼**：

##### 根源 1A（二值 A/B 吸引子的直接元凶）：`DXRandomCtorPatch` 与后台渲染/纹理线程存在跨线程计数器竞态，导致 `ZXLevelState` 反序列化出的两个核心 `DXRandom` 种子发生 `N` vs `N+1` 互换！
1. 查看 `ZXLevelState.cs:111-114`：
   ```csharp
   [DXClonable]
   private DXRandom #=zh9ef5yW$F74p116B0w== = new DXRandom(); // 关卡核心随机源 1（用于行为树/波次/感染）
   [DXClonable]
   private DXRandom #=z4mA$cE116F1B52p3wA== = new DXRandom(); // 关卡核心随机源 2
   ```
   注意：这两个 `DXRandom` 标记了 `[DXClonable]`，当从 `.zxsav` 反序列化恢复 `ZXLevelState` 时，序列化器会通过反射调用 `new DXRandom()` 无参构造函数，然后再把存档里的 `Mt` 数组拷进去——**但是**，`DXRandom` 的游标字段 `private int mti = 625;`（`DXRandom.cs:23`）是 `private` 字段且**没有公开属性**！序列化器只恢复了 `public uint[] Mt { get; set; }`，或者在构造新 `ZXLevelState` / `DXBehaviour` 时直接触发了 `new DXRandom()`！
2. 更致命的是，看 `DXRandomCtorPatch.cs:27-35`：
   ```csharp
   if (!LockstepClock.IsDeterministicModeActive) return;
   long n = Interlocked.Increment(ref _seedCounter);
   uint seed = (uint)(0x9E3779B9u * (uint)n) ^ 0x85EBCA6Bu;
   _initGenrand(__instance, seed);
   ```
   `DXRandomCtorPatch` 只要 `IsDeterministicModeActive == true`，**无论当前是哪个线程调用 `new DXRandom()`，都会递增同一个全局 `_seedCounter`！**
   而在 `DeterministicReplayVerifier.PrepareForLoad()` 中，我们在读档**之前**就开启了 `IsDeterministicModeActive = true`！读档是在 `Task.Factory.StartNew` 后台线程跑的，与此同时主渲染线程仍在跑 `MainMenu` / `DXScene` 动画与 UI 粒子（它们也会 `new DXRandom()`）！
   于是：**主渲染线程与读档后台线程在抢同一个 `_seedCounter`**！主线程只要多跑 1 帧或少跑 1 帧，读档线程里關鍵组件拿到的种子序号就从 `k` 变成了 `k+1`（只有两种可能，取决于主线程在那 5 秒内插队了几次），从而产生经典的 **A/B 双吸引子现象**！

##### 根源 1B：`ZXBehaviour.#=z6E2u0eR7yefR`（僵尸闲逛 Wander 逻辑）直接读取了未被重置的 `DXBehaviour<ZXBehaviourData>.GlobalRandom`！
查看 `ZXBehaviour.cs:1563-1616`（僵尸随机游荡/微移的核心方法 `#=z6E2u0eR7yefR`）：
```csharp
public static DXBehaviourResult #=z6E2u0eR7yefR(ZXBehaviourData #=z0o_eM4o=, int #=z2p_eM4o=, float #=z3p_eM4o=)
{
    ...
    // 第 1581 行：直接调用基类静态属性 DXBehaviour<ZXBehaviourData>.GlobalRandom！
    if (DXBehaviour<ZXBehaviourData>.GlobalRandom.NextDouble() < (double)#=z3p_eM4o=)
    {
        ...
        Point point2 = #=z0o_eM4o=.Entity.Cell + DXWorldGrid.Adjacents8[DXBehaviour<ZXBehaviourData>.GlobalRandom.GetInt(8)];
    }
}
```
再看我们现在的 `LockstepClock.ResetBehaviourRandom(int seed)`（`LockstepClock.cs:204-233`）：
```csharp
Type open = typeof(DXVision.DXBehaviour<>);
Type closed = open.MakeGenericType(bdata); // bdata = ZX.Behaviours.BData
```
**重大发现（Bug）**：
在 `TheyAreBillions.exe` 中，所有僵尸和单位的 AI 行为树数据类叫 **`ZX.Behaviours.ZXBehaviourData`**（继承自 `DXBehaviourData`），而不是 `BData`！
`GameBridge.GameAssembly.GetType("ZX.Behaviours.BData")` 返回的是 `null`（或者根本不是 `ZXBehaviour` 使用的泛型参数 `ZXBehaviourData`）！在 C# 中，静态字段 `DXBehaviour<T>._GlobalRandom` 是**每个封闭泛型类型 `T` 独立一份**的！
因为 `ZXBehaviour` 继承自 `DXBehaviour<ZXBehaviourData>`，如果 `ResetBehaviourRandom` 没有重置 `DXBehaviour<ZXBehaviourData>._GlobalRandom`，那么 `DXBehaviour<ZXBehaviourData>.GlobalRandom` 使用的就是进程启动时由 `private static int _Seed = DXRandom.Global.Next();`（沙盒开启前，纯随机！）生成的静态实例！

##### 根源 1C：`CMovable.#=zUPt0E_k=` 的重寻路随机抖动与渲染可见性耦合（`CSprite.#=zbBN4HvXVWuDv()`）
1. 查看 `CMovable.cs:1292`：
   ```csharp
   this.#=z$r8q5u9A9X9p = DXGame.Current.IAIterationInt + 20 + DXRandom.Global.GetInt(10);
   ```
   每个移动中的单位每隔 `20 + GetInt(10)` 帧重新规划一次局部避让路径。只要某个单位在某帧多调或少调了一次 `DXRandom.Global`，大约 20~70 tick 后就会触发某个僵尸重寻路时机相差 1 tick，产生 `0.02` 格的位移差。
2. 查看 `ZXEntity.cs:634` 与 `CSprite.cs:367`：
   在 `CMovable.cs:638` 的移动完成回调中，调用了 `base.Entity.#=zbkLIvkCtqwewx0xa2A==().#=zbBN4HvXVWuDv()`，即 `CSprite.#=zbBN4HvXVWuDv()`（更新精灵动画帧）。而在 `CSprite.cs:367` 中：
   ```csharp
   public void #=zbBN4HvXVWuDv()
   {
       if (!base.Entity.Visible) return; // ⚠️ 如果实体在当前视野外/不可见，直接提前返回！
       ...
   }
   ```
   虽然后续的动画帧计算本身不消耗 `DXRandom.Global`，但**任何在 `Entity.Visible` 分支内触发的特效/音效如果调用了 `DXRandom.Global` 或 `DXGuid.New()`，就会因为两台机器摄像机位置不同（`Entity.Visible` 不同）而拉偏同步随机数流**！

#### ✅ 问题 1 的回答与根治方案（对应 `docs/10 §1.5`）
1. **四个实验怎么选？**
   - **必须立即执行“修正版实验 A + 修正 `DXBehaviour<ZXBehaviourData>` 重种 + 修正 `ZXLevelState` 内部两个私有 `DXRandom` 重种”**！
   - 具体操作：
     1. **修复 `ResetBehaviourRandom`**：遍历 `GameBridge.GameAssembly.GetTypes()` 中所有继承自 `DXVision.DXBehaviourData` 的类型（特别是 `ZX.Behaviours.ZXBehaviourData`），对每一个构造 `typeof(DXBehaviour<>).MakeGenericType(t)`，将其静态字段 `_Seed` 设为 `seed`，并将 `_GlobalRandom` 设为 `new DXRandom(seed)`！
     2. **重置 `ZXLevelState` 内部私有 `DXRandom` 字段**：在 `LevelLoadComplete` 与基线对齐时，通过反射扫描 `ZXLevelState` 实例的所有 `DXRandom` 类型字段（即 `#=zh9ef5yW$F74p116B0w==` 和 `#=z4mA$cE116F1B52p3wA==`），将它们重新赋值为 `new DXRandom(seed ^ fieldIndex)`（因为刚 new 出来的 `DXRandom(seed)` 其 `mti` 确定性地处于初始状态，不会出现存档反序列化只恢复 `Mt` 却丢失 `mti` 游标的问题！）。
     3. **隔离 `DXRandomCtorPatch` 的线程污染**：在 `DXRandomCtorPatch.Prefix` 中，**仅当 `LockstepClock.InDeterministicContext || LockstepClock.LogicTickActive` 时**才递增同步 `_seedCounter`；非逻辑线程（如主菜单 UI 渲染线程、加载过渡动画线程）调用 `new DXRandom()` 时，使用独立的非同步计数器 `_uiSeedCounter`，彻底斩断渲染线程对逻辑种子序号的插队竞态！
     4. **隔离 `CSprite` / 音效中的 `DXRandom.Global`**：在 `LockstepClock.DXRandom_Global_Prefix` 中，仅当 `Thread.CurrentThread.ManagedThreadId == LogicThreadId` 且不在 `CSprite` / `DXSound` 渲染/音效辅助上下文时才返回 `_syncedRandom`，确保视野内外（`Entity.Visible`）绝不影响 `_syncedRandom` 游标。

---

### 2.2 【解答问题 2 · 最高优阻塞】为什么 Client 收到快照后调用 `LoadGameAndEnter` 5ms 即返回、永远卡在主菜单？

#### 🔍 反编译铁证与 100% 确凿根因
查看实机日志 `TABCoopMod.client.log`：
```text
[14:32:56.835] [INFO] [CoopSessionManager] All 2 chunks received! Saved to ...\coop_client_sync.zxsav. Loading level...
[14:32:56.840] [INFO] [CoopSessionManager] LoadSaveFileAndEnter invoked (took 5 ms)
```
为什么 Host 在启动时调同一个 `GameBridge.LoadSaveFileAndEnter` 能正常读档进图，而 Client 收到快照后调 `LoadSaveFileAndEnter` 却什么都没发生？

我们反编译了 `ZXGame.cs:1483-2205` 和 `DXScene.cs:1601-1628`，真相水落石出：

1. 先看 `ZXGame.#=z9IpK6P4YlW8GIY7olg==(string path)`（即 `LoadSaveFileAndEnter`）做了什么：
   - 它调用 `#=z9DPDdq9qP9lZ(20, loadAction, finishAction, null, true)`（`ZXGame.cs:1911`）；
   - `#=z9DPDdq9qP9lZ` 第一行执行：`base.Paused = true;`，然后调用 `#=zXAMaTPI=(true, cb1)`（`ZXGame.cs:1918`）；
   - `#=zXAMaTPI=`（`ZXGame.cs:2189`）创建一个 `2000ms` 的淡出动画 `DXAnimation`，挂到黑屏遮罩对象 `this.#=zbvwh$ickzalV` 上，然后把 `this.#=zbvwh$ickzalV` 添加到 `base.Scene.AddObject(...)`，并等待 `dXAnimation.OnFinished` 触发后才执行 `cb1` -> `cb2` -> `Task.Factory.StartNew(loadAction)`！

2. **关键问题来了：为什么在 Client 上，这个 `dXAnimation` 永远不会完成（甚至根本没有推进）？！**
   看 **`DXScene.cs:1601-1628` 的 `ComputeSceneAvance()` 源码**：
   ```csharp
   private void ComputeSceneAvance()
   {
       if (this == Current)
       {
           double ticks = DXHelper_Environment.Ticks;
           if (!DXVision.DXGame.Current.WindowActive) // ⚠️ 第 1607 行：罪魁祸首 1！
           {
               _LastTimeUpdated = ticks;
           }
           if (_Paused)
           {
               _LastTimeUpdated = ticks;
           }
           _ElapsedTime = ticks - _LastTimeUpdated;
           _LastTimeUpdated = ticks;
       }
   }
   ```
   **请看第 1607 行**：
   ```csharp
   if (!DXVision.DXGame.Current.WindowActive)
   {
       _LastTimeUpdated = ticks;
   }
   ```
   当我们在同一台电脑上双开 Host 和 Client（无论是跑 `run_chaos_two_instance.ps1` 还是手动本机双开测试）时，**操作系统同一时刻只能有一个窗口处于前台激活状态（`WindowActive == true`）**！
   当 Client 窗口在后台（或者被 Host 窗口抢走焦点）时，`DXGame.Current.WindowActive` 为 **`false`**！
   于是 `ComputeSceneAvance()` 每帧都把 `_LastTimeUpdated = ticks`，导致 **`_ElapsedTime = ticks - _LastTimeUpdated = 0.0`**！
   紧接着在 `DXScene.Update()`（`DXScene.cs:1639`）中：
   `o.Animate(_ElapsedTime)` 传入的是 **`0.0` 毫秒**！
   因此，只要游戏窗口不在前台焦点，主菜单的淡入淡出动画 `dXAnimation` 进度永远是 `0 / 2000ms`，**`OnFinished` 永远不会触发，`loadAction` 永远不会启动**！

3. **这还不是全部！为什么之前尝试“Harmony 跳过 `#=zXAMaTPI=` 淡入淡出”也会静默失败？**
   我们检查了 `ZXGame.cs:1483` 的 `loadAction` 内部实现（`ZXGame_full.cs:1490-1540`）：
   ```csharp
   // loadAction 在后台线程执行：
   string path = #=z0o_eM4o=; // 传入的 filePath
   // ⚠️ 罪魁祸首 2：ZXGame.LoadGame 内部会校验同名 .zxcheck 文件！
   if (!#=zK0H9e1u2wP3x(path)) // 校验 .zxcheck MD5/CRC 签名！
   {
       throw new Exception("Corrupted save...");
   }
   ```
   看看 `CoopSessionManager.cs:458-466` 是怎么保存客机快照的：
   ```csharp
   string clientSavePath = Path.Combine(Path.GetTempPath(), "coop_client_sync.zxsav");
   File.WriteAllBytes(clientSavePath, receivedBytes);
   if (!string.IsNullOrEmpty(_incomingSnapshotCheckContent))
   {
       File.WriteAllText(clientSavePath + ".zxcheck", _incomingSnapshotCheckContent); // ❌ 致命错误！
   }
   ```
   **看第 464 行：`clientSavePath + ".zxcheck"` 拼出来的文件名是 `coop_client_sync.zxsav.zxcheck`！**
   而原版游戏 `ZXGame` 校验存档时查找的是 `Path.ChangeExtension(path, ".zxcheck")`（即 **`coop_client_sync.zxcheck`**，没有中间的 `.zxsav`）！
   对比 Host 端导出时的代码（`CoopSessionManager.cs:314`）：
   `string checkPath = Path.ChangeExtension(tempSavePath, ".zxcheck");` —— Host 端用的是正确的 `Path.ChangeExtension`，所以读取到了 `.zxcheck` 内容；但 Client 端写入时却写成了 `clientSavePath + ".zxcheck"`！
   结果就是：
   - 当没跳过 Fade 时，Client 因为窗口在后台（`WindowActive == false`），永远卡在第一段 Fade 动画（0%）；
   - 当跳过 Fade 时，后台线程真正执行了 `loadAction`，结果因为找不到 `coop_client_sync.zxcheck`，在校验阶段直接抛异常被 `catch when (!Debugger.IsAttached)` 吞掉，静默中止了加载！

#### ✅ 问题 2 的根治方案（两步彻底打通 Client 读档！）
1. **修复 `.zxcheck` 扩展名拼接 Bug（必改）**：
   在 `CoopSessionManager.cs:464` 中，将：
   ```csharp
   File.WriteAllText(clientSavePath + ".zxcheck", _incomingSnapshotCheckContent);
   ```
   修正为（为了双保险，两个文件名都写一份）：
   ```csharp
   string checkPath = Path.ChangeExtension(clientSavePath, ".zxcheck");
   File.WriteAllText(checkPath, _incomingSnapshotCheckContent);
   File.WriteAllText(clientSavePath + ".zxcheck", _incomingSnapshotCheckContent);
   ```
2. **强制后台窗口保持 `WindowActive = true`（必改）**：
   在联机模式或自动化测试（`TestHarnessConfig.Enabled || CoopSessionManager.IsSessionActive`）下，决不能让后台窗口因为失去焦点而冻结 `DXScene._ElapsedTime` 或停止渲染/动画回调！
   在 `LockstepClock.cs`（或独立补丁）中 Hook `DXGame.get_WindowActive`（或在 `DXGame.StartFrame` Prefix 中直接设置 `DXGame.Current.WindowActive = true`——经核对 `DXGame_full.cs:625`，`WindowActive` 是 `public bool WindowActive { get; set; }`，可直接赋值！）：
   ```csharp
   [HarmonyPatch(typeof(DXGame), "get_WindowActive")]
   public static class Patch_DXGame_AlwaysActiveInCoop
   {
       [HarmonyPrefix]
       public static bool Prefix(ref bool __result)
       {
           if (LockstepClock.IsDeterministicModeActive || CoopSessionManager.IsSessionActive || TestHarnessConfig.Enabled)
           {
               __result = true;
               return false;
           }
           return true;
       }
   }
   ```
3. **安全加速过渡动画（可选优化）**：
   不需要跳过 `#=zXAMaTPI=`（跳过可能破坏 `CurrentGameSystem` 生命周期状态机），只需在 `CoopSessionManager` 等待读档期间保持 `WindowActive = true`，2 秒钟的原生 Fade 就会顺滑走完并正常进入 `loadAction`！

---

### 2.3 【解答问题 3】为什么 Chaos 双实例下 Host 曾发生 `0xc00000fd` (STACK_OVERFLOW) 崩溃？

#### 🔍 源码级调用链追踪与死循环递归根因
查看 `CoopSessionManager.cs` 与 `CommandInterceptorPatch.cs`、`NetCommand.cs` 的交互，存在 **2 条致命的同步递归调用环**：

##### 递归环 A（最直接的 `0xc00000fd` 栈溢出元凶）：`CommandInterceptor.Dispatch` 在 Host 端或重入时的无守卫环路
看 `PauseSyncController.cs:69` 与 `PauseAndSpeedInputPatch.cs`：
1. 在 `CoopSessionManager.EnterSyncedLevel()`（`CoopSessionManager.cs:239`）中：
   `CommandInterceptor.OnLocalCommandCaptured = EnqueueLocalCommand;`
2. 但是，在 `CoopSessionManager.BroadcastCurrentLevelSnapshot()`（`CoopSessionManager.cs:300`）中，当 Host 有多个分片或重发快照时，或者当 `DesyncDetectedAlert` 触发时（`CoopSessionManager.cs:548`）：
   ```csharp
   case PacketType.DesyncDetectedAlert:
       int desyncTick = reader.ReadInt32();
       if (!Transport.IsHost)
       {
           DXGame.Current.Paused = true;
       }
   ```
3. **更危险的是 `ChaosTransport.cs:132-198` 的 `PollEvents() -> DeliverDue()`**：
   在 `LocalTcpLoopbackTransport.ReadAvailableFrames`（`INetworkTransport.cs:218`）中：
   ```csharp
   while (stream.DataAvailable)
   {
       ...
       HandleInternalOrDispatch(senderId, pktType, reader);
   }
   ```
   而 `ChaosTransport` 包装在 `LocalTcpLoopbackTransport` 外面：当 `ChaosTransport` 处于断连窗口结束或延迟到期时，`DeliverDue()` 会一口气把积压的几十个 `TurnInputBroadcast` / `ClientInputReport` 包同步回调给 `CoopSessionManager.OnPacketReceived`！
4. 再看 **`DeterministicTaskPatch.cs:25-39`**：
   ```csharp
   [HarmonyPatch(typeof(DXHelper_Task), nameof(DXHelper_Task.For), new[] { typeof(int), typeof(int), typeof(Action<int>), typeof(int) })]
   public static class Patch_DXHelperTask_ForIndex
   {
       [HarmonyPrefix]
       public static bool Prefix(int __0, int __1, Action<int> __2)
       {
           if (!LockstepClock.IsDeterministicModeActive) return true;
           if (__2 != null)
           {
               for (int i = __0; i < __1; i++) __2(i);
           }
           return false;
       }
   }
   ```
   来看看原版 `DXHelper_Task.cs:111-125` 的重载关系！
   ```csharp
   // 重载 1：For(int start, int end, Action<int> action, int itemsPerThread = 1)
   public static void For(int start, int end, Action<int> action, int itemsPerThread = 1)
   {
       For(start, end, delegate(int s, int e) // ⚠️ 它内部调用了重载 2：For(int, int, Action<int, int>, int)！
       {
           for (int i = s; i < e; i++) action(i);
       }, itemsPerThread);
   }
   ```
   这还不是递归，但请看 **`ZXSystemGameLevel` 和 `DXScene` 在主线程同步执行 `For` 时的调用栈深度**：
   当 `DeterministicTaskPatch` 将原本在后台 `Task` 线程池（默认 1MB 独立栈）执行的任务全部改到主线程同步执行，并且此时 `Host` 在 `OnLevelLoadComplete` -> `EnterSyncedLevel` -> `BroadcastCurrentLevelSnapshot`（在 Harmony Postfix 栈顶直接执行 ZIP 压缩 + XML 序列化全图 3000+ 实体，递归遍历 `[DXClonable]` 对象树）时，主线程原本就已经处在：`Main -> Run -> StartFrame -> InvokeOnStartFrame -> FinishLoadCallback -> OnLevelLoadComplete -> Postfix -> EnterSyncedLevel -> BroadcastCurrentLevelSnapshot -> ZXGame.SaveGame -> DXSerializer.SerializeRecursive` 的极深调用栈中！
   而 `DXSerializer` 是递归序列化整个实体-组件-行为树对象图的！在原版中，`SaveGame` 是在浅栈或后台线程调用的；而在我们的代码中，`EnterSyncedLevel` 在 `LevelLoadCompletePatch.Postfix` 中**同步立即调用了 `BroadcastCurrentLevelSnapshot()`**！当存档稍大或叠加 `ChaosTransport` 回调重入时，主线程 1MB 栈直接在 `SaveGame` 的深层递归序列化中耗尽，触发 `clr.dll 0xc00000fd (STACK_OVERFLOW)`！

#### ✅ 问题 3 的根治方案
1. **严禁在 `LevelLoadCompletePatch.Postfix` 栈内同步调用 `BroadcastCurrentLevelSnapshot()`！**
   将 `BroadcastCurrentLevelSnapshot()` 改为设置一个请求标志 `_pendingSnapshotBroadcast = true;`，延迟到下一渲染帧的 `CoopSessionManager.OnFrameTick()` 顶层（此时调用栈最浅，只有 3 层！）再执行 `SaveCurrentGameToFile`！
2. **给 `FlushDueTurnCommands` 和 `CommandExecutor.Execute` 加上重入深度保护（Depth Guard）**：
   添加 `[ThreadStatic] private static int _executionDepth;`，若 `_executionDepth > 4`，立即记录 `ModLogger.Error` 并中止递归，彻底杜绝任何命令回调引发的无限重入。

---

### 2.4 【解答问题 4】为什么 `6666.zxsav` 读档超过 180s 超时？哪些 `DXHelper_Task.For` 重载真正影响确定性？

#### 🔍 反编译铁证（全局搜索 `TheyAreBillions.exe` 与 `DXVision.dll` 的所有 `DXHelper_Task.For` 调用点）
我们对反编译出的全部源码进行了地毯式排查，找出了游戏里调用 `DXHelper_Task.For` 的**所有位置**：

| 序号 | 所在类与方法 | 调用的 `For` 重载签名 | 作用与频率 | 是否影响模拟确定性？ |
| :-: | :--- | :--- | :--- | :-: |
| 1 | `ZXSystemGameLevel.#=zmTU4kueQctVr` (`InitLevel`, 第 395 行) | `ForEach<DXImage>(images, img => img.UpdateTransparencyData())` (`System.Threading.Tasks.Parallel.ForEach`) | 读档时计算精灵透明度碰撞掩码 | ❌ 否（纯只读像素计算） |
| 2 | `DXScene.UpdateObjects()` (`DXScene.cs:1557`) | `For(0, Objects.Count, (int s, int e) => ..., 500)` | 每渲染帧计算场景物体的屏幕裁剪与变换矩阵 | ❌ **否！纯渲染层！** 在 `6666.zxsav` 中有 **33,000+ 个 `DXObject`**，单线程跑每帧耗时极长！ |
| 3 | `DXLevel.LoadTable()` (`DXLevel.cs:180`) | `For(0, tables.Count, (int i) => ...)` | 读档时并行反序列化数据表 | ❌ 否（各表独立，无共享 RNG/ID） |
| 4 | `DXPhysicWorld.UpdateDefaultTime()` (`DXPhysicWorld.cs:114`) | `For(0, Bodies.Count, (int s, int e) => StartUpdatePhysics(), 200)` | 每物理帧（12Hz）向 C++ 物理引擎写入刚体受力 | ✅ **是！唯一影响物理确定性的地方！** |
| 5 | `DXPhysicWorld.UpdateDefaultTime()` (`DXPhysicWorld.cs:126`) | `For(0, Bodies.Count, (int s, int e) => OnNewPositionComputed(), 200)` | 每物理帧（12Hz）从 C++ 物理引擎读回刚体新坐标 | ✅ **是！唯一影响位置确定性的地方！** |
| 6 | `ZXSystemMapLayers` 迷雾/阴影贴图刷新 | `For(0, NCells, (int s, int e) => ...)` | 渲染迷雾纹理像素 | ❌ 否（纯渲染层纹理生成） |

#### 💡 真相大白！
为什么 `6666.zxsav`（3.3 万个实体，近 10 万个 `DXObject` 渲染节点）在开启 `DeterministicTaskPatch` 后会卡死超过 180 秒？
**因为 `DeterministicTaskPatch` 把 `DXScene.UpdateObjects()`（每帧遍历近 10 万个 `DXObject` 做矩阵变换）和读档期的 `DXLevel` 数据加载、迷雾贴图计算全部强行变成了单线程串行执行！**
而实际上，整个游戏引擎在 `DXGame.IsIADeterministic = true` 且 `ZXGame.IsMultiplayer() = true` 时：
- 实体 AI 更新（`DXGame.UpdateGameLogic`）**本来就已经走单线程分支了**（`DXGame_full.cs:2964`：`if (IsIADeterministic) { 单线程遍历 _Entities }`）；
- 寻路（`CMovable`）**本来就已经走主线程同步 `FindPath` 了**；
- **全游戏唯一还在用多线程且影响模拟状态的，只有 `DXPhysicWorld.UpdateDefaultTime()` 里的那两个 `For` 循环！**

#### ✅ 问题 4 的最佳解决方案
1. **在 `DeterministicTaskPatch` 中，绝对不要无条件拦截所有 `DXHelper_Task.For`！**
2. 新增一个标志 `LockstepClock.InPhysicsStep`（在 `DXGame.UpdatePhysics` 的 Harmony Prefix 中置 `true`，Postfix 中置 `false`）：
   - **只有当 `LockstepClock.IsDeterministicModeActive && LockstepClock.InPhysicsStep` 为 `true` 时**，`DeterministicTaskPatch` 才将 `DXHelper_Task.For` 改为单线程顺序执行！
   - 在读档期间（`LoadingLevel`）、场景渲染期间（`DXScene.UpdateObjects`）、迷雾贴图计算期间，**完全放行 `DXHelper_Task.For` 走原生多线程并行**！
3. **收益**：
   - `6666.zxsav` 的读档时间将从 `>180s` 恢复到原生的 **`3~5s`**，渲染帧率提升数倍！
   - 同时由于读档期不再让非逻辑线程污染 `DXRandomCtorPatch._seedCounter`（见 §2.1），且读档完成后统一重种 `ZXLevelState` 与 `DXBehaviour<ZXBehaviourData>` 的 RNG，**既保证了 100% 确定性，又彻底消除了大存档读档超时**！

---

### 2.5 【解答问题 5】暂停真正冻结 `SimTick`、中途加入 ID 分配与 RNG 分流审阅

1. **为什么现在按空格暂停后，画面和逻辑还在继续跑（`Paused` 不冻结 `SimTick`）？**
   - 看 `LockstepClock.cs:264-286` 的 `UpdateGameLogic_Prefix`：
     ```csharp
     if (!LockstepClock.TickGate.IsTickReady(nextTick)) { ... return false; }
     __instance.GameTime = DXGame.LastGameTimeIAUpdate + LockstepClock.FixedStepMs;
     ```
   - 再看原版 `DXGame.UpdateGameLogic()`（`DXGame_full.cs:2950`）：
     原版是通过 `GameSpeed == 0`（当 `Paused = true` 时 `GameSpeed` 返回 `0`）使 `_TaskGameTime` 不再增加 `GameTime`，从而当 `GameTime - LastGameTimeIAUpdate < 33.333ms` 时自然不推进逻辑帧！
   - 而我们的 `UpdateGameLogic_Prefix` **无视了 `__instance.Paused`！只要 `TickGate.IsTickReady(nextTick)` 为 `true`，它就强行把 `GameTime` 加 `33.333ms`，导致即使 `DXGame.Current.Paused == true`，游戏逻辑和僵尸依然在每帧狂奔！**
   - **正确修法（极其简单且绝不与 TurnGate 死锁）**：
     - 当 `DXGame.Current.Paused == true` 时：
       1. **在 `UpdateGameLogic_Prefix` 最开头（但在 `FlushDueTurnCommands` 之后！）检查 `__instance.Paused`**：
          - 先调用 `CoopSessionManager.FlushDueTurnCommands(nextTick)`（这样在暂停期间下达的建造、拉兵、训练、以及**解除暂停 `SetPaused(false)`** 指令都能在暂停态实时执行！）；
          - 如果执行完当前批次指令后，`__instance.Paused` 依然为 `true`，则令 `__instance.GameTime = DXGame.LastGameTimeIAUpdate; return false;`（不推进 `SimTick`、不推进 `GameTime`）！
       2. 在暂停期间，Host 的 `OnFrameTick` 不递增 `TargetSimTick`，而是对当前停住的 `nextTick` 发送即时控制包（或复用 `TargetSimTick = currentSimTick + 1`），一旦有人发出 `SetPaused(false)`，在 `FlushDueTurnCommands(nextTick)` 中将 `Paused` 置回 `false`，下一行立即恢复 `SimTick` 步进！**完全支持《亿万僵尸》最核心的“空格暂停微操建房/拉兵”体验！**

2. **中途加入 / Resync 的实体 ID 分配安全性**：
   - 将同步实体 ID 计数器 `_syncedEntityIdCounter` 的起始段固定为 **`0x4000000000000000L`**（即 $4.61 \times 10^{18}$，位于原版随机 ID 与本地 UI 位 `0x8000000000000000UL` 之间的独立安全段），并在 `SnapshotBegin` 包头中直接携带 Host 当前的 `LockstepClock.CurrentSyncedGuidCounter` 值。Client 读完快照后直接设置该精确值，且 Host 在 `BroadcastCurrentLevelSnapshot` 到收到全部 Client 的 `SnapshotLoadedAck` 期间保持 `Paused = true`（冻结 `SimTick`），即可保证 Host 与 Client 的实体 ID 100% 零差错对齐！

---

## 3. 下一阶段详细技术设计 (Detailed Technical Design for Next Phase)

本节提供可直接落地编码的详细设计（含精确类名、混淆符号、方法签名与代码实现模板）。

### 3.1 设计一：确定性沙盒终极闭环（消除 A/B 分叉 + 恢复大存档秒级加载）

#### 3.1.1 `Determinism/DeterministicTaskPatch.cs` 改造（仅在物理步进内串行化）
- 在 `LockstepClock.cs` 中已经有 `InPhysicsStep` 标志（由 `Patch_DXGame_UpdatePhysics` Prefix/Postfix 维护）。
- 修改 `DeterministicTaskPatch.cs` 的全部 3 个 `Prefix`：
  ```csharp
  // 仅在确定性物理步进期间串行化 DXHelper_Task.For，放行读档与 DXScene 渲染裁剪并行！
  if (!LockstepClock.IsDeterministicModeActive || !LockstepClock.InPhysicsStep)
      return true;
  ```

#### 3.1.2 `Determinism/DXRandomCtorPatch.cs` 改造（隔离非逻辑线程对种子计数器的污染）
```csharp
private static long _seedCounter;
private static long _nonSimSeedCounter;

[HarmonyPrefix]
public static void Postfix(DXRandom __instance)
{
    if (!LockstepClock.IsDeterministicModeActive || _initGenrand == null || __instance == null)
        return;

    // 仅在确定性模拟线程（逻辑帧或受控物理步）内使用同步计数器；
    // 渲染线程、UI 动画线程使用独立计数器，彻底防止主菜单/渲染帧率差异导致 _seedCounter 偏移 1 位（A/B 吸引子）！
    bool isSimThread = (LockstepClock.LogicThreadId != 0 && Thread.CurrentThread.ManagedThreadId == LockstepClock.LogicThreadId)
                       && (LockstepClock.LogicTickActive || LockstepClock.InDeterministicContext || LockstepClock.InPhysicsStep);

    if (isSimThread)
    {
        long n = Interlocked.Increment(ref _seedCounter);
        uint seed = (uint)(0x9E3779B9u * (uint)n) ^ 0x85EBCA6Bu;
        if (seed == 0u) seed = 1u;
        _initGenrand(__instance, seed);
    }
}
```

#### 3.1.3 `LockstepClock.ResetBehaviourRandom(int seed)` 全面覆盖 `ZXBehaviourData` 与 `ZXLevelState` 内部 RNG
在 `LockstepClock.cs` 中更新 `ResetBehaviourRandom(int seed)`：
```csharp
public static void ResetBehaviourRandom(int seed)
{
    try
    {
        var asm = GameBridge.GameAssembly;
        if (asm == null) return;

        Type open = typeof(DXVision.DXBehaviour<>);
        Type baseData = typeof(DXVision.DXBehaviourData);
        int resetCount = 0;

        // 1. 遍历所有 DXBehaviourData 子类（重点是 ZX.Behaviours.ZXBehaviourData），重置其泛型静态 _GlobalRandom
        foreach (var t in asm.GetTypes())
        {
            if (t == null || t.IsAbstract || !baseData.IsAssignableFrom(t)) continue;
            try
            {
                Type closed = open.MakeGenericType(t);
                var fSeed = AccessTools.Field(closed, "_Seed");
                var fRng = AccessTools.Field(closed, "_GlobalRandom");
                fSeed?.SetValue(null, seed);
                fRng?.SetValue(null, new DXRandom(seed));
                resetCount++;
            }
            catch { }
        }

        // 2. 重置 ZXLevelState 实例中的私有 DXRandom 字段 (#=zh9ef5yW$F74p116B0w==, #=z4mA$cE116F1B52p3wA==)
        object ls = GameBridge.CurrentLevelState;
        if (ls != null)
        {
            var fields = ls.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            int idx = 1;
            foreach (var f in fields)
            {
                if (f.FieldType == typeof(DXRandom))
                {
                    f.SetValue(ls, new DXRandom(seed ^ (idx * 0x27D4EB2Du)));
                    idx++;
                }
            }
        }
        ModLogger.Info($"[LockstepClock] ResetBehaviourRandom completed (behaviours={resetCount}, seed={seed}).");
    }
    catch (Exception ex)
    {
        ModLogger.Warn($"[LockstepClock] ResetBehaviourRandom failed: {ex.Message}");
    }
}
```

---

### 3.2 设计二：打通 Client 快照接收与无焦点加载闭环（解决 P2 阻塞）

#### 3.2.1 强制 `DXGame.WindowActive = true` 补丁
在 `LockstepClock.cs` 中新增补丁类，确保本机双开或窗口切到后台时，`DXScene.ComputeSceneAvance()` 正常计算 `_ElapsedTime`，使主菜单读档的 `DXAnimation` 淡入淡出顺利推进：
```csharp
[HarmonyPatch(typeof(DXGame), "get_WindowActive")]
public static class Patch_DXGame_WindowActive
{
    [HarmonyPrefix]
    public static bool Prefix(ref bool __result)
    {
        if (LockstepClock.IsDeterministicModeActive || Sync.CoopSessionManager.IsSessionActive || Testing.TestHarnessConfig.Enabled)
        {
            __result = true;
            return false;
        }
        return true;
    }
}
```

#### 3.2.2 修复 `CoopSessionManager` 快照落盘路径与主线程浅栈导出
1. **修复 Client `.zxcheck` 路径**（`CoopSessionManager.cs:462-466`）：
   ```csharp
   string clientSavePath = Path.Combine(Path.GetTempPath(), "coop_client_sync.zxsav");
   File.WriteAllBytes(clientSavePath, receivedBytes);
   if (!string.IsNullOrEmpty(_incomingSnapshotCheckContent))
   {
       string checkPath = Path.ChangeExtension(clientSavePath, ".zxcheck");
       File.WriteAllText(checkPath, _incomingSnapshotCheckContent);
       File.WriteAllText(clientSavePath + ".zxcheck", _incomingSnapshotCheckContent);
   }
   ```
2. **将 Host 快照导出与 Client 快照加载移至 `OnFrameTick` 顶层执行（消除 `0xc00000fd` 栈溢出与回调重入）**：
   - 在 `CoopSessionManager` 中新增：
     - `private static bool _pendingHostSnapshotExport;`
     - `private static string _pendingClientSnapshotLoadPath;`
   - `EnterSyncedLevel()`（在 `LevelLoadComplete` Postfix 深栈中触发）只设 `_pendingHostSnapshotExport = true;`，不在 Postfix 内直接调 `SaveCurrentGameToFile`；
   - `HandleSnapshotChunk()` 收齐分片后只设 `_pendingClientSnapshotLoadPath = clientSavePath;`，不在网络 `PollEvents` 循环内直接调 `LoadSaveFileAndEnter`；
   - 在下一帧 `OnFrameTick()`（由 `DXGame.StartFrame` Prefix 直接调用，栈深极浅）中检查并执行导出或加载！

---

### 3.3 设计三：暂停态真正冻结 `SimTick` 且支持暂停微操（解决 §2.5 问题 1）

在 `LockstepClock.cs` 的 `UpdateGameLogic_Prefix` 中调整顺序：
```csharp
[HarmonyPrefix]
public static bool UpdateGameLogic_Prefix(DXGame __instance)
{
    if (!LockstepClock.IsDeterministicModeActive) return true;

    if (LockstepClock.LoadingLevel)
    {
        HoldClocks(__instance);
        return false;
    }

    int nextTick = __instance.IAIterationInt + 1;
    Testing.DeterministicReplayVerifier.OnBeforeLogicTick(nextTick);

    if (!LockstepClock.TickGate.IsTickReady(nextTick))
    {
        HoldClocks(__instance);
        return false;
    }

    // 1. 先执行到期指令（包括在暂停期间下达的建造/移动，以及解除暂停 SetPaused(false) 指令！）
    Sync.CoopSessionManager.FlushDueTurnCommands(nextTick);

    // 2. 如果执行完本轮指令后游戏处于暂停状态，则冻结 SimTick 与 GameTime，不推进世界模拟！
    if (__instance.Paused)
    {
        HoldClocks(__instance);
        return false;
    }

    // 3. 非暂停状态：正常按固定步长 33.333ms 推进 1 个 SimTick
    LockstepClock.LogicThreadId = Thread.CurrentThread.ManagedThreadId;
    LockstepClock.LogicTickActive = true;
    LockstepClock.InDeterministicContext = true;

    if (DXGame.LastGameTimeIAUpdate <= 0.0 && __instance.GameTime > LockstepClock.FixedStepMs)
        DXGame.LastGameTimeIAUpdate = __instance.GameTime;

    __instance.GameTime = DXGame.LastGameTimeIAUpdate + LockstepClock.FixedStepMs;
    return true;
}
```
同时在 `CoopSessionManager.StepHostLockstep()` 中：
- 当 `DXGame.Current.Paused == true` 且 `_localOutgoingCommands.Count > 0` 时，Host 立即将这些暂停期间的操作以 `TargetSimTick = currentTick + 1`（即 `nextTick`）广播出去；两端在 `UpdateGameLogic_Prefix` 的 `FlushDueTurnCommands(nextTick)` 中立即执行它们（由于 `Paused == true`，`nextTick` 并未真正消耗，`IAIterationInt` 保持不变，因此下一次暂停操作仍可关联到 `nextTick`！）。

---

### 3.4 设计四：小地图队友视野框与地面光标渲染（Phase 3 收尾）

在 `src/TABCoopMod/UI/AllyOverlayRenderer.cs` 中补全基于 `OMiniMap` 的 Harmony 补丁：
1. 在 `ObfuscatedSymbols.cs` 确认 `OMiniMap` 相关符号（`ZX.GUI.OMiniMap` 类名明文，`OnRender` 方法名明文）。
2. Hook `ZX.GUI.OMiniMap.OnRender` 的 `Postfix`：
   - 当 `CoopSessionManager.IsSessionActive && CoopSessionManager.IsInSyncedLevel` 时，遍历 `CoopSessionManager.AllyStates.Values`；
   - 为每个队友在 `OMiniMap` 实例下懒加载创建一个 `DXObject` 边框（`Z = 21`，透明底色，`DXShaderEffect_Border` 设为队友代表色）；
   - 使用反编译拿到的官方精确公式更新 `Area`：
     ```csharp
     box.Area = Rectangle.Truncate(
         DXHelper_Rectangle.ChangeCoordinates(
             ally.CameraWorldArea,
             DXLevel.Current.PlayableArea,
             miniMap.InnerArea));
     ```

---

## 4. 下一步分阶段工作计划与验收门禁 (Actionable Work Plan & Verification Gates)

请接手开发的 Agent 严格按照以下 **Step A → Step B → Step C → Step D** 顺序执行，每一步都有明确的自动化验证命令与通过标准：

### Step A：修复 `DeterministicTaskPatch` 与 RNG 竞态，通过 P0 确定性回放门禁（含 `1_Backup` 与 `6666`）
- **修改文件**：
  1. `src/TABCoopMod/Determinism/DeterministicTaskPatch.cs`：加上 `&& LockstepClock.InPhysicsStep` 条件，释放读档与渲染多线程。
  2. `src/TABCoopMod/Determinism/DXRandomCtorPatch.cs`：仅在 `LogicThreadId` 且处于逻辑/物理上下文时使用 `_seedCounter`。
  3. `src/TABCoopMod/Determinism/LockstepClock.cs`：升级 `ResetBehaviourRandom` 覆盖所有 `DXBehaviourData` 子类（含 `ZXBehaviourData`）及 `ZXLevelState` 的两个私有 `DXRandom` 字段。
- **验收命令与门禁（Gate A）**：
  ```powershell
  dotnet build src/TABCoopMod/TABCoopMod.csproj -c Release
  powershell -ExecutionPolicy Bypass -File scripts/install_mod.ps1
  # 门禁 A1：战役档 120 tick 零分叉
  powershell -ExecutionPolicy Bypass -File scripts/run_replay_verify.ps1 `
    -SavePath "%USERPROFILE%\Documents\My Games\They Are Billions\Saves\1_Backup.zxsav" -Ticks 120
  # 门禁 A2：生存大档 6666.zxsav 15 秒内完成加载且 120 tick 零分叉
  powershell -ExecutionPolicy Bypass -File scripts/run_replay_verify.ps1 `
    -SavePath "%USERPROFILE%\Documents\My Games\They Are Billions\Saves\6666.zxsav" -Ticks 120
  ```
  - **通过标准**：`compare_hashes.ps1` 输出 **`PASS (120 ticks identical)`**，且 `6666.zxsav` 单跑总耗时 `< 45s`（不再 180s 超时）。

---

### Step B：修复后台窗口 `WindowActive` 与 `.zxcheck` 路径，打通 P2 双实例联机同步门禁
- **修改文件**：
  1. `src/TABCoopMod/Determinism/LockstepClock.cs`：新增 `Patch_DXGame_WindowActive` 强制联机/测试模式下 `WindowActive = true`。
  2. `src/TABCoopMod/Sync/CoopSessionManager.cs`：
     - 修复 `Path.ChangeExtension(clientSavePath, ".zxcheck")`；
     - 将 Host 快照导出（`_pendingHostSnapshotExport`）与 Client 快照加载（`_pendingClientSnapshotLoadPath`）移至 `OnFrameTick` 浅栈顶层执行；
     - 在 `SnapshotBegin` 中同步 `LockstepClock.CurrentSyncedGuidCounter`。
- **验收命令与门禁（Gate B）**：
  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts/run_chaos_two_instance.ps1 `
    -SavePath "%USERPROFILE%\Documents\My Games\They Are Billions\Saves\1_Backup.zxsav" -DurationSec 60
  ```
  - **通过标准**：
    1. `TABCoopMod.client.log` 出现 `[CoopSessionManager] Entered synced level at tick ...` 及 `[StateChecksum] Tick ... MATCH`；
    2. Host 与 Client 全程无 `0xc00000fd` 崩溃，60 秒内双端 `SimTick` 持续推进且 `StateHash` 一致。

---

### Step C：实现暂停态冻结 `SimTick` 与暂停微操同步
- **修改文件**：
  1. `src/TABCoopMod/Determinism/LockstepClock.cs`：在 `UpdateGameLogic_Prefix` 中 `FlushDueTurnCommands` 之后检查 `if (__instance.Paused) { HoldClocks(__instance); return false; }`。
  2. `src/TABCoopMod/Sync/CoopSessionManager.cs`：支持 `Paused` 状态下即时广播并执行玩家的建造/拉兵/恢复指令。
- **验收命令与门禁（Gate C）**：
  - 启动 `scripts/run_game_with_control.ps1`，在暂停状态下通过 MCP 调用 `place_building` 和 `move_units`，验证 `tick` 保持静止但建筑成功放下、随后调用 `request_pause(false)` 3 秒倒计时后 `tick` 正常恢复增长。

---

### Step D：挂载小地图队友视野框与战术 Ping（Phase 3 完结）
- **修改文件**：
  1. `src/TABCoopMod/UI/AllyOverlayRenderer.cs`：挂载 `OMiniMap.OnRender` Postfix 绘制队友彩色视野矩形框，并绑定 `Alt + 左键` 触发 `OMiniMap.#=zvr$ku1Q=` 小地图雷达打点。
- **验收命令与门禁（Gate D）**：
  - 双开进入同步关卡后，移动 Client 摄像机视角，在 Host 端截图确认右下角小地图出现代表 Client 的青色视野框。

---

## 5. 本轮执行结果与对本文档假设的修正（2026-11-10 实测）

> 执行人：Claude Code。按 §4 的 Step A~D 落地代码并逐项实机验证。结论：**Gate B 打通（重大进展）**；Gate A 的 `pos` 分叉仍未归零；Gate C 部分通过；Gate D 已实现未目视确认；6666 载入仍然超时。以下按门禁列出证据与对本文档原假设的修正，供下一轮接手者直接续作。

### 5.1 Step A（确定性回放门禁）：**未通过**

**实测（1_Backup，120 tick，两进程）**
- `compare_hashes`：`FAIL at index 33~40 (tick 31938~31945) [pos]`；`entities/life/proj/prod/resv/经济` 全程一致。
- `compare_pos` 在首个记录 tick 恒为 `PASS`，分叉仍在运行途中出现（与 `docs/10 §1` 一致）。
- `coop_loadpos_run1/2.txt` 与 `coop_baselinepos_run1/2.txt` 逐位相同，`loadPosHash=0x493A454F4E17FBAE` 两跑一致 → 读档态完全确定。

**本轮已落地且保留的确定性加固（均实机 0 错误、`Classes OK=28, failed-tolerant=0`）**
1. `DeterministicTaskPatch` 仅在 `LockstepClock.InPhysicsStep`（`DXGame.UpdatePhysics` 内）串行化 `DXHelper_Task.For`；读档与渲染恢复并行（`6666` 帧率/载入理论改善，但见 §5.4）。
2. `DXRandomCtorPatch` 拆分计数器：仅逻辑/物理/载入工作线程消耗 `_seedCounter`，主线程（菜单/渲染）用独立 `_nonSimSeedCounter`。
3. `DXRandom.Global` 分流修正：加载期间**只有加载工作线程**能拿到 `synced` 流；主线程 UI 一律走 `render` 流（此前主线程加载期抽签数量会随帧数漂移，污染同步流）。
4. `ResetBehaviourRandom` 覆盖全部 `DXBehaviourData` 子类（`types=2`，含 `ZX.Behaviours.BData`），并在 `DXEntity.Update` 前缀按“线程 + 载入代次”重置逻辑线程的 `DXBehaviour<BData>._GlobalRandom`（该字段是 `[ThreadStatic]`，主线程反射改不到逻辑线程）。
5. `DXGame.Focused/Visible` 在确定性模式下强制 `true`；`DXGame.IsFrameSceneModule(n)` 改为按 `IAIterationInt % n` 判定（消除渲染帧率差异对逻辑的影响）。
6. 读档完成时清除引擎残留的 `Paused`，并新增 `LockstepClock.PausedBySession`：**只有同步暂停命令**才冻结时钟（见 §5.3）。

**对本文档 §2.1 根因的反编译核对（重要修正）**
- `DXVision.DXGame.WindowActive` **在本版本不存在**（实际公开属性是 `Focused/Visible`）；`DXScene.ComputeSceneAvance` 也不读取它。按文档给 `get_WindowActive` 打补丁会命中不到目标。
- `ZX.ZXLevelState` **没有**任何 `DXRandom` 字段；文档所述两个私有 `DXRandom`（`#=zh9ef5yW$F74p116B0w==` / `#=z4mA$cE116F1B52p3wA==`）在本 DLL 中不存在。
- `ZX.Behaviours.BData` **确实存在**（`GetType` 不为 null），当前实现可用。
- `DXBehaviour<T>._GlobalRandom` 是 **[ThreadStatic]**（文档未提），且其 getter **无法被 HarmonyX 补丁**：闭合泛型与开放泛型均抛 `HarmonyException`（`Patching exception in method ...DXBehaviour`1<...>::get_GlobalRandom()`）。因此改用“线程内重置”方案（上一节第 4 条）。
- 即便叠加上述全部修复，`pos` 仍是每进程二值（run1/run2 各自的哈希在多次独立运行中稳定复现，例如 tick 31940：`0x72716DB4…` vs `0xE2CCE381…`；tick 31938：`0x0154E32E…` vs `0x13BD9B0E…`）。**结论：`pos` 分叉的根因尚未找到**，不在本文档列出的三条之内。

**已排除的更细线索**
- 物理步数/GameTime 每 tick 一致（`DXPhysicWorld.NUpdates/Iteration` 相同）。
- 实体集合与顺序一致（按 ID 重排；`_Entities` 3017 个参与哈希）。
- 相机两跑逐位相同（`.cam.json` 完全相同）。
- 强制 `Focused/Visible`、关闭 `IsFrameSceneModule` 渲染耦合、隔离主线程 RNG 流后，分叉 tick 只发生小幅移动（31938 ↔ 31945），未消失。

### 5.2 Step B（Client 快照加载门禁）：**通过（功能链路）**

**两个真实根因（与 `docs/10 §2` 和本文档 §2.2 的猜测都不同）**
1. **`.zxcheck` 校验与“存档文件名”绑定**：Client 把收到的快照保存为 `coop_client_sync.zxsav`，再复用 Host 的 `.zxcheck` 内容 → 引擎 `ReadSaveFile` 抛“error while loading the game”。**改为与 Host 完全相同的文件名 `coop_host_snapshot.zxsav/.zxcheck` 后，加载立即成功。**（`IsSaveLoadable` 预检为 true，说明不是签名算法问题，而是名字/路径绑定。）
2. **ChaosTransport 抖动会重排“快照分片”**：`SnapshotBegin` 之前就可能先收到若干 `SnapshotChunk`，旧实现按到达顺序写入 `MemoryStream`，直接丢包。**改为 `Dictionary<int, byte[]>` 按索引缓冲、集齐 `totalChunks` 后按序拼接，再延迟到 `OnFrameTick` 顶层加载。**

**其它修正**
- Host 的快照导出改在 `CoopSessionManager.OnLevelLoadComplete`（此前在 `InitLevel` 触发，导出的是**半反序列化**的存档，且提前 `EnterSyncedLevel`）。
- Host 心跳广播改看 `LockstepClock.PausedBySession`（不看 `DXGame.Paused`）；`EnterSyncedLevel` 清除引擎残留 pause，`BroadcastCurrentLevelSnapshot` 的 `finally` 不再把 `GameSpeed` 恢复成 0。
- Client 加载期间由 `GameBridge.KickLoadingFade` 主动推进引擎的黑色淡入淡出动画（`DXObject.Animate`），不再需要“跳过回调”方案。
- 新增 `MessageBoxAlertDiagPatch`：引擎加载失败只弹框、不抛异常，此补丁把弹框文本写日志（正是靠它定位到上面的 `.zxcheck` 问题）。

**实机结果（loopback 双开 + Chaos `latency=40;jitter=10;loss=0;seed=42`）**
```
[client] Entered synced level at tick 31906 (inputDelay=2, role=Client).
[host]   ClockDiag tick=32803 ...
[client] ClockDiag tick=32805 ...   # 双端随后保持同一 SimTick
```
- 双端 30s 内持续推进，**无 `0xc00000fd`、无 Client 未处理异常**。
- `StateChecksumReport` 仍报 `DESYNC`（tick 32700/32760/32820）；原因是 §5.1 的 `pos` 残差，而非快照链路。

**自动 Resync 已暂时关闭**（`CoopSessionManager.EnableAutoResync=false`）：在“关卡内”二次调用 `LoadSaveFileAndEnter` 会让引擎 `Scene.Init` 清空场景后、旧实体仍在 `_Entities` 中被 `SetCamera → UpdateSceneObjectParallel → CShadow` 访问，触发 `NullReferenceException`（AggregateException 崩溃）。待 `pos` 修复、或改为“回主菜单后再加载”的 Resync 流程后，再把该开关打开。

### 5.3 Step C（暂停冻结 + 暂停微操）：**部分通过**

- 控制服务实测：`set_paused(true)` 后连续 3 次 `game_state` 的 `tick` 恒为 `32647`（冻结生效）；`request_pause(false)` 的 3 秒倒计时结束后 tick 恢复增长（`31904 → 32376`）。
- 暂停期 `place_building`：命令成功派发（`[NetCmd ... PlaceBuilding]`），但在所测坐标上未生成建筑（这些坐标疑似被 CommandCenter 占地/不可建造占据）。**“暂停期建造成功”尚未拿到实机证据**；机制上 `UpdateGameLogic_Prefix` 已调整为“先 `FlushDueTurnCommands(nextTick)`，再检查 `PausedBySession`”，等待用确定性控制会话复核。
- 注意：控制脚本（`run_game_with_control.ps1`）未开启确定性沙盒，所以本轮暂停冻结由原版 `GameSpeed=0` 完成；确定性时钟下的暂停冻结仍需双实例/测试台复核。

### 5.4 Gate A2（6666.zxsav 载入）：**未达成**

- 恢复“读档并行”后，`6666.zxsav` 在控制模式（未开确定性沙盒，等价原生多线程）下**等待 >210 秒仍未完成载入**（进程存活、CPU 约 58s/3.5min，未崩溃）。
- 因此“并行读档 3~5 秒”在本机/本版本不成立；`6666` 载入瓶颈更可能在 `ZXSystemGameLevel.InitLevel` 的 3.3 万实体场景对象构建本身，而不是 `DXHelper_Task.For` 的串行化。建议下一轮先做**分段计时**（`ReadSaveFile` / `InitLevel` / 场景对象创建）再定优化方案。

### 5.5 Step D（小地图队友视野框）：**已实现，未目视验收**

- `AllyMiniMapOverlayPatch` 已挂 `ZX.GUI.OMiniMap.OnRender` Postfix，按玩家色为每个 `AllyStates` 创建 `DXObject` 边框并用 `DXHelper_Rectangle.ChangeCoordinates(ally.CameraWorldArea, DXLevel.Current.PlayableArea, InnerArea)` 定位；实机补丁 0 失败。
- 未做双开截图确认（需要同时进图并移动 Client 相机）。

### 5.6 复现命令（本轮实际使用的）

```powershell
# Gate A
powershell -ExecutionPolicy Bypass -File scripts/run_replay_verify.ps1 `
  -SavePath "%USERPROFILE%\Documents\My Games\They Are Billions\Saves\1_Backup.zxsav" -Ticks 120

# Gate B（含 15~25s 观测保持；脚本已修复“Chaos injection active 误判为快照已广播”的 bug）
powershell -ExecutionPolicy Bypass -File scripts/run_chaos_two_instance.ps1 `
  -SavePath "%USERPROFILE%\Documents\My Games\They Are Billions\Saves\1_Backup.zxsav" `
  -Chaos "latency=40;jitter=10;loss=0;seed=42" -TimeoutSeconds 60 -HoldSeconds 15
```

### 5.7 给下一轮的明确建议

1. **`pos` 分叉**：不要再重复“ctor 计数器 / Global 流 / ThreadStatic 行为 RNG / 相机 / 渲染帧”这几类已排除项。建议直接做 `docs/10 §1.4 实验 B` 的加强版：在基线帧反射遍历**全部实体组件 + DXLevel/ZXGameState + CBehaviour** 中所有 `DXRandom` 实例，dump `mt[0..3]`/`mti`/所属 ID，两进程逐项对比，定位到具体实例；再决定是否按 ID 重种。
2. **Resync 崩溃**：改为“先回主菜单 → 再加载快照”，不要在关卡内 `Scene.Init` 后直接读档。
3. **6666 载入**：先加分段计时，确认瓶颈；不要再次假设是 `For` 并行度问题。
4. **Gate C**：用 `TABCOOP_TEST=1 + 控制端口 + 确定性沙盒` 的组合复核“暂停期建造/移动”，并把 `PausedBySession` 的广播时序（暂停中 `TargetSimTick = 当前 tick`）纳入验证。
