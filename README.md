# TextCascade Desktop

[简体中文](README_ZH-CN.md)

TextCascade Desktop is a lightweight Windows desktop client for the TextCascade text clipboard synchronization service. It is built with C# WinForms and targets framework-dependent .NET 10 on Windows.

> **Compatibility note**: Starting with v2.0.0, this client speaks the `textcascade.v1` protocol and no longer works with the original ClipCascade (Spring/STOMP) server. If you need to sync with a ClipCascade server, use the [v1.x releases](https://github.com/long45343/TextCascade-desktop/releases) instead.

## Features

- Text clipboard synchronization over the `textcascade.v1` protocol: `POST /api/v1/login` (JSON + raw password over TLS) returns a Bearer token, then a WebSocket connection to `wss://{host}/api/v1/sync` with the `textcascade.v1` subprotocol exchanges `hello/welcome/clip/clip_ack/ping/pong/bye/error` JSON messages.
- End-to-end optional encryption invisible to the server: AES-256-GCM with a PBKDF2-HMAC-SHA256 key derived from `username + "$" + password + "$" + salt` (664937 rounds by default), 16-byte nonces, and the FNV-1a 64 hash (lowercase hex) for cross-device deduplication.
- Automatic reconnection with the contract backoff sequences (1/2/5/10/30/60s for normal disconnects, gentle 1/2/5/10s after `bye`/close 1001), reset on `welcome`, plus immediate reconnect on power resume or network recovery.
- Receive watchdog aborts silent connections after `heartbeatTimeoutSeconds + 10s`; `ping` is answered with `pong` immediately.
- Protocol version gate: the client refuses to connect when the server reports a `protocolVersion` higher than the supported version and asks the user to upgrade.
- Token-expiry recovery: silent re-login with the saved password (rate-limit aware, backoff >= 30s), or stop-and-prompt when no password is saved.
- System tray menu with show main window, restart service, and exit actions.
- Save and reconnect flow for updating server/session settings without logging out.
- Auto-login on startup when Save Password is enabled.
- Optional WebSocket connection status balloon notifications.
- Optional trust-all-certificates switch for self-signed deployments.
- Start with Windows support through the current user's Windows startup registry entry.
- English and Simplified Chinese UI text selected from the current Windows UI culture.
- No third-party NuGet packages; the client uses WinForms, `HttpClient`, `ClientWebSocket`, `System.Text.Json`, and Windows APIs.

## Requirements

- Windows 10/11.
- .NET 10 SDK for building.
- .NET 10 Windows Desktop Runtime for running the framework-dependent build.
- TextCascade server speaking the `textcascade.v1` protocol.

## Build

```powershell
dotnet build .\TextCascadeSharp.csproj -c Release
```

## Publish

The project publishes as a single framework-dependent executable:

```powershell
dotnet publish .\TextCascadeSharp.csproj -c Release -o .\publish
```

The output folder contains only `TextCascade.exe` (about 683 KB). The target machine still needs the .NET 10 Windows Desktop Runtime.



Run the app from the published folder:

```powershell
.\publish\TextCascade.exe
```

## User Data

Settings are stored under the current Windows user:

```text
%APPDATA%\TextCascade\settings.json
```

The settings file may contain server URL, username, WebSocket URL, session cookie header, CSRF token, encryption options, size limits, and a DPAPI-protected saved password. Clipboard text content is not persisted to disk by the client.

The Start with Windows option is stored in:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

with the value name `TextCascade`.

## Security Notes

- Saved passwords and session credentials (cookie header, CSRF token) are encrypted with Windows DPAPI in the current-user scope before they are written to disk.
- Legacy plaintext settings files are automatically migrated to DPAPI-protected values on the next load.
- If a protected credential cannot be decrypted (for example after copying the settings file to another user or machine), it is cleared and the app asks for login again instead of crashing.
- If you need to clear all local client state, exit the app and delete `%APPDATA%\TextCascade\settings.json`.

## Project Layout

```text
assets/                  App and tray icons
src/App/                 WinForms UI and tray application context
src/Core/                API client, textcascade.v1 WebSocket sync, crypto, settings, startup
TextCascadeSharp.csproj  Main Windows desktop project
```

## Credits

TextCascade Desktop was developed with reference to [Sathvik-Rao/ClipCascade](https://github.com/Sathvik-Rao/ClipCascade). Thanks to the ClipCascade project for the original clipboard sync design and implementation reference.

## License

This project is open source under the GNU General Public License v3.0. See [LICENSE](LICENSE) for the full license text.
