using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgenLink.Ops
{
    /// <summary>
    /// Save / open / create scenes. open and create refuse to discard unsaved changes unless force:true, and
    /// never pop the modal "save?" dialog (it would freeze the bridge). save writes to disk — the CLI is told
    /// to call it only once the user agrees. All actions refuse while in play mode.
    /// </summary>
    internal static class SceneOps
    {
        public static string Manage(CommandHandlers.RequestParams p)
        {
            string action = (p.action ?? "").ToLowerInvariant();
            switch (action)
            {
                case "save":
                {
                    Scene active = SceneManager.GetActiveScene();
                    string path = string.IsNullOrEmpty(p.path) ? active.path : p.path;
                    if (string.IsNullOrEmpty(path))
                        throw new Exception("the active scene is untitled — pass a path like 'Assets/Scenes/Main.unity'");
                    bool ok = string.IsNullOrEmpty(p.path)
                        ? EditorSceneManager.SaveScene(active)
                        : EditorSceneManager.SaveScene(active, p.path);
                    if (!ok) throw new Exception("Unity declined to save the scene");
                    return new JObj().S("action", "save").S("path", path).B("ok", true).Build();
                }
                case "open":
                {
                    RequireEditMode();
                    if (string.IsNullOrEmpty(p.path)) throw new Exception("open requires a scene path");
                    GuardUnsaved(p.force);
                    Scene s = EditorSceneManager.OpenScene(p.path, p.additive ? OpenSceneMode.Additive : OpenSceneMode.Single);
                    return new JObj().S("action", "open").S("path", s.path).S("name", s.name).B("additive", p.additive).Build();
                }
                case "create":
                {
                    RequireEditMode();
                    GuardUnsaved(p.force);
                    Scene s = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects,
                        p.additive ? NewSceneMode.Additive : NewSceneMode.Single);
                    string savedPath = null;
                    if (!string.IsNullOrEmpty(p.path))
                    {
                        if (!EditorSceneManager.SaveScene(s, p.path)) throw new Exception("created the scene but could not save it to " + p.path);
                        savedPath = p.path;
                    }
                    return new JObj().S("action", "create").S("name", s.name).S("path", savedPath)
                        .S("note", savedPath == null ? "New untitled scene — save it with action:'save' and a path." : null).Build();
                }
                default:
                    throw new Exception("manage_scene action must be 'save', 'open', or 'create'");
            }
        }

        /// <summary>Loaded scenes for get_project_info.</summary>
        public static string ScenesJson()
        {
            var elems = new List<string>();
            Scene active = SceneManager.GetActiveScene();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                elems.Add(new JObj()
                    .S("name", s.name).S("path", s.path)
                    .B("isActive", s == active).B("isLoaded", s.isLoaded).B("isDirty", s.isDirty)
                    .Build());
            }
            return Json.Arr(elems);
        }

        private static void RequireEditMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new Exception("cannot change scenes while in play mode — stop play mode first (agen_playmode stop)");
        }

        private static void GuardUnsaved(bool force)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty && !force)
                    throw new Exception("a loaded scene has unsaved changes. Save it (agen_manage_scene save) or pass force:true to discard.");
        }
    }
}
