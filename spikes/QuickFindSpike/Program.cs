using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

// Probes for quick find, run by hand on the real machine. Each command only reads, except `select`, which
// switches a tab in a window the person running it names, and `chords`, which registers and at once releases
// global hotkeys. Results go into docs/superpowers/notes/2026-11-04-quick-find-probes.md.
//
//   names                 time reading every tab title of every browser and VS Code window
//   chords                which candidate default chords RegisterHotKey accepts
//   apps                  what shell:AppsFolder lists, with parsing names and link targets
//   select <hwnd> <index> try to select the index-th tab of a browser window through UI Automation

// Before reading any geometry or handles, per CLAUDE.md: without per-monitor awareness Windows virtualises.
Native.SetProcessDpiAwarenessContext(-4);

return args switch
{
    ["names"] => Probes.Names(),
    ["chords"] => Probes.Chords(),
    ["apps"] => Probes.Apps(),
    ["select", var hwnd, var index] => Probes.Select(Convert.ToInt64(hwnd, 16), int.Parse(index)),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("names | chords | apps | select <hwnd hex> <index>");
    return 2;
}

static class Probes
{
    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase) { "msedge", "chrome", "brave", "vivaldi", "opera" };

    public static int Names()
    {
        var windows = Native.TopLevel()
            .Select(h => (Handle: h, Process: Native.ProcessName(h), Title: Native.Title(h)))
            .Where(w => Browsers.Contains(w.Process) || w.Process.Equals("Code", StringComparison.OrdinalIgnoreCase))
            .Where(w => w.Title.Length > 0)
            .ToList();

        var total = Stopwatch.StartNew();
        foreach (var w in windows)
        {
            var one = Stopwatch.StartNew();
            var root = AutomationElement.FromHandle(w.Handle);
            var all = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
                .Cast<AutomationElement>().ToList();
            var code = w.Process.Equals("Code", StringComparison.OrdinalIgnoreCase);
            var tabs = code
                ? all.Where(t => t.Current.ClassName.Split(' ').Contains("tab")).ToList()
                : all.Where(t => !InsidePage(t, root)).ToList();
            one.Stop();
            Console.WriteLine($"{w.Handle:X} {w.Process} {one.ElapsedMilliseconds} ms, {tabs.Count} tabs (of {all.Count} TabItems)  [{Short(w.Title)}]");
            tabs.ForEach(t => Console.WriteLine($"    name='{Short(t.Current.Name)}' class='{Short(t.Current.ClassName)}' select={t.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _)} invoke={t.TryGetCurrentPattern(InvokePattern.Pattern, out _)}"));
        }
        Console.WriteLine($"TOTAL {windows.Count} windows in {total.ElapsedMilliseconds} ms");
        return 0;
    }

    // BrowserTabs' rule: a TabItem counts only when no Document lies between it and the window.
    static bool InsidePage(AutomationElement tab, AutomationElement root)
    {
        var walker = TreeWalker.ControlViewWalker;
        for (var e = walker.GetParent(tab); e is not null && !Automation.Compare(e, root); e = walker.GetParent(e))
            if (e.Current.ControlType == ControlType.Document) return true;
        return false;
    }

    static string Short(string s) => s.Length > 70 ? s[..70] + "…" : s;

    public static int Chords()
    {
        const uint Alt = 1, Ctrl = 2, Shift = 4, Win = 8, NoRepeat = 0x4000;
        (string Name, uint Mods, uint Vk)[] candidates =
        [
            ("Win+Alt+Space", Win | Alt, 0x20),
            ("Win+Ctrl+F", Win | Ctrl, 'F'),
            ("Win+Shift+F", Win | Shift, 'F'),
            ("Ctrl+Alt+Space", Ctrl | Alt, 0x20),
            ("Win+Ctrl+Q", Win | Ctrl, 'Q'),
            ("Win+Alt+F", Win | Alt, 'F'),
        ];
        var id = 0x5100;
        foreach (var c in candidates)
        {
            var ok = Native.RegisterHotKey(0, ++id, c.Mods | NoRepeat, c.Vk);
            var error = ok ? 0 : Marshal.GetLastWin32Error();
            if (ok) Native.UnregisterHotKey(0, id);
            Console.WriteLine($"{c.Name,-16} {(ok ? "free" : $"taken (error {error})")}");
        }
        return 0;
    }

    public static int Apps()
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic folder = ((dynamic)shell).NameSpace("shell:AppsFolder");
        var count = 0;
        foreach (dynamic item in folder.Items())
        {
            count++;
            string name = item.Name, path = item.Path;
            object? target = null;
            try { target = item.ExtendedProperty("System.Link.TargetParsingPath"); } catch { }
            Console.WriteLine($"{Short(name),-40} | {Short(path)} | target={target}");
        }
        Console.WriteLine($"TOTAL {count}");
        return 0;
    }

    public static int Select(long hwnd, int index)
    {
        var root = AutomationElement.FromHandle(new nint(hwnd));
        var tabs = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
            .Cast<AutomationElement>().Where(t => !InsidePage(t, root)).ToList();
        if (index >= tabs.Count) { Console.WriteLine($"only {tabs.Count} tabs"); return 1; }
        var tab = tabs[index];
        Console.WriteLine($"selecting '{Short(tab.Current.Name)}'");
        var sw = Stopwatch.StartNew();
        if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p))
        {
            ((SelectionItemPattern)p).Select();
            Console.WriteLine($"SelectionItem.Select done in {sw.ElapsedMilliseconds} ms; IsSelected now {((SelectionItemPattern)p).Current.IsSelected}");
        }
        else Console.WriteLine("no SelectionItemPattern");
        Thread.Sleep(500);
        Console.WriteLine($"window title now: {Short(Native.Title(new nint(hwnd)))}");
        return 0;
    }
}

static class Native
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(nint value);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(nint hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint hWnd, int id);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, nint lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(nint hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    delegate bool EnumProc(nint hWnd, nint lParam);

    public static List<nint> TopLevel()
    {
        var found = new List<nint>();
        EnumWindows((h, _) => { if (IsWindowVisible(h)) found.Add(h); return true; }, 0);
        return found;
    }

    public static string Title(nint h)
    {
        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ProcessName(nint h)
    {
        GetWindowThreadProcessId(h, out var pid);
        try { return Process.GetProcessById((int)pid).ProcessName; } catch { return ""; }
    }
}
