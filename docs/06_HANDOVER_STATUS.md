# 06 — 阶段性交接文档 (Handover Document for Claude Code)

> **交接时间**: 2026-11-09
> **当前完成度**: Phase 0 (100% 完成并实机验证通过) + Phase 1/2/3 核心骨架代码已落地并编译通过
> **接手 Agent**: Claude Code (请先阅读根目录 `CLAUDE.md` 与 `.claude/skills/`)

---

## 1. 已完成且经过实机验证的成果 (What is Done & Verified)

### 1.1 逆向分析与内嵌 SDK 提取（100% 完成）
- 已编写并验证 `scripts/extract_sdk.ps1`，从 `TheyAreBillions.exe` 的加密 ManifestResource 中提取出 **100% 未混淆**的 `DXVision.dll`（1.23MB 自研引擎）、`DXPlatform_Desktop.dll`、`Steamworks.NET.dll`（v10.0.0.0）与 `SmartPropertyGrid.dll`，存放在 `lib/` 目录。
- 已编写并验证 `scripts/decompile_type.ps1`，内部直接使用已缓存的 `ilspycmd.exe`，可在 1 秒内反编译 `TheyAreBillions.exe` 或 `DXVision.dll` 中的任意类。

### 1.2 免修改 EXE 的无损注入引导（100% 实机跑通，重大突破！）
- **严禁直接修改 `TheyAreBillions.exe`**（内置 Eazfuscator 元数据 Token 与调用栈深度校验）。
- 已实现并实机验证 `src/TABCoopMod/Bootstrap/ModAppDomainManager.cs`：通过 `TheyAreBillions.exe.config` 配置 `.NET AppDomainManager`，在 CLR 启动时自动加载 `TABCoopMod.dll`。
- **已解决的两个实机深坑（Claude Code 切勿回退此逻辑）**：
  1. **加载时机坑**：在 `ModAppDomainManager.InitializeNewDomain` 执行时，主程序 `TheyAreBillions.exe` 尚未加载，如果在此时直接访问 `typeof(ZX.Program)` 会导致 CLR 报 `0x8000FFFF (E_UNEXPECTED)` 崩溃。**现已通过订阅 `AppDomain.CurrentDomain.AssemblyLoad` 事件，在 `TheyAreBillions` 刚加载完的第一时间触发 `RuntimeHelpers.RunModuleConstructor` 和 `Harmony.PatchAll()`，实机测试 100% 稳定！**
  2. **跨程序集 `internal` 访问坑**：在 `.NET Framework 4.6` Desktop CLR 下，`IgnoresAccessChecksToAttribute` 只在编译期生效（由 `Krafs.Publicizer` 提供编译期可见性），但运行时 JIT 仍会拦截对 `internal` 方法的直接 IL 调用或泛型委托（抛 `MethodAccessException`）。因此：
     - 在 `GameBridge.cs` 中，跨程序集调用 `internal` 方法统一使用缓存的 `MethodInfo.Invoke`；
     - 在 `MainMenuPatch.cs` 中，实例化 `internal sealed class ZXButtonLink` 使用 `Activator.CreateInstance(..., BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, ...)`。
  3. **Steam 重启拦截坑**：在 `scripts/install_mod.ps1` 中已自动在游戏目录写入 `steam_appid.txt` (`644930`)，防止直接启动 `TheyAreBillions.exe` 时被 `SteamAPI.RestartAppIfNecessary` 秒退重启。

### 1.3 主菜单【CO-OP MULTIPLAYER】按钮与核心模块落地
- 实机启动 `TheyAreBillions.exe` 验证日志（见游戏目录 `TABCoopMod.log`）：
  - `GameBridge.InitializeAndValidate()` 全部 27 项核心混淆类、方法、字段绑定 **100% 验证通过（0 missing）**。
  - `MainMenuPatch.OnLoad_Postfix` 成功在主菜单注入原版样式的 **`CO-OP MULTIPLAYER`** 按钮！
