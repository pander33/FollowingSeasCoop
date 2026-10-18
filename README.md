# ⚓ Following Seas Co-op

[![Version](https://img.shields.io/badge/version-0.1.0-blue)](https://github.com/pander33/FollowingSeasCoop/releases/latest)
[![Game](https://img.shields.io/badge/game-Following%20Seas-1b2838?logo=steam&logoColor=white)](https://store.steampowered.com/app/2895440/)
[![BepInEx](https://img.shields.io/badge/BepInEx-5.4.23.5-orange)](https://github.com/BepInEx/BepInEx/releases)
[![Players](https://img.shields.io/badge/players-2--16-brightgreen)](#-play)
[![Status](https://img.shields.io/badge/status-early%20version-yellow)](#-what-you-share)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

Sail [Following Seas](https://store.steampowered.com/app/2895440/) together. One player opens
their world, friends join it, and everybody is crew of the same ship.

Unofficial; not made or endorsed by Whipstaff Games.

> **This is an early version.** Not every part has been played by a full crew yet.
> **The host should back up their saves first**: paste
> `%USERPROFILE%\AppData\LocalLow\WhipstaffGames\Following Seas` into the address bar of Explorer
> and copy that folder somewhere safe.

## 🧭 In short

1. Every player installs the mod ([Install](#-install)).
2. One player, the **host**, loads their world and opens it: **Esc → Co-op → Host this world**.
3. The others, the **guests**, choose **Co-op** on the title screen and **Join**. Steam friends
   need no address: the host's game is in their list.

The world, the ship and the save are the host's. A guest brings nothing in and takes nothing out:
no world or progress of their own in the session, and the mod never touches a guest's own saves.

## 📦 Install

Every player needs Following Seas on Windows, **the same version of the game** and **the same
version of this mod**.

The mod runs on BepInEx 5, a free loader for game mods. Download it from the
[latest release](https://github.com/pander33/FollowingSeasCoop/releases/latest), which has two
archives:

| Archive | For whom |
|---|---|
| `FollowingSeasCoop-0.1.0.zip` | **Most players.** The mod with BepInEx 5.4.23.5 inside: nothing else to download. |
| `FollowingSeasCoop-0.1.0-plugin-only.zip` | A game that already has BepInEx 5, version 5.4.23.5 or newer. |

Steps:

1. **Close the game and find its folder.** In Steam: right-click Following Seas → **Manage** →
   **Browse local files**. It is the folder with `Following Seas.exe`.
2. **Unpack the archive into that folder.** You should now have `winhttp.dll` next to
   `Following Seas.exe`, and `BepInEx\plugins\FollowingSeasCoop\FollowingSeasCoop.dll` with three
   more `.dll` files beside it. Keep them together.
3. **Start the game.** The title screen has a new **Co-op** button: the mod works.

**Already have BepInEx?** Take the plugin-only archive and unpack it the same way. BepInEx must
be version 5 for x64 (not 6), 5.4.23.5 or newer: older ones have trouble with this game's engine.
The complete archive is safe too — it replaces BepInEx's own files with 5.4.23.5 and leaves your
other mods and their settings alone.

**To update**, unpack the new archive over the old one. Everybody updates together: two
different versions of the mod do not connect.

**To remove the mod**, delete the folder `BepInEx\plugins\FollowingSeasCoop`. To remove BepInEx
as well, delete the `BepInEx` folder and the files `winhttp.dll`, `doorstop_config.ini`,
`.doorstop_version` and `changelog.txt` beside `Following Seas.exe`. Your saves are not touched.

## 🎮 Play

### 🏠 Host

1. Load your world as usual.
2. Press **Esc** and choose **Co-op**.
3. If you like, change **Your name** and **Guests** — how many friends may join: three unless
   you change it, fifteen at most.
4. **Steam** says who may come through Steam: **Anyone** (your Steam friends see the game, and
   anyone else can join by your Steam ID or your address), **Friends only** (your Steam friends
   and nobody else), or **Off** (by address only).
5. Choose **Host this world**.
6. The page now shows who is aboard. Your Steam friends can join right away. For anyone else it
   shows **Your address** with a **Copy** button, and **Copy ID** for your Steam ID: see
   [Over the internet](#-over-the-internet).

Your game stops for a moment each time a guest loads the world. **Stop hosting** on the same page
ends the session: your guests go back to their title screen and your world stays loaded.

### 🚪 Guest

1. **Stay on the title screen** — do not load a world of your own — and choose **Co-op**.
2. A Steam friend who hosts, and a game hosted on your own network, appear in the list by
   themselves: choose **Join** in that row. For any other game, type what the host sent you —
   an address or a Steam ID — into **Host address** and choose **Join**.
3. The host's world loads and you come aboard their ship.

A Steam friend's game can also be joined from Steam itself: right-click the friend in the friends
list and choose **Join Game**, or accept their invite. The game goes to its title screen and
joins by itself.

**Leave the crew** (**Esc → Co-op**) takes you back to the title screen.

### 🌐 Over the internet

Players in different homes need one of these:

- **Steam**, the simplest way: nothing to set up. Steam must be running on both PCs, and the host
  leaves **Steam** on **Anyone** or **Friends only**. Steam friends find the game in their Co-op
  dialog. Someone who is not a friend joins by the host's Steam ID: the host chooses **Copy ID**
  and sends it, the guest pastes it into **Host address**.
- **A virtual network** such as Hamachi, ZeroTier or Radmin VPN. Everybody joins the same network
  in that program, and the guest types the address the host has *in that program*.
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
- 💰 One purse and one set of stores: money, provisions aboard and in the store ashore, debts, the
  journal (contracts, notes read and places found) and contracts belong to the whole crew. A delivery by any player pays the common purse.
- 🗺️ The chart: courses and markers drawn by anyone are on everybody's chart. A change made while
  your own chart is open shows the next time you open it.
- 📦 Cargo: anyone can load, move, unload and deliver crates.
- 🔨 Leaks open and grow for everybody, and anyone can caulk them.
- 🎣 Fishing with the trolling lines, the fish hold and the catch records.
- 🦌 Hunting: a shot animal is dead for everybody, and anyone can carry what it yields.
- 🚣 The rowboat: anyone can launch and stow it; whoever sits in it rows it.
- 🧑‍✈️ The ship's crew: hands taken aboard are the same for everybody, and a guest's orders in the
  crew panel are carried out.
- ⚓ The shipyard: plans, payments and materials are shared, and a ship launched by anyone becomes
  everybody's ship. Whoever stood on the old deck is put aboard the new one.

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
- **Fast travel by the chart** takes the ship and everyone aboard, whoever starts it. A player
  ashore, in the rowboat or in the water is left behind.
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
| `Steam / Enabled` | true | Host and join through Steam as well as by address. |
| `Steam / FriendsOnly` | false | A host over Steam lets in Steam friends only, and nobody by address. |
| `UI / ShowStatus` | true | The small status line while a session runs. |

Leave the `Dev` section alone: it is for developing the mod.

## 🛟 If something goes wrong

- **There is no Co-op button on the title screen.** The archive was not unpacked into the game
  folder itself: check step 2 of [Install](#-install). A folder inside a folder
  (`...\Following Seas\FollowingSeasCoop-0.1.0\BepInEx`) is the usual mistake.
- **The host is not in the list.** The list shows Steam friends who host and games on your own
  network. For any other, type the host's address or Steam ID instead.
- **The dialog says Steam is not running.** Start Steam, then open the Co-op dialog again. The
  Steam account has to own Following Seas. Without Steam the mod still works by address.
- **A Steam friend is listed as "not hosting".** They have not chosen **Host this world** yet,
  their session is full, or they host with **Steam** set to **Off**.
- **Cannot connect.** Check, in this order:
  1. the host has loaded a world and chosen **Host this world**;
  2. everybody has the same version of the mod and of the game;
  3. Windows Firewall on the host's PC allowed the game (it asks the first time you host);
  4. for friends elsewhere, Steam, the virtual network or the port forwarding from
     [Over the internet](#-over-the-internet).
- **A red line at the bottom of the screen.** The mod noticed a problem. You can usually keep
  playing; please report it.
- **Two players see different things** (a door, a barrel, the money). The guest leaves and joins
  again: joining brings the host's whole world anew.

**To report a problem**, open an
[issue](https://github.com/pander33/FollowingSeasCoop/issues), attach the file
`BepInEx\LogOutput.log` from the game folder of the host and of the guest, and say what you were
doing. **F8** opens a debug window; you only need it if
you are asked for something from it.

## 🛠️ For developers

How the mod is built, how it works and its history of changes are in
[`DEVELOPMENT.md`](DEVELOPMENT.md).

## 📄 License

[MIT](LICENSE). The mod ships with LiteNetLib and Facepunch.Steamworks, also under MIT, and with
Valve's `steam_api64.dll`: see [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md). The complete
archive also carries BepInEx as its authors publish it; its licenses are in `BepInEx\licenses`.
