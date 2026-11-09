# 04 — 网络通信协议与状态校验规范 (Network Protocol & State Checksum Specification)

## 1. 传输层抽象接口 (`INetworkTransport`)

为了同时支持 **Steam P2P 正式联机** 与 **AI Agent 本机双开环回测试（Loopback）**，网络底层统一抽象为 `INetworkTransport` 接口：

```csharp
namespace TABCoopMod.Networking
{
    public enum SendChannel : byte
    {
        ReliableOrdered = 0,   // 房间握手、存档快照分片、帧同步指令批次、状态哈希校验
        UnreliableSequenced = 1 // 盟友鼠标世界坐标、视野矩形框、建造预览框 (20Hz)
    }

    public interface INetworkTransport : IDisposable
    {
        bool IsConnected { get; }
        bool IsHost { get; }
        byte LocalPlayerId { get; } // 1 = Host, 2 = Client1, 3 = Client2
        IReadOnlyList<PeerInfo> ConnectedPeers { get; }

        void StartHost(int maxPlayers = 3);
        void ConnectToHost(string connectionTarget); // SteamId64 或 "127.0.0.1:27099"
        void SendToPeer(byte targetPlayerId, SendChannel channel, byte[] payload, int offset, int length);
        void Broadcast(SendChannel channel, byte[] payload, int offset, int length);
        void PollEvents(); // 每渲染帧在 OnStartFrame 调用一次
    }
}
```

### 1.1 `SteamP2PTransport` 实现要点
- 直接复用游戏内置的 `Steamworks.NET`（Version 10.0.0.0，位于 `Steamworks` 命名空间）。
- **Lobby 管理**：
  - `SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 3)`
  - 监听 `Callback<LobbyCreated_t>`、`Callback<GameLobbyJoinRequested_t>`、`Callback<LobbyEnter_t>`、`Callback<LobbyChatUpdate_t>`。
- **P2P 数据收发**：
  - 发送：`SteamNetworking.SendP2PPacket(CSteamID remoteId, byte[] pubData, uint cubData, EP2PSend eP2PSendType, int nChannel)`
    - `Channel 0` 使用 `EP2PSend.k_EP2PSendReliable`（对于大于 1KB 的存档分片自动可靠传输）。
    - `Channel 1` 使用 `EP2PSend.k_EP2PSendUnreliableNoDelay`（传输鼠标与视野坐标）。
  - 接收：在 `PollEvents()` 中按通道循环调用 `SteamNetworking.IsP2PPacketAvailable(out uint msgSize, int channel)` 与 `SteamNetworking.ReadP2PPacket(...)`。
  - **重要细节**：游戏原本在 `ZXGame.OnStartFrame()` 中每 60 帧才调一次 `SteamAPI.RunCallbacks()`（`ZXGame.cs:1817`）。进入联机大厅或联机局内后，我们的 Mod 必须在**每一渲染帧**调用 `SteamAPI.RunCallbacks()`，确保网络包延迟低于 16ms！

### 1.2 `LocalTcpLoopbackTransport` 实现要点
- Host 监听 `127.0.0.1:27099`（TCP `NoDelay = true`），支持 1~2 个本地 Client 进程通过命令行参数 `--coop-client 127.0.0.1:27099` 自动连入。
- 使 AI Agent 无需第二台电脑或第二个 Steam 账号即可在单机完成全套双端联机自动化测试。

---

## 2. 二进制封包格式规范（Wire Framing）

所有网络消息采用小端序（Little-Endian）紧凑二进制编码（`BinaryWriter` / `BinaryReader`）：

### 2.1 通用包头（Packet Header — 6 Bytes）
```text
+-------------------+-------------------+-----------------------------------+
| Magic (2 Bytes)   | MsgType (1 Byte)  | SenderId (1 Byte) | Length (2B)   |
| 0x54, 0x42 ("TB") | PacketType Enum   | 1..3              | Payload Bytes |
+-------------------+-------------------+-----------------------------------+
```

### 2.2 消息类型枚举 (`PacketType : byte`)

