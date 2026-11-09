# 03 — 逆向工程符号映射字典 (Reverse Engineering Symbol Map)

> **目标主程序**: `TheyAreBillions.exe` (Version `1.0.14.29`, 64-bit, MD5/Size: `4,804,608` bytes)
> **目标引擎库**: `DXVision.dll` (Version `1.0.0.0`, 从主程序 ManifestResource `b9a9b1ca9f93492687e026fb08597a75` 解密提取，`1,233,408` bytes)

本文档记录了从本机游戏二进制文件中实测逆向提取的精确类型名、混淆符号（`#=z...`）、方法签名与功能说明。AI Agent 在编写 `GameBridge` 和 Harmony Patch 时**必须直接引用此表**，切勿盲目猜测名称。

---

## 1. 内嵌加密程序集清单（Embedded Manifest Resources）

`TheyAreBillions.exe` 的 `<Module>..cctor` 会调用 `#=qfkZ$KkimwzG_5GjOFAkJH7Cbg6LrXJCCJODXq7ULSO4=.#=zX6exa18=()` 注册 `AppDomain.CurrentDomain.AssemblyResolve`。其内部资源映射关系如下：

| ManifestResource 名称 | 解密后程序集全名 | 导出文件名 | 大小 (Bytes) | 混淆状态 |
| :--- | :--- | :--- | ---: | :--- |
| `b9a9b1ca9f93492687e026fb08597a75` | `DXVision, Version=1.0.0.0` | `DXVision.dll` | 1,233,408 | **100% 无混淆（全明文）** |
| `2e1a46ead7a4460ababa0e338efe2ccb` | `DXPlatform_Desktop, Version=1.0.0.0` | `DXPlatform_Desktop.dll` | 276,992 | 无混淆（封装 SlimDX / OpenTK / BASS） |
| `dd64f0b32b994eb3ba7dd334c48bd40f` | `Steamworks.NET, Version=10.0.0.0` | `Steamworks.NET.dll` | 265,216 | **100% 无混淆（标准 Steamworks.NET）** |
| `3822cc7909fd4dfdac2ffabd4f86c34a` | `SmartPropertyGrid, Version=3.4.0.83` | `SmartPropertyGrid.dll` | 200,704 | 无混淆（编辑器控件） |
| `f3c8ae8f19264c499d8f4d10a18c75a2` | *(Eazfuscator VM Bytecode Stream)* | N/A | 101,124 | 存放被虚拟化方法的指令流 |

---

## 2. 顶层核心系统类映射（Core Systems & Singletons）

在 `TheyAreBillions.exe` 中，继承自 `DXVision.DXGame` 和 `DXVision.DXSystem` 的顶层系统类位于空命名空间（Global Namespace），类名为 `#=z...`，但所有类都提供了一个**同名静态单例获取方法 `#=zuartwoo=()`**！

