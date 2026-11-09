# TABCoopMod — 《亿万僵尸》(They Are Billions) 2-3人 Steam P2P 联机 Mod

> **工作空间路径**: `<repo_root>`
> **本机游戏路径**: `<SteamDir>\steamapps\common\They Are Billions`
> **目标游戏版本**: `TheyAreBillions.exe` v1.0.14.29 (64-bit, .NET Framework 4.6)

---

## 🚀 接手 Agent 请先读这两份

1. **[`CLAUDE.md`](CLAUDE.md)** —— 铁律、环境事实、代码地图、给 Agent 的工作方式建议
2. **[`docs/06_HANDOVER_STATUS.md`](docs/06_HANDOVER_STATUS.md)** —— 当前完成度、未验证项、下一步 Step 1/2/3 任务清单

`.claude/skills/` 下有三个专用 skill：`tab-reverse-engineering`、`tab-build-and-test`、`tab-lockstep-sync`。

## 构建与部署速查

```bash
dotnet build src/TABCoopMod/TABCoopMod.csproj -c Release

# 先关游戏进程，否则 0Harmony.dll 被占用导致部署失败
powershell -NoProfile -Command 'Get-Process *TheyAreBillions* -EA SilentlyContinue | Stop-Process -Force'
powershell -ExecutionPolicy Bypass -File scripts/install_mod.ps1

# 日志
# "<SteamDir>\steamapps\common\They Are Billions\TABCoopMod.log"
```

---

## 1. 项目简介

`TABCoopMod` 是为单机即时战略游戏《亿万僵尸》（They Are Billions）构建的无服务器（Steam P2P）多人合作联机 Mod，支持 **2~3 名玩家** 共同进行生存模式（Survival）与自定义地图（Custom Levels）。

项目分两大演进阶段：
1. **第一阶段（当前核心目标）—— 执政官模式（Archon Mode / 共同经营一家）**：
   2-3 名玩家共同操控同一个人类帝国基地，共享视野、资源与科技，但每位玩家拥有**完全独立的摄像机视角、独立的鼠标框选、独立的控制面板与独立编队**。支持盟友光标/视野框实时显示、小地图信号打点（Ping）、同步暂停与市长投票/拍板。
2. **第二阶段（远期拓展目标）—— 独立基地模式（Independent Bases Mode）**：
   开局在地图不同区域生成 2-3 座独立指挥中心（Command Center），Mod 层面维护各自独立的资源账本、供电网络与单位控制权，共同抵御多路尸潮。

---

## 2. 文档索引（Documentation Index）

> **致开发者与 AI Agent**：在开始任何代码编写或逆向分析前，**必须按顺序阅读 `docs/` 目录下的设计规范与逆向字典**。

| 文档路径 | 核心内容说明 |
| :--- | :--- |
| [`docs/00_AGENT_DEVELOPMENT_GUIDE.md`](docs/00_AGENT_DEVELOPMENT_GUIDE.md) | **AI Agent 开发宪章與环境指南**：铁律约束（严禁改盘面 EXE）、自动化测试工作流、提取与反编译脚本用法。 |
| [`docs/01_PRD_AND_GAMEPLAY.md`](docs/01_PRD_AND_GAMEPLAY.md) | **产品需求与玩法交互设计**：执政官模式交互细节、暂停/倍速权限规则、市长选择同步、盟友视觉反馈与独立基地规划。 |
| [`docs/02_ARCHITECTURE_DESIGN.md`](docs/02_ARCHITECTURE_DESIGN.md) | **核心技术架构设计**：混合确定性帧同步（Lockstep）+ 内存存档流秒传（Zero-MapGen Save Sync）+ 确定性时钟/RNG/GUID 接管方案。 |
| [`docs/03_REVERSE_ENGINEERING_MAP.md`](docs/03_REVERSE_ENGINEERING_MAP.md) | **逆向工程符号映射字典**：实测提取的 `TheyAreBillions.exe` 与 `DXVision.dll` 关键类、混淆符号名（`#=z...`）、方法签名与行号索引。 |
| [`docs/04_NETWORK_PROTOCOL.md`](docs/04_NETWORK_PROTOCOL.md) | **网络通信协议规范**：双通道（Reliable / Unreliable）消息二进制格式、`NetCommand` 载荷定义、状态校验和（StateHash）算法。 |
| [`docs/05_DEVELOPMENT_ROADMAP.md`](docs/05_DEVELOPMENT_ROADMAP.md) | **分阶段开发计划与验收标准**：Phase 0 ~ Phase 4 任务拆解（WBS）、文件级交付物清单与 Agent 自动化验收指标。 |

---

## 3. 目录结构

```text
<repo_root>\
├── README.md                           # 项目总览与文档导航
├── .gitignore                          # 忽略提取的二进制库、编译产物与日志
├── docs/                               # 完整需求、架构、逆向字典与开发计划文档
│   ├── 00_AGENT_DEVELOPMENT_GUIDE.md
│   ├── 01_PRD_AND_GAMEPLAY.md
│   ├── 02_ARCHITECTURE_DESIGN.md
│   ├── 03_REVERSE_ENGINEERING_MAP.md
│   ├── 04_NETWORK_PROTOCOL.md
│   └── 05_DEVELOPMENT_ROADMAP.md
├── scripts/                            # 面向 Agent 的自动化工具脚本
│   ├── extract_sdk.ps1                 # 从游戏 EXE 内存解密提取 DXVision.dll / Steamworks.NET.dll
│   └── decompile_type.ps1              # 快速反编译指定类型的 C# 源码供 Agent 查阅
├── lib/                                # 提取出的游戏与引擎引用程序集 (由脚本生成)
└── src/                                # Mod C# 源码目录 (.NET Framework 4.6, x64)
    └── TABCoopMod/
```

---

## 4. 快速初始化 SDK 引用库

首次克隆或进入工作区时，运行以下命令从本机《亿万僵尸》主程序中解密并导出未混淆的引擎核心库到 `lib/`：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<repo_root>/scripts/extract_sdk.ps1"
```
