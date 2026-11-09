# 00 — AI Agent 开发指南与工程规范 (Agent Development Guide)

> **工作区路径**: `<repo_root>`
> **本机游戏路径**: `<SteamDir>\steamapps\common\They Are Billions`
> **主程序**: `TheyAreBillions.exe` (v1.0.14.29, x64, .NET Framework 4.6)

本文档专为后续接手或并行推进开发的 AI Agent（Claude Code / Hermes / Codex 等）编写，规定了开发铁律、工具链路径与标准工作流。

---

## 1. 四大核心工程铁律（Golden Rules — 严禁违反）

### 铁律一：严禁修改磁盘上的 `TheyAreBillions.exe` 原文件
`TheyAreBillions.exe` 使用了 **Eazfuscator.NET** 保护，其中包含强完整性校验与调用栈验证（位于 `#=qDfSFIK4oxaAg6Chf$Hd4YoX3S8bUcaBW2qfFmC44D9A=`）：
- 它会读取当前程序集的 `AssemblyName`、特定类的 `MetadataToken`，并通过 `StackTrace.GetFrame(3)` 校验调用方是否来自原装 `TheyAreBillions` 程序集。
- **任何对 `TheyAreBillions.exe` 的磁盘静态修改（如使用 de4dot 重写、dnSpy 保存修改后的 EXE、修改 PE 头）都会导致资源解密密钥计算错误或触发游戏反作弊惩罚/崩溃。**
- **唯一正确做法**：保持 `TheyAreBillions.exe` 100% 原版字节不变，通过外部注入器（Doorstop / DLL 代理劫持）在运行时将 `TABCoopMod.dll` 注入到 CLR，并使用 **HarmonyX (`0Harmony.dll`)** 在内存中动态 Hook 方法。

### 铁律二：内嵌引擎库必须通过内存解密脚本提取
游戏的自研引擎 `DXVision.dll`、平台层 `DXPlatform_Desktop.dll` 以及 `Steamworks.NET.dll` 并不以明文存在于游戏目录（注意：`x64/Steamworks.NET.dll` 虽然存在，但主程序优先通过 `<Module>..cctor` 注册的 `AssemblyResolve` 从加密 ManifestResource 中加载）。
- 编译 Mod 前，必须先运行 `scripts/extract_sdk.ps1`，将内存中解密后的 `DXVision.dll`（完全无混淆！）、`DXPlatform_Desktop.dll`、`Steamworks.NET.dll` 导出到 `<repo_root>\lib\`。
- 严禁在 Mod 目录中分发另一份不同版本的 `DXVision.dll` 覆盖加载，Mod 项目引用 `lib/DXVision.dll` 时必须设置 `<Private>False</Private>`（即不复制到输出目录，运行时直接复用主程序已加载的实例）。

### 铁律三：混淆符号隔离原则（`GameBridge` 模式）
在 `TheyAreBillions.exe` 中：
- 实体类（`ZX.Entities.*`）、指令类（`ZX.Commands.*`）、组件类（`ZX.Components.*`）、关卡状态属性（`ZX.ZXLevelState`）**大部分是明文**。
- 但顶层系统类（如 `ZXGame` = `#=z4RevDP3eECXqXS6JRA==`、`ZXSystemGameLevel` = `#=zxRcpu6e7NYzT7tGWqPjpOkc=`）及许多方法名为 `#=z...` 乱码。
- **规定**：所有涉及 `#=z...` 乱码符号的反射、委托绑定（`AccessTools.MethodDelegate`）或 Harmony 目标方法定位，**必须全部收敛在 `src/TABCoopMod/Core/GameBridge.cs` 和 `src/TABCoopMod/Core/ObfuscatedSymbols.cs` 中**。业务层、网络同步层、UI 层代码只允许调用语义清晰的封装接口。

