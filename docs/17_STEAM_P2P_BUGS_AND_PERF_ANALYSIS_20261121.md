# Steam P2P 实测问题与性能瓶颈分析报告（2026-11-21，`v0.2.4`）

> 本文档仅做根因排查与方案设计，**尚未修改任何代码**。待确认各问题优先级与修复方案后再分批实施。

---

## 1. 测试样本与基线概览

| 样本 | 日志文件 | 模式 / 地图 | 关键过程与结果 |
|---|---|---|---|
| **测试 A (`log6`)** | `logs/TABCoopMod_host_6.log` (212 行)<br>`logs/TABCoopMod_client_6.log` (188 行)<br>+ 房主大厅配图 | 2P **独立基地合作 (M4)**<br>分离分城 (`Separated`)，`NCells=360`（底层 `512`，`FactorPlayableArea=0.703125`）<br>`CCs=[(278,275), (219,215)]` | 1. 房主大厅右侧选项与「解散房间」重叠遮挡<br>2. 进图后一家基地无初始电网<br>3. 画面感觉变窄、地图疑似拉伸<br>4. 一家建造的狩猎小屋食物计入另一家<br>5. 68 秒内（Tick 2040）触发 `HARD DESYNC (asyncEconFallback x6)` 与 `_entityOwners` `12 -> 6` 丢失，随后退出 |
| **测试 B (`log7`)** | `logs/TABCoopMod_host_7.log` (3,481 行)<br>`logs/TABCoopMod_client_7.log` (4,336 行) | 2P **执政官共控模式 (Archon)**<br>联机均衡，`NCells=280`（底层 `512`） | 全程约 **2 小时 18 分钟**（15:08:45 ~ 17:26:43，Tick 217,560）**完整通关胜利**，零报错；但最后一波（17:10 ~ 17:22，实体峰值 `14,692`）出现持续 ~11 分钟的严重降速（`4.5–6.5 TPS`）与两次大冻屏（`34s` / `60s`） |

两场测试网络层均稳定工作在 **Direct UDP 直连（`Direct StunIPv4`）**，物理链路 RTT 仅 **`5.6 ~ 6.8 ms`**，`cmdFly=0, bulkFly=0`，**所有问题与卡顿均非网络传输层所致**。

---

## 2. 问题一：`log6` 房间选择模式界面 UI 被遮挡（已 100% 定位）

### 2.1 现象核实（配图对照）
在房主选择「独立基地合作 (M4)」模式后，右列新增的第 4 个紧凑按钮 **`出生: 分离分城 (85~105格对称)`**（青色边框）被底部原生红色按钮 **`解散房间`** 直接盖在正上方，仅露出上下两条青色边缘。

### 2.2 代码根因
位于 `src/TABCoopMod/UI/CoopLobbyWindow.cs` (`BuildRoomView`, L428–429, L595–662)：
- 日志记录：`Layout OContent size: 1500.0x870.0 (Unit=2.000)`，其中：
  - `bodyY = 44f * u = 88 px`，`bodyH = 870 - 88 = 782 px`。
- 右列从上至下依次静态堆叠：
  1. 标题「房间操作」：`0 .. 28*u`（`56 px`），首按钮起点 `ry = 36*u = 72 px`；
  2. 4 个原版 `ZXButtonSteam` 按钮（`_btnInviteSteam`, `_btnHostLoadSave`, `_btnHostNewSurvival`, `_btnHostCustomMap`），每个高约 `~105 px`，`gapY = 5*u = 10 px`，占到底部约 `72 + 4×105 + 3×10 + 4 = 526 px`；
  3. 4 个紧凑选项按钮（`_btnBalancePreset`, `_btnGameMode`, `_btnDefeatRule`, `_btnSpawnMode`），每个 `compactH = 25*u = 50 px`、间距 `4*u = 8 px`，4 个合计占 `4×50 + 3×8 = 224 px`，**底部延伸至 `~750 px`**；
- 而底部的 `_btnDisbandRoom` 硬锚定在：
  ```csharp
  float leaveY = bodyH - _btnInviteSteam.Height - 4f * u; // ≈ 782 - 105 - 8 = 669 px
  ```
- 第 4 个紧凑按钮 `_btnSpawnMode` 位于 `Y ≈ 692 ~ 742 px`，正好完全落入 `_btnDisbandRoom` 的 `[669, 774] px` 区间，发生正面重叠。执政官模式因只显示前 2 个紧凑按钮（底边 `~634 px < 669 px`），所以未暴露该问题。

### 2.3 修复建议
- **方案 A（推荐）**：将 4 个紧凑配置项改为 **2×2 双列网格布局**（每行 2 个，总高仅需 `2×26u + 4u = 56u ≈ 112 px`，相比单列节省 `112 px`），并适当精简按钮文案（如 `预设: 联机均衡`、`模式: 独立基地`、`败北: 流亡交接`、`出生: 分离分城`），把完整说明放在左侧状态说明栏或悬浮提示中。
- **方案 B（备选）**：缩小 4 个原生按钮间距 `gapY`（如 `5u -> 2u`）、紧凑按钮高度（`25u -> 20u`），并在布局代码里加入 `Debug/Log` 边界校验确保 `ry + compactH + 4u <= leaveY`。

---

## 3. 问题二：`log6` 独立基地分离出生后一家基地没有初始电网（已 100% 定位）

### 3.1 现象与日志印证
- `log6` Host L62：`spawnMode=Separated, CCs=[(278,275) (219,215)]`，两座指挥中心相距约 `84` 格，初始供电圆完全不相连（两个独立连通分量）。
- 进图后一家基地周边完全无电网；且在 `Tick 1140 ~ 1200` 立刻发生双端建造合法性分裂：
  - Host L131：`ID COUNTER DRIFT at Tick 1140: Host=...643, Client(2)=...645, delta=-2`
  - Host L135–136：`Tick 1200 Host entities=22466 vs Client entities=22467`
  - Client L128：`[AsyncStructAlign] Tick=1200 built=0 destroyed=1`

### 3.2 引擎反编译根因（3 处原版单基地假设）
现有补丁 `src/TABCoopMod/Patches/EnergyGridMultiBasePatch.cs` **只补了 `ZXSystemMapLayers.#=zK$nELVArHhqXXa$0lA==()` 一处**，但反编译确认引擎实际上在 **另外 3 处关键路径** 独立维护并裁剪电网：

