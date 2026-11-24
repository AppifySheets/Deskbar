using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

/// <summary>
/// A console program started in a console window of its own, for a machine without Windows Terminal (task abfe1db8,
/// ClaudeStart.AttachConsole and TrustConsole). <paramref name="Program"/> is claude.exe's full path or the bare name,
/// <paramref name="Title"/> the window's title, kept against the program's own retitling when given, and left to the
/// program when none.
/// </summary>
public sealed record ConsoleLaunch(string Program, IReadOnlyList<string> Arguments, string WorkingDirectory, Maybe<string> Title);
