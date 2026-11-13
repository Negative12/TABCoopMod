# 06 — 当前项目状态、门禁验收报告与下一步计划 (Project Status)

> **最后更新**: 2026-11-13（会话 6 收官）
> **构建基线**: `TABCoopMod.csproj` Release 编译 `0 警告 0 错误`；实机注入 `GameBridge 0 missing`，Harmony 补丁 **`Classes OK=40, failed-tolerant=0`**。
> **历史过程记录**: 早期排查过程与会话 1~6 演进流水已归档至 [`docs/archive/`](archive/SESSION_CHANGELOG_S1_TO_S6.md)。

---

## 1. 全门禁实测验收总表（2026-11-13 最终代码实测）

| 门禁 | 测试存档与条件 | 实测关键指标与结论 | 状态 |
| :--- | :--- | :--- | :-: |
| **Gate A1** | `1_Backup.zxsav` / 120 ticks 双进程回放 | `[compare] PASS: 120 ticks identical`；`common=2125, moved(>0.01)=0, maxDx=0, maxDy=0` | ✅ 通过 |
| **Gate A2** | `6666.zxsav` / 180 ticks 双进程回放 | `[compare] PASS: 180 ticks identical`；`common=7842, moved(>0.01)=0, maxDx=0, maxDy=0`（加载仅耗时 ~7.87s） | ✅ 通过 |
| **Gate B2** | `6666.zxsav` / 65s Loopback (`latency=0`) | Tick `168484..169440`（956 ticks）连续 **17/17 次 `StateChecksum` 全部 `MATCH`**，`0 DESYNC`，`0 Auto-resync`，`0 ERROR` | ✅ 通过 |
| **Gate B1** | `1_Backup.zxsav` / 65s Loopback (`latency=0`) | Tick `31904..32880`（18 次）全部 `MATCH` → Tick `32940` 原生求解器 `0.002` 格 `Pos` 漂移触发 `Auto-resync`（约 `3.07s` 恢复至 Tick `33058 MATCH`）→ Tick `33058..33480`（9 次）全部 `MATCH`，`0 Client ERROR` | ✅ 通过 |
| **Gate C** | `1_Backup.zxsav` / 控制通道 + 截图 | 双端独立相机位置/缩放同步运行；截图目视确认本地白框与队友彩色视野框（Host `DodgerBlue` / Client `SpringGreen`）在小地图正确渲染 | ✅ 通过 |
| **Gate D** | `1_Backup.zxsav` / 暂停微操 + 3s 倒计时 | Client 发起 `request_pause(true)` 同步冻结于 Tick `31972`（显示 `Player 2 Paused Game`）；暂停期间 `move_units` 派发与资源扣减（`gold 355→235`）双端一致；Host 恢复后顶居中 `Resume in 3...` 清晰可见，解冻后单位于双端同步抵达 `(149.49, 132.35)` / `(149.42, 132.01)`，全程 Tick `31904..32100` 连续 5 次 `MATCH` | ✅ 通过 |

---

## 2. 已确认的工程边界与行为特性

1. **原生 Chipmunk 求解器极微小漂移 + `Auto-resync` 自愈**：
   - 在密集重叠僵尸群下，非托管 `DXPhysicsNative.Space_Step` 偶发 `1e-4` 级别速度微差，累积数十帧后可能产生约 `0.002` 格位置偏离（已通过 `PRE/POST DXPhysicWorld.Step` 验证全部托管输入完全一致）。
   - 已启用的 `Auto-resync` 能在检测到 `StateHash` 不一致后约 **3 秒内**自动重建并恢复一致状态继续锁步，且二次读档已无 `CShadow` NRE。
2. **纯视觉/环境实体不参与对局状态哈希**：
   - 裸 `DXVision.DXEntity`（受击飙血/弹花贴图）与 `Raven`（环境飞鸟）仅作用于视觉表现，在 `StateHasher` 与 `DeterministicReplayVerifier.DumpPositions` 中已明确跳过。
3. **同进程重载 (`inproc`) 仅用于崩溃/资源清理校验**：
   - `PrepareForProgrammaticLoad()` 已彻底消除关卡内二次读档 `CShadow` NRE，但 `inproc` 模式下引擎仍存在跨局静态残留实体总数偏差；确定性比对统一以**双进程模式**为准。
4. **未实机覆盖项（显式标记，不可假设已完成）**：
   - **真人键鼠操作全链路**：`CommandInterceptorPatch`、`PauseAndSpeedInputPatch` 与 `MayorSyncPatch` 的人类实时键鼠点击（而非脚本/MCP 注入）尚未完成端到端人工/输入模拟联调；
   - **Steam Lobby P2P 跨机联机**：`SteamP2PTransport`（内嵌 Steamworks.NET v10）需要双 Steam 账号或跨机实测；
   - **`ChaosTransport` 丢包与断连窗口**：Gate B1/B2 是在 `latency=0` 洁净回环下复测通过，含延迟/抖动/丢包/断连的长期稳定性需回归复测。

---

## 3. 下一步最高优先级任务（Next Steps）

1. **P1 — 真人输入全流程联调**：
   - 在游戏内进入单机拦截模式（`F8`）及双开 Loopback 联机模式，人工逐项验证：框选右键移动/攻击、小地图右键移动、Shift 连续建造与普通放置、兵营训练/取消队列、空格暂停与 `+`/`-` 倍速、市长二选一弹窗、`Alt+左键` 小地图打点。
2. **P2 — `ChaosTransport` 故障注入与 Steam P2P 跨机验证**：
   - 使用 `scripts/run_chaos_two_instance.ps1` 在 `latency=120;jitter=30;loss=0.02;disconnect=4000-5000` 下跑通 60s+ 双端同步；
   - 使用双 Steam 账号/双机测试 `CoopLobbyWindow` 的创建房间、好友邀请与 Steam P2P 全流程联机。
3. **P3 — 协同 UX 完善与 Phase 4 预研**：
   - 补全大地图地面队友彩色鼠标光标与建造预览框渲染；细化市长“客机意向高亮 + 房主拍板”交互；视情况启动 Phase 4 独立基地模式。
