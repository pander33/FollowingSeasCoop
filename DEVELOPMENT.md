# Following Seas Co-op — development

Technical details and the history of changes. What a player needs is in [`README.md`](README.md).

- Game: Following Seas (Whipstaff Games, Steam app 2895440), Steam build 25792767,
  Unity 6000.3.20f1 on Mono; the game's assemblies target .NET Standard 2.1.
- Mod: a BepInEx 5.4.23 plugin, `com.followingseas.coop`, version 0.1.0; released with wire protocol
  12, current development tree uses protocol 21.
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

### Hand-grip review fixes (2026-10-18)

Hammer and lantern have dedicated closed Handle grips: the hammer palm faces sideways and
the lantern palm faces down in the tool frame. Other one-hand tool grips are unchanged.
Source review and Release build pass; these orientations still need an in-game visual check.

Protocol 21 is unchanged. Finger restoration leaves newer animation untouched; finger closure
has its own smoothed contact weight. Wheel targets and palm frames share the collider-local
plane; spheres and capsules use radial normals. Non-eye tool correction ends with a primary-hand
projection if two grips conflict. Release: 0 warnings / 0 errors; ProtocolSmoke: 228 checks passed.
Visual validation on all six crew models and profiling of humanoid pose conversion remain open.

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
| Players | Pose 20 times a second, hull-local aboard, real space elsewhere; avatars are the game's crew models, animated from how they move, bent low in a crouch, holding what the player holds. |
| Ship | The host sends the hull's pose and velocities; a guest's hull is kinematic and follows the buffered poses. A guest's fast travel by the chart is sent to the host, which moves its hull there; after a jump that long every copy moves its origin and reloads the islands. |
| Sea and sky | Weather type, wind, wave spectrum, ocean clock, date and time, four times a second. |
| Controls | Wheel, reefs, rig ropes, winches, doors, lanterns, ballast, kedge anchor, mooring lines, pump — as state, not input. One hand at a time on what is held for a while. |
| Things | Barrels and sacks: a registry keyed by scene path; one mover at a time sends poses, the others draw it from a buffer; stand-ins where a copy lacks the object. |
| Tools in hand | Active inventory slot, raised pose, hammer blows and gunshots; a mesh copy without first-person scripts or arms, retaining lantern lights and flame, and gun sounds and particles. Quadrant and chronometer readings are reproduced locally. |
| Hands on controls | IK targets and palm rotations sent with the player pose, with procedural humanoid finger grips. No dedicated operating cycles. |
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

Not shared: living animals, a fish in another player's hand, the view
through another player's spyglass, another player's rowing animation. A guest cannot ask for the
ship's Reset mode. Known and left alone: the gust phase steps on a guest when frame rates differ.

More detail: the game's item mechanics, as read from the decompile before the item stages were
written, are in [`ITEMS_TECHNICAL.md`](ITEMS_TECHNICAL.md) (Russian).

### Player animation status (development tree, protocol 21)

- Implemented: directional walking and running, procedural crouching and crouch-walking, ladder
  climbing, pitch shared between the chest, neck and head, and two-bone arm IK for carried things,
  tools and controls. A hammer blow is a tool swing, not a dedicated full-body action.
- Tools: lowered/raised spyglass and quadrant, quadrant needle, chronometer hands, gun idle/aim
  poses and timed shot sounds, particles and recoil. The spyglass is moved to the eye, not extended.
- Gun placement currently targets 0.30 m below the avatar's eye at idle and 0.14 m below when aiming.
  Grips are separated sideways, the left farther forward; both were then raised 0.04 m. These are
  placement targets before the arm-reach correction, not measured final world-space heights.
- Palms and fingers: mirrored bone-based palm calibration and humanoid muscle profiles for handles,
  cylinders, support, side grips and a separate trigger index finger. Protocol 21 transmits control
  palm rotations. Finger conversion snapshots and restores the skeleton, applying only complete
  finger chains. Missing bones/muscles are reported once per avatar. Previous finger changes are
  restored from the unconditional update, independent of participation. Control palm frames use
  collider faces and wheel-plane/rim tangents rather than object pivots. Unreachable non-eye tools
  are moved towards the arms' reachable space; finger closure fades with palm/contact distance.
  These grips still require visual acceptance on all crew models.
- Missing: a swimming animation, avatar rowing/seated-at-oars animation, gun reloading,
  wheel regrips and dedicated winch, capstan, pump or rope operating cycles. Control IK
  uses collider geometry, not authored grip points on each moving mechanism.
- Cargo crates are placed through the cargo panel/grid, not carried as inventory things; a crate
  carrying animation is not an outstanding requirement of the current game interaction.
- Verification: the maintainer checked directional walking/running, crouching and tool appearance.
  The journal also records limited spyglass/quadrant and clock/shot-counter observations. Latest
  gun-height and hand adjustments, ladder, look pitch, carried-item grips and control grips still
  need visual acceptance; shot smoke and sound were not confirmed. Implementation is not acceptance.

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
| 2026-10-18 | 17 | The fish sale dialog keeps fish another player put in or sold while it was open. |
| 2026-10-18 | 18 | A fish taken off a hook by two players at once goes to one of them; two players mending one leak at once both count; changes of a contract end the same on every copy. |
| 2026-10-18 | 19 | Another player's body: walking and running in every direction, crouching, climbing a ladder, the head following the look; a tool or a carried thing in its hands. |
| 2026-10-18 | 20 | Raised tool poses, quadrant and chronometer readings, remote gunshot sounds/particles/recoil, and transmitted hand targets for ship controls. Adjusted gun height and separated/raised its hand grips; final visual acceptance remains open. |
| 2026-10-18 | 21 | Procedural palm orientation and finger grips for tools, carried things and controls; control snapshots include palm rotations. Visual acceptance remains open. |
