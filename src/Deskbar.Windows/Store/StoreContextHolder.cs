using Windows.Services.Store;

namespace Deskbar.Windows.Store;

// The one StoreContext this process uses, shared by StoreLicence (the licence read) and StoreUpdates (the
// update check and install). Design: docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md.
//
// WHY ONE INSTANCE, initialised once. A desktop (non-UWP) app must tie a StoreContext to a window through
// IInitializeWithWindow before any call that can raise Store UI. Before this class, every licence read and
// every update check took StoreContext.GetDefault() and initialised it again with the bar's hwnd, every few
// minutes for the life of the process. Whether GetDefault hands back a fresh object or the same one, and
// what a second Initialize on an already initialised context does, has never been measured. If it throws,
// the throw lands in the callers' catch-all and comes back as an ordinary failure: the background check is
// silent about failures by design, so every later update check would fail without anyone seeing why. One
// context, initialised exactly once, cannot hit that.
//
// The window is the first NON-ZERO hwnd any caller passes. IntPtr.Zero (no bar, as in compatibility mode)
// leaves the context uninitialised, which a licence read and an update check do not need since they show
// no UI; the install does, and StoreUpdates.InstallAsync refuses a zero hwnd before it gets here.
//
// A failure is never cached (the briefing's trap: never cache a lookup failure that can succeed later). If
// GetDefault throws, nothing is kept and the next call tries again. If Initialize throws, the context is
// dropped rather than kept half-initialised, and the next call retries from GetDefault. Whether that hands
// back the same object (so the retry is a second Initialize on it) is the unmeasured part above; either way
// a throw there is an ordinary failure for that one call, and a later call can still succeed.
static class StoreContextHolder
{
    // Licence reads and update checks can overlap (both run off the update timer and the first is fired at
    // startup without waiting), so the two fields change together under one lock.
    static readonly Lock Gate = new();
    static StoreContext? context;
    static bool tiedToWindow;

    public static StoreContext Get(IntPtr ownerHwnd)
    {
        lock (Gate)
        {
            context ??= StoreContext.GetDefault();

            if (!tiedToWindow && ownerHwnd != IntPtr.Zero)
            {
                try
                {
                    // The projection's own helper for IInitializeWithWindow
                    // (3E68D4BD-7135-4D10-8018-9FB6D9F33FA1), so no hand-written COM interface or P/Invoke.
                    WinRT.Interop.InitializeWithWindow.Initialize(context, ownerHwnd);
                    tiedToWindow = true;
                }
                catch
                {
                    context = null;
                    throw;
                }
            }

            return context;
        }
    }
}
