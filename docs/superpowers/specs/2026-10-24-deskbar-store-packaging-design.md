# Deskbar on the Microsoft Store: packaging and listing

Date: 2026-10-24. Frame: `2026-10-18-deskbar-commercial-release-design.md`, whose "Store
first" section (2026-10-19) moved this step directly after the rename. The rename shipped as
2.0.0 on 2026-10-19; main is at 2.1.43 as this is written. A builder check (a reviewer
reading this as the person who has to plan from it) ran on 2026-10-24; its findings are
folded in below and marked "builder check" where they changed a decision.

## Intent

The Store is the trusted channel. Microsoft signs the package, so a person who presses Get
sees no SmartScreen page and no "unknown publisher", which is the trust the direct exe on
GitHub Releases cannot offer without a certificate we have decided not to buy yet.

The Store is also where Deskbar Pro is sold (Petre, 2026-10-24, replacing the frame spec's
key-and-Paddle model): the listing is paid, $19 once, with the Store's own trial, and the
Store build is Pro with everything on. The direct build on GitHub is the free tier. There
is no licence key, no checkout on the site and no merchant of record besides Microsoft.
Upgrading is installing from the Store; the state carries over because both builds share
the real `%APPDATA%\Deskbar`. The free tier's limits are the next design; this one makes
the Store build exist and know that it is Pro.

The Store build is the same program from the same tag, packaged as MSIX with full trust.
It must behave like the direct build in everything a person can see, and differ only where
the package makes the direct build's self-maintenance wrong or redundant, and in being Pro.

## What is already in place (paperwork, done by Petre, 2026-10-21 to 2026-10-24)

- Developer account: Individual, on the personal Microsoft account
  petre.chitashvili@gmail.com, opened through storedeveloper.microsoft.com. The publisher
  display name was changed from "Petre" to "AppifySheets" on 2026-10-24 and is under the
  review that change triggers. The manifest's `PublisherDisplayName` must equal whatever
  Partner Center shows at submission time.
- Product reserved: name "Deskbar", Store ID `9PJX26NGFPNF`, listing URL
  `https://apps.microsoft.com/detail/9PJX26NGFPNF`, protocol link
  `ms-windows-store://pdp/?productid=9PJX26NGFPNF`.
- Manifest identity, assigned by the Store and copied verbatim:
  - `Package/Identity/Name`: `Petre.Deskbar`
  - `Package/Identity/Publisher`: `CN=804EE50D-C32E-4285-AAF6-E1C851F9FCEF`
  - Package Family Name: `Petre.Deskbar_n8ch8v53xw03y`
- Domain and site: `https://deskbar.app` is live on Cloudflare Pages with `/privacy`,
  `/terms` and `/refunds`. `hello@deskbar.app` forwards to Petre through Cloudflare Email
  Routing. The site's Store badge is behind a placeholder until the listing is live.

## Goals

1. A `.msix` built in CI from the same commit and version as the direct exe, ready to upload
   to Partner Center without any manual editing.
2. The packaged app passes the Windows App Certification Kit and Store certification on the
   first or second submission.
3. Under the package: the named pipe, `deskbar.cmd`, the global hotkey, the virtual desktop
   COM calls, the tray icon, the app bar docking, media control, the crash relaunch, the
   dock pins, `restart-app`, `deskbar.cmd start` and the 1.x migration all work as they do
   unpackaged. Proved, not assumed.
4. The Store build starts with Windows through the package's startup task, and Manage's
   "Start Deskbar with Windows" switch drives that task.
5. One machine, one running Deskbar: a Store build started while the direct build runs, or
   the reverse, says which one is running and exits.
6. A Store update reaches a running bar: the Store build notices an update, offers it the
   way the direct build offers its own, and comes back after installing it.
7. The direct build's update offer gains its "Microsoft Store" button, and the README and
   the site show the Store badge, once the listing is live.
8. The app can answer "am I Pro?" offline and without a key: packaged and licensed by the
   Store (a purchase or an unexpired Store trial) means Pro. The free tier's gate, which is
   the next design, asks this one question.

