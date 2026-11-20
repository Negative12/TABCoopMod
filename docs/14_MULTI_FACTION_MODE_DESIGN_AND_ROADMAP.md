# 14 — 多人多阵营（独立基地）模式设计规划与技术选型 (Phase 4)

> **整理日期**: 2026-11-20  
> **基准基线**: `v0.1.9`（执政官模式已跑通确定性 Lockstep、双栈直连 / Steam 中继、`coop_host_snapshot` 存档流与异步增量对齐；`dev/multi-faction` 分支已完成 Phase 4 M1~M3）  
> **当前状态**: **Phase 4 M1~M4 核心玩法、正式多基地生成器与多路尸潮已全部实现并验收通过**（共用电网边界、市长方案 A + 新增 0 手续费市长、3+3 奇观划分、功能奇观双阶段 `+50%/人` 众筹、闪电尖塔全队能源并网、胜利者雕像允许叠加、大厅可切双胜负模式、跨阵营光环共享且维持个人限建上限、市场解锁 20% 手续费资源转账；M4 已实装 `360/416` 格扩图、大厅可选聚集/分离双出生模式、共用电网飞地借矿、恶魔村仅扩数量 `1.5×/2.0×` 与 `>=32` 格单栋边缘安全裁剪、生成期三环僵尸浓缩与种子随机精英倾向、`N` 股独立尸潮与受控合流/借道、后期攻城变异体折算控帧）。  
> **定位**: 本文档面向 Phase 4「独立基地合作模式（Multi-Faction Co-op Mode）」开展立项规划沉淀。为避免业务规则与底层实现混杂，文档严格拆分为三部分：  
> - **第一篇（第 1~3 章）**：面向**策划与游戏性决策** —— 玩法边界、已定稿规则总表与 3+3 奇观众筹细则、M4 多基地地图生成/双出生模式/三环折算与多路尸潮设计；  
> - **第二篇（第 4~7 章）**：面向**开发与架构技术选型** —— `TheyAreBillions.exe` 逆向实锤、复杂度评估、阵营隔离方案、市场转账 / 自定义市长与奇观众筹实现、延迟与即时性分层结论、M4 `ZXMapDrawer` / `ZXLevelEvent` / `CUnitGenerator` 底层预规划；  
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

## 第 3 章：多基地地图规模、双出生模式、僵尸折算与多路尸潮设计（M4 已定稿）

> [!NOTE]
> **M1 原型缺陷复盘**：M1 原型（`reports/multifaction_panorama_report.html` 全 6 生态 24 图采样）暴露出三大结构性问题：① 沿用 `296/336` 格尺寸使 3P 人均土地仅单人原版 `0.57×`，外侧基地距边缘仅剩 `35` 格；② 1 号独占正中心、2/3 号流放中环 `~48` 格，导致 `60` 座采样基地中有 `36` 座 `<18` 格贴脸有怪，且副基地频现 `0 木` / `0 石` / `0 草` 或被湖/冰崖截断；③ 单股尸潮与未浓缩外围低级怪在多人大图下存在体验与帧率瓶颈。以下 M4 策划规则已全部拍板定稿。

### 3.1 地图有效尺寸 `NCells` 理论推导与定档
在引擎等距网格（`NCells > 256` 自动升格 `512` 底层网格并由 `FactorPlayableArea = NCells / 512` 裁剪正方形可玩区）中，从地图中心 `(256, 256)` 到左右可玩边界的欧氏距离满足：

$$R_{\text{edge}} \approx 0.289 \times \text{NCells}$$

扣去四周边缘固定约 **`14` 格**的尸潮刷怪条带与禁建缓冲后，单家独立基地需占用 **内环核心区（`0~18` 格）** + **中环农矿工业区（`18~36` 格）** + **外侧防线与恶魔村纵深（`50~55` 格）**。按“每家保留 `0.85×~1.00×` 单人可用地 + 分离外侧纵深足够刷出恶魔村与终局双层防线”反推并定档：

| 模式 / 档位 | `NCells` | 相对原版面积 | 人均分摊面积 | 中心至边界半径 | 推荐游荡密度系数 | 定位与纵深评估 |
| :--- | :---: | :---: | :---: | :---: | :---: | :--- |
| **1P 原版基准** | `256` | $1.00\times$ | $1.00\times$ | $74$ 格 *(可建 `60`)* | $1.00\times$ | 单核同心圆基准 |
| **M1 原型（淘汰）** | `296` / `336` | `1.34×` / `1.72×` | `0.67×` / `0.57×` | `85` / `97` 格 | `1.04×` / `1.08×` | 3P 空间严重过挤，外侧仅剩 `35` 格纵深 |
| ✅ **2P 独立基地** | **`360`** | **$1.98\times$** | **$0.99\times$** | **$104$ 格** | **`0.85×`** | 双家各自拥有近乎完整单人级发育圈与 `50~52` 格外防线纵深 |
| ✅ **3P 独立基地** | **`416`** | **$2.64\times$** | **$0.88\times$** | **$120$ 格** | **`0.80×`** | 三足鼎立：内留 `~50` 格中心会师腹地，外留 `~55` 格防线纵深 |

### 3.2 大厅可选「双出生模式」与「共用电网飞地借矿」机制
在开局等候大厅提供 **`出生模式（SpawnMode）`** 切换开关，所有模式均为每位玩家配齐**全额满编初始部队（每家 `4 游侠 + 1 士兵`，即 2P `8R+2S` / 3P `12R+3S`）**：

| 维度 | 模式 A：聚集出生（Clustered · 抱团联城） | 模式 B：分离出生（Separated · 诸侯分城） |
| :--- | :--- | :--- |
| **基地落位与间距** | 围绕地图中心盆地面对面紧邻落位，CC 间距 **`18 ~ 24` 格**。 | **彻底取消中心 `CC1` 特权**：全员绕中心对称分布（2P 对角 **`80 ~ 95` 格**，3P 正三角形 **`85 ~ 105` 格**），地图正中心留作无恶魔村的会师过渡区。 |
| **开局供电关系** | 因原版 CC 初始供电半径为 `14` 格，间距 `≤24` 格使 **Day 1 开局电网直接交叠并网**。 | 开局互不接通电网与视野边区，中期各自向地图中心清剿后接通电网会师。 |
| **发展与战术体验** | **合兵快扩、空间紧凑**：Day 1 即可全军合兵速推抢地、合建中心共享商业区（银行/市场/胜利者雕像光环），但内圈建房与草地需分扇区协商规划。 | **空间宽裕、前期孤立**：每家拥有完整 `360°` 内中环自由铺开，前期无法跨图派兵救火，依赖**市场转账**与中期会师协同。 |

