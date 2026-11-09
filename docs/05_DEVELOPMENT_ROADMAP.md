# 05 — 面向 AI Agent 的分阶段开发计划与验收标准 (Development Roadmap & WBS)

> **工程原则**：每个 Phase 拆解为颗粒度适中、边界清晰的原子任务（Task），每个 Task 均定义明确的**目标文件**与**可由 Agent 自动运行的验证门禁（Verification Gate）**，支持多 Agent 接力或并行开发。

---

## 阶段总览 (Milestone Overview)

| 阶段 | 名称 | 核心交付目标 | 状态 |
| :---: | :--- | :--- | :---: |
| **Phase 0** | **工具链就位与注入破冰** | 提取内嵌 SDK，搭建 `TABCoopMod.csproj`，实现免改 EXE 注入、Harmony 挂载、`GameBridge` 封装层与主菜单【联机按钮】显示。 | ✅ 已完成 (100% 实机验证) |
| **Phase 1** | **确定性沙盒与单机指令录制回放** | 接管 `IsIADeterministic`、`LoadPhysics`、`DXGuid`、`DXRandom`；完成 7 大类玩家指令拦截与脱离 `CSelectable` 的回放执行器；通过单机录制回放 `StateHash` 100% 一致性测试。 | 🔄 进行中 (85%，见 `docs/09` P0) |
| **Phase 2** | **双模网络层与内存存档流秒传** | 实现 `LocalTcpLoopbackTransport` 与 `SteamP2PTransport`；完成原生 UI 联机大厅；跑通房主建图 -> `.zxsav` 内存流分片传输 -> 客机自动加载进入同一局游戏。 | 🔄 骨架已落地 (60%，待双端联调) |
| **Phase 3** | **执政官模式完整联调与协同 UX** | 跑通 2-3 人 Lockstep 实时共控（含暂停态实时建房/拉兵、市长选择同步、建筑抢地冲突消解）；完成盟友鼠标光标、小地图视野框、战术 Ping 与自动 Resync 热同步。 | 🔄 进行中 (35%，见 `docs/09` P2/P3) |
| **Phase 4** | **独立基地模式拓展（第二阶段）** | 开局多指挥中心生成、Mod 层虚拟阵营资源账本隔离、单位所有权锁定与多路尸潮分流。 | ⏳ 远期规划 |

---

## Phase 0：工具链就位与注入破冰 (Bootstrap & SDK Foundation)

### Task 0.1：自动化 SDK 提取与反编译辅助脚本
- **产出文件**：
  - `scripts/extract_sdk.ps1`
  - `scripts/decompile_type.ps1`
  - `lib/DXVision.dll`, `lib/DXPlatform_Desktop.dll`, `lib/Steamworks.NET.dll`, `lib/SmartPropertyGrid.dll`
- **实现要求**：
  - 通过 PowerShell 加载 `TheyAreBillions.exe` 并运行 `<Module>..cctor`，利用其内部解密器提取出 4 个内嵌程序集保存到 `lib/`。
- **Agent 验收标准**：
  - 运行 `scripts/extract_sdk.ps1` 后，`lib/DXVision.dll` 等文件存在且字节数非零；运行 `scripts/decompile_type.ps1 -Assembly "DXVision.dll" -TypeName "DXVision.DXGame"` 正常输出 C# 源码。

### Task 0.2：创建 `TABCoopMod` C# 工程与 `IgnoresAccessChecksTo` 配置
- **产出文件**：
  - `src/TABCoopMod/TABCoopMod.csproj`
  - `src/TABCoopMod/Properties/IgnoresAccessChecksToAttribute.cs`
  - `src/TABCoopMod/Core/ModLogger.cs`
