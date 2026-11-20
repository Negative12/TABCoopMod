# 14 — 多人多阵营（独立基地）模式设计规划与技术选型 (Phase 4)

> **整理日期**: 2026-11-20  
> **基准基线**: `v0.1.9`（执政官模式已跑通确定性 Lockstep、双栈直连 / Steam 中继、`coop_host_snapshot` 存档流与异步增量对齐）  
> **当前状态**: **核心玩法规则已全部拍板定稿**（共用电网边界、市长方案 A + 新增 0 手续费市长、3+3 奇观划分、功能奇观双阶段 `+50%/人` 众筹、闪电尖塔全队能源并网、胜利者雕像允许叠加、大厅可切双胜负模式、跨阵营光环共享且维持个人限建上限、市场解锁 20% 手续费资源转账）；当前仅**多基地地图生成改造（第 3 章 / 第 7 章）暂搁置**，待后续专题讨论。  
> **定位**: 本文档面向 Phase 4「独立基地合作模式（Multi-Faction Co-op Mode）」开展立项规划沉淀。为避免业务规则与底层实现混杂，文档严格拆分为三部分：  
> - **第一篇（第 1~3 章）**：面向**策划与游戏性决策** —— 玩法边界、已定稿规则总表与 3+3 奇观众筹细则、暂搁置的地图生成尺度预设参考；  
> - **第二篇（第 4~7 章）**：面向**开发与架构技术选型** —— `TheyAreBillions.exe` 逆向实锤、复杂度评估、阵营隔离方案、市场转账 / 自定义市长与奇观众筹实现、延迟与即时性分层结论；  
> - **第三篇（第 8 章）**：**M1~M4 分阶段研发路线与验收门禁**。

---

## 第 0 章：模式目标与演进前提

### 0.1 与 Phase 1~3 执政官模式的承接关系
截至 `v0.1.9`，执政官模式（多人共控同一人类阵营）已验证并沉淀出完整的多人底座，Phase 4 独立基地模式将**直接复用**而无需推倒重来：
1. **无损注入与确定性内核**：`AppDomainManager` 运行时注入、HarmonyX 内存补丁（`Classes OK=65, failed-tolerant=0`）、30Hz 单线程逻辑与 12Hz 同步物理（`1/256` 格末步量化）、`ZXBehaviour` 无状态行为随机流隔离。
2. **网络与热同步底座**：Steam Lobby + `DirectFastPath` 双栈 UDP 直连升级 / Steam 中继保底、`32 KB` 分片 `.zxsav` 内存快照传输与 `Auto-resync` 兜底、[`13_ASYNC_ALIGNMENT_AND_RESYNC_OPTIMIZATION_PLAN.md`](13_ASYNC_ALIGNMENT_AND_RESYNC_OPTIMIZATION_PLAN.md) 中已实装的三阶段异步增量对齐。
3. **绝对工程红线（铁律不变）**：严禁修改磁盘上 `TheyAreBillions.exe` 与 `DXVision.dll` 的任何字节（防 Eazfuscator 反篡改惩罚）；一切变化世界状态的玩家输入必须通过确定性 `NetCommand` 同步。

---

# 第一篇：策划与游戏性决策篇（Gameplay & Design Decisions）

> **本篇读者**：玩法策划 / 联机体验设计  
> **核心目标**：确定多阵营模式的玩法规则与地图手感标准，为后续程序落库划清产品边界。

---

## 第 1 章：核心玩法边界（为什么 V1 锁定“合作抗尸潮”而非 PvP）

Phase 4 的 V1 版本明确限定为 **2~3 人多阵营合作抗尸潮（Co-op Multi-Faction）**：每位玩家拥有各自独立的指挥中心（Command Center）、独立经济资源池、独立科技树与单位/建筑所有权，共同对抗地图游荡僵尸与周期/最终尸潮。

**V1 明确不做 PvP 对抗（玩家互相攻伐）的原因**：
1. **视野与战争迷雾**：原版 `ZXSystemMapLayers` 全局共享一套迷雾与视野数组，且没有“针对本地视角隐藏敌方玩家建筑/部队”的防窥屏与迷雾裁剪逻辑；合作模式天然**共享全队迷雾**，无需重构渲染与视野同步层。
2. **敌我判定与友伤体系**：原版 `ZXTeamType` 仅有 `Player / Zombie / NeutralJoinable / None / Independent` 5 个枚举值，没有多玩家敌对阵营。保留全员在底层同属 `Player` 阵营，可天然实现**无友伤（AOE 溅射/范围塔不误伤队友）**与**全图共战僵尸**；若改做 PvP，需重写单位自动索敌、抛射物碰撞过滤、近战/溅射友伤与建筑占领逻辑，工程量与风险将额外翻倍以上。

---

## 第 2 章：核心游戏性规则决策清单（核心玩法已全部定稿）

以下 6 项核心合作规则已全部拍板定稿，可直接作为 Phase 4 M1~M3 研发基线：

| # | 决策议题 | 状态 | 最终定稿规则 | 体验与实现说明 |
| :-: | :--- | :-: | :--- | :--- |
| **1** | **电力网（特斯拉塔覆盖边界）** | ✅ 已定稿 | **确定采用共用模式**：全队共享可建造供电疆域边界，平时各家电站发电量与耗电量仍按阵营独立结算（建成闪电尖塔后进一步合并能源池）。 | 中期两家防线对接可直接串联供电与合建城墙；彻底放弃高复杂度的“严格独立电网”分叉。 |
| **2** | **市长选举（Mayor）加成归属** | ✅ 已定稿 | **确定采用方案 A + 新增专属“0 手续费市长”**：<br>• 全局属性加成词条全联盟共享生效；<br>• 赠送单位、奖励建筑、科技与资源包归选出/触发方个人；<br>• 额外新增一位联机专属“0 手续费市长”，选中后全队资源转账免除手续费。 | 兼顾团队策略互补与个人账目隔离，彻底规避修改静态参数模板的底层深坑，同时让市长系统深度联动盟友贸易。 |
| **3** | **奇观（Wonders）分类、定价与众筹** | ✅ 已定稿 | **工整的 3+3 二分定稿**：<br>• **3 座功能性奇观**（观察者 / 不朽者学院 / 闪电尖塔）：全图限 1 座，团队共享效果；**研发费与建造费两阶段均按 `1 + 0.5 × (N - 1)` 加算**（2P `1.5×`、3P `2.0×`），且**两阶段均开启全队免手续费众筹**；闪电尖塔电力同步按相同比例加算并激活**全队能源池共享**；<br>• **3 座产出类奇观**（水晶宫 / 阿特拉斯转换器 / 胜利者雕像）：每人限建 1 座，维持原版 `1.0×` 价格，产出归个人；胜利者雕像允许跨阵营蹭光环且**允许多座叠加**。 | 既强化后期“全队合力攒超级奇观”的联机仪式感与协作深度，又靠众筹池彻底避免 `2.0×` 造价卡死单人仓库上限（完整细则见 2.1 节）。 |
| **4** | **胜负规则** | ✅ 已定稿 | **两种模式均实装，开局前在等候大厅切换**：<br>• **模式一（流亡交接）**：单家 CC 被毁后该玩家进入流亡/观战、残余建筑与部队移交盟友，全队 CC 归零才战败；<br>• **模式二（一损俱损）**：任一玩家 CC 被毁即全队立即战败。 | 兼顾常规局容错率与硬核挑战局的防守压迫感，由房主在开局大厅勾选切换并同步客机。 |
| **5** | **光环建筑与数量限制建筑** | ✅ 已定稿 | **允许跨阵营蹭光环，但个人限建上限维持原版**：<br>• 银行 / 市场 / 仓库等光环效果可辐射邻近盟友建筑；<br>• 任何带数量限制（`MaxInstances > 0`）的建筑，**单人可用上限维持原版不变**，按个人独立计数。 | 鼓励两家边界贴邻构筑“联合商业区”，同时既不被队友抢占个人限建名额，也不因联机盲目放大单人上限。 |
| **6** | **盟友资源转账** | ✅ 已定稿 | **任意一人拥有市场后激活，默认扣 20% 手续费**：<br>• 全图任一玩家建成并启用市场（Market）即解锁全队转账通道；<br>• 默认转账扣除 **20% 手续费**（发 `100` 实收 `80`，`20%` 系统损耗）；<br>• 应用联机专属 **“0 手续费市长”** 后全队转账手续费降至 **0%**。 | 形成“先起市场 → 激活盟友输血”的清晰策略节奏，20% 损耗防止无成本倒腾资源，配合 0 手续费市长带来强策略爽点。 |