**依靠「共用电网 + 飞地借矿」消除人工假矿印章**：
- **核心机制联动**：因为第 2 章已定稿“全队共用同一张供电疆域矩阵，且连通性校验认所有存活 `CommandCenter`”，即使在分离模式下两家尚未会师，只要队友家里电网覆盖了矿脉或林地草地，缺资源玩家即可**直接切屏在队友电网里拍下归属自己的采石场/锯木厂/农田（飞地借矿，产出直接进入本人独立账本）**。
- **地形与资源底线策略**：**不做破坏原版柏林噪声风貌的人工方格补矿印章**；仅在生成选址阶段保障 **“全图多基地合计不出现 0 石矿死档”**，让 `~80%~85%` 开局各家自然舒适，刻意保留 `~15%~20%` 单家局部偏穷（需去队友电网借地采矿或靠市场输血）的联机博弈乐趣。

### 3.3 僵尸城镇（Doom Village）仅扩数量、维持原版单镇尺寸与边缘安全隔离
在 `2.0× ~ 2.64×` 大地图与 $2\sim3$ 套独立经济/满编初始部队下，若仅扩单镇半径而不加数量，分离模式下会出现“恶魔村全刷在一人半区、其余玩家方向空旷无赏金可吃”的失衡；而如果在高密度图（如 `VO` 酸蚀之地）同时叠加单镇半径膨胀，会导致百栋巨型城镇切入分离基地周边：
- **数量扩充**：**2P 按原版随机数量 $\times \mathbf{1.5}$（向上取整且保底 $\ge 2$ 座）**；**3P 按原版随机数量 $\times \mathbf{2.0}$（且保底 $\ge 3$ 座）**，按玩家扇区均摊；分离模式下允许小概率（`35%`）在地图正中心会师腹地刷出 **1 座“中央恶魔村”**（满足距所有基地 $\ge 40$ 格）。
- **单镇半径与安全边界**：**维持原版 `1.0×` 单镇规模（`DoomVillagesSize` 不再额外放大）**，在提升城镇分布均衡度的同时避免出现巨型连片城镇；分离模式下选址额外避让原生城镇，并在后处理阶段强制执行**单栋建筑级边缘裁剪（每栋 `CInfectionNest` 距任意基地 $\ge 32$ 格）**。

### 3.4 外围游荡僵尸「生成期三环折算」、镇守巨人与「种子随机精英倾向」
为防止 `360 / 416` 格大图外围刷出海量低级杂兵拖垮 30Hz 单核物理帧，同时保留开局游侠练级手感，**在地图生成阶段**按多中心最近距离场 $d_{\min}(x,y) = \min_{k \in \{1..N\}} \text{dist}((x,y), \text{CC}_k)$（分离模式下对每个玩家外侧扇区附加小幅随机边界偏置，保留 `5%~10%` 局部高压压迫感）执行**三环阶梯浓缩**：

| 环带分区 | 距最近基地 $d_{\min}$ | 对应游戏阶段 | 浓缩规则与换算比例 | 实效与设计约束 |
| :--- | :---: | :--- | :--- | :--- |
| **0. 无怪安全圈** | `< 28` 格 | 开局落位安全带 | **100% 清空** | 彻底根除 M1 原型中的贴脸僵尸（`Z = 0~8`）。 |
| **1. 内环（游侠发育圈）** | **`28 ~ 48` 格** | 前期 (`Day 1~18`) | **`0%` 折算（100% 原版慢速小怪）** | 保证游侠能稳定风筝清地、攒老兵经验。 |
| **2. 中环（兵团推进圈）** | **`48 ~ 75` 格** | 中期 (`Day 18~45`) | **抽取 `50%` T1 低级怪 $\xrightarrow{4 : 1}$ T2 中级怪** | 换算为西装高管（`ZombieDressedA`）/ 奔跑者（`Runner`），该层实体数减少 `37.5%`。 |
| **3. 外环（重武决战圈）** | **`≥ 75` 格至边界** | 后期 (`Day 45+`) | **抽取 `70%` T1（`9 : 1`）+ `40%` T2（`3 : 1`）$\to$ T3 高级精英**；且在 **`d_min >= 85` 格每抽 `30` 只 T3 精英凝聚 `1` 只镇守巨人（Giant）** | 留 `30%` 原生小怪衬底；2P 额外凝聚 `+2~3` 只巨人、3P 额外 `+3~5` 只按外扇区均分；**野区游荡变异体（Mutant）严禁批量折算**（仅固定 `+1 / +1~2` 只且强制推至 $d_{\min} \ge 90$ 格，防前期噪音链式灭门）。 |

**基于地图种子的确定性「外围精英倾向（Seed-Driven Mutation Tendency）」**：
在房主生成地图瞬间，由 `MapSeed` 确定性派生本局外环（$d_{\min} \ge 75$ 格）与后期进攻尸潮的精英池权重（随初始 `.zxsav` 快照一次性同步，**零对齐隐患**；不开局明文剧透，鼓励玩家游侠探路或建成观察者奇观侦察）：