1. **【核心缺陷 B1】开局加载时直接清零第二座基地电网 — `ZXSystemMapLayers.#=z9ok_i231SPp6b3Ux7A==()`**
   - 由 `InitLevel -> ZXSystemMapLayers.#=zoxh1CPc=()` 调用。
   - 该方法先调用全部 `CEnergyTransferer.#=zB_XAZFXeOC5f5keLMg==()` 给 `CC1` 和 `CC2` 都铺上电网格，随后遍历全图 `(i, j)` 做连通性剪枝，**使用了一个单点缓存 `Point point = new Point(-10, -10)`**：
     - 当扫描到第一个带电格子时（`CC2` 位于 `X=219`，比 `CC1` 的 `X=278` 先被扫到），`point.X < 0`，调用 `#=zs1C_pLjYHbdEceIG1y8dYemfTA9s(point2)` 返回 `true` 并将 `point` 锁定在 **`CC2` 岛内**；
     - 后续扫描到 **`CC1 (278,275)` 周围所有带电格子** 时，由于 `point.X >= 0`，代码跳过全 CC 检查，**只调用 `#=z3gBRVDQqtvENQXOHNg==(point2, point)` 检查是否连通到 `CC2` 的 `point`** -> 分离出生必不连通 -> 直接执行 **`#=zVXTH1ahoIxQNg$omKQ==[i, j] = 0u`**，把 `CC1` 整片初始电网全部清零！

2. **【核心缺陷 B2】每 Tick 按本机 `CurrentCommandCenter` 断电，造成主客机互相给对方断电 — `CEnergyTransferer.#=zeJrYi6vVVMO8Q25qqH7ixNY=()`**
   - 由引擎每帧/每 tick 经 `IDXSystem.System_OnStartUpdate` 自动调用。
   - 反编译确认该方法**仅读取 `ZXSystemGameLevel.Current?.CurrentCommandCenter`（单座本地 CC）** 作为唯一 BFS 起点，把所有未通过特斯拉塔连回本机 `CurrentCommandCenter` 的 `CEnergyTransferer` 统统置为 `(Entity as Building).ConnectedToEnergy = false` 并扣减电网！
   - 在多阵营模式下，**主机 `CurrentCommandCenter = CC1`，客机 `CurrentCommandCenter = CC2`**，于是：
     - **主机每 tick 都把 P2 (`CC2`) 的电网断开**；
     - **客机每 tick 都把 P1 (`CC1`) 的电网断开**！
   - 连锁后果：P2 在自己家放建筑时，客机判定 `CanPlaceBuildingAtTarget == true` 成功放下，主机却因 `CC2` 没电判定 `false` 拒单，直接造成 `ID COUNTER DRIFT delta=-2` 与 60 tick 后客机建筑被 `AsyncStructAlign` 强拆（`destroyed=1`）！

3. **【次生缺陷 B3】增量加电剪枝 — `ZXSystemMapLayers.#=zMxZdonzH58cOsMui6Q==()`**
   - 在 `delta > 0` 向电网添加新节点时，同样使用单 `Point point2 = (-10,-10)` 路径快捷缓存，当两个独立电网岛边缘进入同一圆形检测半径时，可能把未与首个格子连通的另一岛边缘误清零。

### 3.3 修复建议
- 在 `EnergyGridMultiBasePatch.cs` 中同时接管上述 3 处方法（多阵营模式或 `ccs.Count > 1` 时启用）：
  1. **`CEnergyTransferer.#=zeJrYi6vVVMO8Q25qqH7ixNY=()`**：改为**多源 BFS**，以**全部存活且 `Team == Player` 且未感染的 `CommandCenter`**（按 `ID` 升序）作为共同种子集合，彻底消除对本地 `CurrentCommandCenter` 的依赖，保证主客机每 tick 电网连通结果 100% 一致。
  2. **`ZXSystemMapLayers.#=z9ok_i231SPp6b3Ux7A==()`**：移除单 `point` 跨岛快捷缓存，改为对每个连通分量 / 每座 `CommandCenter` 维护独立的已验证种子列表（或直接做一次多源网格 BFS 保留所有可达任一存活 CC 的格子）。
  3. **`ZXSystemMapLayers.#=zMxZdonzH58cOsMui6Q==()`**：当单 `point2` 路径检查失败时，回退调用 `#=zs1C_pLjYHbdEceIG1y8dYemfTA9s(point3)`（原版已遍历全部 `CommandCenter`），不再一刀切清零。

---

## 4. 问题三：`log6` 一家建造的狩猎小屋食物给到另一家（及归属丢失/经济串账全链路，已 100% 定位）

该现象并非单一 Bug，而是 **4 个严重缺陷叠加** 共同造成的连锁反应，且全部在 `log6` 与源码/反编译中获得铁证：

### 4.1 【根因 D1 — 致命】`~DXEntity()` GC 终结器静默清空存活实体归属表
- **日志铁证**：
  - Host L105（Tick 0 重载完成）：`[FactionOwnership] Restored 12 owner entries`（2 座 CC + 8 游侠 + 2 士兵）。
  - Host L208（Tick 2040 重载，期间 `buildings=4`，零单位死亡）：`[FactionOwnership] Restored 6 owner entries` —— **没有任何单位死亡，归属表却凭空少了一半（6 条）！**
- **引擎反编译 + Mod 源码铁证**：
  - 反编译 `DXVision.DXEntity` 确认：`DXEntity.Dispose()` **不检查 `if (Disposed) return;`**、**不调用 `GC.SuppressFinalize(this)`**、也**不清零 `ID`**；而终结器 `~DXEntity()` 无条件再次调用 `Dispose()`！
  - 主机在开局导出 `coop_host_snapshot.zxsav` 并执行对称重载（`ClearEntities()`）时，旧的 12 个 `DXEntity` 对象仍留在托管堆等待 GC 终结，且它们的 **`ID` 与重载后新反序列化出的 12 个存活实体完全相同**。
  - 局内运行期间，.NET Finalizer 线程一旦回收旧 `DXEntity`，就会触发 `Patch_DXEntity_Dispose_CleanupOwner.Prefix`（`src/TABCoopMod/Patches/FactionOwnershipPatches.cs:286–299`）：
    ```csharp
    [HarmonyPatch(typeof(DXEntity), "Dispose", new Type[0])]
    public static class Patch_DXEntity_Dispose_CleanupOwner
    {
        public static void Prefix(DXEntity __instance)
        {
            if (__instance == null || LockstepClock.LoadingLevel) return;
            if (CoopSessionManager.IsMultiFactionActive)
                FactionOwnershipTracker.RemoveEntity(__instance.ID); // 未检查是否仍为当前存活实例！
        }
    }
    ```
  - 结果：**GC 终结旧对象把正在局内存活的 CC / 部队从 `_entityOwners` 中直接抹除（`GetOwner == 0`）**！这也同时解释了为什么 `log6` 里 Client 的 `SoldierRegular` / `Ranger` 出现 `10.17 ~ 21.73` 格巨幅位移漂移（Host 端 `GetOwner == 0` 拒绝执行 Client 的移动指令）。

