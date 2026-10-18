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

### Releasing a version

Releases are published at <https://github.com/pander33/FollowingSeasCoop/releases>.

1. Build (0 errors, 0 warnings) and run `Tools/ProtocolSmoke`.
2. Choose the number, `Major.Minor.Patch`. The handshake compares the wire protocol, not the mod
   version: the third digit is for fixes with the protocol unchanged, the second for features or
   a new protocol, the first stays 0 while the mod is an early version.
3. Write the number into `Plugin.cs`, `<Version>` of the `.csproj`, `README.md` (the badge and the
   archive names) and this file (the line at the top, a new section in the history of changes).
4. `Tools\Make-Release.ps1`, and read the list of files it prints for each archive.
5. Commit and push; if anything that is packed changed after step 4, run the script again. Tag
   that commit `vX.Y.Z` and push the tag. A published tag is never moved and a published library
   is never replaced: a mistake in the code is the next patch version.
6. Release notes, in English: whether everyone must update (always when the protocol changed),
   what is new, what changed and what was fixed, the two archives and for whom
   each is, the game build, the SHA256 of both archives.
7. `gh release create vX.Y.Z dist/FollowingSeasCoop-X.Y.Z.zip dist/FollowingSeasCoop-X.Y.Z-plugin-only.zip --title "Following Seas Co-op X.Y.Z" --notes-file <notes> --verify-tag`.
   Not a pre-release: the README links to `releases/latest`. Download both assets afterwards and
   compare their SHA256 with the notes.

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

| Part | How |
|---|---|
| Session | Handshake with protocol check, roster, clock sync, up to `Network/MaxClients` guests. |
| World | The host captures the game's save object with no file, sends it gzip-packed in chunks; the guest gives it to the game's loader. The host is paused while a guest loads. A guest never writes a save. |
| Players | Pose 20 times a second, hull-local aboard, real space elsewhere; avatars are the game's crew models. |
| Ship | The host sends the hull's pose and velocities; a guest's hull is kinematic and follows the buffered poses. A guest's fast travel by the chart is sent to the host, which moves its hull there; after a jump that long every copy moves its origin and reloads the islands. |
| Sea and sky | Weather type, wind, wave spectrum, ocean clock, date and time, four times a second. |
| Controls | Wheel, reefs, rig ropes, winches, doors, lanterns, ballast, kedge anchor, mooring lines, pump — as state, not input. One hand at a time on what is held for a while. |
| Things | Barrels and sacks: a registry keyed by scene path; one mover at a time sends poses, the others draw it from a buffer; stand-ins where a copy lacks the object. |
| Tools in hand | The active inventory slot and hammer blows; shown as a renderer-only copy of the local tool, the lantern with its light and flame. |
| Household | Money, stores aboard and ashore, debts, the journal's contract and world pages (not its events): polled, sent as numbered differences (debts per creditor), the host's numbers win. A trade or transfer dialog applies what the player chose to the stores as they are when it is confirmed, not as they were when it opened. |
| Chart | Courses and markers travel as the game's own lines, which went and which came; a closed chart rebuilds itself from them when it next opens. |
| Contracts | Travel as the game's own save strings; the reward is counted on the delivering copy only. |
| Cargo | A crate is named by its box and its half-metre cell; one player at a time at the panel. |
| Leaks, trolling lines, fish hold, catch records | More control kinds and one more store; only the host rolls for leaks and bites. |
| Hunting | A shot animal and its harvest are told to all; living animals are each copy's own. |
| Rowboat | Launching is a control; in the water it is moved by the copy of the player in it. |
| Shipyard | The ship plans and what was paid or delivered for them are polled and sent line by line; the host adds a guest's change to its own figures. A payment counts from the purse as it is when it is made. A launch runs the game's own code on the launching copy; the others load the same ship scene and put the hull at the same point, a guest's launch through the host. |
| Crew | A guest's orders are carried out by the host's crew; the looks and the roster are sent to guests. |
| Player menu | A Co-op button and page made of copies of the game's own buttons; LAN list of hosted games. |
| Steam | A host is also reachable by its Steam id: Steam P2P datagrams tunnelled to LiteNetLib through loopback. Rich presence lists hosting friends in the join dialog and gives Steam's own Join Game and invites. Friends-only admission. |
| Rescue | A drowned player is put back on deck before the game's death dialog. |
| Other mods | `Runtime/ModChannel`: a line of text under a channel name, host to guests or guest to host; another mod reaches it by reflection (`Register`, `Send`, `Role`; `RegisterPeer` with the sender's NetId, `Members`, `SessionIdentity`; `CatchUnits`/`ReceiveCatchUnits`, a number a mod gives a trolled fish, carried with the hook's state). |

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
| 2026-10-12 | 12 | Dev buttons: water in the hull, harvesting, delivering and abandoning a contract, a hand aboard or ashore, a guest's world loaded again. |
| 2026-10-15 | 13 | A channel for other mods (`ModChannel`, message `ModData`): Following Seas Omens shows the host's night events to its guests. |
| 2026-10-17 | 13 | `ModChannel` for Omens: addressed channels (`RegisterPeer`, `Members`, `SessionIdentity`) and a number carried with a trolled fish. |
| 2026-10-17 | 14 | The lantern in another player's hand gives light; a guest's fast travel by the chart moves the host's ship. |
| 2026-10-17 | 15 | The shipyard: shared ship plans and payments; a ship launched by anyone is everybody's. The hull's pose names its ship. |
| 2026-10-17 | 16 | Audit fixes: the store ashore, debts as differences, trade and payment dialogs opened before another player's change, shipyard payments made at once, the chart's courses and markers, a barrel bought by two hands at once, late packets of players, the rowboat and things, a refused hand on a control. |
| 2026-10-18 | 17 | The journal's world pages (notes read, places found) are shared; a guest whose world was loaded again gets the journal lines written since it joined. |
