using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace AgenLink.Analysis
{
    /// <summary>
    /// Optional bridge to Unity's Project Auditor package (com.unity.project-auditor): script-level
    /// diagnostics (per-frame allocations, Camera.main in Update, ...) outside this audit's reach.
    /// Pure reflection — the package is NOT a dependency. Installed is false when absent, and API
    /// drift degrades to one explanatory finding instead of an exception.
    /// </summary>
    internal static class ProjectAuditorRunner
    {
        private const string AuditorType = "Unity.ProjectAuditor.Editor.ProjectAuditor, Unity.ProjectAuditor.Editor";
        private const int MaxIssues = 200;

        public static bool Installed => Type.GetType(AuditorType) != null;

        /// <summary>Run Project Auditor and map its code diagnostics into Findings (Category "code").</summary>
        public static List<Finding> Run()
        {
            var list = new List<Finding>();
            try
            {
                Type type = Type.GetType(AuditorType);
                if (type == null) return list;
                object auditor = Activator.CreateInstance(type);

                MethodInfo audit = PickAudit(type);
                object report = audit.Invoke(auditor, new object[audit.GetParameters().Length]);

                IEnumerable issues = GetIssues(report);
                if (issues == null)
                {
                    list.Add(ApiMismatch("could not read the report"));
                    return list;
                }

                foreach (object issue in issues)
                {
                    if (list.Count >= MaxIssues) break;
                    string category = Str(Member(issue, "Category", "category"));
                    if (category != null && category != "Code") continue;   // script diagnostics only
                    string desc = Str(Member(issue, "Description", "description"));
                    if (string.IsNullOrEmpty(desc)) continue;
                    string path = Str(Member(issue, "RelativePath", "relativePath", "Filename", "filename"));
                    int line = ToInt(Member(issue, "Line", "line"));
                    list.Add(new Finding
                    {
                        Id = "pa." + (Str(Member(issue, "Id", "id")) ?? "issue"),
                        Severity = MapSeverity(Str(Member(issue, "Severity", "severity"))),
                        Category = "code",
                        Target = string.IsNullOrEmpty(path) ? "(project)" : path,
                        Evidence = line > 0 ? "line " + line : "",
                        Recommendation = desc,
                    });
                }
            }
            catch (Exception e)
            {
                list.Add(ApiMismatch(e.GetType().Name));
            }
            return list;
        }

        private static MethodInfo PickAudit(Type type)
        {
            MethodInfo best = null;
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                if (m.Name == "Audit" && (best == null || m.GetParameters().Length < best.GetParameters().Length))
                    best = m;
            if (best == null) throw new MissingMethodException("Audit");
            return best;
        }

        private static IEnumerable GetIssues(object report)
        {
            if (report == null) return null;
            foreach (string name in new[] { "GetAllIssues", "GetIssues" })
            {
                MethodInfo m = report.GetType().GetMethod(name, new Type[0]);
                if (m != null) return m.Invoke(report, null) as IEnumerable;
            }
            return null;
        }

        private static object Member(object obj, params string[] names)
        {
            if (obj == null) return null;
            Type t = obj.GetType();
            foreach (string n in names)
            {
                PropertyInfo p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null) return p.GetValue(obj);
                FieldInfo fi = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi != null) return fi.GetValue(obj);
            }
            return null;
        }

        private static string Str(object o) => o?.ToString();

        private static int ToInt(object o)
        {
            try { return o == null ? 0 : Convert.ToInt32(o); }
            catch { return 0; }
        }

        private static string MapSeverity(string s) =>
            s == "Critical" ? "critical" : s == "Major" ? "warn" : "info";

        private static Finding ApiMismatch(string why) => new Finding
        {
            Id = "pa.api-mismatch", Severity = "info", Category = "code", Target = "(project)",
            Evidence = why,
            Recommendation = "Project Auditor is installed but its API didn't match this integration — " +
                             "run it directly via Window ▸ Analysis ▸ Project Auditor.",
        };
    }
}
