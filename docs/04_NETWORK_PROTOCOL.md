# 04 — 网络通信协议与状态校验规范 (Network Protocol & State Checksum Specification)

> **状态**: 已与源码 [`INetworkTransport.cs`](../src/TABCoopMod/Networking/INetworkTransport.cs)、[`DirectFastPath.cs`](../src/TABCoopMod/Networking/DirectFastPath.cs)、[`SteamP2PTransport.cs`](../src/TABCoopMod/Networking/SteamP2PTransport.cs)、[`NetCommand.cs`](../src/TABCoopMod/Commands/NetCommand.cs) 与 [`StateHasher.cs`](../src/TABCoopMod/Determinism/StateHasher.cs) 对齐（`v0.1.7`）。

---

## 1. 传输层抽象接口 (`INetworkTransport`)

为了同时支持 **Steam P2P 正式联机（含 `DirectFastPath` 双栈直连加速）** 与 **本机 TCP 环回自动化测试（Loopback + `ChaosTransport`）**，底层统一抽象为 `INetworkTransport`：

```csharp
namespace TABCoopMod.Networking
{
    public enum SendChannel : byte
    {
        ReliableOrdered = 0,     // 大体积可靠同步 (Snapshot, PosDriftSync, StateChecksumReport)
        UnreliableSequenced = 1, // 低延迟非可靠状态 (AllyCursorAndCamera, RttPing/Pong)
        ReliableCommand = 2      // 高优先级可靠指令 (Inputs, Turns, Lobby, SessionStart, LevelEnded)
    }

    public interface ITransportDiagnostics
    {
        bool IsDirectConnected { get; }
        string ActivePathSummary { get; }
        double ActiveDirectRttMs { get; }
        string GetDebugStatsLine();
    }

    public interface INetworkTransport : IDisposable
    {
        bool IsActive { get; }
        bool IsHost { get; }
        byte LocalPlayerId { get; } // 1 = Host, 2 = Client1, 3 = Client2
        IReadOnlyList<PeerInfo> ConnectedPeers { get; }

        event Action<byte, PacketType, BinaryReader> OnPacketReceived;
        event Action<PeerInfo> OnPeerConnected;
        event Action<byte> OnPeerDisconnected;
        event Action<string, string, string> OnVersionMismatch;

        void StartHost(int maxPlayers = 3);
        void ConnectToHost(string targetAddressOrSteamId); // SteamId64 或 "127.0.0.1:27099"
        void SendToPeer(byte targetPlayerId, SendChannel channel, PacketType type, Action<BinaryWriter> writePayload);
        void Broadcast(SendChannel channel, PacketType type, Action<BinaryWriter> writePayload);
        void PollEvents(); // 每渲染帧由主线程调用
        void SyncPeers(IEnumerable<PeerInfo> peers); // 房主全量同步成员与准备状态
    }
}
```

