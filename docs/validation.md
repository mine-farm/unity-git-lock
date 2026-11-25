# Integration validation checklist

Target Unity 2022.3 LTS and Unity 6 on Windows/macOS/Linux as available. This is a manual checklist, not a claim that Unity tests have been executed.

1. Start a temporary shared server and configure two unique users Alice and Bob with access to the same repository key.
2. Open two separate Unity clones of a Git repository with a committed initial scene and prefab; use different branches. Install the package in each. Enable protection and verify Connected, correct user, branch and SHA.
3. Alice acquires a normal C# file and a prefab. Verify the server lists the full SHA and Bob cannot acquire the same path or its .meta sidecar on a different branch.
4. After polling, verify Bob's Project indicators and Inspector warning show Alice and acquisition metadata. Verify supported asset inspectors are disabled and save filtering logs rejected paths. Try moving/deleting the locked prefab and its parent folder; verify rejection without affecting unrelated assets.
5. Lock a scene and enter Prefab Mode for a locked prefab. Verify Scene View warning; try scene save and prefab stage save. Verify on-disk content with git diff. In-memory changes may remain; do not discard unsaved work without user consent.
6. Acquire a folder and verify all child paths, future child paths, and the folder .meta match. Verify a similarly prefixed sibling folder does not match. Concurrent overlapping requests must yield exactly one successful owner.
7. Test owned locks, release and admin-only force release. Check an old lock ID cannot release a new lock.
8. Stop the service, wait for the configured freshness window, and confirm default stale protection blocks Unity edits/saves. In fail-open mode known foreign locks still block while unknown paths are allowed. Restore and refresh.
9. Lock README.md using the CLI and verify Bob's pre-commit/pre-push checks reject its changed path. Test normal push, new branch, rename, and force-push path comparison on disposable refs.
10. Reload scripts, close and reopen Unity, and switch branches. Ensure configuration persists without committing tokens, and locks stay attached to the repository rather than the branch.