### 2.1 六大奇观最终划分与双阶段众筹规则细表

| 奇观名称（类名） | 分类与数量上限 | 价格 / 产出倍率 | 核心机制与效果 | 联机团队规则 |
| :--- | :---: | :---: | :--- | :--- |
| **观察者 / 寂静观察者**<br>(`TheGreatTelescope`) | **功能性奇观**<br>*(全图限 1 座)* | **研发 & 建造 `1.5× / 2.0×`**<br>*(双阶段众筹)* | 加装于 CC 顶部（`TelescopeEnabled = true`），置位 `ShowFullMap = true` 永久消除全图战争迷雾并透视所有敌人动向。 | 全队共有全图视野；凑满凭证后由任一玩家在自家 CC 顶落位（建议放最安全基地）。 |
| **不朽者学院**<br>(`TheAcademy`) | **功能性奇观**<br>*(全图限 1 座)* | **研发 & 建造 `1.5× / 2.0×`**<br>*(双阶段众筹)* | 建成瞬间将全图全部己方现役可晋升部队升为老兵，此后全员后续训练单位出厂即老兵（`MakeSoldiersVeteran = true`）。 | 团队共享升级与出厂老兵效果；由全队众筹研发与建造，建成后全军共同受益。 |
| **闪电尖塔**<br>(`TheSpire`) | **功能性奇观**<br>*(全图限 1 座)* | **研发 & 建造 `1.5× / 2.0×`**<br>电力 **`1200` (2P) / `1600` (3P)** | 提供超大范围 30 格供电半径与按人数等比放大的巨额电力（`800 × (1 + 0.5 × (N - 1))`）。 | **建成并存活期间激活全队能源池共享**（全队发电/耗电合并结算）；若被毁或感染立即退回各家独立核算，具有极高战略护卫价值。 |
| **水晶宫**<br>(`TheCrystalPalace`) | **产出类奇观**<br>*(每人限 1 座)* | 原版 **`1.0×`** 价格<br>产出 **`+800` 食物** | 不依赖绿地地形直接供给大量食物人口基础。 | 每人独立研发、独立建造、产出归建造者个人。 |
| **阿特拉斯转换器**<br>(`TheTransmutator`) | **产出类奇观**<br>*(每人限 1 座)* | 原版 **`1.0×`** 价格<br>产出 **`+40` 石油/8h** | 不依赖油井持续稳定产出石油。 | 每人独立研发、独立建造、产出归个人（富余石油可通过市场转账援助盟友）。 |
| **胜利者雕像**<br>(`TheVictorious`) | **产出类奇观**<br>*(每人限 1 座)* | 原版 **`1.0×`** 价格<br>`8000 HP` + 区域 **`+20%` 金币光环** | 高耐久防御堡垒，大幅提升周围住宅金币税收。 | 每人可建 1 座；**允许盟友蹭光环，且允许多座胜利者雕像光环效果直接叠加**（盘活冷门奇观）。 |

**双阶段众筹核心体验规则**：
1. **为什么必须配套众筹**：在 3P 局 `2.0×` 价格下，功能奇观单步造价最高达 **`16,000 金 / 400 铁 / 400 石`**，远超单人常规 3 座仓库的 `200` 物资仓上限（单人硬憋需强行拍下 **7 座仓库**）。开启全队众筹后，每家只需常规 `2~3` 座仓库即可合力凑齐巨额造价，彻底消除仓储死锁；且奇观众筹直接进入公共项目池，**不走市场、免除 20% 转账手续费**。
2. **研发众筹 → 全队解锁**：全图任意玩家建有前置工坊/铸造厂后即可在公共奇观面板发起对应奇观的“研发众筹”；所有玩家可自由分批向池内捐款（支持在未满额前撤回自己未锁定的捐款）；捐满 `1.5×/2.0×` 研发费后开启研究，研发完成后**全队同步解锁该功能奇观科技**。
3. **建造众筹 → 团队协商落位**：研发完成后自动开启该奇观的“建造众筹”；全队再次共同捐满 `1.5×/2.0×` 建造费后，生成全图唯一的**已付清建造凭证（Ready Voucher）**，随后团队协商由哪位玩家选址拍下（`TheGreatTelescope` 装在对应玩家选定的安全的 CC 顶），避免先拍地基在筹资途中被僵尸拆毁或观察者无地面占地可拍的死局。
4. **闪电尖塔战略风险**：闪电尖塔建成并正常供电期间，全队电力池合二为一，允许电荒玩家直接调用全队富余电力；但一旦闪电尖塔被拆毁或感染，下一核算周期立刻退回各家独立电力结算，可能引发部分前线建筑断电停摆，形成极强的高光攻防目标。

---

## 第 3 章：多基地地图规模、间距预设与“天谴图”控频目标 `[暂搁置 · 待后续专题讨论]`

> [!NOTE]
> **当前状态说明**：根据讨论结论，**多基地地图生成与开局资源分布调整方案暂搁置**，待后续专题继续讨论细化（下方保留初步尺度与控频分析作为参考基线）。

### 3.1 地图有效尺寸 `NCells` 与性能上限控制
原版网格最大边长为 `512`，执政官模式（`v0.1.0+`）已实机验证非 2 的幂次尺寸会自动升格至 `512` 网格并通过 `FactorPlayableArea` 与边界刷怪框裁剪稳定运行。多阵营模式下每人需独立发展内环与中环，地图面积必须显著放大，同时要严防全图实体突破 4 万大关导致后期 30Hz 单核逻辑超时（`>33ms/tick`）：

| 玩家数 | 建议有效边长 `NCells` | 相对原版（256）有效面积 | 游荡僵尸局部密度系数 | 全图游荡总量（面积×密度） | 尸潮配平策略 |
| :---: | :---: | :---: | :---: | :---: | :--- |
| **1P 原版** | `256` | `1.00×` | `1.00×` | `1.00×` | 原版基准 |
| **2P 独立基地** | **`352 ~ 368`** | **`1.89× ~ 2.06×`** | **`0.85× ~ 0.90×`** | **`~1.65× ~ 1.80×`** | 略压游荡密度控制后期实体数；将难度增量更多转移至周期尸潮与最终尸潮分流。 |
| **3P 独立基地** | **`400 ~ 432`** | **`2.44× ~ 2.85×`** | **`0.80× ~ 0.85×`** | **`~1.95× ~ 2.35×`** | 建议将 **`NCells <= 448`** 设为安全上限，确保每家均有独立纵深且帧预算可控。 |