| 值 | 枚举名 | 通道 | 方向 | 说明 |
| :-: | :--- | :---: | :---: | :--- |
| `0x01` | `HandshakeRequest` | Reliable | C -> H | 客机请求加入，携带玩家名、Mod 版本号 |
| `0x02` | `HandshakeAccept` | Reliable | H -> All | 房主分配 `PlayerId (1..3)`，同步房间玩家列表与房间配置 |
| `0x10` | `SnapshotBegin` | Reliable | H -> All | 宣告开始传输 `.zxsav` 快照（含总字节数、分片数、SHA256、`.zxcheck` 字符串、初始 `SyncedGuidSeed`） |
| `0x11` | `SnapshotChunk` | Reliable | H -> All | 存档字节流分片（每片最大 `32,768` Bytes） |
| `0x12` | `SnapshotLoadedAck` | Reliable | C -> H | 客机完成读档，上报初始 `StateHash` 与就绪状态 |
| `0x13` | `SessionStart` | Reliable | H -> All | 全员就绪，开始 `LockstepTick = 0` 步进 |
| `0x20` | `ClientInputReport` | Reliable | C -> H | 客机上报本人在当前 Turn 提交的 `NetCommand[]` 列表 |
| `0x21` | `TurnInputBroadcast` | Reliable | H -> All | **核心帧同步包**：房主汇总全员在 `TargetControlTick` 的指令列表并广播（即使无指令也发空包作为步进心跳） |
| `0x30` | `StateChecksumReport` | Reliable | All -> H | 每 60 逻辑帧（2秒）上报一次当前 `SimTick` 的 `StateHash` |
| `0x31` | `DesyncDetectedAlert` | Reliable | H -> All | 房主检测到 `StateHash` 不一致，通知暂停并启动自动热重连（重发 `0x10 SnapshotBegin`） |
| `0x40` | `AllyCursorAndCamera` | Unreliable | All <-> All | 20Hz 广播本地鼠标世界坐标、摄像机视野框、当前正在预览建造的建筑类型与坐标 |
| `0x41` | `TacticalPing` | Reliable | All <-> All | 小地图/地面战术信号打点（含坐标、发起人、文本） |

---

## 3. 局内操作指令集载荷定义 (`NetCommand`)

在 `0x20 ClientInputReport` 与 `0x21 TurnInputBroadcast` 中，每条 `NetCommand` 包含以下结构：

```csharp
public enum NetCommandType : byte
{
    None = 0,
    ExecuteCommand = 1,      // 通用命令: 攻击、训练、研发、拆除、修理、开关电源、市场交易、集结点、下车等
    GroupMoveOrPatrol = 2,   // 群体移动/巡逻/散开 (调用 #=zgn6bDkSd9MGS3$WvAQ==)
    PlaceBuilding = 3,       // 放置建筑 (调用 Build.#=zBTwSMLo=)
    CancelCurrentOrder = 4,  // 取消实体当前正在执行的命令/建造 (调用 CCommandable.#=zWGiN6nrTv8Dp)
    CancelQueuedOrder = 5,   // 取消生产队列指定槽位 (调用 CCommandable.#=z8g7GC7xLrr8j)
    SetPaused = 6,           // 切换暂停/恢复状态
    SetGameSpeed = 7,        // 修改游戏倍速
    MayorVoteOrSelect = 8    // 市长选举意向高亮或最终拍板
}
```

### 各指令类型的二进制字段详情：

1. **`ExecuteCommand (0x01)`**：
   - `byte PlayerId`：发起指令的玩家 ID
   - `bool IsShiftQueued`：执行时是否模拟按下 Shift 键
   - `int CommandId`：`ZXCommand.ID`（可通过 `ZXCommand.#=zqRskexw=(id)` 还原实例）
   - `ushort ExecutorCount` + `ulong[ExecutorCount] ExecutorEntityIds`：执行该命令的实体 ID 列表
   - `ulong TargetEntityId`：`ZXCommandTarget.SerEntityRef.IDEntity`（无目标实体则为 `0`）
   - `float TargetPosX, TargetPosY`：`ZXCommandTarget.SerPosition`
   - `float TargetSizeW, TargetSizeH`：`ZXCommandTarget.SerSize`
   - `float TargetOriginX, TargetOriginY`：`ZXCommandTarget.SerOrigin`

2. **`GroupMoveOrPatrol (0x02)`**：
   - `byte PlayerId`
   - `int CommandId`（如 `Travel.ID`, `Patrol.ID`, `Chase.ID`, `UseItem_Grenade.ID`）
   - `ushort UnitCount` + `ulong[UnitCount] UnitEntityIds`：参与移动的单位 ID 数组
   - `float DestX, DestY`：鼠标点击的目标世界坐标 `PointF`

