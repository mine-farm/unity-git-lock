import os
import subprocess
import tempfile
import unittest
from pathlib import Path

import git_lock


class GitPathTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        old = os.getcwd()
        self.addCleanup(os.chdir, old)
        os.chdir(self.tmp.name)
        self.git("init", "-b", "main")
        self.git("config", "user.name", "Test User")
        self.git("config", "user.email", "test@example.invalid")
        self.git("config", "commit.gpgsign", "false")
        Path("old.txt").write_text("example", encoding="utf-8")
        self.git("add", ".")
        self.git("commit", "-m", "base")
        self.base = self.git("rev-parse", "HEAD")
        self.git("update-ref", "refs/remotes/origin/main", self.base)

    def git(self, *args):
        return subprocess.check_output(["git", *args], stderr=subprocess.PIPE).decode().strip()

    def test_staged_and_renamed_paths(self):
        self.git("mv", "old.txt", "new.txt")
        self.assertEqual(git_lock.staged_paths(), {"old.txt", "new.txt"})
        self.git("commit", "-m", "rename")
        head = self.git("rev-parse", "HEAD")
        line = f"refs/heads/main {head} refs/heads/main {self.base}\n"
        self.assertEqual(git_lock.pushed_paths("origin", [line]), {"old.txt", "new.txt"})

    def test_new_branch_and_force_update(self):
        Path("new.txt").write_text("new", encoding="utf-8")
        self.git("add", "new.txt")
        self.git("commit", "-m", "add file")
        head = self.git("rev-parse", "HEAD")
        new = f"refs/heads/dev {head} refs/heads/dev {'0' * len(head)}"
        self.assertEqual(git_lock.pushed_paths("origin", [new]), {"new.txt"})
        rewind = f"refs/heads/main {self.base} refs/heads/main {head}"
        self.assertEqual(git_lock.pushed_paths("origin", [rewind]), {"new.txt"})

    def test_branch_delete_ignored(self):
        line = f"(delete) {'0' * len(self.base)} refs/heads/dev {self.base}"
        self.assertEqual(git_lock.pushed_paths("origin", [line]), set())


if __name__ == "__main__":
    unittest.main()