### 3.2 多基地锚点拓扑与“黄金间距”
- **2P 对称布局**：沿随机偏转角度（避免每次固定左右死板方位）分布在相对两极，**两座 CC 间距控制在 `75 ~ 95` 格**（各自距地图中心 `~38 ~ 48` 格，外侧距可玩边界保留 `>= 90` 格纵深）。
- **3P 等边三角形布局**：以地图中心为圆心、半径 **`50 ~ 60` 格**的正三角形（带随机整体旋转角，**相邻两基地间距 `~86 ~ 104` 格**）。
- **间距选择的玩法依据**：
  - **过近（`< 55` 格）**：开局 10 天住宅区、农场与锯木厂即撞车抢地，退化为“两人挤一个基地”；
  - **过远（`> 130` 格）**：基地贴近地图外缘，高阶怪与边缘尸潮刷怪条带直接贴脸，且前期互相跑图支援耗时过长，沦为各玩各的单机；
  - **黄金区间（`75 ~ 100` 格）**：前 20 天每家拥有完整的 `35 ~ 45` 格独立安全圈扩展；中期（Day 20~40）刚好打通两家之间走廊实现防线连片或兵力合流；后期背靠背或并肩抵御外圈总攻。
- **大厅间距预设（可选）**：可在开局界面提供 3 档间距风格：**紧邻联防（~55 格）** / **标准双城（~85 格，默认）** / **远距开荒（~115 格）**。

### 3.3 开局资源分布与“天谴图”控频目标
多人局最怕两种极端：**一是“必死废档”（开局零石矿/被山封死）**导致被迫频繁退房重随；**二是“把把塑料对称神图”**失去《亿万僵尸》开局随机地形博弈的刺激感。

因此策划目标应当**明确区分“死档”与“轻度天谴图”**：

| 开局评级 | 典型特征描述 | 目标占比 | 应对策略 |
| :--- | :--- | :---: | :--- |
| **彻底不可玩死档** | 任一玩家基地与其他基地/外部完全不连通；内圈彻底无木可砍；或扩展半径内完全无石矿导致卡死兵营科技。 | **`< 1%`** | **由生成器保底逻辑兜底消除**（连通性开凿 + 分区矿脉重试 + 极端匮乏微补）。 |
| **正常舒适开局** | 内圈树林充足，可放得下 1~2 座采石场与前期猎屋/农场，中圈有铁矿、油井与金矿，与队友发展空间均衡。 | **`85% ~ 90%`** | **依靠“扇区自然择优 + 分区原生噪声重掷”自然达成**，不做人工方格矿堆。 |
| **轻度天谴挑战图** | 某家草地极少（需靠猎屋/渔屋过渡），或石矿略偏远（需向外多拉 1~2 根电塔抢矿），或相邻路口僵尸压迫更强，但仍可解。 | **`5% ~ 10%`** | **刻意保留的趣味波动**：通过第 2 章的**盟友资源援助（转账）**与**早期游侠协防**把单人局的挫败感转化为联机互助爽点。 |

**生态差异化保护原则**：
- **常规生态（`BR` 暗黑荒原 / `AL` 绿野 / `TM` 森林）**：原生木草丰沛，仅靠扇区自然择优即可自然达到 `92%~95%` 舒适率，**不人工填土造绿**。
- **高难贫瘠生态（`DS` 干旱高地 / `FA` 寒冰荒原 / `VO` 剧毒荒原）**：这些生态本来就是靠稀缺草地或木材压迫开局节奏，切忌强塞绿洲破坏原生态风味；改为按**综合食物潜力（可耕草地 + 猎屋林缘 + 渔屋水岸）**评估下限，并设置**“团队内部极差护栏”**（防止同局内出现 P1 富得流油、P2 寸草不生且无树可猎的离谱贫富分裂）。

---

# 第二篇：开发与架构技术选型篇（Engineering & Technical Architecture）

> **本篇读者**：客户端 / 同步 / 逆向开发人员  
> **核心目标**：基于 `TheyAreBillions.exe` v1.0.14.29 实测反编译事实，明确各子系统改造成本、避坑边界与技术实现路线。

---

## 第 4 章：引擎底层逆向事实与各子系统改造复杂度评估

### 4.1 总体工程量评估
基于对 `TheyAreBillions.exe`（v1.0.14.29）核心类型与组件的反编译验证，**多阵营合作模式的总体工程量定性为“中高复杂度、工程边界清晰”**：
- **预估新增/改动规模**：约 **3,800 ~ 6,500 行 C# 代码**（含奇观双阶段众筹状态机与轻量化面板约 `350~500` 行），新增 **30 ~ 45 个 Harmony 补丁类**（约相当于半个执政官模式的开发量）。
- **核心架构矛盾**：游戏引擎从头至尾建立在**单人全局单例假设**之上（`ZXLevelState.#=zuartwoo=()` 与 `ZXSystemMapLayers.#=zQFWYIvs=`），几乎所有经济扣款、科技查表、电力扩散与 AI 寻的都直接写死读取单一实例。

### 4.2 七大子系统复杂度矩阵与底层实锤依据