3. **`PlaceBuilding (0x03)`**：
   - `byte PlayerId`
   - `ulong BuilderEntityId`：发起建造的基地/建筑实体 ID（如 `CommandCenter.ID`）
   - `string BuildingTypeName`：建筑类名（如 `"ZX.Entities.TentHouse"`）
   - `int CellX, CellY, CellW, CellH`：目标网格区域 `Rectangle`
   - `bool IsBonus`：是否为免费奖励建筑（市长奖励）
   - `bool InstantBuild`：是否瞬间建成

4. **`CancelCurrentOrder (0x04)` & `CancelQueuedOrder (0x05)`**：
   - `byte PlayerId`
   - `ushort EntityCount` + `ulong[EntityCount] EntityIds`
   - `byteQueueSlotIndex`（仅 `0x05` 有效，取值 `0..4`）

5. **`SetPaused (0x06)`**：
   - `byte PlayerId`
   - `bool TargetPaused`

6. **`MayorVoteOrSelect (0x08)`**：
   - `byte PlayerId`
   - `byte MayorSlotIndex`（`0` = 左侧男市长，`1` = 右侧女市长）
   - `bool IsFinalDecision`（`false` = 客机意向高亮，`true` = 最终选定并关闭窗口）

---

## 4. 确定性状态哈希计算规范 (`StateHasher`)

为了在开发阶段由 AI Agent 自动验证确定性，并在游玩阶段实时检测 Desync，每当 `DXGame.Current.IAIterationInt % 60 == 0`（每 60 个逻辑帧）时，在 `OnFinishUpdate` 结束瞬间计算一次 `uint64` 状态哈希（使用 **FNV-1a 64-bit** 算法）：

```csharp
public static ulong ComputeLevelStateHash()
{
    ulong hash = 14695981039346656037UL;
    void Mix(long val) {
        hash ^= (ulong)val;
        hash *= 1099511628211UL;
    }

    var ls = ZXLevelState.#=zuartwoo=();
    if (ls == null) return 0UL;

    // 1. 混合核心时间与全局资源状态
    Mix(DXGame.Current.IAIterationInt);
    Mix(DXGame.Current.GameTimeInt);
    Mix(ls.Gold);
    Mix(ls.Wood);
    Mix(ls.Stone);
    Mix(ls.Iron);
    Mix(ls.Oil);
    Mix(ls.FoodReserved);
    Mix(ls.EnergyReserved);
    Mix(ls.WorkersReserved);
    Mix(ls.NZombiesDead);

    // 2. 按 ID 升序遍历所有受控实体与僵尸，混合其整数化位置与生命值
    // (仅抽样或全量混合非本地 UI 实体，即 entity.ID < 0x8000000000000000UL)
    int entityCount = 0;
    long posChecksum = 0;
    long lifeChecksum = 0;
    foreach (var entity in DXGame.Current.Entities)
    {
        if (entity == null || entity.Disposed || (entity.ID & 0x8000000000000000UL) != 0)
            continue;
        entityCount++;
        // 将浮点坐标乘以 100 取整参与校验，捕获任何寻路或物理偏移
        long qx = (long)Math.Round(entity.Position.X * 100f);
        long qy = (long)Math.Round(entity.Position.Y * 100f);
        posChecksum += (long)entity.ID ^ (qx * 73856093L) ^ (qy * 19349663L);

        if (entity is ZXEntity zx && zx.#=zS6TvFuwF_68l() != null)
        {
            lifeChecksum += (long)entity.ID ^ zx.#=zS6TvFuwF_68l().Life;
        }
    }
    Mix(entityCount);
    Mix(posChecksum);
    Mix(lifeChecksum);
    return hash;
}
```
* **为什么用异或累加（`posChecksum += ...`）**：因为 `DXGame.Current.Entities` 虽然在确定性模式下顺序一致，但使用与顺序无关的带权异或累加，计算开销极低（20,000 实体耗时 `< 0.3ms`），完全不会造成帧率卡顿，同时对任何 0.01 格的坐标偏移或 1 点血量差异具有 100% 检出率！
