using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AgenLink.GitHub
{
    /// <summary>Drives `gh auth login --web` on a background thread; reports the one-time code and completion.</summary>
    internal sealed class GitHubAuthFlow
    {
        public enum Phase { Idle, Starting, AwaitingCode, AwaitingBrowser, Success, Failed }
        public volatile Phase State = Phase.Idle;
        public string Code = "";
        public string Error = "";

        private Process _proc;
        private static readonly Regex CodeRx = new Regex(@"\b([A-Z0-9]{4}-[A-Z0-9]{4})\b");

        /// <summary>onCode: called (background thread) when the one-time code is known. onDone: called when finished.</summary>
        public void Begin(Action<string> onCode, Action<bool, string> onDone)
        {
            State = Phase.Starting;
            var th = new Thread(() => Run(onCode, onDone)) { IsBackground = true, Name = "AgenLink.GhAuth" };
            th.Start();
        }

        private void Run(Action<string> onCode, Action<bool, string> onDone)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = GitIntegration.GhExePath(),
                    WorkingDirectory = GitIntegration.ProjectRootPath(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                };
                foreach (var a in new[] { "auth", "login", "--hostname", "github.com", "--git-protocol", "https", "--web" })
                    psi.ArgumentList.Add(a);

                _proc = Process.Start(psi);
                State = Phase.AwaitingCode;

                // gh prints the one-time code to stderr; read lines until we find it.
                bool codeSent = false;
                var sb = new StringBuilder();
                string line;
                while ((line = _proc.StandardError.ReadLine()) != null)
                {
                    sb.AppendLine(line);
                    var m = CodeRx.Match(line);
                    if (!codeSent && m.Success)
                    {
                        Code = m.Groups[1].Value;
                        onCode?.Invoke(Code);
                        State = Phase.AwaitingBrowser;
                        // Open the device page and let gh proceed (it also tries to open a browser).
                        try { Process.Start(new ProcessStartInfo { FileName = "https://github.com/login/device", UseShellExecute = true }); } catch { }
                        try { _proc.StandardInput.WriteLine(); } catch { }
                        codeSent = true;
                    }
                }
                _proc.WaitForExit();
                if (_proc.ExitCode == 0) { State = Phase.Success; onDone?.Invoke(true, ""); }
                else { State = Phase.Failed; Error = sb.ToString().Trim(); onDone?.Invoke(false, Error); }
            }
            catch (Exception e)
            {
                State = Phase.Failed; Error = e.Message; onDone?.Invoke(false, e.Message);
            }
        }
    }
}
