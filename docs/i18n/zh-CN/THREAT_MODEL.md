# Aether Protocol — 威胁模型

**已针对 HEAD `b8b3d22`（2026-05-06）进行审查。** 本文档描述 `aether-protocol` 的加密协议层所防御的内容、明确不在范围内的内容，以及安全主张所依赖的假设。本文档刻意保持诚实：能够阅读本文的攻击者应能列举出该协议**无法**阻止的每一种攻击，而不应被 README 中的营销措辞所误导。

配套文档为 [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md) §7（安全模型）。如两者存在分歧，`src/AetherNet.Security/` 中的实现为准。

---

## 1. 范围

### `aether-protocol` 的定位

这是一个类 Signal 协议的端到端加密消息库，以及一个网状网络原语（AODV 风格路由 + DTN 存储转发 + SOS 洪泛）。核心安全保证如下：

1. **机密性** — 消息体使用 AES-256-GCM 加密，密钥由双棘轮（Signal §5）派生的逐消息密钥保护。
2. **真实性** — 每个 `MeshPacket` 都携带对规范可签名数据缓冲区的 Ed25519 签名（PROTOCOL_SPEC §2.4）。
3. **重放保护** — 在 5 分钟的新鲜度窗口内，重复的 `(SourceUhid, PacketNonce)` 数据包将被丢弃。
4. **前向保密与后妥协保密** — 双棘轮在每次往返的 DH 公钥变更时重新生成密钥；攻击者即使获取某个会话密钥，也无法恢复过去或未来的消息。

### `aether-protocol` 的非定位

- **不是传输层安全替代品。** 客户端→服务器请使用 TLS。Aether 的 E2EE 适用于点对点网状流量；一旦数据包离开网状网络进入中心化后端，该后端的传输安全由宿主负责。
- **不是密钥管理系统。** 宿主通过 `IPreKeyStore`（或任何 `IKeyValueStore` 适配器）提供身份和预密钥材料的持久存储。硬件密钥库集成、TPM 认证、密钥托管恢复以及静态加密均由宿主负责。
- **不是认证系统。** Aether 认证的是"身份密钥 X 的持有者发送了这个数据包"。将身份密钥 X 映射为"人类 Alice"是宿主 UX 的责任（安全编号对比、带外指纹交换、先验信任链）。
- **不是隐私网络。** 线路上会暴露消息类型、数据包长度、源 UHID、目标 UHID、跳数和时间戳。它不是 Tor。

---

## 2. 已防御的攻击

### 2.1. 传输中的窃听

每个载荷均使用 AES-256-GCM 加密，密钥由双棘轮的对称链派生的逐消息密钥保护（Signal §5.1，使用 `0x01`/`0x02` 域分离的 HMAC-SHA256）。攻击者即使截获 Alice 和 Bob 之间的每个数据包，在没有其中一方的会话密钥的情况下，也无从恢复任何内容。

由 `tests/AetherNet.Security.Tests/SignalProtocolEncryptionTests.cs` 及跨语言 `fixtures/signal/expected/ratchet_step_basic.json` 向量验证。

### 2.2. 消息伪造

每个 Wave-2 数据包都携带对规范 `BuildSignableData(packet)` 缓冲区的 Ed25519 签名（`src/AetherNet.Security/Services/PacketSigningService.cs`，PROTOCOL_SPEC §2.4）。伪造的数据包在每个知道源方身份公钥的跳点处均会验证失败并被丢弃。路由回复包（RREP）由声称的目标节点签名——中间节点无法冒充目标节点，因为它们不持有目标节点的 Ed25519 私钥。

### 2.3. 重放攻击

`PacketSigningService.VerifyPacketAsync`：

- 拒绝 `TimestampMs` 与本地 UTC 偏差超过 5 分钟的数据包（`FreshnessWindowMs = 5 * 60 * 1000`）。
- 维护一个以 `(SourceUhid, PacketNonce)` 为键、TTL 为 5 分钟的内存去重映射。去重键在提交 `5bd52a9` 中从单独的 `nonce` 改为 `(source, nonce)`，以修复两种失效模式：跨发送方的随机数碰撞导致合法流量被丢弃，以及攻击者预先植入随机数以阻止合法发送方第一个数据包的预注册攻击。

计数器：`aethernet.nonces.replayed`、`aethernet.timestamps.stale`。

### 2.4. 前向保密（过去密钥泄露）