| 逻辑类名（原名推断） | 混淆后完整类型名 | 基类 | 获取单例静态方法 | 核心职责与说明 |
| :--- | :--- | :--- | :--- | :--- |
| **`ZXGame`** | `#=z4RevDP3eECXqXS6JRA==` | `DXVision.DXGame` | `#=z4RevDP3eECXqXS6JRA==.#=zuartwoo=()` | 游戏主类：管理 `CurrentGameSystem`、配置、数据表、存档读写（`.zxsav`）、市长弹窗、过场切换。 |
| **`ZXSystem`** | `#=zsW2J3r72Cu83` | `DXVision.DXSystem` | N/A (抽象基类) | 所有 ZX 子系统的基类，提供 `Game` 属性和本地化方法 `#=zBYC_Wew=(string)`。 |
| **`ZXSystemGameLevel`** | `#=zxRcpu6e7NYzT7tGWqPjpOkc=` | `#=zsW2J3r72Cu83` | `#=zxRcpu6e7NYzT7tGWqPjpOkc=.#=zuartwoo=()` | **局内核心控制器**：处理鼠标左右键、框选、建筑放置、单位移动散开、HUD 更新、小地图与命令面板。 |
| **`ZXSystemMainMenu`** | `#=zLs$VX1eoAapVflmW_w==` | `#=zsW2J3r72Cu83` | `#=zLs$VX1eoAapVflmW_w==.#=zuartwoo=()` | 主菜单系统：管理主界面按钮列表（继续、新游戏、读取存档、退出）。 |
| **`ZXSystemCampaign`** | `#=zUsJtyOzSfYDnqdeHjCSzszBbekLj` | `#=zsW2J3r72Cu83` | `#=zUsJtyOzSfYDnqdeHjCSzszBbekLj.#=zuartwoo=()` | 战役大地图系统。 |
| **`ZXSystemMapLayers`** | `#=zJme8KFhmikprnkg_2CDeiQE=` | `#=zsW2J3r72Cu83` | `DXGame.Current.GetSystem<#=zJme8KFhmikprnkg_2CDeiQE=>()` | 地图图层系统：管理战争迷雾 `LayerFogUpdated`、电力网 `LayerEnergyUpdated`、视野更新。 |
| **`ZXSystemAudio`** | `#=zJYbPa4gP2VI16zEh5y2Zxew=` | `#=zsW2J3r72Cu83` | `#=zJYbPa4gP2VI16zEh5y2Zxew=.#=zQFWYIvs=` | 音效与语音播放系统。 |
| **`ZXSystemBuilder`** | `#=z5Ns_eH8ENLF7HFU5mg==` | `#=zsW2J3r72Cu83` | `#=z5Ns_eH8ENLF7HFU5mg==.#=zuartwoo=()` | 建筑施工进度与单位受击警报系统。 |
| **`ZXSteamManager`** | `#=z3Zxcp6RwVCZHa9xpeg==` | `System.Object` | *(静态类)* | Steam 成就、统计数据与 `SteamAPI.RunCallbacks()` 驱动（每 60 渲染帧调用 `#=zVnIv8xS3EOZr()`）。 |
| **`ZXMessageBoxHelper`** | `#=zzi2cl4h1HYA5cX8aaw==` | `System.Object` | *(静态类)* | 游戏原生弹窗工具类（确认框、输入框、错误提示框）。 |
| **`ZXGameState`** | `ZX.ZXGameState` | `System.Object` | `ZXGameState.#=zuartwoo=()` | 顶层游戏状态（含 `LevelState`, `CampaignState`, `SurvivalModeParams`, `Difficulty`, `Name`）。 |
| **`ZXLevelState`** | `ZX.ZXLevelState` | `ZX.DXGameState` | `ZXLevelState.#=zuartwoo=()` | **局内核心数据状态**：包含金木石铁油资源、产量、电力、食物、工人、`LevelEntities`、市长、尸潮事件。 |

---

## 3. `DXVision.dll`（未混淆引擎层）关键类与方法索引

因为 `DXVision.dll` 完全没有混淆，Mod 可以直接强类型引用以下成员：

### 3.1 `DXVision.DXGame`（引擎主循环与实体管理）
- **静态属性与字段**：
  - `public static DXGame Current { get; set; }` —— 当前游戏实例
  - `public static bool IsIADeterministic = false;` —— **确定性开关！设为 `true` 后单线程顺序更新 `_Entities` 并同步更新物理**
  - `public static object SyncLogic = new object();` —— 逻辑线程与渲染线程同步锁
  - `public static double LastGameTimeIAUpdate { get; set; }` —— 上一次 IA 逻辑更新的游戏时间戳（毫秒）
  - `public int IAIterationInt { get; set; }` —— 当前逻辑帧计数（每 Tick +1，30 Ticks/秒）