### 铁律四：本地视角/框选与网络同步指令彻底解耦
在执政官模式中：
- **绝不同步鼠标框选状态（`CSelectable.#=zIiHCgn_odqH7`）**，也**绝不同步本地摄像机移动（`DXScene.Current.CameraCenter`）**。
- 玩家在本地下达指令时，拦截器必须捕获该玩家当前选中的实体 ID 数组 `ulong[] ExecutorEntityIds` 随网络包发送。
- 收到网络指令包并执行回放时，**严禁读取或修改 `CSelectable.#=zIiHCgn_odqH7`**，必须直接通过 `DXGame.Current.GetEntityByID(id)` 获取实体并调用底层命令方法，确保玩家 A 的网络指令绝不会打断玩家 B 正在进行的框选和建筑放置预览！

---

## 2. 本机开发环境与工具链速查

| 环境项 | 本机实测值 / 路径 | 注意事项 |
| :--- | :--- | :--- |
| **操作系统** | Windows 11 x64 (10.0.22631) | Agent 终端运行在 `git-bash (MSYS)` 下，原生 Windows 程序需传 `D:/...` 或 `D:\...` 路径 |
| **.NET SDK** | `.NET SDK 10.0.100` (`C:\Program Files\dotnet\dotnet.exe`) | 支持编译 `<TargetFramework>net46</TargetFramework>`，需配置 `<PlatformTarget>x64</PlatformTarget>` |
| **游戏根目录** | `<SteamDir>\steamapps\common\They Are Billions` | 含 `TheyAreBillions.exe` (x64) 与 `steam_api64.dll` |
| **游戏存档目录** | `%USERPROFILE%\Documents\My Games\They Are Billions\Saves` | 存档格式为 `.zxsav` (加密 ZIP) + `.zxcheck` (校验码文本) |
| **反编译工具** | `ilspycmd` (封装于 `scripts/decompile_type.ps1`) | 可随时反编译 `TheyAreBillions.exe` 或 `lib/DXVision.dll` 中的任意类 |

---

## 3. Agent 常用脚本文档

### 3.1 提取/更新 SDK 引用库
当 `lib/` 目录为空时运行：
```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<repo_root>/scripts/extract_sdk.ps1"
```
产物：
- `lib/DXVision.dll` (1,233,408 bytes，无混淆引擎核心)
- `lib/DXPlatform_Desktop.dll` (276,992 bytes)
- `lib/Steamworks.NET.dll` (265,216 bytes，v10.0.0.0)
- `lib/SmartPropertyGrid.dll` (200,704 bytes)
- `lib/TheyAreBillions.exe` (原版主程序只读副本，仅供编译期或静态分析引用)

### 3.2 按需反编译查看游戏源码
当 Agent 需要查看某个具体游戏类（例如 `ZX.Commands.Upgrade` 或 `DXVision.DXWorldGrid`）的实现细节时，直接运行：
```bash
# 查看 TheyAreBillions.exe 中的类
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<repo_root>/scripts/decompile_type.ps1" -Assembly "TheyAreBillions.exe" -TypeName "ZX.Commands.Upgrade"

# 查看 DXVision.dll 中的类
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<repo_root>/scripts/decompile_type.ps1" -Assembly "DXVision.dll" -TypeName "DXVision.DXWorldGrid"
```

---

## 4. Agent 迭代开发标准闭环（SOP）

每一个子任务（Task）的执行必须遵循以下 4 步：
1. **查阅规范**：确认 `docs/05_DEVELOPMENT_ROADMAP.md` 中的任务目标，并在 `docs/03_REVERSE_ENGINEERING_MAP.md` 中核对目标 Hook 点的方法签名。
2. **编码实现**：在 `src/TABCoopMod/` 下编写模块化代码，遵循 `GameBridge` 封装规范，严禁硬编码未注释的混淆名。
3. **编译与自动化验证**：
   - 运行 `dotnet build` 确保零错误。
   - 部署到游戏目录并启动测试（单机回放验证或 `--coop-host` / `--coop-client` 环回双开验证）。
   - 检查日志输出 `TABCoopMod.log` 与状态哈希日志 `coop_sync_*.jsonl`，**必须有真实运行日志证明功能生效**，严禁仅凭代码编写完成即宣告任务结束。
4. **文档同步**：在 `docs/05_DEVELOPMENT_ROADMAP.md` 中勾选已完成项，若有新发现的混淆函数签名，补充至 `docs/03_REVERSE_ENGINEERING_MAP.md`。