双棘轮在每个 DH 轮换步骤中派生新的发送链密钥（KDF_RK，在 `salt = current_root_key`、`info = "aether-ratchet-rk-v1"` 上使用 HKDF-SHA256，64 字节块按 32+32 分割为新的根密钥和链密钥 — `src/AetherNet.Security/Services/SignalProtocolService.cs`）。攻击者即使获取当前会话状态，也无法解密任何先前消息：每个先前的消息密钥在下一个棘轮步骤之前已被派生并清零（`CryptographicOperations.ZeroMemory`）。

### 2.5. 后妥协安全（未来密钥恢复）

当接收方在入站消息中观察到新的 `SenderEphemeralKeyX25519` 时，它会在接收时执行 DH 棘轮步骤（Signal §5.2）。攻击者缓存的会话状态在下一次往返时即告失效；攻击者在截取会话快照后离开，一旦合法双方交换了一轮消息，便无法再解密后续消息。

接收时的 DH 轮换步骤已在所有 8 种语言中落地——参见 `OPEN_ISSUES.md` 第 2 条中的家族范围提交列表。

### 2.6. 一次性预密钥重放

每个一次性预密钥（OPK）仅被使用一次。C# 参考实现附带一个 100 个 OPK 的池，采用 FIFO 发放、每次生成捆绑时懒补充，以及锁保护的单次消费（`SignalProtocolService.TopUpOpkPoolNoLock`，由 `tests/AetherNet.Core.Tests/PreKeyPoolTests.cs` 验证）。OPK 在响应方 X3DH 消费时立即被移除并清零，因此重放相同 OPK id 的 PreKey 消息无法建立会话。

其余 7 种语言仍每个会话发放一个 OPK——在顺序工作负载下功能正确，但在同时捆绑获取下存在并发风险。详见 `OPEN_ISSUES.md` §9。

### 2.7. 跨语言线路漂移

每个实现必须针对 `fixtures/` 下的固件语料库产生按字节相同的输出：

- `fixtures/expected/*.bin` — 10 个数据包序列化固件，CI 中包含 122 个跨语言字节相等性断言。
- `fixtures/signal/expected/x3dh_basic.json` — X3DH 数学运算（4 个 X25519 DH，在 `info = "aether-x3dh-root-v1"` 上使用 HKDF-SHA256 根密钥）。
- `fixtures/signal/expected/ratchet_step_basic.json`、`ratchet_step_three_iterations.json` — 对称棘轮 KDF。
- `fixtures/signal/expected/kdf_rk_basic.json` — DH 棘轮步骤。

任何语言的 HKDF info 字符串、字节序或填充发生漂移，都会导致其 `SignalFixtureTests` 构建失败。线路兼容的互操作性因此是构建时不变量，而非运行时的期望。

### 2.8. 静态-静态 DH 妥协（早期损坏的 X3DH）

2026-05-05 之前，C# 的 `KEY_EXCHANGE` 实现对两个 DH 操作均使用本地节点的身份密钥——这种静态-静态折叠破坏了 X3DH 的临时密钥前向保密属性。由提交 `07a93f5` 修复：真正的 X3DH 现在执行规范的 4 次 DH 运算：`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`，并使用全新的每会话临时密钥。详见 `OPEN_ISSUES.md` §1。

### 2.9. 路由循环和广播风暴

`RoutingService` 通过有界缓存（默认 10,000 条；`ProtocolConstants.RouteRequestDedupCacheSize`）对 RREQ 数据包按 `(originUhid, broadcastId)` 进行去重。TTL 在每跳递减，`Ttl == 0` 的数据包被丢弃。SOS 广播每源每小时限速 3 次，自发起源抑制防止节点重播自己的 SOS。

### 2.10. 通过耗尽 OPK 池的 DoS 攻击

OPK 池有界（`OpkPoolSize`，默认 100），当可用 OPK 数量降至 `SignalOptionsBag.MinAvailableOpks`（默认 10）以下时，Signal 健康检查报告 `Unhealthy`。宿主在 `aether-signal` 健康状态上配置告警。通过获取捆绑来耗尽 OPK 的攻击者无法超过配置的池大小；响应方的 X3DH 对于已发放的捆绑继续有效，并在下次捆绑生成时随补充而恢复。

### 2.11. 被动 BLE 设备追踪

记录稳定 BLE MAC 或 Service UUID 的被动扫描器可以跨时间和地点追踪某台设备。`BlePrivacy`（`src/AetherNet.Security/Privacy/BlePrivacy.cs`）封堵了标识符关联向量：广播的 Service UUID 每 15 分钟以 `HMAC-SHA256(rotation_key, window)`（PROTOCOL_SPEC §12.3）重新派生，且对等方以可解析私有地址（IRK + `ah`）而非固定 MAC 来寻址。没有轮换密钥或 IRK，两条广播无法被关联。固定于 `fixtures/bleprivacy/`。