## Non-goals

- The free tier's limits, the Pro tab's contents and the reminders. Separate design; this
  one only provides the entitlement answer.
- A licence key, a site checkout, Paddle. Dropped on 2026-10-24 with the pricing change.
  If a way to buy outside the Store is ever needed, a key can be added then.
- An installer for the direct channel. The direct download stays the portable exe.
- A Store submission that is automated end to end. The first submissions are uploaded by
  hand in Partner Center; the submission API is a later convenience.
- ARM64. x64 only, as the direct build.
- Supporting a downgrade between channels. An older build reading state a newer build wrote
  refuses rather than guesses (see "Shared state and version lag"); making it work is not
  attempted.

## Anti-goals

- Two Deskbars starting at login because both channels registered autostart. The Store
  build must take over autostart on its first start, and the direct build must say so
  before it takes autostart back.
- A Store build that forks the user's state into the package's virtualised AppData, so that
  switching channels, or the 1.x migration, sees a different `state.json`. The package reads
  and writes the real `%APPDATA%\Deskbar`.
- A listing whose screenshots show a real person's name as a workspace name. No real names
  in this repo or its listing, screenshots included.
- An update checker that offers the GitHub exe to a Store user.
- A program Deskbar started on someone's behalf (VS Code through `restart-app`, a pinned
  app, Explorer) dying because the Store serviced or removed Deskbar's package. Child
  processes must not carry Deskbar's package identity.
- Deskbar's taskbar and Alt+Tab icons looking worse under the package than they do today.

## Constraints

- The product is a WPF app on .NET 10, published self-contained for win-x64, not trimmed
  (WPF is not trim-safe). The direct build is single-file; the Store build is not, because
  the package is already the container and single-file extraction to `%TEMP%` on every start
  would only cost start time.
- Windows 11 only. The manifest's `TargetDeviceFamily` minimum is `10.0.22000.0`, so the
  Store does not offer the app to Windows 10 machines, matching the README's requirement.
  The project's TFM stays `net10.0-windows10.0.19041.0`.
- The repo's rules apply: a version bump rides in the PR that earns it; every PR that bumps
  the version carries release notes; never push to main; measure before claiming.
- Petre's machine runs the direct build all day and must not be left without a bar. A test
  install of the package on it needs his consent, is started from a terminal with
  `DESKBAR_APPDATA` set, and is removed (or its startup task disabled) before he signs out,
  because the installed package's startup task would otherwise start it at the next sign-in
  against his real profile. The direct build is restarted afterwards.
- Partner Center's publisher-name review is in progress. The first upload cannot happen
  until it clears; the package can be built and tested before that.

## Decisions

### Package shape and how it is built (my call)

- A hand-written `AppxManifest.xml` and `MakeAppx.exe pack`, no Windows Application
  Packaging Project and no Visual Studio. The reason is the same as for the signing script:
  every step is a readable file in `scripts/`, runnable locally and in CI, and nothing is
  generated by an IDE that nobody opens. The manifest lives in the repo at
  `src/Deskbar.App/Package/AppxManifest.xml` with its version as a token the pack script
  replaces.
- `scripts/pack-store.ps1 -Version <v> [-PublishDir <dir>] [-Output <path>]`: publishes
  (or takes a published folder), stages it with the manifest, the images and the resource
  index, replaces the version token with `<v>.0` (the Store requires a four-part version
  whose last part is 0), validates that the identity triple matches the values above, runs
  `MakePri.exe` for the image set (next section) and `MakeAppx.exe pack`, both from the
  installed Windows SDK (found the way `Find-SignTool` finds signtool). Output:
  `Deskbar-<v>-store.msix`, unsigned. The Store signs it; an unsigned package is what
  Partner Center accepts.
- `scripts/pack-store.ps1 -TestSign`: additionally creates, or reuses, a self-signed
  certificate whose subject is exactly the Store publisher string, signs the package with
  it and prints the command that trusts that certificate for sideloading. Builder check:
  sideloading requires the certificate in the Local Machine Trusted People (or Root) store,
  not the current user's, so that command needs an elevated prompt, and the script says so
  rather than running it. The certificate never enters the repo.
