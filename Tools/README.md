# Tools

What is used to find out why two copies of the game disagree. None of it is needed to play.

| Tool | Needs the game | What it answers |
|---|---|---|
| `ProtocolSmoke` | no | Is every message read back the way it was written? |
| Problems line (in the plugin) | yes | Did anything go wrong just now? |
| Trace (in the plugin) + `Merge-Trace.ps1` | yes | Where did it get lost: not sent, not received, not applied? |
| Dump state (in the plugin) + `Compare-Copies.ps1` | yes | Do the copies hold the same world? |
| Dev section of the menu (in the plugin) | yes | How do I get a leak, a fish, a dead animal without sailing for it? |
| `Dev/LagMs` (in the plugin) | yes | Does it still work when messages take time to arrive? |
| `Start-Pair.ps1` | yes | Two copies, a host and a guest, with one command. |
| `DevConsole` | yes | Anything else: runs C# inside the game. Its own `README.md`. |

## ProtocolSmoke

```bash
dotnet run --project Tools/ProtocolSmoke/ProtocolSmoke.csproj -c Release        # add -- -v to see every check
```

Compiles the mod's own `Net/` sources and checks, for every `MsgType`:

- **there and back** — a message with every field filled is written, read, and compared field by
  field; nothing may be left unread;
- **as made** — the same for a message nobody filled;
- **cut packets** — the packet cut at every length is dropped, both as a short datagram and as a
  buffer that still holds the bytes of an older packet;
- **long strings**, **long lists** — what is longer than the limit arrives cut, not lost and not
  as a packet the other side cannot read;
- **noise** — 3000 random packets are dropped without an exception;

and once: every type has a case in `Protocol.Create` and a side in `MessageDirection`, and no wire
number of `MsgType` or `ControlKind` has changed. Messages are filled through reflection, so a new
message or field is covered without touching the tool. **A new `MsgType` or `ControlKind` must be
added to the tables at the top of `Program.cs`** — the check fails until it is, which is the point:
the table is the record of what was released.

Exit code 1 when anything failed. Run it after every change in `Net/`.

## What the plugin itself has

Settings are in the `Dev` section of `BepInEx/config/com.followingseas.coop.cfg`; any setting can
also be given to one copy on its command line as `--coop:Section.Key=value` (two copies started
from one folder read the same file). A setting given that way is never written to the file.

| Setting | What it does |
|---|---|
| `Dev/Tools` | Shows the **Dev** section in the menu's Debug panel. |
| `Dev/Trace` | Writes `BepInEx/coop-trace-<who>.log` during a session. |
| `Dev/DumpEverySeconds` | Writes `BepInEx/coop-state-<who>.txt` this often, at the same moment of the host's clock on every copy. |
| `Dev/LagMs`, `Dev/JitterMs` | Holds every arriving message this long before acting on it. |
| `Dev/AutoHost`, `Dev/AutoJoin` | Hosts once a world is loaded; joins `Network/JoinIp` from the title screen until the host answers. |
| `Dev/RunInBackground` | Keeps the game running while its window is not the active one. |
| `Dev/SaveDir` | This copy's saves and settings go to another folder. |

`<who>` is `host`, `guest2` (the NetId), or `solo` outside a session.

**Problems.** Every warning and error the mod writes to the BepInEx log is counted, wherever it is
written, and so is an exception of the game that passed through a patch of the mod. While the
count is not zero a red line at the bottom of the screen says so; the Debug panel shows the last
five and has a Reset button. Exceptions of the game with no frame of the mod in them are counted
apart ("game exceptions").

**Trace.** The Debug panel's Trace section shows the last notes of a subsystem: each message as it
was sent and as it arrived (`sent ControlState`, `got ItemAction`), and what became of it where
that is not obvious (`waits` — the control is in the local hand, `applied` — after such a wait,
`not yet`, `refused`, `hand opened`, `not done`). A state applied the moment it came has no line
of its own: `got` says it. Messages sent many times a second (poses, the sea, the wheel while it
turns) are only counted, and in the panel a note that repeats (a rope being hauled) is one line
with a count. With `Dev/Trace` everything goes to a file, one line a note; the file is started
anew with every session.

**Dump state.** One line for every shared thing as this copy's game holds it right now: each
control, each touched thing and where it rests, money, stores, debts, fish, records, contracts,
crates, animals.

**Dev section.** Each button does what the game itself would have done on this copy and never
calls the synchronisation — the synchronisation is what is being tested:

- *Open a leak*, *Widen leaks* (host only: a guest's leaks do not widen by themselves);
- *Fish on a hook*, *Fish into the hold*;
- *Go to an animal*, *Kill it* (the nearest living one that is loaded);
- *Money +100*, *Oakum and food +5*;
- *Go to <player>*;
- *Send everything again* (host) / *Ask the host for everything again* (guest): the household,
  the contracts, the hold, the animals and every control — not the things;
- *Lag ms*: the delay, changed while the session runs. It is written to the config file like any
  setting changed in the menu (unless the copy was started with `--coop:` settings), so the status
  line shows `LAG` while it is not zero.

*Go to ...* moves the player the way the game's ladders do. Leaving the deck this way while the
game still counts the player as aboard has not been tried in game.

## Merge-Trace.ps1

```powershell
Tools\Merge-Trace.ps1                                 # every copy's trace in one order
Tools\Merge-Trace.ps1 -Area Controls -Key 'Leak#2'    # one control
Tools\Merge-Trace.ps1 -Problems                       # only what went wrong
Tools\Merge-Trace.ps1 -LastSeconds 30 -Out trace.txt
```

A guest only estimates the host's clock, so two lines a few milliseconds apart may stand in the
wrong order.

## Compare-Copies.ps1

```powershell
Tools\Compare-Copies.ps1               # the host's dump against the first guest's
Tools\Compare-Copies.ps1 -Section Items
Tools\Compare-Copies.ps1 -Watch        # again after every dump (with Dev/DumpEverySeconds)
```

Prints only what differs. Numbers may differ by the line's own tolerance. Exit code 0 when the
copies agree. Dumps made by hand a few seconds apart differ in whatever was moving meanwhile; the
script says how far apart the two files are.

## Start-Pair.ps1

```powershell
Tools\Start-Pair.ps1                                         # host left, guest right, Dev section on
Tools\Start-Pair.ps1 -Trace -DumpEverySeconds 5 -LagMs 80    # everything on
Tools\Start-Pair.ps1 -NoAuto -NoTools                        # as a player would start them
```

Load your world in the left window: it hosts by itself five seconds later, and the right window
joins by itself. The game remembers the window size it was started with: after this script it
starts windowed at that size until you change it in its settings.
The guest keeps its saves in `<GameDir>\_guest-saves`. `-LagMs` is per copy, so a message one way
takes that long and an answer twice that.

## A session with everything on

1. `dotnet run --project Tools/ProtocolSmoke/ProtocolSmoke.csproj -c Release`
2. `Tools\Start-Pair.ps1 -Trace -DumpEverySeconds 5`
3. In a third window: `Tools\Compare-Copies.ps1 -Watch`
4. Do the thing under test. A red line on either copy, or a red line in the comparison, is a
   finding; `Tools\Merge-Trace.ps1 -Area <subsystem>` shows what happened around it.
