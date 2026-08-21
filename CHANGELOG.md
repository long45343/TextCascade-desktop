## [2.1.5] - 2026-08-22

### 性能优化

- AES-GCM GHASH 乘法硬件加速：在支持 x86 PCLMULQDQ / SSE2 的 CPU 上通过硬件无进位乘法与标准多项式模归约加速 GF(2^128) 乘法运算，其他平台自动平滑降级为软件逐位模拟算法。
- GCTR 块加解密内存分配优化：在 CTR 模式多块处理循环中复用固定 16 字节缓冲区，将循环内堆内存分配由 N 次降至 1 次。

### 安全加固

- 自签证书 SHA-256 指纹固定校验（Certificate Pinning）：配置项新增 server_certificate_thumbprint，在开启自签证书支持时支持校验指定的证书指纹；若填入指纹则精确匹配，杜绝中间人攻击。
- UI 交互联动：主窗口中“证书 SHA-256 指纹”输入框仅在勾选“信任所有证书”复选框时开放编辑。

### 代码结构与重构

- 主窗体分部类拆分：将 MainForm.cs 中的控件实例化、排版辅助函数与栅格布局代码解耦至 MainForm.Designer.cs 分部类，主文件专注处理事件响应与状态刷新。
- 补齐 GHASH 硬件/软件一致性测试、证书指纹规范化与配置持久化测试（测试用例增至 197 个，全量通过）。
- 版本号升至 2.1.5.0。
## [2.1.0] - 2026-08-22

### 修复

- 默认服务器地址 typo：localhosts → your-server（Models.cs/SettingsStore.cs/测试/配置文件中的全部副本）。

### 工程化

- 新增 .editorconfig（UTF-8、4 空格缩进、LF 换行）。
- 新增 CONTRIBUTING.md（构建/测试指引、PR 流程）。
- 新增 docs/adr/0001-stomp-to-textcascade-v1.md（协议迁移架构决策记录）。
- 新增 specs/CHOICES.md（锐评修复决策记录）。
- 版本号升至 2.1.0.0。

## [2.0.0] - 2026-08-18

### 破坏性变更（协议整体迁移到 TextCascade `textcascade.v1`）

- 登录改为 `POST /api/v1/login`（JSON：`username`/`password` 原始密码，经 TLS 上送），响应返回 `{token, expiresAtUtc, protocolVersion, maxTextBytes, helloTimeoutSeconds, heartbeatIntervalSeconds, heartbeatTimeoutSeconds}`；删除旧协议的 CSRF/JSESSIONID Cookie、`GET /server-mode`、`GET /max-size`、`GET /csrf-token`、`POST /logout` 与登录 SHA3-512 哈希上送。
- 同步通道改为 Bearer token 认证的 WebSocket（`wss://{host}/api/v1/sync`，子协议 `textcascade.v1`），消息为 `hello/welcome/clip/clip_ack/ping/pong/bye/error` 紧凑 JSON；删除 STOMP 层（`StompClient`/`StompFrame`）。
- 旧版本 settings.json 中的 cookie/CSRF 字段被忽略（不迁移会话），升级后需重新登录；旧版保存的密码仍可解密复用。

### 新增

- hello/welcome 握手：建连即发 hello（含 `clientId`/`clientName`/`lastServerVersion` 与本地剪贴板 snapshot）；welcome 携带服务端最新值，按 version/hash 去重后应用并推进版本游标。
- clip_ack 版本游标推进；接收 clip 按 `version > lastServerVersion` 且 hash 非本端发出才写剪贴板，并抑制下一次本地事件（回环防护）。
- 心跳：收到 `ping` 立即回复 `pong`（RFC3339 Z 时间）；接收看门狗在 `heartbeatTimeoutSeconds + 10s` 无任何字节时主动中断并重连（覆盖 server_busy 无声断开）。
- 重连策略对齐契约：普通断开 1/2/5/10/30/60s（之后固定 60s），`bye`/close 1001 温和 1/2/5/10s（之后固定 10s），收到 welcome 重置；电源恢复（`SystemEvents.PowerModeChanged=Resume`）与网络恢复（`NetworkChange.NetworkAvailabilityChanged`）时提前（1-2s 内）重连。
- 错误码处理表：`invalid_message`/`empty_text`/`hello_timeout`/`frame_too_large`/`server_busy` 记日志保持连接；`text_too_large` 状态提示；`rate_limited` 本地暂停发送约 1s；发送侧自检帧大小避免触发 1009。
- 协议版本门控：登录响应 `protocolVersion` 高于客户端支持的 1 时不建立 WebSocket，明确提示升级（显示服务端版本号）；子协议协商失败（HTTP 400）视为致命错误并停止自动重连。
- token 过期自动重登：WebSocket 升级 401/403 或 token 距过期不足时，有保存密码则静默重登（429 限流退避至少 30s），无保存密码则停止服务、清空会话并提示重新登录。
- 端到端加密对齐双端约定：PBKDF2 salt 改为 `username + "$" + password + "$" + salt`，nonce 默认 16 字节随机（解密兼容 12/16 字节）；派生密钥（`derived_key_b64`）DPAPI 持久化，支持“不保存原始密码仍可解密”。
- `hash` 字段改为 FNV-1a 64 位小写十六进制字符串；`clientId`（UUID v4）首次运行生成并持久化，`clientName` 默认机器名；`lastServerVersion` 持久化为无符号整数（初始 0），welcome/clip/clip_ack 推进游标时经回调写回 settings.json，重启后从持久值恢复（避免本设备早前发出的内容在重启后被重新应用）。
- 设置新增“信任所有证书”开关（自签部署），登录/HTTP 与 WebSocket 两侧生效；敏感字段保护集合改为 `saved_password`/`auth_token`/`derived_key_b64`（DPAPI）。
- 取消勾选“保存密码”时立即清除已存密码，但保留派生密钥与会话参数。