- 已完成编写的核心源码模块：
  - `src/TABCoopMod/Core/ObfuscatedSymbols.cs` & `GameBridge.cs`：完整混淆符号字典与桥接层。
  - `src/TABCoopMod/Determinism/LockstepClock.cs` & `StateHasher.cs`：开启 `DXGame.IsIADeterministic = true`、双轨 `DXGuid.New()` 分流补丁、`DXRandom.Global` 固定种子补丁、FNV-1a 64位全图状态校验和计算器。
  - `src/TABCoopMod/Commands/NetCommand.cs` & `Patches/CommandInterceptorPatch.cs`：脱离本地 `CSelectable` 的指令序列化/回放器与 5 大 Harmony 拦截补丁（含 `Shift` 键状态模拟与生产队列自推进过滤）。
  - `src/TABCoopMod/Networking/INetworkTransport.cs` & `SteamP2PTransport.cs`：支持 `127.0.0.1:27099` 单机双开测试的 `LocalTcpLoopbackTransport` 与支持 Steam 好友房间的 `SteamP2PTransport`。
  - `src/TABCoopMod/Sync/CoopSessionManager.cs`：包含 `.zxsav` 内存存档 32KB 分片流式同步、帧指令广播、60帧周期性 `StateHash` 对账与自动 Resync、20Hz 盟友光标/视野广播。
  - `src/TABCoopMod/UI/CoopLobbyWindow.cs` & `AllyOverlayRenderer.cs`：基于原版 `ZXWindowMessage` 列表选择器的联机大厅弹窗。

---

## 2. 下一步待 Claude Code 推进的任务清单 (Next Steps for Claude Code)

请按照以下优先级依次推进（详细设计见 `docs/02_ARCHITECTURE_DESIGN.md` 与 `docs/05_DEVELOPMENT_ROADMAP.md`）：

### Step 1：实机测试联机大厅 UI 与单机指令拦截回放闭环（Phase 1 收尾）
1. 运行 `scripts/install_mod.ps1` 编译并部署最新代码。
2. 在游戏内点击主菜单 **`CO-OP MULTIPLAYER`** 按钮，验证 `CoopLobbyWindow.ShowLobbyWindow()` 列表弹窗与各选项回调是否正常（注意检查是否有任何 `internal` 类型访问触发 `MethodAccessException`，若有，改用 `AccessTools` 反射调用）。
3. 进入一局生存模式，开启 `CommandInterceptor.IsInterceptionEnabled = true`，测试：
   - 框选游侠右键移动/攻击、小地图右键移动；
   - 放置帐篷/木墙、取消建造；
   - 兵营训练士兵、取消生产队列槽位；
   - 观察 `TABCoopMod.log`，确保玩家操作被准确拦截并通过 `CommandExecutor.Execute` 回放，且僵尸 AI 攻击和生产队列自动跳下一项不会被误拦截。

### Step 2：补全暂停同步、倍速同步与市长二选一同步补丁（Phase 2/3 核心）
1. **暂停与倍速拦截（`PauseSyncPatch.cs`）**：
   - 拦截 `ZXSystemGameLevel.OnKeyDown` 中的空格暂停（`ZXKeyboardCommand.Pause`）及右上角暂停按钮，改为生成 `NetCommandType.SetPaused` 发送给 `CoopSessionManager`。
2. **市长选举同步（`MayorSyncPatch.cs`）**：
   - 参考 `docs/03_REVERSE_ENGINEERING_MAP.md` 中 `ZXGame.#=zcITiOSD6YNmD()`（`ZXGame.cs:1659`）的反编译源码，Hook 市长弹窗中的左/右选择按钮，使房主或客机点击市长后生成 `NetCommandType.MayorVoteOrSelect` 同步应用市长加成并关闭全员弹窗。

### Step 3：完善小地图盟友视野框渲染与双端联调（Phase 3 收尾）
1. **小地图队友视野框（`MinimapCoopPatch.cs`）**：
   - Hook `ZX.GUI.OMiniMap`（反编译源码可运行 `scripts/decompile_type.ps1 -TypeName "ZX.GUI.OMiniMap"` 查看），在 `OMiniMap` 上为 `CoopSessionManager.AllyStates` 中的每位队友创建一个带颜色边框的 `DXObject` 矩形框，实时映射 `state.CameraWorldArea` 到小地图坐标（参考 `OMiniMap.#=zi91u4abSRmxU` 白色视野框的坐标转换公式）。
2. **地面队友鼠标光标渲染**：
   - 在 `ZXSystemGameLevel.OnStartFrame` Postfix 中，将 `AllyStates` 中的 `MouseWorldPosition` 渲染为彩色光标对象。
3. **本机 Loopback 或 Steam P2P 完整联调**：
   - 验证房主进图自动导出 `coop_host_snapshot.zxsav` -> 客机接收并调用 `LoadSaveFileAndEnter` 进入同一张地图 -> 双方同时操作不同部队与建造。

---

## 3. 未验证项清单（Unverified — 明确不要当成"已完成"）

