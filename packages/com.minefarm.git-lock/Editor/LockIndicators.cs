using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Minefarm.GitLock
{
    [InitializeOnLoad]
    internal static class LockIndicators
    {
        static LockIndicators()
        {
            EditorApplication.projectWindowItemOnGUI += DrawProject;
            EditorApplication.hierarchyWindowItemOnGUI += DrawHierarchy;
            Editor.finishedDefaultHeaderGUI += DrawHeader;
            SceneView.duringSceneGui += DrawScene;
        }

        private static string ObjectPath(Object target)
        {
            if (target == null) return null;
            var assetPath = AssetDatabase.GetAssetPath(target);
            if (!string.IsNullOrEmpty(assetPath)) return assetPath;
            var go = target as GameObject;
            if (go == null && target is Component component) go = component.gameObject;
            if (go == null || !go.scene.IsValid()) return null;
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && go.scene == stage.scene) return stage.assetPath;
            return go.scene.path;
        }

        private static void DrawProject(string guid, Rect rect)
        {
            if (!GitLockService.Enabled || Event.current.type != EventType.Repaint) return;
            var asset = AssetDatabase.GUIDToAssetPath(guid);
            if (!GitLockService.TryRepoPath(asset, out var path)) return;
            DrawBadge(rect, GitLockService.Find(path));
        }

        private static void DrawHierarchy(int instanceId, Rect rect)
        {
            if (!GitLockService.Enabled || Event.current.type != EventType.Repaint) return;
            var target = EditorUtility.InstanceIDToObject(instanceId);
            if (GitLockService.TryRepoPath(ObjectPath(target), out var path))
                DrawBadge(rect, GitLockService.Find(path));
        }

        private static void DrawBadge(Rect rect, LockRecord record)
        {
            if (record == null) return;
            var mine = GitLockService.IsMine(record);
            var badge = new Rect(rect.xMax - 17, rect.yMin, 16, 16);
            var previous = GUI.color;
            try
            {
                GUI.color = mine ? new Color(0.35f, 0.9f, 0.55f) : new Color(1f, 0.4f, 0.35f);
                var icon = EditorGUIUtility.IconContent("LockIcon-On");
                var content = icon != null && icon.image != null
                    ? new GUIContent(icon.image, record.Summary) : new GUIContent("L", record.Summary);
                GUI.Label(badge, content);
            }
            finally { GUI.color = previous; }
        }

        private static void DrawHeader(Editor editor)
        {
            if (!GitLockService.Enabled || editor == null) return;
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in editor.targets)
            {
                var asset = ObjectPath(target);
                if (string.IsNullOrEmpty(asset) || !paths.Add(asset)) continue;
                var record = GitLockService.TryRepoPath(asset, out var path) ? GitLockService.Find(path) : null;
                if (record == null)
                {
                    if (GitLockService.Blocked(asset, out var message))
                        EditorGUILayout.HelpBox(message, MessageType.Warning);
                    continue;
                }
                var mine = GitLockService.IsMine(record);
                if (mine && GitLockService.Blocked(asset, out var staleMessage))
                    EditorGUILayout.HelpBox(staleMessage, MessageType.Warning);
                EditorGUILayout.HelpBox((mine ? "Git Lock: owned by you" : "Git Lock: editing / saving prohibited") +
                    "\nOwner: " + record.owner + "\nBranch: " + record.branch +
                    "\nCommit: " + record.ShortCommit + "\nLocked at: " + record.lockedAt +
                    (string.IsNullOrEmpty(record.reason) ? "" : "\nReason: " + record.reason),
                    mine ? MessageType.Info : MessageType.Warning);
                if (GUILayout.Button("View lock details")) GitLockWindow.OpenWithFilter(record.path);
            }
        }

        private static void DrawScene(SceneView view)
        {
            if (!GitLockService.Enabled) return;
            var warnings = new List<string>();
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
            {
                if (GitLockService.Blocked(stage.assetPath, out var reason))
                    warnings.Add(reason);
            }
            else
            {
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.isLoaded && GitLockService.Blocked(scene.path, out var reason))
                        warnings.Add(reason);
                }
            }
            if (warnings.Count == 0) return;
            Handles.BeginGUI();
            try
            {
                GUILayout.BeginArea(new Rect(12, 36, Mathf.Max(180, Mathf.Min(460, view.position.width - 24)), 130));
                EditorGUILayout.HelpBox("Read-only lock: scene/prefab saving is blocked.\n" +
                    string.Join("\n", warnings.GetRange(0, Math.Min(2, warnings.Count))), MessageType.Warning);
                if (GUILayout.Button("Open Git Lock Manager")) GitLockWindow.Open();
                GUILayout.EndArea();
            }
            finally { Handles.EndGUI(); }
        }
    }
}
