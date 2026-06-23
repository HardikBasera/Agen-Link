using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor.PackageManager;

namespace AgenLink
{
    /// <summary>
    /// Thin wrapper over the <c>git</c> and <c>gh</c> CLIs for whole-project GitHub backup with safe,
    /// account-aware pushing. Quick/local operations run synchronously; network operations (push, repo
    /// create) run on a background thread and report back on the main thread.
    /// </summary>
    internal static class GitIntegration
    {
        public struct Result
        {
            public bool ok;
            public int code;
            public string stdout;
            public string stderr;
            public string Message => string.IsNullOrWhiteSpace(stderr) ? (stdout ?? "") : stderr.Trim();
        }

        // ----- executable resolution (Unity may have a stale PATH; check common install dirs too) -----

        private static string GitExe => ResolveExe("git.exe", new[]
        {
            @"C:\Program Files\Git\cmd\git.exe",
            @"C:\Program Files\Git\bin\git.exe"
        });

        private static string GhExe => ResolveExe("gh.exe", new[]
        {
            @"C:\Program Files\GitHub CLI\gh.exe",
            @"C:\Program Files (x86)\GitHub CLI\gh.exe"
        });

        private static string ResolveExe(string exe, string[] common)
        {
            foreach (var c in common) if (File.Exists(c)) return c;
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try { string p = Path.Combine(dir.Trim(), exe); if (File.Exists(p)) return p; }
                catch { /* malformed PATH entry */ }
            }
            return exe; // let Process try; it will fail with a clear message if missing
        }

        // ----- low-level process runner -----

        private static Result RunSync(string file, string workdir, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                WorkingDirectory = workdir ?? ConfigBuilder.ProjectRoot(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            try
            {
                using (var p = Process.Start(psi))
                {
                    // Read both streams concurrently to avoid pipe-buffer deadlocks.
                    var soTask = p.StandardOutput.ReadToEndAsync();
                    var seTask = p.StandardError.ReadToEndAsync();
                    p.WaitForExit();
                    string so = soTask.GetAwaiter().GetResult();
                    string se = seTask.GetAwaiter().GetResult();
                    return new Result { ok = p.ExitCode == 0, code = p.ExitCode, stdout = so, stderr = se };
                }
            }
            catch (Exception e)
            {
                return new Result { ok = false, code = -1, stderr = $"{e.Message} (is '{file}' installed and on PATH?)" };
            }
        }

        private static Result RunGit(string workdir, params string[] args) => RunSync(GitExe, workdir, args);
        private static Result RunGh(string workdir, params string[] args) => RunSync(GhExe, workdir, args);

        private static void RunBg(Func<Result> work, Action<Result> done)
        {
            var th = new Thread(() =>
            {
                Result r;
                try { r = work(); }
                catch (Exception e) { r = new Result { ok = false, code = -1, stderr = e.Message }; }
                MainThreadDispatcher.RunAsync(() => done(r));
            }) { IsBackground = true, Name = "AgenLink.Git" };
            th.Start();
        }

        // ----- repo state -----

        public static bool IsGitRepo()
        {
            return Directory.Exists(Path.Combine(ConfigBuilder.ProjectRoot(), ".git"));
        }

        public static string CurrentBranch()
        {
            var r = RunGit(null, "rev-parse", "--abbrev-ref", "HEAD");
            return r.ok ? r.stdout.Trim() : "";
        }

        public static int PendingChangeCount()
        {
            var r = RunGit(null, "status", "--porcelain");
            if (!r.ok || string.IsNullOrWhiteSpace(r.stdout)) return 0;
            int count = 0;
            foreach (var line in r.stdout.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line)) count++;
            return count;
        }

        public static string RemoteUrl()
        {
            var r = RunGit(null, "remote", "get-url", "origin");
            return r.ok ? r.stdout.Trim() : "";
        }

        public static List<string> RecentCommits(int n)
        {
            var list = new List<string>();
            var r = RunGit(null, "log", $"-{n}", "--pretty=format:%h  %ad  %s", "--date=short");
            if (r.ok)
                foreach (var line in r.stdout.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) list.Add(line.Trim());
            return list;
        }

        // ----- init / commit -----

        public static Result InitRepo(string branch)
        {
            string root = ConfigBuilder.ProjectRoot();
            var init = RunGit(root, "init", "-b", string.IsNullOrEmpty(branch) ? "main" : branch);
            if (!init.ok)
            {
                // Older git without -b: init then rename the branch.
                init = RunGit(root, "init");
                if (!init.ok) return init;
                RunGit(root, "symbolic-ref", "HEAD", $"refs/heads/{(string.IsNullOrEmpty(branch) ? "main" : branch)}");
            }

            EnsureGitignore(root);
            RunGit(root, "add", "-A");
            var commit = RunGit(root, "commit", "-m", "Initial commit (Agen-Link)");
            if (!commit.ok && !LooksLikeNothingToCommit(commit)) return commit;
            return new Result { ok = true, stdout = "Initialized git repository with a Unity .gitignore." };
        }

