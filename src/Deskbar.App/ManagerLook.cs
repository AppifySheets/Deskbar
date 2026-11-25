using System.Windows.Media;

namespace Deskbar.App;

// THE MANAGER LINE'S COLOURS (task 61e4d143; mockup https://claude.ai/artifact/BkuegZx2E2cxLKwHjSrg9f). The manager's own
// question to the user (Core's ManagerQuestion) shows as one line above every task's question, on the bar and in the
// tasks panel, in a colour of its own: Petre asked for "a different color" and for it to "have a priority". Orange
// already means a task waits on him, blue is In progress and the selection, green is Done, red and yellow are the
// Important and Urgent flags, so the manager line is violet, the one hue the task surfaces did not use yet.
//
// One place for both surfaces, so the bar's line and the panel's tile cannot drift apart. Frozen, as every static brush
// must be (a brush with thread affinity throws when the Windows tests build windows on another STA thread).
internal static class ManagerLook
{
    static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>The 3 DIP stripe down the line's left edge, and the bar's ring mark.</summary>
    internal static readonly SolidColorBrush Stripe = Frozen(0xFF, 0xB4, 0x8C, 0xFF);

    /// <summary>The panel tile's fill (a task waiting on the user has its orange-brown ask tint there).</summary>
    internal static readonly SolidColorBrush PanelFill = Frozen(0xFF, 0x2E, 0x26, 0x40);

    /// <summary>The bar line's tint, translucent like a Claude task's orange tint, so the bar's colour reads through.</summary>
    internal static readonly SolidColorBrush BarTint = Frozen(0x2E, 0xB4, 0x8C, 0xFF);

    /// <summary>"Manager asks", the line's title.</summary>
    internal static readonly SolidColorBrush TitleInk = Frozen(0xFF, 0xE4, 0xD6, 0xFF);

    /// <summary>The question itself.</summary>
    internal static readonly SolidColorBrush QuestionInk = Frozen(0xFF, 0xD2, 0xBC, 0xFF);

    /// <summary>The panel's "Manager" chip: fill, and its edge is the stripe.</summary>
    internal static readonly SolidColorBrush ChipFill = Frozen(0xFF, 0x3B, 0x2A, 0x5C);

    /// <summary>The line's tooltip on both surfaces.</summary>
    internal const string Tip = "The manager asks you this. Click to open the manager and answer it there; your reply clears the line.";
}

/// <summary>The manager's open question as the bar and the panel draw it (ManagerRecord.Question and AskedAt).</summary>
internal sealed record ManagerAsk(string Question, DateTimeOffset AskedAt);
