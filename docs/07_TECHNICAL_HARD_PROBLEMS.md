# 07 — 技术难点搁置清单（转交更大参数模型）

> **创建时间**: 2026-11-09
> **用途**: 本轮 Phase 1 收尾实现了"自动化确定性验证 + 网络故障注入"的**地基**。以下问题被判定为高风险/需更深引擎知识，**本轮刻意不做**，在此完整记录，供后续更大参数模型或人工专家接手。
> **前置阅读**: `docs/02_ARCHITECTURE_DESIGN.md`、`docs/04_NETWORK_PROTOCOL.md`、`docs/06_HANDOVER_STATUS.md`、`.claude/skills/tab-lockstep-sync/SKILL.md`。

每条包含：现象 → 为何难 → 已知约束/已尝试 → 建议方向 → 涉及文件 → 验证方式。

---

## 0. 【本轮实证结论】存档加载本身不确定（最重要的新信息）

本轮把自动化测试台跑通后，**两进程各加载同一个 `test.zxsav`、跑同一脚本、在同一基线 tick 454 开始记录**，结果为：

| 项 | run1 | run2 |
|---|---|---|
| 基线 tick | 454 | 454 |
| 实体数 | **43108** | **43025**（差 83） |
| 金/木/预留 | 100 / 20 / 78000234 | 100 / 20 / 78000234（相同） |
| pos / life 校验和 | 不同 | 不同 |
| `coop_replay_result` | FAIL @ index 0 (`entities,pos,life`) | — |

**结论**：固定步长时钟本身工作正常（两跑 tick 序列一致、经济量一致），但**存档加载阶段生成的实体集合不可复现**。已尝试并排除/覆盖：
- `DXGame.IsIADeterministic = true`（单线程实体更新）
- 在 `LoadGameAndEnter` **之前**就启用确定性沙盒（`DeterministicReplayVerifier.PrepareForLoad`）
- `DXGuid.New` 双轨分流 + `LockstepClock.LoadingLevel` 让加载期也用同步 ID
- `DXRandom.Global` 返回共享定种梅森旋转（加载期通过 `LoadingLevel` 生效）

**仍分叉 ⇒ 嫌疑源（需更大模型深入）**：
1. `new DXRandom()` **无参构造函数**未被 Hook（原版用 `ThreadId * Ticks` 定种，文档 §3.4 提过但我们只 Hook 了 `DXRandom.Global`）。这是最可能的元凶。
2. 游戏内部直接用 `System.Random` 或 `DXMath` 随机做僵尸/感染点生成。
3. 加载期用 `DXHelper_Task.For` **并行**创建实体 → 创建顺序/ID 分配不确定。
4. 存档反序列化本身按字典/哈希表顺序重建实体。

**已确认的源码事实（ilspy）**：
- `DXRandom()` 无参构造函数：`init_genrand((uint)ThreadId * (uint)(DXHelper_Environment.Ticks * 100000.0))` —— 明确不确定。
- `DXRandom.Global` getter 首次访问：`_Global = new DXRandom(_GlobalSeed + new Random().Next())` —— 也含 `System.Random`。
- 我们只 Hook 了 `DXRandom.Global`（且仅在 `InDeterministicContext || LogicTickActive || LoadingLevel` 时返回同步实例）；**未 Hook 无参构造函数**，也**未拦截 `new System.Random()`**。

**本轮尝试与失败教训（务必读）**：
- 曾试 `[HarmonyPatch(typeof(DXRandom), ".ctor", new Type[0])]` 前缀改写无参构造函数，结果 Harmony 报 `Patching exception in method null`，**整个 `DeterministicSandboxPatches` 类被跳过（failed-tolerant=1），导致 DXGuid/DXRandom/UpdateGameLogic 门控/LoadPhysics 全部未生效**。已回退。
- **教训**：给 `DeterministicSandboxPatches` 增加高风险 patch 前，务必放进**独立 patch 类**，避免一损俱损。构造函数 patch 建议用 `[HarmonyPatch(typeof(DXRandom), MethodType.Constructor, new Type[0])]` 或改为直接 Hook 私有 `init_genrand(uint)`；并把 `init_genrand` 通过 `AccessTools.MethodDelegate` 缓存以避免热路径反射开销。

**追加实验（会话2 续，均已落地但**未**解决分叉）**：
1. **独立 `DXRandomCtorPatch`**（`Determinism/DXRandomCtorPatch.cs`，`AccessTools.Constructor(typeof(DXRandom), Type.EmptyTypes)` + 私有 `init_genrand` 反射重定种）：独立类干净应用（`patched 1 method`，不影响主类）。实体数由 43108/43025 变为 **43159/43228**——说明无参构造确实参与，但**不是唯一来源**。
2. **强制实体列表按 ID 排序**（`GameBridge.SortEntitiesById` 反射 `DXGame._Entities` 排序）：日志确认 `Re-ordered 9714 entities`，但两跑实体数仍为 **9655/9636**。
3. **基线改为"载入后固定 20 tick"**（减少模拟累积），两跑基线 tick 完全一致（`test.zxsav` 454；`1_Backup.zxsav` 31925）。
4. 关键观察：两跑 **`proj`(投射物) 与 `resv`(预留) 校验和完全一致**，金/木/石/铁/油一致；**仅 `entities` 数量与 `pos`/`life` 分叉**。即经济与投射物已确定，分叉集中在**单位/僵尸的实体级逐 tick 模拟**（生成/死亡数不同 → 位置与生命和随之不同）。