| 种子随机倾向 *(抽选权重)* | 外环 & 后期尸潮精英配比 *(胖子 : 喷毒 : 哈比)* | 额外 Boss 调整 | 针对性战术博弈引导 |
| :--- | :---: | :--- | :--- |
| **均衡标准型 (`35%`)** | **`45% : 35% : 20%`** | 标准数量外圈巨人 | 常规均衡配兵（石墙/震荡塔 + 狙击/泰坦）。 |
| **重甲肉潮型 (`25%`)** | **`70% : 15% : 15%`** | 外圈巨人数量额外 **`+30%`** | 克制纯粹震荡塔/火焰兵 AOE，逼迫转高单点爆发（老兵狙击/死神/机枪塔）。 |
| **腐蚀毒海型 (`20%`)** | **`20% : 65% : 15%`** | 标准巨人 | 克制扎堆步兵站桩，逼迫双层厚墙与远程泰坦/巨炮轰炸。 |
| **疾风猎杀型 (`20%`)** | **`20% : 20% : 60%`** | 后期进攻尸潮变异体折算率 **`+50%`** | 克制单层矮墙与脱节脆皮，逼迫双层石墙 + 荆棘陷阱 + 贴脸防翻墙火力。 |

### 3.5 $N$ 股独立尸潮分阶段缩放、受控合流/借道与后期防卡顿机制
鉴于开局游侠推进范围有限且各家需承担重复科技基建税（Day 1~25 单家实际兵力普遍弱于单人同期），**每次周期尸潮与最终尸潮改为同时刷新 $N$ 股独立分流**（分别锁定各存活基地）：

1. **三阶段单股规模缩放曲线**（以原版同波单股规模为 `100%` 基准）：
   - **第一阶段·前期（第 1~2 波，约 `Day 1~28`）**：**2P 每股 `65%~70%`**（全图总计 `1.3×~1.4×`）/ **3P 每股 `55%~60%`**（总计 `1.65×~1.8×`）—— 单路减压护航前期偏穷发育，但全队多线同时接战无人可划水；
   - **第二阶段·中期（第 3~5 波，约 `Day 29~55`）**：**2P 每股 `90%~95%`**（总计 `1.8×~1.9×`）/ **3P 每股 `80%~85%`**（总计 `2.4×~2.55×`）—— 破圈吃恶魔村与市场成型后逼近单人满额；
   - **第三阶段·后期与终局（第 6+ 波 & Final Swarm，`Day 56+`）**：**2P 每股 `105%~110%`**（总计 `2.1×~2.2×`）/ **3P 每股 `95%~100%`**（总计 `2.85×~3.0×`）。
2. **受控刷怪方向与「小概率合流 / 借道」高光联动**：
   - **前 1~2 波**：**`100%` 强制不同方向分进**（分离模式严格从各自基地朝外侧边缘刷出、聚集模式从互不重叠方向刷出，0% 合流/借道）；
   - **第 3 波起**：每波按 **`75% 多路分进` : `20% 双股同向合流或借道穿行` : `5% 三股合流（仅 3P）`** 确定性抽签。当两股同向或打 P2 的尸潮从 P1 外侧刷出借道硬穿时，安全侧队友可立即切屏利用**共用电网**直接在受袭队友防线上自费抢建弩车/震荡塔/双层墙或通过市场紧急转钱救场，把物理隔绝转化为极强的联机高光配合。
3. **后期尸潮防卡顿三项控制策略**（将同屏冲锋刚体峰值压在 **`< 5,500`** 安全水线以内）：
   - **只数封顶 + 精英提纯**：第 6 波起与最终尸潮为单股设置物理只数软上限（使全图同期总刷怪只数不超过原版 `1.35×~1.45×`），超额强度按种子随机倾向 `6:1 ~ 8:1` 浓缩为胖子/喷毒/哈比；
   - **攻城变异体（Mutant）折算**：后期周期尸潮与最终尸潮将约 `180~200` 只杂兵的溢出预算直接折算为 **`1~3` 只冲锋变异体**（最终尸潮每路额外附带 `1` 只随军巨人），以极少刚体提供真实破墙威胁；
   - **拉长吐怪持续时间**：当处于后期大波次或发生多股同向合流时，动态下调生成器的单步吐怪速率（拉长刷怪持续时间 `1.3× ~ 1.5×`），削减城墙下僵尸叠罗汉挤压峰值 `35%+`。

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

## 第 7 章：多基地地图生成与多路尸潮技术预规划（M4 逆向实锤与落地架构）

> [!NOTE]
> **逆向基线说明**：本章方案基于对 `TheyAreBillions.exe`（v1.0.14.29）核心类型 `#=zyl_NPjjlA7DRfVtsRJCX1kN4BxSr`（`MapGenerator`）、`ZX.GameSystems.ZXMapDrawer`、`ZX.ZXMapLayerZombies`、`ZX.GameSystems.ZXLevelEvent` 与 `ZX.Components.CUnitGenerator` 的完整反编译实锤制定。**M4 采用“原版生成底盘 + `Postfix` 网格级后处理 + 事件流控 Hook”架构**，彻底免除对 999 行原生生成器做脆弱的 IL Transpiler 改造或全量从零重写地形噪声。

### 7.1 为什么 M1 原型全景会出现 `Z = 0 ~ 8` 贴脸僵尸？（`ZXMapDrawer` 延迟实例化机制实锤）
反编译 `MapGenerator.#=zEzgd90E=(ZXRandomLevelParams)` 与 `ZXMapDrawer.#=zl2XxwLg=(DXLevel)` / `#=z0Pkwp_k=(DXLevel)` 确认：
1. **生成器返回时，树、矿与 1.5 万只游荡僵尸尚未成为 `DXEntity`**：
   - `#=zEzgd90E=` 执行完毕返回 `DXLevel` 时，地形、障碍物与普通游荡僵尸全数保存在 **`512×512` 的枚举二维网格**中（即 `ZXMapDrawer.LayerTerrain`、`LayerObjects`、`LayerZombies`）；此时 `ExtraEntities` 内仅包含 `CC1`、初始 `4R+1S`、`CInfectionNest`（恶魔村建筑）、`OilSource`、宝箱废墟、`OldTowers`、`Ravens`、野区 `Giants`/`Mutants` 以及 `4` 个边缘 `CUnitGenerator`（`"N"` / `"S"` / `"E"` / `"W"`）。
   - 真正的实体实例化要等到 `ZXSystemGameLevel.#=zmTU4kueQctVr`（`InitLevel`）调用 `ZXMapDrawer.#=zl2XxwLg=` $\to$ `#=z0Pkwp_k=` 时才会遍历 `LayerZombies[i, j]` 批量通过 `DXEntityPool.New` 生成单位。
