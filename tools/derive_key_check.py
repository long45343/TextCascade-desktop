#!/usr/bin/env python3
"""
跨端密钥一致性诊断工具（textcascade.v1 双端约定）。

在 Android/Windows 客户端与本脚本各运行一次，对比派生出的 AES-256 密钥
与 FNV-1a hash 是否相同。密钥不同则 GCM 解密必然失败（mac check failed）。

约定：
  - AES 密钥：PBKDF2-HMAC-SHA256，salt = UTF-8(username + "$" + password + "$" + salt)，
    迭代 hash_rounds（默认 664937），输出 32 字节（AES-256）
  - hash 字段：明文 UTF-8 字节的 FNV-1a 64 位，小写十六进制（16 字符）

用法：
  python3 derive_key_check.py <username> <password> <salt> <hash_rounds>

示例：
  python3 derive_key_check.py alice 'secret' '' 664937
"""
import base64
import hashlib
import sys

FNV_OFFSET_BASIS = 14695981039346656037
FNV_PRIME = 1099511628211


def derive_key(username: str, password: str, salt: str, rounds: int) -> bytes:
    """与 Windows 端 CryptoManager.DerivePasswordKey / Android 端约定一致"""
    return hashlib.pbkdf2_hmac(
        hash_name="sha256",
        password=password.encode("utf-8"),
        salt=(username + "$" + password + "$" + salt).encode("utf-8"),
        iterations=rounds,
        dklen=32,
    )


def fnv1a64_hex(text: str) -> str:
    """与 Windows 端 HashUtil.Fnv1A64Hex 一致：FNV-1a 64 位小写十六进制"""
    h = FNV_OFFSET_BASIS
    for byte in text.encode("utf-8"):
        h ^= byte
        h = (h * FNV_PRIME) & 0xFFFFFFFFFFFFFFFF
    return f"{h:016x}"


def main():
    if len(sys.argv) != 5:
        print(f"用法: {sys.argv[0]} <username> <password> <salt> <hash_rounds>")
        print(f"示例: {sys.argv[0]} alice 'secret' '' 664937")
        sys.exit(1)

    username = sys.argv[1]
    password = sys.argv[2]
    salt = sys.argv[3]
    rounds = int(sys.argv[4])

    print(f"username:      '{username}'")
    print(f"salt 后缀:     '{salt}'")
    print(f"hash_rounds:   {rounds}")
    print(f"salt 构造:     '{username}$<password>${salt}'")
    print()

    key = derive_key(username, password, salt, rounds)
    print("=== 派生出的 AES-256 密钥 ===")
    print(f"hex:     {key.hex()}")
    print(f"base64:  {base64.b64encode(key).decode()}")

    print("\n=== FNV-1a 64 位 hash（剪贴板去重字段） ===")
    print(f"空文本:      {fnv1a64_hex('')}")
    print(f"'a':         {fnv1a64_hex('a')}")
    print(f"'foobar':    {fnv1a64_hex('foobar')}")


if __name__ == "__main__":
    main()
