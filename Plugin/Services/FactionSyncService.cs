// Plugin/Services/FactionSyncService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using TorchDiscordSync.Plugin.Config;
using TorchDiscordSync.Plugin.Models;
using TorchDiscordSync.Plugin.Utils;
using VRage.Game.ModAPI;

namespace TorchDiscordSync.Plugin.Services
{
    /// <summary>
    /// Synchronizes Space Engineers factions with Discord roles and channels
    /// Includes faction reading functionality (merged from FactionReaderService)
    /// </summary>
    public class FactionSyncService
    {
        private readonly DatabaseService _db;
        private readonly DiscordService _discord;
        private readonly MainConfig _config;

        // ── Post-undo cooldown ──────────────────────────────────────────
        // After an undo operation Discord.Net's in-memory cache still holds
        // the deleted roles/channels for a few seconds.  Any sync that runs
        // during this window will "find" the deleted items and skip creation.
        // We block syncs for SYNC_COOLDOWN_SECONDS after the last undo.
        private const int SYNC_COOLDOWN_SECONDS = 10;
        private DateTime _lastUndoAt = DateTime.MinValue;

        private bool IsCoolingDown()
        {
            if (_lastUndoAt == DateTime.MinValue) return false;
            var elapsed = (DateTime.UtcNow - _lastUndoAt).TotalSeconds;
            if (elapsed < SYNC_COOLDOWN_SECONDS)
            {
                LoggerUtil.LogInfo(
                    $"[FACTION_SYNC] Skipping sync – post-undo cooldown ({elapsed:F1}s / {SYNC_COOLDOWN_SECONDS}s elapsed). " +
                    "Discord cache may still contain stale deleted items.");
                return true;
            }
            return false;
        }

        public FactionSyncService(DatabaseService db, DiscordService discord, MainConfig config)
        {
            _db = db;
            _discord = discord;
            _config = config;

            LoggerUtil.LogDebug("[FACTION_SYNC] FactionSyncService initialized");
        }

        private async Task DeleteTrackedExtraChannelsAsync(
            FactionModel faction,
            string context,
            System.Text.StringBuilder result = null
        )
        {
            if (faction?.ChannelsCreated == null || faction.ChannelsCreated.Count == 0)
                return;

            var protectedIds = new HashSet<ulong>();
            if (faction.DiscordChannelID > 0)
                protectedIds.Add(faction.DiscordChannelID);
            if (faction.DiscordVoiceChannelID > 0)
                protectedIds.Add(faction.DiscordVoiceChannelID);

            foreach (var trackedChannel in faction.ChannelsCreated)
            {
                if (trackedChannel == null || trackedChannel.ChannelID == 0 || trackedChannel.DeletedOnUndo)
                    continue;

                if (protectedIds.Contains(trackedChannel.ChannelID))
                    continue;

                try
                {
                    var deleted = await _discord.DeleteChannelAsync(trackedChannel.ChannelID).ConfigureAwait(false);
                    trackedChannel.DeletedOnUndo = true;

                    if (deleted)
                    {
                        result?.AppendLine(
                            $"✓ Deleted tracked channel: {trackedChannel.ChannelName} (ID: {trackedChannel.ChannelID})"
                        );
                        LoggerUtil.LogInfo(
                            $"[{context}] Deleted tracked channel {trackedChannel.ChannelName} (ID: {trackedChannel.ChannelID})"
                        );
                    }
                    else
                    {
                        LoggerUtil.LogDebug(
                            $"[{context}] Tracked channel already missing: {trackedChannel.ChannelName} (ID: {trackedChannel.ChannelID})"
                        );
                    }
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogWarning(
                        $"[{context}] Tracked channel delete failed for {trackedChannel.ChannelID}: {ex.Message}"
                    );
                }
            }
        }

        /// <summary>
        /// Loads all player-created factions from the current game session.
        /// Filters out NPC factions and factions with non-standard tags.
        /// </summary>
        public List<FactionModel> LoadFactionsFromGame()
        {
            var factionModels = new List<FactionModel>();

            try
            {
                // Access the faction collection from session
                var factionCollection = MySession.Static.Factions as MyFactionCollection;
                if (factionCollection == null)
                {
                    LoggerUtil.LogWarning(
                        "MySession.Static.Factions is null - cannot load factions"
                    );
                    return factionModels;
                }

                // Get all factions from the game
                var allFactions = factionCollection.GetAllFactions();

                if (allFactions == null || allFactions.Length == 0)
                {
                    LoggerUtil.LogInfo("No factions found in session");
                    return factionModels;
                }

                // Iterate through all factions
                foreach (var faction in allFactions)
                {
                    if (faction == null)
                        continue;

                    // Filter: Only 3-character tags (player factions)
                    if (faction.Tag == null || faction.Tag.Length != 3)
                    {
                        continue;
                    }

                    // Filter: Skip NPC factions
                    if (faction.IsEveryoneNpc())
                    {
                        LoggerUtil.LogDebug($"Skipping NPC faction: {faction.Tag}");
                        continue;
                    }

                    // Create faction model
                    var factionModel = new FactionModel
                    {
                        FactionID = (int)faction.FactionId,
                        Tag = faction.Tag,
                        Name = faction.Name ?? "Unknown",
                    };

                    // Load faction members
                    if (faction.Members.Count > 0)
                    {
                        foreach (var memberKvp in faction.Members)
                        {
                            var playerId = memberKvp.Key;
                            var memberData = memberKvp.Value;

                            // Map playerId to SteamID
                            var steamId = MyAPIGateway.Players.TryGetSteamId(playerId);

                            if (steamId == 0)
                            {
                                LoggerUtil.LogWarning(
                                    $"Cannot get SteamID for playerId {playerId} in faction {faction.Tag}"
                                );
                                continue;
                            }

                            // Get player name
                            var playerName = GetPlayerName(playerId);

                            // Create faction member model
                            var factionPlayer = new FactionPlayerModel
                            {
                                PlayerID = (int)playerId,
                                SteamID = (long)steamId,
                                OriginalNick = playerName,
                                SyncedNick = playerName,
                            };

                            factionModel.Players.Add(factionPlayer);
                        }
                    }

                    factionModels.Add(factionModel);
                    LoggerUtil.LogDebug(
                        $"Loaded faction: {faction.Tag} ({faction.Name}) with {factionModel.Players.Count} members"
                    );
                }

                LoggerUtil.LogInfo(
                    $"Loaded {factionModels.Count} player factions from game session"
                );
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(
                    $"Error loading factions from game: {ex.Message}\n{ex.StackTrace}"
                );
            }

            return factionModels;
        }

