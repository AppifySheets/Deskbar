using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;
using Deskbar.Core.Geometry;

namespace Deskbar.Windows.Shell;

// The picture on one screen, as Windows reports it: the file, how it is laid on the screen, the screen's rectangle
// and the whole desktop's (Span lays one picture across every screen). Rectangles in physical pixels.
public sealed record ScreenWallpaper(string Path, WallpaperFit Fit, WallpaperCrop.Rect Screen, WallpaperCrop.Rect Desktop);

// WHICH WALLPAPER IS ON A SCREEN, for the docked bar's frosted band (FloatingBar.Frost.cs, WallpaperCrop).
//
// IDesktopWallpaper, Windows' own per-monitor wallpaper interface, rather than the registry. Measured on Petre's
// desktop on 2026-11-22: HKCU\Control Panel\Desktop\WallPaper named a Spotlight picture written on 19 November, and
// IDesktopWallpaper named the same file for both screens; on his laptop, which shows a different picture, which of
// the two follows Spotlight's daily change still has to be measured. The interface is the one that can answer per
// screen, and it is what Settings itself writes through, so it is the better bet.
//
// Called off the UI thread on purpose: a COM call on the bar's STA thread pumps its message queue, and the bar's
// rebuild has been re-entered that way before (CLAUDE.md, traps). The DesktopWallpaper object is free-threaded
// enough to be made and used on a pool thread.
public static class DesktopWallpaper
{
    // The picture on the screen whose rectangle is `screen`, or None when Windows has no picture file there (a solid
    // colour, a slideshow between pictures) or the screen is not one Windows knows (it is waking, or just went).
    public static Maybe<ScreenWallpaper> On(WallpaperCrop.Rect screen)
    {
        IDesktopWallpaper? wallpaper = null;
        try
        {
            wallpaper = (IDesktopWallpaper)Activator.CreateInstance(Type.GetTypeFromCLSID(DesktopWallpaperClass)!)!;
            var monitors = Enumerable.Range(0, (int)wallpaper.GetMonitorDevicePathCount())
                .Select(index => wallpaper.GetMonitorDevicePathAt((uint)index))
                .Select(id => (Id: id, Rect: RectOf(wallpaper, id)))
                .Where(monitor => monitor.Rect.Width > 0)
                .ToList();
            if (monitors.Count == 0) return Maybe<ScreenWallpaper>.None;

            var desktop = new WallpaperCrop.Rect(
                monitors.Min(m => m.Rect.Left), monitors.Min(m => m.Rect.Top),
                monitors.Max(m => m.Rect.Right), monitors.Max(m => m.Rect.Bottom));
            var fit = (WallpaperFit)wallpaper.GetPosition();

            return monitors.Where(m => m.Rect == screen)
                .Select(m => wallpaper.GetWallpaper(m.Id))
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Select(path => new ScreenWallpaper(path, fit, screen, desktop))
                .TryFirst();
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            // No answer is an ordinary outcome here (the shell restarting, a session without a desktop): the bar
            // keeps its plain see-through band.
            return Maybe<ScreenWallpaper>.None;
        }
        finally
        {
            if (wallpaper is not null) Marshal.ReleaseComObject(wallpaper);
        }
    }

    // A monitor's rectangle, or an empty one for a monitor id that no longer answers (detached since the count).
    static WallpaperCrop.Rect RectOf(IDesktopWallpaper wallpaper, string id)
    {
        try
        {
            wallpaper.GetMonitorRECT(id, out var r);
            return new WallpaperCrop.Rect(r.Left, r.Top, r.Right, r.Bottom);
        }
        catch (COMException)
        {
            return default;
        }
    }

    static readonly Guid DesktopWallpaperClass = new("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD");

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    // shobjidl_core.h, in vtable order: every method up to GetPosition is declared so the slots line up, including
    // the ones this never calls.
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint monitorIndex);
        uint GetMonitorDevicePathCount();
        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out RECT displayRect);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(int position);
        int GetPosition();
    }
}
