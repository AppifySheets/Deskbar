using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.App;

// What the docked bar announces through FloatingBar.Strip: where its shell app bar now stands, so the tasks panel can
// register its own app bar AFTER it and so stay inside it (spec 2026-11-23-tasks-panel-design.md, "Keeping the bar
// outside").
//
// All in physical virtual-screen pixels, the units GetMonitorInfo's rcMonitor and GetWindowRect give and the shell's
// app bar messages speak; the panel converts to DIPs with its own monitor's DPI, as DockTo does.
//
//   Edge       "left", "right" or "seam" while docked; null when the strip was let go (undock, minimize, the
//              display-layout readjustment letting go before it docks again, the bar closing).
//   Screen     the bar's monitor (rcMonitor). For a seam dock, the LEFT-hand screen of the seam. For a let-go, the
//              screen the bar is on now.
//   SeamRight  the seam's right-hand screen when Edge is "seam" (the panel docks on its left edge, inside the bar's
//              right half), None otherwise.
//   MiddleX    the bar window's horizontal middle, from GetWindowRect, read after the window was moved into the strip
//              the shell granted (or, for a let-go, after it went back to floating).
internal sealed record BarStrip(string? Edge, WindowRect Screen, Maybe<WindowRect> SeamRight, int MiddleX);