- Pester tests for the script's pure parts (version token, identity check, image presence),
  in the style of `ReleaseSigning.Tests.ps1`.
- The publish for the package: `dotnet publish -c Release -r win-x64 --self-contained true
  -p:DebugType=none -p:PublishSingleFile=false`, same project, same `VersionPrefix`. The
  exe inside the package is `Deskbar.exe`, so `StartupRegistration.IsTheApp` and the
  pack:// URIs are unchanged.
- A second, small exe in the package: `deskbar-cli.exe`, a console project
  (`src/Deskbar.Cli`) that is the control client only. It parses the same command line as
  `Deskbar.exe ctl` and talks to the same pipe through the Core client code; it owns no
  UI. It exists because the app execution alias must be something a shell can wait for
  (see "The command line under the package"). The direct build does not ship it: the
  one-file rule for the direct download stands, and the shim covers it there.

### Images and the resource index (my call; builder check moved this out of non-goals)

Under the package the shell attributes Deskbar's windows to the package's application user
model ID and draws the taskbar, Alt+Tab and the stand-in's icons from the package's tile
assets, not from `deskbar.ico`. With one plain `Square44x44Logo.png` those icons come out
plated on a coloured tile, which is worse than today and Petre has already sent back a
worse icon once. So the package carries the full small-icon set: `Square44x44Logo` at
scales 100, 125, 150, 200 and 400, plus `targetsize-16/24/32/48/256` each in plain and
`altform-unplated` and `altform-lightunplated` forms, and `Square150x150Logo`, `StoreLogo`
(50 by 50) and `Wide310x150Logo` at scale 100 and 200. Generated by a script in `scripts/`
from `Assets/deskbar-256.png` (and the 256 source regenerated from the ico if sharper) and
checked in under `src/Deskbar.App/Package/Images/`. Qualified names resolve only through a
`resources.pri`, so the pack script runs `MakePri.exe new` with a checked-in `priconfig.xml`
over the Images folder. The spike compares the taskbar icon under the package with the
direct build's at 100% and 150% and reports the difference with screenshots.

### The manifest (my call on contents; the identity values are the Store's)

- Identity: name, publisher and version as above. `PublisherDisplayName`: AppifySheets
  once the review clears, "Petre" if a submission has to go in before it does.
- `TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.22000.0"
  MaxVersionTested="10.0.26100.0"`.
- Application: `Executable="Deskbar.exe"`, `EntryPoint="Windows.FullTrustApplication"`.
  Builder check: this form and the `uap10:RuntimeBehavior="packagedClassicApp"` plus
  `uap10:TrustLevel="mediumIL"` form are alternatives, not a pair; the builder uses the
  `EntryPoint` form, and switches only if MakeAppx or the Store rejects it.
- Capabilities: `runFullTrust` and `unvirtualizedResources` (both restricted; the
  submission's "restricted capabilities" box carries a one-paragraph justification: a
  tray utility that drives Windows' virtual desktops through COM, registers a global hotkey
  and an app bar, and keeps its state in the same `%APPDATA%\Deskbar` folder as its
  unpackaged build so people can switch between the two).
- Virtualisation off for the places Deskbar writes. First choice, builder check: the
  scoped form, `virtualization:FileSystemWriteVirtualization` with
  `ExcludedDirectories` naming `%APPDATA%\Deskbar` and `%APPDATA%\TaskSpaces`, and
  `virtualization:RegistryWriteVirtualization` with `ExcludedKeys` naming the HKCU Run
  key, which limits what the capability asks for and is available at our minimum version.
  Fallback: `desktop6:FileSystemWriteVirtualization` and
  `desktop6:RegistryWriteVirtualization` both `disabled`. Either way the point is the
  same: the package reads and writes the real folder and the real key, or the anti-goal
  above happens. The spike measures which form MakeAppx accepts and whether writes land
  where expected.