**残余风险。** 这仅封堵了 BLE 标识符向量——它并**不**使 Aether 成为隐私网络（§1）。一旦数据包进入网状网络，明文的 `MeshPacket` 头部仍会暴露源/目标 UHID、类型、长度和时间戳（流量分析仍不在范围内，§3.3），且 RF 层指纹识别未得到解决。将轮换标识符发送到空中是宿主 BLE 栈的职责——本库仅负责派生它们。

### 2.12. 胁迫下的密钥泄露（duress）

物理持有设备并胁迫用户解锁的对手。`PanicWipe`（`src/AetherNet.Security/Privacy/PanicWipe.cs`）接受一个**胁迫 PIN**——以恒定时间与存储的 `SHA-256(pin)` 比对（无提前退出的时序泄露）——它会安全擦除密钥名清单中的每一个身份密钥（先以随机数覆写，再清零），使被交出的设备不持有任何可用身份。固定于 `fixtures/panicwipe/`。

**残余风险。** 尽力而为且明确有界：它**不**防御在擦除*之前*捕获的取证镜像、保留了密钥字节先前副本的闪存磨损均衡、强迫用户交出*真实* PIN 的对手，或在消息已被读取之后的胁迫。恒定时间比对缓解的是 PIN 猜测时序，而非完整的侧信道对手（§3.2）。

### 2.13. 唯一设备丢失（恢复）

这并非攻击者，而是丢失身份唯一副本所导致的可用性故障。恢复短语备份（`src/AetherNet.Security/Backup/`）将 32 字节的 Ed25519 身份种子编码为带校验和的 24 词 BIP-39 短语（PROTOCOL_SPEC §12.4），可在任何设备上恢复身份——没有服务器或托管方持有它。

**残余风险——一个新的失窃面。** 该短语**即是**身份：任何读到这 24 个词的人都能完全冒充该用户，且无法撤销。它用设备丢失风险换取了纸面机密风险。本库负责编码/解码短语并计算校验和；安全显示、存储以及可选的 BIP-39 口令均是宿主的责任。

### 2.14. 向多设备同步中注入流氓设备

试图将其控制的设备插入受害者同步集，或伪造同步记录的攻击者。`DeviceLink`（`src/AetherNet.Security/Sync/`）**由身份密钥进行 Ed25519 签名**（PROTOCOL_SPEC §12.1），因此只有身份持有者才能授权新设备——未签名或密钥错误的链接将验证失败。`SyncRecord` 载荷在 DTN/网状路径内以端到端加密方式传输，因此中继承载它们但无法读取。固定于 `fixtures/sync/`。

**残余风险。** 这认证的是*链接*本身，而非被链接设备日后的行为：合法链接*之后*再被攻陷的设备可以看到所有同步状态——同步没有逐记录的前向保密。协调采用对 `(created_at_ms, logical_clock, device_id, record_id)` 的最后写入者获胜，因此时钟被偏移的已链接设备可以左右哪条记录获胜；时钟完整性是宿主的关注点。签名字节的一致性带有 PROTOCOL_SPEC §12.1 中所述的 Swift/CryptoKit 例外。

---

## 3. 不在范围内的内容

以下是协议**无法**阻止的真实攻击。其中一些在未来版本中理论上可以缓解；另一些则从根本上属于宿主关注的问题。

### 3.1. 端点妥协

如果攻击者在 Alice 的设备上拥有 root 权限，他们可以从内存中读取她的身份密钥私有字节，并解密她持有的每个会话。该协议假设设备的进程内存是可信的。缓解措施（平台密钥库、SGX、硬件支持的密钥库）明确是宿主的责任——参见第 4 节。

### 3.2. 侧信道攻击

参考实现对棘轮公钥比较使用了 `CryptographicOperations.FixedTimeEquals`（`SignalProtocolService.ConstantTimeEquals`），但未专门针对以下内容进行加固：

- AES-GCM 中的时序侧信道（.NET BCL 的 `AesGcm` 在支持 AES-NI 的 CPU 上使用硬件加速；软件回退的时序未经审计）。
- 功耗分析侧信道（纯软件——无硬件对策）。
- 密钥派生路径上的缓存时序（通过 BCL 实现的 HKDF-SHA256）。

对被盗解锁设备的国家级实验室攻击是可行的。

### 3.3. 流量分析

线路格式暴露：

- 数据包**类型**（偏移 1 处的 1 字节——RREQ 与 Data 与 SOS 明文可见）。
- 数据包**长度**（载荷未填充）。
- **源和目标 UHID**（UTF-8，明文可见）。
- **时间戳**、**TTL** 和**优先级**。