2. **M1 原型漏删的根因**：M1 [`PlaceMultiFactionStarterBases`](../src/TABCoopMod/Sync/CoopBalanceManager.cs) 在 `OnGeneratePostfix` 里仅清理了 `extraEntities` 里的僵尸（仅误打误撞删了野区 Boss），完全没有擦除 `LayerZombies[x, y]` 网格，导致进入关卡后副基地原地刷出大批贴脸僵尸。
3. **M4 核心架构选型（`Postfix` 网格后处理）**：
   - 在 `SurvivalBalancePatch.Prefix` 仍沿用原版 `MapGenerator.#=zEzgd90E=` 生成天然基底地貌、矿脉与连通图（预设 `NCells = 360 / 416`，**维持 `1.0×` 原版 `DoomVillagesSize`** 并仅按 `1.5× / 2.0×` 上调 `NumDoomVillages` 补足恶魔村），随后在 **`SurvivalBalancePatch.Postfix`**（`ZXMapDrawer.#=zl2XxwLg=` 实例化之前）由 **`MultiBaseMapGenerator.PostProcessGeneratedLevel(...)`** 直接改写 `LayerTerrain`、`LayerObjects`、`LayerZombies`、`ExtraEntities` 与 `LevelEvents`。
   - **收益**：在 `< 5 ms` 内完成全部 26 万格重映射与多基地重排，零实体创建/销毁抖动，且由 Host 执行一次后经既有 `.zxsav` 快照同步至全体客机（**零网络同步负担**）。

### 7.2 `ZX.ZXMapLayerZombies` 25 项原生枚举直写与三环折算算法
反编译 `ZX.ZXMapLayerZombies`（25 个成员，含 `None`）及 `ZXMapDrawer.#=z0Pkwp_k=`（第 1364~1449 行）已实锤每个网格枚举值对应的精确单位实例化行为：

| `ZXMapLayerZombies` 枚举项 | 对应实例模板与每格刷怪数 | 所属浓缩阶梯 | M4 直写利用方式 |
| :--- | :--- | :---: | :--- |
| `Weak1` / `Weak2` / `Weak3` | `ZombieWeakA/B/C`（随机 `1~3` 只/格） | **T1 低级** | 内环（`28~48` 格）100% 保留 |
| `Runners1..3`、`Medium1..3` | `ZombieMediumA/B`、`ZombieWorkerA/B`（`Medium` 含 `DressedA`）`1~3` 只/格 | **T2 中级** | 中环（`48~75` 格）由 T1 按 `4:1` 升格写回 |
| `Strong1..3` | **单兵种直出 `ZombieDressedA`（西装高管）** `1~3` 只/格 | **T2 中级高阶** | 中环浓缩承接项 |
| `Chubbies1..3` | **单兵种直出 `ZombieStrongA`（胖子 800HP）** `1~3` 只/格 | **T3 精英（胖子）** | **外环按种子倾向比例直写** |
| `Venoms1..3` | **单兵种直出 `ZombieVenom`（喷毒者）** `1~3` 只/格 | **T3 精英（口水）** | **外环按种子倾向比例直写** |
| `Harpies1..3` | **单兵种直出 `ZombieHarpy`（哈比）** `1~3` 只/格 | **T3 精英（哈比）** | **外环按种子倾向比例直写** |
| `Powerful1..2` / `Ultra1` | `Chubby/Venom/Harpy` 混合 / `ZombieGiant` | T3 / Boss | 原版混合枚举，可用上面三组直出枚举精确控制比例 |

**`MultiBaseMapGenerator.RewriteZombieRings` 单遍网格重映射管线**：
1. **确定性倾向派生**：使用 `SplitMix64((ulong)seed ^ 0x4D3454454E44)` 抽取本局 `MutationTendency`（`Balanced 35%` / `HeavyArmor 25%` / `Caustic 20%` / `SwarmHunter 20%`），确定 `(wChubby, wVenom, wHarpy)` 权重；并在分离模式下为每家外侧扇区生成小幅边界距离微扰 `bias(angle) ∈ [-10, +4]` 格。
2. **单遍扫描 `LayerZombies[0..nCells, 0..nCells]`**：
   - 计算 $d_{\min}(x, y) = \min_{k} \text{dist}((x, y), \text{CC}_k) + \text{bias}$；
   - 若 $d_{\min} < 28$：**直接置 `ZXMapLayerZombies.None`**，彻底消除贴脸僵尸；
   - 若 $28 \le d_{\min} < 48$（内环）：保持原枚举（若原版在分离模式外侧误写为 `Medium`/`Strong`/`Powerful`，降格还原为 `Weak1..3`，确保游侠圈手感）；
   - 若 $48 \le d_{\min} < 75$（中环）：遇到 `Weak{k}` 时确定性以 `50%` 概率执行 `4:1` 累积配额折算（配额满 `4` 即写回 `Runners1` / `Medium1` / `Strong1`，否则清为 `None`）；
   - 若 $d_{\min} \ge 75$（外环）：对 `Weak{k}` 按 `70%` 抽取 `9:1` 记入 T3 精英配额、对 `Runners/Medium/Strong` 按 `40%` 抽取 `3:1` 记入 T3 精英配额；每累计满 `1` 单位 T3 精英配额：
     - 若 $d_{\min} \ge 85$，每第 `30` 份精英配额记入 `pendingCondensedGiants++`（不写入网格，上限 2P `+3` / 3P `+5`，重甲倾向额外 `+30%`）；
     - 其余配额按 `(wChubby, wVenom, wHarpy)` 权重写入 `Chubbies1` / `Venoms1` / `Harpies1`。
