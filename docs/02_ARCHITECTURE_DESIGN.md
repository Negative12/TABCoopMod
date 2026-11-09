# 02 — 核心技术架构设计 (Technical Architecture Design)

## 1. 架构选型总览：为什么采用“确定性帧同步 + 原生存档快照”混合架构？

《亿万僵尸》（TAB）在生存模式后期同屏拥有 **10,000 ~ 25,000 个活跃僵尸实体（`ZX.Entities.Zombie`）** 以及数千个建筑、投射物（`Proyectile`）和地形格子状态。
- **纯状态同步（State Synchronization）不可行**：若每秒广播 20,000 个实体的位置（`PointF`）、朝向（`Angle`）、生命值（`CLife.Life`）与物理速度（`Velocity`），每秒产生的数据量高达 `20,000 * 24 bytes * 15 Hz ≈ 7.2 MB/s`，远超 Steam P2P 转发带宽上限。
- **纯指令帧同步（Pure Lockstep）痛点**：虽然每秒仅需同步几百字节的玩家指令（`ZXCommand`），但要求所有客户端每帧逻辑计算 100% 确定，且无法支持中途加入或 Desync 后的自愈。

### 💡 最终确定方案：**Deterministic Lockstep（确定性指令帧同步） + Native Save-Stream Snapshot（原生存档流秒级同步）**

```text
+-----------------------------------------------------------------------------------+
|                              TABCoopMod 运行时架构                                 |
+-----------------------------------------------------------------------------------+
|                                                                                   |
|  [注入引导层 Bootstrap]                                                            |
|   winmm.dll / Doorstop -> HarmonyX (0Harmony.dll) -> TABCoopMod.Entry.Initialize()|
|                                                                                   |
|  [核心桥接层 Core / GameBridge]                                                    |
|   封装 #=z4RevDP3eECXqXS6JRA== (ZXGame), #=zxRcpu6e7NYzT7tGWqPjpOkc= (LevelSys)   |
|   统一提供强类型 API，隔离全部 Eazfuscator 混淆符号                                 |
|                                                                                   |
|  +-------------------------------+  +------------------------------------------+  |
|  | 确定性沙盒层 (Determinism)     |  | 指令拦截与回放层 (Command Sync)           |  |
|  | - 开启 DXGame.IsIADeterministic|  | - 拦截右键移动/攻击/交互                 |  |
|  | - 接管 LoadPhysics 异步线程    |  | - 拦截 Build.#=zBTwSMLo= 建筑放置        |  |
|  | - 隔离 DXGuid (Synced vs UI)  |  | - 拦截 ZXCommand.#=zfmgzkuw= 生产/科技   |  |
|  | - 固定 DXRandom 梅森旋转种子   |  | - 脱离 CSelectable 的独立实体集命令回放   |  |
|  +---------------+---------------+  +--------------------+---------------------+  |
|                  |                                       |                        |
|                  v                                       v                        |
|  +-----------------------------------------------------------------------------+  |
|  | 帧同步步进调度器 (LockstepEngine) & 快照同步器 (SnapshotSyncManager)          |  |
|  | - 30Hz 逻辑帧步进 (每帧固定 GameTime += 33.333333333333336 ms)               |  |
|  | - Turn Window (默认每 2 Ticks = 66ms 交换一次全员输入批次 TickInputBatch)    |  |
|  | - 暂停态控制帧 (Paused Control Tick): 暂停时照常同步建造、撤回与移动路径      |  |
|  | - 内存存档流直传: 复用 ZipSerializer (.zxsav) 实现开局同步与一键热重连       |  |
|  +---------------------------------------+-------------------------------------+  |
|                                          |                                        |
|                                          v                                        |
|  +-----------------------------------------------------------------------------+  |
|  | 双模传输抽象层 (INetworkTransport)                                           |  |
|  | - SteamP2PTransport: SteamMatchmaking (Lobby) + SteamNetworking (P2P Relay) |  |
|  | - LocalTcpLoopbackTransport: 127.0.0.1:27099 (供 AI Agent 单机双开自动化测试)|  |
|  +-----------------------------------------------------------------------------+  |
+-----------------------------------------------------------------------------------+
```

---

## 2. 注入与启动引导层设计（Bootstrap & Injection）

### 2.1 为什么不修改 `TheyAreBillions.exe`？
如 `00_AGENT_DEVELOPMENT_GUIDE.md` 所述，`TheyAreBillions.exe` 内置了 Eazfuscator 的完整性与调用栈深度检查。此外，主程序的 `<Module>..cctor` 注册了 `AppDomain.CurrentDomain.AssemblyResolve`，在首次访问 `DXVision`、`DXPlatform_Desktop`、`Steamworks.NET` 时，从加密的 ManifestResource 中实时解压并加载到内存。

