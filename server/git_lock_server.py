"""Repository-wide advisory locks for ordinary Git files. Python 3.10+."""
import argparse
import hmac
import json
import logging
import re
import sqlite3
import uuid
from contextlib import contextmanager
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

MAX_BODY = 1024 * 1024
MAX_TARGETS = 128
MAX_CHECK = 1000
MAX_LOCKS = 5000
ASCII_LOWER = str.maketrans("ABCDEFGHIJKLMNOPQRSTUVWXYZ", "abcdefghijklmnopqrstuvwxyz")
LOG = logging.getLogger("git-lock")


class ApiError(Exception):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status


def text_field(value, name, limit, allow_empty=False):
    if (not isinstance(value, str) or len(value) > limit
            or (not value.strip() and not allow_empty)
            or any(ord(c) < 32 or ord(c) == 127 or 0xD800 <= ord(c) <= 0xDFFF for c in value)):
        raise ApiError(400, "Invalid " + name)
    return value


def repository(value):
    value = text_field(value, "repository", 200)
    if not re.fullmatch(r"[a-z0-9][a-z0-9._/-]*", value) or any(
        part in ("", ".", "..") for part in value.split("/")
    ):
        raise ApiError(400, "Use a lowercase repository key, e.g. github.com/team/game")
    return value


def normalize_path(value):
    value = text_field(value, "path", 1024)
    if "\\" in value or ":" in value or value.startswith("/") or any(
        part in ("", ".", "..") or part.translate(ASCII_LOWER) == ".git"
        for part in value.split("/")
    ):
        raise ApiError(400, "Expected a repository-relative Git path")
    return value


def key(path):
    # Unity assets and their sidecar metadata share a lock. Conservative ASCII
    # case folding prevents aliases on common Windows/macOS worktrees.
    value = path.translate(ASCII_LOWER)
    return value[:-5] if value.endswith(".meta") else value


def overlaps(a, af, b, bf):
    a, b = key(a), key(b)
    return a == b or (af and b.startswith(a + "/")) or (bf and a.startswith(b + "/"))


def array(body, name, maximum):
    value = body.get(name)
    if not isinstance(value, list) or not 1 <= len(value) <= maximum:
        raise ApiError(400, "Invalid " + name)
    return value


def flag(value, name):
    if not isinstance(value, bool):
        raise ApiError(400, "Invalid " + name)
    return value


class Store:
    def __init__(self, db_path):
        self.db_path = str(db_path)
        with self.connection() as db:
            db.execute("PRAGMA journal_mode=WAL")
            db.execute("""CREATE TABLE IF NOT EXISTS locks (
                id TEXT PRIMARY KEY, repository TEXT NOT NULL,
                path TEXT NOT NULL, folder INTEGER NOT NULL,
                owner TEXT NOT NULL, branch TEXT NOT NULL, commit_sha TEXT NOT NULL,
                locked_at TEXT NOT NULL, reason TEXT NOT NULL
            )""")
            db.execute("CREATE INDEX IF NOT EXISTS locks_repo ON locks(repository)")

    @contextmanager
    def connection(self):
        db = sqlite3.connect(self.db_path, timeout=10, isolation_level=None)
        db.row_factory = sqlite3.Row
        try:
            yield db
        finally:
            db.close()

    @contextmanager
    def transaction(self):
        with self.connection() as db:
            db.execute("BEGIN IMMEDIATE")
            try:
                yield db
                db.commit()
            except BaseException:
                db.rollback()
                raise

    @staticmethod
    def records(db, repo):
        return [
            {"id": r["id"], "path": r["path"], "folder": bool(r["folder"]),
             "owner": r["owner"], "branch": r["branch"], "commit": r["commit_sha"],
             "lockedAt": r["locked_at"], "reason": r["reason"]}
            for r in db.execute("SELECT * FROM locks WHERE repository=? ORDER BY path,id", (repo,))
        ]

    @staticmethod
    def snapshot(repo, user, records):
        return {"repository": repo, "viewer": user["name"], "admin": user["admin"], "locks": records}

    def list(self, repo, user):
        with self.connection() as db:
            return self.snapshot(repo, user, self.records(db, repo))

    def acquire(self, repo, user, body):
        branch = text_field(body.get("branch"), "branch", 256)
        commit = text_field(body.get("commit"), "commit", 64)
        if not re.fullmatch(r"(?:[0-9a-f]{40}|[0-9a-f]{64})", commit):
            raise ApiError(400, "A full Git commit SHA is required")
        reason = text_field(body.get("reason", ""), "reason", 512, allow_empty=True)
        targets = []
        for target in array(body, "targets", MAX_TARGETS):
            if not isinstance(target, dict):
                raise ApiError(400, "Invalid target")
            path = normalize_path(target.get("path"))
            folder = flag(target.get("folder"), "folder")
            if not key(path) or key(path).endswith("/"):
                raise ApiError(400, "Invalid asset sidecar path")
            if any(overlaps(path, folder, p, f) for p, f in targets):
                raise ApiError(400, "Overlapping targets in the same request")
            targets.append((path, folder))

        # All readers/writers use SQLite. BEGIN IMMEDIATE serializes competing
        # acquisitions across threads and server processes sharing this database.
        with self.transaction() as db:
            records = self.records(db, repo)
            pending = []
            for path, folder in targets:
                matches = [r for r in records if overlaps(path, folder, r["path"], r["folder"])]
                if matches:
                    if (len(matches) == 1 and matches[0]["owner"] == user["name"]
                            and key(matches[0]["path"]) == key(path)
                            and matches[0]["folder"] == folder):
                        continue  # exact own lock: idempotent, preserve original metadata
                    conflict = matches[0]
                    raise ApiError(409, f'{conflict["path"]} is already locked by {conflict["owner"]}')
                pending.append((path, folder))
            if len(records) + len(pending) > MAX_LOCKS:
                raise ApiError(409, "Repository lock limit reached")
            timestamp = datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")
            for path, folder in pending:
                db.execute("INSERT INTO locks VALUES (?,?,?,?,?,?,?,?,?)",
                           (str(uuid.uuid4()), repo, path, int(folder), user["name"],
                            branch, commit, timestamp, reason))
            return self.snapshot(repo, user, self.records(db, repo))

    def release(self, repo, user, body):
        ids = set(text_field(i, "lock ID", 64) for i in array(body, "ids", MAX_TARGETS))
        force = flag(body.get("force", False), "force")
        if force and not user["admin"]:
            raise ApiError(403, "Administrator required for force unlock")
        with self.transaction() as db:
            records = {r["id"]: r for r in self.records(db, repo)}
            for ident in ids:
                if ident not in records:
                    raise ApiError(404, "Lock no longer exists; refresh first")
                if records[ident]["owner"] != user["name"] and not force:
                    raise ApiError(403, "Only the owner can unlock this file")
            for ident in ids:
                db.execute("DELETE FROM locks WHERE repository=? AND id=?", (repo, ident))
            if force:
                LOG.warning("Force unlock by %s in %s: %s", user["name"], repo, sorted(ids))
            return self.snapshot(repo, user, self.records(db, repo))

    def check(self, repo, user, body):
        paths = [normalize_path(p) for p in array(body, "paths", MAX_CHECK)]
        with self.connection() as db:
            records = self.records(db, repo)
        conflicts = [r for r in records if r["owner"] != user["name"] and any(
            overlaps(p, False, r["path"], r["folder"]) for p in paths
        )]
        return {"repository": repo, "viewer": user["name"], "conflicts": conflicts}


