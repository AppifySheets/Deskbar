# Deskbar: signed, sold, renamed

Design for taking TaskSpaces from a public GitHub build to a paid product called
Deskbar, signed so Windows trusts it. Written 2026-10-18 from a conversation with
Petre; decisions marked "Petre" are his, decisions marked "my call" were made by the
author of this document and can be overturned.

## Intent

The app is in daily use and works. Nothing about the product changes. What changes
is everything around it: its name, the trust Windows shows at download, the money,
and the paperwork. The point is a buyer who hears of the app, downloads it, sees a
named publisher instead of a red "unknown publisher" screen, tries it for a while,
pays, and keeps it, with nothing in that path depending on Petre being at a keyboard.

Petre's reasons, in his words where he gave them: "sign the app so it is trusted and
get ready to sell it"; "we're going to monetize the app"; "it's for people who already
know about virtual desktops"; "I'd rather stay lean as an individual" and register a
company "at a later point, based on what the app does in the marketplace".

## The buyer's path (Petre approved this picture)

A buyer hears of it from a post or a search for a taskbar for virtual desktops and
lands on the product site: one page, a screenshot of the bar, the tagline, a download
button. They download a signed installer; SmartScreen names the publisher instead of
warning. The bar runs with every feature for a trial period, then drops to the free
tier, which keeps working for as long as they like. If they want the rest they buy
Deskbar Pro on a checkout run by a merchant of record, which charges the right VAT in
every country, and a licence key arrives by email. They paste it into Manage; the app
checks it offline against a public key baked into the exe; nothing phones home.
Updates keep coming through the existing update checker. The Microsoft Store is a
second channel, alongside the direct sale, not instead of it, and the same key works
in both.

## Decisions already made

### The name: Deskbar, shown as "Deskbar for Windows" (Petre)

Chosen after clearing it: no mark containing "deskbar" in WIPO's Global Brand Database
(89 registers), no current Windows software with the name, deskbar.app free as of
2026-10-18 (deskbar.com held since 2003). The word is generic for a taskbar toolbar
(Google Deskbar 2003, GNOME, Haiku), so it cannot be owned as a mark, and Google results
for the bare word go to Wikipedia and Haiku. Petre weighed that and kept it; "deskbar
for windows" returns only dead 2003 pages, which a live product page will outrank.

"for Windows" is a compatibility line next to the name, per Microsoft's brand
guidelines; it is never part of the exe name, the product name field or a logo.
Tagline, to carry the search phrase: "the taskbar for your virtual desktops".

Rejected and not to be reopened on the same facts: TaskSpaces (collides with TaskSpace,
a dead 2017 Windows window-grouping utility still listed on every download site;
taskspaces.com is for sale on a brand marketplace), Spacebar (live US software mark
5240304, a 1,300-star macOS bar app, a Store toolbar), Virtual Taskbar (clean but a
category, not a name), Deskrail, Deskbelt (taste).

### Who signs and sells: Petre personally, for now (Petre)

AppifySheets is a brand, not a registered company. Petre stays a private individual,
declares the income himself, and registers an LLC in Georgia later if sales justify
it. Consequences the design accepts:

- The certificate is an individual-validation code signing certificate in Petre's
  name. Windows shows "Petre Chitashvili" as publisher. Microsoft's own Trusted
  Signing is unavailable: its individual validation is US and Canada only and paused,
  and Georgia is on no list for organisations either.
- The Store account is an Individual account. It can never convert to a Company
  account; a later LLC means a new account and an app transfer through Microsoft
  support. The publisher display name is set once at registration and never changes:
  "AppifySheets" (Petre), falling back to his name only if an Individual account is
  refused a brand name.
- The merchant of record account is in Petre's name as an individual seller.

Switching to the LLC later costs: a new certificate (the old one keeps signing until
it expires, no gap), a new Store account and transfer, re-onboarding at the merchant
of record. Known, bounded, and not a reason to wait.

### Source stays public, under PolyForm Strict plus a contribution permission (my call)

The repository is public today with no licence file, which means all rights reserved:
nothing has been given away. Paid features are gated by a key inside a public
repository, so the key is a polite gate and the licence text is what protects the sale.

The candidates, from their texts (polyformproject/polyform-licenses on GitHub):

- PolyForm Noncommercial 1.0.0 lets anyone use, build, modify and redistribute the
  software, modified or not, for noncommercial purposes: personal study, hobby and
  private use, plus charities, schools, public research and government. A business
  using it at work is outside the grant. It has a patent grant with a defence clause,
  a notice requirement and no copyleft. It does permit distributing builds, so a
  hobbyist may legally publish "Deskbar with everything unlocked" as long as they do
  not charge. With a free tier that is the one thing the licence must prevent.
