# TABCoopMod — 《亿万僵尸》(They Are Billions) 2-3人 Steam P2P 联机 Mod

> **工作空间路径**: `<repo_root>`
> **本机游戏路径**: `<SteamDir>\steamapps\common\They Are Billions`
> **目标游戏版本**: `TheyAreBillions.exe` v1.0.14.29 (64-bit, .NET Framework 4.6)

---

## 🚀 接手 Agent 请先读这两份

1. **[`AGENTS.md`](AGENTS.md)**（或 [`CLAUDE.md`](CLAUDE.md)）—— 铁律、环境事实、代码地图、给 Agent 的工作方式建议
2. **[`docs/06_PROJECT_STATUS.md`](docs/06_PROJECT_STATUS.md)** —— 当前门禁验收报告（Gate A1/A2/B1/B2/C/D 全通过）、已知边界与下一步任务

`.agents/skills/` 与 `.claude/skills/` 下均维护了三个专用 skill：`tab-reverse-engineering`、`tab-build-and-test`、`tab-lockstep-sync`。

## 构建与部署速查

```powershell
dotnet build src/TABCoopMod/TABCoopMod.csproj -c Release

# 先关游戏进程，否则 0Harmony.dll 被占用导致部署失败
Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
powershell -ExecutionPolicy Bypass -File scripts/install_mod.ps1

# 日志
# "<SteamDir>\steamapps\common\They Are Billions\TABCoopMod.log"
```

---

## 1. 项目简介

`TABCoopMod` 是为单机即时战略游戏《亿万僵尸》（They Are Billions）构建的无服务器（Steam P2P）多人合作联机 Mod，支持 **2~3 名玩家** 共同进行生存模式（Survival）与战役/自定义关卡。

项目分两大演进阶段：
1. **第一阶段（当前核心目标）—— 执政官模式（Archon Mode / 共控一族）**：
   2-3 名玩家共同操控同一个人类帝国基地，共享视野、资源与科技，但每位玩家拥有**完全独立的摄像机视角、独立的鼠标框选、独立的控制面板与独立编队**。支持小地图队友视野框实时渲染、小地图信号打点（Ping）、同步冻结暂停微操与 3 秒恢复倒计时、市长按 ID 确定性同步。
2. **第二阶段（Phase 4 远期规划）—— 独立基地模式（Independent Bases Mode）**：
   开局在地图不同区域生成 2-3 座独立指挥中心（Command Center），Mod 层面维护各自独立的资源账本、供电网络与单位控制权，共同抵御多路尸潮。

---

## 2. 文档索引（Documentation Index）

| 文档路径 | 类别 | 核心内容说明 |
| :--- | :--- | :--- |
| [`docs/00_DEV_GUIDE.md`](docs/00_DEV_GUIDE.md) | 工程规范 | **开发指南、铁律与踩坑手册**：绝对铁律、工具链命令、CLR / HarmonyX 实机深坑与标准 SOP。 |
| [`docs/01_PRD_AND_GAMEPLAY.md`](docs/01_PRD_AND_GAMEPLAY.md) | 产品规范 | **产品需求与玩法交互设计**：执政官模式交互细节、暂停/倍速/市长同步规则与独立基地规划。 |
| [`docs/02_ARCHITECTURE_DESIGN.md`](docs/02_ARCHITECTURE_DESIGN.md) | 架构设计 | **核心技术架构设计**：`AppDomainManager` 注入、确定性沙盒、指令拦截与回放、内存存档流与 Auto-resync。 |
| [`docs/03_REVERSE_ENGINEERING_MAP.md`](docs/03_REVERSE_ENGINEERING_MAP.md) | 逆向参考 | **逆向工程符号映射字典**：与 `ObfuscatedSymbols.cs` 同步的混淆符号表、坐标/视野结论与原版引擎 Bug 索引。 |
| [`docs/04_NETWORK_PROTOCOL.md`](docs/04_NETWORK_PROTOCOL.md) | 协议规范 | **网络通信协议与状态校验规范**：`INetworkTransport`、8 字节包头、`NetCommand` 载荷与 `StateHasher` v2 算法。 |
| [`docs/05_DEVELOPMENT_ROADMAP.md`](docs/05_DEVELOPMENT_ROADMAP.md) | 路线图 | **分阶段开发路线图与门禁**：Phase 0 ~ Phase 4 完成度、实际源码交付物与待验收项。 |
| [`docs/06_PROJECT_STATUS.md`](docs/06_PROJECT_STATUS.md) | 实时状态 | **当前项目状态与门禁验收报告**：Gate A1/A2/B1/B2/C/D 实测结果、工程边界与下一步优先级。 |
| [`docs/07_TESTING_AND_MCP.md`](docs/07_TESTING_AND_MCP.md) | 测试工具 | **自动化测试台与 MCP 操作指南**：17 个 MCP 工具、双进程回放验证、Loopback/Chaos 联机脚本与存档库。 |
| [`docs/archive/`](docs/archive/ROOT_CAUSE_POSTMORTEMS.md) | 历史归档 | **疑难根因复盘与会话演进日志**：已解决的技术难题深挖档案（`ROOT_CAUSE_POSTMORTEMS.md`）与 S1~S6 里程碑记录。 |

---

## 3. 目录结构

```text
<repo_root>\
├── README.md                           # 项目总览与文档导航
├── AGENTS.md / GEMINI.md / CLAUDE.md   # Agent 工作规则与代码地图
├── .gitignore                          # 忽略提取的二进制库、编译产物、截图与日志
├── docs/                               # 规范、架构、字典、状态与历史归档
├── scripts/                            # SDK 提取、反编译、构建部署、回放与 Chaos 测试脚本
├── lib/                                # 提取出的游戏与引擎引用程序集 (由 extract_sdk.ps1 生成)
└── src/
    ├── TABCoopMod/                     # Mod 主工程 (.NET Framework 4.6, x64)
    └── TABCoopMod.Mcp/                 # 独立 MCP stdio 服务端 (.NET 10)
```

---

## 4. 快速初始化 SDK 引用库

首次克隆或进入工作区时，运行以下命令从本机《亿万僵尸》主程序中解密并导出未混淆的引擎核心库到 `lib/`：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<repo_root>/scripts/extract_sdk.ps1"
```