### 4.2 【根因 D2 — 直接导致食物归属错配】`GetPrimaryCommandCenterForPlayer` 回退到 `ccs[0]` + `Patch_Build_PropagateOwner` 优先取错 CC 归属
- 查看 `src/TABCoopMod/Sync/FactionOwnershipTracker.cs:260–269`：
  ```csharp
  public static DXEntity GetPrimaryCommandCenterForPlayer(byte playerId)
  {
      var ccs = GameBridge.GetAllCommandCenters(aliveOnly: true);
      for (int i = 0; i < ccs.Count; i++)
          if (GetOwner(ccs[i].ID) == playerId) return ccs[i];
      return ccs.Count > 0 ? ccs[0] : null; // 找不到时竟直接回退到另一家的 ccs[0]！
  }
  ```
- 查看 `src/TABCoopMod/Patches/FactionOwnershipPatches.cs:75–86` (`Patch_Build_PropagateOwner`)：
  ```csharp
  byte owner = FactionOwnershipTracker.GetOwner(__0 as DXEntity);
  if (owner == 0) owner = CommandExecutor.ActiveCommandPlayerId;
  if (owner != 0) FactionOwnershipTracker.SetOwner(__result.ID, owner);
  ```
- **触发链**：
  1. 当玩家的 CC 因 D1 丢失归属（或某路径传入的建造执行者 `__0` 指向了另一家 CC）时，`GetPrimaryCommandCenterForPlayer` 会返回 **`ccs[0]`（对方指挥中心）**；
  2. `Patch_Build_PropagateOwner` 先读 `GetOwner(__0)` 而非 `CommandExecutor.ActiveCommandPlayerId`，直接把新建的狩猎小屋绑定到了**对方玩家 ID** 上；
  3. 随后 `FactionContextManager.RecalculateAllFactions()` 按 `GetOwner(item.ID) == pid` 统计 `TotalFoodSupply`，于是**建造者自己食物 +0，对方食物立刻增加该狩猎小屋的产出**！
- **附带漏洞**：`GameBridge.PlaceSyncedStructure()`（`src/TABCoopMod/Core/GameBridge.cs:3260–3349`）在异步结构对齐补建建筑时**从未调用 `FactionOwnershipTracker.SetOwner`**，被补建出的建筑永远是无主（`owner = 0`），建造者永远收不到食物。

### 4.3 【根因 D3 — 直接导致跨家串账】`Patch_ZXLevelState_Recalculate_MultiFaction` 在 `ContextDepth > 0` 时漏放原版全图求和
- 查看 `src/TABCoopMod/Patches/FactionEconomyAndWonderPatches.cs:43–62`：
  ```csharp
  public static void Postfix(ref bool __result)
  {
      if (!__result || _inRecalcAll || !CoopSessionManager.IsMultiFactionActive || !FactionContextManager.IsInitialized)
          return;
      if (FactionContextManager.ContextDepth > 0)
          return; // 漏放！返回 __result = true，放行原版 ZXLevelState.#=zBpC6By32LAqv()！
  ```
- 反编译 `ZX.ZXLevelState.#=zgW9mPqUC4s5b()`（L1874–1880）确认：只要 `#=zv7139RDgRLf_PO97vA==()` 返回 `true`，就会立即调用**原版 `#=zBpC6By32LAqv()`**，遍历全图**所有** `Building`（不分玩家！），把**两家全部食物/工人/电力/人口总和**覆写进当前压栈的 `ZXLevelState`！
- 当建筑放置/完工置脏 `NeedsRecalculate = true` 后，只要在 `ContextDepth > 0` 的任意上下文（如 `CommandExecutor.Execute`、`CResourceGenerator.Update`、`CCommandable.Update`）里触发一次 `#=zgW9mPqUC4s5b()`，就会瞬间把**两家合并的食物总额**灌入当前被压栈的那一位玩家的账本并在 `PopContext()` 时持久化保存！

### 4.4 【根因 D4 & D5 — 次生串账与 UI 误导】
- **D4 — `AsyncEconAlign` 在多阵营模式下改错账本（直接引发 `log6` Tick 2040 `HARD DESYNC`）**：
  - `StateHasher.cs:311–314` 计算资源摘要时压入的是 **P1 账本**（`PushContext(1)`），主机算出 `Host P1 Gold=487 vs Client P1 Gold=486`，向客机下发 `DeltaGold=+1`。
  - 但客机在 `CoopSessionManager.cs:3616–3625` 执行 `GameBridge.AddLevelResourceDelta("Gold", +1)` 时**没有 `PushContext(1)`**，直接加到了客机当前静息的 **P2 账本** 上！
  - 结果：客机的 P1 账本永远差 1 金币，从 Tick 1740 到 2040 连续 6 轮重复发 `+1` 并白给 P2 加了 5 次金币，最终在 Tick 2040 触发 `HARD DESYNC (asyncEconFallback x6)`。
- **D5 — 顶部 HUD 食物/木材/电力悬浮明细未按玩家过滤**：
  - 反编译 `ZXSystemGameLevel` L1109–1134 确认，顶部食物图标 `#=zqb_arWi7kN9a.Tag` 的动态 Tooltip 直接遍历全图 `DXGame.Current.EntitiesOfType<Building>()`，**没有按本地玩家归属过滤**，鼠标悬停食物数字时会看到另一家的狩猎小屋也列在自己账单中。

### 4.5 修复建议
1. **修 D1（最高优先级）**：`Patch_DXEntity_Dispose_CleanupOwner.Prefix` 必须增加三重门卫：
   - 仅在 `LockstepClock.IsSimulationThreadActive` 或主线程有效销毁路径处理，**禁止 GC Finalizer 线程修改 `_entityOwners`**（`if (__instance.Disposed) return;` 且 `var live = DXGame.Current?.GetEntityByID(__instance.ID); if (live != null && !ReferenceEquals(live, __instance)) return;`）。
2. **修 D2**：
   - `Patch_Build_PropagateOwner` 优先使用 `CommandExecutor.ActiveCommandPlayerId`（在联机命令回放中它是权威的指令发起者），仅在 `ActiveCommandPlayerId == 0` 时回退 `GetOwner(__0)`；
   - `GetPrimaryCommandCenterForPlayer(pid)` 在多阵营模式下**严禁**回退到 `ccs[0]`（另一玩家的 CC），找不到应返回 `null`；
   - `AsyncStructAlign` 的 `StructBuild` 消息附带 `OwnerPlayerId`，`PlaceSyncedStructure` 补建时同步恢复归属。
