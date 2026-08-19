# TextCascade Desktop

[简体中文](README_ZH-CN.md)

TextCascade Desktop is a lightweight Windows desktop client for the TextCascade text clipboard synchronization service. It is built with C# WinForms and targets framework-dependent .NET 10 on Windows.

> **Compatibility note**: Starting with v2.0.0, this client speaks the `textcascade.v1` protocol and no longer works with the original ClipCascade (Spring/STOMP) server. If you need to sync with a ClipCascade server, use the [v1.x releases](https://github.com/long45343/TextCascade-desktop/releases) instead.

## Features

- Text clipboard synchronization over the `textcascade.v1` protocol: `POST /api/v1/login` (JSON, raw password over TLS) returns a Bearer token, then a WebSocket connection to `wss://{host}/api/v1/sync` with the `textcascade.v1` subprotocol exchanges `hello/welcome/clip/clip_ack/ping/pong/bye/error` JSON messages.
- Optional end-to-end encryption; when enabled, the server cannot read the plaintext.
- Automatic reconnection: 1/2/5/10/30/60s backoff for normal disconnects, gentle 1/2/5/10s after maintenance closes (`bye`/close 1001), reset on `welcome`; reconnects immediately (within 1-2s) on power resume or network recovery.
- Token-expiry recovery: silent re-login when a password is saved (rate-limit aware, backoff of at least 30s); otherwise the service stops and the user is asked to log in again.
- Optional trust-all-certificates switch for self-signed deployments.

## Requirements

- Windows 10/11.
- .NET 10 SDK for building.
- .NET 10 Windows Desktop Runtime.
- A [TextCascade server](https://github.com/long45343/textcascade-server).

## Build

```powershell
dotnet build .\TextCascadeSharp.csproj -c Release
```

## User Data

Settings are stored under the current Windows user profile:

```text
%APPDATA%\TextCascade\settings.json
```

## Security Notes

- Saved passwords, the Bearer token, and the derived AES key are encrypted with Windows DPAPI in the current-user scope before they are written to disk. The derived key is persisted, so encrypted clipboards remain decryptable even when the raw password is not saved.
- Legacy plaintext settings files are automatically migrated to DPAPI-protected values on the next load.
- If a protected credential cannot be decrypted (for example after copying the settings file to another user or machine), the client clears the field and asks for login again instead of crashing.
- To clear all local client state, exit the app and delete `%APPDATA%\TextCascade\settings.json`.

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