| 子系统 | 复杂度 | 引擎底层实锤（已反编译验证类名与混淆符号） | 技术处理要点与避坑结论 |
| :--- | :---: | :--- | :--- |
| **1. 单位/建筑归属与操控隔离** | ⭐⭐<br>*(中低)* | `ZX.ZXTeamType` 枚举仅包含 `Player, Zombie, NeutralJoinable, None, Independent` 5 项，**引擎无多玩家阵营概念**。 | **不要改写 `ZXTeamType`**（保持全员 `Player` 以天然复用零友伤与全队共同抗敌）。在 Mod 层维护确定性 `EntityOwnerMap`（`ulong EntityId -> byte PlayerId`），随创建/训练/升级/中立接管同步传播，在 `CSelectable` 与 `CommandInterceptorPatch` 拦截非己方操作权。 |
| **2. 五维资源 / 供给 / 市场 / 破产** | ⭐⭐⭐⭐<br>*(中高)* | `ZX.ZXLevelState.#=zuartwoo=()` 被全引擎上百处代码直连调用：<br>• `#=zBpC6By32LAqv()` 遍历全图 `Team == Player` 建筑/单位重算产能、人口、维护费与 `NMarkets`；<br>• `#=zgLWBIL0dUIT9ZcCNSQ==()` 执行 8 小时跳资源并在 `NTurnsWithNegativeGold >= 2` 时按等级驱散部队；<br>• `#=zpo2hsxCgQn3I()` 在超仓储上限且 `NMarkets > 0` 时自动折现卖货；<br>• `ZX.Entities.PickableItem` 拾取后直接向全局 `ZXLevelState` 加资源及 `CommandCenterExtraFood/Energy/Workers`。 | 采用 **“活跃上下文窗口（Active Context Swap）”模式**（详见 5.2 节），避免逐处重构上百个 UI/命令调用点；但必须显式改造重算、8h 结算、超仓卖货、宝箱拾取及 Tooltip 明细汇总。 |
| **3. 科技树、限数建筑与 3+3 奇观众筹** | ⭐⭐⭐⭐<br>*(中高·已定)* | • `ZXLevelState` 内部维护 `DXConcurrentHashSet<string> #=zPk6B9SCHYsPYfjq0cnF9E2c=` 及 `#=zNHCibbkaY4C1bNgYnQ==` / `#=zNGgzojiiQIq14QwoDQ==` 记录单次命令/已研科技；<br>• 限建/奇观上限 `Build.AllInstancesAlreadyBuilt` 直接统计 `EntitiesOfType(Type).Count() >= MaxInstances`；<br>• `TheVictorious` 通过 `Building.TotalGoldSupply` 内的 `UnderEnhancerBuilding.SumFast(...)` 原生叠加计算周边住宅金币加成。 | • `{TheCrystalPalace, TheTransmutator, TheVictorious}` 与所有 `MaxInstances > 0` 建筑按 `Owner == pid` 计数（`TheVictorious` 无需额外补丁即原生支持叠加）；<br>• `{TheGreatTelescope, TheAcademy, TheSpire}` 全局限 1 座，研发 & 建造两阶段均按纯整数 `(base * (2 + N - 1)) / 2` 缩放并接入 `WonderCrowdfundManager`（~350~500 行，详见 5.7 节）；建成 `TheSpire` 后在 `#=zBpC6By32LAqv()` 触发全队能源池合并。 |
| **4. 电力网（特斯拉塔覆盖）** | ⭐⭐<br>*(中低·已定)* | `ZXSystemMapLayers`（`#=zJme8KFhmikprnkg_2CDeiQE=`）仅有一张 `uint[,] #=zVXTH1ahoIxQNg$omKQ==` 电网矩阵；且重建 BFS `#=zK$nELVArHhqXXa$0lA==()` **硬编码只从 `EntitiesOfType<CommandCenter>().First()` 单源启动**（而连通性寻路 `#=zs1C_pLjYHbdEceIG1y8dYemfTA9s(Point)` 反倒已经正确遍历所有 `CommandCenter`）。 | **已锁定共用模式**：仅需修补 `#=zK$nELVArHhqXXa$0lA==()` 为多 CC 起点 BFS（约 50 行），直接复用原版全图供电矩阵与 `#=zs1C_pLjYHbdEceIG1y8dYemfTA9s`；无需做 3×10-bit 位段隔离。 |
| **5. 尸潮目标、游荡禁区与双胜负** | ⭐⭐⭐<br>*(中)* | • `ZXLevelState.CurrentCommandCenter` / `CurrentCommandCenterCell` / `DefaultCellInfectedDestiny` 均为单值；<br>• `ZX.Behaviours.ZXBehaviour` 中 `AttackCommandCenter` 节点（如 `#=zL2NAXr1fnAQ4MrUI0g==`、`#=zVrYo0VGEG7Sj`）及 `CellsAwayCommandCenterWhenRoaming` 直接读取上述单 CC 字段；<br>• `ZX.Entities.Zombie` 在 `IsIAFrameModule(7200)` 检查寻路至单 `CurrentCommandCenterCell` 失败时自杀清理。 | Patch `ZXBehaviour` 相关 4~5 个行为节点及 `Zombie` 卡死检测，改为“就近存活 CC”或按 `Zombie.ID` 取模分流多路进攻；改造 `ZXSystemGameLevel.#=zIoSB3Q$9ZjUF`（`TriggerGameEnd`）支持大厅可切换的“流亡交接 / 一损俱损”双胜负分支。 |
| **6. 市长方案 A + 0 手续费市长** | ⭐⭐<br>*(低·已定)* | 反编译 `ZX.ZXMayor.#=zPTw8CXGE0RqF` 实锤：其直接调用 `ZXDefaultParams.#=zw2hjvSf99fNr(Mods)` **原地修改全局静态模板 `ZXEntityDefaultParams`**（全图共用同一份引用）；全局注册表为 `ZXMayor.#=zoTuFkHI=`。 | **已锁定方案 A**：彻底规避克隆全局模板与拦截热路径 `ZXEntity.#=zcODCID3ur9P9()`（每秒数百万次调用）；属性词条沿用原版全联盟共享，赠送单位/建筑/科技归触发个人；并在 `ZXMayor.#=zl1IE4Os2suIF()` 运行时注入自定义“0 手续费市长”。 |
| **7. 迷雾视野与存档快照** | ⭐⭐<br>*(中低)* | 合作模式全队共享视野迷雾，无需拆分 `ZXSystemMapLayers` 视野/迷雾数组；`ZXLevelState` 原生自带可序列化自定义字典 `#=zT83MmEbnfivNoSls2x6o7BU=`（`Dictionary<string, object>`，可通过 `#=zmEKjLe7WJ0Hr` / `#=zdUZvKcNgbUyf` 读写）。 | 仅需随 `.zxsav` 快照附带 `EntityOwnerMap`、`FactionState[1..N]`、`WonderCrowdfundProjects`、胜负规则及 `SharedEnergyGridActive`/`ZeroTransferFeeActive`（数 KB 元数据），完整复用现有 `coop_host_snapshot` 与 `Auto-resync`。 |

### 4.3 开发期最易遗漏的“隐蔽全局耦合点”清单
除显式经济扣费外，实测反编译已排查出以下 **5 处隐蔽耦合点**，M2 开发时必须逐一覆盖，否则极易出现串账或 Desync：
1. **光环建筑跨阵营共享与自身归属边界**：`Building.UnderEnhancerBuilding` 来源为 `ZXLevelState.#=zDsPVU4dXlejQD9RrXz8QIy8Ssk_1CySn7g==()`，由 `#=zBpC6By32LAqv()` 每周期刷新。**因已定稿允许跨阵营蹭光环（含多座 `TheVictorious` 光环叠加），此处可直接保留原版全局增强建筑范围扫描**（无需按 `Owner` 过滤，直接节省改造面），但银行/市场/仓库自身的 `ResourcesStorage` 仓储扩容、`NMarkets` 数量及金币产出仍严格归属各自 `Owner`。
2. **非用户输入帧的扣/退款**：`CCommandable.#=zrz2EI1A=`（生产队列自动推进下一项）与建筑被毁/拆除退费发生在逻辑模拟帧而非指令拦截帧，必须先查出目标实体 `Owner` 再切上下文（若拆毁建造中的功能奇观，需按比例分摊退回各众筹出资方）。
3. **`CommandCenterExtra*` 补偿归属**：`Building.#=z3rO8Jok=()`（第 373~378 行）中 `if (this is CommandCenter)` 会直接把全局 `CommandCenterExtraWorkers / Food / Energy`（包括开局补偿、市长与拾取食物废墟加成）加到每座 CC 上——多基地时必须按 `Owner` 独立存储并仅加到对应 CC，否则 3 座 CC 会领 3 份。
4. **建造权限与限建计数检查**：`Building.#=zUBPru1Ew_PxN()`、`Structure.#=z2IRuTX3OAzJgkAHNDKW65bN9UxnV()` 与 `Build.AllInstancesAlreadyBuilt` 会校验电网、个人限建数量（`MaxInstances > 0`）与全局状态，回放期与 UI 面板检查均必须处于对应玩家上下文（其中普通限建建筑与产出奇观计数过滤 `Owner == pid`，功能奇观校验全局上限 `1`）。
5. **顶栏与鼠标悬停 Tooltip 汇总**：`ZXSystemGameLevel` 中的资源图标悬停（`ZXDynamicHelp`）直接遍历全图 `Human` / `Building` 并读取全局 `ZXLevelState` 字段；仅改数字而不改 Tooltip 会导致界面数字与明细提示不一致（且在闪电尖塔激活全队能源并网时需正确反映公共电力池状态）。

---

## 第 5 章：阵营隔离核心架构选型

### 5.1 虚拟阵营所有权层（`EntityOwnerMap`）
不引入新的 `ZXTeamType` 枚举值，在 Mod 层构建确定性所有权服务 `FactionOwnershipTracker`：
- **状态存储**：`Dictionary<ulong, byte> EntityOwnerMap`（记录有主实体 ID → `PlayerId ∈ {1..3}`，僵尸/自然矿脉/中立无主物不入表）；
- **全生命周期传播链**：
  1. **开局生成**：多基地开局生成的 `CommandCenter` 与各自初始部队按创建序绑定到 `PlayerId = 1..N`；
  2. **建造与训练**：`NetCommand` 回放期 `Build.#=zBTwSMLo=` 或兵营出兵生成新实体时，继承发起者 `cmd.PlayerId`（队列自动推进继承兵营 `Owner`）；
  3. **升级与状态切换**：建筑/单位升级、废弃防御塔修复接管（`NeutralJoinable -> Player`）时继承操作者或原建筑 `Owner`；
  4. **销毁清理**：实体 `Dispose` / 阵亡时从表移除（防内存长期膨胀）。