以下全部是**代码已写、编译通过、但从未实机跑通**的部分。接手后请逐条验证，不要假设能用。

| # | 未验证项 | 风险 | 验证方式 |
|---|---|---|---|
| 1 | `CoopLobbyWindow.ShowLobbyWindow()` 的 `#=zYjeCpah9Jpv2S1i8lQ==` 反射调用签名是否匹配 | 高 — 签名猜错会静默走到 fallback 弹窗 | 点主菜单 CO-OP 按钮，看是否出现 5 项列表 |
| 2 | `CommandInterceptor` 的 5 个 Harmony 拦截点是否全部命中 | 高 — 拦截不到 = 指令不同步 | 单机模式 `IsInterceptionEnabled=true` 后操作，看日志是否打印 `[CommandExecutor]` |
| 3 | `CommandExecutor.ExecutePlaceBuilding` 里 `Build.#=zQ4lx63I=(Type)` 的真实签名 | 中 — 已改用 `AccessTools` 按名查找，但 `Method_BuildOrTrain_GetForType` 常量名需核对 | 单机放一个帐篷，看是否成功建造且资源扣减正确 |
| 4 | `#=zgn6bDkSd9MGS3$WvAQ==` 群体移动是否会 mutate 传入 List | 中 — 已做防御性 copy，但未验证散点分配确定性 | 双开框选 10 个单位右键同一位置，比对两端坐标哈希 |
| 5 | `DXGame.StartFrame` / `UpdateGameLogic` / `LoadPhysics` 的真实成员名 | 中 — `HarmonyPatch(typeof(DXGame), "StartFrame")` 若不存在则 patch 静默失败 | 启动日志看 Harmony 是否报 "Undefined target method" |
| 6 | 存档快照分片流在 3 人场景下的吞吐与顺序 | 中 — 32KB × N 全走 Reliable，大存档可能卡顿 | 双开进大图，观察 `Receiving Snapshot` 到 `LoadSaveFileAndEnter` 的耗时 |
| 7 | `DXScene.Current.CameraArea` / `OMiniMap` 世界坐标映射 | 低 — UI 层，错了只是显示问题 | 双开看队友视野框位置是否对得上 |
| 8 | `DXGame.Current.InvokeOnStartFrame` / `InvokeOnFinishFrame` 是否存在 | 中 — 若不存在，Snapshot 加载不会延迟到安全帧 | 客机收到快照后看是否崩溃 |
| 9 | Steam P2P 双账号真实联机（非 loopback） | 高 — 内嵌 Steamworks.NET v10 的 Lobby API 未实测 | 需要两个 Steam 账号 |
| 10 | `System.ValueTuple.dll` 运行时是否被游戏的 AssemblyResolve 正确解析 | 低 — 已引用，但 CLR 4.6 加载路径未验证 | 启动看有无 `FileNotFoundException` |

---

## 4. 本次会话末尾的构建 / 部署 / 启动验证状态

```
dotnet build src/TABCoopMod/TABCoopMod.csproj -c Release
→ 已成功生成。0 个警告，0 个错误
→ 产物 src/TABCoopMod/bin/Release/TABCoopMod.dll

scripts/install_mod.ps1
→ TABCoopMod installed successfully!

启动 TheyAreBillions.exe 后 TABCoopMod.log 实测输出：
→ [GameBridge] ALL BINDINGS VERIFIED SUCCESSFULLY (0 missing).
→ [Harmony] All Harmony patches applied. Classes OK=10, failed-tolerant=0.
→ [MainMenuPatch] Injected 'CO-OP MULTIPLAYER' button into TMenuCategory.Main successfully!
→ ERROR 计数 = 0
```

**结论：注入链、符号绑定、全部 10 个 Harmony 补丁类、主菜单按钮注入均已实机验证通过。**

修掉的关键 bug（不要回退）：
1. `OMiniMap.OnMouseRightDown` 含 Eazfuscator 生成异常块 → MonoMod IL 重写抛 `异常块的错误代码生成` → **曾导致 PatchAll 整体失败、Mod 完全不加载**。已从补丁目标中移除。
2. `Harmony.PatchAll(assembly)` 是一损俱损的：任何一类补丁失败就抛出并中断整个 Initialize。已改为**逐类 `PatchClassProcessor(...).Patch()` 包 try/catch**，单类失败只记 Warn。

Git 已提交：`4bbea80 Phase 0 完成 + Phase 1-3 骨架落地`

---