### 1.1 实现类说明
- **`SteamP2PTransport`**：复用内嵌 `Steamworks.NET` v10（`SteamMatchmaking` + `SteamNetworking`），按 `SendChannel` 映射到独立虚拟通道 `nChannel`（`2` 高优指令可靠包、`0` 大块数据可靠包、`1` 低延迟非可靠包），彻底隔离快照/`PosDriftSync` 与回合指令防止队头阻塞；支持最小空闲槽位复用（`AllocateLowestFreePlayerId`，杜绝退房重进变成 `P4`/`P5`）、按 `CSteamID` 幂等去重、大厅 `tab_coop_ver` + `HandshakeAccept` 双端版本一致性校验（`OnVersionMismatch`），并通过 `RegisterGlobalInviteListener()` + `ConsumePendingInviteLobbyId()` + `Dispose()` 回调注销保障邀请安全进房。
- **`DirectFastPathManager`**：挂载于 `SteamP2PTransport` 之上的双栈（IPv4 / IPv6）UDP 直连加速层。经 Steam 信令交换候选端点（`LanIPv4` / `VirtualLan` / `PublicIPv6` / `PublicIPv4` / `UpnpIPv4` / `StunIPv4`），使用 `TBDP` 协议分片传输（`ChunkPayloadBytes = 1080`，`CommandWindowChunks = 48`、`BulkWindowChunks = 48`、64-bit SACK 确认窗口）；支持大厅 20s STUN 保活与 3s 未验证自动重推、对局中 3s 数据通路假死（`DirectDataStallFallbackMs = 3000.0`）自动无损回落 `DirectFallbackBatch` 至 Steam P2P 通道，并向 `ITransportDiagnostics` 提供线速 `ActiveDirectRttMs`。
- **`LocalTcpLoopbackTransport`**：Host 监听 `127.0.0.1:27099`（TCP `NoDelay = true`），同样支持最小空闲槽位复用、版本校验、非侵入式 `ProbeLocalHost()` 与空闲断线检测（`IsSocketDisconnected`），用于双开与自动化回归。
- **`ChaosTransport`**：可配置的故障注入装饰器（延迟 / 抖动 / 丢包 / 断连窗口，带确定性 `seed`，并支持局内/大厅动态调整参数），包裹任意 `INetworkTransport`。

---

## 2. 二进制封包格式规范（Wire Framing）

所有消息采用小端序（Little-Endian）二进制编码（`BinaryWriter` / `BinaryReader`）：

### 2.1 通用包头（Packet Header — 8 Bytes）
```text
+-------------------+-------------------+-------------------+------------------------+
| Magic (2 Bytes)   | MsgType (1 Byte)  | SenderId (1 Byte) | PayloadLength (4 Bytes)|
| 0x54, 0x42 ("TB") | PacketType Enum   | 1..3              | Int32                  |
+-------------------+-------------------+-------------------+------------------------+
```
*(注：`LocalTcpLoopbackTransport` 与 `SteamP2PTransport` 均使用上述 8 字节头；`DirectFastPathManager` UDP 分片外层使用 `TBDP` 帧头封装分片序号、`MsgId`、`Flags` 与 SACK 位图，重组还原为同一 `PacketType` 载荷。)*

### 2.2 消息类型枚举 (`PacketType : byte`)

