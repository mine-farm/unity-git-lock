# Unity Git Lock

Repository-wide file and folder locks for Unity projects using ordinary Git. Git LFS is **not required**. Files remain in their original Git format; the lock service stores only path and ownership metadata.

**Status:** 0.1.0 initial implementation; Unity 2022.3 LTS+ is the target, including Unity 6. Editor compilation and integration must be verified in a real Unity project before team rollout.

## Features

- Atomic file or recursive folder locking, independent of the Git branch or extension. Works for scripts, scenes, prefabs, assets and arbitrary repository-relative files.
- Owner (authenticated server identity), acquisition branch, full HEAD commit SHA, UTC time and optional reason.
- Project and Hierarchy lock icons, Inspector lock details, Scene View warning for locked scenes/prefab stages.
- Unity editability callbacks and filtering of save/move/delete operations. Unity asset and its .meta sidecar share a lock.
- Server-side ownership validation, conflict detection and explicit administrator force unlock.
- Automatic asynchronous polling; stale status is visible. Optional fail-closed editor protection, enabled by default when the integration is enabled.
- Optional Git CLI pre-commit and pre-push checks, including non-Unity files.

## Installation

Unity Package Manager > Add package from git URL:

```
https://github.com/mine-farm/unity-git-lock.git?path=/packages/com.minefarm.git-lock
```

Requires Git on PATH and a committed Git working tree. The server and Python CLI require Python 3.10+; the Unity package itself does not require Python on developer machines.

### 1. Run the shared lock service

Run a **single shared service** for the team. Start locally for testing:

```sh
cd server
cp users.example.json users.json
python3 -c "import secrets; print(secrets.token_urlsafe(48))"
# Edit users.json: give each developer a unique token, name and repository ACL.
python3 git_lock_server.py --config users.json --db git-lock.sqlite3
```

Example repository key: github.com/mine-farm/mf. Every clone/branch of the same repository must use the exact same key. The server does not access GitHub or store Git files. The configured names are server identities, not automatically verified GitHub accounts.

Default address: http://127.0.0.1:8765. For a team deploy behind an HTTPS reverse proxy with request/rate limits, keep the HTTP backend private, persist and back up the SQLite database, and restrict access to the private users configuration. The included Dockerfile can build the backend; mount the users file at /config/users.json and a writable persistent directory at /data (UID 10001). Do not publish the raw HTTP port to the public Internet. Generate a separate high-entropy token for each user; never commit tokens. Restart the service after changes to users.json.

### 2. Configure Unity

Open **Tools > Git Lock > Manager**, expand Connection settings, and set:

| Setting | Value |
| --- | --- |
| Server URL | Shared HTTPS endpoint (localhost HTTP allowed for local testing) |
| Repository key | Shared lowercase repository ID, e.g. github.com/mine-farm/mf |
| Session token | Your own server-issued token, or UNITY_GIT_LOCK_TOKEN in the editor process environment |
| Enable protection | On |
| Refresh | 10 seconds by default |
| Block when stale | On by default; a stale/missing snapshot blocks Unity asset editing/saving |

Apply settings and verify Connected plus your authenticated identity. Non-secret project configuration is stored in ProjectSettings/UnityGitLockSettings.asset; share that file as appropriate. Session tokens are kept in Unity SessionState (not a credential vault; cleared on editor exit) and are not written to the project settings.

### 3. Lock and edit

1. Select files/folders in the Project window, right-click **Git Lock > Lock Selected...**.
2. Enter an optional reason and acquire the locks in the manager. Wait for a successful response before editing.
3. Other users receive the updated status on their next poll. Red icons indicate foreign locks; green indicates your lock. The Inspector shows the owner, branch and short acquisition commit; the manager shows the full SHA.
4. Edit, save, commit and push. Release the lock afterward using the context menu or manager.

