#!/usr/bin/env python3
"""
TextCascade textcascade.v1 WebSocket 监听工具。

用途：连接 TextCascade 服务器，打印 WebSocket 上实际传输的 JSON 消息，
用于验证加密模式下服务端（中间人）能看到什么。

用法：
  python3 ws_monitor.py <server_url> <username> <password> [--insecure]

示例：
  python3 ws_monitor.py https://your-server:8443 alice 'secret' --insecure

依赖：
  pip install requests websockets

注意：实际部署地址与凭据由使用者自行传入，禁止写入本仓库。
"""

import asyncio
import datetime
import json
import sys
import uuid

import requests
import websockets

SUB_PROTOCOL = "textcascade.v1"
CLIENT_ID = str(uuid.uuid4())


def login(server_url: str, username: str, password: str, insecure: bool) -> dict:
    """模拟 ClipApiClient.LoginAsync：POST /api/v1/login，返回响应 JSON"""
    resp = requests.post(
        f"{server_url.rstrip('/')}/api/v1/login",
        json={"username": username, "password": password},
        timeout=8,
        verify=not insecure,
    )
    if resp.status_code == 401:
        raise RuntimeError("登录失败：用户名或密码错误 (401 invalid_credentials)")
    if resp.status_code == 429:
        raise RuntimeError("登录被限流 (429 rate_limited)")
    resp.raise_for_status()
    body = resp.json()
    if body.get("protocolVersion", 1) > 1:
        raise RuntimeError(f"服务端协议版本 {body['protocolVersion']} 高于本工具支持的 1")
    return body


def build_ws_url(server_url: str) -> str:
    """与 ClipConfig.WebsocketUrlFromServerUrl 一致：/api/v1/sync"""
    url = server_url.rstrip("/")
    if url.startswith("https://"):
        ws = "wss://" + url[len("https://"):]
    elif url.startswith("http://"):
        ws = "ws://" + url[len("http://"):]
    else:
        raise ValueError(f"不支持的服务器 URL: {url}")
    return ws.rstrip("/") + "/api/v1/sync"


def build_hello(last_server_version: int) -> str:
    """紧凑 JSON hello（无 snapshot：监听工具不上送本地剪贴板）"""
    return json.dumps(
        {
            "type": "hello",
            "clientId": CLIENT_ID,
            "clientName": "ws-monitor",
            "lastServerVersion": last_server_version,
        },
        separators=(",", ":"),
    )


async def monitor(server_url: str, token: str, insecure: bool):
    ws_url = build_ws_url(server_url)
    print(f"[监听] 连接 {ws_url}（子协议 {SUB_PROTOCOL}）")
    print("[监听] 等待消息... (Ctrl+C 退出)\n")

    ssl_context = None
    if insecure and ws_url.startswith("wss://"):
        ssl_context = True  # websockets: True 表示不校验证书

    async with websockets.connect(
        ws_url,
        additional_headers={"Authorization": f"Bearer {token}"},
        subprotocols=[SUB_PROTOCOL],
        ping_interval=20,
        ping_timeout=10,
        ssl=ssl_context,
    ) as ws:
        await ws.send(build_hello(0))
        print("[发送] hello")

        async for message in ws:
            ts = datetime.datetime.now(datetime.timezone.utc).strftime("%H:%M:%S.%f")[:-3]
            print(f"\n[{ts}Z] 收到消息:")
            print("-" * 60)
            try:
                parsed = json.loads(message)
                print(json.dumps(parsed, indent=2, ensure_ascii=False))
                # 应用层心跳：立即回复 pong
                if parsed.get("type") == "ping":
                    pong = json.dumps(
                        {"type": "pong", "clientTimeUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")},
                        separators=(",", ":"),
                    )
                    await ws.send(pong)
                    print("[发送] pong")
            except json.JSONDecodeError:
                print(message)
            print("-" * 60)


def main():
    args = [a for a in sys.argv[1:] if a != "--insecure"]
    insecure = "--insecure" in sys.argv
    if len(args) != 3:
        print(f"用法: {sys.argv[0]} <server_url> <username> <password> [--insecure]")
        sys.exit(1)

    server_url, username, password = args
    print(f"[登录] {server_url} 用户: {username}")

    body = login(server_url, username, password, insecure)
    print(f"[登录] 成功，token 过期时刻: {body.get('expiresAtUtc')}")
    print(f"[登录] 服务端参数: protocolVersion={body.get('protocolVersion')} "
          f"maxTextBytes={body.get('maxTextBytes')} "
          f"helloTimeout={body.get('helloTimeoutSeconds')}s "
          f"heartbeatInterval={body.get('heartbeatIntervalSeconds')}s "
          f"heartbeatTimeout={body.get('heartbeatTimeoutSeconds')}s")

    try:
        asyncio.run(monitor(server_url, body["token"], insecure))
    except KeyboardInterrupt:
        print("\n[监听] 已停止")
    except Exception as e:
        print(f"\n[错误] {e}")


if __name__ == "__main__":
    main()