- Extensions:
  - Startup task: `<desktop:Extension Category="windows.startupTask"
    Executable="Deskbar.exe" EntryPoint="Windows.FullTrustApplication">` with
    `<desktop:StartupTask TaskId="Deskbar" Enabled="true" DisplayName="Deskbar"/>`.
    Enabled by default, my call: the direct build's box defaults to off only because a
    portable exe has no fixed path to point autostart at, and a package has no such
    problem; tray utilities on the Store (PowerToys is the example people know) start with
    Windows by default. The person can turn it off in Manage or in Settings, Apps,
    Startup. Builder check: whether the task fires at the first sign-in after a Store
    install with no launch in between is measured by the spike, and the README says
    "after you open it once" if it does not.
  - App execution alias (`uap3` and `desktop`): alias `deskbar.exe`, target
    `deskbar-cli.exe`. It puts a `deskbar` command on the PATH through
    `%LOCALAPPDATA%\Microsoft\WindowsApps`.

### The command line under the package (my call; builder check reopened it)

The frame spec promised "`deskbar ctl ...` works from any shell". The reviewer is right
that an alias onto the GUI exe does not deliver that: `deskbar windows` would be an app
start and hit the single-instance guard, and `deskbar ctl windows` from PowerShell would
lose its output, by the measurement recorded in `ControlCommandLine`. So:

- The alias points at `deskbar-cli.exe`, a console program, so `deskbar windows`,
  `deskbar move ...` and the rest work from PowerShell, cmd and Git Bash with their output
  and exit code, no `ctl` and no shim. `deskbar help` prints `ControlUsage` as today.
- The shim `%APPDATA%\Deskbar\deskbar.cmd` is still written by the Store build, pointing at
  the alias path, so every script and briefing that calls `deskbar.cmd` keeps working on a
  Store machine. Its target is stable across updates because the alias is.
- The direct build is unchanged: `Deskbar.exe ctl` and the shim.

### How the app knows it is packaged, and what changes when it is (my call)

A static `Packaging.IsPackaged`, answered once per process by `GetCurrentPackageFullName`
(kernel32; `APPMODEL_ERROR_NO_PACKAGE` means unpackaged). No WinRT needed for the question.
Everything below keys on it, and each switch has a one-line comment naming this design.

Builder check, two facts about the code as it is: the direct build writes the Run value
only from Manage's box and from the 1.x migration, and `ReassertIfEnabled` only rewrites a
value that already exists. And `StartupRegistration.RunsAsPublished` is "the assembly has
no location", which is true only of a single-file build; under the package it is false, so
the Run re-assert, the Start shortcut, the old-version cleanup and the migration's
set-new-Run-value step are already no-ops there. The builder keeps `RunsAsPublished`
meaning what it means and adds the `IsPackaged` switches explicitly, with a comment at
`RunsAsPublished` saying the package relies on it being false.

