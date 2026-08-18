# TextCascade Desktop

[English](README.md)

TextCascade Desktop 是 TextCascade 文本剪贴板同步服务的 Windows 桌面客户端。客户端使用 C# WinForms 开发，目标运行方式为 Windows 上的 framework-dependent .NET 10。

> **兼容性说明**：自 v2.0.0 起本客户端使用 `textcascade.v1` 协议，不再兼容原 ClipCascade（Spring/STOMP）服务端。如需与 ClipCascade 服务端同步，请使用 [v1.x 版本 Release](https://github.com/long45343/TextCascade-desktop/releases)。

## 功能特性

- 基于 `textcascade.v1` 协议同步文本剪贴板：`POST /api/v1/login`（JSON、原始密码经 TLS 上送）返回 Bearer token，随后以 `textcascade.v1` 子协议连接 `wss://{host}/api/v1/sync`，交换 `hello/welcome/clip/clip_ack/ping/pong/bye/error` JSON 消息。
- 端到端可选加密（服务端透明）：AES-256-GCM，密钥由 `username + "$" + password + "$" + salt`（默认 664937 轮 PBKDF2-HMAC-SHA256）派生，nonce 16 字节随机，FNV-1a 64 位小写十六进制 hash 用于双端去重。
- 契约退避序列自动重连：普通断开 1/2/5/10/30/60s，维护断开（`bye`/close 1001）温和 1/2/5/10s，收到 `welcome` 后重置；电源恢复或网络恢复时立即（1-2s 内）提前重连。
- 接收看门狗在 `heartbeatTimeoutSeconds + 10s` 无任何字节时中断连接；收到 `ping` 立即回复 `pong`。
- 协议版本门控：服务端 `protocolVersion` 高于客户端支持版本时拒绝建连并明确提示升级。
- token 过期恢复：有保存密码时静默重登（限流感知，退避至少 30s），无保存密码时停止服务并提示重新登录。
- 托盘菜单支持显示主窗口、重启服务和退出。
- “保存并重连”流程可在不注销的情况下更新服务器/会话设置。
- 启用“保存密码”后支持开机自动登录。
- 可选的 WebSocket 连接状态气泡通知。
- 可选的“信任所有证书”开关，用于自签证书部署。
- 支持通过当前用户的 Windows 启动项实现开机启动。
- 根据当前 Windows UI 语言自动显示英文或简体中文。
- 不依赖第三方 NuGet 包；客户端使用 WinForms、`HttpClient`、`ClientWebSocket`、`System.Text.Json` 和 Windows API。

## 环境要求

- Windows 10/11。
- 构建需要 .NET 10 SDK。
- 运行 framework-dependent 版本需要 .NET 10 Windows Desktop Runtime。
- 使用 `textcascade.v1` 协议的 TextCascade 服务器。

## 构建

```powershell
dotnet build .\TextCascadeSharp.csproj -c Release
```

## 发布

项目默认发布为单个 framework-dependent exe：

```powershell
dotnet publish .\TextCascadeSharp.csproj -c Release -o .\publish
```

发布目录中只有一个 `TextCascade.exe`（约 683 KB）。目标机器仍需安装 .NET 10 Windows Desktop Runtime。



从发布目录运行：

```powershell
.\publish\TextCascade.exe
```

## 用户数据位置

设置文件保存在当前 Windows 用户目录下：

```text
%APPDATA%\TextCascade\settings.json
```

设置文件可能包含服务器地址、用户名、Bearer token 与过期时刻、服务端会话参数（协议版本、文本大小上限、hello/心跳超时）、客户端 ID/名称、最近服务端版本、加密选项、大小限制，以及经 DPAPI 保护的敏感字段（保存密码、auth token、派生 AES 密钥）。客户端不会把剪贴板正文持久化到磁盘。

“开机启动”写入当前用户的注册表启动项：

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

value 名为 `TextCascade`。

## 安全说明

- 保存的密码、Bearer token 和派生 AES 密钥在写入磁盘前会使用 Windows DPAPI 以当前用户作用域加密。派生密钥会持久化，因此即使不保存原始密码，加密剪贴板仍可解密。
- 旧版明文设置文件会在下次加载时自动迁移为 DPAPI 加密值。
- 如果受保护凭据无法解密（例如把设置文件复制到其他用户或机器），客户端会清空该字段并提示重新登录，而不是崩溃。
- 如需清除全部本地客户端状态，退出程序后删除 `%APPDATA%\TextCascade\settings.json`。

## 项目结构

```text
assets/                  应用图标和托盘图标
src/App/                 WinForms UI 和托盘应用上下文
src/Core/                API 客户端、textcascade.v1 WebSocket 同步、加密、设置、开机启动
TextCascadeSharp.csproj  Windows 桌面客户端项目
```

## 致谢

TextCascade Desktop 开发过程中参考了 [Sathvik-Rao/ClipCascade](https://github.com/Sathvik-Rao/ClipCascade)。感谢 ClipCascade 项目提供的剪贴板同步设计与实现参考。

## 许可证

本项目基于 GNU General Public License v3.0 开源。完整许可证文本见 [LICENSE](LICENSE)。
