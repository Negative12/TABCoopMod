# 08 — MCP 自动化测试台（模拟人类操作）

> **创建**: 2026-11-09（会话3）
> **用途**: 让 Agent / 视觉模型通过 MCP 驱动正在运行的《They Are Billions》，自动执行游戏内操作并自动截图复核，实现"人类操作"的自动化测试。

---

## 1. 架构

```
┌────────────────────┐   MCP (stdio, JSON-RPC)   ┌──────────────────────────┐
│ Claude Code / 任意  │ ────────────────────────> │ TABCoopMod.Mcp (net10)   │
│ MCP 客户端           │ <──────────────────────── │  独立进程，stdio 服务端    │
└────────────────────┘                           └───────────┬──────────────┘
                                                              │ TCP 127.0.0.1:27901
                                                              │ 换行分隔 JSON
                                                              v
                                                 ┌──────────────────────────┐
                                                 │ TheyAreBillions.exe      │
                                                 │  ├ ControlServer (TCP)   │
                                                 │  ├ 主线程 StartFrame 队列 │
                                                 │  ├ CommandExecutor/NetCommand │
                                                 │  └ ScreenshotService(DXGame.CaptureScreen) │
                                                 └──────────────────────────┘
```

要点：
- **游戏内**的 `ControlServer` 是真正的执行者：socket 线程只负责收发，**所有游戏操作都排进队列、在渲染主线程（`DXGame.StartFrame`）执行**，避免跨线程改游戏世界。
- 指令统一走 `CommandExecutor`/`NetCommand`（与联机回放同一条路径），不读本地 `CSelectable`。
- **MCP 服务端**是独立 .NET 进程，只做协议转换与转发，不加载游戏程序集。
- **截图**用引擎自带的 `DXGame.CaptureScreen()`（返回 `Bitmap`），保存为 PNG；改动类工具会**延迟约 2 帧**抓图，确保画面已反映操作。

---

## 2. 前置构建

```powershell
# 1) 构建并部署 mod（含 ControlServer）
powershell -ExecutionPolicy Bypass -File scripts/install_mod.ps1

# 2) 构建 MCP 服务端（net10.0，无第三方依赖）
powershell -ExecutionPolicy Bypass -File scripts/build_mcp_server.ps1
```

`.mcp.json` 已指向 `src/TABCoopMod.Mcp/bin/Release/net10.0/TABCoopMod.Mcp.dll`，需在**仓库根目录**运行（Claude Code 以项目根为 cwd）。

---

## 3. 启动带控制通道的游戏

```powershell
# 自动读档进入关卡（推荐）
powershell -ExecutionPolicy Bypass -File scripts/run_game_with_control.ps1 `
    -SavePath "%USERPROFILE%\Documents\My Games\They Are Billions\Saves\1_Backup.zxsav"

# 或手动进游戏后再开控制通道（不自动读档）
powershell -ExecutionPolicy Bypass -File scripts/run_game_with_control.ps1 -NoAutoLoad
```

- 设置 `TABCOOP_CONTROL_PORT=27901`（默认）即启用控制服务；不设置则关闭、零开销。
- 自动读档走 `TABCOOP_AUTO_LOAD_SAVE`，**不触发回放验证器**（验证器需 `TABCOOP_TEST`）。
- 自动截图默认开；`-AutoShotOff` 或 `TABCOOP_AUTO_SHOT=0` 关闭。
- 若存档是暂停态，先调用 `set_paused {paused:false}` 再操作（否则逻辑不推进）。

自检（不经过 MCP 层）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/control_smoke.ps1 -Port 27901
```

---

## 4. 在 Claude Code 中启用

仓库根的 `.mcp.json` 已声明 `tabcoop-game` 服务器。首次使用需**重启 Claude Code** 并在 MCP 授权提示中批准。之后即可直接调用工具（如"把选中的游侠移到 X,Y 并截图"）。

> 若在本会话内新增 `.mcp.json`，当前会话不会热加载，需重启。

---

## 5. 工具列表

| 工具 | 说明 | 主要参数 |
|---|---|---|
| `game_state` | 当前 tick、暂停、资源、实体数、**游戏模式/难度** | — |
| `list_entities` | 列出实体（id/类型/坐标/HP/阵营/可移动） | `typeContains`,`onlyPlayer`,`movable`,`includeProjectiles`,`limit` |
| `move_units` | 群体移动 | `ids[]`,`x`,`y`,`shift?` |
| `place_building` | 放置建筑（走正常 Build 命令扣资源） | `buildingType`,`x`,`y`,`sizeW?`,`sizeH?`,`builderId?`,`isBonus?`,`instant?` |
| `train_unit` | 兵营/生产建筑训练单位 | `barracksId`,`unitType` |
| `cancel_current_order` | 取消当前命令/建造 | `ids[]` |
| `cancel_queued_order` | 取消生产队列槽位 | `ids[]`,`slot` |
| `set_paused` | 暂停/恢复 | `paused` |
| `issue_command` | 原始 NetCommand 逃生口 | `type`,`commandId`,`ids[]`,`targetEntityId`,`x`,`y`… |
| `screenshot` | 立即截图并返回路径 | `tag?` |

**实体 ID 是 64 位，超过 JS 安全整数范围，工具统一以字符串返回**；传参用字符串或数字均可。
改动类工具返回结果含 `screenshot` 字段（PNG 绝对路径）；MCP 服务端会**等待文件落盘**再返回，可直接用读图工具打开。

