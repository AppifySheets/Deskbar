using Deskbar.Core.Tasks;

namespace Deskbar.App;

// WHAT A STEP LOOKS LIKE IN A RUN OF STEPS (the Inbox page's chat): one icon and one verb per kind. The icon is the shape
// the owner reads the kind by (he is colourblind: every state in the Inbox is a shape first, colour second, InboxMarks), so
// the nine kinds have nine different outlines, which a test checks. The verb turns ChatStep.Label (the target without a
// verb: "Dock.cs", "dotnet test") into the mockup's row text, "Read Dock.cs" (r2-app.js stepHTML).
internal static class ChatStepIcons
{
    /// <summary>The name of a kind's icon in <see cref="InboxMarks"/>'s table.</summary>
    internal static string IconOf(StepKind kind) => kind switch
    {
        StepKind.Read => "file",
        StepKind.Edit => "pencil",
        StepKind.Write => "write",
        StepKind.Run => "terminal",
        StepKind.Search => "search",
        StepKind.Web => "globe",
        StepKind.Subagent => "agent",
        StepKind.Plan => "list",
        _ => "spark",
    };

    /// <summary>The verb in front of a step's label ("Read", "Ran"): the row reads "Ran dotnet test".</summary>
    internal static string VerbOf(StepKind kind) => kind switch
    {
        StepKind.Read => "Read",
        StepKind.Edit => "Edited",
        StepKind.Write => "Wrote",
        StepKind.Run => "Ran",
        StepKind.Search => "Searched",
        StepKind.Web => "Looked up",
        StepKind.Subagent => "Asked agent",
        StepKind.Plan => "Plan",
        _ => "Used",
    };

    /// <summary>The words of a step row, for its text and for a screen reader.</summary>
    internal static string TextOf(ChatStep step) => $"{VerbOf(step.Kind)} {step.Label}";
}