3. **Boss 实体实例化与防贴脸重定位**：
   - 将 `pendingCondensedGiants` 对应的 `ZombieGiant`（`#=zYLpZK4K9zSHp.#=zwwyTj9$ODjUy.#=zq6F4jtPoQUZV263PTQ==()`）均匀落位于各玩家外侧扇区且满足 $d_{\min} \ge 85$ 的 `LayerZombies`/可通行格，置 `CellsAwayCommandCenterWhenRoaming = 54` 并附加 `CMiniMapInfo.Auto`；
   - 遍历原生 `ExtraEntities` 中已有的 `ZombieGiant` 与 `ZombieMutant`（`#=zETCVfaScp7p0jCA9Ag==()`）：凡落在分离模式新基地安全半径内（巨人 $d_{\min} < 75$、变异体 $d_{\min} < 90$）者，重迁至对应外侧角区可通行点（不在野区批量新增变异体）。

### 7.3 双出生模式（`MultiFactionSpawnMode`）基地重定位、主连通选址与恶魔村补齐
1. **大厅协议同步**：
   - 在 `CoopSessionManager` 新增枚举 `public enum MultiFactionSpawnMode : byte { Clustered = 0, Separated = 1 }`，在 `CoopLobbyWindow`（仅当选择独立基地模式且开新生存局时）暴露切换按钮并通过 `PacketType.LobbyRosterSync`（`BroadcastLobbyRoster()`）同步客机。
2. **四步后处理管线**：
   - **Step A · 主连通陆地掩码提取**：在 `LayerTerrain` / `LayerObjects` 上从地图中心可建点执行一次 `bool[512, 512] mainWalkable` BFS，并复用原版 `dXNoyseLayer` 可玩矩形内边距裁剪（`margin >= 16` 格）。
   - **Step B · 双模式 `CC_1..CC_N` 落位与 `CC1` 迁出**：
     - **`Clustered`（聚集模式）**：保留中心盆地核心，`CC_1..CC_N` 落在半径 $R_{\text{ring}} = 10 \sim 13$ 格的紧凑环上（CC 互距 **`18 ~ 24` 格**），清理各自 `7×7` 垫基并校验两两间 `Earth/Grass` 连通；
     - **`Separated`（分离模式）**：**将原版生成的 `CC_1` 连同其初始 `4R+1S` 一并迁离正中心 `(256, 256)`**；在半径 $R_{\text{ring}} = 44 \sim 54$ 格（含由 `MapSeed` 决定的随机起始旋转角 $\theta_0$）的 $N$ 等分扇区（2P `180°`、3P `120°`）内，各采样 `24` 个 `mainWalkable` 候选点，按`周围可建面积 + 树木 + 矿/草`综合评分从 Top-4 加权抽选（使 80%~85% 舒适、15%~20% 偏苦但全队合计保底至少 1 家 `stone24 >= 4`）；为 `CC_2..CC_N` 创建指挥中心与各自 `4R+1S` 并调用 `FactionOwnershipTracker.RegisterPendingCcOwners(placedCcs)`。
   - **Step C · 轻量级 A* 会师走廊打通**：
     - 对相邻两座基地检查在 `mainWalkable` 上的连通状态与绕行比（若不可达或绕行长度 `> 2.5 ×` 直线距离），仅在必需时使用带权 A* 穿过山体/水体最小代价格，将路径宽 `4~5` 格强制刷为 `ZXMapLayerTerrainType.Earth` 且清除 `Mountain/Tree`，确保共用电网与中期会师通路畅通。
   - **Step D · 恶魔村仅扩数量补齐与单栋边缘安全裁剪**：
     - **安全区重迁与边缘裁剪**：分离模式选址阶段即对贴近原生 `CInfectionNest`（`< 38` 格）的候选点高额罚分并预留 `pad = 6` 格；聚类时限制跨栋链接距离（相邻 `<= 12` 且距种子 `<= 28` 格，避免巨型城镇连片黏连）；凡某恶魔村质心落入 `d_min < 28 + radius` 或任意单栋建筑距基地 `< 34` 格（或被聚集基地压占）者，整簇平移至合法环带；迁出与补齐结束后再执行一遍**逐栋扫描裁剪，确保地图上任意 `CInfectionNest` 建筑距所有基地均为 `>= 32` 格**；
     - **数量补齐**：`CoopBalanceManager.OnGeneratePrefix` **保持 `DoomVillagesSize` 为 `1.0×` 原版尺寸**，仅将 `NumDoomVillages` 按 `1.5×`（2P 保底 `2` 座）/ `2.0×`（3P 保底 `3` 座）临时上调并在 `Finalizer` 还原；若因原版随机范围或重迁空间不足导致少于目标数，则在各玩家外侧扇区可通行空地补充生成 `CInfectionNest` 建筑簇，并在生成瞬间抹平其占地脚底障碍。

### 7.4 $N$ 股独立尸潮、受控合流/借道与攻城变异体折算 Hook 架构
反编译 `ZXLevelEvent` 与 `CUnitGenerator` 实锤了发动机原生的“多生成器批量出怪”结构，**无需重写整套事件引擎，只需 3 个轻量级 Hook 即可完美支持 $N$ 股独立尸潮**：

- **原版底层机制实锤**：
  1. `MapGenerator.#=zuJRpnkt4f2Fr(DXLevel)` 在 `PlayableArea` 四周创建 4 个 `CUnitGenerator` 实体（`Configuration.Name` 为 `"N"`、`"S"`、`"E"`、`"W"`）；
  2. `ZXLevelEvent.#=zUMCW0sQ=()` 返回 `List<CUnitGenerator>`（即 `CurrentUnitGenerators`）；
  3. `ZXLevelEvent.#=zrITPs$A=()` 在事件触发时，**直接循环遍历 `foreach (CUnitGenerator gen in CurrentUnitGenerators)`**，为列表中的每一个 `gen` 独立生成唯一 `ulong num2 = DXGuid.New()`、创建一个独立的 `ZXMiniMapIndicatorInfectedSwarn`（小地图骷髅头与波次指示），并向该 `gen` 投递 `CUnitGenerator.GenerationRequest`（携带 `IDEvent = num2` 与 `GenerationSpeed`）；
  4. 当允许列表内存在重复的 `CUnitGenerator` 引用（例如 2 股同向合并时列表 `[N, N]`）时，**循环天然会投递两笔具有不同 `IDEvent` 的出怪请求并同时在 `CUnitGenerator` 执行**！

