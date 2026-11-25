import json
import tempfile
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor
from http.server import ThreadingHTTPServer
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

from git_lock_server import ApiError, Store, make_handler, normalize_path

REPO = "github.com/team/game"
ALICE = {"name": "alice", "admin": True, "token": "a" * 32, "repositories": [REPO]}
BOB = {"name": "bob", "admin": False, "token": "b" * 32, "repositories": [REPO]}


def acquire(*paths, branch="main"):
    return {"targets": [{"path": p, "folder": f} for p, f in paths],
            "branch": branch, "commit": "c" * 40, "reason": "editing"}


class LocksTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.db = Path(self.tmp.name) / "locks.sqlite3"
        self.store = Store(self.db)

    def test_cross_branch_and_meta(self):
        result = self.store.acquire(REPO, ALICE, acquire(("Assets/Player.cs", False)))
        record = result["locks"][0]
        self.assertEqual(record["owner"], "alice")
        self.assertEqual(record["branch"], "main")
        self.assertEqual(record["commit"], "c" * 40)
        with self.assertRaises(ApiError) as ctx:
            self.store.acquire(REPO, BOB, acquire(("assets/player.cs.meta", False), branch="dev"))
        self.assertEqual(ctx.exception.status, 409)
        self.assertEqual(self.store.check(REPO, BOB, {"paths": ["Assets/Player.cs.meta"]})["conflicts"], [record])
        self.assertEqual(self.store.check(REPO, ALICE, {"paths": ["Assets/Player.cs"]})["conflicts"], [])
        self.assertEqual(self.store.acquire(REPO, ALICE, acquire(("Assets/Player.cs", False)))["locks"], [record])

    def test_folder_and_atomic_batch(self):
        self.store.acquire(REPO, ALICE, acquire(("Assets/Art", True)))
        with self.assertRaises(ApiError):
            self.store.acquire(REPO, BOB, acquire(("Assets/Other.cs", False), ("Assets/Art/A.png", False)))
        self.assertEqual(len(self.store.list(REPO, BOB)["locks"]), 1)
        self.store.acquire(REPO, BOB, acquire(("Assets/Artwork/B.png", False)))
        self.assertEqual(len(self.store.list(REPO, BOB)["locks"]), 2)
        with self.assertRaises(ApiError):
            self.store.acquire(REPO, BOB, acquire(("Assets", True)))
        self.assertEqual(len(self.store.check(REPO, BOB, {"paths": ["Assets/Art/New.png"]})["conflicts"]), 1)

    def test_release_authorization_and_persistence(self):
        lock = self.store.acquire(REPO, BOB, acquire(("README.md", False)))["locks"][0]
        self.assertEqual(Store(self.db).list(REPO, ALICE)["locks"], [lock])
        with self.assertRaises(ApiError) as ctx:
            self.store.release(REPO, ALICE, {"ids": [lock["id"]]})
        self.assertEqual(ctx.exception.status, 403)
        self.store.release(REPO, ALICE, {"ids": [lock["id"]], "force": True})
        self.assertEqual(self.store.list(REPO, BOB)["locks"], [])
        other = self.store.acquire(REPO, ALICE, acquire(("README.md", False)))["locks"][0]
        with self.assertRaises(ApiError):
            self.store.release(REPO, BOB, {"ids": [other["id"]], "force": True})
        with self.assertRaises(ApiError):
            self.store.release(REPO, ALICE, {"ids": [other["id"], lock["id"]]})
        self.assertEqual(self.store.list(REPO, ALICE)["locks"], [other])
        self.store.release(REPO, ALICE, {"ids": [other["id"]]})

    def test_parallel_acquisition(self):
        barrier = threading.Barrier(2)
        def claim(user):
            barrier.wait()
            try:
                self.store.acquire(REPO, user, acquire(("Assets/Scene.unity", False)))
                return 200
            except ApiError as e:
                return e.status
        with ThreadPoolExecutor(max_workers=2) as pool:
            self.assertEqual(sorted(pool.map(claim, [ALICE, BOB])), [200, 409])
        self.assertEqual(len(self.store.list(REPO, ALICE)["locks"]), 1)

    def test_path_validation_and_repository_isolation(self):
        for path in ("/a", "../a", "a/../b", "a//b", "a\\b", "C:/a", ".git/config", "a/"):
            with self.subTest(path=path), self.assertRaises(ApiError):
                normalize_path(path)
        self.store.acquire(REPO, ALICE, acquire(("Assets/X", False)))
        self.assertEqual(self.store.list("other", ALICE)["locks"], [])

    def test_http_auth(self):
        server = ThreadingHTTPServer(("127.0.0.1", 0), make_handler(self.store, [ALICE, BOB]))
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(server.server_close)
        self.addCleanup(thread.join, 3)
        self.addCleanup(server.shutdown)
        url = f"http://127.0.0.1:{server.server_port}/v1/locks/list"
        def call(token, repo):
            req = Request(url, data=json.dumps({"repository": repo}).encode(),
                          headers={"Authorization": "Bearer " + token, "Content-Type": "application/json"})
            with urlopen(req, timeout=3) as res:
                return json.load(res)
        self.assertEqual(call(ALICE["token"], REPO)["viewer"], "alice")
        with self.assertRaises(HTTPError) as e:
            call("wrong", REPO)
        self.assertEqual(e.exception.code, 401)
        with self.assertRaises(HTTPError) as e:
            call(ALICE["token"], "other")
        self.assertEqual(e.exception.code, 403)


if __name__ == "__main__":
    unittest.main()