- PolyForm Strict 1.0.0 is the same noncommercial grant with no distribution and no
  changes. People may read, build for themselves and verify the app phones nobody,
  and may not publish forks or builds.
- GPL is real open source and lets anyone publish free builds; Krita and Paint.NET
  live with that and sell convenience on the Store. Rejected for the same reason as
  Noncommercial once the free tier exists.

My call: `LICENSE` is PolyForm Strict 1.0.0, with one additional permission stated in
the repository: anyone may submit changes to this repository, and by doing so
licenses those changes to the licensor under terms that allow their use in Deskbar.
Commercial use of any kind needs the paid licence, which comes with its own terms (see
"Legal text"). Neither licence is open source by the OSI definition; the README says
"source available" and never "open source".

### Free tier, trial, Deskbar Pro

Petre: "free listing so we can have more people. we may also have a free and paid
versions"; after the trial "downgrade to the free, with a reminder somewhere that
there are more features and periodic pay reminders".

- Trial: 14 days from first run (Petre), every feature on, no account, no email.
- After the trial the app drops to the free tier and keeps working indefinitely.
  Nothing is deleted: a fourth workspace, a group, a roster or a rename made during the
  trial stays in state.json and comes back the moment a key is entered. While on the
  free tier the paid features are visible and disabled, with the reason shown.
- Reminders (my call): a "Deskbar Pro" item in the tray menu and a Pro tab in Manage
  listing what is paid, with the key field; and once every seven days after the trial,
  a dismissable line under the bar saying the Pro features exist, never a dialog,
  never more often. Entering a key ends all of it.
- Deskbar Pro: one-time purchase, $19, one key, all 2.x updates included (Petre).
- Licensed per user, not per computer (Petre): one person, any number of their own
  machines, not shared. The EULA says so, and the offline key makes it the only claim
  the app can keep, since nothing counts machines.
- The free and paid split is Petre's decision (see "Left open"); the proposal there is
  built so the free tier is the version people recommend and Pro is what a daily user
  wants after a week.
- Key format: a short string, the licensee email and an Ed25519 signature; the app
  holds only the public key and verifies offline. No activation server, no machine
  count, no phone-home. A refund revokes nothing; that is accepted.
- Keys are minted by a small webhook endpoint (a Cloudflare Worker is the default
  shape) that the merchant of record calls on purchase and that emails the key. The
  private key lives only in that endpoint's secret store.

### Merchant of record: Paddle (my call, Lemon Squeezy as fallback)

Paddle is the seller to the buyer and handles VAT and sales tax everywhere, so neither
Petre nor a later LLC registers for tax anywhere. It takes individual sellers and,
per the one source found, pays out to Georgia by bank; Lemon Squeezy reportedly pays
Georgia only via PayPal but has built-in licence keys that would remove the webhook
endpoint. Petre confirms the payout route on Paddle's own pages before signing up; if
Paddle cannot pay him, Lemon Squeezy with its native keys replaces both Paddle and the
minting endpoint.

### Certificate and signing (my call on mechanics)

- An individual-validation OV code signing certificate from Sectigo or SSL.com, about
  $220 to $300 a year at resellers plus about $130 once for the USB token the key
  must live on. Validation: passport or ID plus proof of address.
- Signing runs on Petre's machine, where the token is, by a local script and not by
  a self-hosted runner. My call, on the builder check's point: GitHub advises against
  self-hosted runners on public repositories because a pull request from a fork brings
  its own workflow file, and a one-line `runs-on: self-hosted` job would then run on
  the machine with the signing token plugged in. The release workflow keeps building
  and testing on the hosted runner and publishes the release as a draft with unsigned
  artefacts; the script (`scripts/sign-release.ps1`) downloads the draft's artefacts,
  signs the exe and the installer with signtool and a timestamp, replaces the assets
  and publishes the release. The workflow refuses to publish a non-draft release whose
  assets carry no signature, so an unsigned build cannot reach users by accident.
- Expectation to set in the README and the site: a new certificate has no SmartScreen
  reputation, so for the first weeks some buyers will still see "not commonly
  downloaded" even on a correctly signed file. EV no longer buys instant reputation
  (DigiCert says so). Consistent signing of every release builds it; the Store channel
  has no such prompt at all, which is why the Store is first class.

### Store first, direct download unsigned (Petre, 2026-10-19)