据此设计新增补丁类 **`MultiWaveSwarmPatches.cs`** 的 3 项精准 Hook：
1. **分流/合流方向调度 Hook（`Prefix/Postfix` on `ZXLevelEvent.#=zUMCW0sQ=()`）**：
   - 仅在 `CoopSessionManager.IsMultiFactionActive && ev.AttackCommandCenter && !ev.FinalSwarm && ev.MaxRepetitions <= 5`（即有倒计时的周期尸潮；零散无倒计时骚扰波同理按单股轮询各家）生效：
   - 对存活基地 `pid ∈ AlivePlayers` 按确定性种子 `SplitMix64(mapSeed ^ ev.ID ^ ev.NTimesTriggered)` 分配出怪方位：
     - **Wave 1~2**（`ev.NTimesTriggered < 2` 且处于早期）：`100%` 为每个存活 `CC_k` 匹配其所在象限朝外的 `N/S/E/W` 生成器（聚集模式强制选取互不相同的方向）；
     - **Wave 3+**：按 **`75%` 各取外侧不同生成器 / `20%` 随机抽取 1 股改用队友的外侧生成器（产生双股合流或借道穿行） / `5%`（仅 3P）全员统一至同一生成器** 构造 `List<CUnitGenerator>`（按序对应 `pid_1..pid_k`），并将对应目标列表记录至 `PendingSubSwarmTargets`（同步保证 `ZXLevelEvent.#=z9kNBieQ=()` / `#=zBAspRIo=()` 倒计时通知时能正确拼接多方向或合流告警提示）。
2. **每股独立缩容与后期变异体折算 Hook（`Postfix` on `ZXLevelEvent.#=zHI6D76EGh68R()` & `CoopBalanceManager.ScaleLevelEvents`）**：
   - 先由 `ScaleLevelEvents` 在 `OnGeneratePostfix` 依据阶段时间（第 1~2 波 vs 中期 vs 第 6+ 波/最终波）将**基础模板单股规模**缩放到目标百分比（前期 `68%/58%`、中期 `92%/82%`、后期 `108%/98%`）；
   - 在 `ZXLevelEvent.#=zHI6D76EGh68R()` 返回 `Dictionary<DXEntityTemplate, int>`（当次单股各兵种最终数量，已自动乘上 `(1 + factor)` 重复倍率，**若用于 `FinalSwarm` 4 个边缘生成器则先均摊除以 4 再按股压缩**）后：
     - 统计单股总只数 `totalUnits`；若处于后期（`ColonyHours >= 55 * 24` 或 `FinalSwarm`）且 `totalUnits` 超出物理软上限（周期波单股 `> 850`、最终波单股 `> 1,400`），将超额普通怪（优先扣除 `ZombieWeak*`/`ZombieMediumA/B`/`ZombieWorkerA/B`）：
       - **`75%`** 按 `7 : 1` 换算并按本局 `MutationTendency` 权重写入 `ZombieStrongA`、`ZombieVenom`、`ZombieHarpy`；
       - **`25%`** 按 `180 : 1` 折算注入 **`1 ~ 3` 只 `ZombieMutant`**（哈比猎杀倾向 `+50%`；`FinalSwarm` 额外每路固定附带 `1` 只 `ZombieGiant`）。
3. **每股靶心绑定与合流/后期刷怪降速削峰（`Postfix` on `ZXLevelEvent.#=zrITPs$A=()` + `Prefix/Postfix` on `CUnitGenerator.#=zzHXxhbjNa0Zf()`）**：
   - 在 `#=zrITPs$A=()` 为每个子波生成 `IDEvent = num2` 时，将 `num2 -> TargetPlayerId` 写入确定性映射表 `SubSwarmTargetByEventId`（随 `.zxsav` 或确定性事件序列持久化）；若某生成器在同一波被合流分配了 `M >= 2` 股或处于后期压缩大波，直接下调 `GenerationRequest.GenerationSpeed`（默认 `6` $\to$ `3~4`，多股共享时按 `Math.Max(2, 6 / M)` 分流限速）以拉长出怪持续时长 `1.3× ~ 1.5×`；
   - 在 `CUnitGenerator.#=zzHXxhbjNa0Zf()` 为 `generationRequest2` 调用 `CreateInstance()` 生成每只 `Zombie` 并置 `AttackCommandCenter = true` 时，将其锁定靶心绑定为 `SubSwarmTargetByEventId[generationRequest2.IDEvent]`（由 `FactionOwnershipTracker.PickTargetCommandCenterForZombie` 优先读取该固定靶心 CC，若该 CC 已在流亡模式下被摧毁则自动就近切换至存活 CC）。

### 7.5 离线全生态量化审计与压力验收方案
M4 编码完成后，依托 [`scripts/generate_panorama_comparisons.ps1`](../scripts/generate_panorama_comparisons.ps1) 的无界面批量导出管线构建自动化审计脚本 **`scripts/audit_multibase_maps.ps1`**：
- **样本规模**：**200 随机种子 × 6 生态（`BR`/`AL`/`TM`/`DS`/`FA`/`VO`）× 2 出生模式（`Clustered`/`Separated`）**；
- **量化验收指标**：
  1. `nearestZombieDist >= 28` 格达标率 **`100%`**（零贴脸开局）；
  2. 全队合计 `sum(stone24) >= 4` 达标率 **`>= 99.5%`**；单家舒适率 `~80%~85%`、单家偏穷借地率 `~15%~20%`；
  3. 3P `416` 格全图初始僵尸总数稳定在 **`13,000 ~ 19,000`** 只（且呈现明显的“内圈 T1 / 中圈 T2 / 外圈种子倾向精英 + 镇守巨人”分层）；
  4. 实机运行周期尸潮（含前期分进、第 3+ 波 20% 合流/借道与第 6 波后期变异体压缩）与 `FinalSwarm`，双端 `StateChecksum` 全程 `MATCH`、逻辑单核帧耗时 `< 33 ms`。

---