- **实例属性与字段**：
  - `public double GameTime { get; set; }` —— 当前游戏累计逻辑时间（毫秒）
  - `public double GameSpeed { get; set; }` —— 游戏倍速（`0.0` 为暂停，`1.0` 为正常速度）
  - `public bool Paused { get; set; }` —— 读写 `GameSpeed <= 0.0`
  - `public DXScene Scene { get; }` —— 当前渲染场景（含 `Scene.CameraArea`, `Scene.CameraCenter`）
  - `public IEnumerable<DXEntity> Entities { get; }` —— 底层为 `private List<DXEntity> _Entities`
  - `private Dictionary<ulong, DXEntity> _EntitiesByID` —— 实体 ID 查找表，公开方法为 `public DXEntity GetEntityByID(ulong id)`
- **核心生命周期方法（Hook 目标）**：
  - `internal void StartFrame()` —— 每渲染帧开始，调用 `ProcessInputEvents(true)`
  - `internal void SetCamera()` —— 更新摄像机、执行 `OnStartFrame()`、更新可见实体场景对象、调用 `UpdateGameLogic()`
  - `private void UpdateGameLogic()` —— 当 `GameTime >= LastGameTimeIAUpdate + 33.333333333333336` 时步进 1 个逻辑 Tick（30Hz）
  - `private void WaitForGameLogic()` —— 等待 `_TaskLogic` 完成
  - `public void LoadPhysics()` / `public void LoadPhysicsOld()` / `public void UnloadPhysics()` —— 启动/停止后台计时与物理线程
  - `private void UpdatePhysics()` —— 当 `GameTime - DXPhysicWorld.LastGameTimeUpdatePhysics >= 83.33333333333333` 时步进物理（12Hz）
  - `public void InvokeOnStartFrame(Action a)` / `public void InvokeOnFinishFrame(Action a)` —— 向主渲染线程投递委托

### 3.2 `DXVision.DXEntity` & `DXVision.DXEntityRef`
- `public ulong ID { get; set; }` —— 实体全局唯一 64 位整数 ID
- `public Point Cell { get; set; }` / `public Rectangle CellArea { get; set; }` —— 网格整数坐标
- `public PointF Position { get; set; }` —— 世界浮点坐标
- `public float Angle { get; set; }` —— 朝向角度
- `public T GetComponent<T>()` / `public T AddComponent<T>()` / `public bool RemoveComponent<T>()`

### 3.3 `DXVision.DXGuid` & `DXVision.DXRandom`
- `public static ulong DXGuid.New()` —— 生成 64 位实体 ID（**必须 Hook Prefix 接管**）
- `public static DXRandom DXRandom.Global { get; }` —— 线程静态随机数生成器（**必须固定种子**）
- `public uint[] DXRandom.Mt { get; set; }` —— 梅森旋转内部状态数组（624 个 `uint`）

---

## 4. `TheyAreBillions.exe` 关键类混淆方法详细字典

### 4.1 主游戏类 `ZXGame` (`#=z4RevDP3eECXqXS6JRA==`)

| 混淆名 | 方法签名 / 类型 | 语义还原与用途 |
| :--- | :--- | :--- |
| `#=zuartwoo=()` | `public static #=z4RevDP3eECXqXS6JRA== #=zuartwoo=()` | `ZXGame.Current` 单例获取 |
| `#=zCj$vlp8=` | `public List<ZXCommand> #=zCj$vlp8=` (Field) | 全局所有已注册 `ZXCommand` 实例列表 |
| `#=zBYC_Wew=` | `public static string #=zBYC_Wew=(string key)` | 多语言字符串本地化查询 `Localize(string key)` |
| `#=zND5ul2zfzAnWdSnC0A==` | `public static string #=zND5ul2zfzAnWdSnC0A==()` | 获取存档文件夹路径 (`Documents\My Games\They Are Billions\Saves`) |
| `#=zMtGuEM2lBSlZ5BGWvg==` | `private void #=zMtGuEM2lBSlZ5BGWvg==(string filePath)` | **写存档文件核心方法** `SaveGameStateToFile(string filePath)`（生成 `.zxsav` 和 `.zxcheck`） |
| `#=z3YFOTVtBw_rT7oSQRA==` | `private ZXGameState #=z3YFOTVtBw_rT7oSQRA==(string filePath)` | **读存档文件反序列化** `DeserializeGameStateFromFile(string filePath)` |
| `#=zSV0_oCta8rEv` | `internal void #=zSV0_oCta8rEv(string name, Action onDone, bool showModal, bool updateTime)` | 完整保存游戏流程 `SaveGame(string saveName, ...)` |
| `#=z9IpK6P4YlW8GIY7olg==` | `public void #=z9IpK6P4YlW8GIY7olg==(string saveNameOrPath)` | **完整加载存档并进入关卡流程** `LoadGameAndEnterLevel(string path)` |
| `#=zcITiOSD6YNmD` | `public void #=zcITiOSD6YNmD()` | **弹出市长二选一窗口** `ShowMayorSelectionWindow()` |
| `#=zx3_1WsJs4Dyck79oqg==` | `public void #=zx3_1WsJs4Dyck79oqg==()` | 查看已选市长列表窗口 `ShowSelectedMayorsWindow()` |
| `#=zqF2tlklT$8RE` | `public void #=zqF2tlklT$8RE(Action beforeLoad, bool showLoading)` | 退回主菜单系统 `ReturnToMainMenu()` |

