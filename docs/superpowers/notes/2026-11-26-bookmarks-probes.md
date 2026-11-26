# Bookmark probes, 2026-11-26

Measured on Petre's machine (Windows 11 26200) for the workspace bookmarks plan. Everything was read-only: windows were
enumerated and their property stores read, the Start menu folder and `shell:AppsFolder` were listed. No window was
started, moved, closed or focused. Probe scripts live outside the repo. The AppUserModelID read is the same call as
`ShellTaskbarPins.AppIdOf` (`SHGetPropertyStoreForWindow`, `PKEY_AppUserModel_ID`), with the process DPI awareness set
to per-monitor v2 first and every P/Invoke declared `CharSet.Unicode`.

One trap hit on the way: declaring `IPropertyStore` without `[PreserveSig]` makes the runtime treat the `int` return as
the out value, and every read came back null. Production code has `PreserveSig`, so it is not affected.

## Step 1: AppUserModelIDs of open windows

| Window | Process | AppUserModelID |
| --- | --- | --- |
| Plain Edge window (new tab, a GitHub page) | `msedge.exe` | `MSEdge` |
| Plain Chrome window | `chrome.exe` | `Chrome` |
| Chrome window driven by the browser MCP | `chrome.exe` | `Chrome.mcpchromeeeeb218.Default` (a profile-scoped ID, still no `!`) |
| Edge PWA, Outlook (PWA) | `msedge.exe`, the browser's own process | `outlook.cloud.microsoft-6A7975FB_3y1e9vxbwqkga!App` |
| Edge PWA, iam.ge | `msedge.exe`, the same process | `app.iam.ge-D9839553_en4scw7w9zwzy!App` |
| RustDesk (desktop app) | `RustDesk.exe` | none (null) |
| Teams, Photos, WhatsApp (packaged WinUI) | own exe | none (null) on the top-level window |
| Windows Terminal, VS Code, Beeper, OBS | own exe | none (null) |
| Deskbar's own task windows | Deskbar | `Deskbar.Jump.<guid>`, set by Deskbar itself |

Not open during the probe: Calculator (so no `ApplicationFrameHost` window to read) and Notepad. Both are in
`shell:AppsFolder` with a packaged ID (see Step 2).

Findings:

- Both open Edge PWA windows run inside the browser's one process (`msedge.exe`), so they share its command line. The
  command line of that process happened to carry the `--app-id` of a third PWA, so a command line says nothing about a
  window. Only the window's own AppUserModelID tells the PWAs apart. This matches the existing trap in CLAUDE.md.
- A PWA's ID has the packaged form `<family>!<app>`. The two plain browsers have no `!` (`MSEdge`, `Chrome`).
- Most desktop and packaged apps return null from the window's property store, so a window-derived ID exists for PWAs
  and browsers but not for everything.

## Step 2: Start menu entries of PWAs

`shell:AppsFolder` lists the PWAs with the packaged ID as the parsing name, for example:

| Name | Parsing name | `System.Link.TargetParsingPath` |
| --- | --- | --- |
| Outlook (PWA) | `outlook.cloud.microsoft-6A7975FB_3y1e9vxbwqkga!App` | empty |
| iam.ge | `app.iam.ge-D9839553_en4scw7w9zwzy!App` | empty |
| YouTube Music | `music.youtube.com-5929F88E_vezhnr0wkvrcy!App` | empty |
| Microsoft Teams (PWA), GitHub, Grok, Bitwarden, code-server | `<host>-<hash>_<publisher id>!App` | empty |
| Microsoft Edge | `MSEdge` | `...\Edge\Application\msedge.exe` |
| Google Chrome | `Chrome` | `...\Chrome\Application\chrome.exe` |
| Calculator | `Microsoft.WindowsCalculator_8wekyb3d8bbwe!App` | empty |
| Notepad | `Microsoft.WindowsNotepad_8wekyb3d8bbwe!App` | empty |
| Outlook (classic) | `Microsoft.Office.OUTLOOK.EXE.15` | `...\OUTLOOK.EXE` |