        public static Result AutoCommit(string message)
        {
            if (!IsGitRepo()) return new Result { ok = false, stderr = "Not a git repository yet (use the GitHub tab to set it up)." };
            string root = ConfigBuilder.ProjectRoot();
            RunGit(root, "add", "-A");
            string msg = string.IsNullOrWhiteSpace(message) ? "Agen-Link change" : Trim(message, 200);
            var commit = RunGit(root, "commit", "-m", msg);
            if (!commit.ok && LooksLikeNothingToCommit(commit))
                return new Result { ok = true, stdout = "No changes to commit." };
            return commit;
        }

        public static Result SetIdentity(string name, string email)
        {
            string root = ConfigBuilder.ProjectRoot();
            if (!string.IsNullOrWhiteSpace(name)) RunGit(root, "config", "user.name", name);
            if (!string.IsNullOrWhiteSpace(email)) RunGit(root, "config", "user.email", email);
            return new Result { ok = true, stdout = "Identity set for this repository." };
        }

        // ----- network (background) -----

        public static void PushAsync(string owner, string repo, string branch, Action<Result> done)
        {
            RunBg(() =>
            {
                string root = ConfigBuilder.ProjectRoot();
                EnsureRemote(root, owner, repo);
                return RunGit(root, "push", "-u", "origin", string.IsNullOrEmpty(branch) ? "main" : branch);
            }, done);
        }

        /// <summary>Create a repo with a description and optional immediate push.</summary>
        public static void CreateRepoAsync(string owner, string repo, bool isPrivate, string description, bool push, Action<Result> done)
        {
            RunBg(() =>
            {
                string root = ConfigBuilder.ProjectRoot();
                if (!IsGitRepo()) InitRepo("main");
                string target = string.IsNullOrWhiteSpace(owner) ? repo : $"{owner}/{repo}";
                var args = new List<string>
                { "repo", "create", target, "--source", ".", "--remote", "origin", isPrivate ? "--private" : "--public" };
                if (!string.IsNullOrWhiteSpace(description)) { args.Add("--description"); args.Add(description); }
                if (push) args.Add("--push");
                return RunGh(root, args.ToArray());
            }, done);
        }

        public enum LinkOutcome { LinkedClean, LinkedRemoteAhead, LinkedLocalAhead, LinkedDiverged, Failed }

        public struct LinkResult { public LinkOutcome outcome; public string message; }

        /// <summary>Point origin at github.com/&lt;nameWithOwner&gt;, fetch, and classify the history relationship.</summary>
        public static void LinkExistingAsync(string nameWithOwner, string branch, Action<LinkResult> done)
        {
            RunBg(() =>
            {
                string root = ConfigBuilder.ProjectRoot();
                if (!IsGitRepo()) InitRepo(branch);
                int slash = nameWithOwner.IndexOf('/');
                string owner = slash > 0 ? nameWithOwner.Substring(0, slash) : "";
                string repo = slash > 0 ? nameWithOwner.Substring(slash + 1) : nameWithOwner;
                EnsureRemote(root, owner, repo);
                RunGit(root, "fetch", "origin");

                string br = string.IsNullOrEmpty(branch) ? CurrentBranch() : branch;
                bool localHas = RunGit(root, "rev-parse", "--verify", "HEAD").ok;
                bool remoteHas = RunGit(root, "rev-parse", "--verify", "origin/" + br).ok;

                LinkOutcome o;
                if (!remoteHas || !localHas) o = LinkOutcome.LinkedClean;
                else
                {
                    bool remoteIsAncestor = RunGit(root, "merge-base", "--is-ancestor", "origin/" + br, "HEAD").ok;
                    bool localIsAncestor = RunGit(root, "merge-base", "--is-ancestor", "HEAD", "origin/" + br).ok;
                    if (localIsAncestor && !remoteIsAncestor) o = LinkOutcome.LinkedRemoteAhead;
                    else if (remoteIsAncestor && !localIsAncestor) o = LinkOutcome.LinkedLocalAhead;
                    else if (!remoteIsAncestor && !localIsAncestor) o = LinkOutcome.LinkedDiverged;
                    else o = LinkOutcome.LinkedClean;
                }
                return new Result { ok = true, stdout = o.ToString() };
            }, r =>
            {
                LinkOutcome oc; Enum.TryParse(r.stdout, out oc);
                string msg = oc switch
                {
                    LinkOutcome.LinkedClean => "Linked. origin set; histories compatible.",
                    LinkOutcome.LinkedRemoteAhead => "Linked. Remote has commits you don't — pull before pushing.",
                    LinkOutcome.LinkedLocalAhead => "Linked. You have local commits to push.",
                    LinkOutcome.LinkedDiverged => "Linked, but local and remote have DIVERGED. Reconcile manually (pull/rebase) before pushing — no auto-merge performed.",
                    _ => "Link failed.",
                };
                done(new LinkResult { outcome = oc, message = msg });
            });
        }