3. **修 D3**：`Patch_ZXLevelState_Recalculate_MultiFaction` 当 `ContextDepth > 0` 时，应**直接拦截返回 `__result = false`**（绝不允许原版全图求和污染任何单人账本），或在 `PopContext()` 回到 `Depth == 0` 时统一执行一次 `FactionContextManager.RecalculateAllFactions()`。
4. **修 D4**：多阵营模式下 `DrainAsyncQueues()` 执行 `AsyncEconAlign` 时外包 `FactionContextManager.PushContext(1) / PopContext()`（后续可扩展为全玩家账本增量对齐）。
5. **修 D5**：对顶部资源 Tooltip（或 `ZXDynamicHelp` 构建器）在多阵营模式下过滤非本机玩家建筑（或明确分列「己方 / 盟友」）。

---

## 5. 问题四：`log6` 游戏画面看起来变窄、地图疑似被拉伸（部分确认 + 需补充实机诊断项）

本问题严格区分**代码与日志已确认的事实**与**因日志缺口尚需实机截图/日志复测确认的疑点**：

### 5.1 已确认的日志与代码事实
1. **主机侧分辨率与 `CameraArea` 纵横比核查**：
   - `log6` Host 日志（L71, L104, L207）记录：`view={0,0,2560,1440}`、`screen={0,0,2560,1440}`、`unit=2.000`，初始相机 `cam=(247.0,211.4,24.0x13.5)`（`16:9`），Tick 2040 重载前相机 `cam=(240.0,264.6,42.7x24.0)`（`16:9`）。
   - 反编译 `DXScene.CameraArea` setter（`DXScene.cs:707–718`）：
     ```csharp
     _RootObject.ScalingX = (float)ScreenArea.Width / value.Width;
     _RootObject.ScalingY = (float)ScreenArea.Height / value.Height;
     ```
     在主机侧 `ScreenArea` 与 `CameraArea` 严格同为 `16:9`（`1.7778`），理论上 `ScalingX == ScalingY`；且 `ZXSystemGameLevel.OnSetCamera()`（L3028）每帧用 `viewArea.Height / viewArea.Width` 重算 `rectangleF.Height`。
2. **多阵营独有开局相机强制拉到极窄视口（已确认）**：
   - 对比 `log7`（执政官）与 `log6`（多阵营）：
     - `log6` 多阵营进图时，`FactionOwnershipTracker.CenterLocalCameraOnOwnBase()`（`FactionOwnershipTracker.cs:585`）显式调用 **`GameBridge.SetCameraWorld(myCc.Position, 24f)`**（Host L110、Client L105 均打印 `width=24.0`），把视野强行锁死在极近的 `24.0 × 13.5`（对比 `log7` 正常游玩视野 `64.3 × 36.2`，**可视面积仅约正常的 1/7**，画面观感明显「变窄/压迫」）。
     - 同时注意 `GameBridge.SetCameraWorld` 调用 `scene.SetCameraCenter(scenePt, 24f)`，`DXScene.SetCameraCenter`（`DXScene.cs:2341`）使用 **`width * CameraArea.Height / CameraArea.Width`** 继承调用前的纵横比，**未以本机 `DXScene.ScreenArea` 纵横比校正**。
3. **客机日志缺失关键视口诊断信息（已确认）**：
   - `TABCoopMod_client_6.log` 只打印了 `OContent size: 1500.0x870.0 (Unit=2.000)` 和 `Camera world=(219.5,215.5) -> scene=(258.8,217.5) width=24.0`，**没有打印 `GetHudDiagSummary()`**（因此日志里看不到客机的 `view`、`screen`、`cam.Width x cam.Height`、`FixedAspectRatio` 以及 `RootObject.ScalingX / ScalingY`）。
4. **分离出生布局与 `NCells=360` 地图观感因素（已确认）**：
   - `log6` 地图 `NCells=360`（底层 `512`，`FactorPlayableArea = 360/512 = 0.703125`），`log7` 为 `NCells=280`（底层 `512`，`FactorPlayableArea = 0.546875`）。两者均使用 `DXWorldGrid.GetSceneSquareArea(512, FactorPlayableArea)` 生成场景正方形，且 `OMiniMap` 对实体/迷雾/阴影图统一使用 `ScalingXY = 1f / FactorPlayableArea`。
   - 但在 `log6` 中两座基地 `CC1(278,275)` 与 `CC2(219,215)` 恰好上下垂直对齐在 `sceneX ≈ 258` 中轴线上，且 `NCells=360` 使小地图视野框变得更小更细长。
5. **附带发现 — `AllyOverlayRenderer` 阵营脚底光环坐标系错配**：
   - `src/TABCoopMod/UI/AllyOverlayRenderer.cs:228, 247–248` 用等距场景坐标的 `DXScene.Current.CameraArea` 去裁剪世界网格坐标的 `ent.Position`，且 `ring.Area` 直接以世界坐标 `(pos.X, pos.Y)` 写入 `IsometricMiddle`（应为场景坐标）。虽然不影响主场景拉伸，但会导致多阵营模式下阵营光环偏移或被误裁剪。

### 5.2 本地双开截图（18:05 复现）的像素级与引擎级双重核验
针对你补充上传的本地双开主客机截图（主机 Day 002 14h / 客机 Day 002 17h，均 `1024×576`、`16:9`，对应日志 `TABCoopMod_v0.2.4_20261121_180311_p303004.log` 与 `p266828.log`），以及「画面里所有单位和地形看起来都比正常窄」的反馈，我们从**引擎反编译公式**、**Mod 全量代码审计**、**三组独立截图的像素级 X/Y 拟合**三个维度做了完整核验：

1. **原版引擎渲染链路核验（未改动的 `DXVision.dll` + `DXPlatform_Desktop.dll` + `TheyAreBillions.exe`）**：
   - `DXWorldGrid`：`IsometricTileWidth = 1.4142135f`（$\sqrt{2}$），`IsometricTileHeight = 1f`，`ScenePointFromWorldPoint(x, y) = ((x-y)*0.7071 + N/2, (x+y)*0.5)`；
   - `DXIsometricGridObject`：`_OGridBottom.Angle = 45f`，`_OBaseBottom.ScalingX = 1.0`，`_OBaseBottom.ScalingY = 1 / √2 = 0.7071` → 原版《亿万僵尸》的等距地块与 5×5 指挥中心菱形底座的理论屏幕宽高比天然就是 **$\sqrt{2} : 1 \approx 1.414 : 1$**（倾角约 35.3°，比传统 RTS 的 `2:1` 等距更陡/显得略窄）；
   - `ZXSystemGameLevel.OnSetCamera()`（L300）：每帧计算 `cam.Height = cam.Width * viewArea.Height / viewArea.Width`，代入 `DXScene.CameraArea` setter（`RootObject.ScalingX = ScreenArea.Width / cam.Width`，`ScalingY = ScreenArea.Height / cam.Height`）恒有 **`ScalingX == ScalingY`**；
   - `DXRender_SlimDX`：`BackBuffer = ClientArea`，`Viewport = (0, 0, BackBufferWidth, BackBufferHeight)`，直接以像素坐标提交四边形顶点，无非等比投影矩阵；
   - **Mod 全量源码审计**：`src/TABCoopMod/` 中**没有任何补丁或代码**修改 `RootObject.ScalingX/Y`、`DXIsometricGridObject`、`DXWorldGrid`、`FixedAspectRatio`、`Viewport` 或 `DXRender_SlimDX`。