- **权限与视觉**：
  - `CSelectable` 选中框过滤 + `CommandInterceptorPatch` 操作拦截：默认禁止选中或操控 `owner != LocalPlayerId` 的实体；
  - 渲染层按阵营色（P1 `DodgerBlue` / P2 `SpringGreen` / P3 `Orange`）绘制脚底阵营环，并接管 `ZXSystemMapLayers.#=z1nNfM8mU$CIX` 在小地图上按玩家着色。

### 5.2 经济与科技：活跃上下文窗口（Active Context Swap）模式
由于原版上百处 UI 渲染、`ZXCommand` 费用计算/扣减与按钮灰显逻辑全部硬编码访问单例 `ZXLevelState.#=zuartwoo=()`，**最稳妥、侵入最小的设计模式是“上下文热切换”**：

1. **Mod 层维护独立账本 `FactionState[1..N]`**：
   - 涵盖：`Gold / Wood / Stone / Iron / Oil`、`FoodReserved / WorkersReserved / EnergyReserved`、各资源产能与消耗缓存（`WoodProduction`、`ArmyGoldCost`、`StructuresGoldCost`、`MaxGoldByExportingExcedents` 等）、`NTurnsWithNegativeGold`、`CommandCenterExtraFood/Workers/Energy`、`BonusEntityTemplates`（市长赠送单位/建筑剩余次数）、已研科技/单次命令集合（`#=zPk6B9SCHYsPYfjq0cnF9E2c=`）及 `CurrentCommandCenter` 引用。
2. **默认稳态始终镜像 `LocalPlayerId`**：
   - 在局内自由浏览、鼠标悬停面板、建造预览及渲染期，`ZXLevelState` 单例内保存的**永远是本机玩家的数据**。因此原版顶栏资源数字、资源不足红字提示、科技树灰显与右下角命令面板几乎 **0 改写即可原生正确显示**。
3. **仅在跨阵营逻辑瞬间做 `PushFactionContext(pid) / Pop`**：
   - **回放远端/本机 `NetCommand` 前**：按 `cmd.PlayerId` 将对应玩家的 `FactionState` 写回（Swap-In）`ZXLevelState`，执行完毕并 `FlushStartFramePostMethods()` 后 Swap-Out 还原；
   - **非指令触发的建筑事件**：生产队列自动推进、拆毁退费、拾取 `PickableItem` 时，先查询操作主体实体 `Owner` 并切换上下文；
   - **周期重算与 8h 结算**：在 `ZXLevelState.#=zBpC6By32LAqv()` 与 `#=zgLWBIL0dUIT9ZcCNSQ==()` 中对 `pid = 1..N` 循环切换上下文，仅累加 `EntityOwnerMap[entity.ID] == pid` 的建筑/单位与维护费，完成各家独立的产钱、超仓自动卖货与破产裁兵。

### 5.3 电力网与闪电尖塔能源并网实现（已定稿）
1. **共用供电疆域边界（多源 BFS 修补，约 50 行）**：
   - Patch `ZXSystemMapLayers.#=zK$nELVArHhqXXa$0lA==()`，将原本单点压栈 `CurrentCommandCenterCell` 与 `EntitiesOfType<CommandCenter>().First()` 改为**把所有存活己方 `CommandCenter` 全部压入初始队列与中继列表**，即可立刻支持多基地共享同一张供电边界并兼容现有 `#=zs1C_pLjYHbdEceIG1y8dYemfTA9s` 多 CC 连通性寻路。
2. **闪电尖塔按人数缩放电力 + 全队能源池合并共享**：
   - 使用纯整数比例分子 `scaleNum = 2 + (playerCount - 1)`（1P=`2`、2P=`3`、3P=`4`），闪电尖塔自身发电量为 `(800 * scaleNum) / 2`（2P `1200` / 3P `1600`）；
   - 在 `ZXLevelState.#=zBpC6By32LAqv()` 逐阵营统计完各自电力供给与消耗后，判定全图是否存在**存活、启用且未受感染的 `TheSpire`**：
     - **存在（`SharedEnergyGridActive = true`）**：将所有存活阵营的 `EnergySupply`、`EnergyNeeded` 与 `EnergyReserved` 汇总写入共享Team能源池，并将共享净可用电力映射至每家 `FactionState[pid]` 的 `AvailEnergy`（并在顶栏电力数值旁标注 `[Grid Linked]` / 并网提示）；
     - **不存在或被毁/感染（`SharedEnergyGridActive = false`）**：立即跳过合并步骤，各家自动退回各自独立电力供给/需求核算。

### 5.4 多基地 AI 路由与双胜负模式
- **多基地 AI 路由**：在 `ZXBehaviour` 的 `AttackCommandCenter` 行为节点与 `Zombie` 卡死检测中，传入当前僵尸坐标并选择**最近存活 CC**（或针对定期刷新的大型尸潮波次按 `zombie.ID % aliveCCs.Count` 分流指向不同玩家的 CC）；`CellsAwayCommandCenterWhenRoaming` 改为校验到所有存活 CC 的最小距离。
- **双胜负规则分支**：Patch `ZXSystemGameLevel.#=zIoSB3Q$9ZjUF`（`TriggerGameEnd`）与单 CC 沦陷监听，依据大厅同步参数 `DefeatRule` 分支处理：
  - `AllBasesLost`（流亡交接模式）：当第 `k` 家 `CommandCenter` 血量归零时，将该玩家标记为流亡/观战，按规则将残余单位与建筑所有权移交存活盟友（并合并处理对应 `EntityOwnerMap`）；仅当 `aliveCCs == 0` 时真正触发全队战败结算；
  - `AnyBaseLost`（硬核一损俱损模式）：任一玩家 `CommandCenter` 被毁即直接触发全队战败结算。

### 5.5 快照序列化与状态校验
- `StateHasher`（v3+）将所有 `FactionState[1..N]` 的核心数值、`EntityOwnerMap` 确定性摘要（按 `EntityId` 升序混合）、`WonderCrowdfundProjects` 各项目出资账本、以及 `ZeroTransferFeeActive` / `SharedEnergyGridActive` / `DefeatRule` 全部纳入校验和；
- 房主导出 `coop_host_snapshot.zxsav` 时同步生成随包携带的几 KB 侧边二进制快照（或写入 `ZXLevelState.#=zT83MmEbnfivNoSls2x6o7BU=` 自定义字段），保证开局同步、断线重连与 `Auto-resync` 后阵营归属、多账本、奇观众筹进度与市场/电网状态 100% 一致。

### 5.6 市场解锁转账、20% 手续费与自定义“0 手续费市长”技术实现
1. **市场解锁门控**：
   - 在局内转账面板显示与 `CommandInterceptorPatch` / `CommandExecutor` 校验中，统一判定 **“任意存活阵营存在至少 1 座已完工且未禁用/未感染的 `Market`（即 `FactionState[pid].NMarkets > 0`）”** 即激活全队资源转账；无需每个人都建市场。
2. **确定性整数手续费结算**：
   - 新增 `NetCommandType.TransferResource`（载荷：`FromPlayerId`、`ToPlayerId`、`ResourceType`、`GrossAmount`）；
   - 回放端在 `FromPlayerId` 上下文严格校验库存余额并扣除 `GrossAmount`，随后用纯整数算术计算到账值（杜绝浮点舍入引发跨机 Desync）：
     ```csharp
     int netAmount = CoopSessionManager.ZeroTransferFeeActive ? grossAmount : (grossAmount * 80) / 100;
     ```
   - 切入 `ToPlayerId` 上下文入账 `netAmount`（遵循接收方仓储上限与自身 `NMarkets` 溢价折现卖货逻辑）。
