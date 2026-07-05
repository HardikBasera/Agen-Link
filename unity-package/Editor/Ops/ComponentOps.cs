using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AgenLink.Ops
{
    /// <summary>Add or remove a component on a scene GameObject (Undo-able, scene marked dirty, not saved).</summary>
    internal static class ComponentOps
    {
        private const string UndoLabel = "Agen-Link component";

        public static string Manage(CommandHandlers.RequestParams p)
        {
            GameObject go = ObjectResolver.ResolveGameObject(p.target);
            string action = (p.action ?? "").ToLowerInvariant();
            switch (action)
            {
                case "add":
                {
                    Type t = TypeResolver.RequireComponentType(p.componentType);
                    if (t == typeof(Transform)) throw new Exception("every GameObject already has a Transform");
                    Component added = Undo.AddComponent(go, t);
                    if (added == null) throw new Exception($"could not add {t.Name} (it may conflict with an existing component)");
                    EditorSceneManager.MarkSceneDirty(go.scene);
                    return new JObj()
                        .S("action", "add").S("type", added.GetType().Name)
                        .N("instanceID", added.GetInstanceID()).N("gameObject", go.GetInstanceID())
                        .Raw("properties", PropertyReader.ReadComponentProperties(added, 2))
                        .Build();
                }
                case "remove":
                {
                    Component comp = ObjectResolver.ResolveComponent(go, p.componentType, p.index);
                    if (comp is Transform) throw new Exception("the Transform component cannot be removed");
                    string typeName = comp.GetType().Name;
                    Undo.DestroyObjectImmediate(comp);
                    EditorSceneManager.MarkSceneDirty(go.scene);
                    return new JObj().S("action", "remove").S("type", typeName).N("gameObject", go.GetInstanceID()).B("ok", true).Build();
                }
                default:
                    throw new Exception("manage_component action must be 'add' or 'remove'");
            }
        }
    }
}