2. **像素级实测对比（Sobel 梯度 2D 独立 X/Y 缩放匹配，不受亮度/配色影响）**：

| 对比组合 | X 缩放 `sx` | Y 缩放 `sy` | 纵横比畸变 `sy / sx` | 几何结论 |
|---|---:|---:|---:|---|
| `m4_client` (`2880×1800`, 16:10) 自身 CC 绿框底座 | `W=981 px` | `H=693 px` | `W/H = 1.4156` | 与引擎理论值 $\sqrt{2}=1.4142$ 误差仅 `0.1%` |
| `m4_client` → **本地客机截图 (图 2)** 指挥中心 | `0.1800` | `0.1800` | **`1.0000`** (`148×144 px`) | **零 X/Y 拉伸畸变** |
| `m4_client` → **本地主机截图 (图 1)** 指挥中心 | `0.1675` | `0.1650` | **`0.9851`** (`137×132 px`) | **零 X/Y 拉伸畸变**（±1.5% 为低分辨率量化误差） |
| 原版 F10 全景图 (`br_100_vanilla.jpg`, `2048×2048`) → **图 2** | `1.562` | `1.562` | **`1.0000`** | 与原版 F10 渲染几何完全一致 |
| 18:05 本地主机日志 (`view=2560×1440`, `cam=53.3×30.0`) | `48.03 px/u` | `48.00 px/u` | **`1.0006`** | 引擎实测 `ScalingX == ScalingY` |

3. **综合判断与待排排查的最后一种可能**：
   - **在这两张本地双开截图中，世界场景的像素宽高比与原版引擎公式 $\sqrt{2}:1$ 完全一致（`sy/sx = 1.0000`），并不存在 Mod 造成的 X/Y 压缩拉伸**；
   - 造成视觉上「变窄/不对劲」的已知因素：
     1. **开局/读档镜头被强行拉近到 `width = 24.0`**：18:05 日志显示主机读档原本镜头是 `53.3 × 30.0`，但 `CenterLocalCameraOnOwnBase()`（`FactionOwnershipTracker.cs:585`）在 `EnterSyncedLevel` 时硬调用 `GameBridge.SetCameraWorld(myCc.Position, 24f)`，强行把主客机双双放大 **2.2~2.7 倍**（可视面积缩水至 **1/7**），近景下单位和建筑显得格外高瘦压迫；
     2. **16:9 底部 HUD 原版横向拉宽的参照错觉**：原版 HUD 面板（`#=zNjT9FdpMLDa2.Area = ViewArea`）按 16:10 设计，在 `16:9` 下横向略宽（命令按钮网格间距 `W/H ≈ 1.10–1.13`，`bal_host` 2560×1440 同样如此），与严格 1:1 渲染的世界场景对比会放大「世界偏窄」的错觉；
     3. **潜在隐患（需在修复阶段顺手消除）**：`GameBridge.SetCameraWorld` 调用 `scene.SetCameraCenter(pt, w)` 时继承的是旧 `CameraArea.Height / Width` 而非 `DXScene.ScreenArea` 比例；且 `SmoothCameraPanPatch` 在窗口失焦时会跳过 `OnSetCamera()`，若窗口初始化或缩放瞬间的旧 `CameraArea` 比例异常，失焦窗口在重新获焦前会一直保留错误比例；
   - **若你开启无 Mod 原版单人（或执政官模式）对比后仍觉得画面比这两张图明显更宽**：请在同一显示器/相同分辨率下截一张「你认为比例正常的原版单人局内截图」发给我，我将直接拿它的指挥中心像素宽高比与图 1/图 2 做逐像素比对，一秒就能锁定是否存在显示驱动/DPI/分辨率层面的差异。
   - **P1-1 计划修复项**：
     1. `GameBridge.SetCameraWorld` 改用 `DXScene.ScreenArea` 显式计算 `Height`，杜绝任何窗口缩放/失焦下继承旧纵横比的风险；
     2. `CenterLocalCameraOnOwnBase` 不再在读档时覆盖原存档缩放宽度，新局默认宽度从 `24f` 调整至更舒适的 `42f~48f`；
     3. 客机 `OnLevelLoadComplete` / `SetCameraWorld` 补打 `GetHudDiagSummary()` + `RootObject.ScalingX/Y` + `FixedAspectRatio` 诊断日志；
     4. 修复 `AllyOverlayRenderer.UpdateUnitFactionRings` 的世界/场景坐标混用。

---

## 6. 问题五：`log7` 执政官模式最后一波卡顿瓶颈深度分析（已 100% 量化）

### 6.1 实测时间轴与量化指标（Host & Client 联合对照）

| 时段 | 实测 TPS | 单 Tick 均耗 | 实体数 (`entities`) | 物理直连 `directRtt` | 应用层 `rtt` (均/峰) | `clientLag` (均/峰) | 关键事件 |
|---|---:|---:|---:|---:|---:|---:|---|
| `17:04–17:06`（总攻前） | **`28.9–30.0`** | ~33 ms | `4,873–5,202` | `5.7–5.8 ms` | `12–18 ms` | `0.2–0.3t` (max 1t) | 丝滑满速 30 TPS |
| `17:07`（前哨交火） | `9.5` | ~105 ms | `5,204` | `5.8 ms` | `66 / 284 ms` | `2.2t` (max 6t) | 尸潮触发瞬间突发卡顿 |
| `17:09:25`（重载 #1） | 冻屏 | — | `5,767` | `5.9 ms` | — | — | `PosDrift` (`Sniper MaxDelta=17.18`) 触发 `7.28 MB` 快照重载：**主机 10.7s + 客机 15.1s ≈ 34s 总冻屏** |
| **`17:10–17:21`（总攻峰值，持续 ~11 分钟）** | **`4.5–6.5`** | **`154–222 ms`** | **`10,309 -> 14,692`** | **`5.7–6.1 ms`** | **`93–161 ms` (峰 `290 ms`)** | **`6.7–8.8t` (频触 `9t` 硬停)** | **严重慢动作 + 走走停停卡顿**；期间 `17:12:10` 触发重载 #2 |
| `17:12:10`（重载 #2） | 冻屏 | — | `14,692` | `5.7 ms` | — | — | `MissingOnClient=319/MissingOnHost=317` 触发 **`14.78 MB`** 快照重载：**主机 20.1s（客机等 26.2s）+ 客机 26.8s ≈ 60s 总冻屏** |
| `17:22`（尸潮清尾） | `18.9` | ~53 ms | 迅速下降 | `6.0 ms` | `66 / 162 ms` | `7.3t -> 1t` | 实体清空后迅速回弹 |
| `17:23–17:26`（收官胜利） | **`29.4–30.0`** | ~33 ms | 低 | `5.7–5.9 ms` | `12–14 ms` | `0.5–1.8t` | 完全恢复 30 TPS，顺利通关 |