Petre asked whether the Store could be the only trusted download, and whether the direct
download could point at it. Yes to both, and it changes the order below. The Microsoft
Store signs the package itself, so no certificate is needed for the channel buyers who
care about trust will use. The direct exe on GitHub Releases stays exactly as today,
unsigned, for the people who want it and already click past SmartScreen; the README, the
release notes and the update checker point at the Store first. The certificate purchase
and the signing pipeline (already built and dormant behind the unset variable) are
deferred until sales show the direct download matters to paying customers. If it comes
to that, the cheapest route found on 2026-10-19 is Certum's Standard Code Signing through
the reseller My-SSL at $99 a year, cloud-held key, individuals eligible, no token to ship;
Microsoft's Trusted Signing is closed to Georgia for both individuals and companies.

Consequences: the installer is not built yet (the direct download stays the portable
exe); the Store packaging step moves up to directly after the rename; the order of work
becomes rename, Store packaging and listing, free tier and key, site and checkout, then
signing if ever needed. The sections below are kept as written for the record.

### Packaging

- Direct download: a signed installer (Inno Setup, my call) that installs per user,
  adds Start menu and startup entries and an uninstaller, plus the signed single-file
  exe it contains. The portable single exe stays a shipped asset alongside it (my
  call: it costs nothing and some buyers of a tray utility want exactly that).
- Store: a free listing (Petre), gated by the same key as the direct download. The
  Store build has the same Pro tab with the same key field and a Buy link to the
  site's checkout; Store policy 10.8.1 allows a non-game app to sell through its own
  commerce, and the builder confirms the current wording of that policy before the
  listing is submitted. An MSIX package with full trust, built from the same
  binaries; the Store signs it, and the update checker is off in Store builds because
  the Store updates. Startup becomes the MSIX StartupTask. The named pipe, the global hotkey and the
  virtual desktop COM calls are known to work under full trust; the spike that proves
  it is part of the Store project, not assumed, and it covers the file system too
  (next item).
- Both channels ship the same version from the same tag.

### Getting 1.x users to 2.0 (my call, from the builder check)

The 1.x update checker takes the first `.exe` asset on `releases/latest`, downloads it
beside the running exe, starts it with `--await-previous` and quits. So:

- Release assets have distinct, stable names: `Deskbar-Setup-<v>.exe` and
  `Deskbar-<v>.exe` (portable). The installer is uploaded first so a 1.x client gets it.
- The installer tolerates being launched that way: it ignores `--await-previous`,
  installs silently when started by an old client, and relaunches the app.
- The migration on first 2.0 start copies `state.json` from the TaskSpaces folder,
  removes the old HKCU Run value that points at the old portable exe (or two bars
  start at login), and removes the old `taskspaces.cmd` shim.
- The 2.x checker picks the asset matching its own install type by name.

### One machine, one install (my call, from the builder check)

The direct install and the Store install do not coexist. Each refuses to start if the
other's pipe is live and says so. The MSIX manifest opts out of file system and
registry virtualisation so the Store build reads the same real `%APPDATA%\Deskbar`
and runs the same migration. The Store build writes no shim; it declares an app
execution alias so `deskbar ctl ...` works from any shell, and the direct install's
shim stays for parity.

### Existing users and the gate (my call, from the builder check)

Nobody is grandfathered. Step 4 ships a key-minting command-line tool in the same PR
as the gate, so the first gated release can be preceded by hand-minted keys for Petre
and his colleagues; without a key they land on the free tier like everyone else, with
their extra workspaces, groups and renames preserved but disabled. The private key
never enters the repository; the tool reads it from a file the operator names. The
gate is in every build including Debug and source builds: Petre runs with a key like
everyone else. A private build with the gate patched out is "changes" and outside
PolyForm Strict; the design accepts that it will happen on a few machines and that
the licence, not the code, is what makes it a violation.

### Legal text (my call on shape; Petre signs off)

