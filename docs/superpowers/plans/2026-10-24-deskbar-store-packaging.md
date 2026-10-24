# Deskbar Store Packaging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Microsoft Store channel: an MSIX built in CI from the same tag as the direct exe, an app that knows when it is packaged and behaves accordingly, and the "am I Pro?" answer the free tier's gate will ask.

**Architecture:** Every decision that can be pure lives in `Deskbar.Core` as a plan or a verdict with tests (`PackagedTakeover`, `StateVersionGate`, `ProEntitlement`, `OtherInstance`, `CommandLineQuoting`); the Windows side (`Deskbar.Windows`) holds the P/Invokes and the WinRT calls behind small static classes; the App wires them in `OnStartup` keyed on one static, `PackageIdentity.IsPackaged`. Packaging is a hand-written manifest, a checked-in image set with a resource index, and one PowerShell script with Pester tests, run by CI as a sibling of the direct build. A new console project, `Deskbar.Cli`, ships inside the package only, behind the app execution alias.

**Tech Stack:** .NET 10, WPF, C#/WinRT projections from the Windows SDK (`Windows.ApplicationModel.StartupTask`, `Windows.Services.Store.StoreContext`), xunit, Windows SDK tools (`MakeAppx.exe`, `MakePri.exe`, `signtool.exe`), PowerShell 5.1 with Pester 3.4, GitHub Actions on `windows-latest`.

**Spec:** `docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md` (and its frame, `docs/superpowers/specs/2026-10-18-deskbar-commercial-release-design.md`).

## Global Constraints