### 移除

- STOMP 1.1 帧编解码、`/app/cliptext`、`/user/queue/cliptext` 订阅、cookie 重试与旧断线恢复分桶退避。
- `tools/` 调试脚本中硬编码的真实服务器地址与凭据（spec 要求实际部署地址禁止写入本仓库）。

### 工具

- `tools/debug_login.py` 改为 `POST /api/v1/login` 调试（参数化，无硬编码凭据）。
- `tools/derive_key_check.py` 对齐新 salt 构造（`username$password$salt`）并新增 FNV-1a 64 位 hex 向量输出，输出与 C# 端一致。
- `tools/ws_monitor.py` 改为 textcascade.v1 监听（Bearer + 子协议 + hello/pong，支持 `--insecure` 自签场景）。
- `tools/e2e_verify.cs` 新增端到端验收脚本（file-based C# app，纯 BCL）：对已部署服务端完成 health/登录/401/双连接双向 clip 广播与 ACK 版本一致性/两轮 ping-pong/text_too_large 与 invalid_message 错误帧/无效 token 拒绝/close 终止共 19 项断言。实测 19/19 通过；实测发现的服务端问题见下。

### 服务端联调记录（2026-08-19 复验全部通过）

- 首轮 E2E（19/19）曾发现服务端 4 个问题：close 1000 握手无响应、乱序 pong 被静默 abort、`updatedAtUtc` 非 spec 整秒 Z 格式、CLI `user add` 无法在 stdin 重定向下使用。服务端修复后已逐项复验通过：close 握手完整、乱序 pong 回 `invalid_message` 且连接保持、时间字段整秒 Z、CLI 支持 `--password-stdin`。
- 客户端对上述问题原有的兼容处理（close 2s 超时 Abort 兜底、仅应答式 pong、时间解析兼容两种格式）保留，作为防御性实现。

### 工程

- 测试套件按新协议重写（145 个用例）：协议消息序列化/解析与契约样本镜像（紧凑格式、字段名、Z 结尾时间）、退避序列字面量、hash/version 去重、回显抑制、DPAPI 往返、LoginClient（假 HTTP：成功/401/429/版本不兼容）、引擎状态机（假传输：hello/welcome/clip/clip_ack/ping/bye/error、唤醒重连、会话失效、致命错误、关停）。
- 与服务端 spec（`docs/protocol/lightweight-text-server-spec.md` §4-§7）完成契约对齐审计：修正 `welcome.latest` 时间字段名为 `updatedAtUtc` 并解析 `fromClientId`；入站 clip 补充解析 `id`/`fromClientId`/`fromClientName`/`updatedAtUtc`；`clip_ack` 补充 `updatedAtUtc`；error 帧补充 `referenceId`；登录失败体错误码字段对齐为 `error`（客户端按 HTTP 状态码分支，行为不变）；契约样本逐字镜像服务端 §4-§7 JSON 示例（含无毫秒 Z 时间格式与默认参数 5/30/60）。
- 二轮对齐（§3.1/§5.2/§6.2）：登录响应缺字段时的兜底超时改为服务端默认 5/30/60；上行 RFC3339 时间统一整秒 Z 格式（消除 snapshot 选举对 `localModifiedAtUtc` 字符串比较的同秒歧义）；`clientName` 超长截断至 128 字符。
- 版本号升至 2.0.0。

### 修复（冒烟反馈）

- 主窗体"保存/登录"流程现在真正落盘表单修改：`SaveFormSettings` 原先检查 `_updating`，而该标志在 `SetBusy(true)` 期间恒为 true，导致保存/登录时全部表单变更（含"启用加密"取消勾选）被跳过并在流程尾部弹回旧值。此为旧版继承缺陷，影响所有保存类操作。

## [1.4.0.0] - 2026-08-17

### 修复

