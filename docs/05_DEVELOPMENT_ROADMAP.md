# 05 — 分阶段开发路线图与门禁标准 (Development Roadmap & WBS)

> **状态**: Phase 0 & Phase 1 已完结；Phase 2 & Phase 3 核心工程与自动化门禁已全面落地（截至 `v0.1.7`），进入真人全链路长局联调与跨机多网络 Steam P2P 实玩验收阶段。

---

## 阶段总览 (Milestone Overview)

| 阶段 | 名称 | 核心交付目标 | 当前状态 |
| :---: | :--- | :--- | :---: |
| **Phase 0** | **工具链就位与注入破冰** | 提取内嵌 SDK，搭建 `TABCoopMod.csproj`，实现 `AppDomainManager` 无损注入、`GameBridge` 自检（0 missing）、逐类容错 Harmony 加载、`ModInfo`/`ModLogger` 版本化归档与主菜单按钮。 | ✅ 已完成 (100%) |
| **Phase 1** | **确定性沙盒与指令回放** | 确定性时钟、同步寻路、物理单线程与步末 `1/256` 量化、`DXGuid` 分流、`DXRandom` 定种隔离、视效/事件隔离（`VisualVfxIsolationPatch`）、`LogicThreadId` 边界、8 类指令反射回放、`StateHasher` v3。通过 `1_Backup`（120 tick）与 `6666`（180 tick）双进程零分叉回放门禁（Gate A1/A2）。 | ✅ 已完成 (100%) |
| **Phase 2** | **双模混合网络层与内存存档流** | `LocalTcpLoopbackTransport`、`SteamP2PTransport`（`nChannel` 隔离 + 槽位复用 + 版本校验）、`DirectFastPathManager`（双栈 IPv4/IPv6 UDP 直连升级 + STUN 保活 + 无损 Steam 回退）、`ChaosTransport`、`CoopLobbyWindow`（含本地 Chaos 面板）；`coop_host_snapshot` 32KB 分片同步与主机反压流控。通过 Loopback/Chaos/异构算力同步门禁（Gate B1/B2）。 | ✅ 核心完成，待跨机多网络长局实玩 |
| **Phase 3** | **执政官模式协同 UX 与全量联调** | `TurnInputTickGate` 门控、读取原版绑定的暂停冻结 + 暂停微操、3 秒恢复倒计时、市长同池抽取/奖励单位与建筑同步/对端自动关窗、胜负结算与退局回大厅清理、小地图队友视野框与 Ping、0ms 建造幽灵与单位语音、控制权差分对齐与全场景视觉插值、局内重同步保留本地 `1~8` 编队。已通过 Gate C/D 及 `v0.1.1~v0.1.7` 自动化回归，持续人工桌面长局打磨。 | 🔄 95% |
| **Phase 4** | **独立基地模式拓展（第二阶段）** | 多指挥中心生成、Mod 层虚拟阵营资源账本隔离、单位所有权锁定与多路尸潮分流。 | ⏳ 远期规划 |

---

## 各阶段核心交付物（对应当前实际源码）

### Phase 0：工具链与注入地基 ✅
- 工具脚本：[`scripts/extract_sdk.ps1`](../scripts/extract_sdk.ps1)、[`scripts/decompile_type.ps1`](../scripts/decompile_type.ps1)、[`scripts/install_mod.ps1`](../scripts/install_mod.ps1)、[`scripts/uninstall_mod.ps1`](../scripts/uninstall_mod.ps1)、[`scripts/package_release.ps1`](../scripts/package_release.ps1)
- 注入与核心：[`Bootstrap/ModAppDomainManager.cs`](../src/TABCoopMod/Bootstrap/ModAppDomainManager.cs)、[`Core/ObfuscatedSymbols.cs`](../src/TABCoopMod/Core/ObfuscatedSymbols.cs)、[`Core/GameBridge.cs`](../src/TABCoopMod/Core/GameBridge.cs)、[`Core/ModInfo.cs`](../src/TABCoopMod/Core/ModInfo.cs)、[`Core/ModLogger.cs`](../src/TABCoopMod/Core/ModLogger.cs)
- 入口 UI：[`Patches/MainMenuPatch.cs`](../src/TABCoopMod/Patches/MainMenuPatch.cs)