### 4.2 局内关卡系统 `ZXSystemGameLevel` (`#=zxRcpu6e7NYzT7tGWqPjpOkc=`)

| 混淆名 | 方法签名 / 字段类型 | 语义还原与用途 |
| :--- | :--- | :--- |
| `#=zuartwoo=()` | `public static #=zxRcpu6e7NYzT7tGWqPjpOkc= #=zuartwoo=()` | `ZXSystemGameLevel.Current` 单例获取 |
| `#=z0o_eM4o=` | `internal ZXCommand #=z0o_eM4o=` (Field) | 当前正在等待选择目标的命令（如点击了建造或巡逻后等待左键落地） |
| `#=zsP3wQ40=` | `internal ZXCommandTarget #=zsP3wQ40=` (Field) | 当前命令的目标结构体缓存 |
| `#=zetgfbFxpsSuV` | `private PointF #=zetgfbFxpsSuV` (Field) | 当前鼠标所在的世界浮点坐标 `MouseWorldPosition` |
| `#=zMaiMxP6Lfc1s` | `private Point #=zMaiMxP6Lfc1s` (Field) | 当前鼠标所在的网格坐标 `MouseWorldCell` |
| `#=z_QMU7SQiIBfj` | `private DXEntity #=z_QMU7SQiIBfj` (Field) | 当前鼠标悬停下的实体（对应公开属性 `MouseEntity`） |
| `#=zZ$S0aaS3NQsgmtkOww==` | `private OCellAreaCursor #=zZ$S0aaS3NQsgmtkOww==` (Field) | 建造模式下的地面网格绿/红光标对象 |
| `#=z4cHfUtNBoiM8` | `private ZXControlCommandsManager #=z4cHfUtNBoiM8` (Field) | 右下角命令按钮面板管理器 |
| `#=zCJSdEwbGvSqa` | `private ZXControlSelectionManager #=zCJSdEwbGvSqa` (Field) | 底部多单位选中头像栏管理器 |
| `#=zmTU4kueQctVr` | `public void #=zmTU4kueQctVr(DXLevel level)` | 初始化并绑定关卡 `InitLevel(DXLevel level)` |
| `#=zGxTz6vGn2dxDprCE7A==` | `private void #=zGxTz6vGn2dxDprCE7A==(DXObject sender)` | **场景鼠标右键点击处理函数** `OnSceneMouseRightDown`（触发移动、攻击、拾取、进塔、交互） |
| `#=zX9V39sQYkQxr95dJ7A==` | `private void #=zX9V39sQYkQxr95dJ7A==(DXObject sender)` | **场景鼠标左键点击处理函数** `OnSceneMouseLeftDown`（触发单选或确认命令目标） |
| `#=zdsJiJl0qh63o` | `private bool #=zdsJiJl0qh63o()` | 在左键按下时校验并填充 `#=zsP3wQ40=` 目标，然后调用 `#=zN0jTYBQMVgRf(true)` |
| `#=zN0jTYBQMVgRf` | `internal void #=zN0jTYBQMVgRf(bool showAlert)` | **执行当前挂起的命令** `ExecutePendingCommand(bool showAlert)` |
| `#=zQp2M56bpPd9jmiNkVA==` | `public void #=zQp2M56bpPd9jmiNkVA==(ZXCommand cmd, PointF dest)` | 对当前 `CSelectable` 选中的所有可移动单位执行群体移动/命令 |
| `#=zgn6bDkSd9MGS3$WvAQ==` | `public void #=zgn6bDkSd9MGS3$WvAQ==(List<DXEntity> units, ZXCommand cmd, PointF dest)` | **核心群体寻路散开与目标分配函数** `AssignGroupMoveTargets(List<DXEntity> units, ZXCommand cmd, PointF dest)`（不依赖 `CSelectable`！） |
| `#=znoo7TyEtVemrA_J6FQ==` | `public void #=znoo7TyEtVemrA_J6FQ==(PointF pos, ZXCommandTargetIndicatorColor color, float scale)` | 在地面播放绿/红点击指示圈动画 `ShowGroundClickIndicator` |
| `#=zhWLC3K8=` | `public void #=zhWLC3K8=(string text, Color color, int durationMs)` | **在屏幕顶部中央显示横幅文字提示** `ShowTopBannerMessage(string text, Color color, int durationMs)` |
| `#=zYWs2Gv9F3GD5` | `public void #=zYWs2Gv9F3GD5(string text, Color color, DXProjectImage icon, Action onClick)` | 在屏幕左侧通知队列添加一条通知图标 `EnqueueNotification` |
| `#=zR1qHCYY=` | `private void #=zR1qHCYY=()` | 刷新右下角命令面板 `RefreshCommandPanel()` |

