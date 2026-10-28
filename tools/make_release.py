#!/usr/bin/env python3
"""make_release.py — 建 GitHub Release 并上传资产（走 REST，绕开 push 被墙）。

用法：
    python tools/make_release.py --tag v1.51 \
        --notes docs/release-v1.51.md \
        --asset "Ubuntuflie/GrayRouter-1.51.zip" \
        --asset "Windowsfile/NetPanel.exe"

说明：
- token 从 Windows 凭据管理器取（host=github.com），不落盘、不打印。
- 资产路径相对仓库根目录；`--asset 本地路径:资产名` 可改名。
- 建失败 500 会重试；已存在同名 tag 的 Release 会直接复用。
"""
import argparse
import json
import os
import subprocess
import sys
import time
import urllib.parse

OWNER, REPO = "ark2077nbjks", "FJjx-Ubuntu-software-based-router"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def token():
    out = subprocess.run(
        ["bash", "-lc", "printf 'protocol=https\\nhost=github.com\\n\\n' | git credential fill"],
        capture_output=True, text=True).stdout
    for line in out.splitlines():
        if line.startswith("password="):
            return line.split("=", 1)[1].strip()
    sys.exit("没有找到 github.com 凭据")


class Api:
    def __init__(self, tok):
        self.h = ["-H", f"Authorization: Bearer {tok}",
                  "-H", "Accept: application/vnd.github+json",
                  "-H", "Content-Type: application/json"]
        self.tmp = os.path.join(ROOT, ".release_body.json")

    def __call__(self, method, url, body=None, timeout=900):
        cmd = ["curl", "-s", "-w", "\\n%{http_code}", "-X", method, *self.h]
        if body is not None:
            with open(self.tmp, "w", encoding="utf-8") as f:
                json.dump(body, f)
            cmd += ["--data-binary", "@" + self.tmp]
        out = subprocess.run(cmd + [url], capture_output=True, timeout=timeout).stdout.decode("utf-8", "replace")
        text, _, code = out.rpartition("\n")
        try:
            return int(code or 0), json.loads(text)
        except Exception:
            return int(code or 0), text


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tag", required=True)
    ap.add_argument("--notes", required=True, help="release 正文 markdown 文件")
    ap.add_argument("--asset", action="append", default=[], help="仓库内路径[:资产名]，可多次")
    ap.add_argument("--name", default="")
    a = ap.parse_args()

    body = open(os.path.join(ROOT, a.notes), encoding="utf-8").read() if not os.path.isabs(a.notes) else open(a.notes, encoding="utf-8").read()
    api = Api(token())
    base = f"https://api.github.com/repos/{OWNER}/{REPO}"

    code, rel = api("GET", f"{base}/releases/tags/{a.tag}")
    if code == 200:
        rid = rel["id"]
        print("Release 已存在，复用", rid)
    else:
        rid = None
        for i in range(4):
            code, rel = api("POST", f"{base}/releases", {
                "tag_name": a.tag, "name": a.name or a.tag, "body": body,
                "draft": False, "prerelease": False, "target_commitish": "main"})
            print("建 Release", i + 1, "HTTP", code)
            if code == 201:
                rid = rel["id"]
                break
            time.sleep(3)
        if rid is None:
            sys.exit("建 Release 失败")

    for spec in a.asset:
        path, _, name = spec.partition(":")
        full = os.path.join(ROOT, path)
        name = name or os.path.basename(path)
        size = os.path.getsize(full)
        url = f"https://uploads.github.com/repos/{OWNER}/{REPO}/releases/{rid}/assets?name={urllib.parse.quote(name)}"
        cmd = ["curl", "-s", "-w", "\\n%{http_code}", "-X", "POST", *api.h,
               "-H", "Content-Type: application/octet-stream", "--data-binary", "@" + full, url]
        out = subprocess.run(cmd, capture_output=True, timeout=1800).stdout.decode("utf-8", "replace")
        _, _, code = out.rpartition("\n")
        print(f"  {name}  {size // 1024} KB  → HTTP {code}")

    code, rel = api("GET", f"{base}/releases/tags/{a.tag}")
    for x in rel.get("assets", []):
        print("  资产:", x["name"], x["size"], x["state"], x.get("digest"))


if __name__ == "__main__":
    main()