## 5. 已部署到游戏目录的残留文件（如要干净卸载）

```
TABCoopMod.dll / .pdb / .dll.old
0Harmony.dll
TheyAreBillions.exe.config   ← 已改，原文件备份为 .bak
steam_appid.txt              ← 内容 644930
TABCoopMod.log
```

用 `scripts/uninstall_mod.ps1` 一键清理。

---

## 6. 2026-11-09 第二轮（会话2）：自动化确定性测试台落地 + 实证关键阻塞

> 详细技术结论见 **`docs/07_TECHNICAL_HARD_PROBLEMS.md`**（尤其 §0 / §0.5）。

### 6.1 已完成（本轮新增，编译 0/0，实机注入 OK=14 类）
- **确定性固定步长时钟**：`Determinism/LockstepClock.cs` 新增 `ITickGate`（`AlwaysReady/Manual/TurnInput` 骨架）、`LogicTickActive`（跨线程，修复原先 Task 线程拿不到 `[ThreadStatic]` 标志的问题）、`LoadingLevel`；`UpdateGameLogic` 前缀按 gate 精确推进 `GameTime=Last+33.33`（ready）或夹紧（blocked）；`LoadPhysics/LoadPhysicsOld` 在确定性模式下直接跳过后台线程。
- **自动化回放验证器** `Testing/DeterministicReplayVerifier` + `TestHarnessConfig` + `ReplayTimeline`（纯逻辑可离线单测）+ `TestingLifecyclePatches`：主菜单就绪后自动载入存档 → 等实体稳定取基线 → 逐 tick 记录 StateHash 到 `.timeline` → 两进程各跑一遍 → `compare_hashes.ps1` 比对。触发全走环境变量（见 07 文档 / 脚本头）。
- **脚本化操作注入** `Testing/ScriptedCommandDriver`（默认 +10 帐篷 / +30 移动 / +60 训练；支持 `TABCOOP_SCENARIO` 文本脚本）。
- **网络故障注入** `Networking/ChaosTransport`（延迟/抖动/丢包/断连窗口，seeded，`Delivered/Dropped` 统计），经 `CoopSessionManager.CreateTransport` 自动包裹。
- **StateHasher v2**：新增投射物 / 生产队列 / 预留量子校验和，全反射实现（不碰内部类型），顺序无关。
- **追加确定性尝试（已落地，未解决）**：独立 `DXRandomCtorPatch`（Hook 无参构造定种）、`GameBridge.SortEntitiesById`（装载后按 ID 重排实体）。详见 07 §0。
- **修复**：空选中右键不再被吞；每帧复位 `SuppressIndividualMoveExecution/InQueueAutoAdvance`；`StopSession` 清回调与重入标志、修剪 `_hostTickHashes`；硬编码混淆名收进 `ObfuscatedSymbols`；`ModLogger` 支持 `TABCOOP_LOG_NAME`（多开分日志）。
- **关键运行时修复**：`GameBridge.CurrentLevelState` 原先强转 `internal` 的 `ZXLevelState` 会在运行时抛 `TypeAccessException`；已改为返回 `object` + 反射读取（`GetLevelInt` 等）。`StateHasher`/`GameBridge` 实体路径同样改为 `object` + 反射。

### 6.2 实测结论（重要）
- 注入链、14 个 Harmony 补丁类、主菜单按钮 **全部正常**（`Classes OK=14, failed-tolerant=0`，0 missing）。
- 自动载入存档**能进入关卡**（已修：必须等主菜单 `OnLoad` 后再加载，否则 NRE）。
- 两进程同存档对比，经"载入前启用沙盒 + `DXRandom` 无参构造定种 + 实体按 ID 重排 + DXGuid 加载期不走同步 ID"多步修复后：**tick、entities、life、proj、prod、resv、经济全部一致，只剩 `pos`（位置）分叉**（`1_Backup.zxsav` tick 31925，entities=3017 两跑相同）。问题已精确收敛到**每 tick 的移动/物理位置更新**。详见 `docs/07 §0`。
- 全局串行化 `DXHelper_Task.For` 是**负优化**（entities/life 重新分叉），已回退。不要全局串行。
- **位置 diff 诊断**（新增 `scripts/compare_pos.ps1`）：两跑实体 ID 集合一致，位置不同的**只有移动中的单位（僵尸/市民）**，静态实体完全一致；偏移随 tick **累积增长**（1 tick≈0.16，95 tick≈1.2）→ 判定为**逐 tick 移动积分不确定**（疑似按真实时间而非固定 `GameTime` 推进）。已新增 `LockstepClock.ResetBehaviourRandom` 重置 `DXBehaviour<BData>.GlobalRandom`（源码种子来自 `DXRandom.Global.Next()`），但未完全解决；详见 `docs/07 §0.1`。
- 指令注入仍撞内部类型 `TypeAccessException`（`CommandExecutor`/`ScriptedCommandDriver` 未反射化），本轮降级为 `SKIP`，详见 `docs/07 §0.5`。