### 2.2 推荐注入方案：Doorstop 4 / 代理 DLL 引导 + HarmonyX
1. 使用轻量级原生代理 DLL（如 `winhttp.dll` 或 `version.dll` 放置在游戏根目录，与 `BepInEx` / `UnityDoorstop` 原理相同）或在测试期使用 `NetLauncher` 进程引导器：
   - 宿主启动 `TheyAreBillions.exe` 进程，等待主模块加载并完成 `<Module>..cctor` 初始化后，调用 `Assembly.LoadFrom("TABCoopMod.dll")` 并执行 `TABCoopMod.Bootstrap.Init()`。
   - **补充更稳妥的纯托管引导方案（AppDomain Manager Injection）**：
     由于 `TheyAreBillions.exe` 是标准 `.NET Framework 4.6` 托管程序且带有 `TheyAreBillions.exe.config`（不参与 Eazfuscator 对 `.exe` 字节的校验！），我们只需在 `TheyAreBillions.exe.config`（或环境变量 `APPDOMAIN_MANAGER_ASM` / `APPDOMAIN_MANAGER_TYPE`）中配置自定义的 `AppDomainManager`！
     - **优势**：100% 原生 .NET 机制，无需任何 C++ 注入器或第三方代理 DLL，不会触发任何杀毒软件报警，且在 `ZX.Program.Main` 执行前的第一时刻即可挂载 `Harmony` 补丁！
     - **注意**：在 `AppDomainManager.InitializeNewDomain` 中，先让 `TheyAreBillions.exe` 的 `<Module>..cctor` 运行以注册 `AssemblyResolve`，然后再执行 `Harmony.PatchAll()`。

---

## 3. 确定性沙盒改造方案（Deterministic Sandbox）

通过反编译 `DXVision.dll`，我们精确掌握了游戏主循环与物理循环的源码逻辑，必须对以下 4 个关键点进行接管：

### 3.1 开启引擎内置确定性标志 `DXGame.IsIADeterministic`
在 `DXVision.DXGame`（`DXGame_Engine.cs:1023`）中：
```csharp
public static bool IsIADeterministic = false;
```
当我们在 Mod 初始化时设置 `DXGame.IsIADeterministic = true`：
1. 在 `UpdateGameLogic()`（`DXGame_Engine.cs:2779`）中，引擎不再使用 `DXHelper_Task.For` 多线程乱序更新实体，而是走：
   ```csharp
   for (int j = 0; j < _Entities.Count; j++) {
       DXEntity dXEntity = _Entities[j];
       if (!dXEntity.Disposed && !dXEntity.FreeingToPool) dXEntity.Update();
   }
   ```
2. 在 `SetCamera()`（`DXGame_Engine.cs:2079`）中，每次 `UpdateGameLogic()` 完成后，引擎会紧接着在同一线程同步调用 `UpdatePhysics()`。

### 3.2 接管异步计时器线程 `DXGame.LoadPhysics()`
**原版隐患**：在 `DXGame.LoadPhysics()`（`DXGame_Engine.cs:2844`）和 `LoadPhysicsOld()` 中，引擎即使在 `IsIADeterministic = true` 时，依然启动了后台线程 `_TaskGameTime` 和 `_ThreadPhysic` 来异步累加 `GameTime += num5 * num` 并调用 `DXPhysicWorld.UpdateDefaultTime()`。这会导致两台电脑因帧率/CPU 调度不同，每帧推进的 `GameTime` 不同！
**接管方案**：
- 使用 Harmony `Prefix` 拦截 `DXGame.LoadPhysics()` 和 `DXGame.LoadPhysicsOld()`，在联机模式下直接返回 `false`（阻止后台物理线程和计时线程启动）。
- 由我们的 `LockstepEngine` 在主线程 `DXGame.SetCamera()` 调用 `UpdateGameLogic()` 之前，精确控制 `GameTime` 的步进：
  - 每个逻辑 Tick 固定令 `DXGame.Current.GameTime = DXGame.LastGameTimeIAUpdate + 33.333333333333336`。
  - 调用 `WaitForGameLogic()` 确保单步逻辑在当前帧内同步执行完毕，使 `IAIterationInt` 与我们的 `LockstepTick` 严格 1:1 锁定！

