using System.IO;
using System.Text.Json;
using TaskSpaces.Core;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.App;

// The aging icons' memory across restarts of the app (FloatingBar.Aging.cs). A window outlives the app watching
// it, and the app restarts often (every new build swapped in), so without this every restart would make every
// icon fresh again and hide 15 minutes of aging each time.
//
// %APPDATA%\TaskSpaces\last-used.json, written once a minute (a kill skips any exit handler, which is how
// builds are swapped), read once at startup. Separate from state.json on purpose: this is volatile, rewritten
// all day, and worthless after Windows restarts, when no handle in it can match anything.
static class LastUsedFile
{
    sealed record Entry(long Window, int ProcessId, DateTimeOffset At);

    public static void Restore(WorkspaceManager manager, string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            (JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [])
                .ForEach(e => manager.RestoreLastUsed(new WindowHandle((nint)e.Window), e.ProcessId, e.At));
        }
        catch
        {
            // A file that will not read is a fresh start, which is what there was before this existed.
        }
    }

    public static void Save(WorkspaceManager manager, string path)
    {
        try
        {
            var entries = manager.LastUsedTimes().Select(t => new Entry(t.Window.Value, t.ProcessId, t.At)).ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(entries));
        }
        catch
        {
            // The next minute tries again.
        }
    }
}
