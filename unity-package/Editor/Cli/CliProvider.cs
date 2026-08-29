using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using AgenLink.History;

namespace AgenLink.Cli
{
    /// <summary>
    /// One AI CLI the embedded terminal can launch. Everything that used to be a
    /// claude-or-antigravity conditional — exe resolution, argv, Settings copy, History colours —
    /// lives behind this type, so adding a CLI is (almost) one new file.
    /// </summary>
    internal abstract class CliProvider
    {
        /// <summary>Stable id. Persisted in EditorPrefs and written into sessions.jsonl — never rename.</summary>
        public abstract string Id { get; }

        /// <summary>Name shown in the Settings dropdown and the History badge.</summary>
        public abstract string DisplayName { get; }

        /// <summary>Settings section heading, e.g. "Antigravity CLI (agy)".</summary>
        public abstract string SettingsHeading { get; }

        /// <summary>Executable file name — used for the Browse dialog title and the PATH scan.</summary>
        public abstract string ExeFileName { get; }

        /// <summary>Manual path override from Settings (empty = auto-detect).</summary>
        public abstract string PathOverride { get; set; }

        /// <summary>Settings mini-label describing where auto-detection looks.</summary>
        public abstract string AutoDetectHint { get; }

        /// <summary>History: how to reopen a session whose content this CLI keeps in its own store.</summary>
        public abstract string ResumeHint { get; }

        /// <summary>History badge / card accent.</summary>
        public abstract Color AccentColor { get; }

        /// <summary>Resolve the executable. Throws with an install hint when it cannot be found.</summary>
        public abstract string ResolveExe();

        /// <summary>Wire this CLI's Unity bridge and return its argv. Never throws.</summary>
        public abstract List<string> BuildArgs();

        /// <summary>Conversations for the History tab. Never throws; returns empty on failure.</summary>
        public abstract List<Conversation> LoadHistory(string projectRoot);

        /// <summary>Non-throwing description for the Settings label.</summary>
        public string ResolveDisplay()
        {
            try { return ResolveExe(); }
            catch (Exception e) { return "(not found) " + e.Message; }
        }

        /// <summary>First match for <paramref name="exeFileName"/> on PATH, or null.</summary>
        protected static string ScanPath(string exeFileName)
        {
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string candidate = Path.Combine(dir.Trim(), exeFileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* malformed PATH entry */ }
            }
            return null;
        }

        /// <summary>
        /// Whether <c>&lt;exe&gt; --help</c> mentions <paramref name="needle"/>. Old CLIs abort on an
        /// unknown flag, so capabilities are sniffed once and cached in SessionState for the session.
        /// </summary>
        protected static bool HelpMentions(string exePath, string needle, string sessionKey)
        {
            string cached = SessionState.GetString(sessionKey, "");
            if (cached == "1") return true;
            if (cached == "0") return false;

            bool supported = false;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--help",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var proc = Process.Start(psi))
                {
                    string help = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                    if (!proc.WaitForExit(4000)) { try { proc.Kill(); } catch { /* ignore */ } }
                    supported = help.Contains(needle);
                }
            }
            catch { supported = false; }

            SessionState.SetString(sessionKey, supported ? "1" : "0");
            return supported;
        }

        /// <summary>
        /// Shared steering that outranks a model's default "ask the user / write a script" habits.
        /// Delivered per-CLI: Claude takes it via --append-system-prompt, Codex via
        /// -c developer_instructions. Antigravity relies on AGENTS.md alone.
        /// </summary>
        public const string SystemPrompt =
            "You are connected to a LIVE Unity Editor via agen_* MCP tools that read AND modify it. Rules: query " +
            "editor state with tools (agen_get_scene_hierarchy, agen_find_gameobjects, agen_get_gameobject, " +
            "agen_read_console) instead of asking the user; perform editor operations with tools " +
            "(agen_create_gameobject, agen_set_component_properties, agen_manage_scene, agen_playmode) instead of " +
            "writing editor scripts; call agen_get_gameobject before setting properties; after editing .cs run " +
            "agen_refresh_assets then poll agen_get_compile_errors; after play-mode changes the bridge reconnects " +
            "within seconds — retry, don't abandon tools; scene edits are unsaved — ask before saving.";
    }
}
