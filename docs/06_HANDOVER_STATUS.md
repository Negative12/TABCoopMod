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