- **实现要求**：
  - 目标框架 `<TargetFramework>net46</TargetFramework>`，平台 `<PlatformTarget>x64</PlatformTarget>`，开启 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`。
  - 引入 NuGet 包 `Lib.Harmony` (v2.3.x) 和 `BepInEx.AssemblyPublicizer.MSBuild`（或手写 `IgnoresAccessChecksTo("TheyAreBillions")` 与 `IgnoresAccessChecksTo("DXVision")`）。
  - 引用 `lib/DXVision.dll`、`lib/Steamworks.NET.dll` 及游戏目录的 `TheyAreBillions.exe`（均设为 `<Private>False</Private>`）。
- **Agent 验收标准**：
  - 执行 `dotnet build src/TABCoopMod/TABCoopMod.csproj -c Release` 零错误编译生成 `TABCoopMod.dll` 和 `0Harmony.dll`。

### Task 0.3：实现 `GameBridge` 与 `ObfuscatedSymbols` 隔离封装层
- **产出文件**：
  - `src/TABCoopMod/Core/ObfuscatedSymbols.cs`
  - `src/TABCoopMod/Core/GameBridge.cs`
- **实现要求**：
  - 严格对照 `docs/03_REVERSE_ENGINEERING_MAP.md`，用强类型委托（`AccessTools.MethodDelegate` / `AccessTools.FieldRefAccess`）封装 `ZXGame`、`ZXSystemGameLevel`、`ZXSystemMainMenu`、`ZXLevelState`、`ZXCommand`、`Build`、`CCommandable`、`CSelectable`、`ZXMessageBoxHelper` 的核心方法与字段。
  - 提供启动自检函数 `GameBridge.ValidateBindings()`：在游戏启动时遍历检查所有反射绑定是否非空，并将结果写入 `TABCoopMod.log`。
- **Agent 验收标准**：
  - 编译通过，且在注入游戏后 `GameBridge.ValidateBindings()` 输出 `ALL BINDINGS VERIFIED (0 missing)`。

### Task 0.4：运行时无损注入引导与主菜单【CO-OP MULTIPLAYER】按钮
- **产出文件**：
  - `src/TABCoopMod/Bootstrap/ModAppDomainManager.cs`（及部署脚本 `scripts/install_mod.ps1` / `scripts/uninstall_mod.ps1`）
  - `src/TABCoopMod/Patches/MainMenuPatch.cs`
- **实现要求**：
  - 实现基于 `.NET AppDomainManager`（通过备份并配置 `TheyAreBillions.exe.config`）或 Doorstop 的纯内存无损引导，**绝对不触碰 `TheyAreBillions.exe` 本身**。
  - 在 `MainMenuPatch` 中 Hook `ZXSystemMainMenu.OnLoad`（`#=zLs$VX1eoAapVflmW_w==.OnLoad`），在主菜单按钮列表注入 `"CO-OP MULTIPLAYER (联机模式)"` 按钮，点击后调用 `GameBridge.ShowMessageBox("TAB Co-op Mod", "Multiplayer Engine Initialized!", null)`。
- **Agent 验收标准**：
  - 运行 `scripts/install_mod.ps1` 并启动游戏进程，检查生成的 `TABCoopMod.log` 确认：
    1. Mod 成功加载且 `ValidateBindings()` 全部通过；
    2. `MainMenuPatch.OnLoad_Postfix` 成功触发并注入主菜单按钮；
    3. 游戏正常渲染主菜单无崩溃。

---

## Phase 1：确定性沙盒与单机指令录制回放 (Deterministic Sandbox & Command Replay)

### Task 1.1：确定性时钟、物理、GUID 与 RNG 补丁
- **产出文件**：
  - `src/TABCoopMod/Determinism/LockstepClock.cs`
  - `src/TABCoopMod/Determinism/DeterministicGuidAndRngPatch.cs`
  - `src/TABCoopMod/Determinism/StateHasher.cs`
- **实现要求**：
  - 开启 `DXGame.IsIADeterministic = true`。
  - Hook `DXGame.LoadPhysics` / `LoadPhysicsOld` 阻止异步计时线程，由 `LockstepClock` 在 `DXGame.SetCamera` 中按固定步长 `33.333333333333336ms` 推进 `GameTime`。
  - Hook `DXGuid.New()`：区分 `InDeterministicContext`（返回同步递增 ID）与本地 UI 线程（返回 `0x8000000000000000UL | localCounter`）。
  - 实现 `StateHasher.ComputeLevelStateHash()`（按 `04_NETWORK_PROTOCOL.md` 第 4 节规范）。

### Task 1.2：7 大类玩家指令拦截器 (`CommandInterceptor`)
- **产出文件**：
  - `src/TABCoopMod/Commands/NetCommand.cs`
  - `src/TABCoopMod/Patches/CommandInterceptorPatch.cs`
- **实现要求**：
  - 拦截玩家本地触发的：建筑放置（`Build.#=zBTwSMLo=`）、群体移动（`#=zgn6bDkSd9MGS3$WvAQ==`）、通用命令执行（`ZXCommand.#=zfmgzkuw=`）、取消当前命令（`#=zWGiN6nrTv8Dp`）、取消队列槽位（`#=z8g7GC7xLrr8j`）、暂停/倍速切换、市长选择。
  - 注意设置重入保护标志 `bool IsReplayingCommand`，防止回放执行时被再次拦截造成死循环，并防止单位 AI 内部自动触发的子命令（如士兵自动反击、生产队列自动跳下一项 `#=zrz2EI1A=`）被误当作玩家输入拦截。

### Task 1.3：脱离 `CSelectable` 的无状态指令回放器 (`CommandExecutor`) 与单机回放验证
- **产出文件**：
  - `src/TABCoopMod/Commands/CommandExecutor.cs`
  - `src/TABCoopMod/Testing/DeterministicReplayVerifier.cs`
- **实现要求**：
  - `CommandExecutor.Execute(NetCommand cmd)` 根据指令内的 `ulong[] EntityIds` 查表获取 `ZXEntity` 并执行操作，**全程不读取也不修改 `CSelectable.#=zIiHCgn_odqH7`**，且正确模拟指令携带的 `IsShiftQueued` 状态。
