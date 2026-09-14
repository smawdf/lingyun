#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""通过 GitHub Git Data API 把本地一个提交同步到远端分支。

为什么需要它：本机 github.com:443 时常连不上（git push 直接 Connection reset），
但 api.github.com 可达；而且本地历史里躺着一个 260 MB 的 灵云.exe 旧提交，
GitHub 的 pre-receive 钩子会直接拒绝推送整条历史。所以走 API 在远端现有提交之上
追加一个"当前状态"的快照提交。

用法（在仓库工作区里执行）：
    python cs/tools/sync_via_api.py <本地提交> <远端分支> [--repo owner/name]

凭据取自 `gh auth token`，不落盘、不进仓库。
"""
import base64
import json
import subprocess
import sys
import urllib.error
import urllib.request

API = "https://api.github.com"


def run(*args, **kw):
    return subprocess.run(args, check=True, capture_output=True, text=True, **kw).stdout.strip()


def git_bytes(*args):
    """原始字节输出：文件内容不能用 text=True 读，否则 CRLF 会被折成 LF、末尾换行会被 strip 掉。"""
    return subprocess.run(("git", "-c", "core.quotepath=false", *args),
                          check=True, capture_output=True).stdout


def git(*args):
    # core.quotepath=false：否则中文路径会被 git 输出成 "\347\201\265..."，
    # 直接拿去建 tree 会变成不存在的路径（GitRPC::BadObjectState）
    return run("git", "-c", "core.quotepath=false", *args)


def api(method, path, token, payload=None):
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    req = urllib.request.Request(API + path, data=data, method=method)
    req.add_header("Authorization", "Bearer " + token)
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("X-GitHub-Api-Version", "2022-11-28")
    req.add_header("User-Agent", "lingyun-sync")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            body = resp.read().decode("utf-8")
            return json.loads(body) if body else {}
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")
        raise SystemExit(f"HTTP {e.code} {method} {path}\n{detail}")


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if len(args) < 2:
        raise SystemExit(__doc__)
    commit, branch = args[0], args[1]
    repo = "smawdf/lingyun"
    if "--repo" in sys.argv:
        repo = sys.argv[sys.argv.index("--repo") + 1]

    token = run("gh", "auth", "token")

    # --base <sha>：以指定提交为基点（而不是远端当前 tip）。用于「重做刚才那次快照」——
    # 新提交仍挂在同一个父提交上，再 force 更新 ref，远端不会留下中间产物。
    base = None
    if "--base" in sys.argv:
        base = sys.argv[sys.argv.index("--base") + 1]

    # 变更清单直接和**远端真实树**逐文件比 blob 哈希，不和本地 origin/* 比：
    # 本机 github.com:443 时通时断，fetch 失败后本地引用会停在旧位置，会漏文件（踩过一次）。
    ref = api("GET", f"/repos/{repo}/git/ref/heads/{branch}", token)
    remote_head = ref["object"]["sha"]
    remote_tree = {
        e["path"]: e["sha"]
        for e in api("GET", f"/repos/{repo}/git/trees/{remote_head}?recursive=1", token)["tree"]
        if e["type"] == "blob"
    }
    local_tree = {}
    for line in git("ls-tree", "-r", commit).splitlines():
        meta, path = line.split("\t", 1)
        local_tree[path.strip('"')] = meta.split()[2]

    changed = [("M", path) for path, sha in local_tree.items() if remote_tree.get(path) != sha]
    changed += [("D", path) for path in remote_tree if path not in local_tree]
    if not changed:
        print("没有差异，远端已是最新")
        return

    if base:
        head_sha = git("rev-parse", base)
        force = True
    else:
        head_sha = remote_head
        force = False
    base_tree = api("GET", f"/repos/{repo}/git/commits/{head_sha}", token)["tree"]["sha"]
    print(f"基点 {head_sha[:8]}，基线 tree = {base_tree[:8]}{'（将强制更新 ref）' if force else ''}")

    tree = []
    for status, path in changed:
        if status == "D":
            tree.append({"path": path, "mode": "100644", "type": "blob", "sha": None})
            print(f"  删除 {path}")
            continue
        blob = git_bytes("show", f"{commit}:{path}")
        sha = api("POST", f"/repos/{repo}/git/blobs", token, {
            "content": base64.b64encode(blob).decode("ascii"),
            "encoding": "base64",
        })["sha"]
        tree.append({"path": path, "mode": "100644", "type": "blob", "sha": sha})
        print(f"  上传 {path} -> {sha[:8]}")

    new_tree = api("POST", f"/repos/{repo}/git/trees", token,
                   {"base_tree": base_tree, "tree": tree})["sha"]
    message = git("log", "-1", "--format=%B", commit)
    payload = {"message": message, "tree": new_tree, "parents": [head_sha]}
    new_commit = api("POST", f"/repos/{repo}/git/commits", token, payload)["sha"]
    api("PATCH", f"/repos/{repo}/git/refs/heads/{branch}", token,
        {"sha": new_commit, "force": force})
    print(f"完成：{branch} 更新为 {new_commit}")


if __name__ == "__main__":
    main()