| Direct build does | Store build does instead | Why |
|---|---|---|
| Re-asserts the Run value on every start when one exists; Manage's box and the 1.x migration write it | Never writes or re-asserts the Run value. On its first packaged start it deletes the direct build's `Deskbar` value and `Deskbar.lnk`, once, behind a marker, and only when `AppPaths.RootOverridden` is false (a test start under `DESKBAR_APPDATA` must never touch the real autostart, as the 1.x migration already rules) | Autostart is the startup task; a Run value pointing at the direct exe would start a second bar at login (anti-goal) |
| Manage's "Start Deskbar with Windows" reads and writes the Run value | Reads and writes the package startup task through `Windows.ApplicationModel.StartupTask` (the WinRT projection is already in the build; `MediaControl` uses the same SDK). `DisabledByUser` or `DisabledByPolicy` shows the box unticked and disabled with a line saying where to turn it on | The task is the only autostart the package has |
| Manage's box, when the Store package is installed on the machine (the alias file exists under `%LOCALAPPDATA%\Microsoft\WindowsApps`) | Not applicable | Builder check: the direct build must not silently take autostart back. Its box then carries a line: "The Microsoft Store copy of Deskbar starts with Windows. Ticking this starts both." Ticking still works; it is a warning, not a refusal |
| Writes `Deskbar.lnk` into the Start menu and keeps it pointed at the running exe | Nothing (the package has its own Start entry); deletes the direct build's `Deskbar.lnk` on the first packaged start, as above | A second Start entry would start the direct exe |
| Deletes older versioned exes beside itself | Nothing (already a no-op, see above) | The package folder is read-only |
| Writes `%APPDATA%\Deskbar\deskbar.cmd` pointing at its own path | Writes the same shim pointing at the alias path | "The command line under the package" |
| Checks GitHub on a timer and from the tray, the bar's background menu, Manage's box and the balloon; downloads an exe beside itself | Checks the Store instead, through `StoreContext.GetAppAndOptionalStorePackageUpdatesAsync`, on the same timer and from the same entry points, gated by the same `State.CheckForUpdates` setting; the offer dialog has one button, "Update from the Store", which calls `RequestDownloadAndInstallStorePackageUpdatesAsync` after registering for restart. The GitHub checker and the download are unreachable | Builder check: the Store cannot update a package while it runs, and the bar always runs, so without this a Store install would never update until a sign-out. `StoreContext` in a desktop app needs `IInitializeWithWindow`; the spike measures whether the app comes back after the install, and if it does not, the dialog says "Deskbar will close to update; open it from Start afterwards" |
| Crash relaunch starts `Environment.ProcessPath` | Same | A process with package identity starting its own exe keeps the identity, which here is wanted |
| `ProgramStarter` (`start`, `restart-app`), the dock pins, `explorer /select`, Task Manager: started with `UseShellExecute` | Started through `CreateProcess` with the `PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY` attribute set to break away from the package, process tree included, when `IsPackaged` | Builder check, sure: a child of a packaged full-trust process inherits the package identity, so VS Code started by `restart-app` would run as Deskbar's package and be ended when the Store services or removes it (anti-goal). Breakaway cannot be expressed through `UseShellExecute`, so these call sites get a starter that uses `CreateProcess` under the package and keep `UseShellExecute` outside it. The spike checks the child's package identity both ways |
| 1.x migration (copy state, swap Run value, delete old shim) | Same copy and old-shim deletion; the Run step deletes the `TaskSpaces` value and sets no new one | The startup task replaces the new value |

### Pro is the package (Petre's model, my call on mechanics)

- A static `Entitlement` in the App project with one question, `IsPro`, and a reason
  (`Packaged licence`, `Store trial until <date>`, `Free tier`, `Store licence unavailable`)
  for the Pro tab and the trace. Unpackaged: never Pro. Packaged: Pro when
  `StoreContext.GetAppLicenseAsync` says the licence is active, trial or full; the Store
  caches licences, so this answers offline. `IsActive` false (the trial has run out, or the
  package was sideloaded with a test certificate and has no licence) means free tier, with
  the reason shown. A `StoreContext` failure is treated as "unavailable", which is Pro for
  that session and traced, because punishing a paying customer for a Store hiccup is worse
  than one unpaid session; the builder puts a bound on it (a retry on the update timer).
- Nothing in this design is gated yet. `Entitlement` ships with tests and a line in the
  trace and the debug dump; the free tier's design wires it to features. Until then the
  direct build and the Store build do the same things, and the listing's description says
  that the free version on GitHub will lose the Pro features in a later update.
- The sideloaded test package has no Store licence, so during the spike and in CI
  `Entitlement` reports free tier. That is correct and is what the tests expect; a
  developer override (`DESKBAR_PRO=1`, honoured only when `AppPaths.RootOverridden` is
  true) exists so Pro paths can be exercised without a purchase. The override is traced
  and never honoured against the real profile.
- Trial: the Store's own, configured on the listing's pricing page. The Store offers fixed
  lengths and 14 days is not one of them; 15 days is, and that is what is set. The app
  reads the trial state and expiry from the licence and never keeps its own clock.

### One machine, one running Deskbar (my call; builder check asked which way)