### Phase 1：确定性沙盒与指令系统 ✅
- 确定性内核：[`Determinism/LockstepClock.cs`](../src/TABCoopMod/Determinism/LockstepClock.cs)、[`Determinism/DeterministicTaskPatch.cs`](../src/TABCoopMod/Determinism/DeterministicTaskPatch.cs)、[`Determinism/DXRandomCtorPatch.cs`](../src/TABCoopMod/Determinism/DXRandomCtorPatch.cs)、[`Determinism/TickGate.cs`](../src/TABCoopMod/Determinism/TickGate.cs)、[`Determinism/StateHasher.cs`](../src/TABCoopMod/Determinism/StateHasher.cs)
- 指令与回放：[`Commands/NetCommand.cs`](../src/TABCoopMod/Commands/NetCommand.cs)、[`Patches/CommandInterceptorPatch.cs`](../src/TABCoopMod/Patches/CommandInterceptorPatch.cs)
- 测试与验证台：[`Testing/DeterministicReplayVerifier.cs`](../src/TABCoopMod/Testing/DeterministicReplayVerifier.cs)、[`Testing/ReplayTimeline.cs`](../src/TABCoopMod/Testing/ReplayTimeline.cs)、[`Testing/ScriptedCommandDriver.cs`](../src/TABCoopMod/Testing/ScriptedCommandDriver.cs)、[`Testing/TestingLifecyclePatches.cs`](../src/TABCoopMod/Testing/TestingLifecyclePatches.cs)、[`Testing/ControlServer.cs`](../src/TABCoopMod/Testing/ControlServer.cs)、[`Testing/ScreenshotService.cs`](../src/TABCoopMod/Testing/ScreenshotService.cs)、[`Testing/CameraProbe.cs`](../src/TABCoopMod/Testing/CameraProbe.cs)、[`Testing/TestHarnessConfig.cs`](../src/TABCoopMod/Testing/TestHarnessConfig.cs)

### Phase 2 & Phase 3：网络同步、快照流与协同 UX 🔄
- 网络传输：[`Networking/INetworkTransport.cs`](../src/TABCoopMod/Networking/INetworkTransport.cs)、[`Networking/SteamP2PTransport.cs`](../src/TABCoopMod/Networking/SteamP2PTransport.cs)、[`Networking/DirectFastPath.cs`](../src/TABCoopMod/Networking/DirectFastPath.cs)、[`Networking/ChaosTransport.cs`](../src/TABCoopMod/Networking/ChaosTransport.cs)
- 会话与同步：[`Sync/CoopSessionManager.cs`](../src/TABCoopMod/Sync/CoopSessionManager.cs)、[`Sync/PauseSyncController.cs`](../src/TABCoopMod/Sync/PauseSyncController.cs)、[`Sync/CoopBalanceManager.cs`](../src/TABCoopMod/Sync/CoopBalanceManager.cs)
- 交互拦截与渲染：[`Patches/PauseAndSpeedInputPatch.cs`](../src/TABCoopMod/Patches/PauseAndSpeedInputPatch.cs)、[`Patches/MayorSyncPatch.cs`](../src/TABCoopMod/Patches/MayorSyncPatch.cs)、[`Patches/LevelEndSyncPatch.cs`](../src/TABCoopMod/Patches/LevelEndSyncPatch.cs)、[`Patches/SurvivalBalancePatch.cs`](../src/TABCoopMod/Patches/SurvivalBalancePatch.cs)、[`UI/CoopLobbyWindow.cs`](../src/TABCoopMod/UI/CoopLobbyWindow.cs)、[`UI/AllyOverlayRenderer.cs`](../src/TABCoopMod/UI/AllyOverlayRenderer.cs)

#### Phase 2/3 剩余待验收项
1. **真人键鼠操作全链路长局验证**：`Chaos` 故障注入压测、异构算力压测、生存新局平衡、市长赠送单位/建筑、胜负结束退出及重同步编队保留等均已通过双开自动化验证；后续需通过完整真人桌面游玩覆盖大规模尸潮混战手感与全部建筑/部队热键边界。
2. **Steam P2P 跨机多网络拓扑实玩**：在双 Steam 账号、跨网（IPv6/UPnP/STUN 直连与 CGNAT Steam 中继保底）环境下验证长局稳定性与好友邀请体验。
3. **多人 UX 细节延伸**：后续按需补充地面队友彩色光标/建筑预览框渲染、市长“客机推荐高亮 + 房主拍板”等增强体验。

### Phase 4：独立基地模式拓展（远期规划）⏳
- 开局对称多基地生成、Mod 层虚拟阵营资源池与顶栏 UI 隔离、单位所有权锁与尸潮分流。