### 4.3 命令基类 `ZX.Commands.ZXCommand` 与子类 `Build` / `Train`

| 类名 | 混淆方法名 | 方法签名 | 语义还原与用途 |
| :--- | :--- | :--- | :--- |
| `ZXCommand` | `#=z5pCZsgI=<T>` | `public static T #=z5pCZsgI=<T>()` | 获取指定类型的命令单例 `GetCommand<T>()`（如 `GetCommand<Travel>()`） |
| `ZXCommand` | `#=zqRskexw=` | `public static ZXCommand #=zqRskexw=(int id)` | 根据 `ID` 哈希值查找命令实例 `GetCommandByID(int id)` |
| `ZXCommand` | `#=z4koKvss=` | `public static ZXCommand #=z4koKvss=(string typeName)` | 根据类型名称查找命令实例 `GetCommandByName(string name)` |
| `ZXCommand` | `#=zfmgzkuw=` | `public void #=zfmgzkuw=(ZXEntity executor, ZXCommandTarget target)` | **执行命令核心入口** `Execute(ZXEntity executor, ZXCommandTarget target)` |
| `ZXCommand` | `#=zKc8Kpc_EaFhf` | `public virtual bool #=zKc8Kpc_EaFhf(ZXEntity entity)` | 检查命令当前是否可用/资源是否足够 `CanExecute(ZXEntity entity)` |
| `ZXCommand` | `#=zXAX1VeKTLe7O` | `public virtual bool #=zXAX1VeKTLe7O(ZXEntity entity)` | 检查命令是否应在面板显示 `IsVisible(ZXEntity entity)` |
| `ZXCommand` | `#=zcPu1$70ruiRE` | `public virtual ZXResourcesCost? #=zcPu1$70ruiRE(ZXEntity entity)` | 获取命令资源消耗 `GetResourcesCost(ZXEntity entity)` |
| `ZXCommand` | `#=z9whD4sw=` | `public void #=z9whD4sw=(ZXEntity entity, bool advanceQueue)` | 取消当前正在进行的命令并返还资源 `CancelExecution(ZXEntity entity, bool advanceQueue)` |
| `Build` | `#=zQ4lx63I=` | `public static Build #=zQ4lx63I=(Type entityType)` | 获取对应建筑类型的建造命令实例 `GetBuildCommand(Type buildingType)` |
| `Build` | `#=zBTwSMLo=` | `public static ZXEntity #=zBTwSMLo=(ZXEntity builder, ZXCommandTarget target, Type entityType, bool isBonus, bool instant, DXEntityTemplate template)` | **在指定网格创建建筑实体核心静态方法** `PlaceBuilding(...)` |
| `Train` | `#=zQ4lx63I=` | `public static Train #=zQ4lx63I=(Type unitType)` | 获取对应单位类型的训练命令实例 `GetTrainCommand(Type unitType)` |

