# mamba.TorchDiscordSync.Plugin

> **Note:** This project is under active development. Most features listed below are fully implemented and stable. Some advanced sub-features may still be in refinement.

An advanced Space Engineers Torch server plugin providing deep Discord integration, server automation, and administrative tooling.

Unlike simple chat relays, **TorchDiscordSync** focuses on *deep game integration* — tracking accurate kill data, analysing damage sources in real-time, synchronising player factions, and monitoring server health, all driven by a configurable Discord bot.

| Property | Value |
| :--- | :--- |
| **C# / Runtime** | C# latest / .NET Framework 4.8 |
| **Torch** | v2.4.46.328-master (2.4.37+) |
| **Space Engineers** | 1.208.15+ |
| **Author** | mamba |
| **Version** | 2.4.46 |

---

## 📋 Table of Contents

1. [Feature Overview](#feature-overview)
2. [Smart Chat Relay](#smart-chat-relay)
3. [Secure Faction Chat](#secure-faction-chat)
4. [Faction Synchronisation](#faction-synchronisation)
5. [Advanced Death Analysis](#advanced-death-analysis)
6. [Player & Death Location](#player--death-location)
7. [Server Health Monitoring](#server-health-monitoring)
8. [Discord Admin Commands](#discord-admin-commands)
9. [Player Verification System](#player-verification-system)
10. [Data Storage (XML / SQLite)](#data-storage-xml--sqlite)
11. [Event Logging](#event-logging)
12. [In-Game Commands `/tds`](#in-game-commands-tds)
13. [Configuration Overview](#configuration-overview)
14. [Roadmap & Future Plans](#roadmap--future-plans)
15. [Contributing](#contributing)
16. [Support](#support)

---

## Feature Overview

| Feature | Status | Notes |
| :--- | :---: | :--- |
| Core plugin structure | ✅ | Service injection, logging, config handling |
| Smart Chat Relay (bi-directional) | ✅ | Loop protection, privacy filters |
| Secure Faction Chat | ✅ | Private per-faction Discord channels |
| Faction Synchronisation | ✅ | SE factions → Discord roles + channels |
| Advanced Killer Detection | ✅ | Weapons, turret owners, collisions, environment |
| Damage Tracking Buffer | ✅ | Real-time damage event hooking |
| Death Location Classification | ✅ | Planet / orbit / space, fully dynamic |
| Death Message Templates | ✅ | Fully configurable per death type |
| SimSpeed Watchdog | ✅ | Lag-spike alerts with cooldown |
| Player Count Monitor | ✅ | Discord channel name reflects live count |
| Player Verification | ✅ | SteamID ↔ Discord UserID secure linking |
| Discord Admin Commands | ✅ | Admin console from Discord (`!tds ...`) |
| Data Storage – XML | ✅ | Default, zero-dependency storage |
| Data Storage – SQLite | ✅ | Opt-in; requires external DLL (see below) |
| Event Logging | ✅ | Structured log to DB + Discord staff channel |
| Security & Blacklist | ✅ | Sanitisation, content filtering, blacklist config |

---

## 💬 Smart Chat Relay

Bi-directional chat bridge between the SE server and a configured Discord channel.

- **Loop protection** — messages injected by the plugin (`TDS`, `Discord`, `Server` author tags) are detected and dropped before they can re-enter Discord.
- **Privacy filters** — private whispers and internal faction chat are *never* forwarded to the global channel.
- **Bi-directional** — Discord users can post messages that are broadcast in-game under the `[Discord]` prefix.
- **Rate limiting** — duplicate messages and rapid-fire spam are throttled (configurable delay, default 500 ms).
- **Text sanitisation** — strips control characters and truncates oversized messages before forwarding.

---

## 🔒 Secure Faction Chat

Private, isolated communication channels between SE faction chat and Discord.

- Each faction gets a dedicated Discord text channel inside its own Discord category.
- Messages typed in the SE faction chat are forwarded only to the matching Discord channel — never to the global channel.
- Messages posted in the Discord faction channel are delivered as private in-game messages to all online faction members.
- **GameFactionChatId mapping** — on first sight the channel ID is persisted for instant lookup on all subsequent messages.

---

## 🏴 Faction Synchronisation

Automatically mirrors Space Engineers factions into Discord.

- Creates a Discord **role** and a set of **channels** (text + voice + optional forum) per faction.
- Assigns/removes roles from Discord members as players join or leave factions in-game.
- **Duplicate prevention** — checks for existing roles/channels by name before creating to survive plugin restarts cleanly.
- **Undo / rollback** — `admin:sync:undo <tag>` and `admin:sync:undo_all` remove all Discord artefacts created for a faction and revert the database record.
- **Cleanup** — `admin:sync:cleanup` removes orphaned Discord roles/channels that no longer correspond to any known faction.
- **Cooldown guard** — a 10-second post-undo cooldown prevents a sync from running while Discord's internal cache still shows deleted items.

---

## 🎯 Advanced Death Analysis

Sophisticated, multi-layer system to determine the exact cause and context of every player death.

### Killer Detection

| Scenario | Resolution |
| :--- | :--- |
| Direct PvP | Killer name + weapon grid |
| Turret kill | Traces back to the grid owner, even when offline |
| Collision | Grid-collision victim detected from damage source |
| Environmental (fall, asphyxiation, low-pressure) | Classifies by `MyDamageType` |
| NPC / Space Pirates | Identified by faction tag |
| Self-inflicted | Detected and labelled separately |

### Damage Tracking

- Hooks directly into SE's `IMyDamageSystem.DamageHandler` event.
- Maintains a rolling in-memory buffer of recent damage events per player.
- Resolves the *real* cause of death when the native death event contains ambiguous data.

### Death Message Templates

Fully configurable templates in `DeathMessages.xml`. Four named placeholders are available:

| Placeholder | Meaning |
| :--- | :--- |
| `{killer}` | Name of the killer (player, NPC, or grid) |
| `{victim}` | Name of the dead player |
| `{weapon}` | Weapon or damage source label |
| `{location}` | Classified location string |

**Example templates:**

```
PvP:         "{killer} showed {victim} no mercy with {weapon}"
Turret:      "{victim} walked into {killer}'s {weapon}"
Environment: "{victim} discovered that space has no oxygen"
```

**An example of how the above template would look in the game:**
```
PvP:         "orko showed mamba no mercy with Elite Automatic Rifle on the surface of Mars"
Turret:      "mamba walked into Space Pirates's Interior Turret at the edge of known space"
Environment: "mamba discovered that space has no oxygen in the darkness"
```
- the location (random location text) is added at the end of the message. The exact coordinate of the event is never shown.

**The message on Discord has the same text as the in-game one with the emoticon prefix added:**
```
PvP:         "🔥 orko showed mamba no mercy with Elite Automatic Rifle on the surface of Mars"
Turret:      "⚡ mamba walked into Space Pirates's Interior Turret at the edge of known space"
Environment: "💀 mamba discovered that space has no oxygen in the darkness"
```

---

## 🌍 Player & Death Location

Dynamic, runtime-computed location classification — no hardcoded planet coordinates.

1. All planets in the world are detected at runtime (vanilla and modded).
2. The player's death position is compared against every planet's radius.
3. The nearest planet is selected and its surface/orbit zones are computed dynamically.

| Zone | Condition |
| :--- | :--- |
| **Surface** | Within planet atmosphere radius |
| **Low Orbit** | Just above atmosphere |
| **High Orbit** | Outer orbital band |
| **Inner Space** | Within config-defined inner threshold |
| **Outer Space** | Within config-defined outer threshold |
| **Deep Space** | Beyond all thresholds |

Death history is persisted per-player and used to detect kill streaks and retaliation scenarios.

---

## 🛡️ Server Health Monitoring

Continuous background monitoring via a configurable timer.

- **SimSpeed Watchdog** — if simulation speed drops below the configured threshold for longer than the configured window (default 30 s), an immediate alert is posted to the configured Discord admin channel. An alert cooldown prevents spam.
- **Player Count** — the Discord voice channel name is updated to reflect the current number of online players.
- **Startup grace** — no SimSpeed alerts are fired during the initial startup phase to avoid false positives.

---

## ⚙️ Discord Admin Commands

A dedicated Discord channel acts as a remote admin console. Post `!tds <subcommand>` in the configured admin bot channel to execute any admin command without logging into the game.

**Flow:** channel + author validation → `⚙️ Executing…` acknowledgement → command execution → result embed (success / error / info).

| Command | Description |
| :--- | :--- |
| `!tds admin:sync:check` | Check sync status of all factions |
| `!tds admin:sync:undo <tag>` | Undo sync for a specific faction |
| `!tds admin:sync:undo_all` | Undo all faction syncs |
| `!tds admin:sync:cleanup` | Remove orphaned Discord items |
| `!tds admin:sync:status` | Summary of current sync state |
| `!tds admin:verify:list` | List all verified players |
| `!tds admin:verify:pending` | List pending verifications |
| `!tds admin:verify:delete <steamid>` | Delete a verification record |
| `!tds reload` | Reload plugin configuration |
| `!tds status` | Show plugin status |

---

## ✅ Player Verification System

Securely links a Space Engineers SteamID to a Discord UserID via a one-time code exchange.

**Flow:**

1. Player types `/tds verify @DiscordUsername` (or Discord UserID) in-game.
2. Plugin looks up the Discord user, generates an 8-character random code, stores it with an expiry timestamp, and sends it as a Discord DM.
3. Player opens the Discord DM and replies `!verify <CODE>` to the bot.
4. Bot validates the code, marks the player as verified, and assigns the configured Discord role.

| State | Description |
| :--- | :--- |
| Not verified | No pending or completed record exists |
| Pending | Code generated, awaiting Discord reply |
| Expired | Code past expiry window (configurable, default 15 min) |
| Verified | SteamID ↔ DiscordID link confirmed |

---

## 💾 Data Storage (XML / SQLite)

### XML (default)

Zero external dependencies. Separate XML files per data type (factions, players, verifications, events). Automatic serialisation via `DatabaseService`.

### SQLite (opt-in)

Enable with `config.DataStorage.UseSQLite = true` (automatically enabled when the DLLs are present).

> **⚠️ Installation note:** `SQLite.Core.dll` is a mixed-mode assembly and must **not** be placed in the plugin folder. Place the DLLs in the Torch server root:
>
> ```
> TorchServer\
>   ├── System.Data.SQLite.dll       ← managed wrapper
>   └── x64\
>       └── SQLite.Interop.dll       ← native SQLite engine
> ```
>
> Download from: https://system.data.sqlite.org  
> Package: `sqlite-netFx48-binary-bundle-x64-2013-1.0.118.0.zip`

If the DLLs are absent, the plugin falls back to XML automatically without error.

---

## 📋 Event Logging

Structured log entries are written to the local database (XML or SQLite) and, optionally, to a Discord staff log channel (`config.Discord.StaffLog` channel ID).

Logged event types include: `VerificationAttempt`, `VerificationDeleted`, `UnverifyCommand`, `AdminCommand`, `CommandError`, and more.

---

## 🎮 In-Game Commands `/tds`

All commands are entered in global game chat. The plugin consumes them before they reach Discord.

### Player Commands

| Command | Description |
| :--- | :--- |
| `/tds help` | Show help (admin commands visible only to admins) |
| `/tds status` | Plugin status: bot connectivity, faction count, feature toggles |
| `/tds verify @Name` | Begin verification with a Discord username |
| `/tds verify <DiscordID>` | Begin verification with a Discord UserID |
| `/tds verify:status` | Check verification status and remaining code time |
| `/tds verify:delete` | Cancel a pending (not completed) verification |
| `/tds verify:help` | Step-by-step verification guide |

### Admin Commands

| Command | Description |
| :--- | :--- |
| `/tds reload` | Reload configuration from disk |
| `/tds sync` | Synchronise all factions to Discord |
| `/tds reset` | **Destructive** — delete all Discord roles/channels and reset |
| `/tds cleanup` | Remove orphaned Discord items |
| `/tds unverify <SteamID> [reason]` | Remove player verification |
| `/tds admin:sync:check` | Check faction sync status |
| `/tds admin:sync:undo <tag>` | Undo sync for a specific faction |
| `/tds admin:sync:undo_all` | Undo all faction syncs |
| `/tds admin:sync:cleanup` | Clean up orphaned Discord items |
| `/tds admin:sync:status` | Summary of sync state |
| `/tds admin:verify:list` | List all verified users |
| `/tds admin:verify:pending` | List pending verifications |
| `/tds admin:verify:delete <SteamID>` | Delete any verification record |

---

## ⚙️ Configuration Overview

Configuration files are loaded from the plugin's data directory on startup and can be hot-reloaded with `/tds reload`.

| File | Purpose |
| :--- | :--- |
| `config.xml` | Discord token, channel IDs, admin SteamIDs, feature toggles, thresholds |
| `DeathMessages.xml` | Per-death-type message templates |
| `Blacklist.xml` | Word/phrase blacklist for chat moderation |

Key settings:

| Setting | Description |
| :--- | :--- |
| `Discord.BotToken` | Discord bot token |
| `Discord.GlobalChannelId` | Channel for global chat relay |
| `Discord.AdminChannelId` | Channel for SimSpeed and admin alerts |
| `Discord.AdminBotChannelId` | Channel for `!tds` remote admin commands |
| `Discord.StaffLog` | Channel for structured event logs |
| `Chat.Enabled` | Enable/disable chat relay |
| `Death.Enabled` | Enable/disable death notifications |
| `SimSpeed.Threshold` | Minimum acceptable sim speed (default `0.9`) |
| `SimSpeed.AlertWindowSeconds` | Duration below threshold before alert fires (default `30`) |
| `VerificationCodeExpirationMinutes` | Code validity window (default `15`) |
| `AdminSteamIDs` | List of Steam IDs with admin command access |
| `DataStorage.UseSQLite` | Use SQLite instead of XML (default `true` when DLLs present) |

---

## 🗺️ Roadmap & Future Plans

### Raid Alerts (Offline Protection)
*Dependency: SQLite (already implemented)*  
Notify faction members via Discord DM/ping when their base takes damage while they are offline. High-frequency damage events require SQLite throughput to avoid impacting SimSpeed.

### Leaderboards & Statistics
Weekly and monthly PvP rankings generated from accumulated kill data: K/D ratios, Most Active Faction, Top Ace Pilot, kill streak records.

### Bounty / Retaliation System
Building on the existing retaliation-detection logic — players place bounties on others, tracked in the database and announced on Discord.

### Discord-Driven Economy Readout
Display live in-game economy data (credits, contracts, market prices) via the Discord admin console.

### Scheduled Server Announcements
Configurable recurring Discord embeds (rules, event notices, maintenance windows) posted on a CRON-like schedule.

### Faction War & Territory Tracking
Detect sustained PvP between faction grids and post live "War Status" updates to Discord.

### Web Dashboard *(long-term)*
A lightweight read-only web panel fed by SQLite: live stats, faction standings, kill history, verification status — no Discord required.

### Multi-Server Support
Route one Discord bot across multiple Torch instances with per-server channel configuration.

---

## 🤝 Contributing

Pull requests are welcome.  
Please maintain compatibility with **C# latest / .NET Framework 4.8**.  
All code comments must be in **English**.

---

## ☕ Support

If you like this project and want to support development:  
[Buy Me a Coffee ☕](https://buymeacoffee.com/mamba73)

*Developed by [mamba73](https://github.com/mamba73). Feel free to submit issues or pull requests!*
