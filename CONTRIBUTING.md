# Contributing

感谢你对 TextCascade Desktop 的兴趣。本项目是一个开源（GPL-3.0）的 Windows 剪贴板同步客户端，欢迎提交 Issue 与 Pull Request。

## 环境要求

- Windows 10/11
- .NET 10 SDK（`net10.0-windows`）
- 一个可测试的 TextCascade 服务端（可选，运行端到端测试时需要）

## 构建

```powershell
dotnet build .\TextCascadeSharp.csproj -c Release --warnaserror
```

## 测试

```powershell
dotnet test .\tests\TextCascadeSharp.Tests\TextCascadeSharp.Tests.csproj -c Release
```

## 提 Issue

- 说明复现步骤、预期行为与实际行为。
- 若涉及安全（凭据、加密、证书校验），请优先以私密方式反馈，避免公开泄露。

## 提 PR

1. 从最新的 `v2` 分支开工，尽量保持改动聚焦。
2. 为新行为补充或更新测试；`dotnet build --warnaserror` 必须零警告。
3. 提交信息用简洁的中文或英文说明改动原因，不追求模板化。
4. 合并前确认全部测试通过。