### 3.3 实体唯一 ID 生成器 `DXGuid.New()` 双轨分流
**原版隐患**：`DXVision.DXGuid.New()`（`DXGuid.cs:15`）使用 `private static Random _WindowsRandom = new Random();` 生成 `ulong` ID。且不仅创建单位/建筑会调它，连本地 UI（如 `OMiniMap` 从对象池拿提示图标、本地鼠标点击特效）也会调它。
**接管方案**：
- 使用 Harmony `Prefix` 完全替换 `DXGuid.New()`：
  ```csharp
  public static bool Prefix(ref ulong __result) {
      if (!LockstepEngine.IsMultiplayerActive) return true;
      // 仅当处于确定性逻辑 Tick 更新或网络同步命令执行上下文时，使用同步计数器
      if (LockstepEngine.InDeterministicContext) {
          __result = LockstepEngine.NextSyncedGuid();
      } else {
          // 本地 UI / 渲染帧特效对象，分配高位标记的本地 ID，绝不消耗同步计数器！
          __result = 0x8000000000000000UL | Interlocked.Increment(ref _localUiGuidCounter);
      }
      return false;
  }
  ```

### 3.4 伪随机数生成器 `DXRandom` 种子同步
**原版隐患**：
- `DXRandom.Global` 是 `[ThreadStatic]`，首次访问时用 `new Random().Next()` 随机种子初始化（`DXRandom.cs:44`）。
- 无参构造函数 `new DXRandom()` 使用 `ThreadId * Ticks` 初始化（`DXRandom.cs:66`）。
**接管方案**：
- Hook `DXRandom` 无参构造函数与 `DXRandom.get_Global`：在联机模式下，使用基于当前 `MapSeed ^ LockstepTick` 派生的确定性梅森旋转实例，并在每次快照加载后重置状态数组 `Mt`（`uint[624]`）。

---

## 4. 指令拦截与无状态回放系统（Command Interception & Replay）

### 4.1 玩家全操作入口梳理與拦截点
经过对 `ZXSystemGameLevel` (`#=zxRcpu6e7NYzT7tGWqPjpOkc=`) 与 `ZXCommand` 的完整反编译，玩家在局内的所有修改世界状态的操作只有以下 **7 类**，我们只需在这 7 个入口做 Harmony 拦截：

| 序号 | 操作类型 | 原版调用入口（混淆符号见字典 03） | 拦截与网络化处理方式 |
| :---: | :--- | :--- | :--- |
| **1** | **建造建筑** | `ZX.Commands.Build.#=zBTwSMLo=` | 当本地玩家放置建筑时，不直接生成建筑，发送 `CmdPlaceBuilding(BuilderEntityId, TargetRect, EntityTypeName, IsBonus, InstantBuild)`；收到网络包后调用原函数。 |
| **2** | **群体移动/巡逻/散开** | `ZXSystemGameLevel.#=zgn6bDkSd9MGS3$WvAQ==` | 当本地玩家右键地面/小地图或点击移动/巡逻/投掷手雷到点时，捕获传入的 `List<DXEntity>` 的 ID 数组、`Command.ID` 与目标 `PointF`，发送 `CmdGroupMove`；回放时根据 ID 列表还原 `List<DXEntity>` 调用原函数并触发各实体的 `cmd.#=zfmgzkuw=`。 |
| **3** | **单体/群体通用命令** (攻击目标、训练单位、科技升级、修理、拆除、市场买卖、老兵/攻击优先级切换、下车) | `ZX.Commands.ZXCommand.#=zfmgzkuw=(ZXEntity, ZXCommandTarget)` | 区分调用源：若来自玩家本地 UI/右键交互（非命令队列自动流转 `#=zrz2EI1A=`，也非 AI 行为树内部调用），拦截并打包 `(ExecutorId, CommandId, TargetEntityId, TargetPos, TargetSize, IsShiftPressed)`。 |
| **4** | **取消当前正在执行的命令/建造** | `CCommandable.#=zWGiN6nrTv8Dp()` | 拦截取消按钮点击/快捷键，发送 `CmdCancelCurrent(EntityId)`。 |
| **5** | **取消生产队列中的槽位** | `CCommandable.#=z8g7GC7xLrr8j(int slot)` | 拦截点击生产队列头像取消，发送 `CmdCancelQueueSlot(EntityId, SlotIndex)`。 |
| **6** | **暂停 / 游戏速度切换** | `DXGame.set_Paused` / `set_GameSpeed` | 拦截本地按键（空格 / `+` / `-`）与 UI 暂停按钮，发送 `CmdSetPaused(bool)`，在统一的 `LockstepTick` 生效。 |
| **7** | **市长选举二选一** | `ZXGame.#=zcITiOSD6YNmD` 内匿名委托 | 拦截市长窗口左/右按钮点击事件，发送 `CmdSelectMayor(int optionIndex)`（0=男市长，1=女市长）。 |

