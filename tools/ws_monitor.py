#!/usr/bin/env python3
"""
TextCascade/ClipCascade WebSocket 监听工具。

用途：连接自己的 ClipCascade 服务器，打印 WebSocket 上实际传输的 STOMP 帧，
用于验证 HTTP + 加密模式下中间人能看到什么。

用法：
  python3 ws_monitor.py <server_url> <username> <password>

示例：
  python3 ws_monitor.py http://8.138.188.141:45343 myuser mypass

依赖：
  pip install requests websockets
"""

import asyncio
import hashlib
import sys

import requests
import websockets

# STOMP 帧以 NULL 字符结尾
STOMP_NULL = "\x00"


def sha3_512_hex(text: str) -> str:
    """与 C# 端 CryptoManager.Sha3_512LowercaseHex 一致"""
    return hashlib.sha3_512(text.encode("utf-8")).hexdigest()


def login(server_url: str, username: str, password_sha3: str) -> str:
    """
    模拟 ClipApiClient.LoginAsync 的登录流程，返回 JSESSIONID cookie header。
    简化版：只做 GET /login 拿 CSRF + POST /login 提交表单。
    """
    import re

    url = server_url.rstrip("/")
    session = requests.Session()

    # 1) GET /login 拿 CSRF token
    resp = session.get(f"{url}/login", timeout=8)
    resp.raise_for_status()

    # 提取 <input ... name="_csrf" ... value="...">
    csrf = ""
    for match in re.finditer(r"<input\b[^>]*>", resp.text, re.IGNORECASE):
        tag = match.group()
        if re.search(r'\bname\s*=\s*["\']_csrf["\']', tag, re.IGNORECASE):
            m = re.search(r'\bvalue\s*=\s*["\'](.*?)["\']', tag, re.IGNORECASE)
            if m:
                csrf = m.group(1)
                break

    if not csrf:
        raise RuntimeError("未找到 CSRF token")

    # 2) POST /login
    resp = session.post(
        f"{url}/login",
        data={"username": username, "password": password_sha3, "_csrf": csrf},
        allow_redirects=True,
        timeout=8,
    )

    if "bad credentials" in resp.text.lower() or not resp.ok:
        raise RuntimeError(f"登录失败 (HTTP {resp.status_code})")

    # 3) 从 session 提取 JSESSIONID
    cookies = session.cookies
    session_id = ""
    for c in cookies:
        if c.name == "JSESSIONID":
            session_id = c.value
            break

    if not session_id:
        # 尝试从 cookie 字符串解析
        raw = resp.headers.get("Set-Cookie", "")
        m = re.search(r"JSESSIONID=([^;]+)", raw)
        if m:
            session_id = m.group(1)

    if not session_id:
        raise RuntimeError("登录成功但未获取到 JSESSIONID")

    return f"JSESSIONID={session_id}"


def build_ws_url(server_url: str) -> str:
    """与 ClipConfig.WebsocketUrlFromServerUrl 一致"""
    url = server_url.rstrip("/")
    if url.startswith("https://"):
        ws = "wss://" + url[len("https://"):]
    elif url.startswith("http://"):
        ws = "ws://" + url[len("http://"):]
    else:
        raise ValueError(f"不支持的服务器 URL: {url}")
    return ws.rstrip("/") + "/clipsocket"


def build_stomp_connect() -> str:
    """STOMP CONNECT 帧"""
    headers = [
        "CONNECT",
        f"host:clipsocket",
        "accept-version:1.0,1.1",
        "heart-beat:0,20000",
        "",
        "",
    ]
    return "\n".join(headers) + STOMP_NULL


def build_stomp_subscribe(destination: str, sub_id: str) -> str:
    """STOMP SUBSCRIBE 帧"""
    headers = [
        "SUBSCRIBE",
        f"id:{sub_id}",
        f"destination:{destination}",
        "",
        "",
    ]
    return "\n".join(headers) + STOMP_NULL


async def monitor(server_url: str, cookie_header: str):
    """连接 WebSocket 并持续打印收到的所有 STOMP 帧"""
    ws_url = build_ws_url(server_url)
    print(f"[监听] 连接 {ws_url}")
    print(f"[监听] Cookie: {cookie_header}")
    print(f"[监听] 等待消息... (Ctrl+C 退出)\n")

    async with websockets.connect(
        ws_url,
        additional_headers={"Cookie": cookie_header},
        ping_interval=20,
        ping_timeout=10,
    ) as ws:
        # 发送 STOMP CONNECT
        await ws.send(build_stomp_connect())
        print("[发送] CONNECT 帧")

        # 等待 CONNECTED
        response = await ws.recv()
        print(f"[收到] {response}\n")

        # 订阅用户队列
        await ws.send(build_stomp_subscribe("/user/queue/cliptext", "sub-1"))
        print("[发送] SUBSCRIBE /user/queue/cliptext")
        print("=" * 60)

        # 持续监听
        async for message in ws:
            print(f"\n[{asyncio.get_event_loop().time():.1f}] 收到 STOMP 帧:")
            print("-" * 60)
            print(message)
            print("-" * 60)


def main():
    if len(sys.argv) != 4:
        print(f"用法: {sys.argv[0]} <server_url> <username> <password>")
        print(f"示例: {sys.argv[0]} http://8.138.188.141:45343 myuser mypass")
        sys.exit(1)

    server_url = sys.argv[1]
    username = sys.argv[2]
    password = sys.argv[3]

    # 计算密码 SHA3-512 hash（与 C# 端一致）
    password_sha3 = sha3_512_hex(password)
    print(f"[登录] {server_url} 用户: {username}")

    cookie_header = login(server_url, username, password_sha3)
    print(f"[登录] 成功，获取到会话 Cookie")

    try:
        asyncio.run(monitor(server_url, cookie_header))
    except KeyboardInterrupt:
        print("\n[监听] 已停止")
    except Exception as e:
        print(f"\n[错误] {e}")


if __name__ == "__main__":
    main()