**据此更新的结论**：这不是"装载顺序"问题，而是**每 tick 模拟中存在未接管的随机/并行源**。建议更大模型按以下顺序排查：
- 逐个 Hook `ZXSystem` 子类的 `OnStartUpdate/OnFinishUpdate`，每 tick 记录 `_Entities.Count`，定位**第一处实体数分叉的 tick 与系统**。
- 检查 ZX 系统内部是否用 `DXHelper_Task.For`（并行）或后台寻路（`DXWorldGrid`、`PendingPathfindingCount`）——结果到达顺序不确定。
- 检查是否有代码把 `DXRandom.Global` **缓存为字段**（缓存后我们的 getter patch 不再生效）；以及是否有 `new System.Random()` / `DateTime.Now` / `Guid.NewGuid`.
- 检查僵尸生成/感染潮是否走引擎时间轴之外的线程。
- 可考虑**更极端的验证存档**：暂停态静态存档（无活动僵尸）应能 PASS；若静态也 FAIL，则问题在更底层的实体 Update 顺序/浮点。

**进一步收敛（会话2 末尾，重要）**：在上面的基础上再做了一步——**DXGuid 在 `LoadingLevel` 期间不再走同步低位 ID**（否则加载期临时/UI 对象也被计入实体、数量随进程波动），保留 `LoadingLevel` 仅作用于 `DXRandom`。此时 `1_Backup.zxsav` 两进程对比：

| 字段 | run1 | run2 |
|---|---|---|
| tick | 31925 | 31925 |
| **entities** | **3017** | **3017**（一致） |
| gold/wood/stone/iron/oil | 355/25/55/8/0 | 相同 |
| **life / proj / prod / resv** | 相同 | 相同 |
| **pos** | 6357559823203120965 | 6357559884412218280（**不同**） |

即 **实体集合、生命、投射物、生产队列、预留、经济全部确定，只剩 `pos`（位置）分叉**。这把问题精确锁定为**每 tick 的移动/物理位置更新**。

**又试过的负优化（已回退）**：全局串行化 `DXHelper_Task.For` 三个重载（独立类，`patched 3 methods` 应用成功），结果 entities/life 反而重新分叉——说明全局串行破坏了场景/物理更新的既有语义，**不能全局做**。文件已删除。

**下一步（给更大模型）针对 `pos` 的精确排查**：
- 在第一个分歧 tick 对两跑导出**逐实体位置快照**，看是**全部移动单位**偏移还是仅**僵尸/寻路单位**偏移；偏移是**亚像素(浮点/顺序)**还是**整格(寻路)**。
- 只针对 `DXPhysicWorld.UpdateDefaultTime` 内部的 `For(...)` 做**定点**串行化（而非全局），并确保 `DXPhysicWorld.Bodies` 顺序确定（可能需按实体 ID 排序）。
- 检查 `DXWorldGrid` 寻路结果到达顺序（异步 pathfinding）与单位 `CMovable` 是否用**真实时间**积分速度（若用 `DXHelper_Environment.Ticks` 而非固定 dt，则必须改）。
- 若静态暂停存档可 PASS、活动存档仅 pos 分叉，则基本可判定为寻路/物理并行残留。

### 0.1 【位置 diff 诊断结论 — 会话2 最终】移动积分不确定

用新增的 `scripts/compare_pos.ps1`（`TABCOOP_POS_DUMP=1` 时在首个记录 tick 导出逐实体坐标）对 `1_Backup.zxsav` 两跑对比：

- 两跑 **实体 ID 集合完全一致（3017）**，经济/life/proj/prod/resv 一致。
- 位置不同的**只有移动中的单位**：`ZombieMedium/Worker/Dressed/Weak*` 与 `Worker_A/Worker_B`(市民)；**静态建筑、未移动单位位置完全一致**。
- 偏移量随经过的 tick 数**累积增长**：RNG 重置后 1 tick ≈0.16~0.28；约 95 tick 后 ≈1.0~1.24（亚格→整格级），方向各异。

