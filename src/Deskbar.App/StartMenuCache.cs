using Deskbar.Core.QuickFind;
using Deskbar.Windows.Shell;

namespace Deskbar.App;

// The Start menu's app list for quick find, read on a pool thread and kept for a minute. Reading it walks every
// shortcut, which is too slow for the UI thread and too slow to do each time the box refreshes; apps are
// installed rarely enough that a list a minute old is fine. Asked to refresh whenever quick find opens or
// refreshes, so a newly installed app shows up within a minute without a restart.
public sealed class StartMenuCache
{
    static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);

    IReadOnlyList<StartMenuApp> apps = [];
    DateTime readAt = DateTime.MinValue;
    int reading;

    // The last list read, empty before the first read finishes.
    public IReadOnlyList<StartMenuApp> Apps => Volatile.Read(ref apps);

    public void RefreshIfStale()
    {
        if (DateTime.UtcNow - readAt < Fresh || Interlocked.Exchange(ref reading, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                Volatile.Write(ref apps, StartMenuApps.Read());
            }
            catch (Exception e)
            {
                // A shell that will not answer leaves the last list in place; quick find still finds what is open.
                ClickTrace.Write($"quick find: reading the Start menu failed: {e.Message}");
            }
            finally
            {
                // Stamped on failure too, so a broken shell is asked once a minute rather than every second
                // (final review, M8).
                readAt = DateTime.UtcNow;
                Volatile.Write(ref reading, 0);
            }
        });
    }
}
