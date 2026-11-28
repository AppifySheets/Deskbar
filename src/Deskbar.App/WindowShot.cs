using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// A TASK FROM THE WINDOW ON SCREEN (Deskbar task 494d34ee; Petre: "add a task from the window open on the screen as a
// separate button near the urgent/important buttons, to start immediately in the current workspace").
//
// The add rows of the bar and of the tasks panel have a camera button beside the important and urgent toggles. It takes
// a picture of the window Petre was working in (the one in front before he clicked Deskbar), adds a task with that
// picture and whatever the box holds, and starts Claude on it in the background, exactly as the play button does. This
// file is the half both rows share: which window counts, the picture, and the line that names the window in the task's
// description so the session knows what it is looking at without guessing from pixels.
//
// THE PICTURE is WindowPreview's capture at full size: PrintWindow with PW_RENDERFULLCONTENT, which renders the window
// itself, so the topmost bar or a panel lying over it is never in the picture (measured for the hover card, #134; see
// WindowPreview's header). GetWindowRect includes the invisible resize border Windows 10 and 11 draw around a window,
// which PrintWindow paints black, so the picture is cut to DWM's extended frame bounds, the window as it looks. The app is
// PerMonitorV2 (app.manifest), so both rectangles are physical pixels and line up; if they ever disagree the cut is
// clamped to the picture and does nothing worse than leave the border on.
//
// NOT A PICTURE: nothing at all, the desktop or the taskbar (a click on the desktop leaves Progman in front, and a picture
// of the wallpaper is not a task), Deskbar's own windows, and a minimised window (WindowPreview measured that one blank).
static class WindowShot
{
    /// <summary>The picture of a window and the line naming it, for the new task's description.</summary>
    public sealed record Shot(TaskImageFiles.Pending Picture, string Caption);

    /// <summary>What the refusal says when there is no window to take a picture of.</summary>
    public const string NoWindowText = "There is no window on this desktop to take a picture of. Click the window first, then the button.";

    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out RECT value, int size);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    // The picture is never wider than this before TaskImageFiles narrows it to its own MaxWidth: the box WindowPreview
    // shrinks into, large enough that a window on any screen today comes out at its own size.
    const int LargestSide = 16384;

    /// <summary>
    /// The picture of <paramref name="hwnd"/> and the line naming it, or why there is none (see the header for what is
    /// not a picture). Runs on the UI thread: one PrintWindow, about 30 to 50 ms measured for the hover card.
    /// </summary>
    public static Result<Shot> Of(nint hwnd) =>
        IsPictureWindow(hwnd)
            ? WindowPreview.Of(new WindowHandle(hwnd), 1.0, LargestSide, LargestSide)
                .ToResult(NoWindowText)
                .Map(full => CutToFrame(hwnd, full))
                .Bind(TaskImageFiles.FromBitmap)
                .Map(picture => new Shot(picture, CaptionOf(TitleOf(hwnd), ProcessNameOf(hwnd))))
            : Result.Failure<Shot>(NoWindowText);

    /// <summary>
    /// The line under the typed words in the new task's description: which window the picture is of, by its title and
    /// program, so the session reads what it is looking at. Pure, for the tests.
    /// </summary>
    internal static string CaptionOf(string title, string processName) =>
        (title.Trim(), processName.Trim()) switch
        {
            ("", "") => "Picture of the window on screen.",
            ("", var process) => $"Picture of the {process} window on screen.",
            (var text, "") => $"Picture of the window \"{text}\" on screen.",
            var (text, process) => $"Picture of the window \"{text}\" ({process}) on screen.",
        };

    /// <summary>
    /// The new task's description text with the caption under what was typed after the title (a blank line between),
    /// or the caption alone when nothing was. The pictures follow it (TaskImages.Join). Shared by both add rows.
    /// </summary>
    internal static string WithCaption(string body, string caption) =>
        string.Join("\n\n", new[] { body, caption }.Where(part => part.Length > 0));

    // A window worth a picture: a real, visible top-level window that is neither Deskbar's own nor the shell's desktop
    // or taskbar. Minimised is left to WindowPreview, which declines it.
    static bool IsPictureWindow(nint hwnd) =>
        hwnd != 0 && IsWindowVisible(hwnd) && !FloatingBar.IsOwnWindow(hwnd) && !ShellKeys.IsBareTaskbar(hwnd)
        && ClassOf(hwnd) is not ("Progman" or "WorkerW");

    // The invisible resize border off (see the header): the frame bounds relative to the window's rectangle, clamped to
    // the picture. Anything that cannot be read leaves the picture as it was.
    static BitmapSource CutToFrame(nint hwnd, BitmapSource full)
    {
        if (!GetWindowRect(hwnd, out var outer)
            || DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) != 0) return full;
        var left = Math.Clamp(frame.Left - outer.Left, 0, full.PixelWidth - 1);
        var top = Math.Clamp(frame.Top - outer.Top, 0, full.PixelHeight - 1);
        var width = Math.Clamp(frame.Right - frame.Left, 1, full.PixelWidth - left);
        var height = Math.Clamp(frame.Bottom - frame.Top, 1, full.PixelHeight - top);
        if (left == 0 && top == 0 && width == full.PixelWidth && height == full.PixelHeight) return full;
        var cut = new CroppedBitmap(full, new Int32Rect(left, top, width, height));
        // Frozen like every WPF object handed around here (CLAUDE.md, "Static WPF brushes must be frozen").
        cut.Freeze();
        return cut;
    }

    // CharSet.Unicode on every string call: without it the W entry points read ANSI buffers and return nothing.
    static string TitleOf(nint hwnd)
    {
        var buffer = new StringBuilder(512);
        return NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    static string ClassOf(nint hwnd)
    {
        var buffer = new char[256];
        var length = NativeMethods.GetClassNameW(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    // Empty when it cannot be read (the process is gone, or not ours to look at); never throws.
    static string ProcessNameOf(nint hwnd)
    {
        if (NativeMethods.GetWindowThreadProcessId(hwnd, out var pid) == 0) return "";
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return "";
        }
    }
}