### 6.3 接手后第一优先级
1. 按 `docs/07 §0` 攻克**存档加载确定性**（Hook `DXRandom` 无参构造函数 / 所有 `System.Random` / 加载期并行实体创建）。
2. 按 `docs/07 §0.5` 把 `CommandExecutor` 全量反射化，让脚本注入真正生效。
3. 之后才回到原 Step 2（暂停/倍速/市长同步，见 `docs/07 §5`）。

---

## 7. 2026-11-09 会话3：完成 07 §0.5（指令注入反射化）并实机验证

### 7.1 已完成
- **`CommandExecutor` + `ScriptedCommandDriver` 全量 `object` + 反射化**（07 §0.5 已标记解决）：
  - `GameBridge` 的 `GetCommandById/ByName/GetCommandGeneric/ExecuteCommandOnEntity/AssignGroupMoveTargets/PlaceBuilding` 全部返回/接收 `object`，不再 `as ZXCommand/ZXEntity/ZXCommandTarget`。
  - 新增 `CreateCommandTarget`（`Activator` 造 `ZXCommandTarget` 箱式结构体 + 反射改 `SerPosition/SerSize/SerOrigin/SerEntityRef`）、`HasComponent(DXEntity, Type)`（扫描 public `Components`，替代 `HasComponent<T>()` 泛型实例化）、`GetCommandableTargetObject`、`GetCommandId`、`GetBuild/TrainCommandForType`、`CancelCurrentOrderObject/CancelQueueSlotObject`。
  - 内部类型一律从已绑定 `MethodInfo` 反推或 `GameAssembly.GetType(...)`，不新增 `typeof(内部类型)`。
- 构建 0 警告 0 错误；部署到游戏目录。

### 7.2 实机验证（`1_Backup.zxsav`，120 tick，两进程各跑一遍）
```
[Harmony] Classes OK=15, failed-tolerant=0     (0 missing bindings)
[ScriptDriver] tick 32010 inject 'build_tent'   (无异常)
[ScriptDriver] tick 32030 inject 'move_units'   (无异常)
[ScriptDriver] tick 32060 inject 'train_ranger' (无异常)
ERROR=0  WARN=0
两遍 CommandId 完全一致：Travel=1952427338 / Train=584315074
compare_hashes: 首个分叉仍为 tick 32001 的 pos（注入发生前）→ 未新增分叉类别
```
**结论：§0.5 完成；脚本化指令注入现在能真正执行（不再 TypeAccessException/SKIP）。**

### 7.3 仍未解决 / 下一步
1. **§0.1 移动积分不确定**（唯一残留分叉 `pos`）——仍交更大模型，切入点见 07 §0.1。
2. **捕获侧内部类型**（`CommandInterceptorPatch` 的 `is Build`/`__0.Team`/`__1.SerEntityRef`/`HasComponent<CMovable>()`）尚未以真实输入复核；若 `TABCOOP_INTERCEPT=1` 下复现 `TypeAccessException`，按 §0.5 同法反射化。
3. 之后回到 07 §1（网络帧同步门控）与 §5（暂停/倍速/市长同步）。

---

## 8. 2026-11-09 会话3（续）：MCP 自动化操作测试台

> 详细用法见 **`docs/08_MCP_AUTOMATION.md`**。

### 8.1 新增能力
- **游戏内控制服务** `Testing/ControlServer.cs`：`TABCOOP_CONTROL_PORT` 开启后监听 `127.0.0.1`，换行 JSON；socket 线程只收发，**所有操作排队到 `DXGame.StartFrame` 主线程执行**，统一走 `CommandExecutor`/`NetCommand`。
- **截图服务** `Testing/ScreenshotService.cs`：用引擎自带 `DXGame.CaptureScreen()` 存 PNG；改动类工具**延迟 2 帧**抓图，确保反映操作效果；目录为本工作目录 `tabcoop_shots/`（启动脚本经 `TABCOOP_SHOT_DIR` 指定，未设置时回退 `<游戏目录>/tabcoop_shots`）。
- **独立 MCP 服务端** `src/TABCoopMod.Mcp/`（net10.0，无第三方依赖，手写 MCP stdio JSON-RPC）：把 10 个工具转发到控制服务，并在返回前等待截图落盘。
- **配置/脚本**：`.mcp.json`（`tabcoop-game`）、`scripts/build_mcp_server.ps1`、`scripts/run_game_with_control.ps1`、`scripts/control_smoke.ps1`。
- `TestHarnessConfig` 新增 `ControlPort`/`AutoShot`/`ControlEnabled`；控制模式可单独自动读档且**不触发回放验证器**。