填充、掩护流量和洋葱路由均未实现。能够被动观察 BLE / Wi-Fi 流量的对手可以构建联系人图谱和每次对话的时序档案，即使他们无法读取内容。这是已知的局限性；缓解措施需要线路格式变更，目前不在路线图上。

### 3.4. 量子攻击

X25519（RFC 7748）和 Ed25519（RFC 8032）在运行 Shor 算法的足够大的量子计算机面前均会被破解。该协议**不具备后量子安全性**。未来迁移到混合 Kyber + X25519 / Dilithium + Ed25519 方案是已知的关切，但尚未排期。如果密码学相关量子计算机（CRQC）在相关时间范围内出现，今天被对手以"先收割，后解密"方式录制的现有密文面临风险。

### 3.5. 大规模群组消息

`AetherNet.Security` 提供了 `IGroupKeyProvider` 接缝，但截至 HEAD，完整的 Signal Sender Keys 协议（Signal 使用的异步群组消息构造）**未**实现。今天需要群组消息的宿主回退到 N 个成对会话——功能可用，但每次群发的成本为 O(N)。PROTOCOL_SPEC §7 仅涵盖单接收方威胁。

### 3.6. 首次联系时的身份验证（TOFU）

Aether 认证的是"持有身份密钥 X 的对等方签署了此消息"。它**不**认证"身份密钥 X 实际属于用户期望交谈的人类 Alice"。在首次联系时，控制网络的主动中间人在第一次捆绑交换期间可以替换为自己的身份密钥，签署自己的捆绑，并在两个方向上透明地代理流量。

这是标准的 Signal "首次使用信任"（TOFU）弱点。规范缓解措施是带外安全编号/指纹对比（当面、通过单独信道、在预共享验证屏幕上）。该协议目前未公开用于安全编号派生的公共 API 接口；正在跟踪为差距（尚未列入 `OPEN_ISSUES.md`）——宿主 UX 不应假装默认已验证。

### 3.7. 底层传输的网络层攻击

信号干扰（BLE、Wi-Fi、NearLink）、射频层拒绝服务以及针对传输配对/绑定流程的攻击均不在范围内。传输（`ITransportService`）被视为不透明字节管道。拥有频谱控制权的干扰者会阻止 Aether 传递任何内容。

### 3.8. 超出去重窗口的路由攻击

尚未积累可靠性评分的短暂节点的 Sybil 洪泛攻击、不触发可靠性启发式的机会性中继丢弃攻击，以及保持在速率限制以下的资源耗尽攻击均未得到专门缓解。可靠性评分（PROTOCOL_SPEC §3.5）会降低已证明为恶意节点的优先级，但并非一个成熟的拜占庭容错路由协议。

---

## 4. 安全主张成立的假设

第 2 节中的防御措施以以下不变量为前提。如果其中任何一个被打破，相应的安全属性将丧失。

1. **身份密钥的持久性。** 宿主需要持久且安全地存储长期 Ed25519 + X25519 身份密钥对（例如，通过 `IPreKeyStore` 对接 `EncryptedKeyValueStore` 包装的 `FileSystemKeyValueStore`，或对接平台密钥库）。身份密钥丢失 = 完全账户妥协；私钥的持有者可以以原始对等方身份签署任何内容。

2. **CSPRNG 正确性。** 目标平台上的 `RandomNumberGenerator.GetBytes` 和 `RandomNumberGenerator.GetInt32` 产生密码学安全的输出。整个协议——临时密钥、AES-GCM 随机数、数据包随机数、OPK id——均依赖于此。在 BCL 随机源降级的平台上（某些嵌入式目标、损坏的 Linux 熵池），整个信任树将崩溃。

3. **系统时钟在 UTC ±5 分钟内。** 重放保护基于时间戳窗口。时钟严重错误的设备要么拒绝每个数据包（时钟过旧），要么无限期接受重放（时钟过新）。宿主应在应用启动时对可信时间源进行合理性检查。

4. **OPK 消费的原子性。** 当 `IPreKeyStore` 支持的 `ConsumeOneTimePreKeyAsync(id)` 与针对同一 id 的响应方 X3DH 操作并发运行时，消费必须原子地成功或失败。参考 C# 池在 `_preKeyLock` 下串行化消费；非事务性后端（例如，使用读-修改-写的简单文件存储）上的宿主提供的存储可能允许同一 OPK 被消费两次，从而破坏属性 2.6。`KeyValuePreKeyStore` 直接使用 `IKeyValueStore.RemoveAsync` 进行消费——前提是底层 KV 的删除操作是原子的。