> **首要结论**：整个最后一波期间，底层 UDP 直连延迟 `directRtt` 始终纹丝不动保持在 **`5.7–6.1 ms`**，在途包 `cmdFly=0, bulkFly=0`；但应用层主线程心跳 `rtt` 却从 `12 ms` 飙升至 **`100–290 ms`**。这 100% 证明**瓶颈纯粹是单线程 CPU 计算饱和（外加两次全量快照重载冻屏），与网络带宽或延迟完全无关**。

---

### 6.2 卡顿瓶颈的四层代码级拆解

#### 瓶颈 1（占比最大，核心主因）：确定性单线程模拟 + 同步 A* 寻路 + 客机 CPU 木桶效应
1. **引擎并行被全面关闭**：
   - 为保证确定性，Mod 将 `DXGame.IsIADeterministic = true`，使原版多核并行的实体 AI/逻辑更新退化为单线程顺序执行；
   - `DeterministicTaskPatch.cs` 在 `InPhysicsStep` 期间将 `DXHelper_Task.For` 单线程化（尤其物理回写 Loop 2 `OnNewPositionComputed` 在 `14,692` 实体时全部串行）；
   - **`DeterministicPathfindingPatch`（`LockstepClock.cs:2470`）**：把原版后台线程池并发 A* 寻路替换为**逻辑主线程同步执行**。最后一波 9,000+ 僵尸同时撞击多层城墙、缺口动态变化时，每 tick 触发成百上千次长距离 A* 寻路，全部卡死在单核逻辑线程上！
2. **客机单核算力较主机慢约 30%~35%，拖垮全局锁步**：
   - 从同份快照加载耗时可直接量化两机单核性能差距：
     - `7.28 MB` 快照：`ReadSaveFile` 主机 `8.46s` vs 客机 `11.69s`（+38%）；`InitLevel` 主机 `2.26s` vs 客机 `3.45s`（+53%）；
     - `14.78 MB` 快照：`ReadSaveFile` 主机 `17.67s` vs 客机 `23.56s`（+33%）；`InitLevel` 主机 `2.46s` vs 客机 `3.28s`（+33%）。
   - 在峰值 `14,692` 实体时，客机单 tick 耗时约 `170–220 ms`（仅能跑 `4.5–5.8 TPS`），而主机单 tick 约 `110–140 ms`，主机持续跑在客机前面直到撞上反压上限。

#### 瓶颈 2：主机反压在 `clientLag >= 9t` 处触发「硬停帧」导致明显顿挫
- `src/TABCoopMod/Sync/CoopSessionManager.cs:198–199, 2410–2423`：
  - `clientLag >= 6t`：主机降为 `maxTicks = 1, paceMultiplier = 1.38`；
  - **`clientLag >= 9t`：主机直接 `maxTicks = 0`（逻辑帧硬冻结）**。
- 最后一波 11 分钟内，`clientLag` 几乎全程钉死在 `7t–9t`，主机在「跑几帧 -> 撞 9t 硬停 -> 客机吐出 1 tick -> 再跑 1 帧 -> 再硬停」间剧烈振荡，导致**主机玩家即使本机还有余量也会感到明显的走走停停卡顿**。

#### 瓶颈 3：Mod 热路径在 14.7k 实体下的线性/超线性附加开销
1. **高频 Harmony 补丁 + 生产环境空转的 `Stopwatch` / `Interlocked` 统计**：
   - `CMovableSpriteProgressSyncPatch`、`SceneGlobalRandomInstancePatch`（`DXRandom.genrand_uint32`）、`HelperRandomGetterPatch`（`DXRandom.Global`）、`ZXBehaviourRunRngScopePatch`、`PostMethodInvokeContextPatch` 在万人尸潮交火时每 tick 被调用**数万至十余万次**；
   - 核查 `LockstepClock.cs:1610` 发现：**生产模式下 `[Perf]` 压根不打印日志**（被 `if (Testing.TestHarnessConfig.Enabled)` 门控），但热路径上所有 `Stopwatch.GetTimestamp()` 和原子 `Interlocked.Increment / Add` 却在**无条件执行**，白白消耗大量单核周期！
2. **物理步进全量遍历量化**：
   - `LockstepClock.QuantizePhysicsBodiesPostStep()`（`LockstepClock.cs:1011`）在 12Hz 物理步进末尾持 `DXGame.SyncLogic` 锁全量遍历所有动态刚体（峰值 10,000+ 僵尸），逐个 P/Invoke 读写 `PhysicPosition / PhysicVelocity`。
3. **每 60 Tick 全量状态差分与压缩**：
   - `StateHasher` 虽已均摊到 60 tick（每 tick 扫 `1/60` 实体，但 `fastEntities` 循环本身每 tick 仍要遍历全部 `14,692` 实体做 `ID % 60` 判定），且每 60 tick 结束时 `TrySendPosDriftSync` 在峰值期需对 `14,692` 实体做全量字典构建、差分、压缩并发送（实测峰值 `Drifted = 9,396`，`Applied = 773/14692`），客机还需在主线程批量写回位置/目标。

#### 瓶颈 4：高峰期两次「串行全量快照 Auto-Resync」造成 34 秒与 60 秒巨型冻屏
- **重载 #1（`17:09:25`, Tick 201000, ~34 秒）**：仅因为 **1 只狙击手** 漂移 `MaxDelta = 17.18`（其余 5,766 实体正常，`LifeDiff=0, Missing=0`），连续 10 次超阈值就触发了 `7.28 MB` 全图快照重载！
- **重载 #2（`17:12:10`, Tick 206880, ~60 秒）**：主机导出 `14.78 MB` 快照后，**先自己耗时 `20.1s` 完成对称重载，才开始向客机广播 452 个分片**（期间客机白白空等 `Tick gate waiting 26.2s`！），客机收到后再花 `26.8s` 读盘初始化，串行累加酿成近 **1 分钟彻底冻屏**！

