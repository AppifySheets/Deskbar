using CSharpFunctionalExtensions;
using System.Text.Json.Nodes;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using Deskbar.Core.Rules;

namespace Deskbar.Core.Tests;

public sealed class JsonPersistenceStoreTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"deskbar-tests-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    static AppState SampleState()
    {
        var work = new Workspace(Guid.NewGuid(), "Work", Guid.NewGuid());
        return new AppState(
            [work, new Workspace(Guid.NewGuid(), "Personal", null)],
            [new WorkspaceRule(work.Id, RuleMatchKind.ProcessName, "devenv")],
            [new RenameRule(RuleMatchKind.TitleRegex, "Remote Desktop", "RDP")],
            new Dictionary<Guid, IReadOnlyList<InventoryEntry>>
            {
                [work.Id] = [new InventoryEntry(@"C:\Windows\System32\mstsc.exe", null, "RDP")],
            })
        {
            PersistedRenames = [new PersistedRename("chrome", "Home - Google Chrome", "Home")],
            FloatingBar = new FloatingBarState(120.5, 40.25, true),
            PinnedApps = [new InventoryEntry(@"C:\Programs\Beeper.exe", @"""C:\Programs\Beeper.exe"" ", "Beeper")],
            DetachedApps = [new InventoryEntry(@"C:\Programs\obs.exe", null, "OBS")],
            SwitcherShortcut = "Win+Tab",
        };
    }

    // A state.json written while child workspaces existed names an anchor. It must still be READ (the
    // migration needs it), and after Migrated the group is a plain named group with the old anchor first.
    [Fact]
    public void An_anchored_group_in_an_old_file_loads_as_a_named_group_with_the_anchor_first()
    {
        var group = Guid.NewGuid();
        var child = Guid.NewGuid();
        var anchor = Guid.NewGuid();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"),
            $$"""
            {"Workspaces":[
              {"Id":"{{child}}","Name":"child","DesktopId":"{{Guid.NewGuid()}}","GroupId":"{{group}}"},
              {"Id":"{{anchor}}","Name":"parent","DesktopId":"{{Guid.NewGuid()}}","GroupId":"{{group}}"}],
             "WorkspaceRules":[],"RenameRules":[],"Inventory":{},
             "Groups":[{"Id":"{{group}}","Name":"Work","AnchorWorkspaceId":"{{anchor}}"}]}
            """);

        var loaded = new JsonPersistenceStore(dir).Load().Value;
        Assert.Equal(anchor, Assert.Single(loaded.Groups).AnchorWorkspaceId);

        var migrated = loaded.Migrated();
        Assert.Null(Assert.Single(migrated.Groups).AnchorWorkspaceId);
        Assert.Equal(anchor, migrated.Workspaces[0].Id);
        Assert.Equal("Work", migrated.Groups[0].Name);
    }

    // New files stop carrying the legacy key, so a downgrade-free file is the only shape on disk.
    [Fact]
    public void Saving_a_migrated_state_writes_no_anchor_key()
    {
        var group = Guid.NewGuid();
        var anchor = new Workspace(Guid.NewGuid(), "parent", Guid.NewGuid()) { GroupId = group };
        var state = AppState.Empty with
        {
            Workspaces = [anchor],
            Groups = [new Group(group, "Work") { AnchorWorkspaceId = anchor.Id }],
        };

        Assert.True(new JsonPersistenceStore(dir).Save(state.Migrated()).IsSuccess);

        Assert.DoesNotContain("AnchorWorkspaceId", File.ReadAllText(Path.Combine(dir, "state.json")));
    }

    // The switcher shortcut is an init property with a NON-empty default, unlike every other
    // optional field here, so both halves are worth pinning: a value written by the editor
    // must come back, and a file that predates the setting must load with the default rather
    // than with null.
    [Fact]
    public void Roundtrips_a_configured_switcher_shortcut()
    {
        var store = new JsonPersistenceStore(dir);
        Assert.True(store.Save(SampleState()).IsSuccess);

        Assert.Equal("Win+Tab", store.Load().Value.SwitcherShortcut);
    }

    [Fact]
    public void A_file_written_before_the_switcher_shortcut_existed_loads_with_the_default()
    {
        Directory.CreateDirectory(dir);
        // Exactly the shape an older build wrote: the four required members and nothing else.
        File.WriteAllText(Path.Combine(dir, "state.json"),
            """{"Workspaces":[],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}""");

        Assert.Equal(AppState.DefaultSwitcherShortcut, new JsonPersistenceStore(dir).Load().Value.SwitcherShortcut);
    }

    // Petre: "make window list at the bottom of the bar non-default". Every file written before then holds an
    // explicit "ExpandCurrentWorkspace": false, the bottom list, and must not keep it: that is why the setting was
    // renamed rather than its default flipped (see AppState.WindowListAtBottom). Since the screens became the
    // default, such a file loads with the strips, like every other existing file.
    [Fact]
    public void A_file_that_had_the_bottom_list_by_default_loads_with_the_screens()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"),
            """{"Workspaces":[],"WorkspaceRules":[],"RenameRules":[],"Inventory":{},"ExpandCurrentWorkspace":false}""");

        var loaded = new JsonPersistenceStore(dir).Load().Value;
        Assert.False(loaded.WindowListAtBottom);
        Assert.Equal(WindowListPlace.Screens, loaded.WindowListPlace);
    }

    // Petre: "let's make the strip the default for all users, with the next update". 2.4.126 wrote its choice
    // explicitly, so nearly every file it saved says "WindowListOnScreens": false, the row. That key is no longer
    // read, and such a file loads with the strips (see AppState.WindowListOffScreens).
    [Fact]
    public void A_file_from_2_4_126_with_the_strips_off_loads_with_the_screens()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"),
            """{"Workspaces":[],"WorkspaceRules":[],"RenameRules":[],"Inventory":{},"WindowListAtBottom":false,"WindowListOnScreens":false}""");

        Assert.Equal(WindowListPlace.Screens, new JsonPersistenceStore(dir).Load().Value.WindowListPlace);
    }

    // The previous workspace's item is a choice that starts unmade: a file with no key (every file written before the
    // setting) loads as no choice, so the desk decides (WorkspaceJumps.BackItemWanted), and a choice either way is
    // remembered.
    [Fact]
    public void A_file_with_no_previous_workspace_key_loads_with_no_choice_and_a_choice_is_remembered()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"),
            """{"Workspaces":[],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}""");
        Assert.Null(new JsonPersistenceStore(dir).Load().Value.PreviousWorkspaceItemChoice);

        new JsonPersistenceStore(dir).Save(AppState.Empty with { PreviousWorkspaceItemChoice = false });
        Assert.False(new JsonPersistenceStore(dir).Load().Value.PreviousWorkspaceItemChoice);
        new JsonPersistenceStore(dir).Save(AppState.Empty with { PreviousWorkspaceItemChoice = true });
        Assert.True(new JsonPersistenceStore(dir).Load().Value.PreviousWorkspaceItemChoice);
    }

    // ...and choosing another place after that is remembered.
    [Fact]
    public void Turning_the_strips_off_is_remembered()
    {
        var store = new JsonPersistenceStore(dir);
        store.Save(AppState.Empty with { WindowListOffScreens = true });

        Assert.Equal(WindowListPlace.Row, new JsonPersistenceStore(dir).Load().Value.WindowListPlace);
    }

    // Builds 2.4.77 to 2.5.0 wrote "QuietTaskbar": false into every state.json, chosen or not. It must load as
    // NO choice, so the default (follow auto-hide) reaches those files; that is why the setting moved to a new
    // key (see AppState.QuietTaskbarChoice). The old key rides along in Extra, for a downgrade.
    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public void The_old_quiet_taskbar_key_loads_as_no_choice(string old)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"),
            $$"""{"Workspaces":[],"WorkspaceRules":[],"RenameRules":[],"Inventory":{},"QuietTaskbar":{{old}}}""");

        var loaded = new JsonPersistenceStore(dir).Load().Value;
        Assert.Null(loaded.QuietTaskbarChoice);
        Assert.True(loaded.Extra!.ContainsKey("QuietTaskbar"));
    }

    // A choice made with Manage's box, either way, comes back as that choice and not as "no choice".
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_quiet_taskbar_choice_roundtrips(bool choice)
    {
        var store = new JsonPersistenceStore(dir);
        Assert.True(store.Save(SampleState() with { QuietTaskbarChoice = choice }).IsSuccess);

        Assert.Equal(choice, store.Load().Value.QuietTaskbarChoice);
    }

    // The screens choice round-trips through state.json, and WindowListAtBottom survives next to it so that a
    // build that does not know the screens still lands where he was.
    [Fact]
    public void The_screens_place_is_remembered_beside_the_bottom_choice()
    {
        var store = new JsonPersistenceStore(dir);
        store.Save(AppState.Empty with { WindowListAtBottom = true, WindowListOffScreens = false });

        var loaded = new JsonPersistenceStore(dir).Load().Value;
        Assert.Equal(WindowListPlace.Screens, loaded.WindowListPlace);
        Assert.True(loaded.WindowListAtBottom);
    }

    // Petre: "make this a setting, enabled by default". A file written before the setting existed has no key
    // for it and must load with the split ON, which is why it is stored as its negation (AppState.WholeWindowLines);
    // turning it off is remembered.
    [Fact]
    public void Window_lines_are_split_by_default_and_whole_is_remembered()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"),
            """{"Workspaces":[],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}""");
        Assert.True(new JsonPersistenceStore(dir).Load().Value.SplitWindowLines);

        var store = new JsonPersistenceStore(dir);
        Assert.True(store.Save(AppState.Empty with { WholeWindowLines = true }).IsSuccess);
        Assert.False(store.Load().Value.SplitWindowLines);
    }

    // Petre: "make this default, config option to disable". A file written before the setting existed has no key
    // for it and must load with the whole-bar click ON, which is why it is stored as its negation
    // (AppState.CollapsedIconsPickWindow); turning it off is remembered.
    [Fact]
    public void Collapsed_bars_switch_as_a_whole_by_default_and_icon_clicks_are_remembered()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"),
            """{"Workspaces":[],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}""");
        Assert.True(new JsonPersistenceStore(dir).Load().Value.CollapsedBarSwitchesWhole);

        var store = new JsonPersistenceStore(dir);
        Assert.True(store.Save(AppState.Empty with { CollapsedIconsPickWindow = true }).IsSuccess);
        Assert.False(store.Load().Value.CollapsedBarSwitchesWhole);
    }

    // Choosing the bottom list is still remembered, under its new name.
    [Fact]
    public void The_bottom_list_roundtrips_when_chosen()
    {
        var store = new JsonPersistenceStore(dir);
        Assert.True(store.Save(AppState.Empty with { WindowListAtBottom = true }).IsSuccess);
        Assert.True(store.Load().Value.WindowListAtBottom);
    }

    [Fact]
    public void Roundtrips_full_state()
    {
        var store = new JsonPersistenceStore(dir);
        var state = SampleState();

        Assert.True(store.Save(state).IsSuccess);
        var loaded = store.Load().Value;

        Assert.Equal(state.Workspaces, loaded.Workspaces);
        Assert.Equal(state.WorkspaceRules, loaded.WorkspaceRules);
        Assert.Equal(state.RenameRules, loaded.RenameRules);
        Assert.Equal(state.Inventory.Keys, loaded.Inventory.Keys);
        Assert.Equal(state.Inventory.Values.Single(), loaded.Inventory.Values.Single());
        Assert.Equal(state.PersistedRenames, loaded.PersistedRenames);
        Assert.Equal(state.FloatingBar, loaded.FloatingBar);
        // A pin that does not survive a save/load is the whole reported defect, so both new
        // placement lists are asserted explicitly rather than trusted to the record's shape.
        Assert.Equal(state.PinnedApps, loaded.PinnedApps);
        Assert.Equal(state.DetachedApps, loaded.DetachedApps);
    }

    [Fact]
    public void Old_state_file_without_placement_lists_loads_them_empty()
    {
        // Same back-compat contract as PersistedRenames and FloatingBar: every state.json
        // written before this change has no such keys, and must load as empty rather than
        // throwing or inventing a pinned app.
        Directory.CreateDirectory(dir);
        var oldState = new
        {
            Workspaces = Array.Empty<object>(),
            WorkspaceRules = Array.Empty<object>(),
            RenameRules = Array.Empty<object>(),
            Inventory = new Dictionary<string, object[]>(),
        };
        File.WriteAllText(Path.Combine(dir, "state.json"), System.Text.Json.JsonSerializer.Serialize(oldState));

        var loaded = new JsonPersistenceStore(dir).Load();

        Assert.True(loaded.IsSuccess);
        Assert.Empty(loaded.Value.PinnedApps);
        Assert.Empty(loaded.Value.DetachedApps);
    }

    [Fact]
    public void Missing_file_loads_empty_state_not_failure()
    {
        var loaded = new JsonPersistenceStore(dir).Load();
        Assert.True(loaded.IsSuccess);
        Assert.Empty(loaded.Value.Workspaces);
    }

    [Fact]
    public void Corrupt_file_is_a_failure_not_an_exception()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"), "{ not json !!!");
        Assert.True(new JsonPersistenceStore(dir).Load().IsFailure);
    }

    [Fact]
    public void Old_state_file_without_PersistedRenames_loads_as_empty_list()
    {
        // Backward compatibility: old state.json files have no PersistedRenames key
        // and should deserialize to an empty list (not null, not error).
        Directory.CreateDirectory(dir);
        var oldState = new
        {
            workspaces = new object[0],
            workspaceRules = new object[0],
            renameRules = new object[0],
            inventory = new object[0],
            // Note: no PersistedRenames key
        };
        var json = System.Text.Json.JsonSerializer.Serialize(oldState);
        File.WriteAllText(Path.Combine(dir, "state.json"), json);

        var loaded = new JsonPersistenceStore(dir).Load();

        Assert.True(loaded.IsSuccess);
        Assert.Empty(loaded.Value.PersistedRenames);
    }

    // The stats line detached into its own window (AppState.DetachedStats): the shown flag and the window's place
    // go through the manager into the file and come back, and saving the same thing again writes nothing.
    [Fact]
    public void SaveDetachedStats_round_trips_through_the_store()
    {
        var store = new JsonPersistenceStore(dir);
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);
        Assert.Null(manager.State.DetachedStats);
        var pulses = 0;
        using var pulse = manager.StateChanged.Subscribe(_ => pulses++);

        var detached = new DetachedStatsState(true, 1500.5, 12);
        Assert.True(manager.SaveDetachedStats(detached).IsSuccess);
        Assert.Equal(detached, manager.State.DetachedStats);
        Assert.Equal(detached, store.Load().Value.DetachedStats);
        Assert.Equal(1, pulses);

        // Unchanged: no write, no pulse.
        Assert.True(manager.SaveDetachedStats(detached).IsSuccess);
        Assert.Equal(1, pulses);

        // Put back on the bar: the place is kept for the next time.
        Assert.True(manager.SaveDetachedStats(detached with { Shown = false }).IsSuccess);
        Assert.Equal(new DetachedStatsState(false, 1500.5, 12), store.Load().Value.DetachedStats);
    }

    // The tasks window's place (AppState.TasksWindow), remembered across restarts: through the manager into the file
    // and back, and the same place again writes nothing.
    [Fact]
    public void SaveTasksWindow_round_trips_through_the_store()
    {
        var store = new JsonPersistenceStore(dir);
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);
        Assert.Null(manager.State.TasksWindow);
        var pulses = 0;
        using var pulse = manager.StateChanged.Subscribe(_ => pulses++);

        var place = new TasksWindowState(2600, 140, 1080, 840, Maximized: false);
        Assert.True(manager.SaveTasksWindow(place).IsSuccess);
        Assert.Equal(place, store.Load().Value.TasksWindow);
        Assert.Equal(1, pulses);

        Assert.True(manager.SaveTasksWindow(place).IsSuccess);
        Assert.Equal(1, pulses);
    }

    // The tasks panel's state (AppState.TasksPanel): through the manager into the file and back, and the same state
    // again writes nothing.
    [Fact]
    public void The_panel_state_survives_a_save_and_load()
    {
        var store = new JsonPersistenceStore(dir);
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);
        var pulses = 0;
        using var pulse = manager.StateChanged.Subscribe(_ => pulses++);

        // CurrentWorkspace (task ed1a01fc) is an init property, so it is checked to make the round trip too.
        // HideSubtasks (task 173ef268) likewise.
        var panel = new TasksPanelState(Open: true, Width: 512.5) { CurrentWorkspace = true, HideSubtasks = true };
        Assert.True(manager.SaveTasksPanel(panel).IsSuccess);
        Assert.Equal(panel, store.Load().Value.TasksPanel);
        Assert.Equal(1, pulses);

        Assert.True(manager.SaveTasksPanel(panel).IsSuccess);
        Assert.Equal(1, pulses);
    }

    [Fact]
    public void A_file_without_a_panel_state_reads_as_closed_at_the_default_width()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"),
            """{"Workspaces":[],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}""");

        var loaded = new JsonPersistenceStore(dir).Load();

        Assert.True(loaded.IsSuccess);
        Assert.Null(loaded.Value.TasksPanel);
        var panel = loaded.Value.TasksPanel ?? TasksPanelState.Default;
        Assert.False(panel.Open);
        Assert.Equal(460, panel.Width);
        Assert.False(panel.CurrentWorkspace);
        // An older file shows subtasks, as every build before the checkbox did.
        Assert.False(panel.HideSubtasks);
    }

    [Fact]
    public void An_old_state_file_without_the_key_loads_null()
    {
        // Every state.json written before the detached stats window has no such key: the stats line stays on the
        // bar, and nothing is fabricated.
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"),
            """{"Workspaces":[],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}""");

        var loaded = new JsonPersistenceStore(dir).Load();

        Assert.True(loaded.IsSuccess);
        Assert.Null(loaded.Value.DetachedStats);
    }

    [Fact]
    public void Old_state_file_without_FloatingBar_loads_as_null_ie_hidden_at_default_position()
    {
        // Same back-compat contract as PersistedRenames above: the floating bar is a
        // brand-new (Task 11) feature, so every state.json written before this task has
        // no such key at all. It must deserialize to null (hidden, default position),
        // never throw and never fabricate a visible bar out of nowhere.
        Directory.CreateDirectory(dir);
        var oldState = new
        {
            workspaces = new object[0],
            workspaceRules = new object[0],
            renameRules = new object[0],
            inventory = new object[0],
            // Note: no FloatingBar key
        };
        var json = System.Text.Json.JsonSerializer.Serialize(oldState);
        File.WriteAllText(Path.Combine(dir, "state.json"), json);

        var loaded = new JsonPersistenceStore(dir).Load();

        Assert.True(loaded.IsSuccess);
        Assert.Null(loaded.Value.FloatingBar);
    }

    // --- the previous copy -------------------------------------------------------------------------
    //
    // A colleague's machine lost every workspace when it restarted. Petre: "let's also have a backup for the
    // previous file", "so that if the last one is not available the one before that is". Each save keeps the
    // file it replaces as state.json.previous, and a load that cannot read state.json falls back to it.

    string StateFile => Path.Combine(dir, "state.json");
    string PreviousFile => StateFile + ".previous";

    static AppState Named(params string[] names) =>
        AppState.Empty with { Workspaces = names.Select(n => new Workspace(Guid.NewGuid(), n, Guid.NewGuid())).ToList() };

    static IReadOnlyList<string> Names(AppState state) => state.Workspaces.Select(w => w.Name).ToList();

    [Fact]
    public void Each_save_keeps_the_file_it_replaces_as_the_previous_copy()
    {
        var store = new JsonPersistenceStore(dir);
        store.Save(Named("first"));
        store.Save(Named("second"));

        Assert.Equal(["second"], Names(store.Load().Value));
        Assert.Contains("first", File.ReadAllText(PreviousFile));
        Assert.DoesNotContain("second", File.ReadAllText(PreviousFile));
    }

    // What a power cut can leave behind: the rename reached the disk and the bytes did not, so the file is the
    // right length and every byte of it is zero.
    [Fact]
    public void An_unreadable_state_file_falls_back_to_the_previous_copy_and_is_kept_aside()
    {
        var store = new JsonPersistenceStore(dir);
        store.Save(Named("Work", "Personal"));
        store.Save(Named("Work", "Personal", "Later"));
        File.WriteAllBytes(StateFile, new byte[4096]);

        var loaded = new JsonPersistenceStore(dir).Load();

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error : "");
        Assert.Equal(["Work", "Personal"], Names(loaded.Value));
        // The broken file is moved aside for a look later, never deleted.
        Assert.False(File.Exists(StateFile));
        Assert.Single(Directory.GetFiles(dir, "state.json.*.bak"));
    }

    [Fact]
    public void A_missing_state_file_falls_back_to_the_previous_copy()
    {
        var store = new JsonPersistenceStore(dir);
        store.Save(Named("Work"));
        store.Save(Named("Work", "Personal"));
        File.Delete(StateFile);

        Assert.Equal(["Work"], Names(new JsonPersistenceStore(dir).Load().Value));
    }

    // After a fallback, the next save must not rotate the broken file into the previous slot over the good copy
    // it was just rescued from.
    [Fact]
    public void Saving_after_a_fallback_keeps_the_good_previous_copy()
    {
        var store = new JsonPersistenceStore(dir);
        store.Save(Named("Work"));
        store.Save(Named("Work", "Personal"));
        File.WriteAllBytes(StateFile, new byte[4096]);

        var recovering = new JsonPersistenceStore(dir);
        recovering.Load();
        recovering.Save(Named("Work", "Again"));

        Assert.Equal(["Work", "Again"], Names(recovering.Load().Value));
        Assert.Contains("\"Work\"", File.ReadAllText(PreviousFile));
        // A char, not a string: xUnit compares substrings culture-sensitively, and "\0" is ignorable there, so a
        // string search for it "finds" it in any text.
        Assert.False(File.ReadAllText(PreviousFile).Contains('\0'));
    }

    // Both unreadable: still the failure App answers by backing the file up and telling the user.
    [Fact]
    public void Both_copies_unreadable_is_still_a_failure()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(StateFile, "{ not json !!!");
        File.WriteAllText(PreviousFile, "{ nor this");

        Assert.True(new JsonPersistenceStore(dir).Load().IsFailure);
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        var store = new JsonPersistenceStore(dir);
        store.Save(SampleState());
        Assert.Single(Directory.GetFiles(dir));
    }

    // Finding 5 (reviewer, Important): RuleMatchKind (and any future enum) must serialize
    // as its name, not a bare int -- unreadable on disk and a silent renumbering hazard.
    [Fact]
    public void Enum_fields_serialize_as_names_not_bare_ints()
    {
        var store = new JsonPersistenceStore(dir);
        store.Save(SampleState());

        var raw = File.ReadAllText(Path.Combine(dir, "state.json"));

        Assert.Contains("\"TitleRegex\"", raw);   // RenameRule.Kind
        Assert.Contains("\"ProcessName\"", raw);  // WorkspaceRule.Kind
        Assert.DoesNotContain("\"Kind\": 0", raw);
        Assert.DoesNotContain("\"Kind\": 1", raw);
    }

    [Fact]
    public void Enum_fields_still_roundtrip_correctly_when_written_as_names()
    {
        var store = new JsonPersistenceStore(dir);
        var state = SampleState();
        store.Save(state);

        var loaded = store.Load().Value;

        Assert.Equal(RuleMatchKind.ProcessName, loaded.WorkspaceRules.Single().Kind);
        Assert.Equal(RuleMatchKind.TitleRegex, loaded.RenameRules.Single().Kind);
    }

    // A newer build can add a StripItem and save it as hidden. This build must load the rest of that file instead
    // of treating it as corrupt (which would move it aside and fall back to .previous), so it skips the name it
    // does not know and keeps the ones it does.
    [Fact]
    public void A_hidden_strip_item_this_build_does_not_know_is_skipped_and_the_file_still_loads()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"),
            """{"Workspaces":[],"WorkspaceRules":[],"RenameRules":[],"Inventory":{},"HiddenStripItems":["Weather","Clock"]}""");

        var result = new JsonPersistenceStore(dir).Load();

        Assert.True(result.IsSuccess);
        Assert.Equal([StripItem.Clock], result.Value.HiddenStripItems);
        Assert.Empty(Directory.GetFiles(dir, "*.bak*"));
        Assert.True(File.Exists(Path.Combine(dir, "state.json")));
    }

    [Fact]
    public void Every_hidden_strip_item_roundtrips_and_is_saved_by_name()
    {
        var store = new JsonPersistenceStore(dir);
        var all = Enum.GetValues<StripItem>();
        Assert.True(store.Save(SampleState() with { HiddenStripItems = all }).IsSuccess);

        Assert.Equal(all, store.Load().Value.HiddenStripItems);
        Assert.Contains("\"Clock\"", File.ReadAllText(Path.Combine(dir, "state.json")));
    }

    // A state.json an older build wrote: TileSizes holds the grid, TileLayouts has no key. Save writes it as such
    // (migration is Load's job), so this is the same text the old build produced.
    static readonly Deskbar.Core.Tiling.TileSizes OldGrid = new([0.3, 0.7], [[1.0], [0.25, 0.75]]);

    string WriteOldState()
    {
        var old = new Workspace(Guid.NewGuid(), "Old", null) { TileSizes = [new ScreenTileSizes("0,0,1920,1080", OldGrid)] };
        Assert.True(new JsonPersistenceStore(dir).Save(AppState.Empty with { Workspaces = [old] }).IsSuccess);
        return Path.Combine(dir, "state.json");
    }

    [Fact]
    public void Old_sizes_load_as_layouts()
    {
        WriteOldState();

        var loaded = new JsonPersistenceStore(dir).Load().Value.Workspaces.Single();

        Assert.Empty(loaded.TileSizes);
        var entry = Assert.Single(loaded.TileLayouts);
        Assert.Equal("0,0,1920,1080", entry.Screen);
        Assert.Equal(Deskbar.Core.Tiling.TileLayout.FromGrid(OldGrid), entry.Layout);
    }

    // An older build reading a tree in TileSizes would crash, so a saved state never carries sizes: the trees live
    // only in TileLayouts (design, "Storing and migration").
    [Fact]
    public void Saved_state_keeps_the_old_property_empty()
    {
        var path = WriteOldState();
        var persistence = new JsonPersistenceStore(dir);

        Assert.True(persistence.Save(persistence.Load().Value).IsSuccess);

        var text = File.ReadAllText(path);
        Assert.Contains("\"TileSizes\": []", text);
        Assert.Contains("\"TileLayouts\"", text);
        Assert.Contains("\"kind\"", text);
    }

    // A state that already has layouts is not migrated again: its (newer) layouts win over any stale sizes.
    [Fact]
    public void Existing_layouts_are_not_overwritten_by_old_sizes()
    {
        var layout = Deskbar.Core.Tiling.TileLayout.Automatic(2);
        var w = new Workspace(Guid.NewGuid(), "W", null)
        {
            TileSizes = [new ScreenTileSizes("s", OldGrid)],
            TileLayouts = [new ScreenTileLayout("s", layout)],
        };
        Assert.True(new JsonPersistenceStore(dir).Save(AppState.Empty with { Workspaces = [w] }).IsSuccess);

        var loaded = new JsonPersistenceStore(dir).Load().Value.Workspaces.Single();

        Assert.Equal(layout, loaded.TileLayouts.Single().Layout);
    }

    // An old entry with an opening: the opening rides along as the layout's opening, so room kept for the next
    // window outlives the upgrade (ported from the TileSizes setting tests).
    [Fact]
    public void Old_sizes_with_an_opening_load_as_a_layout_with_it()
    {
        var opened = new Deskbar.Core.Tiling.TileSizes([0.5, 0.5], [[1.0], [0.4, 0.6]]) { Opening = 1 };
        var old = new Workspace(Guid.NewGuid(), "Old", null) { TileSizes = [new ScreenTileSizes("0,0,1920,1080", opened)] };
        Assert.True(new JsonPersistenceStore(dir).Save(AppState.Empty with { Workspaces = [old] }).IsSuccess);

        var layout = new JsonPersistenceStore(dir).Load().Value.Workspaces.Single().TileLayouts.Single().Layout;

        Assert.Equal(Deskbar.Core.Tiling.TileLayout.FromGrid(opened), layout);
        Assert.Equal(1, layout.Opening);
    }

    // The tiling keys as a hand-edited state.json can have them. Missing or null, each list loads EMPTY, never null:
    // the Tiler's state key and the menus read them without a null check (ValueListConverter, final review item 8).
    [Theory]
    [InlineData("")]
    [InlineData(",\"TileSizes\":null")]
    [InlineData(",\"TileLayouts\":null")]
    [InlineData(",\"TileSizes\":null,\"TileLayouts\":null")]
    public void Missing_or_null_tiling_lists_load_empty(string keys)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"),
            $$$"""{"Workspaces":[{"Id":"{{{Guid.NewGuid()}}}","Name":"Old","DesktopId":null{{{keys}}}}],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}""");

        var loaded = new JsonPersistenceStore(dir).Load();

        Assert.True(loaded.IsSuccess);
        var workspace = loaded.Value.Workspaces.Single();
        Assert.NotNull(workspace.TileSizes);
        Assert.Empty(workspace.TileSizes);
        Assert.NotNull(workspace.TileLayouts);
        Assert.Empty(workspace.TileLayouts);
    }

    // An old TileSizes entry that cannot describe a grid is DROPPED by the migration, and the rest of the file loads.
    // A throw inside the parse would make DurableFile take the whole file for corrupt and fall back to
    // state.json.previous, losing every other change since. The good entry beside it still migrates.
    [Theory]
    [InlineData("""{"Columns":[0.5,0.5],"Rows":[[1.0]]}""")]                 // fewer row lists than columns
    [InlineData("""{"Columns":[0.5,0.5],"Rows":[[1.0],[1.0],[1.0]]}""")]     // more row lists than columns
    [InlineData("""{"Columns":[0.5,0.5],"Rows":[[1.0],null]}""")]            // a null row list
    [InlineData("""{"Columns":null,"Rows":[[1.0]]}""")]                      // no columns
    [InlineData("""{"Columns":[1.0],"Rows":null}""")]                        // no rows
    [InlineData("""{"Columns":[],"Rows":[]}""")]                             // an empty shape
    [InlineData("""{"Columns":[1.0],"Rows":[[]]}""")]                        // a column with no rows
    [InlineData("""{"Columns":[0.5,0.5],"Rows":[[1.0],[]]}""")]              // one of the columns with no rows
    [InlineData("null")]                                                     // no sizes at all
    public void A_malformed_old_sizes_entry_is_dropped_and_the_rest_loads(string sizes)
    {
        Directory.CreateDirectory(dir);
        var good = """{"Screen":"good","Sizes":{"Columns":[0.3,0.7],"Rows":[[1.0],[0.25,0.75]]}}""";
        File.WriteAllText(Path.Combine(dir, "state.json"),
            $$$"""{"Workspaces":[{"Id":"{{{Guid.NewGuid()}}}","Name":"Old","DesktopId":null,"TileSizes":[{"Screen":"bad","Sizes":{{{sizes}}}},{{{good}}},null]}],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}""");
        var fellBack = new List<string>();

        var loaded = new JsonPersistenceStore(dir, fellBack.Add).Load();

        Assert.True(loaded.IsSuccess);
        Assert.Empty(fellBack);
        var workspace = loaded.Value.Workspaces.Single();
        Assert.Equal("Old", workspace.Name);
        Assert.Empty(workspace.TileSizes);
        var entry = Assert.Single(workspace.TileLayouts);
        Assert.Equal("good", entry.Screen);
        Assert.Equal(Deskbar.Core.Tiling.TileLayout.FromGrid(OldGrid), entry.Layout);
    }

    // A null entry in TileLayouts (hand-edited) is dropped too, rather than reaching the Tiler.
    [Fact]
    public void A_null_layout_entry_is_dropped()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"),
            $$$"""{"Workspaces":[{"Id":"{{{Guid.NewGuid()}}}","Name":"Old","DesktopId":null,"TileLayouts":[null,{"Screen":"s","Layout":null}]}],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}""");

        var loaded = new JsonPersistenceStore(dir).Load();

        Assert.True(loaded.IsSuccess);
        Assert.Empty(loaded.Value.Workspaces.Single().TileLayouts);
    }

    // A tree entry that cannot be read is DROPPED on its own (whole-branch review, item 4). TileNode's discriminator is
    // "kind", and a missing or unknown one made the whole Deserialize throw, which DurableFile reads as a corrupt file:
    // it moved state.json aside and loaded state.json.previous, losing every other change since for one tiling entry.
    // A tree that parses but is no layout (shares that do not sum to 1 in a split, a share at or below 0, a split with
    // no children) is dropped too, rather than reaching the Tiler. The good entry beside it loads, every other part of
    // the state is kept, and no fallback happens.
    [Theory]
    [InlineData("""{"Screen":"bad","Layout":{"Root":{"Axis":"Across","Children":[]}}}""")]                       // no kind
    [InlineData("""{"Screen":"bad","Layout":{"Root":{"kind":"blob"}}}""")]                                         // unknown kind
    [InlineData("""{"Screen":"bad","Layout":{"Root":{"kind":"split","Axis":"Down","Children":[{"Node":{"kind":"what"},"Share":1}]}}}""")] // a bad kind deeper down
    [InlineData("""{"Screen":"bad","Layout":{"Root":{"kind":"split","Axis":"Across","Children":[{"Node":{"kind":"leaf"},"Share":0.5},{"Node":{"kind":"leaf"},"Share":0.4}]}}}""")] // shares sum to 0.9
    [InlineData("""{"Screen":"bad","Layout":{"Root":{"kind":"split","Axis":"Across","Children":[{"Node":{"kind":"leaf"},"Share":-0.5},{"Node":{"kind":"leaf"},"Share":1.5}]}}}""")] // a negative share
    [InlineData("""{"Screen":"bad","Layout":{"Root":{"kind":"split","Axis":"Across","Children":[{"Node":{"kind":"leaf"},"Share":0},{"Node":{"kind":"leaf"},"Share":1}]}}}""")] // a zero share
    [InlineData("""{"Screen":"bad","Layout":{"Root":{"kind":"split","Axis":"Down"}}}""")]                         // no children
    [InlineData("""{"Screen":"bad","Layout":{"Root":{"kind":"split","Axis":"Down","Children":[null]}}}""")]       // a null child
    [InlineData("""{"Screen":"bad","Layout":{"Root":{"kind":"split","Axis":"Down","Children":[{"Share":1}]}}}""")] // a child with no node
    public void A_tree_entry_that_cannot_be_read_is_dropped_and_the_rest_loads(string bad)
    {
        var good = Deskbar.Core.Tiling.TileLayout.FromGrid(OldGrid);
        var state = SampleState();
        var work = state.Workspaces[0] with { TileLayouts = [new ScreenTileLayout("good", good)] };
        Assert.True(new JsonPersistenceStore(dir).Save(state with { Workspaces = [work, state.Workspaces[1]] }).IsSuccess);

        // The bad entry goes in by hand, first, before the good one.
        var file = JsonNode.Parse(File.ReadAllText(StateFile))!;
        file["Workspaces"]![0]!["TileLayouts"]!.AsArray().Insert(0, JsonNode.Parse(bad));
        File.WriteAllText(StateFile, file.ToJsonString());
        var fellBack = new List<string>();

        var loaded = new JsonPersistenceStore(dir, fellBack.Add).Load();

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error : "");
        Assert.Empty(fellBack);
        Assert.Empty(Directory.GetFiles(dir, "*.bak"));
        var entry = Assert.Single(loaded.Value.Workspaces[0].TileLayouts);
        Assert.Equal("good", entry.Screen);
        Assert.Equal(good, entry.Layout);
        // Everything else came through.
        Assert.Equal(["Work", "Personal"], Names(loaded.Value));
        Assert.Equal(state.WorkspaceRules.Single().Pattern, loaded.Value.WorkspaceRules.Single().Pattern);
        Assert.Equal(state.RenameRules, loaded.Value.RenameRules);
        Assert.Equal(state.FloatingBar, loaded.Value.FloatingBar);
        Assert.Equal("Win+Tab", loaded.Value.SwitcherShortcut);
    }

    // A tree stored outside normal form (a hand edit: a split of one child, a split inside one of its own axis) loads
    // as its normal form, which lays out the same.
    [Fact]
    public void A_loaded_tree_is_brought_to_normal_form()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(StateFile,
            $$$$"""{"Workspaces":[{"Id":"{{{{Guid.NewGuid()}}}}","Name":"W","DesktopId":null,"TileLayouts":[{"Screen":"s","Layout":{"Root":{"kind":"split","Axis":"Across","Children":[{"Node":{"kind":"leaf"},"Share":0.5},{"Node":{"kind":"split","Axis":"Across","Children":[{"Node":{"kind":"leaf"},"Share":0.5},{"Node":{"kind":"split","Axis":"Down","Children":[{"Node":{"kind":"leaf"},"Share":1}]},"Share":0.5}]},"Share":0.5}]}}}]}],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}""");

        var layout = new JsonPersistenceStore(dir).Load().Value.Workspaces.Single().TileLayouts.Single().Layout;

        Assert.Equal(Deskbar.Core.Tiling.TileLayout.Normal(
            new Deskbar.Core.Tiling.TileBranch(Deskbar.Core.Tiling.Axis.Across,
                new[] { .5, .25, .25 }.Select(s => new Deskbar.Core.Tiling.TileChild(Deskbar.Core.Tiling.TileLeaf.Instance, s)).ToValueList()),
            null), layout);
    }
}