**已做的修复尝试与结果**：
- 新增 `LockstepClock.ResetBehaviourRandom`：反射把 `DXBehaviour<BData>._GlobalRandom` 重新赋值为 `new DXRandom(seed)`（源码：`private static int _Seed = DXRandom.Global.Next(); public static DXRandom _GlobalRandom; GlobalRandom { if null => new DXRandom(Seed) }`）。已确认日志生效，但**仅把它在基线点重置不够**（基线前已漂移）；改为在 InitLevel 重置后仍漂移 → 说明**不只是行为树 RNG**。
- 结论：剩余分叉是**逐 tick 的移动/位移积分本身不确定**——极可能是移动/路径推进按**真实流逝时间**（`DXHelper_Environment.Ticks` 差值或物理 `OnNewPositionComputed` 插值）而不是固定 `GameTime` 步长 33.33ms，导致每 tick 位移随帧时长波动并累积。

**给更大模型的精确切入点**：
1. 反编译 `ZX.Components.CMovable` / `ZX.Commands.Travel` / `ZX.Behaviours.ZXBehaviour` 中的移动推进，确认位移是否用 `DXHelper_Environment.Ticks`/真实 dt；若是，改为按 `DXGame.Current.GameTime` 固定步长积分（或强制每 tick `dt = 33.333ms`）。
2. 检查 `DXPhysicBody.OnNewPositionComputed` 与 `FactorTimeForInterpolation`（`UpdateDefaultTime` 里用 `GameTime` 计，但插值可能引入真实时间）。
3. 检查 `BData` 是否带**每实体随机状态**（生成时以非确定种子初始化），需在加载后按实体 ID 重置。
4. 验证手段：`scripts/run_replay_verify.ps1`（两进程）+ `compare_pos.ps1`；目标是把 `moved(>0.01)` 降到 0。

**建议下一步**：
- 反编译存档加载链：`ZXGame.#=z9IpK6P4YlW8GIY7olg==`（`Method_ZXGame_LoadGameAndEnter`）→ 反序列化 → `ZXLevelState` 重建 → 僵尸/感染点生成。
- Hook `DXRandom` 的**所有构造函数**（尤其无参）与 `DXRandom.Mt` 状态复位；grep 反编译源码里所有 `new System.Random` / `DateTime.Now.Ticks` / `Guid.NewGuid`。
- Hook 加载期的实体创建入口，强制串行 + 同步 ID。
- 验证方法即本轮工具：`scripts/run_replay_verify.ps1`（两进程）+ `scripts/compare_hashes.ps1`，修好后应 `compare PASS`。

> 另：即使加载确定，`CoopSessionManager` 里客机加载房主快照后也必须在 `LoadSaveFileAndEnter` 前设置 `LoadingLevel=true` 并在 `InitLevel` 后置回 false，否则客机加载态同样会分叉。

### 0.1 追加（会话3）：生存档 `6666.zxsav` 的分叉比战役档更严重

用新 MCP/控制台设计与两进程 `run_replay_verify.ps1`，在**生存**中后期档 `6666.zxsav`（tick 168485，玩家实体 ~1020）上重跑 120 tick：

```
[compare] FAIL at index 0 (tick 168601): [entities,pos,life,proj]
[pos] run1=7911 entities, run2=7920 entities
[pos] ID set differs: onlyInRun1=18, onlyInRun2=27
[pos] common=7893, moved(>0.01)=3761, maxDx=65.93, maxDy=151.68
[pos] top moved: ZombieDressedA 908, ZombieWeakC 428, ... Worker_A 160
```

对比：**战役档 `1_Backup` 只暴露 `pos`；生存档额外暴露 `entities/life/proj`**。

含义（重要）：
- 战役档是"较容易"的用例，**仅凭战役 PASS/only-pos 会低估问题**。生存模式下还有**实体生成/死亡层面的不确定**（尸潮/僵尸生成、僵尸 AI、投射物/战斗结算）。`prod/resv/经济` 仍一致。
- 因此后续确定性工作（hook 无参 `DXRandom`、加载并行、波次生成/死亡 RNG）**必须在生存档验证**；`6666.zxsav` 建议作为主测试档。
- 附带观察：控制模式（不启用确定性沙盒）载入时 `entityCount≈30618`，而回放模式（载入前启用沙盒）两跑均为 ~7911/7920。两者是不同载入路径，实体总量差异很大——回放内部两跑可比，但需注意不要跨路径比较。

> 结论：`docs/07 §0` 的"加载不确定"与本节"逐 tick 模拟不确定"在生存档同时存在；战役档只是恰好把后者收敛到 `pos`。

---

## 0.6 【会话3 实证】运行时“移相机 / 开全图 / 截交互区”遇到引擎阻力

**目标**：让分叉可视化 HTML 的两张同 tick 截图看向“有单位交互的区域”，并去除迷雾。

**已确认可行**：
- 控制模式（非确定性）下，`reveal_map`（`ZXLevelState.ShowFullMap=true` + `ZXSystemMapLayers.FogEnabled=false`）能把一片**地形**从全黑变为可见（对照组：同相机、未开图=全黑，开图=地形）。
- MCP `set_camera`（改 `DXScene.CameraArea`）在该帧附近能改变画面。

