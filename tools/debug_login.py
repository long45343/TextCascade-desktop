#!/usr/bin/env python3
"""
调试 textcascade.v1 登录流程：POST /api/v1/login，查看响应字段。

用法：
  python3 debug_login.py <server_url> <username> <password>

示例：
  python3 debug_login.py https://your-server:8443 alice 'secret'

注意：实际部署地址与凭据由使用者自行传入，禁止写入本仓库。
"""
import json
import sys

import requests


def main():
    if len(sys.argv) != 4:
        print(f"用法: {sys.argv[0]} <server_url> <username> <password>")
        sys.exit(1)

    url = sys.argv[1].rstrip("/")
    username = sys.argv[2]
    password = sys.argv[3]

    resp = requests.post(
        f"{url}/api/v1/login",
        json={"username": username, "password": password},
        timeout=8,
    )
    print(f"POST /api/v1/login: HTTP {resp.status_code}")
    try:
        body = resp.json()
        print(json.dumps(body, indent=2, ensure_ascii=False))
        token = body.get("token", "")
        if resp.ok and token:
            print(f"\ntoken 前 30 字符: {token[:30]}...")
            print("WebSocket 入口（由 server_url 派生）:")
            ws = url.replace("https://", "wss://", 1).replace("http://", "ws://", 1)
            print(f"  {ws}/api/v1/sync  (子协议 textcascade.v1, Authorization: Bearer <token>)")
    except ValueError:
        print(f"body (非 JSON): {resp.text[:300]}")


if __name__ == "__main__":
    main()
