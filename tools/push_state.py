#!/usr/bin/env python3
"""push_state.py — 把本地仓库当前状态整体同步到 GitHub（REST，绕过 git push 被墙）。

为什么不用 `git diff <rev> HEAD`：REST 推出来的提交 sha 和本地不同（同树不同父），
本地可能落后于远端，按提交区间 diff 会漏文件、也会漏删除。这里改成「比状态」：
远端 tree 的 path→sha 表 vs 本地 `ls-tree -r -z`，只传不一致的、远端多出来的标 sha=null。

用法：
    python push_state.py --repo D:/path/to/repo --owner me --name my-repo [--branch main]

依赖：git、curl、Windows 凭据管理器里已存 github.com 的 PAT（x-access-token / ghp_…）。
"""

import argparse
import base64
import json
import os
import subprocess
import sys
import tempfile


def git(repo, *args):
    return subprocess.run(["git", "-C", repo, *args], capture_output=True).stdout


def get_token():
    out = subprocess.run(
        ["bash", "-lc", "printf 'protocol=https\\nhost=github.com\\n\\n' | git credential fill"],
        capture_output=True, text=True,
    ).stdout
    for line in out.splitlines():
        if line.startswith("password="):
            return line.split("=", 1)[1].strip()
    sys.exit("没有找到 github.com 的凭据（git credential fill 空）")


class Api:
    def __init__(self, token, owner, repo):
        self.base = f"https://api.github.com/repos/{owner}/{repo}"
        self.h = ["-H", f"Authorization: Bearer {token}",
                  "-H", "Accept: application/vnd.github+json",
                  "-H", "Content-Type: application/json"]
        self.tmp = os.path.join(tempfile.gettempdir(), "_push_state_body.json")

    def __call__(self, method, path, body=None, timeout=420):
        cmd = ["curl", "-s", "-X", method, *self.h, f"{self.base}/{path}"]
        if body is not None:
            # 关键：大 body 走临时文件，塞进 argv 会 WinError 206
            with open(self.tmp, "w", encoding="utf-8") as f:
                json.dump(body, f)
            cmd += ["--data-binary", "@" + self.tmp]
        out = subprocess.run(cmd, capture_output=True, timeout=timeout).stdout
        try:
            return json.loads(out)
        except Exception:
            sys.exit(f"API {method} {path} 返回异常: {out[:300]}")


def local_blobs(repo):
    """path -> (blob_sha, mode)，用 -z 避免中文路径被转义。"""
    out = {}
    for chunk in git(repo, "ls-tree", "-r", "-z", "HEAD").split(b"\0"):
        if not chunk:
            continue
        meta, path_b = chunk.split(b"\t", 1)
        mode, _typ, sha = meta.decode().split()
        out[path_b.decode("utf-8")] = (sha, "100755" if mode == "100755" else "100644")
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", required=True)
    ap.add_argument("--owner", required=True)
    ap.add_argument("--name", required=True)
    ap.add_argument("--branch", default="main")
    a = ap.parse_args()

    api = Api(get_token(), a.owner, a.name)
    tip = api("GET", f"git/ref/heads/{a.branch}")["object"]["sha"]
    rtree = api("GET", f"git/commits/{tip}")["tree"]["sha"]
    remote = {e["path"]: (e["sha"], e.get("mode", "100644"))
              for e in api("GET", f"git/trees/{rtree}?recursive=1")["tree"]
              if e["type"] == "blob"}
    local = local_blobs(a.repo)
    print(f"远端 {len(remote)} 个文件 / 本地 {len(local)} 个，tip {tip[:8]}")

    entries, uploaded = [], 0
    for path, (sha, mode) in local.items():
        if path in remote and remote[path][0] == sha:
            continue
        # 关键：取 git 规范化后的内容，别读工作区（CRLF 会让树对不上）
        blob = git(a.repo, "cat-file", "blob", sha)
        b = api("POST", "git/blobs", {"content": base64.b64encode(blob).decode(),
                                       "encoding": "base64"})
        entries.append({"path": path, "mode": mode, "type": "blob", "sha": b["sha"]})
        uploaded += 1
        print(f"  ↑ {path}")

    deleted = 0
    for path in remote:
        if path not in local:
            entries.append({"path": path, "mode": remote[path][1], "type": "blob", "sha": None})
            deleted += 1
            print(f"  ✗ {path}")

    if not entries:
        print("已经是同步状态")
        return

    tree = api("POST", "git/trees", {"base_tree": rtree, "tree": entries})
    want = git(a.repo, "rev-parse", "HEAD^{tree}").decode().strip()
    print(f"树一致: {tree['sha'] == want} ({tree['sha'][:8]} vs {want[:8]})")
    if tree["sha"] != want:
        print("警告：树仍不一致，检查 .gitattributes 与 cat-file 取内容那条路径")

    msg = git(a.repo, "log", "-1", "--pretty=%s").decode().strip()
    commit = api("POST", "git/commits", {"message": msg, "tree": tree["sha"], "parents": [tip]})
    ref = api("PATCH", f"git/refs/heads/{a.branch}", {"sha": commit["sha"], "force": False})
    ok = ref["object"]["sha"] == commit["sha"]
    print(f"上传 {uploaded} / 删除 {deleted} → {commit['sha'][:8]}，ref 校验 {ok}")
    if not ok:
        sys.exit("ref 更新校验失败")


if __name__ == "__main__":
    main()
