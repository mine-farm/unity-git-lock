using System;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Minefarm.GitLock
{
    [InitializeOnLoad]
    internal static class GitLockService
    {
        private sealed class Result
        {
            public GitContext context;
            public LockSnapshot snapshot;
        }

        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 64 * 1024 * 1024
        };
        private static Task<Result> job;
        private static string operation;
        private static double nextPoll;
        private static DateTime updatedAt = DateTime.MinValue;
        private static bool wasFresh;

        public static LockSnapshot Snapshot { get; private set; }
        public static GitContext Context { get; private set; }
        public static string Error { get; private set; } = "";
        public static string Notice { get; private set; } = "";
        public static bool Enabled => GitLockSettings.instance.enabled;
        public static bool Busy => job != null;
        public static bool Fresh => Snapshot != null &&
            (DateTime.UtcNow - updatedAt).TotalSeconds <= Math.Max(30, GitLockSettings.instance.refreshSeconds * 3);
        public static DateTime UpdatedAt => updatedAt;
        public static LockRecord[] Records => Snapshot?.locks ?? Array.Empty<LockRecord>();
        public static event Action Changed;

        static GitLockService()
        {
            EditorApplication.update += Tick;
        }

        public static void Reset()
        {
            if (Busy) throw new InvalidOperationException("Wait for the current lock request.");
            Snapshot = null;
            Context = null;
            updatedAt = DateTime.MinValue;
            Error = Notice = "";
            nextPoll = 0;
            Notify();
        }

        public static bool IsMine(LockRecord record) =>
            record != null && Snapshot != null && record.owner == Snapshot.viewer;

        public static LockRecord Find(string repoPath, bool descendants = false, bool foreignOnly = false)
        {
            if (repoPath == null) return null;
            LockRecord own = null;
            foreach (var record in Records)
            {
                if (!LockPaths.Overlaps(repoPath, descendants, record.path, record.folder)) continue;
                if (!IsMine(record)) return record;
                if (!foreignOnly) own = record;
            }
            return own;
        }

        public static bool IsManagedAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            var key = LockPaths.Key(assetPath.Replace('\\', '/'));
            return key == "assets" || key.StartsWith("assets/", StringComparison.Ordinal)
                || key == "packages" || key.StartsWith("packages/", StringComparison.Ordinal)
                || key == "projectsettings" || key.StartsWith("projectsettings/", StringComparison.Ordinal);
        }

        public static bool TryRepoPath(string assetPath, out string path)
        {
            path = null;
            return Context != null && IsManagedAsset(assetPath) && Context.TryAssetPath(assetPath, out path);
        }

        public static bool Blocked(string assetPath, out string reason, bool descendants = false)
        {
            reason = "";
            if (!Enabled || !IsManagedAsset(assetPath)) return false;
            if (TryRepoPath(assetPath, out var path))
            {
                var record = Find(path, descendants, true);
                if (record != null)
                {
                    reason = "Git Lock: " + record.path + " locked by " + record.Summary;
                    return true;
                }
            }
            if (!Fresh && GitLockSettings.instance.failClosed)
            {
                reason = "Git Lock: lock status is unavailable or stale. Refresh in Tools/Git Lock/Manager.";
                return true;
            }
            return false;
        }

        public static void Refresh() => Start("list", new LockRequest());

        public static void Acquire(LockTarget[] targets, string reason)
        {
            if (targets == null || targets.Length == 0) return;
            Start("acquire", new LockRequest { targets = targets, reason = reason ?? "" });
        }

        public static void Release(string[] ids, bool force = false)
        {
            if (ids == null || ids.Length == 0) return;
            Start("release", new LockRequest { ids = ids, force = force });
        }

        private static void Tick()
        {
            if (job != null && job.IsCompleted)
            {
                var completed = job;
                var op = operation;
                job = null;
                try
                {
                    var result = completed.GetAwaiter().GetResult();
                    Context = result.context;
                    Snapshot = result.snapshot;
                    updatedAt = DateTime.UtcNow;
                    Error = "";
                    if (op != "list") Notice = op == "acquire" ? "Lock acquired." : "Lock released.";
                }
                catch (Exception ex)
                {
                    Error = ex.Message;
                    if (op != "list") Notice = "Request failed: " + ex.Message + " Refresh before retrying.";
                }
                nextPoll = EditorApplication.timeSinceStartup + (op == "list" ? Math.Max(3, GitLockSettings.instance.refreshSeconds) : 1);
                Notify();
            }
            if (wasFresh != Fresh)
            {
                wasFresh = Fresh;
                Notify();
            }
            if (Enabled && !Busy && EditorApplication.timeSinceStartup >= nextPoll)
                Refresh();
        }

        private static void Start(string op, LockRequest request)
        {
            if (!Enabled || Busy) return;
            var settings = GitLockSettings.instance;
            try
            {
                var endpoint = GitLockSettings.ValidateEndpoint(settings.serverUrl);
                var token = GitLockSettings.Token;
                if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Set your personal lock token in the Manager.");
                var repo = settings.repository.Trim();
                if (string.IsNullOrEmpty(repo)) throw new InvalidOperationException("Set the shared repository key in the Manager.");
                var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                request.repository = repo;
                operation = op;
                job = Task.Run(async () =>
                {
                    var context = GitContext.Read(project);
                    if (op == "acquire")
                    {
                        request.branch = context.Branch;
                        request.commit = context.Commit;
                    }
                    var uri = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/v1/locks/" + op);
                    using (var message = new HttpRequestMessage(HttpMethod.Post, uri))
                    {
                        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        message.Content = new StringContent(JsonUtility.ToJson(request), Encoding.UTF8, "application/json");
                        using (var response = await Http.SendAsync(message).ConfigureAwait(false))
                        {
                            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (!response.IsSuccessStatusCode)
                            {
                                var error = "HTTP " + (int)response.StatusCode;
                                try
                                {
                                    var detail = JsonUtility.FromJson<ApiError>(body);
                                    if (!string.IsNullOrEmpty(detail?.error)) error += ": " + detail.error;
                                }
                                catch (Exception) { }
                                throw new InvalidOperationException(error);
                            }
                            var snapshot = JsonUtility.FromJson<LockSnapshot>(body);
                            if (snapshot == null || snapshot.repository != repo || string.IsNullOrEmpty(snapshot.viewer)
                                || snapshot.locks == null || snapshot.locks.Any(r => r == null || !LockPaths.IsValid(r.path)
                                    || string.IsNullOrEmpty(r.id) || string.IsNullOrEmpty(r.owner)))
                                throw new InvalidOperationException("Invalid lock server response.");
                            return new Result { context = context, snapshot = snapshot };
                        }
                    }
                });
                if (op != "list") Notice = "Submitting " + op + "...";
                Notify();
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                if (op != "list") Notice = "Request failed: " + ex.Message;
                nextPoll = EditorApplication.timeSinceStartup + Math.Max(3, settings.refreshSeconds);
                Notify();
            }
        }

        private static void Notify()
        {
            EditorApplication.RepaintProjectWindow();
            SceneView.RepaintAll();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            Changed?.Invoke();
        }
    }
}
