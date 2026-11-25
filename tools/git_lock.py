#!/usr/bin/env python3
"""Unity Git Lock CLI and optional Git hooks. Requires Python 3.10+."""
import argparse
import ipaddress
import json
import os
import re
import subprocess
import sys
from urllib.error import HTTPError
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def git(*args):
    return subprocess.check_output(["git", *args], stderr=subprocess.PIPE)


def git_text(*args):
    return git(*args).decode("utf-8").rstrip("\r\n")


def require_env(name):
    value = os.environ.get(name, "")
    if not value:
        raise RuntimeError("Missing environment variable " + name)
    return value


def endpoint():
    value = require_env("UNITY_GIT_LOCK_URL").rstrip("/")
    uri = urlsplit(value)
    host = uri.hostname
    local = host == "localhost"
    if host:
        try:
            local = local or ipaddress.ip_address(host).is_loopback
        except ValueError:
            pass
    if (not host or uri.username is not None or uri.password is not None
            or uri.query or uri.fragment
            or not (uri.scheme == "https" or (uri.scheme == "http" and local))):
        raise RuntimeError("HTTPS is required except for loopback HTTP")
    return value


def api(operation, body=None):
    repository = require_env("UNITY_GIT_LOCK_REPOSITORY")
    data = json.dumps({"repository": repository, **(body or {})}).encode()
    req = Request(endpoint() + "/v1/locks/" + operation, data=data,
                  headers={"Authorization": "Bearer " + require_env("UNITY_GIT_LOCK_TOKEN"),
                           "Content-Type": "application/json"})
    try:
        with build_opener(NoRedirect).open(req, timeout=15) as res:
            result = json.load(res)
    except HTTPError as error:
        try:
            detail = json.loads(error.read(8192)).get("error", "")
        except Exception:
            detail = ""
        raise RuntimeError(f"HTTP {error.code}: {detail}") from None
    if result.get("repository") != repository:
        raise RuntimeError("Unexpected repository in lock server response")
    return result


def parse_paths(data):
    return {path.decode("utf-8") for path in data.split(b"\0") if path}


def staged_paths():
    return parse_paths(git("diff", "--cached", "--name-only", "--no-renames", "-z", "--"))


def pushed_paths(remote, lines):
    """Conservative path union: newly introduced commits plus existing-ref net diffs."""
    paths = set()
    commits = set()
    for line in lines:
        if not line.strip():
            continue
        local_ref, local_sha, remote_ref, remote_sha = line.split()
        if not remote_ref.startswith("refs/heads/"):
            continue  # only branch updates
        for sha in (local_sha, remote_sha):
            if not re.fullmatch(r"(?:[0-9a-f]{40}|[0-9a-f]{64})", sha):
                raise RuntimeError("Invalid pre-push object ID")
        if not local_sha.strip("0"):
            continue  # branch deletion
        if remote_sha.strip("0"):
            # Also catches file changes caused by non-fast-forward updates.
            paths.update(parse_paths(git("diff", "--name-only", "--no-renames", "-z", remote_sha, local_sha, "--")))
            commits.update(git_text("rev-list", local_sha, "^" + remote_sha).splitlines())
        else:
            # New branch: commits not in locally known remote-tracking branches.
            commits.update(git_text("rev-list", local_sha, "--not", "--remotes=" + remote).splitlines())
    for sha in commits:
        paths.update(parse_paths(git("diff-tree", "--root", "-m", "--no-commit-id", "-r",
                                     "--name-only", "--no-renames", "-z", sha, "--")))
    return paths


def check_paths(paths):
    paths = sorted(set(paths))
    conflicts = {}
    for i in range(0, len(paths), 100):
        result = api("check", {"paths": paths[i:i + 100]})
        for lock in result["conflicts"]:
            conflicts[lock["id"]] = lock
    for lock in conflicts.values():
        print(f'LOCKED: {lock["path"]} | {lock["owner"]} | {lock["branch"]} | {lock["commit"][:8]}',
              file=sys.stderr)
    if conflicts:
        return 1
    print(f"Git Lock: checked {len(paths)} path(s), no foreign locks.")
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("list")
    lock = sub.add_parser("lock")
    lock.add_argument("path", nargs="+", help="Repository-relative path(s), forward slashes")
    lock.add_argument("--folder", action="store_true", help="Treat all supplied paths as folders")
    lock.add_argument("--reason", default="")
    unlock = sub.add_parser("unlock")
    unlock.add_argument("id", nargs="+")
    unlock.add_argument("--force", action="store_true")
    check = sub.add_parser("check")
    check.add_argument("path", nargs="+")
    sub.add_parser("check-staged")
    pre = sub.add_parser("pre-push")
    pre.add_argument("remote")
    pre.add_argument("url", nargs="?")
    args = parser.parse_args()
    try:
        if args.command in ("lock", "check-staged", "pre-push"):
            os.chdir(git_text("rev-parse", "--show-toplevel"))
        if args.command == "list":
            result = api("list")
        elif args.command == "lock":
            commit = git_text("rev-parse", "--verify", "HEAD")
            try:
                branch = git_text("symbolic-ref", "--quiet", "--short", "HEAD")
            except subprocess.CalledProcessError as e:
                if e.returncode != 1:
                    raise
                branch = "(detached HEAD)"
            if git_text("rev-parse", "--verify", "HEAD") != commit:
                raise RuntimeError("Git HEAD changed; retry")
            result = api("acquire", {"targets": [{"path": p, "folder": args.folder} for p in args.path],
                                      "reason": args.reason, "branch": branch, "commit": commit})
        elif args.command == "unlock":
            result = api("release", {"ids": args.id, "force": args.force})
        elif args.command == "check":
            return check_paths(args.path)
        elif args.command == "check-staged":
            return check_paths(staged_paths())
        elif args.command == "pre-push":
            return check_paths(pushed_paths(args.remote, sys.stdin))
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    except (OSError, RuntimeError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        print(f"Git Lock: {error}. If Git objects are missing, fetch and retry.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
