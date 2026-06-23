using System;
using System.Collections.Generic;

namespace AgenLink.History
{
    internal enum TurnKind { You, Claude, Action }

    internal sealed class ConvTurn
    {
        public TurnKind Kind;
        public string Text;
        public string Detail;   // Action turns only: full command / file path behind the short marker
        public ConvTurn(TurnKind kind, string text, string detail = null) { Kind = kind; Text = text; Detail = detail; }
    }

    /// <summary>One AI session, reduced to a readable transcript.</summary>
    internal sealed class Conversation
    {
        public string Title;
        public DateTime StartedAt;                       // local time; used for date grouping/sorting
        public string FilePath;
        public string Agent = "claude";                  // "claude" | "antigravity" | "analysis" — drives badge + colors
        public bool MetaOnly;                            // agy sessions: we know they ran, content lives in agy's store
        public string SourceNote;                        // analysis cards only: "Analysis tab" | "Terminal/MCP"
        public List<ConvTurn> Turns = new List<ConvTurn>();
    }
}
