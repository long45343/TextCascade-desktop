# 0001: 从 STOMP 迁移到 textcascade.v1 协议

- 状态: 已接受
- 日期: 2026-08-18（v2.0.0）
- 决策者: 项目作者

## 背景

v1.x 客户端通过 Spring/STOMP 与原始 ClipCascade 服务端通信，依赖 CSRF/JSESSIONID Cookie 会话、订阅 `/app/cliptext` 与 `/user/queue/cliptext`。该协议绑定 Java/Spring 实现细节，无法支撑多端（Windows / Android / 服务端）co-located 部署的轻量化需求。

## 决策

采用独立的 `textcascade.v1` 协议：

- 登录改为 `POST /api/v1/login`（JSON，原始密码经 TLS 上送），返回 Bearer token 与服务端参数（`expiresAtUtc`、`protocolVersion`、`maxTextBytes`、心跳/超时参数等）。
- 同步通道改为 Bearer 认证的 WebSocket：`wss://{host}/api/v1/sync`，子协议 `textcascade.v1`。
- 消息类型固定为 `hello / welcome / clip / clip_ack / ping / pong / bye / error`，紧凑 JSON。
- 认证从 Cookie 会话改为 Bearer token + 过期自动重登。
- 移除 STOMP 层（`StompClient` / `StompFrame`）。

## 后果

- 好处：协议由本项目自持并跨平台落地（Windows / Android 复刻同一套帧格式与握手），依赖 Spring/STOMP 服务端成为历史；可做完整端到端契约测试（19 项断言）。
- 代价：旧版（v1.x）与新版协议不兼容，现有用户升级后需重新登录（新客户端不再保留旧的 cookie/CSRF 会话迁移）。
- 版本门控：登录响应 `protocolVersion` 高于客户端支持版本时拒绝建立 WebSocket，提示升级，避免静默协议错配。
