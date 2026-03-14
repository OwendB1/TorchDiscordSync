# mamba.TorchDiscordSync.Plugin

---
**NOTE: This project is still in active development.** Many features are partially working or require fixes.  
Not everything listed below is fully stable yet – ongoing work.

An advanced Space Engineers Torch server plugin providing deep Discord integration, server automation and administrative tooling.

Unlike simple chat relays, this plugin focuses on **deep game integration**—tracking accurate kill data, analyzing damage sources in real-time, synchronizing player factions, and monitoring server health.

**C#**: 4.6+ / .NET Framework 4.8  
**Torch**: 2.4.37+  
**Space Engineers**: 1.208+  
**Author**: mamba  
**Version**: 2.4.37


---
## 🚀 Project Status: Active development

| Feature | Status | Notes |
| :--- | :---: | :--- |
| **Core Plugin Structure** | ✅ Done | Service injection, Logging, Config handling. |
| **Smart Chat Relay** | ✅ Done | Bi-directional. Ignores private/faction messages. Loop protection. |
| **SimSpeed Monitor** | ✅ Done | Configurable watchdog (def. 30s) alerting admins on lag spikes. |
| **Advanced Killer Detection** | ✅ Done | Identifies weapons, turret owners, collisions, and environmental deaths. |
| **Damage Tracking Buffer** | ✅ Done | Real-time hooking into damage events. |
| **Discord Bot Connection** | ✅ Done | Basic bot connectivity and intent handling. |
| **Data Storage (XML)** | ✅ Done | Current storage solution. |
| **Death Location Classification** | ✅ Done | Planet, orbit & inner, outer, deep space detection. |
| **User Verification** | ✅ Done | Securely linking SteamID to Discord UserID. Steam ID verification with in-game confirmation. |
| **Faction Synchronization** | ✅ Done | Syncing SE Factions to Discord Roles. Automatic role management for faction members. |
| **Secure Faction Chat** | ✅ Done | Private channels for faction comms (Game ↔ Discord). |
| **SQLite Migration** | ✅ Done | First major upcoming milestone!  Critical for high-volume features (Raid Alerts). |

---
## 🌟 Key Features (Implemented)

---
### 💬 Smart Chat Relay
A robust bi-directional chat system that connects your server to a Discord channel.
- **Loop Protection:** Prevents bot messages from echoing back endlessly.
- **Privacy Filters:** Automatically ignores private DMs and internal Faction chat to protect sensitive gameplay info.
- **Bi-Directional:** Discord users can chat with in-game players seamlessly.

---
### 🛡️ Server Health Monitoring
- **SimSpeed Watchdog:** Automatically monitors server simulation speed. If SimSpeed drops below the threshold for a configurable time (default: 30s), it alerts administrators immediately.

---
### 🎯 Advanced Death Analysis
The plugin uses a sophisticated damage tracking system to analyze the exact cause of death:
- **PvP Detection:** Identifies the killer, weapon used, and grids involved.
- **Turret Tracking:** Traces automated turret fire back to the owner (even if offline).
- **Environmental Awareness:** Distinguishes between all enviroment deaths, sample: Collisions, Asphyxiation (LowPressure), and Gravity falls...

#### 🌍 Dynamic Location Classification

The system works as follows:

1. All planets in the world are detected dynamically
   - Supports vanilla and custom planets
   - Planet positions are calculated at runtime

2. Player death position is captured
   - Distance to all planets is calculated
   - Nearest planet is selected

3. Based on planet radius, the system determines:
   - Surface
   - Low Orbit
   - High Orbit

4. If no planet is within range:
   - Config-defined inner and outer space thresholds are used
   - Beyond outer space → Deep Space

This allows accurate classification without hardcoded zones.


### 📝 Death Message Templates

Death messages support exactly four parameters:

- killer
- victim
- weapon
- location

**Example templates:**

*PvP Message:*
```"{killer} showed {victim} no mercy with {weapon}"```
- *Result in game:*
💀 mamba showed orko no mercy with Flare Gun in the vicinity of Titan


*Environment Oxygen Message:*
```"{victim} discovered that space has no oxygen"```
- *Result in game:*
⚡ mamba discovered that space has no oxygen on Moon's surface


*Turret Message*
```"{killer} used {weapon} to send {victim} to eternal hunting grounds"```
- *Result in game:*
⚔️ Space Pirates used Gatling Turret to send mamba to eternal hunting grounds lost in the void


---
## 🗺️ Roadmap & Future Plans

### 1. Secure Faction Chat
Once Faction Sync is complete, the plugin will create private Discord channels restricted to faction members, allowing secure communication between the game faction chat and Discord.

### 2. SQLite Migration (High Priority)
To support high-frequency data logging (such as bullet impacts during heavy PvP) without causing server lag, the storage system will be migrated from XML to SQLite. This is a prerequisite for the Raid Alert system.

### 3. Raid Alerts (Offline Protection)
*Dependency: SQLite Migration*
A system to notify Faction members via Discord DM/Ping when their base is taking damage while they are offline. Requires the performance of SQLite to handle rapid damage events without impacting SimSpeed.

### 4. Leaderboards & Statistics
Weekly or monthly PvP rankings (K/D ratios, Most Active Faction, Top Ace Pilot) generated from the gathered kill data.

### 5. Bounty / Retaliation System
Building on the existing retaliation logic, this feature will allow players to place bounties on others, tracked and announced via Discord.

### 6. Discord Admin Commands
Administrative commands will be accessible directly from Discord, allowing server management from mobile devices without logging into the game.
Planned capabilities:
- Trigger faction sync
- View server status & SimSpeed
- Execute verification actions
- Emergency admin operations

Discord will effectively become a remote admin console.


---
## 🤝 Contributing
Pull requests are welcome.
Please maintain compatibility with C# 4.6+.

---
## ☕ Support
If you like this project and want to support development:
[Buy Me a Coffee ☕](https://buymeacoffee.com/mamba73)

*Project is currently under active development.*
*Developed by [mamba73](https://github.com/mamba73). Feel free to submit issues or pull requests!*