#!/usr/bin/env python3
"""调试登录流程，查看 cookie 的实际返回情况"""
import requests
import re
import hashlib
import sys

url = 'http://8.138.188.141:45343'
user = 'longclip'
pw = 'long03@alioth?'
sha3 = hashlib.sha3_512(pw.encode()).hexdigest()

s = requests.Session()

# 1) GET /login
r1 = s.get(url + '/login', timeout=8)
sys.stdout.write(f'GET /login: {r1.status_code}\n')
sys.stdout.write(f'Cookies after GET: {s.cookies.get_dict()}\n')
sys.stdout.flush()

# extract csrf
csrf = ''
for m in re.finditer(r'<input\b[^>]*>', r1.text, re.IGNORECASE):
    tag = m.group()
    if re.search(r'\bname\s*=\s*["\']_csrf["\']', tag, re.IGNORECASE):
        mv = re.search(r'\bvalue\s*=\s*["\'](.*?)["\']', tag, re.IGNORECASE)
        if mv:
            csrf = mv.group(1)
            break
sys.stdout.write(f'CSRF: {csrf[:30] if csrf else "NOT FOUND"}\n')
sys.stdout.flush()

# 2) POST /login - 不跟随重定向
r2 = s.post(url + '/login', data={'username': user, 'password': sha3, '_csrf': csrf}, allow_redirects=False, timeout=8)
sys.stdout.write(f'\nPOST /login (no redirect): {r2.status_code}\n')
sys.stdout.write(f'Set-Cookie: {r2.headers.get("Set-Cookie", "NONE")}\n')
sys.stdout.write(f'Location: {r2.headers.get("Location", "NONE")}\n')
sys.stdout.write(f'Cookies after POST: {s.cookies.get_dict()}\n')
sys.stdout.flush()

# 跟随重定向
if r2.status_code in (301, 302, 303):
    loc = r2.headers.get('Location', '/')
    r3 = s.get(loc if loc.startswith('http') else url + loc, timeout=8)
    sys.stdout.write(f'\nAfter redirect to {loc}: {r3.status_code}\n')
    sys.stdout.write(f'Cookies after redirect: {s.cookies.get_dict()}\n')
    sys.stdout.write(f'Body starts: {r3.text[:200]}\n')
    sys.stdout.flush()
