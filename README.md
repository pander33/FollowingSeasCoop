# ⛵ Following Seas Co-op

[![Version](https://img.shields.io/badge/version-0.1.0-blue)](DEVELOPMENT.md#history-of-changes)
[![Game](https://img.shields.io/badge/game-Following%20Seas-1b2838?logo=steam&logoColor=white)](https://store.steampowered.com/app/2895440/)
[![BepInEx](https://img.shields.io/badge/BepInEx-5.4.23-orange)](https://github.com/BepInEx/BepInEx/releases)
[![Players](https://img.shields.io/badge/players-2--16-brightgreen)](#-play)
[![Status](https://img.shields.io/badge/status-early%20version-yellow)](#-what-you-share)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

Sail [Following Seas](https://store.steampowered.com/app/2895440/) together. One player opens
their world, friends join it, and everybody is crew of the same ship.

Unofficial; not made or endorsed by Whipstaff Games.

> **This is an early version.**
> **The host should back up their saves first**: paste
> `%USERPROFILE%\AppData\LocalLow\WhipstaffGames\Following Seas` into the address bar of Explorer
> and copy that folder somewhere safe.

## 🧭 In short

1. Every player installs the mod ([Install](#-install)).
2. One player, the **host**, loads their world and opens it: **Esc → Co-op → Host this world**.
3. The others, the **guests**, choose **Co-op** on the title screen and **Join**.

The world, the ship and the save are the host's. A guest brings nothing in and takes nothing out:
no world or progress of their own in the session, and the mod never touches a guest's own saves.

## 📦 Install

Every player needs:

- Following Seas on Windows, **the same version of the game** for everybody;
- **the same version of this mod** for everybody;
- BepInEx 5, the free loader that runs mods for the game.

Steps:

1. **Find the game folder.** In Steam: right-click Following Seas → **Manage** →
   **Browse local files**. It is the folder with `Following Seas.exe`.
2. **Install BepInEx.** Download `BepInEx_win_x64_5.4.23.x.zip` from the
   [BepInEx releases](https://github.com/BepInEx/BepInEx/releases) (version 5, x64 — not 6) and
   unpack it into the game folder, so that `winhttp.dll` lies next to `Following Seas.exe`.
3. **Start the game once and close it.** This makes BepInEx create its folders.
4. **Unpack the mod's archive into the game folder.** You should now have
   `BepInEx\plugins\FollowingSeasCoop\FollowingSeasCoop.dll` with `LiteNetLib.dll` beside it.
5. **Start the game.** The title screen has a new **Co-op** button: the mod works.

To remove the mod, delete the folder `BepInEx\plugins\FollowingSeasCoop`.

## 🎮 Play

### 🏠 Host

1. Load your world as usual.
2. Press **Esc** and choose **Co-op**.
3. If you like, change **Your name** and **Guests** — how many friends may join: three unless
   you change it, fifteen at most.
4. Choose **Host this world**.
5. The page now shows **Your address** with a **Copy** button, and who is aboard. Send the address
   to your friends. On your home network that is all; for friends elsewhere see
   [Over the internet](#-over-the-internet).

Your game stops for a moment each time a guest loads the world. **Stop hosting** on the same page
ends the session: your guests go back to their title screen and your world stays loaded.

### 🚪 Guest

1. **Stay on the title screen** — do not load a world of your own — and choose **Co-op**.
2. A game hosted on your own network appears in the list by itself: choose **Join** in its row.
   For any other game, type the address the host sent you into **Host address** and choose
   **Join**.
3. The host's world loads and you come aboard their ship.

**Leave the crew** (**Esc → Co-op**) takes you back to the title screen.

### 🌐 Over the internet

Players in different homes need one of these two:

- **A virtual network** such as Hamachi, ZeroTier or Radmin VPN, the simpler way. Everybody joins
  the same network in that program, and the guest types the address the host has *in that
  program*.
- **Port forwarding.** The host sets their router to forward **UDP port 7777** to their PC and
  gives the guests their public address (a search for "what is my IP" shows it).

### 💾 Saving and playing again

Only the host's game saves the world, the way it always does. To continue another day, the same
host loads the same world and hosts again; the guests join again. If the host quits, the session
ends for everybody.

## 🤝 What you share

- 🌊 The ship, the sea, the weather and the time of day are the same for everybody.
- ☸️ Anyone can steer, reef, work the sheets and braces, drop the kedge anchor, moor, pump, and use
  winches, doors, lanterns and ballast.
- 🛢️ Barrels and sacks can be carried, put down and thrown by anyone.
- 💰 One purse and one set of ship's stores: money, provisions, debts, the journal and contracts
  belong to the whole crew. A delivery by any player pays the common purse.
- 📦 Cargo: anyone can load, move, unload and deliver crates.
- 🔨 Leaks open and grow for everybody, and anyone can caulk them.
- 🎣 Fishing with the trolling lines, the fish hold and the catch records.
- 🦌 Hunting: a shot animal is dead for everybody, and anyone can carry what it yields.
- 🚣 The rowboat: anyone can launch and stow it; whoever sits in it rows it.
- 🧑‍✈️ The ship's crew: hands taken aboard are the same for everybody, and a guest's orders in the
  crew panel are carried out.

**Not shared:** living animals (each player sees their own until one is shot), a fish in another
player's hand, another player's gunshot, and the view through another player's spyglass.

## 💡 Good to know

- **One player at a time.** A wheel, a rope, a winch, a barrel or the cargo panel is used by one
  player at a time. If someone else has it, a message names them, and you can take it when they
  let go.
- **A guest's Esc menu does not pause the game.** The world is the host's and sails on.
- **When the host pauses**, guests cannot move and see a message saying so.
- **If you drown**, you are put back on deck a few seconds later.
- **Left behind in the water?** Press **Esc**, choose **Co-op**, then **Return to the ship**.
- **Only the host can use the ship's Reset.**
- Sails may twitch a little on a guest's screen. This is only how it looks.

## ⚙️ Settings

Your name, the number of guests and the port are set on the Co-op page; you do not need a file
for them. Everything is also in `BepInEx\config\com.followingseas.coop.cfg`, which appears after
the first start and can be edited in Notepad while the game is closed.

| Setting | At first | What it does |
|---|---|---|
| `Player / Name` | your Windows user name | The name other players see. |
| `Network / Port` | 7777 | The UDP port the host listens on. |
| `Network / MaxClients` | 3 | How many guests the host accepts, from 1 to 15. |
| `Network / AnnounceOnLan` | true | The host appears in the Co-op list of players on the same network. |
| `UI / ShowStatus` | true | The small status line while a session runs. |

Leave the `Dev` section alone: it is for developing the mod.

## 🛟 If something goes wrong

- **There is no Co-op button on the title screen.** BepInEx or the mod is not in the right place:
  check steps 2 and 4 of [Install](#-install).
- **The host is not in the list.** The list only finds games on your own network. Type the host's
  address instead.
- **Cannot connect.** Check, in this order:
  1. the host has loaded a world and chosen **Host this world**;
  2. everybody has the same version of the mod and of the game;
  3. Windows Firewall on the host's PC allowed the game (it asks the first time you host);
  4. for friends elsewhere, the virtual network or the port forwarding from
     [Over the internet](#-over-the-internet).
- **A red line at the bottom of the screen.** The mod noticed a problem. You can usually keep
  playing; please report it.
- **Two players see different things** (a door, a barrel, the money). The guest leaves and joins
  again: joining brings the host's whole world anew.

**To report a problem**, send the file `BepInEx\LogOutput.log` from the game folder of the host
and of the guest, and say what you were doing. **F8** opens a debug window; you only need it if
you are asked for something from it.

## 🛠️ For developers

How the mod is built, how it works and its history of changes are in
[`DEVELOPMENT.md`](DEVELOPMENT.md).

## 📄 License

[MIT](LICENSE). The mod ships with LiteNetLib, also under MIT: see
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