3. **自定义“0 手续费市长”注入（零改磁盘 EXE/数据表）**：
   - 在 `ZXMayor.#=zl1IE4Os2suIF()`（市长定义表加载入口）挂载 `Postfix`，向静态注册表 `ZXMayor.#=zoTuFkHI=` 动态构造并插入自定义市长实例（如 `ID = "CoopMayor_ZeroTransferFee"`），仅在多阵营合作模式下开放进入候选抽样池；
   - 在 `MayorSyncPatch` 应用该市长 ID 时，将共享会话标识 `ZeroTransferFeeActive = true` 写入同步状态并同步顶部通知横幅，全程**不改写任何 `ZXDefaultParams` 模板数据**。

### 5.7 3+3 奇观分流与功能奇观双阶段众筹技术实现
1. **3+3 归属与上限分流**：
   - **功能奇观集合** `{ TheGreatTelescope, TheAcademy, TheSpire }`：在研发、持有凭证、在建与建成全生命周期内严格执行全局上限 `1`（任一方触发即同步锁定全局其余玩家入口）；
   - **产出奇观集合** `{ TheCrystalPalace, TheTransmutator, TheVictorious }` 及所有 `MaxInstances > 0` 普通建筑：拦截 `Build.AllInstancesAlreadyBuilt` 与 `IsVisible`，改为仅统计 `EntityOwnerMap[e.ID] == pid` 的己方数量；
   - **胜利者雕像叠加**：保留 `Building.TotalGoldSupply` 原版 `UnderEnhancerBuilding.SumFast(...)` 累加逻辑不变，零额外补丁即可天然实现跨阵营蹭光环与多雕像叠加。
2. **纯整数倍率缩放（防浮点舍入漂移）**：
   - 功能性奇观在研发阶段与建造阶段的 5 维资源造价统一采用纯整数比例缩放（工人与电力需求保持原版不翻倍）：
     ```csharp
     int scaleNum = 2 + (playerCount - 1); // 1P=2, 2P=3, 3P=4
     int scaledCost = (baseCost * scaleNum) / 2;
     ```
3. **双阶段众筹状态机（`WonderCrowdfundManager`）**：
   - **项目状态模型**：每座功能性奇观维护确定性状态 `WonderProjectState`：`Phase`（`Research` | `Build`）、`TargetCost`（5 维目标额度）、`ContributedTotal`、`PerPlayerContributed[1..N]`（各玩家出资明细，供撤回/拆毁按比例退款）、`Status`（`Idle` | `Funding` | `Ready` | `Completed`）。
   - **确定性众筹指令**：新增 `NetCommandType.WonderContribute`（从出资方 `FactionState[pid]` 校验余额并扣除资源直入公共池——**绕过市场手续费与个人仓储上限限制**）与 `NetCommandType.WonderWithdraw`（仅允许在 `Status == Funding` 阶段退回本人已出资部分）。
   - **阶段一：研发众筹流程**：
     - 全图任意玩家拥有该奇观对应的前置研究建筑（如石屋/铸造厂）时即可开启该项目 `Research` 众筹；
     - 当 `ContributedTotal >= TargetCost` 瞬间由系统在发起方工坊触发**零扣费研发启动**（或由发起工坊按原版读条推进），研发完成时将对应已解锁科技 ID 同步写入**所有存活阵营的 `UnlockedCommands` 集合**。
   - **阶段二：建造众筹流程**：
     - 研发完成后自动切入 `Phase = Build` 并开放建造众筹；
     - 当 `ContributedTotal >= TargetCost` 瞬间置 `Status = Ready`，生成全图唯一**已付清建造凭证（`WonderBuildVoucher`）**（该凭证下单建造时跳过金/木/石/铁/油扣费，仅校验地形、电力疆域与所需工人）；
     - 任意玩家皆可使用凭证拍下奇观（`TheGreatTelescope` 直接加装在拍单者自选的己方 `CommandCenter` 顶），放置瞬间核销凭证并将全图已建数锁为 `1`。此设计彻底规避“观察者没有地面实体占地”、“筹资中途地基被打爆导致众筹白费”等边界故障；若奇观在施工完成前被玩家主动取消，按确定性整数比例从 `PerPlayerContributed` 退回各出资方账本。

---

## 第 6 章：控制权确定性与客机操作即时性技术分析

针对“拆分单位所有权后，控制权确定性是否能让客机操作反馈做到 0ms 即时执行”的问题，需按**三个技术层次**严格区分：

### 层次 1：消除跨端竞态否决，让本地 0ms UI 预演“准 100% 确定成功”（✅ V1 立竿见影）
- 在执政官模式下，尽管 [`CommandInterceptorPatch.cs`](../src/TABCoopMod/Patches/CommandInterceptorPatch.cs) 已实装本地待建幽灵框与资源预扣，但因资源与单位共享，客机点击后的 `RTT` 窗口内仍可能被主机抢花钱或覆盖指令，导致回放被拒、幽灵框突然回滚。
- 多阵营建立 **Single-Writer Ownership（单写者所有权）** 后，没有任何队友能花你的钱、抢你的兵营槽位或改写你的单位指令。客机本地 0ms 响起的确认语音、地面光圈、建造幽灵框与预扣状态，除碰撞队友边界建筑或突发贴脸敌军外，**几乎 100% 会被权威回合确认**，彻底消除运营层的“吞单焦虑”。

### 层次 2：释放“去中心化对称 Turn 调度”，真实执行延迟直接砍半（✅ 1.0 RTT → 0.5 RTT）
- 当前执政官模式为了对同阵营并发冲突做中心裁决，采用 `Client → Host 聚合 → Host 广播 TargetSimTick` 的星型流控，导致客机一次操作要等 **完整 1 个 RTT** 才能真正迈步（如 [`11_SYNC_OWNERSHIP_AND_VISUAL_SMOOTHING.md`](11_SYNC_OWNERSHIP_AND_VISUAL_SMOOTHING.md) 实测 `latency=80ms` 下平均落地延时约 `380ms`）。
- 在多阵营单写者模型下，绝大部分操作天然无写冲突，可将回合交换升级为 **对称 P2P Turn 广播**（2 人局直发对端，3 人局全互联或 Host 零等待转发）：双方各自按 `TargetTick = CurrentTick + InputDelayTicks`（`ceil((RTT/2) / 33.3ms)`）投递输入，冲突时仅用确定的 `PlayerId` 序裁决公共空地抢建。
- **收益**：客机真实指令执行延迟直接由 **1.0 RTT 降至 0.5 RTT**（如 `100ms` RTT 下执行延迟从 `~133ms` 压到 `~66ms`），同时 **100% 保持严格确定性 Lockstep 与 `StateChecksum MATCH`**。

