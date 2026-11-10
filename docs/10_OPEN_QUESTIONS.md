# 10 — 待审阅的技术难题与求助 (Open Questions & Guidance Needed)

> **编写时间**: 2026-11-10
> **用途**: 本文档只记录**当前实现已到位、但仍存在困难或方向不明确**的问题。请审阅者（其他模型/开发者）针对每节末尾的“请回答”给出具体指导。
> **前置阅读**: `docs/09_AUDIT_AND_WORKPLAN.md` §4（本轮修复与实测结果）、`CLAUDE.md`（铁律：绝不改 EXE 字节、只用 HarmonyX 运行时补丁、跨程序集 internal 必须反射）。
> **代码基线**: Release 编译 0 错误；已实机验证补丁 `Classes OK=25, failed-tolerant=0`。

---

## 0. 当前验证状态速览

| 项 | 状态 |
|---|---|
| 7 处审计 Bug（§1.1~§1.6） | 代码已修复，关键项实机确认 |
| P0 确定性回放（1_Backup，120 tick） | **大幅改善但未归零**：仅 `pos` 偶发分叉（见问题 1） |
| P1 相机 / 开全图 | **实机截图通过** |
| P2 双实例锁步 | Host 链路通过；**Client 快照加载被引擎卡住**（见问题 2） |
| P3 暂停/倍速/市长 | 命令层实机通过；暂停不冻结 tick（已知边界，见 §5） |
| Chaos 双实例 | 曾出现 1 次 Host 栈溢出崩溃（见问题 3） |
| 6666.zxsav 验收 | 载入超时，未完成（见问题 4） |

---

## 1. 【最高优先】回放仍存在“每进程二值”的 `pos` 分叉

### 1.1 现象
`scripts/run_replay_verify.ps1 -SavePath <1_Backup.zxsav> -Ticks 120`（两进程各跑一遍）：

- `compare_hashes.ps1`：几乎每次 `FAIL at index 65~83 (tick 31970~31988)`，类别永远是 `[pos]`。
- `compare_pos.ps1`：在**首个记录 tick** 上恒为 `PASS`（`moved(>0.01)=0`）——说明分叉发生在运行途中，而非载入态。
- 分歧字段只有 `pos`；`life / proj / prod / resv / entities / 经济` 全程一致。
- 分叉规模：逐 tick 位置 dump 显示，首个分叉 tick 仅 1~2 个僵尸差 ~0.01 格，随后扩散到约 130 个僵尸、最大 ~0.02~0.03 格（约等于一个移动步长）。
- **决定性线索**：轨迹只有两条吸引子 A/B，两个进程各自独立地选 A 或 B。连续复跑时会出现“A/B 角色互换”：
  - 某次：run1=`0xFC32EA24…`，run2=`0xF391D57F…`
  - 下一次：run1=`0xF391D57F…`，run2=`0xFC32EA24…`
  → 这是**每进程一个 0/1 随机位**，不是线程竞态、不是顺序问题、不是单进程内不确定。

### 1.2 已排除的可能（有日志证据）
1. **载入态不一致**：`coop_loadpos_run1/2.txt` 与 `coop_baselinepos_run1/2.txt` 在多次实验中**逐位相同**（FNV 覆盖全部非 UI 实体的精确 float 位置），且两进程基线 `GameTime` 相同。
2. **物理步进不同**：逐 tick 记录 `DXPhysicWorld.NUpdates / Iteration / GameTime`，两进程完全一致；`UpdateDefaultTime` 已被 `DeterministicTaskPatch` 顺序化。
3. **寻路异步**：`ZXPlayerModeType=Multiplayer`，`PendingPathfindingCount` 全程 0；逐 tick `DXWorldGrid.NPathsComputed` 序列在分叉前完全一致；路径请求参数日志的首次差异发生在 **pos 已分叉之后**（32028 > 31983），排除“路径导致位置分叉”。
4. **读档期时钟漂移**：`UpdateGameLogic_Prefix` 在 `LoadingLevel` 期间冻结逻辑/物理时钟后，载入哈希与基线哈希均稳定一致。
5. **读档中途重播种子**：`StartRun` 曾调用 `EnableDeterministicSandbox`（会 ResetCounter），已删除；读档期不再做任何 RNG 重置。
6. **`DXRandom.Global` 线程静态**：已把 getter 在**所有上下文**改投固定种子流（逻辑=`synced`、物理回调=`aux`、渲染=`render`），并给 `DXGame.UpdatePhysics` 加了上下文标记补丁。
7. **参数化 `DXRandom` 计数器**：`DXRandomCtorPatch.ResetCounter()` 在 `EnableDeterministicSandbox` 统一重置；载入期 `DXHelper_Task.For` 三重重载全部顺序执行。