The two PWA IDs read from open windows in Step 1 are both parsing names in `shell:AppsFolder`, character for character.
The plain Edge ID `MSEdge` and the plain Chrome ID `Chrome` are in `shell:AppsFolder` too, as the browsers' own entries.

The Edge PWAs here are not `.lnk` files. Searching the user and all-users Start menu `Programs` folders for shortcuts
to `msedge.exe`, `chrome.exe` or carrying `--app-id` found only the plain Chrome, Edge, Outlook (classic) and Imaging
Edge shortcuts, with real exe targets and no arguments. The PWAs are MSIX packages with the Edge runtime as host.
Reading the Outlook (PWA) package manifest: identity name `outlook.cloud.microsoft-6A7975FB`, package family
`outlook.cloud.microsoft-6A7975FB_3y1e9vxbwqkga`, one application with `Id="App"`, `uap10:HostId="PWA"` and
`uap10:Parameters="--app-id=<id> --ip-edge-aumid=Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!MSEDGE"`. There is no
`--profile-directory` in the manifest.

`msedge_proxy.exe` exists next to `msedge.exe` and `chrome_proxy.exe` next to `chrome.exe`, but no shortcut on this
machine targets either of them.

## Step 3: does `shell:AppsFolder\<id>` start the PWA (not run live)

Not run. The controller ruled out starting any app. From Steps 1 and 2 only the precondition is established: every open
PWA window's ID is exactly the parsing name of an item in `shell:AppsFolder`, and so is a plain Edge window's ID
(`MSEdge`). `explorer.exe shell:AppsFolder\<id>` is how Start launches those items, and `ShellTaskbarPins` already launches installed apps this
way (the comment at its launch method names that arm), so the PWA IDs should launch the same way. Not measured: whether a
PWA that is closed opens its own window, and whether `shell:AppsFolder\MSEdge` opens a new browser window.

## Step 4: what a Chromium address-bar drag offers (not run)

Skipped by the controller, nothing was asked of Petre.

## Decisions

(a) `BookmarkCandidates.IsStartableAppId`: an ID is startable when it has the packaged form, that is it contains `!`
(`<family>!<app>`). That accepts every PWA ID and the Store apps (Calculator, Notepad), and it excludes the plain
browsers without a special case, because `MSEdge`, `Chrome` and `Chrome.<profile>.<name>` have no `!`. Add an explicit
exclusion list only if a plain browser ever shows a `!`. Classic IDs such as `Microsoft.Office.OUTLOOK.EXE.15` are in
`shell:AppsFolder` but are not matched by this rule; those windows are covered by the Program bookmark route. Most
desktop windows have no ID at all.

(b) Step 3 did not fail, it was not run. Plan on the app-ID route for a PWA window, since the ID matches an
`shell:AppsFolder` parsing name. If a later live check shows that `shell:AppsFolder\<id>` does not open the PWA, the
spec's fallback applies: the PWA window becomes a Program bookmark with the browser exe plus `--app-id` (and
`--profile-directory` where the window's profile is known), and Task 4's window rule changes to read the browser's
command line, which is unreliable for PWAs that share the browser's process.

(c) No mapping of `msedge_proxy.exe` or `chrome_proxy.exe` is needed by anything observed here: the Start menu
shortcuts target the real browser exes and the MSIX PWAs have no shortcut. The proxy exes do exist, and classic Chrome
or Edge app shortcuts on other machines target them, so keep Task 3's `ProxyBrowsers` mapping as a cheap safeguard, but
it is not exercised by this machine's data.

(d) Not measured; Task 9 reads UniformResourceLocatorW, then UniformResourceLocator, then UnicodeText, and takes the name from FileGroupDescriptorW with .url stripped, else the host.
