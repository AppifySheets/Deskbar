using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE TASKS PANEL FROM THE KEYBOARD ALONE (Deskbar task b61f79de; design
// docs/superpowers/specs/2026-11-28-keyboard-navigation-design.md; plan docs/superpowers/plans/2026-11-28-keyboard-navigation.md).
// Petre: "make it possible to navigate with the keyboard only". He picked "menus and letter keys" over a Tab ring.
//
// WHY NOT WPF FOCUS. Every button of the panel is built Focusable = false (Flat: a click must never take the keyboard from
// a box he is typing in), and every redraw, which comes every few seconds while Claude sessions work, rebuilds the
// buttons. A Tab ring would have to remember the focused button by name and put it back after each redraw. Instead the
// keys act on what already survives a redraw:
//
//   the selection   Up / Down / Home / End move the board's selected task (TasksBoard.Select), the same highlight a click
//                   gives, in the order the view draws its lines (Keys). PanelKeys.Step decides which line.
//   a click's twin  Enter does what a click on the selected line does: the Inbox opens its page, the List and the Matrix
//                   unfold or fold its thread (TasksBoard.Pick).
//   the menus       the menu key or Shift+F10 opens the task's "⋯" menu (OpenMenuFor), a WPF ContextMenu, which then walks
//                   its items with the arrows by itself. W opens the workspace chip's move menu, P the pull request (a menu
//                   when there are several).
//   the actions     R rechecks (the recheck tile's Reevaluate), N opens the add box, C puts the keyboard in the comment box.
//   switching       Left / Right walk the Inbox's tabs; Ctrl+1..4 the views (Inbox, List, Matrix, Stats).
//
// NEVER WHILE HE TYPES. The window's PreviewKeyDown runs before any box sees the key, so every plain key here first asks
// whether a text box has the keyboard (TypingIn) and leaves it alone if so: arrows, letters and Enter belong to the box.
// A PAGE OPENS WITH THE KEYBOARD IN ITS BOX (TasksWindow.PageFocus.cs, task 64744611), so on a page the plain letters
// would be typed into the message. There the same actions answer to Alt+letter (Alt+P, Alt+W, Alt+R, Alt+N), which a
// text box never uses, and to the menu key; Page Up / Page Down scroll the page even from the box, which is at most seven
// lines tall. Alt+letter works everywhere, so one habit does for both. Esc, F2 and Ctrl+F keep exactly what they did.
//
// A key that does nothing returns false, so it is not swallowed: an arrow in an empty view, a letter with no task.
public partial class TasksWindow
{
    // Whether a text box has the keyboard: the search, add, comment, rename, link and subtask boxes are all TextBoxBase.
    // Only a box still on screen counts. Esc from a page takes the page, and its box with it, away while WPF can still
    // name that box as the focused element; counted as typing, it kept every arrow dead after the page closed (the
    // duplicate task bd149ac3: "after i leave the chat with an escape ... i can't navigate with arrow keys").
    // A read-only box (every comment's body is a read-only RichTextBox, focusable so its text can be selected) is not
    // typing: clicking a comment must not leave the arrows dead.
    static bool TypingIn(object? focused) => focused is TextBoxBase { IsVisible: true, IsReadOnly: false };

    // A button that does take the keyboard has it: the dialog, permission and plan cards' choices and the answer buttons
    // are focusable, reached with Tab from the comment box. Their arrows move between them and their Enter presses them,
    // as WPF's own keyboard navigation does, so the panel leaves those keys alone.
    static bool OnAButton(object? focused) => focused is ButtonBase { IsVisible: true };

    // The element the key being handled came from (the window's PreviewKeyDown sets it around Press). Normally the focused
    // element; asked as well because a key raised on a box directly, as a test does, must stay that box's.
    object? pressSource;

    // Whether a box has the key: the focused element or the key's own source is a text box on screen.
    bool Typing => TypingIn(Keyboard.FocusedElement) || TypingIn(pressSource);

    /// <summary>Where the panel is, for the keys and the keys card.</summary>
    internal PanelPlace PlaceShown =>
        InboxPage.Visibility == Visibility.Visible ? PanelPlace.InboxPage
        : latest switch
        {
            { Stats: not null } => PanelPlace.Stats,
            { Inbox: not null } => PanelPlace.InboxList,
            { Matrix: true } => PanelPlace.Matrix,
            _ => PanelPlace.List,
        };

    // The task the keys act on: the open page's, else the selected line's while that line is drawn in the view (a
    // selection hidden by a filter or another tab is not at hand, so W or R would act on something unseen).
    Maybe<Guid> KeyTask =>
        inboxPage is { } page && InboxPage.Visibility == Visibility.Visible ? page
        : PlaceShown != PanelPlace.InboxPage && selectedId.HasValue && Keys.Contains(selectedId.Value) ? selectedId
        : Maybe<Guid>.None;

    /// <summary>
    /// The keys beyond Esc, F2 and Ctrl+F (Press hands every other key here). True when the key did something.
    /// </summary>
    bool Navigate(Key key, ModifierKeys modifiers)
    {
        // The manager's docked window is one chat with no lines and no toolbar: its keys are its box's.
        if (ManagerOnly) return false;
        var typing = Typing;
        var plain = modifiers == ModifierKeys.None && !typing;
        var alt = modifiers == ModifierKeys.Alt;
        var onPage = PlaceShown == PanelPlace.InboxPage;
        // Arrows and Enter belong to a focused button (OnAButton), and to a box.
        var free = !typing && !OnAButton(Keyboard.FocusedElement);
        return (key, modifiers) switch
        {
            (Key.Up, ModifierKeys.None) when free => onPage ? ScrollPage(s => s.LineUp()) : Move(PanelKeys.Previous) || HoldArrow(),
            (Key.Down, ModifierKeys.None) when free => onPage ? ScrollPage(s => s.LineDown()) : Move(PanelKeys.Next) || HoldArrow(),
            (Key.Home, ModifierKeys.None) when free => onPage ? ScrollPage(s => s.ScrollToHome()) : Move(PanelKeys.First),
            (Key.End, ModifierKeys.None) when free => onPage ? ScrollPage(s => s.ScrollToEnd()) : Move(PanelKeys.Last),
            // Page Up / Page Down: the page scrolls even from its box; a list scrolls only when no box has the keyboard.
            (Key.PageUp, ModifierKeys.None) when onPage || !typing => onPage ? ScrollPage(s => s.PageUp()) : ScrollList(s => s.PageUp()),
            (Key.PageDown, ModifierKeys.None) when onPage || !typing => onPage ? ScrollPage(s => s.PageDown()) : ScrollList(s => s.PageDown()),
            (Key.Enter or Key.Return, ModifierKeys.None) when free => OpenSelected(),
            (Key.Left, ModifierKeys.None) when free => StepTab(-1) || HoldArrow(),
            (Key.Right, ModifierKeys.None) when free => StepTab(1) || HoldArrow(),
            (Key.D1 or Key.NumPad1, ModifierKeys.Control) => ShowView(PanelPlace.InboxList),
            (Key.D2 or Key.NumPad2, ModifierKeys.Control) => ShowView(PanelPlace.List),
            (Key.D3 or Key.NumPad3, ModifierKeys.Control) => ShowView(PanelPlace.Matrix),
            (Key.D4 or Key.NumPad4, ModifierKeys.Control) => ShowView(PanelPlace.Stats),
            // The menu key never types anything, so it opens the task's menu from a box too (a box's own cut/copy/paste
            // menu stays on its right-click).
            (Key.Apps, ModifierKeys.None) or (Key.F10, ModifierKeys.Shift) => OpenTaskMenu(),
            (Key.C, _) when plain || alt => FocusComment(),
            (Key.P, _) when plain || alt => OpenPullRequestKey(),
            (Key.W, _) when plain || alt => OpenMoveKey(),
            (Key.R, _) when plain || alt => Recheck(),
            (Key.N, _) when plain || alt => AddKey(),
            // The keys card: F1 from anywhere, ? (Shift and the / key) only where it is not being typed.
            (Key.F1, ModifierKeys.None) => ToggleKeysCard(),
            (Key.OemQuestion, ModifierKeys.Shift) when !typing => ToggleKeysCard(),
            _ => false,
        };
    }

    // An arrow nothing used (an empty view, Left/Right outside the Inbox's tabs), while the keyboard rests where FocusBoard
    // and KeepKeysInPanel put it (the toolbar or the window): kept, so WPF's own directional navigation does not carry the
    // keyboard into the search box, where the next letters would be typed.
    bool HoldArrow() => Keyboard.FocusedElement is { } focused && (ReferenceEquals(focused, Toolbar) || ReferenceEquals(focused, this));

    // Up / Down / Home / End over the lines the view draws, in the order it draws them.
    bool Move(int by)
    {
        if (Actions?.Select is not { } select || PlaceShown == PanelPlace.Stats) return false;
        var next = PanelKeys.Step(Keys, selectedId, by);
        if (next.HasNoValue) return false;
        select(next.Value);
        return true;
    }

    // A page's arrows and paging keys scroll it (its scroller is not focusable, so it never sees them itself).
    bool ScrollPage(Action<ScrollViewer> scroll)
    {
        if (pageScroller is not { } scroller) return false;
        scroll(scroller);
        return true;
    }

    // Page Up / Page Down over the List or the Inbox's list (the Matrix scrolls per cell and is left alone).
    bool ScrollList(Action<ScrollViewer> scroll)
    {
        if (PlaceShown is PanelPlace.Matrix or PanelPlace.Stats) return false;
        scroll(Scroller);
        return true;
    }

    // Enter: the selected line's click. The Inbox opens its page; the List and the Matrix unfold or fold its thread.
    bool OpenSelected()
    {
        if (KeyTask.HasNoValue || PlaceShown == PanelPlace.InboxPage) return false;
        // The card listed the list's keys; a page has others.
        HideKeysCard();
        if (PlaceShown == PanelPlace.InboxList) OpenInboxPage(KeyTask.Value);
        else Actions?.Pick(KeyTask.Value);
        return true;
    }

    // Left / Right: the Inbox's tab before or after the one shown, stopping at the ends (Needs you, Claude, Notes, All).
    bool StepTab(int by)
    {
        if (PlaceShown != PanelPlace.InboxList || InboxShown is not { Tabbed: true }) return false;
        var tabs = Enum.GetValues<InboxTab>();
        var next = tabs[Math.Clamp(Array.IndexOf(tabs, inboxTab) + by, 0, tabs.Length - 1)];
        if (next == inboxTab) return true;
        HideKeysCard();
        ChooseInboxTab(next);
        return true;
    }

    // Ctrl+1..4: the view switch's buttons, as their clicks call them (InboxFooterOf). The Matrix only where the panel is
    // wide enough to allow it (MatrixAllowed), as its button only shows there.
    bool ShowView(PanelPlace place)
    {
        if (Actions is not { } actions) return false;
        // Another view has other keys: the card, listing this one's, goes.
        HideKeysCard();
        switch (place)
        {
            case PanelPlace.InboxList when actions.ShowInbox is { } inbox: inbox(true); return true;
            case PanelPlace.List: actions.ShowMatrix(false); return true;
            case PanelPlace.Matrix when latest is { MatrixAllowed: true }: actions.ShowMatrix(true); return true;
            case PanelPlace.Stats when actions.ShowStats is { } stats: stats(); return true;
            default: return false;
        }
    }

    // What a keyboard-opened menu hangs from: the task's "⋯" button where it shows, else its line (a slim List line or a
    // line whose quiet buttons are hidden has no visible "⋯").
    UIElement? AnchorFor(Guid id) =>
        menuButtons.GetValueOrDefault(id) is { IsVisible: true } more ? more
        : inboxLines.TryGetValue(id, out var line) ? line.Line
        : selectionFrames.TryGetValue(id, out var parts) ? parts.Frame
        : null;

    // The menu key or Shift+F10: the task's whole menu, what "⋯" opens, under its button.
    bool OpenTaskMenu()
    {
        if (KeyTask.HasNoValue) return false;
        OpenMenuFor(KeyTask.Value, anchor: AnchorFor(KeyTask.Value));
        return true;
    }

    // C: the keyboard to the task's comment box, there only while its thread is open (a page always has one).
    bool FocusComment()
    {
        if (KeyTask.HasNoValue || CommentBoxOf(KeyTask.Value) is not { } box) return false;
        FocusBox(box);
        return true;
    }

    // P: the task's pull request, opened as its link on the page opens it; with several, a menu of them to pick from.
    bool OpenPullRequestKey()
    {
        if (KeyTask.HasNoValue || RowFor(KeyTask.Value) is not { PullRequests.Count: > 0 } row) return false;
        if (row.PullRequests.Count == 1)
        {
            OpenPullRequest(row.Id, row.PullRequests[0]);
            return true;
        }
        var menu = new ContextMenu { PlacementTarget = AnchorFor(row.Id), Placement = PlacementMode.Bottom };
        row.PullRequests.ToList().ForEach(pr => menu.Items.Add(Item(pr.Label, () => OpenPullRequest(row.Id, pr))));
        OpenMenu(menu);
        return true;
    }

    // W: the workspace chip's move menu (InboxChip). A closing task has no chip to press, so no menu either.
    bool OpenMoveKey()
    {
        if (KeyTask.HasNoValue || RowFor(KeyTask.Value) is not { Closing: false } row) return false;
        OpenMoveMenu(row.Id, row.Place.Workspace, AnchorFor(row.Id));
        return true;
    }

    // R: the recheck tile's press (RecheckButton), only where the tile would show.
    bool Recheck()
    {
        if (KeyTask.HasNoValue || RowFor(KeyTask.Value) is not { } row || !Reevaluates(row) || Actions?.Reevaluate is not { } reevaluate) return false;
        reevaluate(row.Id);
        return true;
    }

    // N: the add box, as "+ Add" opens it.
    bool AddKey()
    {
        ShowAdd();
        return true;
    }

    // THE KEYS NEED THE KEYBOARD IN THE PANEL. A line is a button that never takes the keyboard, so a panel brought in front
    // by a click on a line can be active with nothing in it focused. After an activation settles (the same Input priority
    // RefocusPageBox waits for), the list gets the keyboard on its toolbar, as the board's FocusBoard gives it, so the
    // arrows work without a click first. A page is RefocusPageBox's: its box takes the keyboard there.
    void KeepKeysInPanel() => Dispatcher.BeginInvoke(() =>
    {
        if (IsActive && !IsKeyboardFocusWithin && !ManagerOnly && OpenPageBox is null && !Toolbar.Focus()) Focus();
    }, System.Windows.Threading.DispatcherPriority.Input);
}