# 第三篇：分阶段开发路线图与验收门禁（Roadmap & Gates）

## 第 8 章：M1 ~ M4 里程碑排期与验收标准

依托现有 `v0.1.9` 执政官底座，Phase 4 划分为 4 个渐进式里程碑，**每阶段必须跑通对应自动化门禁后再推进下一阶段**（M1~M3 聚焦核心多阵营同步与玩法闭环，正式多基地地图生成器留待讨论定稿后于 M4 完整落地）：

| 里程碑 | 预计周期 | 核心交付物 | 主要代码落点（规划） | 验收门禁标准 |
| :--- | :---: | :--- | :--- | :--- |
| **M1**<br>**多基地 + 归属原型**<br>**`✅ 已完成 (2026-11-20)`** | ~1 周 | ① 最小化多 CC 开局放置/测试自定义地图落位与开局各端相机定位至己方 CC（完整地图生成器暂搁置）；<br>② **共用电力网**多 CC 起点 BFS 修补；<br>③ `EntityOwnerMap` 全周期传播 + 操控隔离（先保留共享经济，专注跑通多基地、多路尸潮分流、**双胜负模式**不崩溃）；<br>④ 随快照同步 `EntityOwnerMap` 与规则标志。 | • `Sync/FactionOwnershipTracker.cs`<br>• `Patches/FactionOwnershipPatches.cs`<br>• `Patches/EnergyGridMultiBasePatch.cs`<br>• `Patches/CommandInterceptorPatch.cs`<br>• `Patches/LevelEndSyncPatch.cs`<br>• `Determinism/StateHasher.cs` | • `GameBridge 0 missing (verified=166)`、`Classes OK=78, failed-tolerant=0`；<br>• 实测通过 [`verify_multifaction_m1.ps1`](../scripts/verify_multifaction_m1.ps1)（`AllBasesLost` 流亡交接 + 共用供电 + 操控隔离）、[`verify_multifaction_survival.ps1`](../scripts/verify_multifaction_survival.ps1)（新生存多基地 + `AnyBaseLost`）与执政官 [`run_replay_verify.ps1`](../scripts/run_replay_verify.ps1)（120t `identical`）。 |
| **M2**<br>**独立经济账本、科技与 3+3 奇观分流**<br>**`✅ 已完成 (2026-11-20)`** | ~1.5~2 周 | ① `FactionContextManager` 与 `ZXLevelState` 上下文 `PushContext/PopContext` 切换框架；<br>② 覆盖回放、队列推进、拆毁退费、宝箱拾取、8h 结算、超仓卖货、击杀分钱与 `CommandCenterExtra*` 按 CC 隔离；<br>③ 保留跨阵营光环共享与 `TheVictorious` 原生叠加，隔离 `MaxInstances > 0` 建筑及 3 座产出类奇观按个人计数，实现 `TheSpire` 按人数加成（`+1200/+1600`）及**存活并网 / 被毁退回**能源池机制；<br>④ 市长方案 A（全局词条共享 + 个人奖励槽）与 `StateHasher` 纳入全 `FactionLedger[1..N]` 哈希。 | • `Sync/FactionContextManager.cs`<br>• `Patches/FactionEconomyAndWonderPatches.cs`<br>• `Commands/NetCommand.cs`<br>• `Determinism/StateHasher.cs`<br>• `Sync/FactionOwnershipTracker.cs` | • `GameBridge 0 missing (verified=197)`、`Classes OK=97, failed-tolerant=0`；<br>• 实测通过 [`verify_multifaction_m2_m3.ps1`](../scripts/verify_multifaction_m2_m3.ps1)：双端 `economyHash` 严格一致、`TheSpire` `12000G/225S/300I/150O` 完工并网后 `SharedPool=133/1990`（`P2 own=1850 >= 1200`, `P1=P2=1990`）、`0 DESYNC`、`0 ERROR`。 |
| **M3**<br>**延迟优化、双阶段众筹与市场贸易 UX**<br>**`✅ 已完成 (2026-11-20)`** | ~1.5 周 | ① 实装 **对称 P2P Turn 调度（`PacketType.SymmetricTurnInput = 0x24`，`0.5 RTT`）** 与主机统一 `(TargetSimTick, PlayerId, TraceId)` 确定性归并排序；<br>② 实装 **市场解锁全队转账**（整数 `20%` 手续费）、注入自定义 **`CoopMayor_ZeroTransferFee` 0 税市长**与 `ZeroTransferFeeActive` 同步；<br>③ 实装 **`WonderCrowdfundManager` 与 `F7 MultiFactionPanel` 双标签面板**（3 座功能奇观**研发 & 建造双阶段**均按 `1.5×/2.0×` 缩放，支持捐资/撤回/凭证落位与非阻塞完工横幅）；<br>④ 实装单位脚底等距阵营色环渲染（`AllyOverlayRenderer`）与 `ZXEntity.MinimapColor` 按阵营小地图着色。 | • `Sync/WonderCrowdfundManager.cs`<br>• `UI/MultiFactionPanel.cs`<br>• `Sync/CoopSessionManager.cs`<br>• `Patches/FactionEconomyAndWonderPatches.cs`<br>• `UI/AllyOverlayRenderer.cs`<br>• `Networking/INetworkTransport.cs`<br>• `Commands/NetCommand.cs` | • 实测通过 [`verify_multifaction_m2_m3.ps1`](../scripts/verify_multifaction_m2_m3.ps1)：`100 Gold -> +80 Gold`（20% 税）、`CoopMayor_ZeroTransferFee` 后 `50 Wood -> +50 Wood`（0% 税）、Spire 双阶段众筹+撤回+零成本凭证建造全程 `StateChecksum MATCH`、`0 ERROR`；<br>• 4 张 HUD/色环/小地图截图经目视校验清晰可用；回归 [`verify_multifaction_m1.ps1`](../scripts/verify_multifaction_m1.ps1) 8/8、[`verify_multifaction_survival.ps1`](../scripts/verify_multifaction_survival.ps1) 5/5 与执政官 `run_replay_verify.ps1` 120t `identical` 全部通过。 |
| **M4**<br>**正式多基地地图生成器、双出生模式与多路尸潮**<br>**`✅ 已完成 (2026-11-20)`** | ~1~1.5 周 | ① 扩图至 `360`（2P）/ `416`（3P），大厅实装 `MultiFactionSpawnMode` 切换（聚集 `18~24` 格 Day 1 并网 vs 分离 `80~105` 格去中心对称分布，依赖共用电网飞地借矿不做人工假矿）；<br>② 实装 `MultiBaseMapGenerator` 网格级 `Postfix` 后处理管线：主连通选点（含迁出 `CC1` + 选址避让城镇）、轻量级 A* 会师走廊、恶魔村**仅扩数量 `1.5×/2.0×` 维持 `1.0×` 单镇半径**、重迁与 **`>= 32` 格逐栋边缘裁剪**、三环僵尸直写浓缩（内环不动 / 中环 `4:1` / 外环 `9:1` + 镇守巨人凝聚 + 种子随机精英倾向）；<br>③ 实装 `MultiWaveSwarmPatches`：$N$ 股独立尸潮 3 阶段缩容（`68%/58%` $\to$ `92%/82%` $\to$ `108%/98%` + FinalSwarm `2.10×/2.70×`）、Wave 1~2 `100%` 独立外侧出兵点、Wave 3+ `75%/20%/5%` 分流/合流借道、后期变异体折算与降速削峰。 | • `Sync/MultiBaseMapGenerator.cs`<br>• `Sync/CoopBalanceManager.cs`<br>• `Sync/FactionOwnershipTracker.cs`<br>• `Patches/SurvivalBalancePatch.cs`<br>• `Patches/MultiWaveSwarmPatches.cs`<br>• `UI/CoopLobbyWindow.cs`<br>• `scripts/verify_multifaction_m4.ps1`<br>• `scripts/generate_m4_showcase_panoramas.ps1` | • Release `0 警告 0 错误`；<br>• [`verify_multifaction_m4.ps1`](../scripts/verify_multifaction_m4.ps1) 全通：Clustered 2P 双端房间同步 (`sep=21.5`, Tick 1 `StateChecksum MATCH`)，`BR/AL/VO/DS` 实时多向尸潮分发与 FinalSwarm 变异体/巨人压缩审计通过；<br>• 6 生态 × 4 模式共 **24 张 2048px 全景审计**（[`multifaction_m4_panorama_report.html`](../reports/multifaction_m4_panorama_report.html)）：`allCcsConnected=24/24`、`nearestZombieDist>=28` 24/24 (`0` 例贴脸)、`nearestDoomDist>=32` 24/24 (`minD=34.8`，`VO 2P/3P` 恶魔村实测 `157/237` 且 `minD=47.6/56.4`)、`Separated` 全队 `stone24` 最低 `37`。 |