### 1.3 当前最可疑的残余来源（按可能性排序）
1. **载入期仍有“非 `DXHelper_Task` 的并行路径”在创建 `DXRandom`**：例如引擎内部直接用 `Task.Factory`/`Parallel.For` 反序列化或初始化实体，导致 `new DXRandom()` 拿到不同计数器值 → 某僵尸行为的第一抽签决定 A/B。
   - 已知 `ZXSystemGameLevel` 里有一处 `Parallel.ForEach`（仅计算 `DXImage.UpdateTransparencyData()`，看似纯渲染缓存，未必是元凶）。
2. **某个静态/系统缓存了沙盒启用前的 `DXRandom.Global` 引用**：`DXRandom.Global` 是 `[ThreadStatic]`，首次访问用 `new Random().Next()` 播种。若某系统在 `PrepareForLoad` 之前就把该实例存进静态字段并在模拟期使用，就会带入进程级随机。
3. **`DXBehaviour<BData>` 的每实体随机**：我们只重种了静态 `_GlobalRandom`，若每实体 `BData` 内部还持有独立 `DXRandom`（载入期创建、种子来自上述路径），重种静态字段无效。

### 1.4 建议的下一步实验（请审阅者判断优先级）
- **实验 A（低成本）**：把 `DXRandom.Global` getter 的确定性劫持提前到**进程启动即生效**（不依赖 `IsDeterministicModeActive`），用固定常量种子，消除“沙盒启用前播种”的所有隐患，观察 A/B 是否消失。
- **实验 B（定位）**：载入完成后，反射遍历 `DXGame.Current.Entities` 的全部组件、`ZXLevelState` 的集合字段、`DXBehaviour` 数据对象，找出所有 `DXRandom` 字段；把每个实例内部 `mt[]`（前 4 个 uint）、`mti` 与所属实体 ID 写入文件，对比两进程，确认是哪个实例/实体出现种子差异。
- **实验 C（根治候选）**：在基线帧对上述所有 `DXRandom` 实例**按 `(实体ID, 字段名)` 重新播种**（反射调用私有 `init_genrand(uint)`），使载入顺序彻底无关。
- **实验 D**：确认载入期间是否有非 `DXHelper_Task` 的线程创建；可临时给 `DXRandomCtorPatch.Prefix` 记录 `Thread.CurrentThread.ManagedThreadId` + 调用栈前 3 帧，对比两进程。

### 1.5 请回答
1. 上述 4 个实验中，你建议先做哪一个？是否有更直接的定位办法（例如给 `DXRandom.init_genrand` 加日志并统计调用序列哈希）？
2. 实验 C 的“按实体 ID 重种”是否有语义风险（有些 RNG 可能与存档时序耦合，强行重种反而引入分叉）？有没有更安全的等价方案（如把所有实体随机改为**按需惰性创建**并统一播种）？
3. 若最终无法消除二值位，是否可接受“回放验收只比较 `life/proj/prod/resv/经济`，`pos` 作为已知残差”并把门禁改为“分叉实体数/最大位移阈值”？请给出你认为合理的验收标准。

---

## 2. 【阻塞 P2】Client 收到快照后无法真正加载（引擎淡入淡出把加载吞掉）

### 2.1 现象
双实例（loopback，无 Chaos）实测：
- Host：`Entered synced level at tick 31904` → 导出并广播快照 38.8KB / 2 chunks → 正常持续 tick。
- Client：握手成功 → `Receiving Snapshot` → 落盘 `coop_client_sync.zxsav` → 调 `LoadSaveFileAndEnter` **仅 5ms 返回** → 游戏仍停在主菜单（`tick=0, levelLoaded=false`），日志无 `InitLevel`、无异常、无加载完成回调。

### 2.2 反编译确认的调用链
`ZXGame.#=z9IpK6P4YlW8GIY7olg==(path)`（`ZXGame.cs:1483`）调用 `#=z9DPDdq9qP9lZ(20, loadAction, finishAction, null, true)`（`ZXGame.cs:1911`）：

1. `#=z9DPDdq9qP9lZ` 设置 `Paused=true`，调用 `#=zXAMaTPI=(true, cb1)`（`ZXGame.cs:1918`）。
2. `#=zXAMaTPI=`（`ZXGame.cs:2189`）：
   - `if (Scene == null) return;`（我们实测调用前 `DXGame.Current.Scene != null`）
   - `if (#=zbvwh$ickzalV.Scene != null) return;`（我们实测该 fade 对象的 `Scene == null`，提前返回分支不会命中）
   - 创建 `DXAnimation`，`OnFinished → InvokeOnStartFrame(cb)`，把黑屏 `DXObject` `Scene.AddObject`，播放 `#=zOuu84gHcjRar`(≈2000ms) 淡入动画。
