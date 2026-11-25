# Window class names ran out on some processors, and it looked like memory (2026-11-25)

Deskbar tasks 4d419051 (first reading) and c5e9b17f (this one, which corrects it). On 2026-11-25 everything that
made a WPF window failed at random with `Win32Exception (8): Not enough memory resources are available to process
this command` from `MS.Win32.HwndWrapper..ctor`: test runs from about 12:20, Snipping Tool at 12:50, and Deskbar
itself, which could not start nine times between 13:11 and 13:21.

## The short version

- The failing call is `RegisterClassEx`. WPF registers a window class with a name of its own for every window, and
  each name needs a slot in the session's table of class names, window messages and clipboard formats (the "USER
  atom table"): 16,320 slots for every program on every desktop of the session.
- The table was **not full and nothing leaked**. About 840 names were in use. Windows keeps the free slots in one
  list per processor, and thirteen of this machine's 32 processors could register nothing while four could
  register 300 or more. A thread fails or succeeds by the processor it happens to run on.
- That state begins the one time the table really does fill. What filled it: the window tests. A test host kept
  every class of every test until it exited, 4,014 names for one run of `Deskbar.Windows.Tests`, so four runs at
  once fill the table, and several sessions run that suite at the same time.
- It can be repaired without signing out: a slot goes back to the list of the processor that releases it, so slots
  taken on a processor with plenty and released on one with none make that one work again.

What was done: `tests/Deskbar.Windows.Tests/StaThread.cs` shuts each test's dispatcher down, so a run no longer
hoards names; Deskbar balances the slots before its first window, every 30 seconds and after any refused window
(`src/Deskbar.Windows/Recovery/ClassNameSlots.cs`); `scripts/user-atoms.ps1` measures per processor and repairs
with `-Repair`, and `scripts/test-hidden.ps1` runs it first.

## What was measured

All on Petre's machine (i9-14900K, 32 logical processors, Windows 10.0.26100), same boot, no sign-out in between.

| Measurement | Result |
| --- | --- |
| A thread pinned to each processor in turn registers up to 300 uniquely named classes, three rounds | 300 on processors 0, 3, 10, 27; 0 on 4-7, 15, 16, 20-23, 25, 29, 31; between 1 and 294 on the rest. Identical in every round. |
| The first few slot numbers each processor hands out, taken and put back in reverse, twice | The same numbers in the same order per processor: a last-in-first-out list each. |
| 40 classes registered on processor 0, unregistered on processor 4 | Processor 4 went from 0 to 40. |
| 3,000 more attempts on a processor at 0 | 0 succeeded. |
| Every slot sorted into named, free on some processor's list (each list drained and restored), or neither | 843 named, 5,860 free and reachable, 9,681 neither, spread evenly over all 64 pages. |
| Kernel pool tag `AtmA` (one allocation per atom, all atom tables of the machine) | 1,646 live. So the 9,681 are not in use by anyone. |
| A child registers 20 classes, makes a window of each (top-level, message-only, or none) and exits without cleaning up | All 20 slots free again every time, on whichever processor ran the exit. |
| Names held by a test host over one run of `Deskbar.Windows.Tests`, 1,609 tests, before the fix | Rose steadily to 4,014 and fell to 0 when the host exited. |
| The same run after the fix (1,620 tests, all passed), sampled every quarter second | At most 9. |
| The same run before the fix on the broken session, not pinned | 866 of 1,609 failed once the host had taken 1,164 names. |
| Repair: every processor under 64 topped up to 128 | 20 processors short, 2,368 slots moved in 66 ms, all 32 able to register again. |

## Why Windows behaves like this

Read from `ntoskrnl.exe` 10.0.26100.9457 with public symbols (`RtlpInsertStringAtom`, `ExCreateHandleEx`,
`ExDestroyHandle`, `ExpAllocateHandleTableEntrySlow`, `ExpInsertLowLevelTableIntoFreeList`):

- The atom table sits on a kernel handle table. A name's number is 0xC000 plus its handle's index, so only
  indexes below 0x4000 can be names: 64 pages of 255 entries.
- The handle table keeps free entries in one last-in-first-out list per processor. `ExCreateHandleEx` takes from
  the calling processor's list, and goes on to the next processor's only when a list is empty. `ExDestroyHandle`
  puts an entry on top of the list of the processor that frees it.
- Only when every list is empty does the table grow, by a page appended to the caller's list.
- `RtlpInsertStringAtom` takes a handle, and if its index is 0x4000 or more it destroys the handle again and
  fails with "no memory".

So the first time 16,320 names are in use at once, the table grows a 65th page, and its 255 entries can never be
names. They stay in the free lists for the rest of the session. A processor with one of them on top of its list
takes it, is refused and puts it back, every time, and because its list is not empty it never looks at the other
lists. Whatever lies under such an entry in a list cannot be reached (the 9,681). A failed attempt that is
preempted, or moved to another processor, between taking the entry and putting it back drops it on top of usable
slots, which is how more of them get buried the longer the session fails.

Nothing in user mode can remove those entries. What can be done is to keep usable slots on top of every list,
which is all a thread ever sees.

## What the first reading got wrong

The first note said the table was full of leaked class atoms with no name, leaked whenever a process exited with
windows of its own classes on the visible desktop, and that only signing out empties it. Each step was a
measurement from one thread that did not know the lists are per processor:

- "15,200 slots answer invalid-handle while registration is refused" was read as 15,200 atoms without a name.
  They were free slots the measuring thread's processor could not reach.
- "A child's slots are never handed out again" was a check from one processor for slots that had been released on
  another. With all 32 lists checked, every slot comes back.
- "Headroom: 200 of 200" one second and "refused" the next was the scheduler moving the probe.

The old `scripts/user-atoms.ps1` therefore reported FULL at random and stopped test runs that would have passed.

## Using the script

```
pwsh scripts/user-atoms.ps1            # names in use, what each processor can register, a verdict
pwsh scripts/user-atoms.ps1 -Repair    # tops up the processors that are short
pwsh scripts/user-atoms.ps1 -Top 20    # who holds the names, by shape
```

`SHORT` means some processors cannot register 64 classes; `-Repair` fixes that in well under a second. `FULL`
means the table is nearly full for real: find the program holding thousands of names and end it before the table
fills, because that is the event that breaks the session. A running Deskbar does the repair by itself.
