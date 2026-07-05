using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AgenLink.Ops
{
    /// <summary>Editor-level actions: run a menu item, drive play mode, and set/ping the selection.</summary>
    internal static class EditorOps
    {
        public static string MenuItem(CommandHandlers.RequestParams p)
        {
            if (string.IsNullOrEmpty(p.menuPath)) throw new Exception("execute_menu_item requires menuPath, e.g. 'GameObject/UI/Button'");
            if (!EditorApplication.ExecuteMenuItem(p.menuPath))
                throw new Exception($"menu item '{p.menuPath}' was not found or is currently disabled");
            return new JObj().S("menuPath", p.menuPath).B("executed", true).Build();
        }

        public static string PlayMode(CommandHandlers.RequestParams p)
        {
            string action = (p.action ?? "status").ToLowerInvariant();
            switch (action)
            {
                case "play":
                    if (EditorApplication.isCompiling) throw new Exception("still compiling — wait for compilation to finish before entering play mode");
                    EditorApplication.isPlaying = true;
                    break;
                case "stop": EditorApplication.isPlaying = false; break;
                case "pause": EditorApplication.isPaused = true; break;
                case "unpause": EditorApplication.isPaused = false; break;
                case "step": EditorApplication.Step(); break;
                case "status": break;
                default: throw new Exception("playmode action must be play, stop, pause, unpause, step, or status");
            }
            return new JObj()
                .S("action", action)
                .B("isPlaying", EditorApplication.isPlaying)
                .B("isPaused", EditorApplication.isPaused)
                .B("isCompiling", EditorApplication.isCompiling)
                .S("note", action == "play" || action == "stop"
                    ? "This triggers a domain reload — the bridge drops for a few seconds. Poll {action:'status'} until it responds."
                    : null)
                .Build();
        }

        public static string SetSelection(CommandHandlers.RequestParams p)
        {
            if (p.targets == null || p.targets.Length == 0) throw new Exception("set_selection requires targets: [ref, ...]");
            var objs = new List<UnityEngine.Object>();
            var names = new List<string>();
            foreach (string t in p.targets)
            {
                UnityEngine.Object o = t.StartsWith("Assets/") || t.StartsWith("Packages/")
                    ? ObjectResolver.ResolveAsset(t)
                    : ObjectResolver.ResolveGameObject(t);
                objs.Add(o);
                names.Add(Json.Str(o.name));
            }
            Selection.objects = objs.ToArray();
            if (p.ping && objs.Count > 0) EditorGUIUtility.PingObject(objs[0]);
            return new JObj().N("selected", objs.Count).Raw("names", Json.Arr(names)).B("pinged", p.ping).Build();
        }
    }
}