5. **首次联系身份验证。** 对等方的身份公钥在第一次交换消息之前已通过带外方式验证（安全编号、指纹、可信目录），或者宿主接受 TOFU 风险，满足于在下次联系时检测密钥变更。否则，§3.6 是一个开放的中间人窗口。

6. **宿主进程内存不可被对手读取。** 参见 §3.1。

---

## 5. 已知弱点及缓解措施

### 5.1. 首次联系中间人攻击（TOFU）

**弱点：** 在第一次捆绑交换期间控制点对点链路的主动攻击者可以替换自己的捆绑并代理流量。
**缓解措施：** 宿主 UX 必须在将联系人视为已验证之前暴露安全编号/公钥指纹对比流程。用于安全编号派生的公共 API 接口尚未在 `AetherNet.Security` 中发布；正在跟踪为差距。

### 5.2. 签名预密钥轮换滞后

**弱点：** 在宿主调用 `RotateSignedPreKeyAsync` 之前，每次捆绑中提供的都是同一个 SPK。通过 §3.1 端点妥协获知 SPK 私钥的对手，可以针对自上次轮换以来的任何捕获捆绑运行 X3DH。
**缓解措施：** 每日调度 `RotateSignedPreKeyAsync`。默认的 `SignedPreKeyRotationOptions` 保留 3 个先前的 SPK，以便在最近轮换的密钥下签署的飞行中消息在轮换窗口内仍可解密。默认轮换间隔为 7 天——针对主动被攻击用户的采用者应缩短此间隔。

### 5.3. 无持久化的内存会话状态

**弱点：** 如果 `SignalProtocolService` 在没有 `sessionStore` 的情况下构建，进程崩溃或重启会丢失所有活动会话。前向保密是完整的（丢失的密钥无法恢复），但对等方的下一条消息将无法解密，因为接收链已消失。
**缓解措施：** 在任何生产部署中，将 `KeyValueSignalSessionStore` 对接持久的 `IKeyValueStore`。示例控制台演示为清晰起见使用了 `InMemoryDtnBundleStore` 等；生产宿主不得如此。

### 5.4. 压缩标志线路字节过渡窗口

**弱点：** `MessagingService` 有一个可选的 Brotli 压缩接缝，它会在明文信封前面无条件地添加一个标志字节。运行压缩前代码的对等方会将该标志字节误读为应用载荷的第一个字节。
**缓解措施：** 采用者将 `MessagingOptions.Compression.Enabled = false`，直到每个对等方都具有新的版本。该标志字节将由未来的能力协商握手来管控。参见 `CompressionOptions` 上的迁移说明。

### 5.5. C 语言差距

**弱点：** C 实现仅提供 X25519 + KDF_RK 原语以及固件验证器。它**未**实现完整的 `SignalProtocolService` API（X3DH 会话建立、OPK/SPK 生命周期、DH 棘轮集成）。在基于 C 的微控制器上部署 Aether 的宿主无法使用当前的 C 接口进行端到端加密流量。详见 `OPEN_ISSUES.md` §11。

### 5.6. OPK 池仅限 C#

**弱点：** 100 个 OPK 的池（含 FIFO 发放和原子消费，防御 2.6）是 C# 参考特性。Go、Python、TypeScript、Rust、Swift、Kotlin 实现每个会话仍仅发放一个 OPK。在同时发起者负载下，争用同一捆绑源的两个响应方都可能观察到同一个 OPK，导致 X3DH 产生会话状态不匹配。
**缓解措施：** 对于受影响的语言，在宿主侧串行化捆绑消费（每个对等方每次只有一个发起者）。详见 `OPEN_ISSUES.md` §9。

### 5.7. 非 C# 语言中的演示签名

**弱点：** 各语言的演示程序（Go、Python、TS、Rust、Swift、Kotlin、C）为可视化目的对完整序列化线路字节进行签名，而非规范的 `BuildSignableData` 缓冲区。这些语言中的库代码是正确的——只有演示走了捷径，但这对移植者而言令人困惑。
**缓解措施：** 详见 `OPEN_ISSUES.md` §10。以 C# 演示的第 3 步为规范流程。

---

## 6. 报告安全问题

请参见 [`SECURITY.md`](../SECURITY.md) 了解负责任披露政策。请发送电子邮件至 `security@thegeeknetwork.co.za`，并附上复现步骤；预计在 48 小时内收到确认，并在 7 天内收到初步评估。

根据第 3 节不在范围内的问题仍欢迎报告——我们宁愿知道我们未防御的内容，也不希望用户在生产环境中发现这一差距。
