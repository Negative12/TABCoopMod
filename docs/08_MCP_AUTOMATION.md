# 08 — MCP 自动化测试台（模拟人类操作）

> **创建**: 2026-11-09（会话3）
> **更新**: 已扩展至 **17 个工具**（含倍速 `set_game_speed`、暂停倒计时 `request_pause`、市长查询/选择 `list_mayors`/`mayor_select`、相机导航 `get_camera`/`set_camera`、开全图 `reveal_map`）。相机等轴测坐标转换与开图显示僵尸的修复方案见 `docs/09_AUDIT_AND_WORKPLAN.md` §2.2。
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
| `set_game_speed` | 设置模拟倍速（0=暂停，1=正常，最高 20） | `speed` |
| `request_pause` | 本地暂停/恢复；**恢复走 3 秒倒计时**(顶部横幅) 再恢复 | `paused` |
| `list_mayors` | 列出市长（注册表 id/name/level） | `level?` |
| `mayor_select` | 按 MayorId 应用市长（确定性，不逐端重抽样） | `mayorId` |
| `issue_command` | 原始 NetCommand 逃生口 | `type`,`commandId`,`ids[]`,`targetEntityId`,`x`,`y`… |
| `screenshot` | 立即截图并返回路径 | `tag?` |
| `get_camera` | 当前相机世界矩形 + 屏幕尺寸（用于世界→像素映射） | — |
| `set_camera` | 移动/缩放相机到世界坐标或某个实体，并自动延迟截图 | `x`,`y`,`w`,`h`,`zoom`,`entityId` |
| `reveal_map` | 开全图（置 `ZXLevelState.ShowFullMap` 并关 `FogEnabled`），仅影响渲染，不改模拟 | `enabled?` |

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

**已覆盖（走确定性指令路径或渲染辅助）**
- 移动、建造、训练、取消当前/队列、暂停/恢复（含 3 秒倒计时 `request_pause`）。
- 倍速调节（`set_game_speed`）、市长列表查询与按 ID 确定性应用（`list_mayors` / `mayor_select`）。
- 实体/资源/暂停/模式状态查询（`game_state` / `list_entities`）。
- 摄像机查询/移动缩放（`get_camera` / `set_camera`）与开全图去黑雾（`reveal_map`）。

**未覆盖（后续可加）**
- 鼠标框选/点选、编队（UI 层，本地独立状态，不影响确定性）。
- 攻击/修理等需要 `commandId` 的复合指令（可用 `issue_command` 手动指定，后续可加按名查 commandId 的便利工具）。
- 真实键鼠硬件事件注入（当前是"命令级"注入，不模拟输入设备）。

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

---

## 11. 分叉可视化 HTML 报告（同 tick 截图标注）

把两进程回放的**同 tick** 数据可视化，直接看出"分叉发生在哪些位置"：

1. 回放验证器在首个记录 tick 除导出 `coop_pos_run1/2.txt` 外，还在**工作目录** `tabcoop_shots/` 保存**唯一命名**的截图与相机参数：
   - `replay_run<N>_t<tick>_<yyyyMMdd_HHmmss_fff>.png`
   - `replay_run<N>_t<tick>_<...>.cam.json`
   - 并维护 `tabcoop_shots/latest_replay_shots.json`（记录两跑截图/相机路径），**不再用固定文件名覆盖**，因此不会出现浏览器缓存旧图。
2. 生成报告。截图默认沿用存档相机（常在安全区/黑雾区），建议加：
   - `-ShotCam "cx,cy,w,h"`：把回放截图相机移到交互区（6666 的分叉集中在 x≈37–97、y≈52–112，可用 `"75,85,64,40"`；引擎会按地图边界轻微夹紧）。
   - `-RevealMap`：开全图（置 `ShowFullMap`、关 `FogEnabled`），去掉黑雾；仅影响渲染，不改模拟。
   ```powershell
   powershell -File scripts/run_replay_verify.ps1 -SavePath "<save>" -Ticks 120 -ShotCam "75,85,64,40" -RevealMap
   ```
   然后生成报告（`--manifest` 自动读取两跑的唯一截图/相机路径）：
   ```powershell
   python scripts/build_divergence_report.py `
     --pos1 "<game>\coop_pos_run1.txt" --pos2 "<game>\coop_pos_run2.txt" `
     --tl1 "<game>\coop_replay_run1.timeline" --tl2 "<game>\coop_replay_run2.timeline" `
     --manifest "tabcoop_shots/latest_replay_shots.json" `
     --out analysis/divergence_6666/index.html
   ```
3. 报告内容：
   - 左：世界坐标散点/箭头（两跑同一 tick 的逐实位移，颜色/长度表示位移大小，×表示独有实体）。
   - 右：**两张同 tick 截图**，按坐标在每个分叉实体处画框（实线=本跑位置，虚线=另一跑位置），默认优先标注视野内分叉点。
   - 下：逐 tick 分叉时间线（实体数曲线 + pos/life/proj/prod/resv 一致性色带）。
   - 交互：最小位移过滤、类型过滤、最多框数、缩放/平移、悬停查看实体 id/类型/位移。
4. 想看某个分叉点的放大实景，用 MCP：`set_camera {x,y,zoom}` 或 `set_camera {entityId}` → 自动截图到 `tabcoop_shots/`；`get_camera` 可回读相机矩形。

> 说明：回放截图与坐标来自同一 tick 附近（截图延迟 2 tick 等渲染）；相机由存档恢复或 `-ShotCam` 覆盖；两跑相机一致时可直接叠加对比。
>
> **开全图实现取舍**：引擎可见性判断是 `ZXLevelState.ShowFullMap || !ZXSystemMapLayers.FogEnabled`，所以直接置这两个参数最简单、无副作用（仅渲染）；`ZX.Entities.RevealMap`（沉默窥探者）只是靠 `CMapWatcher.WatchRange` 提供视野，需要建造/资源，不必用。MCP 的 `reveal_map` 工具即直接置位实现（实测黑雾消失、模拟状态不变）。
