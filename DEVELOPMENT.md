# Following Seas Co-op — development

Technical details and the history of changes. What a player needs is in [`README.md`](README.md).

- Game: Following Seas (Whipstaff Games, Steam app 2895440), Steam build 25792767,
  Unity 6000.3.20f1 on Mono; the game's assemblies target .NET Standard 2.1.
- Mod: a BepInEx 5.4.23 plugin, `com.followingseas.coop`, version 0.1.0, wire protocol 12.
- Transport: LiteNetLib over UDP, port 7777 by default; the same datagrams can travel over Steam
  peer-to-peer (Facepunch.Steamworks) in place of an address.

## Build

```bash
dotnet build FollowingSeasCoop.csproj -c Release -p:GameDir="<game folder with BepInEx>"
```

The plugin is built straight into `<GameDir>\BepInEx\plugins\FollowingSeasCoop\`
(`FollowingSeasCoop.dll`, `LiteNetLib.dll`, `Facepunch.Steamworks.Win64.dll` and `steam_api64.dll`;
the last two are in `libs/`, the game has no Steam library of its own). `GameDir` defaults to the
path in the `.csproj`.
The build fails with a file lock while the game runs from that folder.

A release archive:

The archives of a release:

```powershell
Tools\Make-Release.ps1
# dist\FollowingSeasCoop-<version>.zip               the mod with BepInEx 5.4.23.5: the whole installation
# dist\FollowingSeasCoop-<version>-plugin-only.zip   the mod alone, for a game that has BepInEx
```

It runs the protocol check, builds into a staging folder (the game's plugin folder is not
touched, a running game is no obstacle), and packs the four libraries with `README.md`, `LICENSE`
and `THIRD-PARTY-NOTICES.md` under `BepInEx\plugins\FollowingSeasCoop\`; both archives are
unpacked into the game folder. The complete one adds the BepInEx archive as published (pinned by
version and SHA256, kept in `dist\cache`, downloaded when missing) and `BepInEx\licenses\` from
`Tools/ReleaseNotices`. The version is read from `Plugin.cs` and must be the built library's.

To publish: commit, run the script, tag `v<version>` on that commit, and attach both archives.

There is no automated test of the game logic: it is verified in game. The wire layer has a check
that needs no game, `Tools/ProtocolSmoke`. The debugging tools — the trace, the comparison of two
copies' state, the Dev section, a script that starts a host and a guest side by side — are
described in [`Tools/README.md`](Tools/README.md).

## Model

Host-authoritative co-op. The host owns the world, the save and the ship's physics; guests are
crew in the host's world and have no world or progress of their own. The host trusts its guests:
requests are not checked for eligibility.

Three layers:

- **`Net/`** — transport and session only, no Unity or game reference. `CoopNet` wraps LiteNetLib
  and drives the handshake, the clock and the roster; `Protocol` is the one place packets are
  written and read (`[byte msgType][payload]`); `LanDiscovery` finds games on the local network.
  `SteamLink` is the only door to Steam, and `DatagramTunnel` carries an unchanged LiteNetLib
  session over it through loopback sockets, so nothing above the transport knows the difference.
- **`Sync/`** — one class per subsystem: reads the local game's state, applies the remote one.
- **`Runtime/`** — `CoopBehaviour`, the one MonoBehaviour that owns the loop, every subsystem and
  the Harmony patches; the player's menu (uGUI) and the debug window (IMGUI).

Rules the code keeps:

- **Versioned protocol.** `Protocol.Version` is bumped on any breaking wire change and gates the
  connection. `MsgType` values are only ever appended.
- **Players are a dictionary** by `NetId`; the host allocates ids and is itself 1.
- **Request → host applies → broadcast** for interactions.
- **Snapshots are stamped** with the host's clock and applied through a buffer with interpolation;
  what is read or moved in `FixedUpdate` is stamped on the physics clock.
- **Every Harmony patch body is guarded**; a patch set that fails to install switches its feature
  off, a required one blocks sessions with a readable reason.
- **A game method run for another player has no local side effect**: the acting copy runs the
  game's code, the others are given the result.

Coordinates: the game uses a floating origin. Off the ship a position travels in real space
(scene position plus the origin's offset, as doubles); on the ship it travels in the hull's frame,
so it lines up on every copy whatever that copy's origin is.

## What is implemented, and how

"Played" means run in game by the maintainer; everything else is written and builds, and has not
been run yet.

| Part | How | State |
|---|---|---|
| Session | Handshake with protocol check, roster, clock sync, up to `Network/MaxClients` guests. | played |
| World | The host captures the game's save object with no file, sends it gzip-packed in chunks; the guest gives it to the game's loader. The host is paused while a guest loads. A guest never writes a save. | played |
| Players | Pose 20 times a second, hull-local aboard, real space elsewhere; avatars are the game's crew models. | played |
| Ship | The host sends the hull's pose and velocities; a guest's hull is kinematic and follows the buffered poses. | played |
| Sea and sky | Weather type, wind, wave spectrum, ocean clock, date and time, four times a second. | played |
| Controls | Wheel, reefs, rig ropes, winches, doors, lanterns, ballast, kedge anchor, mooring lines, pump — as state, not input. One hand at a time on what is held for a while. | played (the general case) |
| Things | Barrels and sacks: a registry keyed by scene path; one mover at a time sends poses, the others draw it from a buffer; stand-ins where a copy lacks the object. | the registry played, carrying not |
| Tools in hand | The active inventory slot and hammer blows; shown as a renderer-only copy of the local tool. | not played |
| Household | Money, stores, debts, journal: polled, sent as numbered differences, the host's numbers win. | not played |
| Contracts | Travel as the game's own save strings; the reward is counted on the delivering copy only. | not played |
| Cargo | A crate is named by its box and its half-metre cell; one player at a time at the panel. | not played |
| Leaks, trolling lines, fish hold, catch records | More control kinds and one more store; only the host rolls for leaks and bites. | not played |
| Hunting | A shot animal and its harvest are told to all; living animals are each copy's own. | not played |
| Rowboat | Launching is a control; in the water it is moved by the copy of the player in it. | not played |
| Crew | A guest's orders are carried out by the host's crew; the looks and the roster are sent to guests. | not played |
| Player menu | A Co-op button and page made of copies of the game's own buttons; LAN list of hosted games. | played |
| Steam | A host is also reachable by its Steam id: Steam P2P datagrams tunnelled to LiteNetLib through loopback. Rich presence lists hosting friends in the join dialog and gives Steam's own Join Game and invites. Friends-only admission. | not played |
| Rescue | A drowned player is put back on deck before the game's death dialog. | not played |

Not shared: living animals, a fish in another player's hand, another player's shot, the view
through another player's spyglass, another player's rowing animation. A guest cannot ask for the
ship's Reset mode. Known and left alone: the gust phase steps on a guest when frame rates differ.

More detail: the game's item mechanics, as read from the decompile before the item stages were
written, are in [`ITEMS_TECHNICAL.md`](ITEMS_TECHNICAL.md) (Russian).

## History of changes

Version 0.1.0 is the first release; the plugin version did not change while it was written, the
wire protocol did.

### 0.1.0

| Date | Protocol | Change |
|---|---|---|
| 2026-10-08 | 1 | Foundation: session, joining the host's world, player avatars. |
| 2026-10-08 | 2 | The ship, the sea, the weather and the time of day. |
| 2026-10-08 | 4 | The ship's controls; fixes of defects that ended a session. |
| 2026-10-08 | 4 | Item catalog and registry: identity of things, the late-join manifest. |
| 2026-10-11 | 4 | The registry hardened: a guarded tick, manifest in parts. |
| 2026-10-11 | 6 | Carrying, dropping and throwing things. |
| 2026-10-11 | 7 | The tool another player has out. |
| 2026-10-11 | 8 | The household and contracts. |
| 2026-10-11 | 9 | The ship's cargo. |
| 2026-10-11 | 9 | Things carried off before the session; stale movers settled. |
| 2026-10-11 | 10 | Leaks and the hammer, trolling lines, the fish hold, catch records, hunting. |
| 2026-10-11 | 10 | Debugging tools: protocol check, trace, state dump, Dev section, pair launcher. |
| 2026-10-11 | 10 | The in-game Co-op menu; F8 becomes the debug window. |
| 2026-10-11 | 10 | Hosted games of the local network are listed in the join dialog. |
| 2026-10-11 | 11 | The rowboat, crew orders and the crew's looks, the trolling reel, mooring line length. |
| 2026-10-11 | 12 | The crew roster; the pumps the crew works. |
| 2026-10-11 | 12 | `Dev/Window` keeps test copies windowed. |
| 2026-10-11 | 12 | Dev buttons: a contract, a crate, weather, time, teleports, mending a leak. |
| 2026-10-11 | 12 | Dev button *Make a thing*: a thing made while the game runs is shared. |
| 2026-10-11 | 12 | Release 0.1.0 prepared: player's README, this file, the release script. |
| 2026-10-11 | 12 | Hosting and joining through Steam, in the Co-op page: friends list, Steam ID, friends only. |
| 2026-10-11 | 12 | Two release archives: complete with BepInEx 5.4.23.5, and the plugin alone. |