**已确认不可行 / 受引擎限制**：
1. **确定性回放里截图不跟随相机/开图**：`run_replay_verify` 中 `SetShowFullMap/FogEnabled` 返回 True、`CameraArea` 读回也是新值，
   但 `DXGame.CaptureScreen()` 得到的帧仍是**载入时的默认相机**，开图也不改变该帧。额外显式调用 `DXGame.SetCamera()` 无效。
   → 回放的渲染管线/呈现被确定性模式影响，截图不可用来自定义视角。
2. **控制模式下游戏会每帧重控相机**：`set_camera` 之后约 1 秒再 `get_camera`，`CameraArea` 已被改回（实测 x58,y75 → x138,y54），
   且 `CameraArea` 会被夹紧（请求 110×69 → 实际 72×45，且位置被挪）。→ 无法稳定固定视角。
3. **即便开图，敌人仍可能不渲染**：`FogEnabled=false`/`ShowFullMap=true` 后地形可见，但僵尸在“未注视”区域仍不可见
   （`ZXSystemMapLayers` 的敌人渲染分支似乎仍依赖 `hideEnemiesIfNotWatched`/“已探索”标记），导致截图常是空林地。

**建议方向（给更大模型）**：
- 反编译 `DXSystemMapLayers`（`#=zJme8KFhmikprnkg_2CDeiQE=`）的可见性/敌人渲染分支，找到 `hideEnemiesIfNotWatched` 与 `LayerFog` 的“已探索”填充逻辑，
  直接把这些格标记为已探索/已可见，从而真正显示僵尸。
- 找到每帧写 `DXScene.CameraArea` 的源头（输入/相机控制器），临时停用或直接设置其“目标相机”字段，使自定义视角稳定。
- 或研究：在截帧前临时 `DXGame.IsIADeterministic=false` 并强制一帧渲染/呈现，再 `CaptureScreen()`。

**现阶段结论**：**世界坐标散点/箭头图是“分叉在哪”的可靠依据**；截图目前只能尽力而为，不能保证落在指定交互区并显示敌人。

### 待研究清单（TODO，交更大模型）

- [ ] **R1 回放截帧不跟随相机/开图**：`DXGame.CaptureScreen()` 在 `IsIADeterministic=true` 下返回的帧似乎是载入时默认相机那一帧。尝试：截帧前临时 `DXGame.IsIADeterministic=false`，强制一帧渲染/呈现（`DXGame.SetCamera`/`FinishScene`/`Present`）后再 `CaptureScreen`；或定位 `CaptureScreen` 的数据来源（是否离屏 RT 未随相机重绘）。
- [ ] **R2 相机每帧被游戏重控**：定位每帧写 `DXScene.CameraArea` / `ZXLevelState.LevelCameraArea` 的源头（输入边缘滚动、相机控制器、存档恢复），提供“锁定相机”开关或直接设置其目标字段，使 `set_camera` 稳定。
- [ ] **R3 开图后敌人（僵尸）仍隐藏**：反编译 `ZXSystemMapLayers`（`#=zJme8KFhmikprnkg_2CDeiQE=`）敌人渲染分支，弄清 `hideEnemiesIfNotWatched` 与 `LayerFog` “已探索/已可见”标记；直接把这些格标记为已探索，或找出真正控制敌人可见性的开关（`ShowFullMap`/`FogEnabled` 只解决地形）。
- [ ] **R4 CameraArea 夹紧/位移规则**：弄清 `CameraArea` 被夹紧的约束（`LevelCameraArea`/地图边界/1.6 宽高比/最大缩小 72×45），使 `set_camera` 结果可预测。
- [ ] **R5 真实两跑画面**：上述修复后，在回放截帧里采集每跑真实画面（当前截图仅作背景参考）。
- [ ] **R6 低 ID 实体/投射物 ID 复用**：分叉报告里大量 `DXEntity`(≈100000+) 与低 ID 投射物呈“巨幅位移”，疑为跨跑 ID 复用（同 ID 指向不同对象）。研究 ID 分配/复用与装载顺序，或在报告里可靠地区分“真实位移”与“ID 复用假位移”。

> 说明：以上均为**运行时可视化/取证**问题，与 §0.1 的模拟确定性解耦；修复后可用于人工/视觉复核，但不改变同步逻辑本身。

### 0.7 【会话3 调研】随机数种子与确定性设置