        /// <summary>
        /// Synchronize all player factions to Discord.
        /// For each game faction:
        ///  - If it exists in XML, SKIP DB create but CHECK/REPAIR Discord role + channel.
        ///  - If it does not exist in XML, CREATE role + channel and save to XML.
        /// Role name is always 3-char tag (e.g. BLB, sVz).
        /// Channel name is lowercase faction name (e.g. "blind leading blind", "svizac").
        /// </summary>
        public async Task SyncFactionsAsync(List<FactionModel> factions = null)
        {
            // Check if faction sync is enabled in config
            if (_config == null || _config.Faction == null || !_config.Faction.Enabled)
            {
                LoggerUtil.LogDebug("[FACTION_SYNC] Faction sync is disabled in config - skipping");
                return;
            }

            // Post-undo cooldown: Discord.Net cache may still have deleted roles/channels
            if (IsCoolingDown()) return;

            try
            {
                LoggerUtil.LogInfo("[FACTION_SYNC] Starting faction synchronization");

                // If no factions provided, load from game
                if (factions == null || factions.Count == 0)
                {
                    factions = LoadFactionsFromGame();
                }

                if (factions == null || factions.Count == 0)
                {
                    LoggerUtil.LogWarning("[FACTION_SYNC] No factions to synchronize");
                    return;
                }

                // Process each faction - SKIP / CREATE decision per faction
                foreach (var gameFaction in factions)
                {
                    // Skip factions with invalid tag length (should be 3 characters)
                    if (gameFaction.Tag == null || gameFaction.Tag.Length != 3)
                    {
                        LoggerUtil.LogDebug(
                            "[FACTION_SYNC] Skipping faction with invalid tag: " + gameFaction.Tag
                        );
                        continue;
                    }

                    // Look up existing faction record in XML database
                    var existing = _db.GetFaction(gameFaction.FactionID);
                    FactionModel dbFaction;

                    if (existing != null)
                    {
                        // SKIP DB create, but still verify Discord objects
                        dbFaction = existing;
                        LoggerUtil.LogDebug(
                            "[FACTION_SYNC] SKIP DB create for faction "
                                + dbFaction.Tag
                                + " (ID: "
                                + dbFaction.FactionID
                                + ") - already stored in XML, checking Discord objects"
                        );
                    }
                    else
                    {
                        // CREATE new record based on game faction
                        dbFaction = gameFaction;
                        LoggerUtil.LogDebug(
                            "[FACTION_SYNC] CREATE faction "
                                + dbFaction.Tag
                                + " (ID: "
                                + dbFaction.FactionID
                                + ") - not present in XML, creating role/channel and saving"
                        );
                    }

                    // ============================================================
                    // Check if role already exists on Discord
                    // ============================================================
                    if (dbFaction.DiscordRoleID > 0)
                    {
                        var existingRole = _discord.GetExistingRole(dbFaction.DiscordRoleID);
                        if (existingRole != null)
                        {
                            LoggerUtil.LogDebug(
                                "[FACTION_SYNC] Existing Discord role found for "
                                    + dbFaction.Tag
                                    + " (RoleID: "
                                    + dbFaction.DiscordRoleID
                                    + ")"
                            );
                        }
                        else
                        {
                            LoggerUtil.LogDebug(
                                "[FACTION_SYNC] Stored role ID not found on Discord for "
                                    + dbFaction.Tag
                                    + " (RoleID: "
                                    + dbFaction.DiscordRoleID
                                    + ") - will recreate"
                            );
                            dbFaction.DiscordRoleID = 0;
                        }
                    }

                    // ============================================================
                    // Check if channel already exists on Discord
                    // ============================================================
                    if (dbFaction.DiscordChannelID > 0)
                    {
                        var existingChannel = _discord.GetExistingChannel(dbFaction.DiscordChannelID);
                        if (existingChannel != null)
                        {
                            LoggerUtil.LogDebug(
                                "[FACTION_SYNC] Existing Discord channel found for "
                                    + dbFaction.Tag
                                    + " (ChannelID: "
                                    + dbFaction.DiscordChannelID
                                    + ")"
                            );
                        }
                        else
                        {
                            LoggerUtil.LogDebug(
                                "[FACTION_SYNC] Stored channel ID not found on Discord for "
                                    + dbFaction.Tag
                                    + " (ChannelID: "
                                    + dbFaction.DiscordChannelID
                                    + ") - will recreate"
                            );
                            dbFaction.DiscordChannelID = 0;
                        }
                    }

                    // ============================================================
                    // Create role if needed
                    // ============================================================
                    if (dbFaction.DiscordRoleID == 0)
                    {
                        // FIX: Before creating, check if role with same tag already exists
                        // on Discord (prevents duplicates when XML db is missing/empty).
                        var existingRoleId = _discord.FindRoleByName(dbFaction.Tag);
                        if (existingRoleId > 0)
                        {
                            LoggerUtil.LogInfo(
                                "[FACTION_SYNC] Found existing Discord role by name '" + dbFaction.Tag +
                                "' (ID: " + existingRoleId + ") – reusing, skipping create.");
                            dbFaction.DiscordRoleID = existingRoleId;
                            dbFaction.DiscordRoleName = dbFaction.Tag;
                        }
                        else
                        {
                            LoggerUtil.LogDebug(
                                "[FACTION_SYNC] Creating Discord role for faction: " + dbFaction.Tag
                            );
                            dbFaction.DiscordRoleID = await _discord.CreateRoleAsync(dbFaction.Tag).ConfigureAwait(false);

                            if (dbFaction.DiscordRoleID > 0)
                            {
                                dbFaction.DiscordRoleName = dbFaction.Tag;
                                LoggerUtil.LogSuccess(
                                    "[FACTION_SYNC] Created role "
                                        + dbFaction.Tag
                                        + " (ID: "
                                        + dbFaction.DiscordRoleID
                                        + ")"
                                );
                            }
                            else
                            {
                                LoggerUtil.LogWarning(
                                    "[FACTION_SYNC] Failed to create role for " + dbFaction.Tag
                                );
                            }
                        } // end else (role not found by name)
                    }

                    // ============================================================
                    // Create channel if needed
                    // Channel name is lowercase faction name
                    // Pass roleID for permission setup
                    // ============================================================
                    if (dbFaction.DiscordChannelID == 0)
                    {
                        var channelName =
                            (gameFaction.Name != null ? gameFaction.Name : dbFaction.Tag)
                                .ToLower();

                        // FIX: Check if text channel with same name already exists on Discord
                        var existingChannelId = _discord.FindTextChannelByName(channelName);
                        if (existingChannelId > 0)
                        {
                            LoggerUtil.LogInfo(
                                "[FACTION_SYNC] Found existing text channel by name '" + channelName +
                                "' (ID: " + existingChannelId + ") – reusing, skipping create.");
                            dbFaction.DiscordChannelID = existingChannelId;
                            dbFaction.DiscordChannelName = channelName;
                        }
                        else
                        {
                            LoggerUtil.LogDebug(
                                "[FACTION_SYNC] Creating Discord channel for faction: "
                                    + gameFaction.Name
                                    + " (channel: "
                                    + channelName
                                    + ")"
                            );

                            dbFaction.DiscordChannelID = await _discord.CreateChannelAsync(
                            channelName,
                            _config.Discord.FactionCategoryId,
                            dbFaction.DiscordRoleID
                        ).ConfigureAwait(false);

                            if (dbFaction.DiscordChannelID > 0)
                            {
                                dbFaction.DiscordChannelName = channelName;
                                LoggerUtil.LogSuccess(
                                    "[FACTION_SYNC] Created channel "
                                        + channelName
                                        + " (ID: "
                                        + dbFaction.DiscordChannelID
                                        + ")"
                                );
                            }
                            else
                            {
                                LoggerUtil.LogWarning(
                                    "[FACTION_SYNC] Failed to create channel for " + gameFaction.Name
                                );
                            }
                        } // end else (channel not found by name)
                    }

                    // ============================================================
                    // Create voice channels if enabled (same name lowcase, same role)
                    // ============================================================
                    var lowcaseName = (gameFaction.Name != null ? gameFaction.Name : dbFaction.Tag).ToLower();
                    ulong? catId = _config.Discord.FactionCategoryId;
                    var roleId = dbFaction.DiscordRoleID > 0 ? (ulong?)dbFaction.DiscordRoleID : null;

                    if (_config.Faction.AutoCreateVoice)
                    {
                        try
                        {
                            // ============================================================
                            // Validate stored voice ID — must be an actual voice channel type.
                            // A text-channel ID saved as VoiceChannelID (past bug) must be rejected.
                            // ============================================================
                            if (dbFaction.DiscordVoiceChannelID > 0)
                            {
                                var existingVoice = _discord.GetExistingVoiceChannel(dbFaction.DiscordVoiceChannelID);
                                if (existingVoice != null)
                                {
                                    LoggerUtil.LogDebug(
                                        "[FACTION_SYNC] Existing Discord voice confirmed for "
                                            + dbFaction.Tag + " (VoiceID: " + dbFaction.DiscordVoiceChannelID + ")"
                                    );
                                }
                                else
                                {
                                    LoggerUtil.LogInfo(
                                        "[FACTION_SYNC] Stored VoiceID " + dbFaction.DiscordVoiceChannelID +
                                        " not found (or wrong type) on Discord for " + dbFaction.Tag +
                                        " – will search by name or recreate"
                                    );
                                    dbFaction.DiscordVoiceChannelID = 0;
                                    dbFaction.DiscordVoiceChannelName = null;
                                }
                            }

                            if (dbFaction.DiscordVoiceChannelID == 0)
                            {
                                // Fallback 1: check ChannelsCreated list
                                ulong existingVoiceId = 0;
                                if (dbFaction.ChannelsCreated != null)
                                {
                                    var createdVoice = dbFaction.ChannelsCreated
                                        .FirstOrDefault(c => c.ChannelType == "Voice" && !c.DeletedOnUndo);
                                    if (createdVoice != null && createdVoice.ChannelID > 0)
                                    {
                                        var ch = _discord.GetExistingVoiceChannel(createdVoice.ChannelID);
                                        if (ch != null)
                                        {
                                            existingVoiceId = createdVoice.ChannelID;
                                            LoggerUtil.LogInfo(
                                                "[FACTION_SYNC] Found voice from ChannelsCreated: " + createdVoice.ChannelName +
                                                " (ID: " + existingVoiceId + ") – reusing");
                                        }
                                    }
                                }

                                // Fallback 2: search Discord by name
                                if (existingVoiceId == 0)
                                    existingVoiceId = _discord.FindVoiceChannelByName(lowcaseName);

                                if (existingVoiceId > 0)
                                {
                                    LoggerUtil.LogInfo(
                                        "[FACTION_SYNC] Found existing voice channel '" + lowcaseName +
                                        "' (ID: " + existingVoiceId + ") – reusing, skipping create.");
                                    dbFaction.DiscordVoiceChannelID = existingVoiceId;
                                    dbFaction.DiscordVoiceChannelName = lowcaseName;
                                }
                                else
                                {
                                    LoggerUtil.LogDebug(
                                        "[FACTION_SYNC] Creating voice channel for faction: " + lowcaseName);
                                    var voiceId = await _discord.CreateVoiceChannelAsync(lowcaseName, catId, roleId).ConfigureAwait(false);
                                    if (voiceId > 0)
                                    {
                                        dbFaction.DiscordVoiceChannelID = voiceId;
                                        dbFaction.DiscordVoiceChannelName = lowcaseName;
                                        if (dbFaction.ChannelsCreated == null) dbFaction.ChannelsCreated = new List<DiscordChannelCreated>();
                                        dbFaction.ChannelsCreated.Add(new DiscordChannelCreated { ChannelID = voiceId, ChannelName = lowcaseName, ChannelType = "Voice", CreatedAt = DateTime.UtcNow });
                                        LoggerUtil.LogSuccess("[FACTION_SYNC] Created voice: " + lowcaseName + " (ID: " + voiceId + ")");
                                    }
                                    else
                                    {
                                        LoggerUtil.LogWarning("[FACTION_SYNC] Failed to create voice channel for " + lowcaseName);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LoggerUtil.LogError("[FACTION_SYNC] Voice channel creation failed: " + ex.Message);
                        }
                    }

                    // Update sync status metadata
                    if (dbFaction.DiscordRoleID > 0 && dbFaction.DiscordChannelID > 0)
                    {
                        dbFaction.SyncStatus = "Synced";
                        dbFaction.SyncedAt = DateTime.UtcNow;
                        LoggerUtil.LogInfo(
                            "[FACTION_SYNC] Faction ready: "
                                + dbFaction.Tag
                                + " - "
                                + gameFaction.Name
                                + " (Role: "
                                + dbFaction.DiscordRoleID
                                + ", Channel: "
                                + dbFaction.DiscordChannelID
                                + ")"
                        );
                    }

                    // Save faction to database
                    try
                    {
                        _db.SaveFaction(dbFaction);
                        LoggerUtil.LogSuccess($"[FACTION_SYNC] ✓ Saved faction: {dbFaction.Tag}");
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogError($"[FACTION_SYNC] Failed to save faction: {ex.Message}");
                    }

                    // ============================================================
                    // Sync faction members (add them to faction)
                    // Use players from game faction (current session)
                    // ============================================================
                    if (gameFaction.Players != null && gameFaction.Players.Count > 0)
                    {
                        foreach (var player in gameFaction.Players)
                        {
                            // Create synced nickname with faction tag
                            player.SyncedNick = "[" + dbFaction.Tag + "] " + player.OriginalNick;

                            // Create player model for database
                            var playerModel = new PlayerModel
                            {
                                PlayerID = player.PlayerID,
                                SteamID = player.SteamID,
                                OriginalNick = player.OriginalNick,
                                SyncedNick = player.SyncedNick,
                                FactionID = dbFaction.FactionID,
                                DiscordUserID = player.DiscordUserID,
                            };

                            // Save player to database
                            _db.SavePlayer(playerModel);
                        }

                        LoggerUtil.LogDebug(
                            "[FACTION_SYNC] Synced "
                                + gameFaction.Players.Count
                                + " players for "
                                + dbFaction.Tag
                        );
                    }
                }

                LoggerUtil.LogSuccess("[FACTION_SYNC] Synchronization complete");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(
                    "[FACTION_SYNC] Sync error: " + ex.Message + "\n" + ex.StackTrace
                );
            }
        }

        /// <summary>
        /// Reset Discord - delete all roles and channels created by plugin
        /// WARNING: This is a destructive operation!
        /// </summary>
        public async Task ResetDiscordAsync()
        {
            try
            {
                LoggerUtil.LogWarning(
                    "[FACTION_SYNC] Starting Discord reset (DESTRUCTIVE OPERATION)"
                );

                var factions = _db.GetAllFactions();
                if (factions != null && factions.Count > 0)
                {
                    foreach (var faction in factions)
                    {
                        // Delete Discord role
                        if (faction.DiscordRoleID != 0)
                        {
                            await _discord.DeleteRoleAsync(faction.DiscordRoleID).ConfigureAwait(false);
                            LoggerUtil.LogInfo(
                                $"[FACTION_SYNC] Deleted Discord role for: {faction.Tag}"
                            );
                        }

                        if (faction.DiscordChannelID != 0)
                        {
                            await _discord.DeleteChannelAsync(faction.DiscordChannelID).ConfigureAwait(false);
                            LoggerUtil.LogInfo($"[FACTION_SYNC] Deleted Discord channel for: {faction.Name}");
                        }
                        if (faction.DiscordVoiceChannelID != 0)
                        {
                            await _discord.DeleteChannelAsync(faction.DiscordVoiceChannelID).ConfigureAwait(false);
                            LoggerUtil.LogInfo($"[FACTION_SYNC] Deleted voice for: {faction.Name}");
                        }

                        await DeleteTrackedExtraChannelsAsync(faction, "FACTION_SYNC").ConfigureAwait(false);
                    }
                }

                // Clear all local database
                _db.ClearAllData();
                LoggerUtil.LogSuccess("[FACTION_SYNC] Discord reset complete - all data cleared");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[FACTION_SYNC] Reset error: {ex.Message}");
            }
        }

        /// <summary>
        /// NEW: Resync a specific faction by deleting and recreating its Discord channel and role
        /// </summary>
        public async Task ResyncFactionAsync(string factionTag)
        {
            try
            {
                LoggerUtil.LogInfo($"[FACTION_SYNC] Starting resync for faction: {factionTag}");

                // Find faction by tag
                var faction = _db?.GetAllFactions()?.FirstOrDefault(f => f.Tag == factionTag);
                if (faction == null)
                {
                    LoggerUtil.LogError($"[FACTION_SYNC] Faction not found: {factionTag}");
                    throw new Exception($"Faction not found: {factionTag}");
                }

                // Delete existing Discord channel and role
                if (faction.DiscordChannelID != 0 && _discord != null)
                {
                    try
                    {
                        await _discord.DeleteChannelAsync(faction.DiscordChannelID).ConfigureAwait(false);
                        LoggerUtil.LogSuccess(
                            $"[FACTION_SYNC] Deleted old channel for {factionTag}"
                        );
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogWarning(
                            $"[FACTION_SYNC] Error deleting old channel: {ex.Message}"
                        );
                    }
                }

                if (faction.DiscordRoleID != 0 && _discord != null)
                {
                    try
                    {
                        await _discord.DeleteRoleAsync(faction.DiscordRoleID).ConfigureAwait(false);
                        LoggerUtil.LogSuccess($"[FACTION_SYNC] Deleted old role for {factionTag}");
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogWarning(
                            $"[FACTION_SYNC] Error deleting old role: {ex.Message}"
                        );
                    }
                }

                // Recreate faction role
                var newRoleId = await _discord.CreateRoleAsync(factionTag).ConfigureAwait(false);
                if (newRoleId == 0)
                {
                    LoggerUtil.LogError(
                        $"[FACTION_SYNC] Failed to create new role for {factionTag}"
                    );
                    throw new Exception("Failed to create new role");
                }

                // Update faction with new role ID
                faction.DiscordRoleID = newRoleId;

                // Recreate faction channel
                var newChannelId = await _discord.CreateChannelAsync(
                    faction.Name.ToLower().Replace(" ", "-"),
                    _config.Discord.FactionCategoryId
                ).ConfigureAwait(false);

                if (newChannelId == 0)
                {
                    LoggerUtil.LogError(
                        $"[FACTION_SYNC] Failed to create new channel for {factionTag}"
                    );
                    throw new Exception("Failed to create new channel");
                }

                // Update faction with new channel ID
                faction.DiscordChannelID = newChannelId;

                // Save updated faction to database
                _db?.SaveFaction(faction);

                LoggerUtil.LogSuccess(
                    $"[FACTION_SYNC] Resync complete for {factionTag} (Channel: {newChannelId}, Role: {newRoleId})"
                );
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[FACTION_SYNC] Resync failed for {factionTag}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Admin command: /tds admin:sync:check
        /// Check status of all faction syncs
        /// </summary>
        public string AdminSyncCheck()
        {
            try
            {
                LoggerUtil.LogInfo("[ADMIN:SYNC:CHECK] Executed");

                var allFactions = _db.GetAllFactions();
                if (allFactions == null || allFactions.Count == 0)
                {
                    return "No factions in database";
                }

                var result = new System.Text.StringBuilder();
                result.AppendLine("[SYNC STATUS CHECK]");
                result.AppendLine();

                int synced = 0, orphaned = 0, failed = 0, pending = 0;

                foreach (var faction in allFactions)
                {
                    if (faction.SyncStatus == "Synced")
                    {
                        result.AppendLine($"✓ {faction.Tag}: SYNCED");
                        result.AppendLine($"    Role ID: {faction.DiscordRoleID}");
                        result.AppendLine($"    Channel: {faction.DiscordChannelName} (ID: {faction.DiscordChannelID})");
                        result.AppendLine($"    Synced at: {faction.SyncedAt}");
                        synced++;
                    }
                    else if (faction.SyncStatus == "Orphaned")
                    {
                        result.AppendLine($"⚠ {faction.Tag}: ORPHANED - {faction.ErrorMessage}");
                        orphaned++;
                    }
                    else if (faction.SyncStatus == "Failed")
                    {
                        result.AppendLine($"❌ {faction.Tag}: FAILED - {faction.ErrorMessage}");
                        failed++;
                    }
                    else
                    {
                        result.AppendLine($"⏳ {faction.Tag}: PENDING");
                        pending++;
                    }
                }

                result.AppendLine();
                result.AppendLine($"Summary: Synced={synced}, Pending={pending}, Orphaned={orphaned}, Failed={failed}");

                LoggerUtil.LogInfo($"[ADMIN:SYNC:CHECK] Result: {synced} synced, {pending} pending, {orphaned} orphaned, {failed} failed");
                return result.ToString();
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[ADMIN:SYNC:CHECK] Error: {ex.Message}");
                return $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Admin command: /tds admin:sync:undo <faction_tag>
        /// Delete Discord role and channel for a faction
        /// </summary>
        public async Task<string> AdminSyncUndo(string factionTag)
        {
            try
            {
                LoggerUtil.LogWarning($"[ADMIN:SYNC:UNDO] Executing for faction: {factionTag}");

                var faction = _db.GetFactionByTag(factionTag);
                if (faction == null)
                {
                    LoggerUtil.LogError($"[ADMIN:SYNC:UNDO] Faction not found: {factionTag}");
                    return $"Faction '{factionTag}' not found in database";
                }

                var result = new System.Text.StringBuilder();
                result.AppendLine($"[UNDO] {factionTag}");

                await DeleteTrackedExtraChannelsAsync(faction, "ADMIN:SYNC:UNDO", result).ConfigureAwait(false);

                // Delete Discord role
                if (faction.DiscordRoleID > 0)
                {
                    try
                    {
                        var roleDeleted = await _discord.DeleteRoleAsync(faction.DiscordRoleID).ConfigureAwait(false);
                        if (roleDeleted)
                        {
                            result.AppendLine($"✓ Deleted Discord role: {faction.DiscordRoleName}");
                            LoggerUtil.LogSuccess($"[ADMIN:SYNC:UNDO] Deleted role: {faction.DiscordRoleName}");
                            faction.DiscordRoleID = 0;
                            faction.DiscordRoleName = "";
                        }
                        else
                        {
                            result.AppendLine($"⚠ Discord role not found or already deleted: {faction.DiscordRoleName}");
                            LoggerUtil.LogWarning($"[ADMIN:SYNC:UNDO] Role not found: {faction.DiscordRoleName}");
                            faction.DiscordRoleID = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogError($"[ADMIN:SYNC:UNDO] Failed to delete role: {ex.Message}");
                        return $"Failed to delete role: {ex.Message}";
                    }
                }

                // Delete Discord channels (text and voice)
                if (faction.DiscordChannelID > 0)
                {
                    try
                    {
                        var channelDeleted = await _discord.DeleteChannelAsync(faction.DiscordChannelID).ConfigureAwait(false);
                        if (channelDeleted)
                        {
                            result.AppendLine($"✓ Deleted Discord channel: {faction.DiscordChannelName}");
                            LoggerUtil.LogSuccess($"[ADMIN:SYNC:UNDO] Deleted channel: {faction.DiscordChannelName}");
                            faction.DiscordChannelID = 0;
                            faction.DiscordChannelName = "";
                        }
                        else
                        {
                            result.AppendLine($"⚠ Discord channel not found or already deleted: {faction.DiscordChannelName}");
                            LoggerUtil.LogWarning($"[ADMIN:SYNC:UNDO] Channel not found: {faction.DiscordChannelName}");
                            faction.DiscordChannelID = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogError($"[ADMIN:SYNC:UNDO] Failed to delete channel: {ex.Message}");
                        return $"Failed to delete channel: {ex.Message}";
                    }
                }
                if (faction.DiscordVoiceChannelID > 0)
                {
                    try
                    {
                        await _discord.DeleteChannelAsync(faction.DiscordVoiceChannelID).ConfigureAwait(false);
                        result.AppendLine($"✓ Deleted voice: {faction.DiscordVoiceChannelName}");
                        faction.DiscordVoiceChannelID = 0;
                        faction.DiscordVoiceChannelName = "";
                    }
                    catch (Exception ex) { LoggerUtil.LogWarning($"[ADMIN:SYNC:UNDO] Voice delete: {ex.Message}"); faction.DiscordVoiceChannelID = 0; }
                }

                // Remove faction record from XML storage to avoid duplicate syncs on next run
                _db.DeleteFaction(faction.FactionID);
                LoggerUtil.LogDebug(
                    $"[ADMIN:SYNC:UNDO] Removed faction {factionTag} (ID: {faction.FactionID}) from database"
                );

                result.AppendLine($"✓ Undo completed for {factionTag} (role, channel, and DB entry removed)");
                LoggerUtil.LogSuccess($"[ADMIN:SYNC:UNDO] Completed for {factionTag}");

                // Set cooldown so next timer sync waits for Discord cache to clear
                _lastUndoAt = DateTime.UtcNow;
                LoggerUtil.LogInfo($"[FACTION_SYNC] Post-undo cooldown started ({SYNC_COOLDOWN_SECONDS}s) – Discord cache needs time to clear");

                return result.ToString();
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[ADMIN:SYNC:UNDO] Error: {ex.Message}");
                return $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Admin command: /tds admin:sync:cleanup
        /// Delete all orphaned Discord roles and channels
        /// </summary>
        public async Task<string> AdminSyncCleanup()
        {
            try
            {
                LoggerUtil.LogWarning("[ADMIN:SYNC:CLEANUP] Executing cleanup of orphaned syncs");

                var allFactions = _db.GetAllFactions();
                var orphaned = allFactions?.Where(f => f.SyncStatus == "Orphaned").ToList();

                if (orphaned == null || orphaned.Count == 0)
                {
                    LoggerUtil.LogInfo("[ADMIN:SYNC:CLEANUP] No orphaned syncs to clean");
                    return "No orphaned syncs to clean up";
                }

                var result = new System.Text.StringBuilder();
                result.AppendLine($"[CLEANUP] Found {orphaned.Count} orphaned syncs");

                var cleaned = 0;

                foreach (var faction in orphaned)
                {
                    try
                    {
                        // Delete role if exists
                        if (faction.DiscordRoleID > 0)
                        {
                            var deleted = await _discord.DeleteRoleAsync(faction.DiscordRoleID).ConfigureAwait(false);
                            if (deleted)
                            {
                                result.AppendLine($"✓ Deleted orphaned role: {faction.DiscordRoleName}");
                                LoggerUtil.LogSuccess($"[ADMIN:SYNC:CLEANUP] Deleted role: {faction.DiscordRoleName}");
                            }
                        }

                        // Delete channel if exists
                        if (faction.DiscordChannelID > 0)
                        {
                            var deleted = await _discord.DeleteChannelAsync(faction.DiscordChannelID).ConfigureAwait(false);
                            if (deleted)
                            {
                                result.AppendLine($"✓ Deleted orphaned channel: {faction.DiscordChannelName}");
                                LoggerUtil.LogSuccess($"[ADMIN:SYNC:CLEANUP] Deleted channel: {faction.DiscordChannelName}");
                            }
                        }

                        if (faction.DiscordVoiceChannelID > 0)
                        {
                            var deleted = await _discord.DeleteChannelAsync(faction.DiscordVoiceChannelID).ConfigureAwait(false);
                            if (deleted)
                            {
                                result.AppendLine($"✓ Deleted orphaned voice channel: {faction.DiscordVoiceChannelName}");
                                LoggerUtil.LogSuccess($"[ADMIN:SYNC:CLEANUP] Deleted voice: {faction.DiscordVoiceChannelName}");
                            }
                        }

                        await DeleteTrackedExtraChannelsAsync(faction, "ADMIN:SYNC:CLEANUP", result).ConfigureAwait(false);

                        // Reset faction status
                        faction.SyncStatus = "Pending";
                        faction.DiscordRoleID = 0;
                        faction.DiscordChannelID = 0;
                        faction.DiscordVoiceChannelID = 0;
                        faction.DiscordRoleName = "";
                        faction.DiscordChannelName = "";
                        faction.DiscordVoiceChannelName = "";
                        faction.SyncedAt = null;
                        faction.ErrorMessage = "";
                        _db.SaveFaction(faction);

                        cleaned++;
                    }
                    catch (Exception ex)
                    {
                        result.AppendLine($"❌ Failed to clean {faction.Tag}: {ex.Message}");
                        LoggerUtil.LogError($"[ADMIN:SYNC:CLEANUP] Failed to clean {faction.Tag}: {ex.Message}");
                    }
                }

                result.AppendLine($"✓ Cleanup complete: {cleaned} factions cleaned");
                LoggerUtil.LogSuccess($"[ADMIN:SYNC:CLEANUP] Completed: {cleaned} factions");
                return result.ToString();
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[ADMIN:SYNC:CLEANUP] Error: {ex.Message}");
                return $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Admin command: /tds admin:sync:undo_all
        /// Delete Discord roles and channels for ALL factions and clear faction records from XML.
        /// This is similar to a scoped reset only for faction-related data.
        /// </summary>
        public async Task<string> AdminSyncUndoAll()
        {
            try
            {
                LoggerUtil.LogWarning("[ADMIN:SYNC:UNDO_ALL] Executing full faction undo (all factions)");

                var allFactions = _db.GetAllFactions();
                if (allFactions == null || allFactions.Count == 0)
                {
                    LoggerUtil.LogInfo("[ADMIN:SYNC:UNDO_ALL] No factions found in database");
                    return "No factions in database to undo.";
                }

                var result = new System.Text.StringBuilder();
                result.AppendLine("[UNDO_ALL] Starting full faction undo");
                result.AppendLine("Total factions: " + allFactions.Count);

                foreach (var faction in allFactions)
                {
                    result.AppendLine();
                    result.AppendLine("Faction: " + faction.Tag + " (" + faction.Name + ")");

                    // Delete role if exists
                    if (faction.DiscordRoleID > 0)
                    {
                        try
                        {
                            var deletedRole = await _discord.DeleteRoleAsync(faction.DiscordRoleID).ConfigureAwait(false);
                            if (deletedRole)
                            {
                                result.AppendLine("  ✓ Deleted role ID: " + faction.DiscordRoleID);
                                LoggerUtil.LogSuccess(
                                    "[ADMIN:SYNC:UNDO_ALL] Deleted role for faction " + faction.Tag
                                );
                            }
                            else
                            {
                                result.AppendLine(
                                    "  ⚠ Role not found or already deleted (ID: "
                                        + faction.DiscordRoleID
                                        + ")"
                                );
                                LoggerUtil.LogWarning(
                                    "[ADMIN:SYNC:UNDO_ALL] Role not found for faction " + faction.Tag
                                );
                            }
                        }
                        catch (Exception ex)
                        {
                            result.AppendLine(
                                "  ❌ Failed to delete role (ID: "
                                    + faction.DiscordRoleID
                                    + "): "
                                    + ex.Message
                            );
                            LoggerUtil.LogError(
                                "[ADMIN:SYNC:UNDO_ALL] Failed to delete role for "
                                    + faction.Tag
                                    + ": "
                                    + ex.Message
                            );
                        }
                    }
                    else
                    {
                        result.AppendLine("  ℹ No Discord role stored for this faction.");
                    }

                    // Delete channel if exists
                    if (faction.DiscordChannelID > 0)
                    {
                        try
                        {
                            var deletedChannel = await _discord.DeleteChannelAsync(
                                faction.DiscordChannelID
                            ).ConfigureAwait(false);
                            if (deletedChannel)
                            {
                                result.AppendLine(
                                    "  ✓ Deleted channel ID: " + faction.DiscordChannelID
                                );
                                LoggerUtil.LogSuccess(
                                    "[ADMIN:SYNC:UNDO_ALL] Deleted channel for faction " + faction.Tag
                                );
                            }
                            else
                            {
                                result.AppendLine(
                                    "  ⚠ Channel not found or already deleted (ID: "
                                        + faction.DiscordChannelID
                                        + ")"
                                );
                                LoggerUtil.LogWarning(
                                    "[ADMIN:SYNC:UNDO_ALL] Channel not found for faction " + faction.Tag
                                );
                            }
                        }
                        catch (Exception ex)
                        {
                            result.AppendLine(
                                "  ❌ Failed to delete channel (ID: "
                                    + faction.DiscordChannelID
                                    + "): "
                                    + ex.Message
                            );
                            LoggerUtil.LogError(
                                "[ADMIN:SYNC:UNDO_ALL] Failed to delete channel for "
                                    + faction.Tag
                                    + ": "
                                    + ex.Message
                            );
                        }
                    }
                    else
                    {
                        result.AppendLine("  ℹ No Discord channel stored for this faction.");
                    }

                    if (faction.DiscordVoiceChannelID > 0)
                    {
                        try
                        {
                            var deletedVoice = await _discord.DeleteChannelAsync(
                                faction.DiscordVoiceChannelID
                            ).ConfigureAwait(false);
                            if (deletedVoice)
                            {
                                result.AppendLine(
                                    "  ✓ Deleted voice channel ID: " + faction.DiscordVoiceChannelID
                                );
                                LoggerUtil.LogSuccess(
                                    "[ADMIN:SYNC:UNDO_ALL] Deleted voice channel for faction " + faction.Tag
                                );
                            }
                            else
                            {
                                result.AppendLine(
                                    "  ⚠ Voice channel not found or already deleted (ID: "
                                        + faction.DiscordVoiceChannelID
                                        + ")"
                                );
                                LoggerUtil.LogWarning(
                                    "[ADMIN:SYNC:UNDO_ALL] Voice channel not found for faction " + faction.Tag
                                );
                            }
                        }
                        catch (Exception ex)
                        {
                            result.AppendLine(
                                "  ❌ Failed to delete voice channel (ID: "
                                    + faction.DiscordVoiceChannelID
                                    + "): "
                                    + ex.Message
                            );
                            LoggerUtil.LogError(
                                "[ADMIN:SYNC:UNDO_ALL] Failed to delete voice channel for "
                                    + faction.Tag
                                    + ": "
                                    + ex.Message
                            );
                        }
                    }
                    else
                    {
                        result.AppendLine("  ℹ No Discord voice channel stored for this faction.");
                    }

                    await DeleteTrackedExtraChannelsAsync(faction, "ADMIN:SYNC:UNDO_ALL", result).ConfigureAwait(false);

                    // Finally, remove faction record from XML
                    _db.DeleteFaction(faction.FactionID);
                    result.AppendLine(
                        "  ✓ Removed faction record from XML (ID: " + faction.FactionID + ")"
                    );
                    LoggerUtil.LogDebug(
                        "[ADMIN:SYNC:UNDO_ALL] Removed faction "
                            + faction.Tag
                            + " (ID: "
                            + faction.FactionID
                            + ") from database"
                    );
                }

                result.AppendLine();
                result.AppendLine("[UNDO_ALL] Completed for all factions.");
                LoggerUtil.LogSuccess("[ADMIN:SYNC:UNDO_ALL] Completed full faction undo.");

                _lastUndoAt = DateTime.UtcNow;
                LoggerUtil.LogInfo($"[FACTION_SYNC] Post-undo cooldown started ({SYNC_COOLDOWN_SECONDS}s)");

                return result.ToString();
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[ADMIN:SYNC:UNDO_ALL] Error: " + ex.Message);
                return "Error: " + ex.Message;
            }
        }

        /// <summary>
        /// Admin command: /tds admin:sync:status
        /// Show summary of sync status
        /// </summary>
        public string AdminSyncStatus()
        {
            try
            {
                LoggerUtil.LogInfo("[ADMIN:SYNC:STATUS] Executed");

                var allFactions = _db.GetAllFactions();
                if (allFactions == null || allFactions.Count == 0)
                {
                    return "No factions in database";
                }

                var synced = allFactions.Count(f => f.SyncStatus == "Synced");
                var pending = allFactions.Count(f => f.SyncStatus == "Pending");
                var failed = allFactions.Count(f => f.SyncStatus == "Failed");
                var orphaned = allFactions.Count(f => f.SyncStatus == "Orphaned");
                var total = allFactions.Count;

                var status = $"Sync Status: {synced}/{total} synced | {pending} pending | {failed} failed | {orphaned} orphaned";

                LoggerUtil.LogInfo($"[ADMIN:SYNC:STATUS] {status}");
                return status;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[ADMIN:SYNC:STATUS] Error: {ex.Message}");
                return $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Retrieves the display name for a player by their identity ID.
        /// </summary>
        private string GetPlayerName(long playerId)
        {
            try
            {
                var identity = MySession.Static.Players.TryGetIdentity(playerId);
                return identity?.DisplayName ?? "Unknown";
            }
            catch (Exception ex)
            {
                LoggerUtil.LogWarning($"Error getting player name for ID {playerId}: {ex.Message}");
                return "Unknown";
            }
        }
    }
}
