using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Minefarm.GitLock
{
    internal sealed class AssetLockGuard : AssetModificationProcessor
    {
        // Unity's legacy callback includes a useful explanatory message and is
        // also consulted by its batched editability implementation (2022.3+).
        private static bool IsOpenForEdit(string assetPath, out string message) =>
            !GitLockService.Blocked(assetPath, out message);

        private static bool CanOpenForEdit(string assetPath, out string message) =>
            !GitLockService.Blocked(assetPath, out message);

        private static string[] OnWillSaveAssets(string[] paths)
        {
            var allowed = new List<string>(paths.Length);
            foreach (var path in paths)
            {
                if (GitLockService.Blocked(path, out var reason))
                    Debug.LogWarning("Save blocked: " + path + "\n" + reason);
                else allowed.Add(path);
            }
            return allowed.ToArray();
        }

        private static AssetDeleteResult OnWillDeleteAsset(string path, RemoveAssetOptions options)
        {
            if (!GitLockService.Blocked(path, out var reason, true)) return AssetDeleteResult.DidNotDelete;
            Debug.LogWarning("Delete blocked: " + path + "\n" + reason);
            return AssetDeleteResult.FailedDelete;
        }

        private static AssetMoveResult OnWillMoveAsset(string from, string to)
        {
            // Path-based locks do not follow renames. Require releasing any
            // lock rooted inside the moved subtree before moving it, even own.
            if (GitLockService.Blocked(from, out var reason, true) ||
                GitLockService.Blocked(to, out reason, true))
            {
                Debug.LogWarning("Move blocked: " + reason);
                return AssetMoveResult.FailedMove;
            }
            if (GitLockService.Enabled && GitLockService.TryRepoPath(from, out var repoPath))
            {
                foreach (var record in GitLockService.Records)
                {
                    if (LockPaths.Overlaps(repoPath, true, record.path, false))
                    {
                        Debug.LogWarning("Move blocked: release the lock at " + record.path + " before renaming.");
                        return AssetMoveResult.FailedMove;
                    }
                }
            }
            return AssetMoveResult.DidNotMove;
        }
    }
}