**引擎 RNG 结构**（`DXVision.DXRandom : System.Random`，MT19937）：
- `DXRandom.Global`：`_Global = new DXRandom(_GlobalSeed + new Random().Next())` —— 默认**非确定**（我们已 patch 为同步实例）。
- `DXRandom()` 无参构造：`init_genrand(ThreadId * Ticks*100000)` —— 默认**非确定**（我们已 patch 为定种）。
- `DXRandom(int seed)` / `Initialize(int seed)`：可显式定种；`Mt`（uint[624]）可 get/set，但私有 `mti` 不可设，故**整状态恢复不完整**，建议按 seed 重播种而非拷 `Mt`。
- `DXLevel.Random`：`public DXRandom Random { get; set; }`，默认 `new DXRandom()`（非确定）。**可直接赋 `new DXRandom(seed)`**，控制关卡级随机（如市长候选人抽取用的就是它）。
- `ZX.GameSystems.ZXRandomLevelParams.Seed`：**生存地图生成种子**（public int，get/set），默认 `DXRandom.Global.Next()`。载入存档会带上该 seed；要两端一致，必须同存档/同 seed。
- `DXGame.IsIADeterministic`：只控制**单线程实体更新 + 同步物理**，**不是 RNG 定种开关**。

**结论/建议**：
- 没有“一个总开关”让全引擎确定性随机；需分别接管：`DXRandom.Global`（已）、无参 `DXRandom()`（已）、`DXLevel.Random`（建议在载入后赋定种实例）、生存 `Seed`（同存档即可）。
- 可选新增控制工具 `seed_rng {seed}`：`DXLevel.Current.Random = new DXRandom(seed)` + 重播 `DXRandom.Global`，用于可复现实验；`Mt` 可读用于哈希。

---

## 0.5 【已解决 · 会话3】指令注入撞到内部类型的 TypeAccessException

**状态：已修复并实机验证（2026-11-09 会话3）。**

### 原问题
`GameBridge` 与 `StateHasher` 已改为纯反射/`object` 并通过。但 **`Commands/NetCommand.cs` 的 `CommandExecutor` 与 `Testing/ScriptedCommandDriver` 仍强类型引用内部类型**，实机执行时报：
- `ExecutePlaceBuilding`：`as ZXEntity` / `Build` 强类型 → `TypeAccessException: 尝试访问类型 ZX.Entities.ZXEntity`
- `GameBridge.GetCommandGeneric`：`as ZXCommand` → `TypeAccessException: 尝试访问类型 ZX.Commands.ZXCommand`
- `ScriptedCommandDriver` 的 `Travel` / `Train` / `CMovable` / `CCommandable` 同理。

这仍是铁律#3：`ZXEntity`/`CCommandable`/`CSelectable`/`ZXCommandTarget`/`Travel`/`Train`/`Build`/`CMovable`/`ZXLevelState` 全部 `internal`。JIT 对内部类型做 `castclass/isinst`（以及用内部类型实例化泛型方法）会抛 `TypeAccessException`。注意：`typeof(内部类型)` / `AccessTools.Method(typeof(...))` 本身**不**抛（`ldtoken` 允许），真正致命的是**cast/isinst/泛型实例化**。

### 已实施的修复（全部走 `object` + 反射，不再写死任何内部类型名）
- **`Core/GameBridge.cs`**
  - `GetCommandById/ByName/GetCommandGeneric` 返回 `object`，不再 `as ZXCommand`。
  - `ExecuteCommandOnEntity(object cmd, object executor, object target)`、`AssignGroupMoveTargets(List<DXEntity>, object cmd, PointF)`、`PlaceBuilding(object ...)` 全部改为 `object` 入参（`MethodInfo.Invoke` 内部完成装箱/转型，我方代码不做 cast）。
  - 新增 `HasComponent(DXEntity, Type)`：扫描 public `DXEntity.Components` 列表 + `Type.IsInstanceOfType`，替代会抛异常的 `HasComponent<T>()` 泛型实例化。
  - 新增 `CreateCommandTarget(PointF, SizeF, PointF, ulong)`：用 `Activator.CreateInstance(ZXCommandTarget)` 造箱式结构体，再用反射属性 `SerPosition/SerSize/SerOrigin/SerEntityRef` 就地改写；`SerEntityRef` 通过 `DXEntityRef<ZXEntity>.MakeGenericType` + `Entity` 属性反射填充。
  - 新增 `GetCommandableTargetObject`（反射 `CCommandable.Target`）、`GetCommandId`（反射 `ZXCommand.ID`）、`CancelCurrentOrderObject` / `CancelQueueSlotObject`、`GetBuildCommandForType` / `GetTrainCommandForType`。
  - 内部类型一律从已绑定的 `MethodInfo` 反推（`_mCmdExec` 的参数类型即 `ZXEntity`/`ZXCommandTarget`），或 `GameAssembly.GetType("ZX.Components.CMovable")`，避免新增 `typeof(内部类型)`。
- **`Commands/NetCommand.cs`**：`CommandExecutor` 全量 `object` 化；`BuildCommandTarget` 返回 `object`；移除 `ZXEntity/ZXCommand/ZXCommandTarget/CMovable/CCommandable/Build` 的强类型引用与 `using`。
- **`Testing/ScriptedCommandDriver.cs`**：`Travel`/`Train` 用 `GameAssembly.GetType("ZX.Commands.Travel"/"Train")`；组件判定改 `GameBridge.HasComponent(..., TypeCMovable/TypeCCommandable)`；`as ZXCommand` 改为 `GetCommandId(object)`。