        // ----- gh accounts -----

        public static bool GhInstalled() => RunGh(null, "--version").ok;

        public static string GhActiveAccount()
        {
            var r = RunGh(null, "api", "user", "--jq", ".login");
            return r.ok ? r.stdout.Trim() : null;
        }

        public static List<string> GhAccounts()
        {
            var r = RunGh(null, "auth", "status");
            string combined = (r.stdout ?? "") + "\n" + (r.stderr ?? "");
            var accounts = new List<string>();
            foreach (Match m in Regex.Matches(combined, @"account\s+(\S+)"))
            {
                string name = m.Groups[1].Value.Trim();
                if (name.Length > 0 && !accounts.Contains(name)) accounts.Add(name);
            }
            return accounts;
        }

        public static Result GhSwitch(string user) => RunGh(null, "auth", "switch", "--hostname", "github.com", "--user", user);

        public static Result GhSetupGit() => RunGh(null, "auth", "setup-git");

        /// <summary>Repos for the active account (and orgs gh has access to), newest first.</summary>
        public static List<AgenLink.GitHub.GitHubRepo> GhListRepos(int limit = 200)
        {
            var r = RunGh(null, "repo", "list", "--limit", limit.ToString(),
                          "--json", "nameWithOwner,visibility,updatedAt");
            return r.ok ? AgenLink.GitHub.GitHubRepo.ParseList(r.stdout) : new List<AgenLink.GitHub.GitHubRepo>();
        }

        public static void GhListReposAsync(int limit, Action<List<AgenLink.GitHub.GitHubRepo>> done)
        {
            List<AgenLink.GitHub.GitHubRepo> captured = null;
            RunBg(() => { captured = GhListRepos(limit); return new Result { ok = true }; },
                  _ => done(captured ?? new List<AgenLink.GitHub.GitHubRepo>()));
        }

        public static Result GhSignOut() => RunGh(null, "auth", "logout", "--hostname", "github.com");

        /// <summary>Resolved gh path, for the browser sign-in flow's own Process.</summary>
        public static string GhExePath() => GhExe;

        /// <summary>Project root, for the sign-in flow's working directory.</summary>
        public static string ProjectRootPath() => ConfigBuilder.ProjectRoot();

        // ----- helpers -----

        private static void EnsureRemote(string root, string owner, string repo)
        {
            string url = $"https://github.com/{owner}/{repo}.git";
            var get = RunGit(root, "remote", "get-url", "origin");
            if (get.ok) RunGit(root, "remote", "set-url", "origin", url);
            else RunGit(root, "remote", "add", "origin", url);
        }

        private static void EnsureGitignore(string root)
        {
            string gi = Path.Combine(root, ".gitignore");
            if (!File.Exists(gi))
            {
                string tpl = GetTemplatePath();
                if (tpl != null && File.Exists(tpl)) File.Copy(tpl, gi);
                else File.WriteAllText(gi,
                    "[Ll]ibrary/\n[Tt]emp/\n[Oo]bj/\n[Bb]uild/\n[Bb]uilds/\n[Ll]ogs/\n[Uu]ser[Ss]ettings/\n" +
                    "*.csproj\n*.sln\n.vs/\n.idea/\n.vscode/\nAgenLink~/\n", new UTF8Encoding(false));
            }
            else
            {
                string text = File.ReadAllText(gi);
                if (!text.Contains("AgenLink~"))
                    File.AppendAllText(gi, "\n# Agen-Link local history\nAgenLink~/\n");
            }
        }

        private static string GetTemplatePath()
        {
            try
            {
                var pi = PackageInfo.FindForAssembly(typeof(BridgeSettings).Assembly);
                if (pi != null && !string.IsNullOrEmpty(pi.resolvedPath))
                {
                    string p = Path.Combine(pi.resolvedPath, "templates", "unity.gitignore");
                    if (File.Exists(p)) return p;
                }
            }
            catch { /* not a package */ }
            return null;
        }

        private static bool LooksLikeNothingToCommit(Result r)
        {
            string s = ((r.stdout ?? "") + (r.stderr ?? "")).ToLowerInvariant();
            return s.Contains("nothing to commit") || s.Contains("nothing added to commit");
        }

        private static string Trim(string s, int max)
        {
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }
}