Folder locks include future child files. To lock a file outside Unity's Project window, enter its Git-root-relative path in the manager or use the CLI. The manager also supports filtering, refresh, and confirmed administrator force unlock.

## Lock semantics

- Lock scope is repository + normalized path, not branch + path. Branch/commit record where the lock was acquired, not where the file currently lives. The same authenticated owner can edit across branches/worktrees; do not share a token.
- Paths use forward slashes, ASCII case-insensitive matching, and one trailing .meta alias (Unity sidecars). Non-ASCII path case is exact. Folder matching observes directory boundaries.
- Exact repeat acquisition by the same owner is idempotent and preserves the original metadata. All-or-nothing multi-selection requests reject overlapping/foreign targets. SQLite BEGIN IMMEDIATE serializes acquisitions.
- Locks are explicit, persistent and do not expire automatically. Administrators can force unlock stale claims. Unlock uses lock IDs, not paths, so an old client cannot accidentally release a newer claim.
- Locks are path-based, not Git object IDs or Unity GUIDs. Release locks rooted at a path before renaming/moving it; a folder lock can still cover renames of unlocked children within the folder. Release locks after deletion/push as well.
- Unlocked paths can be edited without claiming a lock; this is an advisory collaboration system, not exclusive checkout for every file.

## Optional CLI and Git hook

Clone this repository separately (or copy tools/git_lock.py). Configure these environment variables on each workstation:

```sh
export UNITY_GIT_LOCK_URL=https://your-lock-server.example
export UNITY_GIT_LOCK_REPOSITORY=github.com/mine-farm/mf
export UNITY_GIT_LOCK_TOKEN=YOUR_PERSONAL_TOKEN
python3 /path/to/tools/git_lock.py list
python3 /path/to/tools/git_lock.py lock Assets/Scenes/Main.unity --reason "Level edit"
python3 /path/to/tools/git_lock.py lock Assets/Art --folder
python3 /path/to/tools/git_lock.py unlock LOCK_ID
```

On Windows use PowerShell environment variables and python instead of python3 if necessary.

Adapt tools/pre-commit.example or tools/pre-push.example in the project's active Git hooks directory. Do not overwrite existing hooks; chain the check and preserve stdin for pre-push. Make the hook executable on Unix. For pre-push, the CLI checks the union of changed paths in introduced commits and net changes to existing branch refs (including force-push changes). New-branch comparison uses local remote-tracking refs; fetch before pushing. Merge commits can produce conservative false positives. Server errors fail the hook; re-fetch/refresh and resolve conflicts. Hooks do not run automatically unless installed.

## Enforcement boundaries

This is not a GitHub permission system. Unity's editability callbacks disable supported persistent asset inspectors; save/move/delete callbacks reject known foreign locks. Loaded scene objects, prefab stage objects and custom editors can still change in memory; their supported Unity save paths are guarded. External IDEs, direct filesystem writes, asset creation/import APIs, Git checkout/merge and APIs bypassing Unity callbacks can change files. No OS read-only attributes are changed. Polling introduces a race window and the optional local Git hooks can be bypassed or race with acquisition. GitHub does not automatically enforce these locks on the server. Do not treat the package as a security boundary; a trusted team must acquire locks before editing and push before releasing. Strict enforcement would require a coordinated remote-side push/merge gate.

## Tests

Server (unit, concurrency, persistence and HTTP auth):

```sh
cd server
python3 -m unittest -v
```

Unity EditMode tests cover path validation, metadata aliases and folder boundaries. For an installed package, include "com.minefarm.git-lock" in the project's manifest.json testables array and open Test Runner > EditMode. Use two separate users/clones to verify Project/Inspector indicators, save filtering, move/delete restrictions, remote refresh, and offline behavior. See docs/validation.md.

## Layout

- packages/com.minefarm.git-lock: editor-only UPM package.
- server: Python standard library / SQLite service.
- tools: optional CLI and local Git hook examples.
- docs: API and manual integration validation.