- STOMP 连接现在等待服务端 `CONNECTED` 帧并纳入同一 15 秒握手超时；服务端重启后若只建立 WebSocket 而协议会话未就绪，会主动断开并进入既有重连流程，不再悬挂。
- 两阶段断线恢复对齐 Android 版：断线后先用旧 cookie 额外重试 2 次 WebSocket，仍失败则停留在缓存凭据 HTTP 重登阶段并沿用同一断线退避持续重试，不再回退旧 cookie，也不再设置 5 分钟恢复预算。
- 登录失败分类对齐服务端语义：仅 HTTP 401/403 或响应包含 `bad credentials` 视为凭据被拒；HTTP 500/502/503 等按临时登录请求失败处理并继续自动恢复，不再误报“服务器拒绝登录”。
- 剪贴板监听器的创建与释放固定调度回 WinForms UI 线程，避免后台会话恢复路径释放 WinForms 组件时引发线程异常。
- 连接已关闭时的剪贴板发送会显式报错，不再被误判为发送成功并提交去重 hash，断线竞态中的内容可在下次继续同步。

## [1.3.1.1] - 2026-08-16
本项目的版本变更记录。

## [1.3.1.1] - 2026-08-16

### 工程

- 版本号升至 1.3.1.1。
- Release 工作流支持四段版本 tag（如 `v1.3.1.1`），并直接上传单个 exe 作为 GitHub Release 资产。

## [1.3.1] - 2026-08-16

### 修复

- 服务器重启后不再停在“会话过期”终态：已保存密码时会自动重新登录并恢复同步，登录失败按 2s/5s/10s/20s/30s 有界重试。
- 会话失效且未保存密码时先停止旧服务、清空失效会话语据，再提示重新登录；手动登录成功后服务会自动启动，不再需要额外点击“重启服务”。
- 会话恢复任务支持取消：手动登录、重启服务、注销、退出会使旧恢复任务失效，避免旧任务覆盖新会话。
- 托盘常驻启动时也会创建隐藏主窗体，`--startup` 模式下会话恢复不再因缺少 UI 线程调度目标抛异常。

### 工程

- 新增 GitHub Release 工作流：推送符合 `v<major>.<minor>.<patch>` 的 tag 时自动构建、测试、单文件发布并创建带 zip 资产的 GitHub Release。
- 版本号升至 1.3.1。

## [1.3.0] - 2026-08-12

### 新增

- 本地凭据保护：保存的密码、会话 cookie、CSRF token 在落盘前使用 Windows DPAPI（当前用户作用域）加密，并支持旧版明文设置自动迁移。
- 会话失效识别：WebSocket 握手返回 401/403 时不再无限重连，保存密码时自动重新登录一次，否则提示重新登录。
- 半开连接看门狗：连接后长时间收不到任何数据会主动断开并触发重连。
- WebSocket 状态通知：连接/断开时按设置显示托盘气泡，30 秒内同向事件节流。
- 单文件发布：默认发布为单个 framework-dependent `TextCascade.exe`（约 683 KB），不再生成 pdb 和 zip 包。
- CI：新增 GitHub Actions，自动执行 Release 构建（警告视为错误）和全部测试。
- 测试覆盖：新增引擎、STOMP 客户端、HTTP 客户端、DPAPI、日志等测试，总数从 80 增至 141。

### 改进

- 重连逻辑改为 single-flight：同一时间只有一个重连任务在途，错误与关闭事件连发时不再产生重复连接。
- 连接建立通过信号量串行化，避免 Start 与重连并发产生双会话。
- 握手增加 15 秒应用层超时，DNS/网络挂起不再无限卡住。
- 心跳语义修正：客户端按 `heart-beat: 0,20000` 协商只接收心跳、不再回送。
- 剪贴板写入改为最多 5 次短退避重试，最终失败再走 `cmd /c clip` 兜底，UI 不再长时间卡死。
- 登录时的 SHA3 与 PBKDF2 计算移入线程池，避免登录期间界面假死。
- 日志写失败静默丢弃，单文件超过 1 MB 自动滚动一代。
- WebSocket 消息和未终止 STOMP 帧增加缓冲上限，恶意/异常数据不会耗尽内存。
- 单个畸形 STOMP 帧只跳过并记录日志，不再拖垮整条连接。
- 主项目启用 `TreatWarningsAsErrors`，构建保持 0 警告。
- 测试项目升级 `Microsoft.NET.Test.Sdk`、`Microsoft.TestPlatform.TestHost` 与 `xunit`。

### 修复

- 全局未处理异常兜底：UI 线程、AppDomain 和未观察任务异常不再直接导致托盘程序退出。
- 停止流程中的取消/释放竞态不再抛出未观察异常。
- `content-length` 为负值或超大值时按实际字节数钳制，不再越界读取。
- 会话过期凭据解密失败时清空字段并提示重新登录，而不是崩溃或静默使用错误数据。

### 文档

- README 中英文版更新托盘菜单、保存并重连、开机自动登录、DPAPI 安全说明与单文件发布命令。

## [1.2.0] - 2026-08-12

### 新增

- 保存并重连流程：更新服务器/会话设置后自动重新连接。
- 开机自动登录：启用“保存密码”后随 Windows 启动并自动登录。
- 调试工具：`tools/` 下新增登录、密钥派生核对和 WebSocket 监控脚本。
- 单元测试：覆盖 GCM、哈希、JSON、设置持久化和 STOMP 帧解析，共 80 个用例。

### 修复

- 根据代码审查结果修复跨端兼容、线程模型和状态一致性相关问题。