| 值 | 枚举名 | 通道 | 方向 | 说明 |
| :-: | :--- | :---: | :---: | :--- |
| `0x01` | `HandshakeRequest` | ReliableCommand | C -> H | 客机请求加入，携带玩家名与 `ModInfo.Version` |
| `0x02` | `HandshakeAccept` | ReliableCommand | H -> All | 房主分配 `PlayerId (1..3)`、同步成员列表（含 `IsReady`）并附带房主 `ModInfo.Version` |
| `0x06` | `LobbyProbe` | TCP Pre-Handshake | C -> H | 客机探测本地主机是否在线（触发新连接预握手通道，不加入成员列表） |
| `0x07` | `LobbyProbeAck` | TCP Pre-Handshake | H -> C | 返回协议版本、在线玩家数、上限、当前 `LobbyRoomPhase` 与房主名 |
| `0x08` | `LobbyRosterSync` | ReliableCommand | H -> All | 房主广播房间列表、准备状态、`LobbyRoomPhase`、生存平衡预设与本地 Chaos 参数 |
| `0x09` | `ClientReadyState` | ReliableCommand | C -> H | 客机切换准备 / 取消准备状态 |
| `0x0A` | `LobbyHostNotice` | ReliableCommand | H -> All | 房主操作通知（选图中 / 加载中 / `ReturnToLobby` 召回客机退回主菜单重开大厅 / 房主退房） |
| `0x0B` | `DirectEndpointExchange` | ReliableCommand | All <-> All | `DirectFastPath` 交换候选 IPv4/IPv6 端点与探测令牌 |
| `0x0C` | `DirectCutoverSync` | ReliableCommand | All <-> All | 双方确认直连通道验证完成并同步切换至 UDP Fast-Path |
| `0x0D` | `DirectFallbackBatch` | ReliableCommand / Ordered | All <-> All | 直连通道假死/断开时通过 Steam 可靠通道无损补传未确认消息批次 |
| `0x10` | `SnapshotBegin` | ReliableOrdered | H -> All | 宣告开始传输 `.zxsav` 快照（总字节数、分片数、`.zxcheck` 内容、初始随机种子、`SyncedEntityIdCounter` 等） |
| `0x11` | `SnapshotChunk` | ReliableOrdered | H -> All | 存档字节流分片（每片最大 `32,768` Bytes，按索引乱序缓冲组装） |
| `0x12` | `SnapshotLoadedAck` | ReliableCommand | C -> H | 客机完成读档并上报初始状态就绪 |
| `0x13` | `SessionStart` | ReliableCommand | H -> All | 全员就绪，对齐并进入 Lockstep 步进（携带起始 tick、seed、idCounter 与当前 `InputDelayTicks`） |
| `0x20` | `ClientInputReport` | ReliableCommand | C -> H | 客机上报本人在当前 Turn 提交的 `NetCommand[]` 列表，并捎带客机当前 `SimTick` |
| `0x21` | `TurnInputBroadcast` | ReliableCommand | H -> All | 房主汇总全员在 `TargetSimTick` 的指令批次并广播（含空心跳包） |
| `0x22` | `HostPauseKeepalive` | ReliableCommand | H -> All | 房主暂停或打开模态弹窗时周期保活心跳，区分于真实断线/失联 |
| `0x23` | `LevelEnded` | ReliableCommand | H -> All | 房主权威同步关卡胜利/战败结算（`won`, `endTick`），客机立即弹出 `游戏结束` 并抑制暂停误报 |
| `0x30` | `StateChecksumReport` | ReliableOrdered | All -> H | 每 60 逻辑帧（2 秒）上报 `StateHasher` v3 摘要、16 分桶哈希、客机自控单位反向对齐样本与 `SyncedIdCounter` |
| `0x31` | `DesyncDetectedAlert` | ReliableCommand | H -> All | 房主检测到状态不一致时下发告警并触发自动快照 `Auto-resync` |
| `0x32` | `PosDriftSyncChunk` | ReliableOrdered | H -> C | `async_align` 模式下房主分片下发动态实体（及必要静态实体）生命/位置校正批次 |
| `0x33` | `PosDriftStatsReport` | ReliableOrdered | C -> H | 客机回传本轮 `PosDriftSync` 应用计数、最大/均方根偏差及 `MissingOnHost`/`MissingOnClient` 统计 |
| `0x40` | `AllyCursorAndCamera` | UnreliableSequenced | All <-> All | 20Hz 广播本地鼠标世界坐标、视野矩形并捎带发送方当前 `SimTick` |
| `0x41` | `TacticalPing` | ReliableCommand | All <-> All | 小地图/地面战术信号打点 |
| `0x42` | `RttPing` | UnreliableSequenced | H -> All | 局内 `500ms` 周期下发 `Stopwatch` 时间戳与 `CurrentSmoothedRttMs` 探测包 |
| `0x43` | `RttPong` | UnreliableSequenced | C -> H | 客机回显 `RttPing` 并捎带实时 `SimTick`，供房主自适应调整 `InputDelayTicks (1..4)` 与反压流控 |

---

## 3. 局内操作指令集定义 (`NetCommand`)

`NetCommand` 显式携带 `ExecutorEntityIds`，回放侧严禁读取本地 `CSelectable`：

```csharp
public enum NetCommandType : byte
{
    None = 0,
    ExecuteCommand = 1,      // 通用命令: 攻击、训练、研发、拆除、修理、市场、集结点、下车、取出奖励单位等
    GroupMoveOrPatrol = 2,   // 群体移动/巡逻/散开
    PlaceBuilding = 3,       // 放置建筑（含 EntityBonusCreation 市长奖励建筑）
    CancelCurrentOrder = 4,  // 取消当前执行命令/建造
    CancelQueuedOrder = 5,   // 取消生产队列槽位
    SetPaused = 6,           // 切换会话暂停/恢复状态
    SetGameSpeed = 7,        // 修改游戏倍速 (0..20)
    MayorVoteOrSelect = 8    // 市长选择（按 MayorId 确定性应用）
}
```