- Identity values, verbatim: `Package/Identity/Name` = `Petre.Deskbar`; `Package/Identity/Publisher` = `CN=804EE50D-C32E-4285-AAF6-E1C851F9FCEF`; Package Family Name = `Petre.Deskbar_n8ch8v53xw03y`; Store ID = `9PJX26NGFPNF`; `PublisherDisplayName` = `AppifySheets` (Partner Center shows the value in force; use that one).
- Package version is `VersionPrefix` plus `.0`; `TargetDeviceFamily` `Windows.Desktop` `MinVersion="10.0.22000.0"` `MaxVersionTested="10.0.26100.0"`. The project TFM stays `net10.0-windows10.0.19041.0`.
- The Store publish is `dotnet publish -c Release -r win-x64 --self-contained true -p:DebugType=none -p:PublishSingleFile=false`; never `PublishTrimmed`.
- Startup task id `Deskbar`, `Enabled="true"`, display name `Deskbar`. Alias `deskbar.exe` onto `deskbar-cli.exe`. Alias path on a machine: `%LOCALAPPDATA%\Microsoft\WindowsApps\deskbar.exe`.
- Dialog copy, exact: already running, Store copy: `The copy installed from the Microsoft Store is running.`; already running, other folder: `The copy at <folder> is running.`; newer state: `Your settings were saved by Deskbar <newer>; this is <running>. Start the newer copy, or remove it and delete the WrittenBy line from state.json to go on with this one.`; Manage's warning in the direct build: `The Microsoft Store copy of Deskbar starts with Windows. Ticking this starts both.`; Store update offer: question `Update Deskbar from the Store?`, button `Update from the Store`; fallback line when restart is not proven: `Deskbar will close to update; open it from Start afterwards.`
- Entitlement reasons, exact strings: `Store licence`, `Store trial until <yyyy-MM-dd>`, `Free tier`, `Store licence unavailable`, `Developer override`. Override variable `DESKBAR_PRO=1`, honoured only when `AppPaths.RootOverridden` is true.
- Any run of the app during this work uses `DESKBAR_APPDATA` set to a scratch folder and never Petre's real profile; Petre's bar is never left down (stop, build, start in one command). A test install of the package on Petre's machine needs his consent and is removed before he signs out.
- C# style from the global CLAUDE.md: `private` implied, expression bodies, `var`, immutable records, readonly collections, `Result`/`Result<T>` from CSharpFunctionalExtensions for expected failures, lambdas over loops, ample comments that say why. Every switch on `IsPackaged` carries a one-line comment naming the design.
- Before every PR: `dotnet build Deskbar.sln -c Release -warnaserror` then `dotnet test Deskbar.sln --filter "Category!=Integration"`. Never run the Integration category.
- Commit messages and the PR end with the single line `*fable5.1*` (or the implementing model's short name) and nothing else as attribution. No em dashes, no title-case headings, no emoji.
- Bash tool: never use `>` or `>>` redirection in commands (a hook turns it into a repo file); write files with the Write tool.
- Work happens in the clone at `C:\Users\PETRE~1.CHI\AppData\Local\Temp\claude\c--Users-petre-chitashvili-repos-TaskSpaces\b9b09c15-8215-4216-8629-12624704b348\scratchpad\deskbar-work`, branch `store-packaging` from `origin/main`; the main checkout belongs to another session.

## Review Focus

1. A sideloaded test package has no Store licence: `StoreContext` answers with an inactive licence. That must read as `Free tier`, never as a crash and never as Pro. Pinned in Task 7.
2. A `StoreContext` call that throws or times out (no Store on the machine, a Windows Sandbox) must read as `Store licence unavailable` and Pro for the session, traced, not as free tier. Pinned in Task 7.
3. `state.json` written by a build whose version carries build metadata (`2.1.50+abc123`) or by a build with no `WrittenBy` at all must load in a build of the same or a newer version; only a strictly newer `WrittenBy` refuses. Pinned in Task 3.
4. The first-packaged-start takeover under `DESKBAR_APPDATA` must do nothing at all, not even write the marker, and a second start with the marker present must do nothing. Pinned in Task 9.
5. A program path with spaces, an argument with embedded quotes and an argument containing `%` must survive the cmd `start` breakaway line unchanged, for programs that split their command line with the C runtime's rules. Measured during Task 5: `%` is not doubled on a `cmd /c` line (`^%` is the escape), and programs that split with `CommandLineToArgvW` end a quoted region at `""`, so an embedded quote followed later by whitespace splits there; accepted because no caller passes embedded quotes. Pinned in Task 5.

---

### Task 1: PackageIdentity, the one question everything keys on

**Files:**
- Create: `src/Deskbar.Windows/Packaging/PackageIdentity.cs`
- Test: `tests/Deskbar.Windows.Tests/Packaging/PackageIdentityTests.cs`

**Interfaces:**
- Produces: `public static class PackageIdentity` with `public static bool IsPackaged { get; }` (answered once, cached), `public static string? PackageFamilyName { get; }` (null when unpackaged), `internal static IDisposable Override(bool packaged)` for tests (sets a thread-static override, restores on dispose), and `public static string AliasPath` = `Path.Combine(Environment.GetFolderPath(LocalApplicationData), "Microsoft", "WindowsApps", "deskbar.exe")`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Unpackaged_test_host_is_not_packaged() => Assert.False(PackageIdentity.IsPackaged);
[Fact] public void Override_makes_it_packaged_until_disposed()
{
    using (PackageIdentity.Override(true)) Assert.True(PackageIdentity.IsPackaged);
    Assert.False(PackageIdentity.IsPackaged);
}
[Fact] public void Alias_path_is_under_the_user_WindowsApps_folder() =>
    Assert.EndsWith(@"\Microsoft\WindowsApps\deskbar.exe", PackageIdentity.AliasPath);
```

- [ ] **Step 2: Run the tests, expect a compile failure naming `PackageIdentity`**

Run: `dotnet test tests/Deskbar.Windows.Tests --filter "FullyQualifiedName~PackageIdentityTests"`

- [ ] **Step 3: Implement `PackageIdentity`**

`GetCurrentPackageFullName` from kernel32 (`CharSet.Unicode`), length-probing call; `APPMODEL_ERROR_NO_PACKAGE` (15700) means unpackaged, `ERROR_INSUFFICIENT_BUFFER` (122) then a second call means packaged. `PackageFamilyName` through `GetCurrentPackageFamilyName` the same way. The comment says which design this serves and that the answer is cached for the process.

- [ ] **Step 4: Run the tests, expect PASS**

- [ ] **Step 5: Commit** `feat: PackageIdentity answers whether the app runs from an MSIX package`

### Task 2: The `version` control command

**Files:**
- Modify: `src/Deskbar.Core/Control/RemoteControl.cs` (constructor, `Dispatch`), `src/Deskbar.Core/Control/ControlUsage.cs` (Listing section)
- Modify: `src/Deskbar.App/App.xaml.cs` where `RemoteControl` is constructed (pass the version and exe path)
- Test: `tests/Deskbar.Core.Tests/RemoteControlVersionTests.cs`

**Interfaces:**
- Produces: `RemoteControl` gains optional constructor parameter `Func<(string Version, string? Exe, bool Packaged)>? identity = null`; `version` replies JSON `{ "version": "...", "exe": "...", "packaged": false }`, exit `ControlReply.Ok`; without `identity` it fails with `version is not available in this build`.
- `ControlUsage.All` Listing section gains `new("version", "", "the running build as JSON: version, exe, packaged")`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Version_answers_the_identity_handed_in()
{
    var control = new RemoteControl(Manager(), identity: () => ("2.1.44", @"C:\x\Deskbar.exe", false));
    var reply = control.Execute(["version"]);
    Assert.Equal(ControlReply.Ok, reply.ExitCode);
    Assert.Contains("\"version\": \"2.1.44\"", reply.Output);
    Assert.Contains("\"packaged\": false", reply.Output);
}
[Fact] public void Version_is_listed_in_usage() => Assert.Contains(ControlUsage.All.SelectMany(s => s.Commands), c => c.Verb == "version");
```

(`Manager()` is whatever helper the neighbouring `RemoteControl*Tests` use to build a `WorkspaceManager`.)

- [ ] **Step 2: Run, expect FAIL** (`version` unknown, usage missing)
- [ ] **Step 3: Implement** the dispatch arm `("version", []) => Version()`, the usage line, and in `App.xaml.cs` pass `identity: () => (UpdateService.RunningVersion, Environment.ProcessPath, PackageIdentity.IsPackaged)`.
- [ ] **Step 4: Run the whole Core suite, expect PASS** (the usage-completeness test in `RemoteControlMenuTests` must still pass)
- [ ] **Step 5: Commit** `feat: deskbar version reports the running build`

### Task 3: `WrittenBy` in state.json and the refusal of newer state

**Files:**
- Create: `src/Deskbar.Core/Persistence/StateVersionGate.cs`
- Modify: `src/Deskbar.Core/Persistence/AppState.cs` (add `public string? WrittenBy { get; init; }`), `src/Deskbar.Core/Persistence/JsonPersistenceStore.cs` (constructor gains `string? runningVersion = null`; `Save` stamps `WrittenBy`; `Load` runs the gate)
- Modify: `src/Deskbar.App/App.xaml.cs` where the store is constructed and where `Load` failure is handled (around the `JsonPersistenceStore.Load() deliberately FAILS` comment)
- Test: `tests/Deskbar.Core.Tests/Persistence/StateVersionGateTests.cs`, `tests/Deskbar.Core.Tests/Persistence/JsonPersistenceStoreWrittenByTests.cs`

**Interfaces:**
- Produces: `public static class StateVersionGate { public static Result Check(string? writtenBy, string runningVersion); public static string RefusalMessage(string writtenBy, string running); }`. `Check` compares the `major.minor.patch` prefix only (strip `+metadata` and `-prerelease`); null or unparsable `writtenBy` passes; a newer `writtenBy` fails with `RefusalMessage`, exact copy from Global Constraints.
- `public sealed record NewerStateRefusal(string Message)` is not needed: `Load` returns `Result.Failure(message)` and the App's existing failure path shows it; the App distinguishes it by prefix `Your settings were saved by` only to exit instead of starting empty.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData(null, "2.1.44")] [InlineData("2.1.44", "2.1.44")] [InlineData("2.1.43+abc", "2.1.44")]
[InlineData("garbage", "2.1.44")] [InlineData("2.1.44", "2.1.44+deadbeef")]
public void Same_older_missing_or_unreadable_WrittenBy_passes(string? writtenBy, string running) =>
    Assert.True(StateVersionGate.Check(writtenBy, running).IsSuccess);

[Fact] public void Newer_WrittenBy_refuses_with_both_versions_named()
{
    var result = StateVersionGate.Check("2.1.50", "2.1.44");
    Assert.True(result.IsFailure);
    Assert.Equal("Your settings were saved by Deskbar 2.1.50; this is 2.1.44. Start the newer copy, or remove it and delete the WrittenBy line from state.json to go on with this one.", result.Error);
}

[Fact] public void Save_stamps_WrittenBy_and_Load_refuses_a_newer_stamp()
{
    var dir = TempDir();
    new JsonPersistenceStore(dir, runningVersion: "2.1.50").Save(AppState.Empty);
    var older = new JsonPersistenceStore(dir, runningVersion: "2.1.44").Load();
    Assert.True(older.IsFailure);
    Assert.StartsWith("Your settings were saved by Deskbar 2.1.50", older.Error);
}
```

- [ ] **Step 2: Run, expect FAIL**
- [ ] **Step 3: Implement** the gate, the stamp (`state with { WrittenBy = runningVersion }` when `runningVersion` is not null), the check inside `Load` after deserialising, and in `App.xaml.cs` pass `UpdateService.RunningVersion` and, on a failure whose message starts with `Your settings were saved by`, show it in a `MessageBox` and `Shutdown()` instead of backing the file up.
- [ ] **Step 4: Run the Core suite, expect PASS**
- [ ] **Step 5: Commit** `feat: state.json carries WrittenBy, and an older build refuses newer state`

### Task 4: `Deskbar.Cli`, the console client behind the alias

**Files:**
- Create: `src/Deskbar.Cli/Deskbar.Cli.csproj` (OutputType Exe, AssemblyName `deskbar-cli`, same TFM, `PlatformTarget` x64, reference `Deskbar.Windows`), `src/Deskbar.Cli/Program.cs`
- Create: `src/Deskbar.Windows/Control/ControlConsole.cs`
- Modify: `Deskbar.sln` (add the project under `src`)
- Test: `tests/Deskbar.Windows.Tests/Control/ControlConsoleTests.cs`

**Interfaces:**
- Produces: `public static class ControlConsole { public static int Run(IReadOnlyList<string> args, string pipeName, Func<string, IReadOnlyList<string>, Task<ControlReply>> send, TextWriter stdout, TextWriter stderr); }`. Writes `reply.Output` plus newline to stdout when `ExitCode == ControlReply.Ok`, else to stderr; returns the exit code. `args` are forwarded as they are (no `ctl` verb). `Program.Main` is `ControlConsole.Run(args, ControlWire.PipeName(sessionId), ControlClient.RunAsync, Console.Out, Console.Error)` with UTF-8 output encoding without BOM.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Ok_reply_goes_to_stdout_with_exit_zero()
{
    var (out_, err) = (new StringWriter(), new StringWriter());
    var code = ControlConsole.Run(["windows"], "pipe", (_, a) => Task.FromResult(ControlReply.Ok("[]")), out_, err);
    Assert.Equal(0, code); Assert.Equal("[]" + Environment.NewLine, out_.ToString()); Assert.Equal("", err.ToString());
}
[Fact] public void Failure_goes_to_stderr_with_its_code()
{
    var (out_, err) = (new StringWriter(), new StringWriter());
    var code = ControlConsole.Run(["nope"], "pipe", (_, _) => Task.FromResult(ControlReply.Fail("unknown command")), out_, err);
    Assert.NotEqual(0, code); Assert.Contains("unknown command", err.ToString());
}
```

(Use whatever factory names `ControlReply` really has; read `ControlReply.cs` first.)

- [ ] **Step 2: Run, expect FAIL**
- [ ] **Step 3: Implement** `ControlConsole`, the project and `Program.cs`; add to the solution with `dotnet sln Deskbar.sln add src/Deskbar.Cli/Deskbar.Cli.csproj`.
- [ ] **Step 4: Build Release with warnings as errors and run the Windows tests, expect PASS**
- [ ] **Step 5: Commit** `feat: deskbar-cli, the console control client the Store package puts behind its alias`

### Task 5: Breakaway launcher for programs Deskbar starts on someone's behalf

**Files:**
- Create: `src/Deskbar.Core/Control/CommandLineQuoting.cs`
- Create: `src/Deskbar.Windows/Processes/DetachedLauncher.cs`
- Modify: `src/Deskbar.Windows/Control/ProgramStarter.cs`, `src/Deskbar.Windows/Shell/TaskbarPins.cs` (`Launch` and `Reveal`), `src/Deskbar.App/FloatingBar.Stats.cs` (Task Manager), `src/Deskbar.App/DebugDump.cs` (explorer /select)
- Test: `tests/Deskbar.Core.Tests/Control/CommandLineQuotingTests.cs`, `tests/Deskbar.Windows.Tests/Processes/DetachedLauncherTests.cs`

**Interfaces:**
- Produces: `public static class CommandLineQuoting { public static string ForCmdStart(string program, IReadOnlyList<string> arguments, string? workingDirectory); }` returning the full `cmd.exe` argument string `/d /c start "" [/d "<cwd>"] "<program>" <args...>` with each argument quoted for cmd (`"` doubled, `%` doubled, `^` escaping for `& | < > ^`).
- `public static class DetachedLauncher { public static Result<int?> Start(string program, IReadOnlyList<string> arguments, string? workingDirectory); }`. Unpackaged: today's `Process.Start` with `UseShellExecute = true` and `ArgumentList`, returning the pid. Packaged: `CreateProcess` of `%SystemRoot%\System32\cmd.exe` with `STARTUPINFOEX` carrying `PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY` = `PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_ENABLE_PROCESS_TREE` (0x01), `CREATE_NO_WINDOW`, the quoted line from `CommandLineQuoting`, returning `null` (cmd exits; the child is found by name). `internal static Result<int?> Start(..., bool packaged)` overload for tests.
- `ProgramStarter.Start`, `TaskbarPins.Launch` (the two non-AppsFolder arms), `TaskbarPins.Reveal`, Task Manager and the dump's `explorer /select` all call `DetachedLauncher.Start`. The `shell:AppsFolder` arm stays as it is (Explorer brokers it) with a comment saying the spike checks it.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Quotes_spaces_embedded_quotes_and_percent_for_cmd_start() =>
    Assert.Equal(@"/d /c start """" /d ""C:\my dir"" ""C:\Program Files\X\x.exe"" ""a b"" ""say """"hi"""""" 100%%",
        CommandLineQuoting.ForCmdStart(@"C:\Program Files\X\x.exe", ["a b", "say \"hi\"", "100%"], @"C:\my dir"));
[Fact] public void No_working_directory_means_no_d_switch() =>
    Assert.StartsWith(@"/d /c start """" ""code""", CommandLineQuoting.ForCmdStart("code", [], null));
```

```csharp
[Fact] public void Unpackaged_start_of_cmd_returns_a_pid()
{
    var pid = DetachedLauncher.Start("cmd.exe", ["/c", "exit"], null, packaged: false);
    Assert.True(pid.IsSuccess); Assert.NotNull(pid.Value);
}
[Fact] public void Packaged_path_starts_cmd_with_breakaway_and_returns_null_pid()
{
    var marker = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    var result = DetachedLauncher.Start("cmd.exe", ["/c", $"echo x> \"{marker}\""], null, packaged: true);
    Assert.True(result.IsSuccess); Assert.Null(result.Value);
    SpinWait.SpinUntil(() => File.Exists(marker), 5000); Assert.True(File.Exists(marker));
}
```

(The breakaway attribute is accepted by an unpackaged parent too, so the packaged path is exercised for real on the test host.)

- [ ] **Step 2: Run, expect FAIL**
- [ ] **Step 3: Implement** both classes and reroute the five call sites. The `CreateProcess` P/Invokes are `CharSet.Unicode`; `InitializeProcThreadAttributeList`, `UpdateProcThreadAttribute`, `DeleteProcThreadAttributeList`, `CreateProcessW` with `EXTENDED_STARTUPINFO_PRESENT`.
- [ ] **Step 4: Run Core and Windows suites, expect PASS** (the existing `RemoteControlStartTests` and pins tests must be untouched in outcome)
- [ ] **Step 5: Commit** `feat: programs Deskbar starts break away from its package identity`

### Task 6: Package manifest, images, resource index and `pack-store.ps1`

**Files:**
- Create: `src/Deskbar.App/Package/AppxManifest.xml`, `src/Deskbar.App/Package/priconfig.xml`, `src/Deskbar.App/Package/Images/*.png` (generated), `scripts/make-store-images.ps1`, `scripts/pack-store.ps1`, `scripts/StorePackaging.psm1`, `scripts/StorePackaging.Tests.ps1`
- Modify: `src/Deskbar.App/Deskbar.App.csproj` (exclude `Package\**` from the default item globs so the folder is not compiled or copied)

**Interfaces:**
- Produces: `StorePackaging.psm1` exports `Get-StoreManifest -TemplatePath -Version` (returns the manifest text with `__VERSION__` replaced by `<Version>.0`, throws when the version is not `\d+\.\d+\.\d+`), `Test-StoreIdentity -ManifestText` (throws unless Name, Publisher and PublisherDisplayName equal the Global Constraints values), `Get-RequiredImages` (the list of file names the manifest and priconfig expect), `Find-SdkTool -Name` (newest `bin\<ver>\x64\<Name>` under `${env:ProgramFiles(x86)}\Windows Kits\10`), `Invoke-StorePack -PublishDir -StageDir -ManifestText -ImagesDir -Output` (copies, runs `MakePri.exe new`, then `MakeAppx.exe pack /d <stage> /p <output> /o`).
- `pack-store.ps1 -Version <v> [-PublishDir <dir>] [-Output <path>] [-TestSign]`: publishes when `-PublishDir` is absent, packs, and with `-TestSign` makes or reuses a `CN=804EE50D-C32E-4285-AAF6-E1C851F9FCEF` self-signed certificate in `Cert:\CurrentUser\My`, signs with signtool, and prints the elevated command that imports it into `Cert:\LocalMachine\TrustedPeople` without running it.
- Manifest contents per the spec's "The manifest" section: `EntryPoint="Windows.FullTrustApplication"`, capabilities `runFullTrust` and `unvirtualizedResources`, the scoped `virtualization:` exclusions (`%APPDATA%\Deskbar`, `%APPDATA%\TaskSpaces`; `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) with the `desktop6` global switches as the fallback the builder switches to if MakeAppx rejects the scoped form, the startup task extension with `Executable` and `EntryPoint`, the alias extension onto `deskbar-cli.exe`, and the image set the spec lists (44x44 at five scales plus target sizes in plain, unplated and light-unplated forms; 150x150, StoreLogo 50x50, Wide 310x150 at scale 100 and 200).

- [ ] **Step 1: Write the failing Pester tests** (Pester 3.4 style, run with `powershell.exe -NoProfile -Command "Invoke-Pester -Path scripts/StorePackaging.Tests.ps1"`): the version token is replaced and `.0` appended; a two-part version throws; a manifest with the wrong Publisher throws; every file in `Get-RequiredImages` exists under `src/Deskbar.App/Package/Images`; `Find-SdkTool -Name MakeAppx.exe` returns an existing path on a machine with the SDK (skip with a message otherwise).
- [ ] **Step 2: Run, expect FAIL**
- [ ] **Step 3: Write the manifest, the priconfig, `make-store-images.ps1`** (System.Drawing resize from `Assets/deskbar-256.png`; unplated forms are the same pixels, the plated form is the icon on the Store tile colour `#1F1F1F`), generate and check in the images, write the module and the script.
- [ ] **Step 4: Run the Pester tests, expect PASS; then run `scripts/pack-store.ps1 -Version 0.0.1 -TestSign` locally and confirm `Deskbar-0.0.1-store.msix` exists and `MakeAppx` reported no error.** Record the chosen virtualisation form in the ledger.
- [ ] **Step 5: Commit** `feat: the Store package manifest, images and pack-store.ps1`

### Task 7: ProEntitlement, the one question the free tier will ask

**Files:**
- Create: `src/Deskbar.Core/Licensing/ProEntitlement.cs`, `src/Deskbar.Windows/Store/StoreLicence.cs`, `src/Deskbar.App/Entitlement.cs`
- Modify: `src/Deskbar.App/DebugDump.cs` (a `Line("entitlement", ...)` after `version`), `src/Deskbar.App/App.xaml.cs` (refresh on startup after the bar exists, and on the update timer's tick)
- Test: `tests/Deskbar.Core.Tests/Licensing/ProEntitlementTests.cs`

**Interfaces:**
- Produces: `public sealed record LicenceFacts(bool IsActive, bool IsTrial, DateTimeOffset? TrialEnds);` `public sealed record Verdict(bool IsPro, string Reason);` `public static class ProEntitlement { public static Verdict Decide(bool packaged, Result<LicenceFacts> licence, bool developerOverride, bool rootOverridden); }`. Rules: override and rootOverridden → `(true, "Developer override")`; not packaged → `(false, "Free tier")`; licence failure → `(true, "Store licence unavailable")`; active trial → `(true, "Store trial until <yyyy-MM-dd>")`; active full → `(true, "Store licence")`; inactive → `(false, "Free tier")`.
- `public static class StoreLicence { public static Task<Result<LicenceFacts>> ReadAsync(IntPtr ownerHwnd); }` in Deskbar.Windows: `StoreContext.GetDefault()`, `IInitializeWithWindow.Initialize(hwnd)`, `GetAppLicenseAsync()`, mapped to `LicenceFacts`; any exception or a 10 s timeout becomes a `Result.Failure` with the message.
- `public static class Entitlement { public static Verdict Current { get; } public static Task RefreshAsync(IntPtr ownerHwnd); }` in the App, caching the last verdict, tracing each refresh with `ClickTrace.Write($"entitlement: {reason}")`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Unpackaged_is_free_tier() => Assert.Equal(new Verdict(false, "Free tier"), ProEntitlement.Decide(false, Result.Success(new LicenceFacts(true, false, null)), false, false));
[Fact] public void Inactive_licence_is_free_tier() => Assert.Equal(new Verdict(false, "Free tier"), ProEntitlement.Decide(true, Result.Success(new LicenceFacts(false, false, null)), false, false));
[Fact] public void Failed_licence_read_is_pro_but_says_unavailable() => Assert.Equal(new Verdict(true, "Store licence unavailable"), ProEntitlement.Decide(true, Result.Failure<LicenceFacts>("boom"), false, false));
[Fact] public void Trial_names_its_end() => Assert.Equal(new Verdict(true, "Store trial until 2026-11-08"), ProEntitlement.Decide(true, Result.Success(new LicenceFacts(true, true, new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero))), false, false));
[Fact] public void Override_counts_only_under_a_root_override()
{
    Assert.Equal(new Verdict(true, "Developer override"), ProEntitlement.Decide(false, Result.Failure<LicenceFacts>("x"), true, true));
    Assert.Equal(new Verdict(false, "Free tier"), ProEntitlement.Decide(false, Result.Failure<LicenceFacts>("x"), true, false));
}
```

- [ ] **Step 2: Run, expect FAIL**
- [ ] **Step 3: Implement** the three classes and the two wiring points. `Entitlement.RefreshAsync` reads `Environment.GetEnvironmentVariable("DESKBAR_PRO") == "1"` and `AppPaths.RootOverridden`, calls `StoreLicence.ReadAsync` only when `PackageIdentity.IsPackaged`.
- [ ] **Step 4: Run Core and Windows suites, expect PASS**
- [ ] **Step 5: Commit** `feat: Entitlement answers whether this build is Deskbar Pro`

### Task 8: The startup switch: Run value unpackaged, startup task packaged

**Files:**
- Create: `src/Deskbar.Windows/Startup/IStartupSwitch.cs`, `src/Deskbar.Windows/Startup/RunValueStartup.cs` (wraps `StartupRegistration`: move `StartupRegistration.cs` from the App to `src/Deskbar.Windows/Startup/` if its only dependencies allow, else wrap it in place), `src/Deskbar.Windows/Startup/PackagedStartup.cs`, `src/Deskbar.Windows/Startup/StartupSwitch.cs` (the chooser)
- Modify: `src/Deskbar.App/ManageWindow.xaml` (a `TextBlock x:Name="StartupNote"` under the box, collapsed by default), `src/Deskbar.App/ManageWindow.xaml.cs` (constructor and `OnStartupToggled` use the switch; the note shows the Store warning in the direct build when `File.Exists(PackageIdentity.AliasPath)`, or the where-to-turn-it-on line when `Availability` is not `Available`)
- Test: `tests/Deskbar.Windows.Tests/Startup/StartupSwitchTests.cs`, `tests/Deskbar.Windows.Tests/ManageSettingsTabTests.cs` (extend)

**Interfaces:**
- Produces: `public enum StartupAvailability { Available, DisabledByUser, DisabledByPolicy, Unavailable }`; `public interface IStartupSwitch { bool IsEnabled { get; } StartupAvailability Availability { get; } Result Enable(); Result Disable(); }`; `public static class StartupSwitch { public static IStartupSwitch Current { get; } internal static IDisposable Override(IStartupSwitch fake); }` choosing `PackagedStartup` when `PackageIdentity.IsPackaged`.
- `PackagedStartup` uses `StartupTask.GetAsync("Deskbar")`, maps `StartupTaskState` (`Enabled`, `EnabledByPolicy` → enabled and Available; `Disabled` → Available, not enabled; `DisabledByUser`; `DisabledByPolicy`), `Enable` = `RequestEnableAsync` (a result other than Enabled is a failure naming the state), `Disable` = `Disable()`.
- `ManageWindow` ctor parameter list unchanged; it reads `StartupSwitch.Current`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Unpackaged_chooser_is_the_run_value_switch() => Assert.IsType<RunValueStartup>(StartupSwitch.Current);
[Fact] public void Packaged_chooser_is_the_startup_task_switch() { using var _ = PackageIdentity.Override(true); Assert.IsType<PackagedStartup>(StartupSwitch.Current); }
```

In `ManageSettingsTabTests`: with `StartupSwitch.Override(fake)` where the fake reports `DisabledByPolicy`, the box is unchecked and `IsEnabled == false` and `StartupNote.Text` says `Startup is turned off by policy. Turn it on under Settings, Apps, Startup.`; with the fake `Available` and enabled, the box is checked and toggling it calls `Disable` once.

- [ ] **Step 2: Run, expect FAIL**
- [ ] **Step 3: Implement** the four files and the Manage changes. `StartupSwitch.Current` is evaluated once per process (the chooser caches) and the override replaces it for the test's scope.
- [ ] **Step 4: Run the Windows suite, expect PASS** (the existing `StartupRegistrationTests` keep passing wherever the class now lives)
- [ ] **Step 5: Commit** `feat: Start with Windows drives the package's startup task when packaged`

### Task 9: Startup under the package: takeover, shim, migration, the other build's name

**Files:**
- Create: `src/Deskbar.Core/Migration/PackagedTakeover.cs`, `src/Deskbar.App/PackagedTakeoverApplier.cs`, `src/Deskbar.Core/Control/OtherInstance.cs`
- Modify: `src/Deskbar.Core/Migration/RenameMigration.cs` (`RenameFacts` gains `bool Packaged`; `Registry(facts)` never yields `SetNewRunValueToThisExe` when `Packaged`), `src/Deskbar.App/RenameMigrator.cs` (`Gather` fills `Packaged`), `src/Deskbar.App/App.xaml.cs` (`OnStartup`: shim target, takeover after `ReassertIfEnabled`, the already-running message), `src/Deskbar.App/StartupRegistration.cs` or its new home (a comment at `RunsAsPublished` saying the package relies on it being false)
- Test: `tests/Deskbar.Core.Tests/Migration/PackagedTakeoverTests.cs`, `tests/Deskbar.Core.Tests/Migration/RenameMigrationTests.cs` (extend), `tests/Deskbar.Core.Tests/Control/OtherInstanceTests.cs`, `tests/Deskbar.Windows.Tests/ControlShimTests.cs` (extend)

**Interfaces:**
- Produces: `public sealed record TakeoverFacts(bool Packaged, bool MarkerExists, bool RootOverridden, bool DirectRunValueExists, bool DirectShortcutExists);` `public abstract record TakeoverStep; public sealed record DeleteDirectRunValue : TakeoverStep; public sealed record DeleteDirectShortcut : TakeoverStep; public sealed record WriteMarker : TakeoverStep;` `public static class PackagedTakeover { public const string MarkerName = "packaged-first-start-done"; public static IReadOnlyList<TakeoverStep> Plan(TakeoverFacts facts); }`. Empty unless `Packaged && !MarkerExists && !RootOverridden`; then the deletions that apply, then `WriteMarker`.
- `PackagedTakeoverApplier.Apply(IReadOnlyList<TakeoverStep>, string stateDir, Action<string> trace)` in the App: deletes the HKCU Run value `Deskbar`, deletes `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Deskbar.lnk`, writes the marker file; each attempt traced, a failure traced and skipped.
- `public static class OtherInstance { public static string Describe(IReadOnlyList<string?> otherExePaths); }`: a path containing `\WindowsApps\` → the Store sentence; a readable path → `The copy at <folder> is running.`; none readable → `Deskbar is already running.`; the App appends the existing `Look for the tiled icon...` line in every case.
- The shim: under the package `ControlCommandLine.WriteShim(AppPaths.NewStateDir, PackageIdentity.AliasPath)` and `ShimContent` gains an overload `ShimContent(string exePath, bool aliasTarget)` that omits ` ctl` when the target is the alias (the console client takes the command directly).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void First_packaged_start_deletes_direct_autostart_and_writes_marker() =>
    Assert.Equal([new DeleteDirectRunValue(), new DeleteDirectShortcut(), new WriteMarker()],
        PackagedTakeover.Plan(new(true, false, false, true, true)));
[Fact] public void Nothing_under_a_root_override_not_even_the_marker() => Assert.Empty(PackagedTakeover.Plan(new(true, false, true, true, true)));
[Fact] public void Nothing_when_the_marker_exists() => Assert.Empty(PackagedTakeover.Plan(new(true, true, false, true, true)));
[Fact] public void Nothing_unpackaged() => Assert.Empty(PackagedTakeover.Plan(new(false, false, false, true, true)));
[Fact] public void Only_the_marker_when_there_is_nothing_to_delete() => Assert.Equal([new WriteMarker()], PackagedTakeover.Plan(new(true, false, false, false, false)));
```

```csharp
[Fact] public void Packaged_rename_migration_sets_no_new_run_value() =>
    Assert.DoesNotContain(RenameMigration.Plan(AfterTheUpdate with { Packaged = true, OldRunValueExists = true }), s => s is SetNewRunValueToThisExe);
```

```csharp
[Fact] public void Store_path_is_named() => Assert.StartsWith("The copy installed from the Microsoft Store is running.", OtherInstance.Describe([@"C:\Program Files\WindowsApps\Petre.Deskbar_2.1.44.0_x64__n8ch8v53xw03y\Deskbar.exe"]));
[Fact] public void Folder_is_named() => Assert.StartsWith(@"The copy at C:\Users\x\Programs\Deskbar is running.", OtherInstance.Describe([@"C:\Users\x\Programs\Deskbar\Deskbar-2.1.40-win-x64.exe"]));
[Fact] public void Unreadable_path_falls_back() => Assert.StartsWith("Deskbar is already running.", OtherInstance.Describe([null]));
```

```csharp
[Fact] public void Alias_shim_forwards_without_ctl() => Assert.Contains("\"C:\\u\\WindowsApps\\deskbar.exe\" %*", ControlCommandLine.ShimContent(@"C:\u\WindowsApps\deskbar.exe", aliasTarget: true));
```

- [ ] **Step 2: Run, expect FAIL**
- [ ] **Step 3: Implement.** In `OnStartup`, after `StartupRegistration.ReassertIfEnabled()` and before `StartMenuShortcut.Ensure()`: gather `TakeoverFacts` (`PackageIdentity.IsPackaged`, marker file in `AppPaths.NewStateDir`, `AppPaths.RootOverridden`, `StartupRegistration.IsEnabled`, `File.Exists(StartMenuShortcut.ShortcutPath)`), plan, apply. The already-running branch lists `Process.GetProcessesByName("Deskbar")` other than self, reads `MainModule.FileName` inside a try (an elevated process throws; that is the null case), and shows `OtherInstance.Describe(...)`.
- [ ] **Step 4: Run both suites, expect PASS**
- [ ] **Step 5: Commit** `feat: a packaged first start takes over autostart, and the already-running dialog names the other copy`

### Task 10: The Store-side update check

**Files:**
- Create: `src/Deskbar.Windows/Store/StoreUpdates.cs`, `src/Deskbar.Core/Updates/UpdateChannel.cs`
- Modify: `src/Deskbar.App/App.xaml.cs` (`StartUpdateChecks`, `CheckForUpdate`, `CheckForUpdateNow`, the offer), `src/Deskbar.App/ManageWindow.xaml` (the `CheckForUpdates` box text when packaged: `Check the Microsoft Store for new versions`), `src/Deskbar.App/UpdateService.cs` (`StoreProductId` stays empty; nothing else)
- Test: `tests/Deskbar.Core.Tests/Updates/UpdateChannelTests.cs`

**Interfaces:**
- Produces: `public sealed record StoreUpdate(string Version, bool Mandatory);` `public static class StoreUpdates { public static Task<Result<Maybe<StoreUpdate>>> NewerAsync(IntPtr ownerHwnd); public static Task<Result> InstallAsync(IntPtr ownerHwnd); }` using `GetAppAndOptionalStorePackageUpdatesAsync` and `RequestDownloadAndInstallStorePackageUpdatesAsync`; `InstallAsync` calls `CrashRestart`'s registration first (whatever the existing `RegisterApplicationRestart` wrapper is called) so Windows may bring the app back.
- `public static class UpdateChannel { public static string OfferQuestion(bool packaged, string version, bool restartProven); public static string OfferButton(bool packaged); }` returning the Global Constraints copy: packaged → `Update Deskbar from the Store?` plus, when `restartProven` is false, a second line `Deskbar will close to update; open it from Start afterwards.`; unpackaged → today's `Update Deskbar to <version>?` and `Update`. `restartProven` is a constant in the App set by the spike's result (false until measured).
- App: when packaged, the timer and the manual check call `StoreUpdates.NewerAsync`, the offer uses `UpdateChannel`, accepting calls `StoreUpdates.InstallAsync`; the GitHub path is not reachable when packaged (a comment at each fork names the design).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Packaged_offer_asks_about_the_store_and_warns_until_restart_is_proven()
{
    Assert.Equal("Update Deskbar from the Store?\n\nDeskbar will close to update; open it from Start afterwards.", UpdateChannel.OfferQuestion(true, "2.1.50", restartProven: false));
    Assert.Equal("Update Deskbar from the Store?", UpdateChannel.OfferQuestion(true, "2.1.50", restartProven: true));
    Assert.Equal("Update from the Store", UpdateChannel.OfferButton(true));
}
[Fact] public void Unpackaged_offer_is_unchanged() { Assert.Equal("Update Deskbar to 2.1.50?", UpdateChannel.OfferQuestion(false, "2.1.50", false)); Assert.Equal("Update", UpdateChannel.OfferButton(false)); }
```

- [ ] **Step 2: Run, expect FAIL**
- [ ] **Step 3: Implement** the two classes and the App forks. The existing `UpdateOfferTests` keep passing: the dialog class is unchanged, only its inputs differ.
- [ ] **Step 4: Run both suites, expect PASS**
- [ ] **Step 5: Commit** `feat: the Store build checks the Store for updates and installs from it`

### Task 11: CI: pack the Store package beside the direct exe, install it and ask it its version

**Files:**
- Modify: `.github/workflows/release.yml` (build job: a `Pack the Store package` step after the publish step, an artifact upload; a new `store-smoke` job after `build`)
- Create: `scripts/store-smoke.ps1`

**Interfaces:**
- The pack step runs `scripts/pack-store.ps1 -Version <v> -Output artifacts/store/Deskbar-<v>-store.msix` (publishing its own non-single-file output to `artifacts/store-publish`), uploads `Deskbar-<v>-store.msix` as a workflow artifact. Not attached to the release.
- `store-smoke.ps1 -Package <msix> -AppData <folder>`: creates a throwaway certificate with the Store publisher subject, signs a copy of the package, imports the certificate into `Cert:\LocalMachine\TrustedPeople`, `Add-AppxPackage`, starts the app through the alias path with `DESKBAR_APPDATA=<folder>` (`Start-Process` of `explorer.exe shell:AppsFolder\Petre.Deskbar_n8ch8v53xw03y!App` after setting the variable in the machine environment is unreliable; instead start `<WindowsApps>\Petre.Deskbar_<v>_x64__n8ch8v53xw03y\Deskbar.exe` directly from PowerShell, which inherits the variable), waits up to 30 s for `deskbar version` through the alias to answer, asserts the JSON says `"packaged": true` and the expected version, then `Stop-Process` and `Remove-AppxPackage`. If `appcert.exe` exists under the SDK it runs WACK against the installed package and uploads the report; a WACK failure is reported but does not fail the job in this task (a later task flips that once it is known to be stable).
- Every step carries the comment style of the file: what it does and why, so the next person can read the workflow without the design.

- [ ] **Step 1: Write the pack step and the smoke job; push the branch and run the workflow with `gh workflow run Release -f version=<VersionPrefix>` on the branch** (workflow_dispatch builds a draft; delete the draft afterwards with `gh release delete v<version> --yes` since it is only a CI exercise, and say so in the ledger).
- [ ] **Step 2: Read the run: the pack step produced the artifact, the smoke job's `deskbar version` answered `packaged: true`.** Expected: both green; WACK result recorded whatever it says.
- [ ] **Step 3: Fix what failed, re-run until green.**
- [ ] **Step 4: Commit** `ci: the release build packs the Store package and installs it on the runner`

### Task 12: The spike on a real machine (needs Petre)

**Files:**
- Create: `docs/superpowers/notes/2026-10-xx-store-package-spike.md` (findings)
- Modify: whichever source files the findings require, each with its own commit

This task runs on a Windows 11 machine with the Store present: a VM Petre provides, or his own machine under the Global Constraints (consent, `DESKBAR_APPDATA` from the terminal, removal before sign-out, the direct build restarted). The executor asks Petre which, and stops until answered.

- [ ] **Step 1: Build with `scripts/pack-store.ps1 -Version <VersionPrefix> -TestSign`, import the certificate with the printed elevated command, `Add-AppxPackage` the package.**
- [ ] **Step 2: Start it from a terminal with `DESKBAR_APPDATA` set and check, writing one line per item into the findings note:** the bar appears; tray icon; `deskbar windows` through the alias from PowerShell returns JSON and `$LASTEXITCODE` 0; `deskbar.cmd windows` from the state folder does the same; `Win+Ctrl+Tab` switches; creating a workspace names a real virtual desktop; docking the bar reserves the edge; media control shows now playing; `deskbar start <ws> -- notepad` starts Notepad and `(Get-Process notepad).Path` plus `Get-AppxPackage`-style identity check (`tasklist /apps` lists packaged processes; Notepad must not appear under Deskbar's package) shows no package identity; the same for a pinned app; the `packaged-first-start-done` marker was NOT written (root override); `state.json` under the override folder carries `WrittenBy`; the taskbar icon at 100% and 150% against the direct build's, screenshots in the note; Manage's box reflects the startup task and toggles it (check in Settings, Apps, Startup).
- [ ] **Step 3: Install a second test package with a higher version while the first runs, through `Add-AppxPackage -ForceApplicationShutdown`, and record whether the app comes back on its own.** Set `restartProven` in the App accordingly (Task 10's constant) in its own commit.
- [ ] **Step 4: Sign out and in once with the package installed and the override NOT set, after exiting the direct build with Petre's consent, to see whether the startup task fires without a prior launch; then remove the package, restart the direct build, confirm his Run value and shortcut are intact.**
- [ ] **Step 5: Commit** the note and any fixes: `docs: Store package spike findings`

### Task 13: README, briefing, version bump and the PR

**Files:**
- Modify: `README.md` (a `### From the Microsoft Store` subsection under Install: what the package does differently, the one-install rule, the way back; the Store badge comment stays until the listing is live), `CLAUDE.md` (two bullets: `PackageIdentity.IsPackaged` is the switch and where the switches are; `deskbar` on PATH on a Store machine), `Directory.Build.props` (`VersionPrefix` to the next minor, e.g. `2.2.0`, since the app gains a channel), `.github/workflows/release.yml` only if Task 11 left a TODO
- Test: none new; the full suites and the Release build with warnings as errors

- [ ] **Step 1: Write the README section and the briefing bullets; bump the version.**
- [ ] **Step 2: `dotnet build Deskbar.sln -c Release -warnaserror` and `dotnet test Deskbar.sln --filter "Category!=Integration"`, expect green.**
- [ ] **Step 3: Commit** `docs: the Store channel in the README and the briefing (2.2.0)`
- [ ] **Step 4: Open the PR** titled `Deskbar on the Microsoft Store: package, Pro entitlement and the packaged behaviour (2.2.0)` with a `## Release notes` section written for users (the direct build's user-visible changes: `deskbar version`, the already-running dialog names the other copy, state.json carries `WrittenBy` and an older build refuses newer state, Manage warns when the Store copy owns startup; and that a Store listing is coming), the spike findings linked, and the attribution line. Arm the PR watch from the briefing.
