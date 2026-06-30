# mamba.TorchDiscordSync.Plugin

> **Note:** This project is under active development. Most features listed below are fully implemented and stable. Some advanced sub-features may still be in refinement.

An advanced Space Engineers Torch server plugin providing deep Discord integration, server automation, and administrative tooling.

Unlike simple chat relays, **TorchDiscordSync** focuses on *deep game integration* — tracking accurate kill data, analysing damage sources in real-time, synchronising player factions, and monitoring server health, all driven by a configurable Discord bot.

| Property | Value |
| :--- | :--- |
| **C# / Runtime** | C# 6.0 / .NET Framework 4.8 Syntax |
| **Torch** | v2.4.65.328-master (2.4.49+) |
| **Space Engineers** | 1.208.15+ |
| **Author** | mamba |
| **Version** | 2.4.65 |

---

## 📋 Table of Contents

1. [Feature Overview](#feature-overview)
2. [Quick Start](#quick-start)
3. [Smart Chat Relay](#smart-chat-relay)
4. [Secure Faction Chat](#secure-faction-chat)
5. [Faction Synchronisation](#faction-synchronisation)
6. [Advanced Death Analysis](#advanced-death-analysis)
7. [Player & Death Location](#player--death-location)
8. [Server Health Monitoring](#server-health-monitoring)
9. [Discord Admin Commands](#discord-admin-commands)
10. [Player Verification System](#player-verification-system)
11. [Data Storage](#data-storage)
12. [Event Logging & Infrastructure](#event-logging--infrastructure)
13. [Torch UI Configuration](#torch-ui-configuration)
14. [In-Game Commands](#in-game-commands)
15. [Configuration & Localization](#configuration--localization)
16. [Roadmap & Future Plans](#roadmap--future-plans)
17. [Contributing](#contributing)
18. [Support](#support)

---

## 🌟 Feature Overview

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
| Localization Engine | ✅ | Dedicated `Localization/` folder with `.ini` regional support |
| Native Torch UI Interface | ✅ | Full visual configuration tab within Torch Server GUI |

---

## 🚀 Quick Start

**Complete bot setup — from Discord Developer Portal to first message in-game.**

---

### Step 1 — Create the Discord bot

1. Go to https://discord.com/developers/applications and click **New Application**.
2. Give it a name (e.g. `MyServerBot`), then open the **Bot** tab.
3. Click **Reset Token**, copy the token, and paste it into `MainConfig.xml`:
   ```xml
   <BotToken>PASTE_TOKEN_HERE</BotToken>
   ```
4. On the same Bot tab, scroll down to **Privileged Gateway Intents** and enable **both**:
   - ✅ **Server Members Intent**
   - ✅ **Message Content Intent**

   > Without both intents the bot will connect and then immediately disconnect with a WebSocket error.

---

### Step 2 — Invite the bot to your Discord server

1. In the Developer Portal, go to **OAuth2 → URL Generator**.
2. Under **Scopes**, tick `bot`.
3. Under **Bot Permissions**, tick at minimum:
   - `Send Messages`
   - `Read Message History`
   - `Manage Roles`
   - `Manage Channels`
4. Copy the generated URL, open it in a browser, and invite the bot to your server.

---

### Step 3 — Get your IDs

Enable **Developer Mode** in Discord: *Settings → Advanced → Developer Mode*.
Then right-click any server, channel, or role and select **Copy ID**.

You need at minimum:

| What | Where to find it | Config field |
| :--- | :--- | :--- |
| Discord Server ID | Right-click your server name → Copy ID | `Discord.GuildID` |
| Global chat channel | Right-click the channel → Copy ID | `Discord.ChatChannelId` |

Optional but recommended:

| What | Config field |
| :--- | :--- |
| Admin alert channel | `Discord.AdminAlertChannelId` |
| Remote admin command channel | `Discord.AdminBotChannelId` |
| SimSpeed display channel | `Discord.SimSpeedChannelId` |
| Player count display channel | `Discord.PlayerCountChannelId` |

---

### Step 4 — Configure `MainConfig.xml`

Minimum working configuration:

```xml
<Discord>
  <BotToken>YOUR_BOT_TOKEN</BotToken>
  <GuildID>YOUR_SERVER_ID</GuildID>
  <ChatChannelId>YOUR_CHAT_CHANNEL_ID</ChatChannelId>
</Discord>

<Chat>
  <Enabled>true</Enabled>
  <ServerToDiscord>true</ServerToDiscord>  <BotToGame>true</BotToGame>              </Chat>
```

Everything else defaults to `0` (disabled) and can be enabled later. Alternatively, use the visual interface inside Torch Server to change settings dynamically.

---

### Step 5 — Start Torch

The bot runs **embedded inside Torch** — no separate hosting is needed.
On server start, check the Torch log for these lines to confirm everything is working:

```txt
[SUCCESS] [DISCORD_BOT] Bot connection established
[SUCCESS] [DISCORD_BOT] Bot is ready and listening!
[SUCCESS] All services initialized
```

If you see repeated reconnection attempts, check that **both** Privileged Intents are enabled (Step 1) and that `GuildID` is not `0`.

---

### Step 6 — Test it

| Test | How |
| :--- | :--- |
| Bot online | Should appear as online in your Discord server member list |
| Server → Discord | Type anything in-game global chat → should appear in your Discord channel |
| Discord → Game | Post a message in your Discord chat channel → should appear in-game as `[Discord] Name: message` |
| Admin alerts | Server start/stop messages should appear in `AdminAlertChannelId` |
| In-game commands | Type `/tds help` in-game — you should see the help text appear as a private message |

---

### Hot-reload

Any time you change `MainConfig.xml` while the server is running, apply it without restarting:
- **In-game:** `/tds reload`
- **From Discord:** `!tds reload` (in the `AdminBotChannelId` channel)
- **From Torch GUI:** Simply modify parameters directly on the interface tab and click the save/reload control handle.

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

### NPC Detection Fixes (Spiders & Wolves)
A critical issue regarding native Environment/NPC death mapping has been resolved. In historical variations, non-grid wildlife sources (such as Space Spiders or Cyberwolves) could either generate empty references or crash calculation layers due to unstable object types during clean character cleanups. 

The detection architecture now successfully patterns environmental creature attacks under explicit criteria. To complement this, a toggle has been added to the configuration system:
- `<TrackNPCActivity>` (Checkbox in the GUI) enables or disables logging, tracking, or broadcasting deaths caused by or related to non-player animal/NPC interactions, eliminating clutter from public server feeds.

### Death Message Templates

Fully configurable templates in `DeathMessages.xml`. Four named placeholders are available:

| Placeholder | Meaning |
| :--- | :--- |
| `{killer}` | Name of the killer (player, NPC, or grid) |
| `{victim}` | Name of the dead player |
| `{weapon}` | Weapon or damage source label |
| `{location}` | Classified location string (appended automatically) |

**Example templates and their output:**

```txt
PvP:         "{killer} showed {victim} no mercy with {weapon}"
→ 🔥 orko showed mamba no mercy with Elite Automatic Rifle on the surface of Mars

Turret:      "{victim} walked into {killer}'s {weapon}"
→ ⚡ mamba walked into Space Pirates's Interior Turret at the edge of known space

Environment: "{victim} discovered that space has no oxygen"
→ 💀 mamba discovered that space has no oxygen in the darkness
```

The location text is appended automatically — exact coordinates are never shown. Discord messages receive the same text with a random emote prefix from `DeathMessageEmotes`.

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

- **SimSpeed Watchdog** — if simulation speed drops below the configured threshold, an alert is posted to the admin channel. A cooldown (default 20 min) prevents spam.
- **Player Count** — a Discord voice channel name is updated to reflect the current number of online players.
- **Server lifecycle alerts** — start, stop, restart, and crash events are posted to the admin channel.
- **Startup grace** — no SimSpeed alerts are fired during the initial startup phase to avoid false positives.

---

## ⚙️ Discord Admin Commands

A dedicated Discord channel acts as a remote admin console. Post `!tds <subcommand>` in the configured admin bot channel to execute any admin command without logging into the game.

**Flow:** channel + author validation → `⚙️ Executing…` acknowledgement → command execution → result embed.

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
| `!tds reload` | Reload plugin configuration and translations |
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

## 💾 Data Storage

### XML (default)

Zero external dependencies. Separate XML files per data type (factions, players, verifications, events).

### SQLite (opt-in)

Enable with `<UseSQLite>true</UseSQLite>` in the `DataStorage` section of your config.

> **⚠️ Critical installation note:** `System.Data.SQLite.dll` is a mixed-mode assembly and must **not** be placed in the plugin folder. Place it in the **Torch server root** (where `Torch.Server.exe` lives).

**Download (verified bundle — single DLL, no `x64` folder needed):**
- Release page: https://github.com/mamba73/mamba.TorchDiscordSync/releases/tag/v2.4.47
- Direct: https://github.com/mamba73/mamba.TorchDiscordSync/releases/download/v2.4.47/System.Data.SQLite_v1.0.118.zip

**Installation:**
1. Shut down Torch.
2. Copy `System.Data.SQLite.dll` from the ZIP into the Torch root folder.
3. Restart Torch.

If the DLL is absent, the plugin falls back to XML silently — the server will not crash.

---

## 📋 Event Logging & Infrastructure

### LoggerUtil Architecture
The plugin employs a dedicated internal pipeline wrapped within `LoggerUtil`. All internal diagnostic workflows utilize structured log hooks (`LogInfo`, `LogWarning`, `LogError`) to route outputs systematically to the server console, local text dumps, and active diagnostic data adapters without relying on generalized layout wrappers.

Structured log entries are also cleanly appended to the local database backend and optionally broad-casted directly into the configured Discord staff log channel (`Discord.StaffLog` channel ID).

Logged event types include: `VerificationAttempt`, `VerificationDeleted`, `UnverifyCommand`, `AdminCommand`, `CommandError`, and more.

---

## 🖥️ Torch UI Configuration

The plugin embeds a rich visual configuration dashboard mapped into a dedicated **Torch Server GUI Tab** labeled `TorchDiscordSync`. This user interface replaces manual text serialization maintenance for daytime activities, giving administrators absolute control via structured WPF components.

### UI Sections & Parameter Controls
1. **Core Settings Layout:**
   - **Enable Master Plugin:** Global checkbox bound directly to runtime engine lifecycle states.
   - **Debug Mode Logging:** Activating this box instructs `LoggerUtil` to capture fine-grained trace context.

2. **Discord API Connectivity Card:**
   - **Bot Authorization Token:** Masked text field to insert private credentials. Includes manual visibility toggle controls.
   - **Target Server Guild ID:** Text block validated against numerical formats to lock runtime execution into specific servers.
   - **Global Chat & Staff Log Identifiers:** Independent input rows supporting automated clipboard evaluation to simplify raw numeric mapping.

3. **Feature Automation Toggles:**
   - Checkboxes are provided to switch functional frameworks natively: `Enable Chat Relay`, `Synchronize Server Factions`, `Execute SimSpeed Monitoring`.
   - **Track NPC Activity Checkbox:** Explicit boolean state toggle to completely suppress or permit the logging and broadcasting of character updates and tracking vectors stemming from system wildlife scripts (Spiders / Wolves).

4. **Dynamic Value Sliders:**
   - Visual scroll nodes control timing configurations including validation intervals, damage logging retention thresholds, and SimSpeed trigger ceilings without requiring system hot-reloads.

---

## 🎮 In-Game Commands

All commands are typed in global game chat. The plugin consumes them before they reach Discord.

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
| `/tds reload` | Reload configuration and localization tables from disk |
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

## ⚙️ Configuration & Localization

Configuration is stored in `MainConfig.xml` in the plugin's data directory.
Hot-reload: `/tds reload` in-game or `!tds reload` from Discord.

All channel/role/category IDs default to `0` (feature disabled). Set them to the real Discord ID to activate.

| File | Purpose |
| :--- | :--- |
| `MainConfig.xml` | All settings — token, channel IDs, feature toggles, thresholds |
| `DeathMessages.xml` | Per-death-type message templates |
| `Blacklist.xml` | Word/phrase blacklist for chat moderation |

### Localization Subsystem
All active strings and translation dictionary layers are compiled systematically under a dedicated `Localization/` subdirectory inside the plugin's core data directory. The target mapping lookup is declared directly within the primary runtime profile parameter:

```xml
<Language>en-US</Language>
```

The translation system automatically isolates localization files based on formal BCP-47 language codes bundled with region qualifiers (`en-US`, `en-GB`, `hr-HR`). On initialization, missing regional dictionaries dynamically build a clean internal layout copy directly into the targeted platform path.

#### 🇬🇧 English (US) Base Template (`Localization/en-US.ini`)
```ini
; ==============================================================================
;   [X] DO NOT TRANSLATE THIS SECTION
; ==============================================================================
[Metadata]
ConfigVersion = 1
LanguageCode = en-US
LanguageName = English (United States)

; ==============================================================================
;   [V] TRANSLATE EVERYTHING BELOW THIS LINE
; ==============================================================================

[Help]
Help.Separator = ===============================================
Help.Title =          TDS (Torch Discord Sync)
Help.Admin.SyncHeader = [ADMIN] SYNC COMMANDS:
Help.Admin.SyncCheck =   /tds admin:sync:check      - Check status of all faction syncs

[Status]
Status.Header = === TDS Plugin Status ===
Status.Online = Status: [OK] ONLINE

[Zone]
ZoneSurface = on the surface of planet {planet}
ZoneLowOrbit = in low orbit of planet {planet}
```

#### 🇬🇧 English (UK) Template (`Localization/en-GB.ini`)
```ini
[Metadata]
ConfigVersion = 1
LanguageCode = en-GB
LanguageName = English (United Kingdom)

[Help]
Help.Separator = ===============================================
Help.Title =          TDS (Torch Discord Sync)

[Zone]
ZoneSurface = on the surface of planet {planet}
ZoneLowOrbit = in low orbit of planet {planet}
```

#### 🇭🇷 Hrvatski Paket (`Localization/hr-HR.ini`)
```ini
[Metadata]
ConfigVersion = 1
LanguageCode = hr-HR
LanguageName = Hrvatski (Hrvatska)

[Help]
Help.Separator = ===============================================
Help.Title =          TDS (Torch Discord Sync)
Help.Admin.SyncHeader = [ADMIN] NAREDBE SINKRONIZACIJE:
Help.Admin.SyncCheck =   /tds admin:sync:check      - Provjeri status sinkronizacije frakcija

[Status]
Status.Header = === TDS Status Dodatka ===
Status.Online = Status: [OK] AKTIVAN

[Zone]
ZoneSurface = na povrsini planeta {planet}
ZoneLowOrbit = u niskoj orbiti planeta {planet}
```

---

### Root settings

| Setting | Default | Description |
| :--- | :---: | :--- |
| `Enabled` | `true` | Master enable/disable switch for the entire plugin |
| `Debug` | `false` | Enable verbose debug logging |
| `AdminSteamIDs` | *(empty)* | List of SteamIDs with admin command access |
| `CleanupIntervalSeconds` | `30` | How often orphaned data is cleaned up |
| `DamageHistoryMaxSeconds` | `15` | How long damage events are kept in the tracking buffer |

---

### Discord section

| Setting | Default | Description |
| :--- | :---: | :--- |
| `BotToken` | `YOUR_BOT_TOKEN` | **Required.** Discord bot token from the Developer Portal |
| `GuildID` | `0` | **Required.** Your Discord server (guild) ID |
| `BotPrefix` | `!` | Prefix for bot commands (e.g. `!verify`, `!tds`) |
| `SyncIntervalSeconds` | `30` | How often faction sync runs (seconds) |
| `EnableDMNotifications` | `true` | Send verification codes via Discord DM |
| `VerificationCodeExpirationMinutes` | `15` | Verification code validity window |
| `ChatChannelId` | `0` | Channel for **global in-game chat relay** (Game ↔ Discord) |
| `StaffLog` | `0` | Channel for structured plugin event logs |
| `StatusChannelId` | `0` | Channel for server status embeds |
| `SimSpeedChannelId` | `0` | Voice channel whose **name** is updated with live SimSpeed |
| `PlayerCountChannelId` | `0` | Voice channel whose **name** is updated with live player count |
| `FactionCategoryId` | `0` | Discord category where faction channels are created |
| `AdminAlertChannelId` | `0` | Channel for SimSpeed warnings and server lifecycle alerts |
| `AdminBotChannelId` | `0` | Channel for `!tds` remote admin commands from Discord |
| `VerifiedRoleId` | `0` | Role automatically assigned after successful verification |

> **To get a channel or server ID:** enable Developer Mode in Discord settings → right-click any channel or server → **Copy ID**.

---

### Chat section

Controls the chat relay and optional in-game chat moderation.

| Setting | Default | Description |
| :--- | :---: | :--- |
| `Enabled` | `false` | Master toggle for the chat system |
| `ServerToDiscord` | `false` | Forward in-game global chat to `ChatChannelId` |
| `BotToGame` | `false` | Broadcast Discord messages into the game |
| `UseFactionChat` | `false` | Enable faction chat routing (Game ↔ Discord faction channel) |
| `GameToDiscordFormat` | `:rocket: **{p}**: {msg}` | Format for game→Discord messages |
| `DiscordToGameFormat` | `[Discord] {p}: {msg}` | Format for Discord→game broadcasts |
| `JoinMessage` | `:sunny: {p} joined` | Posted to Discord when a player joins |
| `LeaveMessage` | `:new_moon: {p} left` | Posted to Discord when a player leaves |
| `StripEmojisForInGameChat` | `true` | Remove Discord emoji before showing messages in-game |
| `GlobalColor` | `White` | In-game chat colour for global relay messages |
| `FactionColor` | `Green` | In-game chat colour for faction relay messages |
| `EnableModeration` | `false` | Enable auto-moderation (blacklist, warn, mute, kick) |
| `MaxWarningsBeforeMute` | `3` | Warnings before a player is muted |
| `MuteDurationMinutes` | `10` | Duration of a mute |
| `MaxMutesBeforeKick` | `2` | Mutes before a player is kicked |
| `AdminLogChannelId` | `0` | Channel for moderation action logs |
| `IgnoredChatAuthors` | *(empty)* | List of in-game chat author names to silently drop before relaying to Discord. Use this if another bot or mod on your server echoes/reposts player messages back into chat (e.g. a translator or repeat bot), which would otherwise be duplicated on Discord. Comparison is case-insensitive — add the exact name shown in the duplicated line. |

```xml
<Chat>
  <IgnoredChatAuthors>
    <Author>Good.bot</Author>
  </IgnoredChatAuthors>
</Chat>
```
> If you see every chat message appearing twice on Discord, check whether another bot is echoing chat in-game and add its exact display name here.

---

### Death section

| Setting | Default | Description |
| :--- | :---: | :--- |
| `Enabled` | `false` | Enable death detection and analysis |
| `LogToDiscord` | `false` | Post death messages to `ChatChannelId` |
| `AnnounceInGame` | `false` | Broadcast death message to all in-game players |
| `DetectRetaliation` | `false` | Detect and highlight retaliation kills |
| `RetaliationWindowMinutes` | `60` | Window for a kill to count as retaliation |
| `EnableLocationZones` | `true` | Classify death location (surface / orbit / space) |
| `GridDetectionEnabled` | `true` | Include the grid name in the death context |
| `ShowGridName` | `true` | Show the grid name in death messages |
| `DeathMessageEmotes` | `📢⚔️💀🔥⚡` | Pool of emotes randomly prepended to Discord death messages |
| `MessageDeduplicationWindowSeconds` | `3` | Suppress duplicate death events within this window |
| `InnerSystemMaxKm` | `5000` | Distance threshold for Inner System zone (km) |
| `OuterSpaceMaxKm` | `10000` | Distance threshold for Outer Space zone (km) |
| `PlanetProximityMultiplier` | `3` | Multiplier applied to planet radius for surface/orbit zones |
| `TrackNPCActivity` | `true` | **New.** Toggle logging and broadcasting for non-player creature/wildlife (Spiders/Wolves) events |

---

### Monitoring section

| Setting | Default | Description |
| :--- | :---: | :--- |
| `Enabled` | `true` | Enable the monitoring service |
| `StatusUpdateIntervalSeconds` | `30` | How often channel names and status are refreshed |
| `EnableSimSpeedMonitoring` | `true` | Update `SimSpeedChannelId` channel name with live SimSpeed |
| `SimSpeedThreshold` | `0.6` | SimSpeed below this value triggers a warning alert |
| `SimSpeedChannelNameFormat` | `{emoji} SimSpeed: {ss}` | Channel name template (`{emoji}`, `{ss}`) |
| `SimSpeedNormalEmoji` | `🔧` | Emoji shown when SimSpeed is healthy |
| `SimSpeedWarningEmoji` | `⚠️` | Emoji shown when SimSpeed is below threshold |
| `EnableSimSpeedAlerts` | `true` | Post alert to `AdminAlertChannelId` on SimSpeed drop |
| `SimSpeedAlertCooldownSeconds` | `1200` | Minimum gap between SimSpeed alerts (default 20 min) |
| `EnablePlayerCountMonitoring` | `true` | Update `PlayerCountChannelId` name with live player count |
| `PlayerCountChannelNameFormat` | `👥 {p}/{pp} players` | Channel name template (`{p}` = online, `{pp}` = max) |
| `EnableAdminAlerts` | `true` | Post server start/stop/crash notices to `AdminAlertChannelId` |
| `ServerStartedMessage` | `✅ Server Started!` | Message posted when the session loads |
| `ServerStoppedMessage` | `❌ Server Stopped!` | Message posted when the session unloads cleanly |
| `ServerCrashedMessage` | `💥 CRITICAL: SERVER CRASHED` | Message posted on unexpected shutdown |

---

### Faction section

| Setting | Default | Description |
| :--- | :---: | :--- |
| `Enabled` | `false` | Enable faction synchronisation to Discord |
| `AutoCreateChannels` | `false` | Automatically create a text channel per faction |
| `AutoCreateForum` | `false` | Automatically create a forum channel per faction |
| `AutoCreateVoice` | `false` | Automatically create a voice channel per faction |
| `FactionDiscordToGlobalFallback` | `true` | Fall back to the global channel if a faction has no Discord channel |

---

### DataStorage section

| Setting | Default | Description |
| :--- | :---: | :--- |
| `UseSQLite` | `true` | Use SQLite as primary storage; falls back to XML if DLL is absent |
| `SaveEventLogs` | `true` | Persist plugin event log entries to database |
| `SaveDeathHistory` | `true` | Persist death history per player |
| `SaveGlobalChat` | `false` | Persist global chat messages to database |
| `SaveFactionChat` | `false` | Persist faction chat messages to database |
| `SavePrivateChat` | `false` | Persist private/whisper messages to database |

---

## 🗺️ Roadmap & Future Plans

### Raid Alerts (Offline Protection)
*Dependency: SQLite (already implemented)*
Notify faction members via Discord DM/ping when their base takes damage while they are offline.

### Leaderboards & Statistics
Weekly and monthly PvP rankings: K/D ratios, Most Active Faction, Top Ace Pilot, kill streak records.

### Bounty / Retaliation System
Players place bounties on others, tracked in the database and announced on Discord.

### Discord-Driven Economy Readout
Display live in-game economy data (credits, contracts, market prices) via the Discord admin console.

### Scheduled Server Announcements
Configurable recurring Discord embeds (rules, event notices, maintenance windows) on a CRON-like schedule.

### Faction War & Territory Tracking
Detect sustained PvP between faction grids and post live "War Status" updates to Discord.

### Web Dashboard *(long-term)*
Lightweight read-only web panel fed by SQLite: live stats, faction standings, kill history, verification status.

### Multi-Server Support
Route one Discord bot across multiple Torch instances with per-server channel configuration.

---

## 🤝 Contributing

Pull requests are welcome. Please adhere to the following standards to ensure project stability:

*   **Syntax Compatibility**: Strictly maintain **C# 6.0 / .NET Framework 4.8** compatibility. Modern C# features (e.g., file-scoped namespaces, records, init-only setters) are not supported.
*   **Language Standards**: All code, comments, and documentation must be written in **English**.
*   **Zero Hardcoding**: All user-facing text (chat/logs/Discord) must be handled via dynamic configuration or localized modules.
*   **Production Readiness**: Only submit 100% complete, functional code. Placeholders (`// TODO`, `// ...`) or partial implementations will not be accepted.

---

## 💡 Special Thanks & Acknowledgments

A huge thank you to the community members who helped improve this plugin through their invaluable feedback, bug reports, and ideas:

* **Owen [C#]** – For forking the plugin, porting it to Linux, and implementing a full configuration UI, which inspired the native Torch Admin settings panel interface.
* **Ryunoske** – For extensive testing, pointing out configuration bottlenecks (SQLite setup/Discord Intents), reporting the in-game command registration bug, and suggesting UI status command optimizations.
* **Daniel_Felipe [ECHO]** – For his massive contribution of over 1,200 lines of code in an attempt to localize the plugin, which served as the direct catalyst and inspiration for implementing our modular, configuration-based localization system. Daniel_Felipe also provided critical feedback on NPC entity handling in death messages.

---

## 💬 Getting Help & Feedback

If you encounter a bug, have a feature request, or need assistance, please use the official support thread on the Torch Discord:

*   **Support & Bug Reports**: [mamba.TorchDiscordSync.Plugin Support Forum](https://discord.com/channels/929141809769226271/1482305125451038781)

Please check existing posts before asking to avoid duplicates. When reporting bugs, **always include the relevant log files** from `\Instance\mambaSaveData\logs\` so I can investigate the issue effectively.

---

## ☕ Support

If you like this project and want to support development:
[Buy Me a Coffee ☕](https://buymeacoffee.com/mamba73)

*Developed by [mamba73](https://github.com/mamba73). Feel free to submit issues or pull requests!*
