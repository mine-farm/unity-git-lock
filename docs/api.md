# Lock service protocol (v1)

JSON over HTTPS (HTTP only for local development). Every POST requires Authorization: Bearer PERSONAL_TOKEN. Request bodies include a repository key assigned by the server administrator. No GitHub token is required. User name and administrator permissions are determined from server configuration, not the client.

| Method | Route | Additional request properties |
| --- | --- | --- |
| GET | /health | none |
| POST | /v1/locks/list | none |
| POST | /v1/locks/acquire | targets: [{path, folder}], branch, commit, reason (optional) |
| POST | /v1/locks/release | ids: [lock ID], force: boolean (optional) |
| POST | /v1/locks/check | paths: [repo-relative path] |

List/acquire/release return {repository, viewer, admin, locks}. A record includes {id, path, folder, owner, branch, commit, lockedAt, reason}. Check returns {repository, viewer, conflicts}, where conflicts are other owners' locks matching the submitted paths. The client supplies the acquisition branch/commit, recorded as reported; the server does not validate Git history or remote membership.

Acquisition/release are atomic. The service rejects overlapping file/folder claims with HTTP 409; duplicate exact claims by the same owner preserve the lock ID and acquisition metadata. Releasing a missing ID returns 404. Unauthorized requests return 401/403. Errors include {error: message}. Client must refresh after a failed or ambiguous mutation, including timeouts.

Limits: request <= 1 MiB; up to 128 targets or release IDs per transaction, up to 1000 check paths, up to 5000 active locks per repository. Paths are Git-root-relative, without traversal components, control characters, backslash, colon or .git segments. Use UTF-8. The conservative ASCII-case-folded path key also removes a trailing .meta.

The backend stores lock metadata in SQLite. Run behind a trusted TLS reverse proxy for non-loopback clients. Keep user tokens and database files private. The API intentionally does not implement Git hosting, webhook enforcement, or credential federation.
