using System;
using System.Linq;

namespace Minefarm.GitLock
{
    [Serializable]
    public sealed class LockRecord
    {
        public string id, path, owner, branch, commit, lockedAt, reason;
        public bool folder;
        public string ShortCommit => string.IsNullOrEmpty(commit) ? "?" : commit.Substring(0, Math.Min(8, commit.Length));
        public string Summary => owner + " | " + branch + " | " + ShortCommit;
    }

    [Serializable]
    internal sealed class LockTarget
    {
        public string path;
        public bool folder;
        public LockTarget(string path, bool folder) { this.path = path; this.folder = folder; }
    }

    [Serializable]
    internal sealed class LockRequest
    {
        public string repository, branch, commit, reason;
        public LockTarget[] targets;
        public string[] ids;
        public bool force;
    }

    [Serializable]
    internal sealed class LockSnapshot
    {
        public string repository, viewer;
        public bool admin;
        public LockRecord[] locks;
    }

    [Serializable]
    internal sealed class ApiError { public string error; }

    public static class LockPaths
    {
        public static bool IsValid(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 ||
                path.StartsWith("/", StringComparison.Ordinal) ||
                path.IndexOfAny(new[] { '\\', ':' }) >= 0 ||
                path.Any(c => c < 32 || c == 127))
                return false;
            return !path.Split('/').Any(p => p == "" || p == "." || p == ".." || AsciiLower(p) == ".git");
        }

        private static string AsciiLower(string value)
        {
            var chars = value.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                if (chars[i] >= 'A' && chars[i] <= 'Z') chars[i] = (char)(chars[i] + 32);
            return new string(chars);
        }

        public static string Key(string path)
        {
            var value = AsciiLower(path);
            return value.EndsWith(".meta", StringComparison.Ordinal)
                ? value.Substring(0, value.Length - 5) : value;
        }

        public static bool Overlaps(string a, bool aFolder, string b, bool bFolder)
        {
            a = Key(a); b = Key(b);
            return a == b || (aFolder && b.StartsWith(a + "/", StringComparison.Ordinal))
                || (bFolder && a.StartsWith(b + "/", StringComparison.Ordinal));
        }
    }
}
