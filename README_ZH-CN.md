# TextCascade Desktop

[English](README.md)

TextCascade Desktop 是 TextCascade/ClipCascade 的 Windows 桌面客户端，用于同步文本剪贴板。客户端使用 C# WinForms 开发，目标运行方式为 Windows 上的 framework-dependent .NET 10。

## 功能特性

- 通过现有 TextCascade/ClipCascade P2S 服务流程同步文本剪贴板。
- 托盘菜单支持显示主窗口、重启服务和退出。
- “保存并重连”流程可在不注销的情况下更新服务器/会话设置。
- 启用“保存密码”后支持开机自动登录。
- 可选的 WebSocket 连接状态气泡通知。
- 可选加密，兼容现有客户端的加密格式。
- 支持登录、注销、保存密码复用、本地剪贴板大小限制等设置。
- 支持通过当前用户的 Windows 启动项实现开机启动。
- 根据当前 Windows UI 语言自动显示英文或简体中文。
- 不依赖第三方 NuGet 包；客户端使用 WinForms、`HttpClient`、`ClientWebSocket`、`System.Text.Json` 和 Windows API。

## 环境要求

- Windows 10/11。
- 构建需要 .NET 10 SDK。
- 运行 framework-dependent 版本需要 .NET 10 Windows Desktop Runtime。
- ClipCascade P2S 模式服务器。

## 构建

```powershell
dotnet build .\TextCascadeSharp.csproj -c Release
```

## 发布

生成 framework-dependent 的发布目录：

```powershell
dotnet publish .\TextCascadeSharp.csproj -c Release --self-contained false -o .\publish
```

将发布目录中的 exe 和依赖文件打包成 zip：

```powershell
Compress-Archive -Path .\publish\* -DestinationPath .\TextCascade-publish.zip -Force
```

从发布目录运行：

```powershell
.\publish\TextCascade.exe
```

## 用户数据位置

设置文件保存在当前 Windows 用户目录下：

```text
%APPDATA%\TextCascade\settings.json
```

设置文件可能包含服务器地址、用户名、WebSocket 地址、会话 cookie、CSRF token、加密选项、大小限制和经 DPAPI 加密的保存密码。客户端不会把剪贴板正文持久化到磁盘。

“开机启动”写入当前用户的注册表启动项：

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

value 名为 `TextCascade`。

## 安全说明

- 保存的密码和会话凭据（cookie header、CSRF token）在写入磁盘前会使用 Windows DPAPI 以当前用户作用域加密。
- 旧版明文设置文件会在下次加载时自动迁移为 DPAPI 加密值。
- 如果受保护凭据无法解密（例如把设置文件复制到其他用户或机器），客户端会清空该字段并提示重新登录，而不是崩溃。
- 如需清除全部本地客户端状态，退出程序后删除 `%APPDATA%\TextCascade\settings.json`。

## 项目结构

```text
assets/                  应用图标和托盘图标
src/App/                 WinForms UI 和托盘应用上下文
src/Core/                API 客户端、WebSocket/STOMP 同步、加密、设置、开机启动
TextCascadeSharp.csproj  Windows 桌面客户端项目
```

## 致谢

TextCascade Desktop 开发过程中参考了 [Sathvik-Rao/ClipCascade](https://github.com/Sathvik-Rao/ClipCascade)。感谢 ClipCascade 项目提供的剪贴板同步设计与实现参考。

## 许可证

本项目基于 GNU General Public License v3.0 开源。完整许可证文本见 [LICENSE](LICENSE)。
