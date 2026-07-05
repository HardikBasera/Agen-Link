using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AgenLink.Ops
{
    /// <summary>
    /// Sets serialized fields on a component the same way the Inspector does — SerializedProperty first
    /// (Inspector-accurate, Undo via ApplyModifiedProperties), falling back to public property/field
    /// reflection for anything the serialized model doesn't expose. Value shapes mirror the Inspector:
    /// numbers/bools/strings, enum names, Vector/Color/Quaternion as arrays or objects, object references by
    /// instanceID / asset path / guid / scene path, arrays as JSON arrays, nested structs as objects.
    /// Failures are reported per-property; the rest still apply.
    /// </summary>
    internal static class PropertyEngine
    {
        private const string UndoLabel = "Agen-Link set properties";

        public static string Set(string requestLine)
        {
            var req = JObject.Parse(requestLine);
            JToken pr = req["params"];
            string target = (string)pr?["target"];
            string componentType = (string)pr?["componentType"];
            int componentIndex = (int?)pr?["componentIndex"] ?? 0;
            if (!(pr?["properties"] is JObject props) || !props.HasValues)
                throw new Exception("set_component_properties requires params.properties: {name: value, ...}");

            GameObject go = ObjectResolver.ResolveGameObject(target);
            Component comp = ObjectResolver.ResolveComponent(go, componentType, componentIndex);

            var applied = new List<string>();
            var failed = new List<KeyValuePair<string, string>>();

            var so = new SerializedObject(comp);
            so.Update();

            // Split keys: those the serialized model exposes vs. those needing reflection. Doing all serialized
            // sets and ONE ApplyModifiedProperties before any reflection set avoids Apply clobbering a
            // reflection change (Apply writes back the whole cached serialized state).
            var reflectionKeys = new List<JProperty>();
            foreach (JProperty jp in props.Properties())
            {
                SerializedProperty prop = FindProperty(so, jp.Name);
                if (prop == null) { reflectionKeys.Add(jp); continue; }
                try { ApplyToken(prop, jp.Value); applied.Add(jp.Name); }
                catch (Exception e) { failed.Add(new KeyValuePair<string, string>(jp.Name, e.Message)); }
            }
            so.ApplyModifiedProperties();

            foreach (JProperty jp in reflectionKeys)
            {
                try { ApplyReflection(comp, jp.Name, jp.Value); applied.Add(jp.Name); }
                catch (Exception e) { failed.Add(new KeyValuePair<string, string>(jp.Name, e.Message)); }
            }

            bool isSceneObject = !EditorUtility.IsPersistent(comp);
            if (isSceneObject) EditorSceneManager.MarkSceneDirty(go.scene);
            else EditorUtility.SetDirty(comp);

            var appliedArr = new List<string>();
            foreach (string a in applied) appliedArr.Add(Json.Str(a));
            var failedArr = new List<string>();
            foreach (KeyValuePair<string, string> f in failed)
                failedArr.Add(new JObj().S("property", f.Key).S("error", f.Value).Build());

            return new JObj()
                .S("component", comp.GetType().Name)
                .N("instanceID", comp.GetInstanceID())
                .N("appliedCount", applied.Count)
                .Raw("applied", Json.Arr(appliedArr))
                .Raw("failed", Json.Arr(failedArr))
                .B("sceneDirty", isSceneObject)
                .S("note", isSceneObject ? "Scene not saved — review (Ctrl+Z reverts), then save if wanted." : null)
                .Build();
        }

        /// <summary>Exact name, then the common m_-prefixed serialized names (mass -> m_Mass).</summary>
        private static SerializedProperty FindProperty(SerializedObject so, string key)
        {
            return so.FindProperty(key)
                ?? so.FindProperty("m_" + char.ToUpperInvariant(key[0]) + key.Substring(1))
                ?? so.FindProperty("m_" + key);
        }

        private static SerializedProperty FindRelative(SerializedProperty parent, string key)
        {
            return parent.FindPropertyRelative(key)
                ?? parent.FindPropertyRelative("m_" + char.ToUpperInvariant(key[0]) + key.Substring(1))
                ?? parent.FindPropertyRelative("m_" + key);
        }

        private static void ApplyToken(SerializedProperty prop, JToken token)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer: prop.longValue = token.ToObject<long>(); break;
                case SerializedPropertyType.Boolean: prop.boolValue = token.ToObject<bool>(); break;
                case SerializedPropertyType.Float: prop.doubleValue = token.ToObject<double>(); break;
                case SerializedPropertyType.String: prop.stringValue = token.Type == JTokenType.Null ? "" : (string)token; break;
                case SerializedPropertyType.Character: prop.intValue = token.ToObject<int>(); break;
                case SerializedPropertyType.LayerMask: prop.intValue = ToLayer(token); break;
                case SerializedPropertyType.Enum: SetEnum(prop, token); break;
                case SerializedPropertyType.Color: prop.colorValue = ToColor(token); break;
                case SerializedPropertyType.Vector2: prop.vector2Value = ToVector2(token); break;
                case SerializedPropertyType.Vector3: prop.vector3Value = ToVector3(token); break;
                case SerializedPropertyType.Vector4: prop.vector4Value = ToVector4(token); break;
                case SerializedPropertyType.Quaternion: prop.quaternionValue = ToQuaternion(token); break;
                case SerializedPropertyType.Vector2Int: { Vector3 v = ToVector3(token); prop.vector2IntValue = new Vector2Int((int)v.x, (int)v.y); break; }
                case SerializedPropertyType.Vector3Int: { Vector3 v = ToVector3(token); prop.vector3IntValue = new Vector3Int((int)v.x, (int)v.y, (int)v.z); break; }
                case SerializedPropertyType.ObjectReference:
                    prop.objectReferenceValue = ResolveObjectRef(token, ExpectedRefType(prop));
                    break;
                default:
                    if (prop.isArray) { SetArray(prop, token); break; }
                    if (prop.hasVisibleChildren) { SetStruct(prop, token); break; }
                    throw new Exception($"unsupported property type {prop.propertyType} for '{prop.name}'");
            }
        }

        private static void SetArray(SerializedProperty prop, JToken token)
        {
            if (!(token is JArray arr)) throw new Exception($"'{prop.name}' is an array; provide a JSON array");
            prop.arraySize = arr.Count;
            for (int i = 0; i < arr.Count; i++)
                ApplyToken(prop.GetArrayElementAtIndex(i), arr[i]);
        }

        private static void SetStruct(SerializedProperty prop, JToken token)
        {
            if (!(token is JObject obj)) throw new Exception($"'{prop.name}' is a compound value; provide a JSON object");
            foreach (JProperty jp in obj.Properties())
            {
                SerializedProperty child = FindRelative(prop, jp.Name);
                if (child == null) throw new Exception($"'{prop.name}' has no sub-field '{jp.Name}'");
                ApplyToken(child, jp.Value);
            }
        }

        private static void SetEnum(SerializedProperty prop, JToken token)
        {
            if (token.Type == JTokenType.Integer) { prop.intValue = token.ToObject<int>(); return; }
            string name = (string)token;
            string[] names = prop.enumNames;
            int idx = Array.FindIndex(names, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new Exception($"'{name}' is not valid for '{prop.name}'. Options: {string.Join(", ", names)}");
            prop.enumValueIndex = idx;
        }

        // ---------- reflection fallback ----------

        private static void ApplyReflection(Component comp, string key, JToken token)
        {
            Type ct = comp.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

            PropertyInfo pi = ct.GetProperty(key, flags);
            if (pi != null && pi.CanWrite)
            {
                Undo.RecordObject(comp, UndoLabel);
                pi.SetValue(comp, Coerce(token, pi.PropertyType));
                EditorUtility.SetDirty(comp);
                return;
            }
            FieldInfo fi = ct.GetField(key, flags);
            if (fi != null)
            {
                Undo.RecordObject(comp, UndoLabel);
                fi.SetValue(comp, Coerce(token, fi.FieldType));
                EditorUtility.SetDirty(comp);
                return;
            }
            throw new Exception($"no serialized property, public property, or field '{key}' on {ct.Name}. " +
                                $"Valid serialized names: {ValidNames(comp)}");
        }

        private static object Coerce(JToken token, Type type)
        {
            if (type == typeof(Vector2)) return ToVector2(token);
            if (type == typeof(Vector3)) return ToVector3(token);
            if (type == typeof(Vector4)) return ToVector4(token);
            if (type == typeof(Quaternion)) return ToQuaternion(token);
            if (type == typeof(Color)) return ToColor(token);
            if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return ResolveObjectRef(token, type);
            if (type.IsEnum)
                return token.Type == JTokenType.Integer
                    ? Enum.ToObject(type, token.ToObject<int>())
                    : Enum.Parse(type, (string)token, true);
            return token.ToObject(type);
        }

        // ---------- Unity value coercions (accept array [..] or object {..}) ----------

        private static float FA(JToken t) => t.ToObject<float>();

        private static Vector2 ToVector2(JToken t)
        {
            if (t is JArray a) return new Vector2(FA(a[0]), FA(a[1]));
            return new Vector2(FA(t["x"]), FA(t["y"]));
        }

        private static Vector3 ToVector3(JToken t)
        {
            if (t is JArray a) return new Vector3(FA(a[0]), FA(a[1]), a.Count > 2 ? FA(a[2]) : 0f);
            return new Vector3(FA(t["x"]), FA(t["y"]), t["z"] != null ? FA(t["z"]) : 0f);
        }

        private static Vector4 ToVector4(JToken t)
        {
            if (t is JArray a) return new Vector4(FA(a[0]), FA(a[1]), a.Count > 2 ? FA(a[2]) : 0f, a.Count > 3 ? FA(a[3]) : 0f);
            return new Vector4(FA(t["x"]), FA(t["y"]), t["z"] != null ? FA(t["z"]) : 0f, t["w"] != null ? FA(t["w"]) : 0f);
        }

        private static Quaternion ToQuaternion(JToken t)
        {
            if (t is JArray a)
                return a.Count >= 4 ? new Quaternion(FA(a[0]), FA(a[1]), FA(a[2]), FA(a[3]))
                                    : Quaternion.Euler(FA(a[0]), FA(a[1]), a.Count > 2 ? FA(a[2]) : 0f);
            if (t["w"] != null) return new Quaternion(FA(t["x"]), FA(t["y"]), FA(t["z"]), FA(t["w"]));
            return Quaternion.Euler(FA(t["x"]), FA(t["y"]), t["z"] != null ? FA(t["z"]) : 0f);
        }

        private static Color ToColor(JToken t)
        {
            if (t is JArray a) return new Color(FA(a[0]), FA(a[1]), FA(a[2]), a.Count > 3 ? FA(a[3]) : 1f);
            return new Color(FA(t["r"]), FA(t["g"]), FA(t["b"]), t["a"] != null ? FA(t["a"]) : 1f);
        }

        private static int ToLayer(JToken t)
        {
            if (t.Type == JTokenType.Integer) return t.ToObject<int>();
            int l = LayerMask.NameToLayer((string)t);
            if (l < 0) throw new Exception($"unknown layer '{t}'");
            return l;
        }

        // ---------- object reference resolution ----------

        private static Type ExpectedRefType(SerializedProperty prop)
        {
            // prop.type looks like "PPtr<$Material>" for object references.
            string s = prop.type;
            int lt = s.IndexOf('$');
            if (lt >= 0)
            {
                string tn = s.Substring(lt + 1).TrimEnd('>');
                Type t = TypeResolver.Resolve(tn);
                if (t != null) return t;
            }
            return typeof(UnityEngine.Object);
        }

        private static UnityEngine.Object ResolveObjectRef(JToken token, Type expected)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            UnityEngine.Object resolved;

            if (token.Type == JTokenType.Integer)
            {
                resolved = EditorUtility.InstanceIDToObject(token.ToObject<int>());
            }
            else if (token.Type == JTokenType.String)
            {
                string s = (string)token;
                resolved = s.StartsWith("Assets/") || s.StartsWith("Packages/")
                    ? ObjectResolver.ResolveAsset(s, expected)
                    : ObjectResolver.ResolveGameObject(s);
            }
            else if (token is JObject o)
            {
                if (o["instanceID"] != null) resolved = EditorUtility.InstanceIDToObject((int)o["instanceID"]);
                else if (o["guid"] != null) resolved = ObjectResolver.ResolveAsset((string)o["guid"], expected);
                else if (o["path"] != null) resolved = ObjectResolver.ResolveAsset((string)o["path"], expected);
                else throw new Exception("object reference needs instanceID, guid, or path");
            }
            else throw new Exception("unsupported object-reference value");

            if (resolved == null) return null;

            // A GameObject given where a Component is expected -> grab that component.
            if (resolved is GameObject g && typeof(Component).IsAssignableFrom(expected) && expected != typeof(GameObject))
            {
                Component c = g.GetComponent(expected);
                if (c == null) throw new Exception($"'{g.name}' has no {expected.Name}");
                resolved = c;
            }
            if (!expected.IsInstanceOfType(resolved))
                throw new Exception($"expected a {expected.Name} but got a {resolved.GetType().Name}");
            return resolved;
        }

        private static string ValidNames(Component comp)
        {
            var so = new SerializedObject(comp);
            var names = new List<string>();
            SerializedProperty it = so.GetIterator();
            if (it.NextVisible(true))
                do { names.Add(it.name); } while (names.Count < 30 && it.NextVisible(false));
            return string.Join(", ", names);
        }
    }
}