### 8.2 工具
`game_state` / `list_entities` / `move_units` / `place_building` / `train_unit` / `cancel_current_order` / `cancel_queued_order` / `set_paused` / `issue_command` / `screenshot`。
实体 ID 为 64 位、以字符串返回；改动类工具结果含 `screenshot` 路径。

### 8.3 实机验证（`1_Backup.zxsav` 自动读档）
- 控制层 `control_smoke.ps1`：`ping`/`get_state`/`list_entities`/`screenshot` 正常；PNG 有效（2880×1800）。
- MCP 全链路（stdio 喂 JSON-RPC）：`initialize`/`tools/list`/`tools/call` 正常；`game_state`（unpause 前后）、`list_entities`、`move_units`（游侠从 (136.17,125.48) 移动到 (147.41,127.5) 途中）、`train_unit`（Train cmd 584315074）均成功并自动截图。
- 修复：MCP `JsonNode` 参数父子复用（`DeepClone`）；`game_state` 与 `get_state` 工具名对齐。
- 截图经缩略图目视确认为真实游戏画面。
- **存档模式实测**（`game_state` 新增 `gameMode/inCampaign/difficulty/survivalMode`）：`1_Backup.zxsav`=**Campaign**(Normal，~9.8k 实体/178 玩家实体)；`test.zxsav`/`900survival.zxsav`/`6666.zxsav`=**Survival**（`6666` 为发育完整的中后期档，1020 玩家实体，推荐做自动化操作测试）。战役与生存引擎确定性等效，但代表性不同，详见 08 §6。

### 8.4 尚未覆盖
鼠标框选/点选/编队/摄像机、攻击/修理等需 commandId 的复合指令（可 `issue_command` 手动）、倍速/市长（executor 未实现）、真实键鼠事件注入。详见 08 §8。

### 8.5 用新设计重走测试（生存档 `6666.zxsav`）
- **MCP 操作链路**（控制台 + 生存档）：`game_state`(Survival,1020 玩家实体)→`set_paused(false)`→`list_entities`(3×SoldiersCenter, Worker/Sniper)→`move_units`(3 单位)→`train_unit`(Ranger)→`screenshot` 全部成功，自动截图落盘。
- **两进程确定性回放**（120 tick）：首个分叉 tick 168601 报 `[entities,pos,life,proj]`；两跑实体 7911 vs 7920、ID 集合差 18/27、`moved=3761`。**比战役档（只 `pos`）更严重** → 生存档暴露实体生成/死亡层不确定。详见 `docs/07 §0.1 追加`。
- 结论：**后续确定性验证必须以生存档为准**，战役档仅作次要用例。

### 8.6 分叉可视化与相机导航（新）
- 回放验证器在首个记录 tick 额外导出同 tick 截图 `coop_shot_runN.png` 与相机变换 `coop_cam_runN.json`（`Testing/CameraProbe.cs`）。
- 回放截图默认沿用存档相机（实测指向右下安全区，无单位交互）。用 `run_replay_verify.ps1 -ShotCam "cx,cy,w,h"` 覆盖到交互区（实测 6666 的分叉集中在 x≈37–97、y≈52–112，用 `-ShotCam "75,85,64,40"` 后截图覆盖左侧交互区；引擎会按地图边界轻微夹紧 X）。
- 新增 `scripts/build_divergence_report.py`：生成自包含 HTML，左=世界坐标分叉散点/箭头，右=**两张同 tick 截图上按坐标画分叉框**（实线本跑/虚线另一跑，优先视野内），下=逐 tick 分项时间线。示例产物：`analysis/divergence_6666/index.html`（已 gitignore）。
- MCP 新增 `get_camera` / `set_camera`：可移动到任意世界坐标/实体并缩放后自动截图，用于放大查看差异点。实测相机移动后保持、画面随之改变。

