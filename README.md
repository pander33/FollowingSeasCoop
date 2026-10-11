# Following Seas Co-op

A co-op mod for [Following Seas](https://store.steampowered.com/app/2895440/). Early development
(v0.1.0, protocol 10): a guest can join the host's world, the players see each other, the ship, the
sea, the weather and the time of day are the host's, and the ship's controls are
shared. **Leaks, the rowboat and crew orders are not shared yet.**

Unofficial; not made or endorsed by Whipstaff Games.

## What is implemented

Written and built; only partly tried in game so far.

- Host a loaded world, join it over LAN or a direct IP (UDP port 7777).
- The guest receives the host's world and appears aboard the host's ship. The guest's own saves
  are never written while it is in the host's world.
- Players see each other on deck and ashore.
- The guest's ship follows the host's; waves, wind, weather and time of day match.
- Any player can steer, reef, work the sheets and braces, lay the kedge anchor, moor, pump, and use
  winches, doors, lanterns and ballast.
  One player at a time works a control; a second player sees who has it.
- A drowned player is put back aboard instead of being offered a save to load, and the co-op menu
  has **Return to the ship** for a player left behind in the water.
- While the host has the game paused, guests cannot move.
- One household for the crew: money, the ship's provisions, debts, the journal and accepted
  contracts are shared; a delivery by any player pays the common purse once. Built, not tried in
  game yet.
- The tool a player has out (spyglass, compass, hammer, lantern and the rest) is seen by the
  others. Built, not tried in game yet.
- Barrels and sacks can be carried, put down and thrown by any player and are seen by the others;
  one player at a time holds a thing. A thing put into the ship's hold is added to the shared
  provisions. Built, not tried in game yet.
- Leaks open and grow the same for everybody and any player can caulk them; the trolling lines'
  weights and the fish on their hooks, the fish hold and the catch records are shared; a shot
  animal lies dead for everybody and its yield can be carried by anyone. Built, not tried in game
  yet.
- Cargo crates are shared: any player can load, move, unload and deliver them, one player at a
  time at the cargo panel; a second player sees who is loading. Built, not tried in game yet.

## Install

1. Install [BepInEx 5.4.23 (x64)](https://github.com/BepInEx/BepInEx/releases) into the game folder
   and start the game once.
2. Put `FollowingSeasCoop.dll` and `LiteNetLib.dll` into `BepInEx/plugins/FollowingSeasCoop/`.

Everyone needs the same version of the mod and of the game.

## Play

- **Host:** load your world, press **Esc**, choose **Co-op**, then **Host this world**. The page
  then shows the address to give your friends (with **Copy**) and who is aboard. Your name, the
  number of guests and the UDP port are set on the same page before hosting.
- **Guest:** choose **Co-op** on the title screen, type the host's address, choose **Join**. An
  address may carry a port (`192.168.0.40:7777`); without one the configured port is used.
- On the same network the address shown to the host is enough. Over the internet the guest needs
  the host's public address, and the host's router must forward the UDP port to the host's PC.
- **Leave the crew** (Esc, Co-op) returns a guest to the title screen; **Stop hosting** keeps the
  host's world loaded. Both ask once before they do it.
- A guest's game does not pause under its Esc menu: the world is the host's.
- **F8** opens the debug window (diagnostics, state dumps, trace, development tools).

The host's game pauses while a guest loads.

## Build

```bash
dotnet build FollowingSeasCoop.csproj -c Release -p:GameDir="<game folder with BepInEx>"
```

Debugging tools (a protocol check that needs no game, a trace, a comparison of two copies, a
script that starts a host and a guest side by side) are described in [`Tools/README.md`](Tools/README.md).
