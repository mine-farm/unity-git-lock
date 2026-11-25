using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Minefarm.GitLock
{
    internal sealed class GitLockWindow : EditorWindow
    {
        private Vector2 scroll;
        private bool showSettings;
        private bool enabledDraft, failClosedDraft, folder;
        private string urlDraft, repoDraft, tokenDraft = "", filter = "", reason = "", manualPath = "";
        private int refreshDraft;
        private LockTarget[] selected = Array.Empty<LockTarget>();

        [MenuItem("Tools/Git Lock/Manager")]
        public static void Open()
        {
            var window = GetWindow<GitLockWindow>("Git Lock");
            window.minSize = new Vector2(480, 360);
            window.Show();
        }

        private void OnEnable()
        {
            ReadSettings();
            showSettings = !GitLockService.Enabled;
            GitLockService.Changed += Repaint;
            Selection.selectionChanged += Repaint;
        }

        private void OnDisable()
        {
            GitLockService.Changed -= Repaint;
            Selection.selectionChanged -= Repaint;
        }

        private void ReadSettings()
        {
            var s = GitLockSettings.instance;
            enabledDraft = s.enabled;
            failClosedDraft = s.failClosed;
            urlDraft = s.serverUrl;
            repoDraft = s.repository;
            refreshDraft = s.refreshSeconds;
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Unity Git Lock", EditorStyles.boldLabel);
            DrawStatus();
            showSettings = EditorGUILayout.Foldout(showSettings, "Connection settings", true);
            if (showSettings) DrawSettings();
            EditorGUILayout.Space(8);

            using (new EditorGUI.DisabledScope(!GitLockService.Enabled || GitLockService.Busy || !GitLockService.Fresh))
            {
                DrawAcquire();
            }

            EditorGUILayout.Space(8);
            DrawRecords();
            EditorGUILayout.EndScrollView();
        }

        private void DrawStatus()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var status = !GitLockService.Enabled ? "Disabled"
                    : GitLockService.Busy ? "Synchronizing..."
                    : GitLockService.Fresh ? "Connected" : "Status unavailable";
                EditorGUILayout.LabelField(status, EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(!GitLockService.Enabled || GitLockService.Busy))
                    if (GUILayout.Button("Refresh", GUILayout.Width(90))) GitLockService.Refresh();
            }
            var context = GitLockService.Context;
            var snapshot = GitLockService.Snapshot;
            if (snapshot != null)
            {
                EditorGUILayout.LabelField("Signed in", snapshot.viewer + (snapshot.admin ? " (administrator)" : ""));
                EditorGUILayout.LabelField("Repository", snapshot.repository);
                EditorGUILayout.LabelField("Last sync", GitLockService.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            }
            if (context != null)
            {
                EditorGUILayout.LabelField("Local branch", context.Branch);
                EditorGUILayout.SelectableLabel(context.Commit, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            if (!string.IsNullOrEmpty(GitLockService.Error))
                EditorGUILayout.HelpBox(GitLockService.Error, MessageType.Error);
            if (GitLockService.Enabled && !GitLockService.Fresh)
                EditorGUILayout.HelpBox("Lock status is stale. " + (GitLockSettings.instance.failClosed
                    ? "Unity edits are blocked until synchronization succeeds."
                    : "Known foreign locks remain blocked; unknown files may be edited."), MessageType.Warning);
            if (!string.IsNullOrEmpty(GitLockService.Notice))
                EditorGUILayout.HelpBox(GitLockService.Notice, GitLockService.Notice.StartsWith("Request failed", StringComparison.Ordinal)
                    ? MessageType.Warning : MessageType.Info);
        }

        private void DrawSettings()
        {
            using (new EditorGUI.DisabledScope(GitLockService.Busy))
            {
                enabledDraft = EditorGUILayout.Toggle("Enable protection", enabledDraft);
                urlDraft = EditorGUILayout.TextField("Server URL", urlDraft);
                repoDraft = EditorGUILayout.TextField("Repository key", repoDraft);
                refreshDraft = EditorGUILayout.IntSlider("Refresh (seconds)", refreshDraft, 3, 30);
                failClosedDraft = EditorGUILayout.Toggle("Block when stale", failClosedDraft);
                EditorGUILayout.HelpBox("Use the same repository key on every branch and clone. HTTPS is required except on localhost.", MessageType.None);
                EditorGUILayout.LabelField("Personal token", string.IsNullOrWhiteSpace(GitLockSettings.Token) ? "Not configured" : "Configured");
                tokenDraft = EditorGUILayout.PasswordField("Session token", tokenDraft);
                EditorGUILayout.HelpBox("Session tokens are not committed; alternatively set UNITY_GIT_LOCK_TOKEN before launching Unity.", MessageType.None);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Apply settings"))
                    {
                        try
                        {
                            if (enabledDraft)
                            {
                                GitLockSettings.ValidateEndpoint(urlDraft);
                                if (string.IsNullOrWhiteSpace(repoDraft))
                                    throw new InvalidOperationException("Enter a repository key.");
                            }
                            var s = GitLockSettings.instance;
                            s.enabled = enabledDraft;
                            s.serverUrl = urlDraft.Trim();
                            s.repository = repoDraft.Trim();
                            s.refreshSeconds = refreshDraft;
                            s.failClosed = failClosedDraft;
                            s.Persist();
                            if (!string.IsNullOrWhiteSpace(tokenDraft))
                                GitLockSettings.SetSessionToken(tokenDraft);
                            tokenDraft = "";
                            GitLockService.Reset();
                            GitLockService.Refresh();
                        }
                        catch (Exception ex) { EditorUtility.DisplayDialog("Git Lock", ex.Message, "OK"); }
                    }
                    if (GUILayout.Button("Clear session token"))
                    {
                        GitLockSettings.SetSessionToken("");
                        tokenDraft = "";
                        GitLockService.Reset();
                        GitLockService.Refresh();
                    }
                }
            }
        }

        private void DrawAcquire()
        {
            EditorGUILayout.LabelField("Acquire locks", EditorStyles.boldLabel);
            reason = EditorGUILayout.TextField("Reason (optional)", reason);
            if (GUILayout.Button("Use current Project selection"))
                selected = GitLockMenus.SelectedTargets();
            if (selected.Length > 0)
            {
                foreach (var target in selected.Take(12))
                    EditorGUILayout.LabelField((target.folder ? "[Folder] " : "[File] ") + target.path, EditorStyles.wordWrappedLabel);
                if (selected.Length > 12) EditorGUILayout.LabelField("... and " + (selected.Length - 12) + " more");
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Lock selected (" + selected.Length + ")")) Submit(selected);
                    if (GUILayout.Button("Clear", GUILayout.Width(70))) selected = Array.Empty<LockTarget>();
                }
            }

            EditorGUILayout.Space(4);
            manualPath = EditorGUILayout.TextField("Repository-relative path", manualPath);
            folder = EditorGUILayout.Toggle("Folder (recursive)", folder);
            using (new EditorGUI.DisabledScope(!LockPaths.IsValid(manualPath)))
                if (GUILayout.Button("Lock path"))
                    Submit(new[] { new LockTarget(manualPath, folder) });
            EditorGUILayout.HelpBox("Manual paths can include files outside Assets, such as README.md or ProjectSettings. Folder locks also cover files added later.", MessageType.None);
        }

        private void Submit(LockTarget[] targets)
        {
            if (targets.Length > 128 || reason.Length > 512)
            {
                EditorUtility.DisplayDialog("Git Lock", "Select at most 128 paths and use a reason of at most 512 characters.", "OK");
                return;
            }
            GitLockService.Acquire(targets, reason);
        }

        private void DrawRecords()
        {
            EditorGUILayout.LabelField("Repository locks (" + GitLockService.Records.Length + ")", EditorStyles.boldLabel);
            filter = EditorGUILayout.TextField("Filter path / owner / branch", filter);
            foreach (var record in GitLockService.Records)
            {
                if (!string.IsNullOrEmpty(filter) &&
                    record.path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    record.owner.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (record.branch ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField((record.folder ? "[Folder] " : "[File] ") + record.path, EditorStyles.wordWrappedLabel);
                    var mine = GitLockService.IsMine(record);
                    EditorGUILayout.LabelField(mine ? "Owned by you" : "Locked by another user", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("Owner / branch", record.owner + " / " + record.branch);
                    EditorGUILayout.LabelField("Commit at acquisition");
                    EditorGUILayout.SelectableLabel(record.commit ?? "", EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    EditorGUILayout.LabelField("Locked at (UTC)", record.lockedAt);
                    if (!string.IsNullOrEmpty(record.reason))
                        EditorGUILayout.LabelField("Reason: " + record.reason, EditorStyles.wordWrappedLabel);
                    using (new EditorGUI.DisabledScope(GitLockService.Busy || !GitLockService.Fresh || !GitLockService.Enabled))
                    {
                        if (mine && GUILayout.Button("Unlock"))
                            ConfirmRelease(record, false);
                        else if (!mine && GitLockService.Snapshot?.admin == true && GUILayout.Button("Force unlock (admin)"))
                            ConfirmRelease(record, true);
                    }
                }
            }
        }

        internal static void ConfirmRelease(LockRecord record, bool force)
        {
            if (EditorUtility.DisplayDialog("Git Lock", (force ? "Force unlock " : "Unlock ") + record.path +
                    " (" + record.owner + ")? Only release after your changes are pushed or the owner agrees.", "Unlock", "Cancel"))
                GitLockService.Release(new[] { record.id }, force);
        }

        internal static void OpenForSelection()
        {
            Open();
            var window = GetWindow<GitLockWindow>();
            window.selected = GitLockMenus.SelectedTargets();
            window.Repaint();
        }

        internal static void OpenWithFilter(string path)
        {
            Open();
            var window = GetWindow<GitLockWindow>();
            window.filter = path;
            window.Repaint();
        }
    }

    internal static class GitLockMenus
    {
        public static LockTarget[] SelectedTargets()
        {
            var targets = new List<LockTarget>();
            foreach (var guid in Selection.assetGUIDs)
            {
                var asset = AssetDatabase.GUIDToAssetPath(guid);
                if (!GitLockService.TryRepoPath(asset, out var path)) continue;
                // Registry/cache packages are not part of this worktree.
                if (asset.StartsWith("Packages/", StringComparison.Ordinal) &&
                    !System.IO.File.Exists(asset) && !System.IO.Directory.Exists(asset)) continue;
                if (targets.Any(t => LockPaths.Key(t.path) == LockPaths.Key(path))) continue;
                targets.Add(new LockTarget(path, AssetDatabase.IsValidFolder(asset)));
            }
            // A selected parent folder already covers selected descendants.
            return targets.Where(t => !targets.Any(parent => parent != t && parent.folder &&
                LockPaths.Overlaps(parent.path, true, t.path, false))).ToArray();
        }

        [MenuItem("Assets/Git Lock/Lock Selected...", false, 2000)]
        private static void LockSelected() => GitLockWindow.OpenForSelection();

        [MenuItem("Assets/Git Lock/Lock Selected...", true)]
        private static bool ValidateLock() => GitLockService.Enabled && GitLockService.Fresh &&
            !GitLockService.Busy && Selection.assetGUIDs.Length > 0;

        [MenuItem("Assets/Git Lock/Unlock Selected...", false, 2001)]
        private static void UnlockSelected()
        {
            var targets = SelectedTargets();
            var records = GitLockService.Records.Where(r => GitLockService.IsMine(r) &&
                targets.Any(t => LockPaths.Key(t.path) == LockPaths.Key(r.path))).ToArray();
            if (records.Length == 0)
            {
                EditorUtility.DisplayDialog("Git Lock", "No owned lock exactly matches the selection. Use the Manager to release a parent folder lock.", "OK");
                return;
            }
            if (records.Length > 128)
            {
                EditorUtility.DisplayDialog("Git Lock", "Unlock up to 128 locks at once.", "OK");
                return;
            }
            if (EditorUtility.DisplayDialog("Git Lock", "Unlock " + records.Length +
                " selected lock(s)? Push your changes first.", "Unlock", "Cancel"))
                GitLockService.Release(records.Select(r => r.id).ToArray());
        }

        [MenuItem("Assets/Git Lock/Unlock Selected...", true)]
        private static bool ValidateUnlock() => ValidateLock();

        [MenuItem("Assets/Git Lock/Show Lock Details", false, 2002)]
        private static void ShowDetails()
        {
            var target = SelectedTargets().FirstOrDefault();
            var record = target == null ? null : GitLockService.Find(target.path);
            GitLockWindow.OpenWithFilter(record?.path ?? target?.path ?? "");
        }
    }
}