### 典型流程
1. `game_state` → 确认 `levelLoaded=true`、`paused`。
2. `set_paused {paused:false}`。
3. `list_entities {onlyPlayer:true,movable:true,typeContains:"Ranger"}` → 拿到 id。
4. `move_units {ids:["..."],x:150,y:130}` → 返回截图路径。
5. 读图或再次 `list_entities` 验证位置变化。

---

## 6. 测试存档与游戏模式

`game_state` 现在会返回 `gameMode`(Survival/CustomLevel/Campaign)、`inCampaign`、`difficulty`、`survivalMode`，可直接确认当前存档属于哪种模式。

已实测的本机存档：

| 存档 | 模式 | tick | 实体数 | 玩家实体 | 资源 | 适用场景 |
|---|---|---|---|---|---|---|
| `1_Backup.zxsav` | **Campaign**(Normal) | 31904 | ~9.8k | 178 | 355金 | 战役流程；**不代表生存** |
| `test.zxsav` | **Survival** | 443 | ~43k | 6 | 100/20 | 生存早期（单位极少） |
| `900survival.zxsav` | **Survival** | 11 | ~39.7k | 6 | 100/20 | 生存开局（几乎无基地） |
| `6666.zxsav` | **Survival** | 168485 | ~30.6k | **1020** | 9605金/392油 | **生存中后期、发育完整——推荐做操作/自动化测试的默认存档** |

**战役 vs 生存是否等效：**
- **引擎确定性层面等效**：两者共用同一 `ZXLevelState`、实体更新、物理、`DXRandom`、`NetCommand`/`CommandExecutor`。`docs/07 §0.1`（移动积分）与 §0.5（反射化）结论对两种模式都成立。
- **代表性不同（不等效）**：战役有任务脚本/目标/`CampaignState`/`LevelEvents`；生存有 `SurvivalModeParams`（随机地图生成）、周期性尸潮、难度/天数缩放、市长系统。生存的实体规模与随机面更大，是**更严苛、更接近联机目标**的确定性测试面。
- **建议**：自动化操作测试默认用 `6666.zxsav`（发育完整的生存档）；早期流程用 `test.zxsav`/`900survival.zxsav`；战役档保留为第二套用例，避免只测一种模式。

## 7. 截图

- **默认目录：本工作目录 `<repo_root>\tabcoop_shots\`**（由 `run_game_with_control.ps1` 设置 `TABCOOP_SHOT_DIR`，直接写入工作目录，无需再搬）。
- 若手动启动游戏、未设置 `TABCOOP_SHOT_DIR`，回退到 `<游戏目录>\tabcoop_shots\`。
- 命名：`<序号>_t<tick>_<时分秒_毫秒>_<tag>.png`，例如 `00002_t32786_160213_380_move_units.png`。
- 分辨率与渲染目标一致（实测 2880×1800，约 10MB/张）。体积较大，自动化批量跑时注意清理；`tabcoop_shots/` 已在 `.gitignore` 中，不会误提交。
- 画面经实测为真实游戏渲染（非黑屏）。
- 历史截图已手动迁移至 `tabcoop_shots/`（无需重跑）。

---

## 8. 已覆盖 vs 未覆盖的人类操作

**已覆盖（走确定性指令路径）**
- 移动、建造、训练、取消当前/队列、暂停。
- 实体/资源/暂停状态查询。

**未覆盖（后续可加）**
- 鼠标框选/点选、编队、摄像机移动（UI 层，通常不影响确定性）。
- 攻击/修理等需要 `commandId` 的复合指令（可用 `issue_command` 手动指定，但缺少按名查 commandId 的便利工具）。
- 倍速、市长二选一（`NetCommandType.SetGameSpeed/MayorVoteOrSelect` 的 executor 尚未实现，见 `docs/07 §5`）。
- 真实键鼠事件注入（当前是"命令级"注入，不模拟输入设备）。

---

## 9. 协议细节（控制层）

换行分隔 JSON，一行一请求/响应：

```json
// 请求
{"id":1,"tool":"move_units","args":{"ids":["217334551281212148"],"x":150,"y":130}}
// 成功
{"id":1,"ok":true,"result":{"dispatched":true,"command":"...","screenshot":"D:\\...\\00002_..._move_units.png"}}
// 失败
{"id":1,"ok":false,"error":"unknown tool 'x'"}
```

- 监听仅绑定 `127.0.0.1`，无鉴权，仅用于本机开发。
- 单请求 10s 超时（主线程忙/加载中会超时并返回 error）。

---

## 10. 排障

| 现象 | 处理 |
|---|---|
| MCP 工具报 `control server unavailable` | 游戏未启动或未设 `TABCOOP_CONTROL_PORT`；或 27901 被占用 |
| `levelLoaded=false` | 还在主菜单/加载中；用 `run_game_with_control.ps1 -SavePath ...` 自动读档 |
| 操作后画面无变化 | 存档为暂停态，先 `set_paused{false}`；或命令被游戏拒绝（资源/位置非法） |
| `unknown tool 'game_state'` | 控制端为旧 DLL，重新 `install_mod.ps1` |
| `.mcp.json` 未被加载 | 需重启 Claude Code 并批准项目 MCP 服务器 |
| 截图很大/很慢 | `-AutoShotOff`；或仅在需要时调 `screenshot` |