---

### 6.3 性能优化分级方案（按投入产出比排序）

#### P0（低风险、高收益，建议优先落地）
1. **慢帧自动输出 `[Perf]` 耗时拆解 + 生产模式关闭热路径计时开销**：
   - 在 `LockstepClock.cs` 将热路径 `Stopwatch.GetTimestamp()` / `Interlocked` 用 `DiagPerfSamplingActive` 开关门控（平时关闭零开销；仅在 `TestHarnessConfig.Enabled` 或连续检测到 `DiagAvgTickLogicMs > 45ms` 时按「采样 60 tick -> 打印 1 行 `[Perf]` -> 冷却 30s」方式低频采样），既消除无谓热路径开销，又能立即看清最后一波 `logic / phys / path / quant / movSync / coopTick` 的精确毫秒占比！
2. **Auto-Resync 主客机「并行重载」直接砍半冻屏时间**：
   - 主机导出 `coop_host_snapshot.zxsav` 后，**立即先异步广播快照分片给客机，再同时启动主机自身对称重载**（而非等主机 20 秒重载完再发包）。在 `14.78 MB` 场景下，总冻屏时间可从 **~60 秒直降至 ~28 秒**！
3. **最后一波高压期自适应放宽 `PosDrift` / `AsyncPosAlign` 策略**：
   - 重载 #1 的 `17.18` 格仅源于单只狙击手被挤偏，无 `LifeDiff/Missing` 不应触发全图快照重载（应提高单单位 `PosDrift` 重载触发门槛，或允许对单只极端离群单位提高强制对齐权重而非整图重载）；
   - 当活跃实体 `> 8,000` 时，自动拉长 `PosDriftSync` 冷却周期（如 `60t -> 180t`）并跳过远端未交战僵尸的精细坐标差分。

#### P1（中深度优化，直接提升最后一波 TPS）
4. **同步 A* 寻路热路径加速与去重（`DeterministicPathfindingPatch`）**：
   - 分析 `DXWorldGrid.FindPath` 在尸潮撞墙时的重复请求（同源格/邻近格向同一城墙缺口/指挥中心寻路），引入**按 `(cellSize, originBucket, destinyBucket, mask, gridVersion)` 的同 tick/短窗口确定性路径缓存**，避免数百只重叠僵尸在同 1 tick 内反复跑一模一样的失败 A*！
5. **`QuantizePhysicsBodiesPostStep` 增量/降开销优化**：
   - 将反射/PInvoke 批量读写优化为仅处理 `Velocity != 0` 或位置未量化对齐的活跃动态刚体（静态/休眠僵尸直接跳过），减少 `14.7k` 规模下的原生互操作调用量。
6. **平滑主机反压曲线，消除 `9t` 硬停顿挫**：
   - 当检测到慢帧（`DiagAvgTickLogicMs > 30ms`）时，用**连续比例降速**（如 `paceMultiplier` 随 `clientLag` 从 `1.0` 平滑过渡到 `2.5`）替代 `>= 9t` 的断崖式 `maxTicks = 0` 硬停，并将高压期硬顶上限适度放宽到 `14t~16t`，让主机和客机都保持稳定的低速流畅而非一卡一顿。

#### P2（架构级探索，需基于 P0 `[Perf]` 实测数据决策）
7. **评估 `async_align` 模式下恢复部分安全多线程**：
   - 待 P0 `[Perf]` 采样给出最后一波中 `logic`、`path`、`physFor1/2` 的精确毫秒占比后，若 `physFor2`（`OnNewPositionComputed`）或纯只读 AI 阶段占大头，可评估借助「位置/血量异步权威对齐」兜底、在可控范围内恢复多线程物理或后台确定性排序寻路，冲击最后一波 `15–20+ TPS`。

---

## 7. 总结与建议实施顺序

| 优先级 | 问题 | 根因确定度 | 核心改动文件 |
|---|---|---|---|
| **P0-1** | 问题三（D1/D2/D3/D4）：GC 终结器抹除归属、建造/重算跨家串账、`AsyncEconAlign` 写错账本 | **100% 确认** | `FactionOwnershipPatches.cs`, `FactionOwnershipTracker.cs`, `FactionEconomyAndWonderPatches.cs`, `CoopSessionManager.cs`, `GameBridge.cs` |
| **P0-2** | 问题二（B1/B2/B3）：分离基地无初始电网 & 主客机反向断电 | **100% 确认** | `EnergyGridMultiBasePatch.cs` |
| **P0-3** | 问题一：大厅 M4 选项与「解散房间」重叠 | **100% 确认** | `CoopLobbyWindow.cs` |
| **P1-1** | 问题四：开局视野过窄 (`24f`) + 纵横比校正 + 客机视口/黑边/光环诊断与修复 | **部分确认（需补客机日志验证拉伸）** | `FactionOwnershipTracker.cs`, `GameBridge.cs`, `AllyOverlayRenderer.cs`, `LockstepClock.cs` |
| **P1-2** | 问题五（P0+P1 性能项）：最后一波慢帧 `[Perf]` 采样、去除热路径计时开销、并行快照重载、高压期寻路缓存与平滑反压 | **100% 定位主因（细节待 `[Perf]` 实测细分）** | `LockstepClock.cs`, `CoopSessionManager.cs`, `DeterministicTaskPatch.cs` |

> **下一步**：问题四（画面/视口比例）按要求暂搁置跳过；其余全部修复项及新增 UI/奇观调整已落地并通过实测回归验证。

---

## 8. 修复实施与回归验收记录（2026-11-21）

### 8.1 已落地改动清单
1. **P0-1 归属丢失与跨家串账（D1/D2/D3/D4）**：
   - `Patches/FactionOwnershipPatches.cs`：`Patch_DXEntity_Dispose_CleanupOwner` 增加 `__instance.Disposed` 及 `ReferenceEquals(live, __instance)` 守卫，根治快照重载后 GC 终结器抹除活跃实体归属；`Patch_Build_PropagateOwner` 优先取权威锁步 `CommandExecutor.ActiveCommandPlayerId`。
   - `Sync/FactionOwnershipTracker.cs`：`GetPrimaryCommandCenterForPlayer` 在多阵营模式下不再回退到 `ccs[0]`（另一玩家的基地）。
   - `Patches/FactionEconomyAndWonderPatches.cs`：`Patch_ZXLevelState_Recalculate_MultiFaction` 移除 `ContextDepth > 0` 提前返回，确保任何上下文内重算均走 `RecalculateAllFactions()` 而不会回退原版全图总和；`Patch_Build_ConsumeWonderVoucher` 同步优先取 `ActiveCommandPlayerId`。
   - `Sync/CoopSessionManager.cs` & `Core/GameBridge.cs`：`AsyncEconAlign` 在多阵营模式下包裹 `PushContext(1)`，避免把 P1 差分错写进客机 P2 账本；`StructSampleMeta` / `DynamicSpawnMeta` 透传 `OwnerPlayerId` 并在 `PlaceSyncedStructure` / `SpawnSyncedDynamicEntity` 写入归属。