### 二进制序列化顺序（`NetCommand.WriteTo` / `ReadFrom`）
1. `byte Type`、`byte PlayerId`、`int TargetSimTick`、`bool IsShiftQueued`
2. `int CommandId`、`string BuildingTypeName`（对于 `PlaceBuilding` 为建筑类名；对于 `EntityBonusCreation` / `ExtractFromUnitHolderSlot` 等槽位敏感指令亦携带目标实体类型名以防前移误触发）
3. `ushort ExecutorCount` + `ulong[ExecutorCount] ExecutorEntityIds`
4. `ulong TargetEntityId`、`PointF TargetPosition (float,float)`、`SizeF TargetSize (float,float)`、`PointF TargetOrigin (float,float)`
5. `Rectangle BuildingCellArea (4 × int)`、`bool IsBonusBuilding`、`bool InstantBuild`、`byte QueueSlotIndex`（兼作槽位敏感指令的 `NSlot`）
6. `bool TargetPausedState`、`byte MayorOptionIndex`、`bool IsMayorFinalDecision`、`float GameSpeed`、`string MayorId`
7. `uint TraceId`（端到端操作延迟采样追踪 ID，向下兼容旧流尾部读取）

---

## 4. 确定性状态哈希计算规范 (`StateHasher` v3)

`StateHasher.IncrementalEpochHasher`（`HashVersion = 3`）按 `entity.ID % 60` 将全图摘要分摊至连续 60 个 `SimTick` 计算（亦可调用 `ComputeDigest()` 一次性全量采样），输出 FNV-1a 64-bit 摘要（全量使用 `object` + `GameBridge` 反射，顺序无关）：
1. **全局标量**：`HashVersion (3)`、`SimTick`、`GameTimeMs`、五资源（`Gold`, `Wood`, `Stone`, `Iron`, `Oil`）以及反射扫描 `ZXLevelState` 全部 `Reserv/Worker/House/Food/Energy` 整数属性的 `ReservationChecksum`。
2. **实体遍历与过滤**：
   - 跳过 `Disposed` 实体；
   - 跳过高位本地 UI ID（`(entity.ID & 0x8000000000000000UL) != 0`，含 `VisualVfxIsolationPatch` 隔离的视口告警事件）；
   - 跳过纯视觉/非交互实体：裸 `DXVision.DXEntity`（`entity.GetType() == typeof(DXEntity)`，如受击飙血/弹花）与环境飞鸟 `Raven`。
3. **分项累加（顺序无关）**：
   - **投射物**（`IsProjectileName`）：计入 `ProjectileCount` 与 `ProjectileChecksum`；
   - **常规单位/建筑**：精确坐标量化 `Math.Round(pos * 100)` 累加至 `PosChecksum`（同时按 `0.5` 格步长计算粗粒 `CoarsePosChecksum`）；`Life` 累加至 `LifeChecksum`；动态僵尸写入 `AggroZombieCount` 与 `ZombieAggroChecksum`；非僵尸实体的 `CCommandable` 生产队列按 `(entity.ID, slot+1, commandId)` 累加至 `ProductionChecksum`；同步维护 16 组 `PosBuckets` / `LifeBuckets` / `EntityBuckets` 用于定位局部差异桶。
4. **双层哈希输出**：
   - 先 Mix `EntityCount`、`ProjectileCount`、`QueuedOrderCount`、`AggroZombieCount`、`LifeChecksum`、`ZombieAggroChecksum`、`ProjectileChecksum`、`ProductionChecksum`、`ReservationChecksum` 得到 **`HardHash`**（用于 `async_align` 区分结构/战斗/经济硬分叉）；
   - 再 Mix `PosChecksum` 得到 **`FullHash`**。
