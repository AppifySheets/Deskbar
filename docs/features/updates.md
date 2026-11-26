---
title: Updates
summary: Deskbar checks for a newer release every five minutes, offers an update at most once a day (a critical fix at once), and installs the new exe beside the one you are running.
order: 14
---

Every five minutes Deskbar asks whether a newer release exists. It offers you an update at most once a day, in a small window of its own, and a release that fixes something serious straight away.

## How to use it

The GitHub copy asks GitHub. It asks GitHub's API first, and when the API refuses it asks github.com's release pages instead. That matters on office networks: the API answers at most 60 unsigned requests an hour from one internet address, and a whole office often shares one, so the API is often refusing there. A release whose exe is still being built is ignored until it is ready. New releases come out often, sometimes many in a day, so Deskbar does not ask about each one: it waits until a day has passed since it last asked, then offers the newest release, which carries everything released before it. A critical release is offered as soon as it is out. You are asked once: "Update Deskbar to 1.28.0?". Say no and an **Update to …** item stays on the tray and bar menus until you are on that version. Only the newest offer is ever shown. A newer release that comes out while you are waiting for your day takes over that menu item quietly, without asking again. A critical release is offered once; if you say no, later releases wait for the day as usual.

Accepting downloads the new exe next to the one you are running, never over it, through a `.part` file that is renamed only when complete, with a progress window showing the real percentage. Then the new copy starts, the old one exits, and "Start with Windows" is re-pointed at the new file. The previous exe stays on disk, so if you dislike the new version you can go back one step. When the new version starts, it removes the `Deskbar-<version>-win-x64.exe` files beside it that are older than that previous one. Downloads are https only, from github.com or githubusercontent.com.

**Check for updates…** on the tray and bar menus asks immediately and always answers: you are on the latest, a newer one exists, the download is still being built, or the check failed and here is the releases page.

The Microsoft Store copy does not use GitHub for any of this. It asks the Store on the same timer, from the same menu items and under the same checkbox in Manage. Its offer window has one action button, **Update from the Store**, which has the Store install the update. Deskbar closes to update and comes back by itself.

Deskbar asks in its own window rather than with a Windows notification because a portable exe with no Start menu shortcut has no AppUserModelID, and Windows silently drops its toasts. Registering one would cost the "one file, nothing installed" property.

## Settings

**Check GitHub for new versions**, in Manage's Settings tab, governs only the automatic check. A manual check still contacts GitHub, because clicking it is the request, and it always finds the newest release, whatever day it is. Unticking takes effect at the next start.

**Offer every new version as soon as it is out**, under it, drops the daily wait: every release is offered as it appears, within five minutes. It is off unless you tick it, and it takes effect at the next check. The Microsoft Store copy does not have it, because the Store decides when its updates arrive.

The update check is the app's only network traffic on its own. [Reporting a problem](reporting-a-problem.md) sends a report only when you press Send.

## With Claude sessions

Deskbar rewrites its Claude Code skill file when it updates. See [Claude Code sessions](claude-sessions.md).

## Related

- [Manage](manage.md) has the update box.
- The README on [installing](../../README.md#install) and [the Microsoft Store copy](../../README.md#from-the-microsoft-store).
