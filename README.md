# Following Seas Co-op

A co-op mod for [Following Seas](https://store.steampowered.com/app/2895440/). Early development
(v0.1.0, protocol 2): a guest can join the host's world, the players see each other, and the ship, the
sea, the weather and the time of day are the host's. **Only the host can sail the ship: the controls
are not shared yet.**

Unofficial; not made or endorsed by Whipstaff Games.

## What is implemented

Written and built; only partly tried in game so far.

- Host a loaded world, join it over LAN or a direct IP (UDP port 7777).
- The guest receives the host's world and appears aboard the host's ship. The guest's own saves
  are never written while it is in the host's world.
- Players see each other on deck and ashore.
- The guest's ship follows the host's; waves, wind, weather and time of day match.

## Install

1. Install [BepInEx 5.4.23 (x64)](https://github.com/BepInEx/BepInEx/releases) into the game folder
   and start the game once.
2. Put `FollowingSeasCoop.dll` and `LiteNetLib.dll` into `BepInEx/plugins/FollowingSeasCoop/`.

Everyone needs the same version of the mod and of the game.

## Play

- **Host:** load your world, press **F8**, choose **Host this world**.
- **Guest:** stay on the title screen, press **F8**, enter the host's IP, choose **Join**.
- **Disconnect** in the same menu returns a guest to the title screen.

The host's game pauses while a guest loads.

## Build

```bash
dotnet build FollowingSeasCoop.csproj -c Release -p:GameDir="<game folder with BepInEx>"
```