The single-instance guard is unchanged: both builds take `Local\Deskbar.SingleInstance`,
so the second to start exits. The dialog now says which build is running: the loser looks
for the other `Deskbar.exe` process and reads its path, "The copy installed from the
Microsoft Store is running" when the path is under `WindowsApps`, "The copy at <folder> is
running" otherwise, and the plain sentence of today when it cannot read a path. No new pipe
command is needed for this, and it works against every direct build ever shipped.

Both installed on one machine is supported in exactly this sense: the Store build takes
autostart once (the deletion above), the direct build warns before taking it back, and if
someone does tick both, the loser of the login race shows the dialog above and the person
knows what to untick. The README's install section gets a "From the Microsoft Store"
heading that says this in one paragraph, including the way back to the direct build:
untick the box in the Store copy's Manage (or remove the Store copy), then tick the box in
the direct copy's Manage. Nothing re-registers autostart by itself.

### Shared state and version lag (my call; builder check raised it)

The Store will lag the direct channel: releases have been daily, certification takes days,
and submissions are by hand. On a single-channel machine that is invisible. On a machine
with both, an older build can be handed `state.json` written by a newer one, and
`JsonPersistenceStore` has no schema version: an enum value it does not know fails the
load, which backs the file up and starts empty, and even a clean load drops fields the
older build does not know when it writes back. Until now that only happened on a deliberate
rollback.

