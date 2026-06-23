using System.Runtime.CompilerServices;

// The EditMode test assembly lives in a separate DLL; expose internals to it so it
// can exercise TerminalProtocol / ScreenBuffer / VtParser directly.
[assembly: InternalsVisibleTo("AgenLink.Editor.Tests")]
[assembly: InternalsVisibleTo("AgenLink.Editor.History.Tests")]
[assembly: InternalsVisibleTo("AgenLink.Editor.GitHub.Tests")]
[assembly: InternalsVisibleTo("AgenLink.Editor.Neuron.Tests")]
[assembly: InternalsVisibleTo("AgenLink.Editor.Analysis.Tests")]