- **Agent 验收标准（关键门禁）**：
  - 使用 `DeterministicReplayVerifier`：加载同一测试存档两次，在固定 Tick 序列（如 Tick 10 造帐篷、Tick 30 派 4 个游侠去坐标 X,Y、Tick 60 兵营造兵）注入相同指令列表，运行到 Tick 600，验证两次运行的 `StateHash` 序列 **100% 完全相等**！

---

## Phase 2：双模网络层与内存存档流秒传 (Networking & Zero-MapGen Save Sync)

### Task 2.1：双模网络传输层 (`LocalTcpLoopbackTransport` & `SteamP2PTransport`)
- **产出文件**：
  - `src/TABCoopMod/Networking/INetworkTransport.cs`
  - `src/TABCoopMod/Networking/PacketSerializer.cs`
  - `src/TABCoopMod/Networking/LocalTcpLoopbackTransport.cs`
  - `src/TABCoopMod/Networking/SteamP2PTransport.cs`
- **实现要求**：
  - 实现 `04_NETWORK_PROTOCOL.md` 定义的全部封包序列化与反序列化。
  - 实现本机 TCP 环回传输（支持 `--coop-host` 与 `--coop-client` 命令行参数自动连接）。
  - 实现基于 `SteamMatchmaking` + `SteamNetworking` 的 Steam P2P 传输，并在每渲染帧驱动 `SteamAPI.RunCallbacks()`。

### Task 2.2：内存存档流导出、分片传输与客机热加载 (`SnapshotSyncManager`)
- **产出文件**：
  - `src/TABCoopMod/Sync/SnapshotSyncManager.cs`
  - `src/TABCoopMod/UI/CoopLobbyWindow.cs`
- **实现要求**：
  - 使用游戏原生 `ZXWindow`、`DXListView`、`ZXButtonSteam` 构建主菜单联机大厅窗口（支持创建 Steam 房间、邀请好友、开启 Loopback 测试）。
  - 当房主进入关卡（`ZXSystemGameLevel.#=zmTU4kueQctVr` 完成）时，自动调用 `ZXGame.#=zMtGuEM2lBSlZ5BGWvg==` 导出临时 `.zxsav` + `.zxcheck`，按 32KB 分片发送给客机。
  - 客机收齐分片并校验后，调用 `ZXGame.#=z9IpK6P4YlW8GIY7olg==` 加载进入关卡，上报初始 `StateHash`。
- **Agent 验收标准**：
  - 双开或联机测试：房主新建生存游戏，客机自动接收 `.zxsav` 快照流并在 5 秒内进入同一张地图，双方日志打印的 `Tick 0 StateHash` 完全相等。

---

## Phase 3：执政官模式完整联调与协同 UX (Archon Mode Full Loop & Co-op UX)

### Task 3.1：Lockstep 回合调度器与暂停态控制帧 (`LockstepSessionManager`)
- **产出文件**：
  - `src/TABCoopMod/Sync/LockstepSessionManager.cs`
- **实现要求**：
  - 实现 `ClientInputReport -> Host TurnInputBroadcast -> All Peers Execute` 的 30Hz 步进闭环。
  - 支持**暂停态实时同步**：当 `SyncedPaused == true` 时，`ControlTick` 照常运行，玩家在暂停时放下的建筑、下达的移动指令立即在所有客户端执行并渲染，但 `SimTick`（`GameTime`）保持静止。
  - 处理同帧同位置建造冲突：在 `Build.#=zBTwSMLo=` 前再次检查目标区域格子可用性，若已被同帧前序指令占用，则安全跳过并免扣费。

### Task 3.2：市长选择同步、盟友光标/视野框渲染与战术信号 Ping
- **产出文件**：
  - `src/TABCoopMod/Patches/MayorSyncPatch.cs`
  - `src/TABCoopMod/UI/AllyOverlayRenderer.cs`
  - `src/TABCoopMod/UI/MinimapCoopPatch.cs`
- **实现要求**：
  - 拦截市长弹窗选择事件，支持客机点击高亮推荐、房主点击最终拍板并同步关闭全员弹窗。
  - 通过 Unreliable 通道（20Hz）同步鼠标世界坐标与 `DXScene.Current.CameraArea`：
    - 在 `OMiniMap` 上绘制队友代表色的视野矩形框；
    - 在主场景绘制队友的彩色鼠标光标与玩家名；
    - 支持 `Alt + 左键` 触发全员 `OMiniMap.#=zvr$ku1Q=` 雷达闪烁打点。

### Task 3.3：周期性状态校验与一键热同步自愈 (`AutoResyncController`)
- **产出文件**：
  - `src/TABCoopMod/Sync/AutoResyncController.cs`
- **实现要求**：
  - 每 60 个 `SimTick` 自动比对全员 `StateHash`。
  - 若出现不一致（或房主按下 `F10` 手动触发同步），自动暂停并复用 `SnapshotSyncManager` 将房主最新内存快照推送给客机，3 秒内恢复完全一致状态继续游戏。
- **Agent 验收标准**：
  - 双端联机连续运行 10 分钟以上（涵盖建造、产兵、多线拉扯清图、市长选举、手动触发热同步），全程零崩溃，Resync 后 `StateHash` 恢复 100% 一致。