### 实机验证结果（`1_Backup.zxsav`，120 tick，两进程各跑一遍）
```
[Harmony] Classes OK=15, failed-tolerant=0        (0 missing bindings)
[ScriptDriver] tick 32010 inject 'build_tent'    (无异常)
[ScriptDriver] tick 32030 inject 'move_units'    (无异常)
[ScriptDriver] tick 32060 inject 'train_ranger'  (无异常)
ERROR 计数 = 0 / WARN = 0
```
- 两遍日志中 `CommandId` 完全一致（`Travel=1952427338`、`Train=584315074`），说明命令单例 ID 在确定性沙盒下稳定。
- `compare_hashes.ps1` 首个分叉仍是 tick 32001 的 `pos`（在任何注入发生之前）→ **本次修复没有新增任何分叉类别**；`life/proj/prod/resv/经济` 仍一致。残留的 `pos` 分叉属于 §0.1（移动积分不确定），与本项无关。

### 捕获侧实测补充（同一会话）
另跑一遍 `TABCOOP_INTERCEPT=1`（60 tick，AI 指令持续流经被 `Patch_ZXCommand_Execute` 补丁的 `ZXCommand.#=zfmgzkuw=` 前缀）：**日志无任何 `TypeAccessException`/`MethodAccessException`/ERROR**。说明该 Prefix 方法的 JIT（含 `is Build`/`is Travel`、`__0.Team`、`__1.SerEntityRef`、`HasComponent<CMovable>()`）在当前 CLR 下并未抛类型访问异常。因此捕获侧暂**无需**反射化改造；仍需人工以真实鼠标输入验证「捕获→序列化→回放」闭环的功能正确性（自动化无法代替点击）。


---

## 1. 真·网络帧同步门控（Turn Window + TargetSimTick 缓冲执行）★★★ 最高优先级

**现象**：当前 `CoopSessionManager` 是指令"到达即执行"（execute-on-receipt）。`NetCommand.TargetSimTick` 被序列化但**从未被读取**。即便两端都开启 `IsIADeterministic` 且固定步长，只要网络延迟使同一条指令在两端落在不同 `SimTick` 执行，结果必然分叉。

**为何难**：真正的 lockstep 需要：
1. Host 每 `ControlTick`（30Hz）收齐所有 peer 的 `ClientInputReport`，合成 `TurnInputBroadcast`（即使空包也发心跳）；
2. 每个 peer 只在 `CurrentSimTick == TargetSimTick` 时执行该 tick 的指令；
3. 客机用 `TurnInputTickGate`（已在本轮 `Determinism/TickGate.cs` 留骨架 `MarkReady(int)`）阻塞 `UpdateGameLogic` 直到该 tick 的 turn 包到达；
4. 断线/迟到要有输入延迟（input delay）或 stall 策略，否则会卡死。

**已提供的骨架**：
- `Determinism/TickGate.cs`：`ITickGate` / `AlwaysReadyTickGate` / `ManualTickGate` / `TurnInputTickGate`。
- `LockstepClock.TickGate` 可插拔；`DeterministicSandboxPatches.UpdateGameLogic_Prefix` 已按 gate 决定是否步进。
- `CoopSessionManager` 已有 `TurnInputBroadcast` / `ClientInputReport` 封包收发与 `_hostTurnBuffer`。

**建议方向**：
- 在 `CoopSessionManager` 加 `Dictionary<int, List<NetCommand>> _pendingTurnCommands` 与 `FlushDueTurnCommands(int tick)`；`OnPacketReceived(TurnInputBroadcast)` 只入队不执行。
- Host 的 `OnFrameTick` 改为"每 ControlTick 广播并执行一次"，并用 `TurnInputTickGate.MarkReady(simTick)` 驱动本地 gate。
- 客机 `TurnInputTickGate.IsTickReady` 依赖收到该 tick 的 turn 包。
- 需要**卡帧保护**：若某 peer 掉线，不能无限等待；超时后按"空输入"推进或触发 Resync。

**验证**：用 `ChaosTransport`（本轮已实现）注入 100~300ms 延迟 + 抖动，双开跑脚本化指令，比对 StateHash 时间线；无门控必然 FAIL，有门控且输入对齐应 PASS。

---

## 2. 固定步长门控是否影响 UI / 物理（需实机确认）★★

**现象**：本轮 `UpdateGameLogic_Prefix` 采用"ready 时把 `GameTime` 精确置为 `LastGameTimeIAUpdate + 33.333333333333336`，否则夹到阈值下方"的策略，且 `LoadPhysics/LoadPhysicsOld` 前缀在确定性模式下直接 return false（关闭后台 `_TaskGameTime` / `_ThreadPhysic` 线程）。`UpdatePhysics()` 仍由 `SetCamera` 在 `IsIADeterministic` 下同步调用。