So `state.json` gains a `WrittenBy` version. A build that finds a `WrittenBy` newer than
itself refuses to start with a dialog naming both versions ("Your settings were saved by
Deskbar 2.1.50; this is 2.1.43. Start the newer copy, or remove it and delete the
WrittenBy line from state.json to go on with this one.") rather than loading. Same major
version only; the 1.x migration is not affected because it copies into a new file. This is
small, it is in Core, and it is the only honest alternative to silent loss.

### Versioning and what ships from one tag

The package version is `VersionPrefix` plus `.0`. The CI job that builds the direct exe
gains a sibling step that runs `pack-store.ps1` with the same version and keeps the `.msix`
as a workflow artifact named `Deskbar-<v>-store.msix`. It is not attached to the GitHub
release: an unsigned `.msix` cannot be installed by anyone, and a release asset that does
nothing when double-clicked would only produce support mail. Petre downloads the artifact
and uploads it in Partner Center. Not every release needs a Store submission; every
submission carries the whole app.

### Testing, and the spike that proves full trust

- Unit tests, Core and Windows: `Packaging` is a static the tests can override (the same
  pattern as `AppPaths.RootOverridden`); every switch in the table has a test for both
  values. `StartupRegistration` gains a packaged twin for the startup task behind an
  interface, so Manage's box is testable without a package. The `WrittenBy` rule and the
  breakaway starter's argument building are pure and tested in Core and Windows.
- A `version` control command is added to `ControlUsage` (the test that every dispatched
  command is listed covers it); the CI smoke step uses it.
- Pester tests for `pack-store.ps1`.
- CI: the pack step itself validates the manifest (`MakeAppx` refuses a bad one). A further
  CI step signs the package with a throwaway certificate created on the runner, trusts it
  in the machine store (the runner is elevated), installs it, starts the app with
  `DESKBAR_APPDATA` set to a runner folder, asks it `deskbar version` through the alias, and
  runs the Windows App Certification Kit against the installed package. Builder check: WACK
  is an optional SDK feature and may not be on `windows-latest`; the builder checks the
  image first, and if it is missing or the step is too slow, WACK runs by hand before every
  submission and the CI step keeps the install-and-ask smoke test.
- The spike, before the switches are finished: build the package by hand, install it
  signed with the test certificate, and check each line of the goals list, plus the four
  the builder check added: a child started by `restart-app` carries no package identity;
  the startup task fires at the first sign-in after install without a launch in between
  (or not); the app comes back after a Store-style update (simulated by installing a
  higher-versioned test package over the running one); the taskbar icon at 100% and 150%
  against the direct build's. The machine is a Windows 11 VM or Windows Sandbox first, and
  Petre's machine last, under the constraint above. Findings go into the ledger, and
  anything that does not work under the package is a design question, not a workaround.

### The listing (Petre writes the final words; this is the shape)

- Name: Deskbar. Publisher: AppifySheets. Category: Utilities and tools. Price: $19 once
  (Microsoft converts per market), all markets, with a 15-day free trial. Age rating: the
  IARC questionnaire, answered as a utility with no content.
- Short description, under 100 characters: "The taskbar for your virtual desktops: name
  them, see every window on one bar."
- Description: the README's first section, cut to the Store's length, plus a paragraph
  saying that this is Deskbar Pro, that a free version with fewer features is on GitHub,
  and that the free version keeps everything until the first gated release. The exact free
  and Pro split is the frame spec's open item for Petre and is not decided here.
- Screenshots: at least three at 1920 by 1080, taken from a profile whose workspace names
  are generic (Work, Writing, Side project). Captured by Petre or by the builder with
  `DESKBAR_APPDATA` pointing at a staged `state.json`. The README's own hero image is
  behind reality and is not reused.
- Store logo for the listing: 300 by 300, from the same source as the tiles.
- Privacy policy URL: `https://deskbar.app/privacy`. Website: `https://deskbar.app`.
  Support contact: `hello@deskbar.app`.
- Restricted capabilities justification: the paragraph under "The manifest".
- Notes for certification: how to see the app do something (open Manage from the tray,
  create a workspace, watch the bar), that it registers `Win+Ctrl+Tab`, and that it starts
  with Windows by default and where that is turned off.

### After the listing is live (one small PR)

`UpdateService.StoreProductId` becomes `9PJX26NGFPNF`, which lights the dormant "Microsoft
Store" button on the direct build's update offer; the README's badge comment is uncommented
with the same ID; the site's badge placeholder is switched on in the site repo. Not before:
the link 404s until the listing is published.

### Order of PRs

1. Packaging: manifest, images and resource index, `pack-store.ps1` and its tests,
   `Deskbar.Cli`, `Packaging.IsPackaged`, the behaviour switches with their tests, the
   breakaway starter, `WrittenBy`, the `version` command, `Entitlement`, the README
   section, the CI pack step. One PR, version bump included, because it changes the app. If the spike shows it
   is too much for one review, the split is: packaging and CLI first (no behaviour change
   to the direct build), switches second.
2. The spike runs between the manifest existing and the switches being finished, on the
   builder's branch; its findings may reorder the rest.
3. The CI install-and-WACK step, if it was not stable enough to land in 1.
4. The first submission (Petre, by hand), then the "after the listing is live" PR.

## Left to the builder

- The exact P/Invoke and caching for `Packaging.IsPackaged`, and how tests override it.
- Where the first-packaged-start marker lives. My suggestion: a `packaged-first-start-done`
  file in `%APPDATA%\Deskbar`, beside the state; the `RootOverridden` gate above is what
  protects the real autostart, not the marker's location.
- The startup-task wrapper's shape and how a `DisabledByPolicy` state is shown in Manage.
- The breakaway starter's shape and which call sites share it.
- `StoreContext` initialisation in a WPF process (`IInitializeWithWindow` on the bar's
  hwnd) and how the restart registration is done.
- The image generation script and the `priconfig.xml`.
- The CI step's throwaway certificate and the WACK invocation, and whether it runs on every
  version or on demand.
- The restricted-capabilities paragraph's final wording.

## Decided by Petre on 2026-10-24

- Pricing model: the Store build is Deskbar Pro, paid; the direct build is the free tier;
  no licence key, no Paddle, no site checkout. The first submission goes in as a paid
  listing before the free tier's gate exists; early Store buyers get what the direct build
  still gives free, and the description says so.
- Payout: Georgia is on Microsoft's payout list for the Store, bank transfer and PayPal.
  Petre fills in the payout and tax profile in Partner Center before the first submission;
  that is paperwork, not code, and it is where the address may be asked for again.

## Open for Petre

1. Who captures the screenshots, and on which machine.
2. Whether the package should also be offered for sideloading from GitHub Releases later,
   which would need the certificate this design avoids. Not now; noted so it is not
   forgotten.
3. The free and Pro split, carried over from the frame spec, needed by the next design and
   by nothing in this one.
