# 锐评修复决策记录

## 问题 3：SettingsData.ServerUrl 默认值 typo (`localhosts` → `localhost`)

**文件**: `src/Core/Models.cs`，第 10 行
**问题**: 默认值 `"https://localhosts:8443"` 多了一个 `s`。

### 选项

**A. 只修 typo**
直接把 `localhosts` 改为 `localhost`。一行改动，零副作用。
- 优：最小改动，秒级完成，不影响任何功能
- 劣：只修显示问题，不改"默认值完全不可用"这个本质（用户不配服务器地址就用不了）

**B. 修 typo + 加空串合理默认行为**
修 typo 的同时让 `ServerUrl` 为空时 UI 层给出更明显的配置指引（比如显示"请配置服务器地址"而不是派生 `wss:///api/v1/sync` 这样的破碎地址）。
- 优：用户体验改善，空地址时不会显示奇怪的 WebSocket URL
- 劣：改动范围稍大，涉及 UI 层判断逻辑

**C. 不动**
默认值反正没人直接用，不影响任何运行时行为。
- 优：绝对安全
- 劣：可能被直接编译进 Release，有较真的用户看到会困惑

---

## 问题 4：手写 AES-GCM (`GcmCipher`) 的处理方案

**问题**: `src/Core/GcmCipher.cs` 手写了约 200 行 AES-GCM。.NET 10 的 `AesGcm` 已支持 `new AesGcm(key, nonceSizeInBytes: 16)`，无需手写。但替换需要保证与现有密文互通。

### 选项

**A. 替换为内置 `AesGcm` + 迁移兼容层**
删掉 `GcmCipher`，从 `CryptoManager` 改用 `new AesGcm(key, 16)` 和 `new AesGcm(key, 12)` 分别处理 16/12 字节 nonce。保留 `GcmCipher.cs` 短期内作为回退路径。
- 优：消除手写密码学原语风险，复用经过 FIPS 验证的 .NET 内置实现
- 劣：改动较大（`CryptoManager` 加新实现 + 测试验证互解），需要确认 E2E 加密内容能互解

**B. 保留现状 + 加跨平台互通向量测试**
不动 `GcmCipher` 一行代码，只在测试项目中加一个已知输出向量的测试：取 Android 端 (`javax.crypto`) 对已知 key+plaintext 的加密输出，断言 `GcmCipher` 能解密出相同明文。
- 优：零生产代码风险，测试验证了兼容性，成本最低
- 劣：`GcmCipher` 仍在生产路径上，未来 .NET 升级时可能被安全审计问询

**C. 全部替换为内置 `AesGcm`，删除 `GcmCipher.cs`**
彻底替换，不留手写代码。
- 优：消除技术债务，删 200 行自维护代码，社区审查无顾虑
- 劣：需要完整的迁移验证（加密→解密→与 Android 端交叉验证），不可回退

---

## 问题 10：补充工程化文件

**问题**: 项目缺少 `CONTRIBUTING.md`、`.editorconfig`（CODE_OF_CONDUCT 可选）。

### 选项

**A. 只加 `.editorconfig`**
锁定 charset=utf-8、indent_style=space、indent_size=4、end_of_line=lf。最常用的工程约定文件。
- 优：一分钟加完，IDE 自动生效，防止缩进/换行争议
- 劣：不解决参与指引缺失问题

**B. `.editorconfig` + `CONTRIBUTING.md`**
在 A 的基础上加一个简短的 CONTRIBUTING（标题+构建命令+测试要求+PR 指引共 20-30 行）。
- 优：开源项目的基本配置完整，"如何参与"有了说明
- 劣：需要维护，内容过时比没有更糟

**C. 不加**
当前已开源且只有你一人维护，没有外部贡献者需要指引。
- 优：零维护成本
- 劣：如果有人想提 PR，没人告诉他要跑什么命令

---

## 问题 12：架构决策记录（ADR）

**问题**: 缺少 `docs/adr/` 目录，关键决策（协议迁移、手写 AES-GCM、DPAPI 保护）只散落在 CHANGELOG 中。

### 选项

**A. 写 3 个核心 ADR**
从 git log + CHANGELOG 提取三个核心决策写成 ADR：
1. STOMP → textcascade.v1
2. 手写 AES-GCM 的背景与现状
3. DPAPI 凭据保护
每个 2-3 段，格式：Context → Decision → Consequences。
- 优：新维护者一小时上手项目历史
- 劣：写完需要和实际代码保持一致，否则会误导

**B. 只写 1 个最关键 ADR（协议迁移）**
协议迁移是最重大的架构变更，影响理解整个 v2 代码结构。只写这个。
- 优：最小投入解决最大痛点
- 劣：其他决策仍需从 git log 和 CHANGELOG 反推

**C. 不写 ADR，在 CHANGELOG 基础上加强**
给 CHANGELOG 的条目加 `[ADR-link]` 标记，把解释写进 CHANGELOG 正文本身。
- 优：不引入新文件类型，维护负担最低
- 劣：CHANGELOG 通常面向用户而非维护者，混杂两类信息

## 问题 3：SettingsData.ServerUrl 默认值 typo（已选）

**选择**: D — 改为 https://your-server:8443
**理由**: 既修了 typo，又让默认值更明确地是一个占位符，不会有人误以为 localhost 就能用。

## 问题 4：手写 AES-GCM (GcmCipher)（已选）

**选择**: C — 全部替换为内置 AesGcm，删除 GcmCipher.cs
**理由**: .NET 10 的 AesGcm 已支持 16 字节 nonce，手写密码学原语不再必要。彻底替换消除技术债务。

## 问题 10：补充工程化文件（已选）

**选择**: B — .editorconfig + 简短的 CONTRIBUTING.md
**理由**: 基本工程化配置和参与指引一次性补齐，不多不少。

## 问题 12：架构决策记录 ADR（已选）

**选择**: B — 只写 1 个 ADR：STOMP → textcascade.v1 协议迁移
**理由**: 这是理解 v2 代码结构的关键决策；问题 4 选了 C（删除 GcmCipher），手写 AES-GCM 的 ADR 也不再需要了，刚好只写 1 个。

## ⚠ 冲突记录：问题 4 选择 C 需要修正

**现状**: 已按 C 改动（CryptoManager 改用内置 AesGcm，删除 GcmCipher.cs/GcmCipherTests.cs）后运行时发现：
- .NET 10 内置 AesGcm 仅支持 12 字节 nonce（AEAD_AES_256_GCM 规范限制）。
- 当前双端约定是生成 16 字节 nonce（Android/Windows 互通）。
- 继续 C 会破坏 16 字节 nonce 加密/解密，与 Android 端互通冲突。

**待用户重新选择**: 保留 B（恢复手写 GcmCipher 并加互通测试） vs 改为 12 字节 nonce（需同步改 Android 端，影响面大） vs 其他方案。

## 问题 4：手写 AES-GCM (GcmCipher)（重新选择）

**选择**: A — 恢复手写 GcmCipher，加 .NET AesGcm 往返测试
**理由**: 内置 AesGcm 只支持 12 字节 nonce，当前双端互通约定（Android/Windows）用 16 字节，C 方案不兼容。回到原始实现+与 NIST 认证 oracle 的互通验证，是最低风险路径。