**已知副作用（本轮接受）**：
- **游戏速度 = 渲染帧率**：因每个渲染帧恰好推进 1 个逻辑 tick，60fps → 60 tick/s（原版 30/s，即 2 倍速）。对"确定性验证"无害（两跑一致），但正式联机必须改为"按真实累计时间量化步进"或"每帧最多 N 个 tick"。
- 若 `DXPhysicWorld.LastGameTimeUpdatePhysics` 跨重载残留，可能首帧物理狂步；本轮已在 verifier `ResetStaticsForRun` 里置 0，但正式流程需在 InitLevel 时统一重置。

**为何难**：需要确认 UI 动画/摄像机/选择框在 gate 阻塞时仍以渲染帧更新（它们走 `InvokeOnStartFrame`，预期不受影响，但未实测）；物理是否严格按 83.33ms 边界步进；是否有系统依赖 `CurrentFrameIAFactor` 插值导致观感问题。

**建议方向**：实机录屏确认固定步长下画面正常；若物理停滞，改为在 tick 步进时显式调用 `DXPhysicWorld.UpdateDefaultTime()`；若要恢复 30Hz 墙钟节奏，引入 accumulator（`realDelta += dt; while(realDelta>=33.33){ step(); }`）。

**涉及**：`Determinism/LockstepClock.cs`、`Determinism/DeterministicTickPatches`（当前并入 LockstepClock）、`DXVision.DXGame`（UpdateGameLogic/UpdatePhysics/LoadPhysics/SetCamera）。

---

## 3. 同进程二次载入存档的引擎安全性（已内置两进程回退）★★

**现象**：`DeterministicReplayVerifier` 主路径是"载入存档跑一遍 → 同进程再 `LoadGameAndEnter` 同一存档跑第二遍 → 比对"。但引擎的 `LoadGameAndEnter`（`ZXGame.#=z9IpK6P4YlW8GIY7olg==`）设计给主菜单进入用，**同进程重复调用可能泄漏旧 level/physics 单例或崩溃**。

**已实现缓解**：
- 看门狗：第二次 `InitLevel` 若 300 帧内未触发 → `ReplayVerifier FALLBACK_REQUIRED`。
- 两进程回退：`TABCOOP_REPLAY_RUN=1` 与 `=2` 各在独立游戏进程跑一遍，产出 `coop_replay_run1/2.timeline`，再用 `scripts/compare_hashes.ps1` 比对。

**建议方向**：研究 `ZXGame.ReturnToMainMenu`（`#=zqF2tlklT$8RE`，ObfuscatedSymbols 已有常量但 GameBridge 尚未绑定）做干净卸载；或确认 `LoadGameAndEnter` 内部是否会 reset `_Entities`/`DXPhysicWorld`/`ZXLevelState.Current`。若都不行，直接把两进程模式升为主线。

**验证**：看日志第二跑是否进入 `[ReplayVerifier] RUN 2 started`；若出现 `FALLBACK_REQUIRED` 则改两进程。

---

## 4. 同一 Steam 账号双开两个游戏进程 ★★

**现象**：`SteamP2PTransport` 与 `steam_appid.txt` 方案下，能否在同一台机、同一 Steam 账号同时运行两个 `TheyAreBillions.exe` 并在 Steam Lobby 内互相可见，尚未验证（Steam 可能限制同账号多实例）。

**建议方向**：优先用 `LocalTcpLoopbackTransport`（不需要 Steam 账号，已验证可用路径）；正式联机用两账号或两台机器。若必须同机，尝试 `-multirun`/不同 `steam_appid` 沙箱或直接放弃 Steam 走局域网 TCP 传输实现。

**涉及**：`Networking/SteamP2PTransport.cs`、`scripts/install_mod.ps1`（写 steam_appid.txt）。

---

## 5. Step 2：暂停 / 倍速 / 市长同步补丁（进行中）★

**会话3 进展**：`SetGameSpeed` 已补全——`CommandExecutor` 新增 `case SetGameSpeed`（写 `DXGame.GameSpeed`，0..20），`NetCommand` 增加 `GameSpeed` 字段并序列化，MCP 暴露 `set_game_speed {speed}`。实测：暂停(0)→3→1 生效，`game_state` 的 `gameSpeed` 与实际推进一致。
**市长接口已解析并实现 executor**：`ZXMayor` 有静态注册表 `Dictionary<string,ZXMayor> #=zoTuFkHI=`、`ID/Name/Level/Gender`，应用方法 `#=zPTw8CXGE0RqF(ZXLevelState)`；弹窗 `ZXGame.#=zcITiOSD6YNmD()` 按 `Level=MayorStates.Count+1` 用 `DXLevel.Current.Random` 随机选男/女，左右按钮分别应用。**设计：用 `MayorId` 传参应用**（`GameBridge.ApplyMayor`），避免两端重抽样；新增 MCP `list_mayors`/`mayor_select`。实测 level1 有 40 名，`mayor_select Mayor_001` 后 `MayorStates` 4→5。
**暂停恢复 3 秒倒计时**：`PauseSyncController`（横幅倒计时，引擎无专门倒计时渲染器）+ MCP `request_pause`；实测请求恢复→3→2→1→自动恢复。