### 4.4 实体与组件类（`ZX.Entities.ZXEntity`, `CCommandable`, `CSelectable`, `CLife`）

| 类名 | 混淆成员名 | 签名 | 语义还原与用途 |
| :--- | :--- | :--- | :--- |
| `ZXEntity` | `#=zkK_YqS85HrT3g75MJrlvMYo=` | `public CCommandable #=zkK_YqS85HrT3g75MJrlvMYo=()` | 获取实体的 `CCommandable` 组件 |
| `ZXEntity` | `#=zTXZoQFXOaWH7` | `public CSelectable #=zTXZoQFXOaWH7()` | 获取实体的 `CSelectable` 组件 |
| `ZXEntity` | `#=zS6TvFuwF_68l` | `public CLife #=zS6TvFuwF_68l()` | 获取实体的 `CLife` 生命值组件 |
| `ZXEntity` | `#=zCv3n3xlGOMilHuHlHA==` | `public CMovable #=zCv3n3xlGOMilHuHlHA==()` | 获取实体的 `CMovable` 移动组件 |
| `ZXEntity` | `#=zcODCID3ur9P9` | `public ZXEntityDefaultParams #=zcODCID3ur9P9()` | 获取实体的静态属性配置表参数 `Params` |
| `CCommandable` | `#=zE83e_h4=` | `public ZXCommand #=zE83e_h4=()` | 获取当前正在执行的命令 `CurrentCommand` |
| `CCommandable` | `#=zQKpiMFU=` | `public void #=zQKpiMFU=(ZXCommand cmd)` | 设置当前执行的命令 `SetCurrentCommand(ZXCommand cmd)` |
| `CCommandable` | `#=zyCiF7Lo=` | `internal ZXCommandTarget #=zyCiF7Lo=` (Field) | 当前命令的目标结构体（对应公开属性 `Target`） |
| `CCommandable` | `#=zRQYIK_o=` | `public void #=zRQYIK_o=(ZXCommand cmd, ZXCommandTarget target)` | 将命令加入生产队列 `EnqueueOrder(ZXCommand cmd, ZXCommandTarget target)` |
| `CCommandable` | `#=z8g7GC7xLrr8j` | `public void #=z8g7GC7xLrr8j(int slotIndex)` | 取消生产队列中第 `slotIndex` 槽位的订单并退款 `CancelQueuedOrderAt(int slotIndex)` |
| `CCommandable` | `#=zWGiN6nrTv8Dp` | `public void #=zWGiN6nrTv8Dp()` | 取消当前正在进行的命令 `CancelCurrentCommand()` |
| `CSelectable` | `#=zIiHCgn_odqH7` | `public static DXConcurrentList<CSelectable> #=zIiHCgn_odqH7` | **本地当前选中的所有实体列表** `CurrentlySelectedList` |
| `CSelectable` | `#=zM3nhRXPdWdhx` | `public static ZXEntity #=zM3nhRXPdWdhx()` | 获取当前选中的主实体 `PrimarySelectedEntity` |
| `CSelectable` | `#=zClknEUY73pQc` | `public static void #=zClknEUY73pQc()` | 清空当前所有选中 `UnselectAll()` |

### 4.5 关卡状态类 `ZX.ZXLevelState` 关键方法