### 层次 3：为什么 V1 依然严禁客机逻辑层“0ms 本地先行移动/建造”？（⚠️ 物理与僵尸强耦合上限）
即便单位独占归属 P2，也不能把之前撤除的逻辑/物理先行移动复活或把 `_unitOwnership` 反向对齐升级为常态权威：
1. **后方建设抢跑必破经济相位**：若 P2 在 `Tick T` 扣钱建房、主机在 `Tick T+3` 才收到建造，双端该建筑的完工帧、供电刷新帧与 8 小时产钱结算相位将永久错开 `3 ticks`，彻底破坏 Lockstep 同帧一致性。
2. **前线风筝拉扯撞上共享僵尸硬墙**：TAB 中的单位并不是在真空里移动——它每帧实时改写 `DXWorldGrid` 占格、广播噪声（Noise）、触发迷雾并与**全图共享的僵尸群**发生 Chipmunk2D 刚体推挤与近战结算。若 P2 本地 0ms 抢先走位而僵尸权威端晚 `3~4 ticks` 才收到位移，P2 屏幕上已经脱离危险区的游侠会在 `LifeFixes` 下发后**凭空掉血或隔空被感染**；双人联军同守一处时更会在“谁触发了变异体仇恨”上产生不可收敛的分叉。
3. **终极解成本过高**：唯有引入类似 MMO/RTS 的**空间分区权威（Spatial Authority）**（把 P2 附近无 P1 介入的局部僵尸簇连带移交给 P2 算），才能实现真 0ms 操作，但这相当于重写整套跨区实体迁移协议，工程风险远超 V1 收益。

> **技术选型定论**：Phase 4 V1 坚守 **“确定性 Lockstep + `v0.1.9` 增量对齐底座”**，通过 **层次 1（单写者准 100% UI 预演） + 层次 2（对称 Turn 把客机等待砍半至 0.5 RTT）** 获取最佳体感性价比，严禁引入逻辑层 0ms 抢跑。

---

## 第 7 章：多主基地地图生成器技术设计（`MultiBaseMapGenerator`）`[暂搁置 · 待后续专题讨论]`

> [!NOTE]
> **当前状态说明**：地图生成逻辑调整方案暂搁置，待游戏性专题继续讨论后再最终定稿；M1 原型期可先用最小对称多 CC 测试落位或专用测试图验证归属与同步闭环。以下保留已逆向出的原版限制与初步管线调研作为后续参考。

### 7.1 为什么不能复用并魔改原版 `MapGenerator.#=zEzgd90E=`？
实测反编译 [`MapGenerator`](../src/TABCoopMod/Patches/SurvivalBalancePatch.cs)（`#=zyl_NPjjlA7DRfVtsRJCX1kN4BxSr.#=zEzgd90E=`，共 999 行）确认了两大致命局限：
1. **联合天谴概率指数放大**：原版只对唯一 `CC1` 校验树林（`Enlarge(11)` 内 `>=18` 格）、100 次重试石矿（`FromCenter(20)` 内 `>=4` 格）、100 次重试铁矿（`FromCenter(50)` 内 `>=10` 格），且不校验草地与油井。若单家出烂图概率为 `20%`，3 家同时不天谴的概率只剩 `(0.8)^3 = 51.2%`（近半数开局至少有一家天谴）；且因石矿重试仅看 `CC1`，副基地门口**完全无石矿的死档率将高达 30%~50%+**。
2. **IL Transpiler 维护灾难**：原版 `999` 行生成器含大量局部闭包与噪声层局部变量，用 Harmony Transpiler 硬插多基地循环极其脆弱。

**架构选型**：在 `SurvivalBalancePatch` 的 `Prefix` 拦截多阵营新生存局，直接跳过原版 999 行方法，交由独立的 **`MultiBaseMapGenerator.Generate(ZXRandomLevelParams)`** 接管执行——完全复用游戏原生的 `DXNoyseLayer`、`ZXMapDrawer`、`ZXMapTheme`、`ZXLevelEvent` 与 `DXLevel` API，仅在 Host 生成一次后经现有快照传给客机（**零网络同步负担**）。

### 7.2 五步确定性生成与保底管线

1. **网格升格与可玩区裁剪**：沿用已在 [`CoopBalanceManager.cs`](../src/TABCoopMod/Sync/CoopBalanceManager.cs) 验证通过的机制，将非 2 的幂次边长（`352~368` / `400~432`）自动升格至 `512` 底层网格，经 `DXWorldGrid.GetSceneSquareArea(nCells, factor)` 与不可通行边框 `dXNoyseLayer` 裁剪有效游玩区与四周刷怪条带。
2. **扇区自然择优（Top-K 加权抽选，不造塑料平地）**：
   - 先运行原生水/山/草地/森林 Perlin 噪声生成基底地貌；
   - 在各玩家对应的环形扇区带（距中心 `35~52` 格或按预设间距，距边缘 `>= 85` 格）采样 `20~30` 个可建候选点；
   - 按 **周边树量 + 综合食物潜力（草地农场 + 可放猎屋林缘 + 可放渔屋水岸）+ 建房开阔度** 评分；
   - **剔除死地后在合格池按权重 Top-K 随机抽选**——不死磕第 1 名，自然压低极差开局同时保留每局地形风貌与 `5%~10%` 偏苦变化。
3. **多基地全连通校验 & 最小代价 A* 开凿**：
   - 以全部 `CC_1..CC_N` 为起点执行一次连通分量 BFS；若某两座基地被山脉或湖泊隔断，使用带权 A* 寻找穿过山/水代价最小的路径，自动开凿宽 `4~6` 格的陆地走廊。
4. **Voronoi 分区独立矿脉噪声重掷 + 极端兜底**：
   - 按“距哪座 `CC_k` 最近”将地图划分为 $N$ 个 Voronoi 扇区；
   - **石矿与铁矿**：每个扇区各自独立执行原版 `while (tries < 60)` Perlin 噪声重试，仅校验己方基地内圈（石矿 `r <= 24` 格 `>= 4` 格，保留少量偏远矿点变化；铁矿 `r <= 48` 格 `>= 8~10` 格），再按 Voronoi 掩码合成到 `LayerObjects`。每一簇矿脉 **100% 保持原版 Perlin 噪声自然形态**，单扇区成功率 `> 99%`；
   - **油井 & 金矿审计**：按 Voronoi 归属校验每家扇区至少各有 `1~2` 个可达油井和金矿簇，缺口就在同扇区可通行空地补充搜索落点；
   - **贫富极差护栏**：在开局附近（`r <= 7`）清理障碍保证首圈建房；仅当某玩家内圈木/食低于全队均值 `40%` 或无任何可用食物源时，才在合理外环小簇微补少量树林或草地（对 `DS`/`FA`/`VO` 严格尊重生态，不毁特征）。
5. **多中心最小距离场统一驱动威胁梯度**：
   定义地图任意格 `(x, y)` 到所有基地的最近距离场：

   $$d_{\min}(x, y) = \min_{k \in \{1..N\}} \text{dist}\bigl((x, y),\ \text{CC}_k\bigr)$$

   原版生成器所有基于单基地距离的逻辑全部替换为 `d_min`：
   - **安全无怪圈**：对所有 `CC_k` 均抠除 `MinDistanceForWeakInfected` 安全区；
   - **僵尸强弱同心环**：游荡僵尸等级（Weak / Medium / Strong / Powerful）按 `d_min` 计算——每座基地外围呈现自然由弱到强的同心环，**两座基地之间的走廊天然处于弱~中怪区**便于中期打通会师（可顺势在中点放置宝箱/废墟防御塔），**地图四角外缘自动聚集高阶怪与 Boss**；
   - **巨人 & 变异体禁区**：`d_min >= 70`，防止一家的外围 Boss 贴脸刷在另一家门口；
   - **感染者村庄（Doom Village）**：要求 `d_min >= 16 + radius * 1.4`、避开基地间直连中轴走廊并按扇区大致均摊，保留单镇扩径（`DoomVillagesSize` 缩放）与跨界上限修正机制。

### 7.3 离线量化校验方案
在正式启动多基地地图生成器实现后，可复用 [`scripts/generate_panorama_comparisons.ps1`](../scripts/generate_panorama_comparisons.ps1) 的无界面批量导出能力，构建 **200 随机种子 × 6 种生态（`BR`/`AL`/`TM`/`DS`/`FA`/`VO`）** 离线审计脚本，量化输出各基地 `r=16 / 25 / 45` 圈内木材、食物潜力、石/铁/金/油井数量分布，验证死档率 `< 1%`、舒适开局率 `>= 85%~90%` 与轻度天谴率 `5%~10%` 的既定目标。