**producer 拦截尝试与回退（重要教训）**：曾实现 `PauseSyncPatch`（Hook `DXGame.set_Paused`）与 `MayorSyncPatch`（Hook `ZXMayor.#=zPTw8CXGE0RqF`），补丁本身能应用（`Classes OK=17`），但**启用后游戏在载入期报错/退出**：`DXGame.set_Paused` 在关卡载入阶段就会被游戏调用，producer 前缀 `return false` 打断原始 setter，破坏载入路径（`TABCoopMod.log` 在“All Harmony patches applied”后即中断、无异常记录，属硬中断）。**已回退并删除这两个 producer 补丁**，恢复稳定（控制模式实测 `control-ready`、进程存活、`ping/get_state` 正常、0 错误）。
**不要**再直接 Hook `DXGame.set_Paused` 做 producer。后续正确方向：
- 改 Hook **输入层**（空格键/暂停按钮的输入处理），或仅在“会话已建立 + 关卡已稳定运行（`IAIterationInt>0`、`!LoadingLevel`）+ 显式 opt-in”时才允许 producer 生效；测试期（仅拦截、无会话）保持完全惰性。
- 市长同理：Hook 弹窗按钮的 `Activated` 回调（而非 `apply` 方法），或仅在会话中启用。
- 现有可用路径：`request_pause`（本地暂停/倒计时恢复）、`mayor_select`（按 ID 应用，确定性）、`set_game_speed` 均已实测通过。

**现象**：`NetCommandType.SetGameSpeed(7)` 与 `MayorVoteOrSelect(8)` 已定义且可序列化，但 `CommandExecutor.Execute` 的 switch **没有这两个 case**（静默 no-op），且没有任何拦截点会产生它们。`SetPaused(6)` executor 有 case，但同样**没有 producer**——玩家按空格暂停只改本地 `DXGame.Paused`，另一端继续跑 → 立即分叉。

**建议方向**：
- 新建 `Patches/PauseSyncPatch.cs`：Hook 空格 / 右上角暂停按钮 / `DXGame.set_Paused`，生成 `SetPaused` 指令；`SetGameSpeed` 同理 Hook `+`/`-`。
- 新建 `Patches/MayorSyncPatch.cs`：Hook 市长弹窗 `ZXGame.#=zcITiOSD6YNmD()` 的左右按钮，生成 `MayorVoteOrSelect`。
- `CommandExecutor` 补 `SetGameSpeed` / `MayorVoteOrSelect` 的 case。

**注意**：暂停在 TAB 是"空格微操"的核心，停开发这一步会掩盖大量确定性 bug（与 `tab-lockstep-sync` skill 的"别急着 Resync"一致）。

---

## 6. 跨机器浮点 / 引擎内部不确定源 ★★

**现象**：即便固定步长 + 定种 RNG，仍需确认：寻路（`DXWorldGrid` 的 pathfinding 任务/线程）、投射物物理、`DXHelper_Task.For` 是否在 `IsIADeterministic` 下真正单线程、以及 32/64 位浮点在不同 CPU 上是否一致。

**建议方向**：先用单进程两跑验证"同机确定性"（本轮 verifier 目标）；再两进程同机；最后跨机。若跨机分叉，用 StateHasher 的 per-component（pos/life/proj/prod/resv）二分定位。

**注意**：`tab-lockstep-sync` 已明确"哈希里不能有 `GetHashCode()` 结果"——本轮已修正 `StateHasher.MixQueue` 早期误用（现按 entityId/slot/commandId 确定性累加）。

---

## 7. 传输层帧头 docs 与代码不一致（小）★

`docs/04_NETWORK_PROTOCOL.md` §2.1 写 6 字节包头（2B Length），但 `INetworkTransport.cs` 实际实现是 8 字节头（`Magic2 + Type1 + Sender1 + Length4`）。本轮以代码为准；如需对外协议文档严谨，二选一统一。

---

## 8. 默认脚本场景依赖存档内容（小）★

`Testing/ScriptedCommandDriver.CreateDefault` 假设存档里有 `CommandCenter` / `Ranger` / `SoldiersCenter`。缺失时该操作 `SKIP` 并在日志说明，不会崩溃，但会让测试覆盖变弱。建议后续支持从 `TABCOOP_SCENARIO` 指定脚本文件（已实现 `op=build_tent|move|train|cancel|pause` 语法），针对具体存档定制。