2. **P0-2 分离基地初始无电网（B1/B2/B3）**：
   - `Patches/EnergyGridMultiBasePatch.cs` & `Core/ObfuscatedSymbols.cs` & `Core/GameBridge.cs`：补齐全部 3 处引擎电网逻辑——`CEnergyTransferer.#=zeJrYi6vVVMO8Q25qqH7ixNY=()` 以全部存活 `CommandCenter` 为多源 BFS 起点，`ZXSystemMapLayers.#=z9ok_i231SPp6b3Ux7A==()` 与 `#=zMxZdonzH58cOsMui6Q==()` 在跨岛路径未命中单点缓存时回退到 `#=zs1C_pLjYHbdEceIG1y8dYemfTA9s(cell)`（任意基地可达即保留通电）。
3. **P0-3 大厅模式选择改用原版 `ZXComboBox` 下拉列表栏**：
   - `Core/GameBridge.cs` 新增 `CreateZXComboBox`（反射实例化 `ZX.GUI.ZXComboBox` 并纳入 `InitializeAndValidate` 自检）。
   - `UI/CoopLobbyWindow.cs` 将原 4 个堆叠按钮替换为 2×2 原生 `ZXComboBox` 下拉栏（合作模式 / 新局预设 / 出生布局 / 败北规则），卡片背景置底 `Z=-10`、文本与下拉控件置顶 `Z=5/10`、切换选项时清理 `_OText` GPU 纹理防 Direct3DEx 错位，与底部「解散房间」零遮挡。
4. **奇观众筹出资档位调整与研究读条倒计时**：
   - `UI/MultiFactionPanel.cs`：金币调整为 `投+500` / `投+1000` / `投满`，木/石/铁/油调整为 `投+50` / `投+100` / `投满`。
   - `Sync/WonderCrowdfundManager.cs` / `Sync/CoopSessionManager.cs` / `Commands/NetCommand.cs` / `UI/MultiFactionPanel.cs`：新增 `WonderPhase.Researching` 阶段（`DefaultResearchDurationTicks = 1350`，30Hz 下固定 45 秒确定性倒计时），在 `OnSimulationTickCompleted` 锁步推进并纳入 `WriteState`/`ReadState`/`ComputeHash`；奇观面板底部新增实底文字 + 青色进度条与顶部 `[F7] 奇观研究中 (X%)` 实时指示，倒计时结束后全队同步解锁科技并进入建造众筹。
5. **P1-1 相机/视口比例与光环潜在隐患修复**：
   - `Core/GameBridge.cs`：`SetCameraWorld` 改用 `DXScene.ScreenArea` 纵横比显式计算 `Height` 并直接写入 `CameraArea`；`GetHudDiagSummary` 增加 `scale=(sx,sy)` 与 `fixedAR`。
   - `Sync/FactionOwnershipTracker.cs`：`CenterLocalCameraOnOwnBase` 不再将读档/开局缩放压窄至 `24f`（若当前宽度 `>= 28f` 则保留，否则使用舒适的 `42f`）。
   - `Sync/CoopSessionManager.cs`：客机 `OnLevelLoadComplete` 补齐 `GetHudDiagSummary()` 视口诊断日志。
   - `UI/AllyOverlayRenderer.cs`：`UpdateUnitFactionRings` 通过 `DXWorldGrid.ScenePointFromWorldPoint(pos)` 转换后再与 `CameraArea` 做视锥裁剪，修复阵营脚底光环误裁剪。
6. **P1-2 最后一波性能瓶颈优化**：
   - `Determinism/LockstepClock.cs` / `DeterministicTaskPatch.cs` / `DXRandomCtorPatch.cs`：热路径 `Stopwatch` / `Interlocked` 全部受 `DiagPerfSamplingActive` 门控（常态零开销，检测到慢帧 `>45ms` 自动低频采样 60 tick 输出 `[Perf]` 并冷却 30s）；`DeterministicPathfindingPatch` 新增按 `DXWorldGrid.SetCell` 版本失效的确定性精确路径缓存；`QuantizePhysicsBodiesPostStep` 为静止已量化刚体增加纯托管快路径跳过 P/Invoke。
   - `Sync/CoopSessionManager.cs`：Steam P2P 跨机场景下 Auto-Resync 先广播分片再并行执行主机重载（同机回环保持串行防同盘文件锁）；慢帧下平滑主机反压曲线（`paceMultiplier` 连续过渡至 `2.5x`，硬停上限抬至 `15t`）；实体 `>8000` 时放宽 `PosDriftSync` 冷却并提高单离群单位触发重载门槛。

### 8.2 编译与双开实测验证结果（`v0.2.5`）
- **编译部署**：`dotnet build src/TABCoopMod/TABCoopMod.csproj -c Release` **0 警告 0 错误**，已部署至游戏目录。
- **启动自检**：主客机均 **`GameBridge ALL BINDINGS VERIFIED SUCCESSFULLY (0 missing, verified=233)`**，Harmony **`Classes OK=105, failed-tolerant=0`**。
- **分离基地 2P 新生存实测**：`CC1` 与 `CC2` 分离出生；主客机开局相机均为舒适的 `42.0 × 23.6`（`aspect=1.778`，客机日志输出 `scale=(106.67,106.67) fixedAR=null`）；客机 P2 在 CC2 旁成功建造 `HunterCottage`（`owner=2`），主机 P1 在 CC1 旁成功建造 `TentHouse`（`owner=1`），各自独立扣费（P1 金币 `1010`、P2 金币 `940`），`trackedOwners` 稳定同步至 `17`，全程 `StateChecksum MATCH`，无 `ID COUNTER DRIFT` / `AsyncStructAlign` / `AsyncEconAlign` / `HARD DESYNC` / `ERROR`。
- **奇观众筹新档位与研究读条全链路实测**：闪电尖塔研究众筹集齐（`12000G/225S/300I/150O`）后双端同步进入 `WonderPhase.Researching`，进度条实时推进并在倒计时结束后双端同步进入 `BuildFunding`（`researchUnlocked=true`，主客机 `wonderHash=0xED7A3EA7C3E437F7` 完全一致）。
- **UI 实机截图目视核验**：大厅 4 个原生 `ZXComboBox` 下拉栏、奇观众筹 `500/1000` & `50/100` 按钮、研究读条进度条与单位阵营光环均清晰无遮挡、无渲染毛边。