`ZXLevelState` 的绝大部分属性（`Gold`, `Wood`, `Stone`, `Iron`, `Oil`, `FoodReserved`, `EnergyReserved`, `WorkersReserved`, `LevelEntities`, `Mayors` 等）**全部是明文公开属性**！以下是其关键混淆方法：

| 混淆方法名 | 签名 | 语义还原与用途 |
| :--- | :--- | :--- |
| `#=zuartwoo=()` | `public static ZXLevelState #=zuartwoo=()` | `ZXLevelState.Current` 单例获取 |
| `#=zQXHqcVh9mGZZ` | `public override void #=zQXHqcVh9mGZZ()` | 存档前同步保存 `DXGame.Current.GameTime` 和物理时间到状态对象 |
| `#=zF1YHTRUZSNRa` | `public override void #=zF1YHTRUZSNRa()` | 读档后将状态对象的时间参数恢复到 `DXGame.Current` 并重启物理 |
| `#=z5YtS5bwlM1kX` | `public void #=z5YtS5bwlM1kX(ZXResourcesCost cost)` | **扣除资源** `ConsumeResources(ZXResourcesCost cost)` |
| `#=zG0slAM4=` | `public void #=zG0slAM4=(ZXResourcesCost cost)` | **返还/增加资源** `RefundResources(ZXResourcesCost cost)` |
| `#=zh4x6WKNtqKnr` | `public bool #=zh4x6WKNtqKnr(ZXResourcesCost cost)` | **检查资源是否足够支付** `CanAffordResources(ZXResourcesCost cost)` |
| `#=zOHDY2QTzgRSB` | `public DXLevel #=zOHDY2QTzgRSB()` | 获取底层 `DXLevel` 实例 |

### 4.6 主菜单系统 `ZXSystemMainMenu` (`#=zLs$VX1eoAapVflmW_w==`) 与弹窗工具 `#=zzi2cl4h1HYA5cX8aaw==`

| 类名 | 混淆成员名 | 签名 | 语义还原与用途 |
| :--- | :--- | :--- | :--- |
| `ZXSystemMainMenu` | `#=zUM4xl3ca4QS35TzMAg==` | `private DXGridLayout #=zUM4xl3ca4QS35TzMAg==` | 主菜单垂直按钮布局容器 |
| `ZXSystemMainMenu` | `#=zyDj$brKr7icb` | `private Dictionary<TMenuCategory, List<ZXButtonLink>>` | 存放 `Main`, `New`, `More` 三个层级菜单按钮的字典（**在此注入联机按钮最完美！**） |
| `ZXSystemMainMenu` | `#=zp8c19tQ=` | `private void #=zp8c19tQ=(TMenuCategory cat)` | 切换显示的菜单按钮分类 |
| `ZXMessageBoxHelper` (`#=zzi2cl4h1HYA5cX8aaw==`) | `#=zcvkFXLt$qEo6` | `public static ZXWindow #=zcvkFXLt$qEo6(string title, string msg, Action onOk)` | 显示单按钮提示弹窗 `ShowMessageBox(title, msg, onOk)` |
| `ZXMessageBoxHelper` (`#=zzi2cl4h1HYA5cX8aaw==`) | `#=zFNDB6pz7Wqg3` | `public static void #=zFNDB6pz7Wqg3(string title, string msg, Action onYes, Action onNo)` | 显示确认/取消双按钮弹窗 `ShowConfirmBox(title, msg, onYes, onNo)` |
| `ZXMessageBoxHelper` (`#=zzi2cl4h1HYA5cX8aaw==`) | `#=znd_PcbSNuH79` | `public static ZXWindow #=znd_PcbSNuH79(string title, string prompt, string defaultText, ...)` | 显示文本输入弹窗 `ShowInputBox(...)` |
| `OMiniMap` | `#=zvr$ku1Q=` | `internal void #=zvr$ku1Q=(Point cell, string text, DXProjectImage img, int durationMs)` | **在小地图指定格子播放雷达闪烁波纹动画** `PingMiniMapCell(...)` |