---

### 附：规则状态汇总（M1~M4 全部规则与代码交付已完成）

#### 已拍板定稿并实装（M1~M4 全部落地）
1. **电力网**：共用供电疆域边界，平时各家发电/耗电独立结算；建成存活 `TheSpire` 后激活全队能源池共享。
2. **市长系统**：方案 A（属性词条联盟共享，赠送单位/建筑/科技/资源包归个人）+ 新增联机专属“0 手续费市长”。
3. **3+3 奇观划分、定价与双阶段众筹**：
   - **功能性奇观（3 座：`TheGreatTelescope` / `TheAcademy` / `TheSpire`）**：全图限 1 座、团队共享效果；研发 & 建造两阶段均按 `1 + 0.5 × (N - 1)` 加算且**双阶段开启全队免手续费众筹**；`TheSpire` 电力同步按相同比例缩放并合并全队能源池。
   - **产出类奇观（3 座：`TheCrystalPalace` / `TheTransmutator` / `TheVictorious`）**：每人限建 1 座、原版 `1.0×` 价格、产出归个人；`TheVictorious` 允许跨阵营蹭光环且允许多座叠加。
4. **胜负规则**：支持“流亡交接（全灭才败）”与“硬核一损俱损”两种模式，开局前在大厅切换。
5. **光环与限建建筑**：允许跨阵营蹭光环；有数量限制（`MaxInstances > 0`）的建筑单人可用上限维持原版不变。
6. **资源转账**：任意一人拥有市场后全队激活，默认扣除 20% 手续费，选中“0 手续费市长”后降为 0%。
7. **多基地地图生成、双出生模式、三环折算与多股尸潮（M4 定稿）**：
   - **尺寸定档**：**2P `360` 格**（`0.85×` 游荡密度）/ **3P `416` 格**（`0.80×` 游荡密度）；
   - **双出生模式**：大厅可切换 **聚集出生（`18~24` 格，Day 1 并网快扩）** 与 **分离出生（`80~105` 格，全员去中心化对称展开，正中心做会师腹地）**；依赖**共用电网飞地借矿**应对单家偏穷开局，不做人工假矿印章；
   - **恶魔村扩充**：维持原版 **`1.0×`** 单镇半径，仅按 `1.5×`（2P 保底 `2` 座）/ `2.0×`（3P 保底 `3` 座）扩充数量，并强制任意 `CInfectionNest` 建筑距所有基地 **`>= 32` 格**；
   - **生成期三环浓缩与种子倾向**：`d_min < 28` 清空、`28~48` 留 T1、`48~75` `4:1` 换 T2、`>= 75` `9:1`/`3:1` 升 T3 精英并按 `30:1` 凝聚外围镇守巨人（严控野区变异体）；外环精英配比与后期偏向由 `MapSeed` 确定性派生 4 类主题倾向；
   - **$N$ 股尸潮与控帧**：每次刷新 $N$ 股独立分流（前/中/后期单股规模 `65%~70%` $\to$ `90%~95%` $\to$ `105%~110%`），前 2 波 100% 分进，第 3 波起按 `75% 分进 / 20% 双股合流或借道 / 5% 三股全合流` 触发协防高光；后期通过只数封顶 + 精英提纯 + `180:1` 攻城变异体折算 + 降速拉长刷出时间锁死卡顿。
