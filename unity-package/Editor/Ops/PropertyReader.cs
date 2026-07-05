using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AgenLink.Ops
{
    /// <summary>
    /// Serializes a component's SerializedObject to a JSON object of {propertyName: value}. The keys it emits
    /// are exactly the names <see cref="PropertyEngine"/> accepts, so a get -> edit -> set round-trip works by
    /// construction. Bounded on every axis (depth, array length, string length, total size) so a fat component
    /// (ParticleSystem, Terrain) can't blow up the caller's context.
    /// </summary>
    internal static class PropertyReader
    {
        private const int MaxArray = 25;
        private const int MaxString = 1024;
        private const int ByteBudget = 16000;

        /// <summary>{name: value, ...} for every visible top-level serialized property of the component.</summary>
        public static string ReadComponentProperties(Component c, int maxDepth)
        {
            var so = new SerializedObject(c);
            var sb = new StringBuilder("{");
            bool first = true;
            bool truncated = false;

            SerializedProperty it = so.GetIterator();
            if (it.NextVisible(true))
            {
                do
                {
                    if (sb.Length > ByteBudget) { truncated = true; break; }
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Json.Str(it.name)).Append(':').Append(RenderValue(it, maxDepth));
                } while (it.NextVisible(false)); // false: stay at top level; RenderValue reads children via a copy
            }

            if (truncated)
            {
                if (!first) sb.Append(',');
                sb.Append(Json.Str("__truncated")).Append(":true");
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static string RenderValue(SerializedProperty p, int depthLeft)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.longValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return p.boolValue ? "true" : "false";
                case SerializedPropertyType.Float: return F(p.doubleValue);
                case SerializedPropertyType.String: return Json.Str(Clip(p.stringValue));
                case SerializedPropertyType.Character: return p.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.LayerMask: return p.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Enum:
                {
                    int idx = p.enumValueIndex;
                    if (p.enumNames != null && idx >= 0 && idx < p.enumNames.Length) return Json.Str(p.enumNames[idx]);
                    return p.intValue.ToString(CultureInfo.InvariantCulture);
                }
                case SerializedPropertyType.Color:
                {
                    Color col = p.colorValue;
                    return "{" + "\"r\":" + F(col.r) + ",\"g\":" + F(col.g) + ",\"b\":" + F(col.b) + ",\"a\":" + F(col.a) + "}";
                }
                case SerializedPropertyType.Vector2: { Vector2 v = p.vector2Value; return "{\"x\":" + F(v.x) + ",\"y\":" + F(v.y) + "}"; }
                case SerializedPropertyType.Vector3: { Vector3 v = p.vector3Value; return "{\"x\":" + F(v.x) + ",\"y\":" + F(v.y) + ",\"z\":" + F(v.z) + "}"; }
                case SerializedPropertyType.Vector4: { Vector4 v = p.vector4Value; return "{\"x\":" + F(v.x) + ",\"y\":" + F(v.y) + ",\"z\":" + F(v.z) + ",\"w\":" + F(v.w) + "}"; }
                case SerializedPropertyType.Quaternion: { Quaternion q = p.quaternionValue; return "{\"x\":" + F(q.x) + ",\"y\":" + F(q.y) + ",\"z\":" + F(q.z) + ",\"w\":" + F(q.w) + "}"; }
                case SerializedPropertyType.Vector2Int: { Vector2Int v = p.vector2IntValue; return "{\"x\":" + v.x + ",\"y\":" + v.y + "}"; }
                case SerializedPropertyType.Vector3Int: { Vector3Int v = p.vector3IntValue; return "{\"x\":" + v.x + ",\"y\":" + v.y + ",\"z\":" + v.z + "}"; }
                case SerializedPropertyType.Rect: { Rect r = p.rectValue; return "{\"x\":" + F(r.x) + ",\"y\":" + F(r.y) + ",\"width\":" + F(r.width) + ",\"height\":" + F(r.height) + "}"; }
                case SerializedPropertyType.Bounds:
                {
                    Bounds b = p.boundsValue;
                    return "{\"center\":{\"x\":" + F(b.center.x) + ",\"y\":" + F(b.center.y) + ",\"z\":" + F(b.center.z) +
                           "},\"size\":{\"x\":" + F(b.size.x) + ",\"y\":" + F(b.size.y) + ",\"z\":" + F(b.size.z) + "}}";
                }
                case SerializedPropertyType.ObjectReference:
                {
                    Object o = p.objectReferenceValue;
                    if (o == null) return "null";
                    string assetPath = AssetDatabase.GetAssetPath(o);
                    var job = new JObj().S("name", o.name).S("type", o.GetType().Name).N("instanceID", o.GetInstanceID());
                    if (!string.IsNullOrEmpty(assetPath)) job.S("assetPath", assetPath);
                    return job.Build();
                }
                default:
                    // Arrays (excluding strings, handled above) and custom structs/classes fall here.
                    if (p.isArray) return RenderArray(p, depthLeft);
                    if (p.hasVisibleChildren && depthLeft > 0) return RenderStruct(p, depthLeft);
                    return Json.Str(p.propertyType.ToString()); // opaque leaf (Gradient, AnimationCurve, depth-limited)
            }
        }

        private static string RenderArray(SerializedProperty p, int depthLeft)
        {
            int n = p.arraySize;
            var sb = new StringBuilder("[");
            int shown = Mathf.Min(n, MaxArray);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(RenderValue(p.GetArrayElementAtIndex(i), depthLeft - 1));
            }
            sb.Append(']');
            if (n > shown) return "{\"__array\":" + sb + ",\"__length\":" + n + ",\"__truncated\":true}";
            return sb.ToString();
        }

        private static string RenderStruct(SerializedProperty p, int depthLeft)
        {
            var sb = new StringBuilder("{");
            bool first = true;
            SerializedProperty c = p.Copy();
            SerializedProperty end = p.GetEndProperty();
            bool enter = true;
            while (c.NextVisible(enter) && !SerializedProperty.EqualContents(c, end))
            {
                enter = false; // after descending into the first child, iterate its siblings, not grandchildren
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Json.Str(c.name)).Append(':').Append(RenderValue(c, depthLeft - 1));
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static string Clip(string s) => s != null && s.Length > MaxString ? s.Substring(0, MaxString) : s;

        private static string F(double v) =>
            double.IsNaN(v) || double.IsInfinity(v) ? "null" : v.ToString("R", CultureInfo.InvariantCulture);
    }
}
