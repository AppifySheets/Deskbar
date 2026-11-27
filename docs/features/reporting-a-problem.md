---
title: Reporting a problem
summary: Send a report with what happened, screenshots and an optional debug dump, straight from the tray or the bar.
order: 15
---

**Report a problem…**, on the tray menu and the bar's menu, opens a small window that sends a report privately to the Deskbar team.

## How to use it

Say what happened, paste screenshots with Ctrl+V (Win+Shift+S works well) or drop image files on the window, up to five, and press Send. Nobody needs an account or a sign-in.

The debug dump is ticked by default. It describes your screens, the bar and every window on them, titles included, so **View what it contains** opens it in Notepad before anything is sent. Untick it if you would rather not send it.

The contact line is optional. Without it the report can be read but nobody can answer you or ask a follow-up question, so Send asks once when the line is empty or does not look like an email, a Telegram @handle or a phone number. Press "Send anyway" to send it as it is.

Reports go privately to the Deskbar team, not to a public issue tracker. The window shows a reference number once the report has arrived. A report from a copy with Deskbar Pro is marked priority, and those are answered first.

If the report cannot be sent (no connection, for example), the whole report is saved under `%APPDATA%\Deskbar\reports` and the window names the folder. **Try again** sends the same report.

The report travels over https to `report.deskbar.app`, a small relay whose code is in the repository's [relay folder](https://github.com/AppifySheets/Deskbar/tree/main/relay). The relay holds the key that files the report, and the app holds none.

## From the manager or a script

The [manager session](claude-sessions.md#the-manager-session) can send a report for you: tell it what went wrong, from the desk or from the Claude app on your phone. A script can do the same with `deskbar.cmd report -`, the text on stdin:

```bash
"$APPDATA/Deskbar/deskbar.cmd" report - <<'EOF'
The bar went blank after switching workspaces.
EOF
```

The report window opens already filled in and sending, with the debug dump of that moment, and shows the reference number or the saved copy as it does after Send. There is no contact question, since whoever sent it is not at the window to answer. The report ends with a line saying it came from the manager or from the command line, so whoever reads it knows the words may be a summary.

## Settings

There is nothing to set up. The debug dump box and the contact line are chosen per report, in the window itself. **Report a problem…** is also on the Deskbar Pro tab in Manage.

## Related

- [Updates](updates.md) covers the app's only other network traffic.
- [Manage](manage.md) has the Deskbar Pro tab.
- The README on [Deskbar Pro](../../README.md#deskbar-pro).