3. **加载动作依赖动画完成**：`cb1` 里设置 `IsLoading=true`，再调用 `#=zXAMaTPI=(false, cb2)`；`cb2` 才 `Dispose` 当前系统并 `Task.Factory.StartNew(loadAction)`；加载完成后再走一段淡出。
4. 因此只要 `DXAnimation` 不推进，`loadAction` 永不执行。

### 2.3 已尝试且回退的方案
- 用 Harmony Prefix 劫持 `#=zXAMaTPI=`，在 `LoadFadesToSkip>0` 时直接 `InvokeOnStartFrame(__1)` 跳过三段淡入淡出。
- 结果：3 段回调都被执行（日志有 `Skipping load fade (remaining=2/1/0)`），但**加载任务仍未开始**（无 `InitLevel`），推测 `loadAction` 在任务里抛异常后被引擎 `catch when (!IsDebuggerAttached)` 静默吞掉；也可能是在 `CurrentGameSystem` 自己的 StartFrame 调用链里同步 `Dispose` 造成重入/坏状态。
- 该实验已完全回退，不在当前代码中。

### 2.4 需要弄清的关键事实（未知）
- `DXGame.Scene` 与 `DXScene.Current` 在主菜单是否**同一个对象**？（若是不同对象，黑屏 fade 被加到不参与更新的场景，动画自然不会推进。）
- 主菜单期间 `DXScene.Paused`（`DXScene._Paused`）是否为 true？为 true 时 `DXScene.Update` 会跳过 `o.Animate`。
- 后台/非激活窗口是否导致 `ComputeSceneAvance()` 返回 0（`DXScene._ElapsedTime == 0` → 动画不推进）？我们曾观察：Host 在后台仍会因我们的 `UpdateGameLogic` 补丁推进 tick，但这是否代表渲染/动画循环也在跑，尚未确认。
- 从网络包回调（`OnFrameTick → PollEvents → OnPacketReceived`）里调用 `LoadGameAndEnter` 是否属于引擎不支持的重入场景？菜单 UI（Continue/CO-OP 大厅）是在哪个阶段调用它的？

### 2.5 请回答
1. 你建议优先验证哪个事实？给出最小验证代码/反射字段名（如 `DXScene._Paused`、`DXGame.Scene` 与 `DXScene.Current` 的引用相等性、`DXScene._ElapsedTime`）。
2. 是否有比“跳过 fade”更安全的接接口：例如 (a) 强制驱动 `DXAnimation.Advance` 固定步长直到完成；(b) 把加载请求延迟到“主菜单完全空闲后的某一帧”再调用；(c) 复用主菜单 Continue 按钮的 `Activated` 回调路径；(d) 直接调用 `#=z9DPDdq9qP9lZ` 的第二段 `#=zXAMaTPI=(false, cb2)` 并自行保证前置状态？
3. 如果必须在“改引擎加载流程”与“改会话协议（Client 不读快照、改为双方各自从同一存档启动）”之间选择，你倾向哪一个？为什么？

---

## 3. Chaos 双实例下 Host 曾 CLR 栈溢出（`0xc00000fd`）

- 场景：`TABCOOP_CHAOS='latency=120;jitter=30;loss=0.02;seed=42'`，Host+Client 双开，Host 读档并广播快照。
- Windows 事件日志：`APPCRASH TheyAreBillions.exe / clr.dll 4.8.9310.0 / c00000fd`（STACK_OVERFLOW），发生在启动后约 46s、Host 已进入 `EnterSyncedLevel` 并完成一次 `LevelLoadComplete` 之后。
- 未能复现：Host 单开 75s 稳定；双开**无 Chaos** 100s 稳定。当时 Client 卡在快照加载（问题 2），Host 独自运行。
- 疑点：
  1. 新增的 `TurnInputTickGate` + 每 tick 广播心跳是否在某种条件下递归（`FlushDueTurnCommands` 内执行命令 → 命令回调再次进入 `UpdateGameLogic`）？
  2. `DeterministicTaskPatch` 顺序化 `For` 后，嵌套 `For` 是否存在深层递归（物理回调里再触发 `For`）？
  3. 引擎 `IsMultiplayer=true` 路径本身是否有递归（例如网络玩家列表/同步回调）。