Paddle's seller approval and the Store listing each ask for a privacy policy, terms
and a refund policy reachable from the domain, and the paid binary needs an EULA that
PolyForm, which covers the source, does not provide. The "one page" site therefore
has a footer linking to three short pages: terms and EULA, privacy (which can be one
sentence: nothing is collected), refunds (Paddle's standard policy). The builder
drafts them; Petre, as the seller, approves them before step 0 completes.

### The rename is a migration, not a find and replace

Product and assembly name Deskbar, exe `Deskbar.exe`, pipe name, AppData folder
`%APPDATA%\Deskbar` with `state.json` copied from `%APPDATA%\TaskSpaces` on first run
if present, shim `deskbar.cmd`, GitHub repository renamed to AppifySheets/Deskbar (old
URLs redirect, and the update checker's URL changes anyway), `Product`, `Company`
and `Description` fields, window titles, the tray tooltip, the README, the manual test
script. Tests follow. The CLAUDE.md terminology section is rewritten. Version becomes
2.0.0: the rename plus the paid model is a major version by any reading.

## Goals

- A download that names its publisher on SmartScreen and installs without a red screen.
- A buyer can go from the site to a licensed bar without Petre doing anything.
- The direct channel and the Store channel both work from one tag.
- Existing users (Petre, a few colleagues) keep their state across the rename.

## Non-goals

- Subscriptions, seat counts, floating licences, a customer portal.
- Telemetry, crash reporting, analytics. None exist and none are added.
- A website beyond one page plus the legal pages its footer links to.
- Localisation.
- A company registration. That is a later decision on sales data.

## Anti-goals (what would make this a failure even if it worked)

- A licence check that calls home or breaks offline.
- A rename that loses a user's workspaces, roster or renames.
- A signed build that still shows "unknown publisher" because the installer was
  signed and the exe was not, or either was signed without a timestamp.
- Private material in the public repo: keys, the Paddle webhook secret, Petre's
  address from the certificate paperwork. The public key in the exe is the only key
  that ever lands in git.
- Calling the product open source in the README while the licence says otherwise.

## Constraints

- Windows only, WPF, net10.0-windows, single-file publish, no trimming (WPF is not
  trim-safe). The release workflow builds on windows-latest and must keep doing so.
- Never push to main, never merge; one branch per topic, one PR each, version bump
  in the PR that earns it.
- The running app owns its state; nothing outside it edits state.json, including the
  migration, which runs inside the app on first start.
- Petre uses the app all day: the rename must not leave his machine without a bar.

## Order of work, and what each piece is

Each is its own design-and-plan cycle with its own PRs; this document is the frame.

0. Paperwork, Petre, no code: buy the certificate and token; register deskbar.app;
   open the Paddle account and confirm payouts; open the Store Individual account and
   settle the publisher display name. Weeks of validation latency, so first.
1. Licence file, the contribution permission and README wording. Smallest PR,
   unblocks the public story.
2. Signing: the self-hosted runner, the workflow split, signtool with timestamp, a
   proof on a clean VM that SmartScreen shows the publisher. Can ship under the
   TaskSpaces name; the certificate does not care.
3. Rename to Deskbar with the state migration. Version 2.0.0.
4. Free tier, trial and licence key: the feature gate and the free tier's limits, the
   trial clock, the key format and verifier, the Pro tab in Manage and the tray item,
   the weekly reminder line, the minting tool and endpoint.
5. Site and checkout: deskbar.app, Paddle product, webhook to the minting endpoint,
   key email.
6. Store: MSIX packaging project, StartupTask, full-trust spike, listing.

## Left to the builder

- Installer details (Inno Setup script, per-user install location, upgrade handling).
- The signing script's token PIN handling and how the workflow detects an unsigned
  asset.
- The exact key string layout, as long as it is Ed25519 and verifies offline.
- How the trial clock resists the obvious "delete the folder" reset (my suggestion: it
  need not; the free tier is the floor, the trial is a courtesy).
- How a disabled paid feature looks and explains itself on the free tier.
- The Worker's language and hosting account, and how it emails the key.
- The MSIX project shape.

## Decided by Petre on 2026-10-18

- Store listing: free, key-gated, same key as the direct download.
- Price: $19 once, all 2.x updates included. Trial: 14 days.
- Store publisher display name: AppifySheets.
- After the trial: downgrade to the free tier, with a reminder that Pro exists and
  periodic reminders to pay.

## Left open for Petre

1. The free and paid split. Proposal:
   - Free: name your desktops; the bar with every desktop's windows; click to switch;
     drag a window to another workspace; the Win+Ctrl+Tab switcher; colours by
     position; up to three workspaces.
   - Pro: unlimited workspaces; groups; placement memory (rosters); window renames;
     custom colours; the pins dock line; bar styles; docking modes; the automation
     pipe and shim.
   The workspace cap is the lever that scales with how much someone lives in the app;
   the Pro list is everything that exists because Petre lived in it.
2. The licence: PolyForm Strict with the contribution permission (this design's
   call), or PolyForm Noncommercial accepting that unlocked free builds may be
   published noncommercially.