---

# 第三篇：分阶段开发路线图与验收门禁（Roadmap & Gates）

## 第 8 章：M1 ~ M4 里程碑排期与验收标准

依托现有 `v0.1.9` 执政官底座，Phase 4 划分为 4 个渐进式里程碑，**每阶段必须跑通对应自动化门禁后再推进下一阶段**（M1~M3 聚焦核心多阵营同步与玩法闭环，正式多基地地图生成器留待讨论定稿后于 M4 完整落地）：

| 里程碑 | 预计周期 | 核心交付物 | 主要代码落点（规划） | 验收门禁标准 |
| :--- | :---: | :--- | :--- | :--- |
| **M1**<br>**多基地 + 归属原型** | ~1 周 | ① 最小化多 CC 开局放置/测试自定义地图落位与开局各端相机定位至己方 CC（完整地图生成器暂搁置）；<br>② **共用电力网**多 CC 起点 BFS 修补；<br>③ `EntityOwnerMap` 全周期传播 + 操控隔离（先保留共享经济，专注跑通多基地、多路尸潮分流、**双胜负模式**不崩溃）；<br>④ 随快照同步 `EntityOwnerMap` 与规则标志。 | • `Sync/FactionOwnershipTracker.cs`<br>• `Patches/SurvivalBalancePatch.cs`<br>• `Patches/EnergyGridMultiBasePatch.cs`<br>• `Patches/CommandInterceptorPatch.cs`<br>• `Patches/LevelEndSyncPatch.cs`<br>• `Determinism/StateHasher.cs` | • `GameBridge 0 missing`、`Harmony failed-tolerant=0`；<br>• 2P 双开测试：两家各自操控己方单位/建造互不串控、共享铺电边界、多路尸潮分流攻击两座 CC、流亡交接与一损俱损均正常触发、`Auto-resync` 后归属不丢失、`0 ERROR`。 |
| **M2**<br>**独立经济账本、科技与 3+3 奇观分流** | ~1.5~2 周 | ① `FactionState[1..N]` 与 `ZXLevelState` 上下文切换（`Push/Pop`）框架；<br>② 覆盖回放、队列推进、拆毁退费、宝箱拾取、8h 结算、超仓卖货、破产裁兵与 `CommandCenterExtra*` 按 CC 隔离；<br>③ 保留跨阵营光环共享与 `TheVictorious` 原生叠加，隔离 `MaxInstances > 0` 建筑及 3 座产出类奇观按个人计数，实现 `TheSpire` 按人数加成（`+1200/+1600`）及**存活并网 / 被毁退回**能源池机制；<br>④ 市长方案 A（全局词条共享 + 个人奖励槽）与 `StateHasher` 纳入全 `FactionState[1..N]` 哈希。 | • `Sync/FactionState.cs`<br>• `Sync/FactionContextManager.cs`<br>• `Patches/FactionEconomyPatches.cs`<br>• `Commands/NetCommand.cs`<br>• `Determinism/StateHasher.cs` | • 双开并发压测：同帧建造/取消队列/拾取宝箱/跨 8h 结算/单方破产裁兵，双方账本彻底隔离；验证限数建筑双方互不挤占个人上限、光环与多座 `TheVictorious` 叠加生效、以及 `TheSpire` 建成并网/被毁退回独立供电无缝切换；<br>• `ChaosTransport` 30s 回归全程 `StateChecksum MATCH`。 |
| **M3**<br>**延迟优化、双阶段众筹与市场贸易 UX** | ~1.5 周 | ① 升级 **对称 P2P Turn 调度（0.5 RTT）**，叠加单写者 0ms 本地队列/幽灵预演；<br>② 实装 **任意一人建市场解锁全队转账**（整数 `20%` 手续费）、注入自定义 **“0 手续费市长”** 与 `ZeroTransferFeeActive` 同步；<br>③ 实装 **`WonderCrowdfundManager` 与轻量化众筹面板**（3 座功能奇观**研发 & 建造双阶段**均按 `1.5×/2.0×` 缩放并开放免手续费捐资/撤回/凭证落位）；<br>④ 单位脚底阵营色环渲染、小地图按阵营着色、大厅模式切换（执政官 / 独立基地 + 双胜负开关）。 | • `Sync/WonderCrowdfundManager.cs`<br>• `UI/WonderCrowdfundPanel.cs`<br>• `Sync/CoopSessionManager.cs`<br>• `Patches/MayorSyncPatch.cs`<br>• `UI/AllyOverlayRenderer.cs`<br>• `UI/CoopLobbyWindow.cs`<br>• `Commands/NetCommand.cs` | • `latency=80~120ms` 模拟下客机命令延时降至接近 `0.5 RTT + 1t`；<br>• 双开验证市场解锁转账（`100 -> 80` / 0 手续费市长 `100 -> 100`）；<br>• 2P/3P 验证在单人仅 2~3 座仓库不突破个人仓储上限下，通过双阶段众筹完成功能奇观研发与建造，`StateChecksum MATCH`。 |
| **M4**<br>**正式多基地地图生成器 & 全生态回归** | 待定 | ① 恢复讨论并定稿 `MultiBaseMapGenerator`（多基地选址、天谴率控制、资源配平与预设距离）；<br>② 200 种子 × 6 生态全景与开局资源统计扫描，验证死档率、舒适率与轻度天谴率目标。 | • `Sync/MultiBaseMapGenerator.cs`<br>• `Patches/SurvivalBalancePatch.cs`<br>• `scripts/audit_multibase_maps.ps1` | • 200×6 批量测试达到既定开局资源与“天谴图”控频目标；<br>• 3P 联机完整长局闭环通过。 |

---

### 附：规则状态汇总与待继续讨论清单

#### 已拍板定稿（可直接作为 M1~M3 设计与开发依据）
1. **电力网**：共用供电疆域边界，平时各家发电/耗电独立结算；建成存活 `TheSpire` 后激活全队能源池共享。
2. **市长系统**：方案 A（属性词条联盟共享，赠送单位/建筑/科技/资源包归个人）+ 新增联机专属“0 手续费市长”。
3. **3+3 奇观划分、定价与双阶段众筹**：
   - **功能性奇观（3 座：`TheGreatTelescope` / `TheAcademy` / `TheSpire`）**：全图限 1 座、团队共享效果；研发 & 建造两阶段均按 `1 + 0.5 × (N - 1)` 加算且**双阶段开启全队免手续费众筹**；`TheSpire` 电力同步按相同比例缩放并合并全队能源池。
   - **产出类奇观（3 座：`TheCrystalPalace` / `TheTransmutator` / `TheVictorious`）**：每人限建 1 座、原版 `1.0×` 价格、产出归个人；`TheVictorious` 允许跨阵营蹭光环且允许多座叠加。
4. **胜负规则**：支持“流亡交接（全灭才败）”与“硬核一损俱损”两种模式，开局前在大厅切换。
5. **光环与限建建筑**：允许跨阵营蹭光环；有数量限制（`MaxInstances > 0`）的建筑单人可用上限维持原版不变。
6. **资源转账**：任意一人拥有市场后全队激活，默认扣除 20% 手续费，选中“0 手续费市长”后降为 0%。

#### 待后续专题继续讨论（未最终定稿）
1. **多基地地图生成改造（暂搁置）**：确定正式启动多基地地图生成时的尺寸、底座间距、资源补齐尺度与“天谴图”阈值。