- 需要的帮助：如何在 **.NET Framework 4.8** 进程上抓托管栈（`procdump -ma -e`? `clrmd`? Visual Studio attach?），以及建议在哪些函数加“进入/退出计数”或深度 guard 来定位。

### 请回答
1. 推荐的最小侵入式栈溢出定位工具与命令（该游戏是 x64 桌面 .NET Framework 4.6 运行在 4.8 CLR 上）。
2. 基于上述调用链，你最怀疑哪一处递归？建议加 guard 的位置与形式。

---

## 4. 6666.zxsav 验收超时（单线程载入的代价）

- 为消除回放的载入期不确定性，`DeterministicTaskPatch` 在**确定性模式激活期间（含 LoadingLevel）**把 `DXHelper_Task.For` 三个重载全部改为顺序执行。
- 1_Backup（318KB）载入约 5s，正常；6666.zxsav（977KB，3.3 万实体）载入 >180s，`run_replay_verify.ps1` 默认超时被触发，未完成 P0 门禁中的 6666 项。
- 可选方案：
  1. 只对 `ForRange(Action<int,int>)`（物理两段）顺序化，`ForIndex` 在载入期保持并行；
  2. 载入期并行 + 问题 1 的“载入后统一重种/定位”方案双管齐下；
  3. 仅提高验收超时（例如 600s）；
  4. 其他你认为更合理的方案。

### 请回答
1. 哪个方案对“确定性”与“载入时间”的平衡最好？是否会影响问题 1 的二值分叉？
2. 若采用方案 1，请指出哪些 `For` 重载会被反序列化路径使用（可从 `DXHelper_Task.For` 的调用点/闭包特征判断），以及如何验证载入期并行不会引入新的 `new DXRandom()` 顺序问题。

---

## 5. 已知但暂不阻塞的边界（供审阅者确认优先级）

1. **暂停不真正冻结 tick**：确定性沙盒的 `UpdateGameLogic` Prefix 每帧强制推进 `GameTime`，`DXGame.Paused`（`GameSpeed<=0`）无法阻断本补丁。当前 `SetPaused` 命令保证双端同一 `TargetSimTick` 应用，但世界继续推进。若要真正冻结，需要把暂停改成“gate 级控制包”（暂停后停发心跳/停止 tick，恢复时 Host 单独下发）——这会不会与 P2 的 turn gate 产生死锁？请给设计建议。
2. **中途加入/Resync 的实体 ID**：快照写入时 Host 校准 `maxId+1000`，Client 载入后校准同一值；但 Host 在“快照生成 → Client 载入完成”之间新生成的实体 ID 仍可能与 Client 追帧分配错位。是否应改为“快照期间 Host 暂停生成”（现有代码只暂停了帧，不阻止逻辑生成）？
3. **`DXRandom` 渲染/物理分流**：我们把非逻辑 Global 抽签拆成 `aux`(物理) 与 `render`(渲染) 两条固定种子流。审阅者是否认可这种拆分？有没有可能某些**模拟相关**的随机抽签发生在渲染回调里，从而被错误归入 render 流？

---

## 6. 复现命令与日志位置

```powershell
# 部署（需先关闭游戏进程）
powershell -ExecutionPolicy Bypass -File scripts/install_mod.ps1

# P0：两进程确定性回放
powershell -ExecutionPolicy Bypass -File scripts/run_replay_verify.ps1 `
  -SavePath "%USERPROFILE%\Documents\My Games\They Are Billions\Saves\1_Backup.zxsav" -Ticks 120

# P1：控制模式（MCP 端口 27901）
powershell -ExecutionPolicy Bypass -File scripts/run_game_with_control.ps1 `
  -SavePath "%USERPROFILE%\Documents\My Games\They Are Billions\Saves\1_Backup.zxsav"
# 然后调用 MCP：reveal_map / set_camera / get_camera / screenshot

# P2：Chaos 双实例
powershell -ExecutionPolicy Bypass -File scripts/run_chaos_two_instance.ps1 `
  -SavePath "%USERPROFILE%\Documents\My Games\They Are Billions\Saves\1_Backup.zxsav"
```

- 游戏日志：`<SteamDir>\steamapps\common\They Are Billions\TABCoopMod.log`
- 回放产物：同目录 `coop_replay_run1/2.timeline`、`coop_pos_run1/2.txt`、`coop_loadpos_run*.txt`、`coop_baselinepos_run*.txt`
- 本轮实测的 A/B 轨迹对（供对比）：
  - `0xB91467F9808786EF` / `0x5684C497E797BDF1`
  - `0x71EBA50C75638BC8` / `0x8D544B5BD902FEDE`
  - `0xFC32EA24594955ED` / `0xF391D57F30452F2E`
