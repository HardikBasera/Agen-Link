using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace AgenLink.Analysis
{
    /// <summary>One audit result: what's wrong, where, how bad, and (when safe) which apply_fixes op fixes it.</summary>
    internal sealed class Finding
    {
        public string Id;             // stable rule id, e.g. "mesh.high-poly"
        public string Severity;       // "critical" | "warn" | "info"
        public string Category;       // geometry | lighting | rendering | assets | physics | ui | settings
        public string Target;         // scene hierarchy path or asset path
        public string Evidence;       // the numbers behind the verdict
        public string Recommendation; // what to do about it
        public string FixType;        // apply_fixes op name when auto-fixable, else null
        public string FixValue;       // suggested value for the fix op (string form), else null

        public string ToJson() => new JObj()
            .S("id", Id)
            .S("severity", Severity)
            .S("category", Category)
            .S("target", Target)
            .S("evidence", Evidence)
            .S("recommendation", Recommendation)
            .S("fixType", FixType)
            .S("fixValue", FixValue)
            .B("autoFixable", FixType != null)
            .Build();

        public static int Rank(string severity) => severity == "critical" ? 0 : severity == "warn" ? 1 : 2;

        /// <summary>Severity order (critical → warn → info), then stable by rule id.</summary>
        public static readonly Comparison<Finding> BySeverity = (a, b) => Rank(a.Severity) != Rank(b.Severity)
            ? Rank(a.Severity) - Rank(b.Severity)
            : string.CompareOrdinal(a.Id, b.Id);

        /// <summary>Inverse of ToJson ("autoFixable" is derived from FixType, so it round-trips implicitly).</summary>
        public static Finding FromJObject(JObject o) => new Finding
        {
            Id = (string)o["id"],
            Severity = (string)o["severity"],
            Category = (string)o["category"],
            Target = (string)o["target"],
            Evidence = (string)o["evidence"],
            Recommendation = (string)o["recommendation"],
            FixType = (string)o["fixType"],
            FixValue = (string)o["fixValue"],
        };

        /// <summary>Sort by severity, cap to max, and build the common response envelope.</summary>
        public static string BuildReport(List<Finding> findings, int max, string statsJson)
        {
            findings.Sort(BySeverity);
            int critical = 0, warn = 0, info = 0;
            foreach (Finding f in findings)
            {
                if (f.Severity == "critical") critical++;
                else if (f.Severity == "warn") warn++;
                else info++;
            }
            bool truncated = findings.Count > max;
            var elems = new List<string>();
            for (int i = 0; i < findings.Count && i < max; i++) elems.Add(findings[i].ToJson());
            var o = new JObj()
                .N("total", findings.Count)
                .N("critical", critical)
                .N("warnings", warn)
                .N("infos", info)
                .B("truncated", truncated);
            if (statsJson != null) o.Raw("stats", statsJson);
            return o.Raw("findings", Json.Arr(elems)).Build();
        }
    }
}