### 4.2 关键技术点：如何防止 `Shift` 键状态不一致？
看 `ZXCommand.#=zfmgzkuw=` 的第 1 行代码（`ZXCommand.cs:376`）：
```csharp
public void #=zfmgzkuw=(ZXEntity #=zCb8SJ4S0dwZU, ZXCommandTarget #=zQRhD0TA=)
{
    DXInputState.Current.IsKeyPressed((DXKeys)8, false);
```
且在 `ZXSystemGameLevel` 中判断连续放置建筑或排队命令时，会读取 `DXInputState.Current.IsKeyPressed((DXKeys)65536, false)`（即 `Shift` 键）。
- **风险**：如果玩家 A 按住 Shift 点命令，而玩家 B 没按 Shift，在玩家 B 的机器上回放该命令时读取了玩家 B 本地的 `DXInputState` 就会出错！
- **解决方案**：在网络指令包中显式携带 `bool ModifierShift` 标志。当 `CommandExecutor` 在各客户端回放某条网络指令时，临时将 `DXInputState.Current` 的 Shift 状态覆盖为指令包中的 `ModifierShift`，执行完毕后立即恢复本地真实按键状态！

### 4.3 关键技术点：暂停期间的“实时建筑放置与路径预览”
在《亿万僵尸》中，90% 的建造和部队分兵是在**暂停状态（`Paused = true`）**下完成的。
Look at `Build.#=zBTwSMLo=` (`Build.cs:199`):
```csharp
DXGame.Current.InvokeOnStartFrame((Action)(() => {
    ((DXEntity)zXEntity).AddToScene((DXObject)null);
    ...
}));
```
因为 `InvokeOnStartFrame` 是在**渲染帧（`OnRender -> PostMethods_OnStartFrame.ExecuteAll()`）**执行的，即使游戏逻辑暂停（`Paused = true`），渲染帧依然以 60FPS 运行！
因此：
- 我们的 `LockstepEngine` 将 Tick 分为两层：
  1. **Network Control Tick（网络控制帧，恒定 30Hz）**：无论游戏是否暂停，只要全员网络连通，每 33ms 交换一次指令包并执行 `CommandExecutor`。因此**暂停期间队友放下的房子、拉的木墙、点的兵营训练队列、拉的部队移动线，会在所有人的屏幕上立即显示出来！**
  2. **Simulation Logic Tick（世界模拟帧，非暂停时与 Control Tick 同步推进）**：仅当 `SyncedPaused == false` 时，每个 Control Tick 同时推进 `GameTime += 33.333333333333336` 并驱动单位移动、战斗与物理更新。

---

## 5. 内存存档流同步架构（Zero-MapGen Save Sync）

通过反编译 `ZXGame.cs`（`1286~1604` 行），我们摸清了 TAB 存档读写的全部机制：
- **写存档核心函数**：`ZXGame.#=zMtGuEM2lBSlZ5BGWvg==(string filePath)`
  - 内部调用 `#=zpKDARrtdO1GK7shdnQ==` 设置 `ZipSerializer.Current.Password`，然后调用 `ZipSerializer.Write` 写出 `.zxsav` 文件，最后调用 `#=zv3_eRJTVn6$5` 写出 `.zxcheck` 校验文件。
- **读存档核心函数**：`ZXGame.#=z9IpK6P4YlW8GIY7olg==(string filePath)`
  - 内部校验 `.zxcheck`，调用 `#=z3YFOTVtBw_rT7oSQRA==` 反序列化出 `ZXGameState`，重建 `ZXLevelState`、实体字典 `LevelEntities` 与物理世界，并调用 `ZXLevelState.#=zF1YHTRUZSNRa()` 恢复所有计时器变量！

### 快照同步工作流：
1. **房主打包（Host Snapshot Export）**：
   - 房主进入新地图（或触发 Resync）时，锁定 `DXGame.Current.Paused = true`。
   - 调用 `ZXLevelState.#=zuartwoo=().#=zQXHqcVh9mGZZ()` 保存当前精确时间参数，然后调用 `#=zMtGuEM2lBSlZ5BGWvg==(tempSavePath)` 生成 `coop_sync_temp.zxsav` 与 `coop_sync_temp.zxcheck`。
   - 读取这两个文件的字节数组（通常共 `1.5 MB ~ 3.5 MB`），计算 SHA256，切分为 `32 KB` 的 `SaveChunkPacket` 广播给所有客机。
2. **客机加载（Client Snapshot Import）**：
   - 客机接收全部分片并重组为本地 `Saves/coop_client_sync.zxsav` 和 `Saves/coop_client_sync.zxcheck`。
   - 客机在主线程调用 `ZXGame.#=zuartwoo=().#=z9IpK6P4YlW8GIY7olg==(clientSyncSavePath)`。
   - 客机加载完成回调触发后，重置本地 `LockstepEngine.SyncedGuidCounter` 和 `DXRandom` 状态，向房主发送 `ClientSyncReadyPacket(StateHash)`。
3. **全员就绪（All Ready）**：
   - 房主收到所有客机的 `ClientSyncReadyPacket` 且校验 `StateHash` 一致后，广播 `StartLockstepPacket`，正式开始协同作战！