def load_users(path):
    raw = json.loads(Path(path).read_text(encoding="utf-8"))
    users = raw["users"]
    if not isinstance(users, list) or not users:
        raise ValueError("Configure at least one user")
    names, tokens = set(), set()
    for u in users:
        name = text_field(u["name"], "user name", 128)
        token = u["token"]
        if (not isinstance(token, str) or not re.fullmatch(r"[A-Za-z0-9._~-]{32,256}", token)
                or name in names or token in tokens):
            raise ValueError("Unique user names and high-entropy tokens (32+ characters) required")
        names.add(name)
        tokens.add(token)
        repos = u["repositories"]
        if not isinstance(repos, list) or not repos:
            raise ValueError("Each user must have explicit repository access")
        for repo in repos:
            repository(repo)
        u["admin"] = flag(u.get("admin", False), "admin")
    return users


def make_handler(store, users):
    routes = {
        "/v1/locks/list": store.list,
        "/v1/locks/acquire": store.acquire,
        "/v1/locks/release": store.release,
        "/v1/locks/check": store.check,
    }

    class Handler(BaseHTTPRequestHandler):
        server_version = "UnityGitLock/0.1"
        protocol_version = "HTTP/1.0"

        def setup(self):
            super().setup()
            self.connection.settimeout(15)

        def reply(self, status, value):
            payload = json.dumps(value, ensure_ascii=True, separators=(",", ":")).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(payload)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(payload)

        def authenticate(self):
            header = self.headers.get("Authorization", "")
            if not header.startswith("Bearer "):
                raise ApiError(401, "Authentication required")
            token = header[7:].encode("utf-8")
            for u in users:
                if hmac.compare_digest(token, u["token"].encode()):
                    return u
            raise ApiError(401, "Invalid token")

        def do_GET(self):
            if self.path == "/health":
                self.reply(200, {"status": "ok"})
            else:
                self.reply(404, {"error": "Not found"})

        def do_POST(self):
            try:
                if self.path not in routes:
                    raise ApiError(404, "Not found")
                user = self.authenticate()
                length = int(self.headers.get("Content-Length", "0"))
                if not 0 < length <= MAX_BODY:
                    raise ApiError(413, "Invalid request size")
                body = json.loads(self.rfile.read(length).decode("utf-8"))
                if not isinstance(body, dict):
                    raise ApiError(400, "Expected a JSON object")
                repo = repository(body.get("repository"))
                if repo not in user["repositories"]:
                    raise ApiError(403, "Repository access denied")
                route = routes[self.path]
                result = route(repo, user) if self.path.endswith("/list") else route(repo, user, body)
                self.reply(200, result)
            except ApiError as e:
                self.reply(e.status, {"error": str(e)})
            except (ValueError, UnicodeError):
                self.reply(400, {"error": "Invalid request"})
            except (BrokenPipeError, ConnectionResetError):
                pass
            except Exception:
                LOG.exception("Request failed")
                try:
                    self.reply(503, {"error": "Lock service unavailable"})
                except OSError:
                    pass

        def log_message(self, fmt, *args):
            LOG.info("%s %s", self.client_address[0], fmt % args)

    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True, help="Private JSON user/token configuration")
    parser.add_argument("--db", default="git-lock.sqlite3")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO)
    users = load_users(args.config)
    store = Store(args.db)
    with ThreadingHTTPServer((args.host, args.port), make_handler(store, users)) as server:
        LOG.info("Listening on %s:%s (HTTP; use TLS reverse proxy for remote clients)", args.host, args.port)
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
