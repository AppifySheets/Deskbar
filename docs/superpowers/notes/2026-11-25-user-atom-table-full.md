# The USER atom table filled up, and it looked like desktop heap (2026-11-25)

Deskbar task 4d419051. From about 12:20 every `scripts/test-hidden.ps1` run failed: a few tests passed, then the
rest threw `Win32Exception (8): Not enough memory resources are available to process this command` from
`MS.Win32.HwndWrapper..ctor`, at 8 MB, 32 MB, any heap. One test alone failed too. Two testhosts died on the same
exception at 12:41 and 12:42, and Snipping Tool crashed twice at 12:50, so it was not only the tests.

## What it was not (each measured)

- **The hidden desktop's heap.** A thread bound to a fresh 8 MB hidden desktop created 9,995 plain windows before
  the per-process handle quota (error 1158) stopped it. A process started on such a desktop, the way the test run
  starts, made 9,996.
- **Memory, the window count, USER or GDI objects.** Commit 69 of 206 GB, 30 GB available; about 3,200 USER and
  2,900 GDI objects in the whole session, steady.
- **Desktop heap in general.** Plain `CreateWindowEx` never failed, about 8,000 calls, in the same seconds where
  class registration failed up to 15% of the time.

## What it was

`RegisterClassEx` itself, and only for string class names:

- 1.3 million register-and-unregister cycles under an INTEGER class name (`MAKEINTATOM`, which never touches the
  atom table) failed 0 times. The same under a string name failed in bursts.
- Holding classes until the table refused, and then walking every slot 0xC000 to 0xFFFF with
  `GetClipboardFormatName`: 175 to 351 slots were mine, about 843 had someone else's name, and about 15,200 answered
  `ERROR_INVALID_HANDLE`. The table has 16,384 slots, so roughly 15,000 were taken by atoms with no name.

Window class names, registered window messages and clipboard format names share this one table per session
([Raymond Chen, 2017](https://devblogs.microsoft.com/oldnewthing/20171006-00/?p=97147)), on every desktop and window
station of the session. A hidden test desktop does not escape it. WPF makes a class with a unique name for every
`Dispatcher` (`HwndWrapper[app;;{guid}]`), so every WPF window anywhere in the session needs a free slot.

## How the slots leak

A small child process registered 60 classes with unique names, made one window of each and exited without cleaning
up (as a test host does). Then the table was filled to see whether the child's 60 slots were handed out again:

| Child ran on | Slots back after it exited |
| --- | --- |
| a hidden desktop, cleaning up (control) | 56 of 60 |
| a hidden desktop, not cleaning up | 60 of 60 |
| the visible desktop, not cleaning up | 0 of 60, and 0 of 20 twice more |
| the visible desktop, classes but no windows | 60 of 60 |

The leaked atoms have no name afterwards and their windows are gone (`IsWindow` false). The session's USER handle
table, read through `user32!gSharedInfo`, held one destroyed window in all, so the windows are not pinned as
zombies. What holds the slot is the class atom. That only happens when the class had a window on the visible
desktop, so something there takes hold of the class of a window it sees, and the slot outlives the process.

A hidden desktop with an out-of-context WinEvent hook that only pumped messages leaked nothing (20 of 20 back, three
times). With `GetClassName` called in the hook's callback it leaked 20, 14 and 20 of 20. Calling `GetClassName`
only on create or only on destroy events gave mixed results. This was measured while the table was already full,
where other programs take freed slots between the child exiting and the check, so treat it as a lead, not a finding.

Not found yet: which program on the visible desktop does it. Deskbar restarted at 13:05 and the leak went on, so
its own hook thread is not what holds the slots.

## Recovering, and finding the culprit

Nothing frees the table but ending the session: sign out or reboot. To find the culprit, start from a fresh session:

1. `pwsh scripts/user-atoms.ps1` right after signing in, and again every hour. Headroom going down while the named
   count stays flat is the leak.
2. Close candidates one at a time (the session had 231 WinEvent hooks: PowerToys, RustDesk, Teams, Beeper, Docker
   Desktop, Everything, the NVIDIA container, Windows Terminal, Deskbar) and repeat the leak test above. The first
   one whose closing stops the leak is the one.

`scripts/test-hidden.ps1` now runs `scripts/user-atoms.ps1` first and stops with this explanation instead of
failing a few hundred tests.
