using System;
using System.Collections.Generic;
using UnityEngine;

namespace AgenLink.GitHub
{
    /// <summary>One repository as returned by `gh repo list --json nameWithOwner,visibility,updatedAt`.</summary>
    [Serializable]
    internal class GitHubRepo
    {
        public string nameWithOwner;
        public string visibility;
        public string updatedAt;

        [Serializable] private class Wrapper { public GitHubRepo[] items; }

        /// <summary>Parse gh's top-level JSON array (JsonUtility needs an object, so wrap it).</summary>
        public static List<GitHubRepo> ParseList(string json)
        {
            var list = new List<GitHubRepo>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            try
            {
                var w = JsonUtility.FromJson<Wrapper>("{\"items\":" + json + "}");
                if (w?.items != null) list.AddRange(w.items);
            }
            catch { /* malformed — return what we have */ }
            return list;
        }
    }
}
