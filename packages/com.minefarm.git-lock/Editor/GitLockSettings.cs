using System;
using UnityEditor;

namespace Minefarm.GitLock
{
    [FilePath("ProjectSettings/UnityGitLockSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class GitLockSettings : ScriptableSingleton<GitLockSettings>
    {
        public bool enabled;
        public string serverUrl = "http://127.0.0.1:8765";
        public string repository = "";
        public int refreshSeconds = 10;
        public bool failClosed = true;

        // Only non-secret project configuration is serialized.
        public void Persist() => Save(true);

        private const string TokenKey = "Minefarm.GitLock.SessionToken";
        public static string Token
        {
            get
            {
                var session = SessionState.GetString(TokenKey, "");
                return string.IsNullOrWhiteSpace(session)
                    ? Environment.GetEnvironmentVariable("UNITY_GIT_LOCK_TOKEN") ?? ""
                    : session;
            }
        }

        public static void SetSessionToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) SessionState.EraseString(TokenKey);
            else SessionState.SetString(TokenKey, value.Trim());
        }

        public static Uri ValidateEndpoint(string endpoint)
        {
            if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
                || !(uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback))
                || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("Use an HTTPS server URL (HTTP is allowed only on loopback).");
            return uri;
        }
    }
}
