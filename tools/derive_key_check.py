#!/usr/bin/env python3
"""
跨端密钥一致性诊断工具。

在 Python 客户端和 C# 客户端各运行一次，对比派生出的 AES-256 密钥是否相同。
如果密钥不同，GCM 解密必然失败（mac check failed）。

用法：
  python3 derive_key_check.py <username> <password> <salt> <hash_rounds>

示例：
  python3 derive_key_check.py longclip 'long03@alioth?' '' 664937
"""
import hashlib
import base64
import sys


def derive_key(username: str, password: str, salt: str, rounds: int) -> bytes:
    """与 ClipCascade Python 端 cipher_manager.py 的 hash_password 完全一致"""
    return hashlib.pbkdf2_hmac(
        hash_name="sha256",
        password=password.encode("utf-8"),
        salt=(username + password + salt).encode("utf-8"),
        iterations=rounds,
        dklen=32,
    )


def main():
    if len(sys.argv) != 5:
        print(f"用法: {sys.argv[0]} <username> <password> <salt> <hash_rounds>")
        print(f"示例: {sys.argv[0]} longclip 'long03@alioth?' '' 664937")
        sys.exit(1)

    username = sys.argv[1]
    password = sys.argv[2]
    salt = sys.argv[3]
    rounds = int(sys.argv[4])

    print(f"username:      '{username}'")
    print(f"password:      '{password}'")
    print(f"salt:          '{salt}'")
    print(f"hash_rounds:   {rounds}")
    print(f"salt 构造:     '{username}{password}{salt}'")
    print()

    key = derive_key(username, password, salt, rounds)

    print("=== 派生出的 AES-256 密钥 ===")
    print(f"hex:     {key.hex()}")
    print(f"base64:  {base64.b64encode(key).decode()}")
    print()

    # 同时计算 SHA3-512 hash（登录时发送的值）
    sha3 = hashlib.sha3_512(password.encode("utf-8")).hexdigest()
    print(f"SHA3-512(password) hex: {sha3}")


if __name__ == "__main__":
    main()
