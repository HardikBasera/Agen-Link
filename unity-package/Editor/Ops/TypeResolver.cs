using System;
using System.Collections.Generic;
using UnityEngine;

namespace AgenLink.Ops
{
    /// <summary>
    /// Resolves a type name to a <see cref="Type"/>. Accepts a fully-qualified name ("UnityEngine.Rigidbody")
    /// or a bare class name ("Rigidbody"), scanning every loaded assembly so project MonoBehaviours resolve
    /// too. Results are cached; the cache is static and therefore reset on every domain reload, which is
    /// exactly when new/renamed types could appear.
    /// </summary>
    internal static class TypeResolver
    {
        private static readonly Dictionary<string, Type> Cache = new Dictionary<string, Type>();

        public static Type Resolve(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (Cache.TryGetValue(name, out Type cached)) return cached;

            Type found = Type.GetType(name);
            if (found == null)
            {
                var shortMatches = new List<Type>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }
                    foreach (Type t in types)
                    {
                        if (t == null) continue;
                        if (t.FullName == name) { found = t; break; }
                        if (t.Name == name) shortMatches.Add(t);
                    }
                    if (found != null) break;
                }
                if (found == null && shortMatches.Count == 1) found = shortMatches[0];
                else if (found == null && shortMatches.Count > 1)
                {
                    // Multiple types share this short name (e.g. "Transform" also lives in
                    // UnityEngine.Rendering.RadeonRays and log4net.Util). Prefer the canonical Unity
                    // type over third-party collisions; only throw if the top preference is itself tied.
                    found = PreferCanonical(shortMatches);
                    if (found == null)
                    {
                        var names = new List<string>();
                        for (int i = 0; i < shortMatches.Count && i < 6; i++) names.Add(shortMatches[i].FullName);
                        throw new Exception($"type '{name}' is ambiguous ({shortMatches.Count} matches: " +
                                            $"{string.Join(", ", names)}). Use the full name including namespace.");
                    }
                }
            }

            Cache[name] = found; // cache misses too (null) — repeated bad names shouldn't re-scan every assembly
            return found;
        }

        // Pick the single best-ranked type among short-name collisions, or null if two share the top
        // rank (genuinely ambiguous — let the caller throw). Canonical Unity types win over third-party
        // ones that merely reuse the name.
        private static Type PreferCanonical(List<Type> matches)
        {
            Type best = null;
            int bestRank = int.MaxValue, bestCount = 0;
            foreach (Type t in matches)
            {
                int r = NamespaceRank(t);
                if (r < bestRank) { bestRank = r; best = t; bestCount = 1; }
                else if (r == bestRank) bestCount++;
            }
            return bestCount == 1 ? best : null;
        }

        // 0 = core UnityEngine (Transform, Rigidbody, ...); 1 = any other Unity engine/editor namespace
        // (UnityEngine.UI, UnityEngine.Rendering.*, UnityEditor.*); 2 = everything else (project + 3rd party).
        private static int NamespaceRank(Type t)
        {
            string ns = t.Namespace ?? "";
            if (ns == "UnityEngine") return 0;
            if (ns.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                ns == "UnityEditor" || ns.StartsWith("UnityEditor.", StringComparison.Ordinal)) return 1;
            return 2;
        }

        /// <summary>Resolve a name that must be a concrete Component subtype (for add/get/set). Throws otherwise.</summary>
        public static Type RequireComponentType(string name)
        {
            Type t = Resolve(name);
            if (t == null) throw new Exception($"unknown type '{name}'. Use a full name ('UnityEngine.Rigidbody') or a class name ('Rigidbody').");
            if (!typeof(Component).IsAssignableFrom(t))
                throw new Exception($"'{t.FullName}' is not a Component.");
            if (t.IsAbstract)
                throw new Exception($"'{t.FullName}' is abstract and cannot be added; name a concrete component type.");
            return t;
        }
    }
}
