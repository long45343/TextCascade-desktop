# TextCascade Desktop

[简体中文](README_ZH-CN.md)

TextCascade Desktop is a lightweight Windows desktop client for TextCascade/ClipCascade text clipboard synchronization. It is built with C# WinForms and targets framework-dependent .NET 10 on Windows.

## Features

- Text clipboard synchronization through the existing TextCascade/ClipCascade P2S server flow.
- System tray app with show, start service, stop service, and exit actions.
- Optional encryption compatible with the existing clients.
- Login, logout, saved-password hash reuse, and local clipboard size limit settings.
- Start with Windows support through the current user's Windows startup registry entry.
- English and Simplified Chinese UI text selected from the current Windows UI culture.
- No third-party NuGet packages; the client uses WinForms, `HttpClient`, `ClientWebSocket`, `System.Text.Json`, and Windows APIs.

## Requirements

- Windows 10/11.
- .NET 10 SDK for building.
- .NET 10 Windows Desktop Runtime for running the framework-dependent build.
- ClipCascade server in P2S mode.

## Build

```powershell
dotnet build .\TextCascadeSharp.csproj -c Release
```

## Publish

Create a framework-dependent publish folder:

```powershell
dotnet publish .\TextCascadeSharp.csproj -c Release --self-contained false -o .\publish
```

Package the published executable and support files as a zip:

```powershell
Compress-Archive -Path .\publish\* -DestinationPath .\TextCascade-publish.zip -Force
```

Run the app from the published folder:

```powershell
.\publish\TextCascade.exe
```

## User Data

Settings are stored under the current Windows user:

```text
%APPDATA%\TextCascade\settings.json
```

The settings file may contain server URL, username, WebSocket URL, session cookie header, CSRF token, encryption options, size limits, and password hashes. Clipboard text content is not persisted to disk by the client.

The Start with Windows option is stored in:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

with the value name `TextCascade`.

## Security Notes

- Plaintext passwords are not stored.
- When Save Password is enabled, the client stores a password hash for reuse.
- While logged in, session data such as cookie header and CSRF token may be stored so the app can reconnect.
- If you need to clear all local client state, exit the app and delete `%APPDATA%\TextCascade\settings.json`.

## Project Layout

```text
assets/                  App and tray icons
src/App/                 WinForms UI and tray application context
src/Core/                API client, WebSocket/STOMP sync, crypto, settings, startup
TextCascadeSharp.csproj  Main Windows desktop project
```

## Credits

TextCascade Desktop was developed with reference to [Sathvik-Rao/ClipCascade](https://github.com/Sathvik-Rao/ClipCascade). Thanks to the ClipCascade project for the original clipboard sync design and implementation reference.

## License

This project is open source under the GNU General Public License v3.0. See [LICENSE](LICENSE) for the full license text.
