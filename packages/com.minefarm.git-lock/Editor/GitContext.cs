using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Minefarm.GitLock
{
    internal sealed class GitContext
    {
        public string Root { get; private set; }
        public string ProjectPrefix { get; private set; }
        public string Branch { get; private set; }
        public string Commit { get; private set; }

        public static GitContext Read(string projectRoot)
        {
            var root = Full(Run(projectRoot, "rev-parse --show-toplevel"));
            var project = Full(projectRoot);
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string prefix;
            if (string.Equals(root, project, comparison)) prefix = "";
            else if (project.StartsWith(root + "/", comparison)) prefix = project.Substring(root.Length + 1) + "/";
            else throw new InvalidOperationException("Unity project is not inside the Git worktree.");

            var commit = Run(projectRoot, "rev-parse --verify HEAD");
            var branch = Run(projectRoot, "symbolic-ref --quiet --short HEAD", true);
            var confirmed = Run(projectRoot, "rev-parse --verify HEAD");
            if (confirmed != commit) throw new InvalidOperationException("Git HEAD changed; retry.");
            return new GitContext
            {
                Root = root, ProjectPrefix = prefix, Commit = commit,
                Branch = string.IsNullOrEmpty(branch) ? "(detached HEAD)" : branch
            };
        }

        private static string Full(string path) => Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');

        public bool TryAssetPath(string assetPath, out string path)
        {
            path = null;
            if (string.IsNullOrEmpty(assetPath)) return false;
            var relative = assetPath.Replace('\\', '/');
            if (!LockPaths.IsValid(relative)) return false;
            path = ProjectPrefix + relative;
            return LockPaths.IsValid(path);
        }

        private static string Run(string cwd, string arguments, bool allowDetached = false)
        {
            var start = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            using (var process = new Process { StartInfo = start })
            {
                try { process.Start(); }
                catch (Exception) { throw new InvalidOperationException("Cannot start Git. Install Git and add it to PATH."); }
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(); } catch (Exception) { }
                    throw new TimeoutException("Git command timed out.");
                }
                if (!Task.WaitAll(new Task[] { output, error }, 2000))
                    throw new TimeoutException("Git output timed out.");
                if (process.ExitCode != 0)
                {
                    if (allowDetached && process.ExitCode == 1) return "";
                    throw new InvalidOperationException("Git " + arguments + " failed. Open a committed Git worktree.");
                }
                return output.Result.TrimEnd('\r', '\n');
            }
        }
    }
}
