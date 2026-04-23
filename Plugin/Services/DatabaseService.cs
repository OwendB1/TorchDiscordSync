// Plugin/Services/DatabaseService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using TorchDiscordSync.Plugin.Config;
using TorchDiscordSync.Plugin.Models;
using TorchDiscordSync.Plugin.Utils;

namespace TorchDiscordSync.Plugin.Services
{
    public class DatabaseService
    {
        // ============================================================
        // SQLITE PRIMARY (optional, controlled by config UseSQLite)
        // ============================================================
        private SqliteDatabaseService _sqlite;
        private bool _usingSQLite = false;

        // VerificationData.xml - only verification events (history). No duplicate of VerificationPlayers.xml.
        private readonly string _verificationDataPath;
        private VerificationDataModel _verificationData;
        private readonly object _lock = new object();

        // Separate data files (wrappers: FactionDataModel, PlayerDataModel, EventDataModel, ChatDataModel)
        private readonly string _factionDataPath;
        private readonly string _playerDataPath;
        private readonly string _eventDataPath;
        private readonly string _chatDataPath;
        private FactionDataModel _factionData;
        private PlayerDataModel _playerData;
        private EventDataModel _eventData;
        private ChatDataModel _chatData;

        // ============================================================
        // VERIFICATIONPLAYERS.XML FIELDS
        // ============================================================
        private readonly string _verificationPlayersPath;
        private VerificationPlayersData _verificationPlayersData;

        /// <summary>
        /// Init database service. When config.DataStorage.UseSQLite=true (default), SQLite is used as
        /// primary storage. XML files are always loaded as fallback in case SQLite fails.
        /// </summary>
        public DatabaseService(string configPath = null)
        {
            string dataDir = MainConfig.GetDataDirectory();

            _verificationDataPath = Path.Combine(dataDir, "VerificationData.xml");
            _factionDataPath = Path.Combine(dataDir, "FactionData.xml");
            _playerDataPath = Path.Combine(dataDir, "PlayerData.xml");
            _eventDataPath = Path.Combine(dataDir, "EventData.xml");
            _chatDataPath = Path.Combine(dataDir, "ChatData.xml");

            _verificationData = new VerificationDataModel();
            _factionData = new FactionDataModel();
            _playerData = new PlayerDataModel();
            _eventData = new EventDataModel();
            _chatData = new ChatDataModel();

            // Always load XML (used as fallback / migration source)
            LoadFactionDataFromXml();
            LoadPlayerDataFromXml();
            LoadEventDataFromXml();
            LoadChatDataFromXml();
            LoadVerificationDataFromXml();

            _verificationPlayersPath = Path.Combine(dataDir, "VerificationPlayers.xml");
            LoadVerificationPlayersFromXml();

            // ============================================================
            // SQLITE INITIALIZATION
            // ============================================================
            var cfg = MainConfig.Load();
            bool sqliteEnabled = cfg?.DataStorage?.UseSQLite ?? true;

            if (sqliteEnabled)
            {
                LoggerUtil.LogDebug("[DB] SQLite is ENABLED in config (UseSQLite=true). Initializing...");
                try
                {
                    _sqlite = new SqliteDatabaseService(dataDir);
                    _usingSQLite = true;
                    LoggerUtil.LogSuccess("[DB] SQLite initialized successfully – using SQLite as PRIMARY database.");
                    LoggerUtil.LogDebug("[DB] XML files remain available as fallback storage.");
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogError($"[DB] SQLite initialization FAILED – falling back to XML storage. Error: {ex.Message}");
                    _sqlite = null;
                    _usingSQLite = false;
                }
            }
            else
            {
                LoggerUtil.LogInfo("[DB] SQLite is DISABLED in config (UseSQLite=false) – using XML storage.");
            }
        }

        // ============================================================
        // LOAD / SAVE PER-FILE
        // ============================================================

        private void LoadFactionDataFromXml()
        {
            try
            {
                if (File.Exists(_factionDataPath))
                {
                    var serializer = new XmlSerializer(typeof(FactionDataModel));
                    using (var fs = new FileStream(_factionDataPath, FileMode.Open))
                        _factionData = (FactionDataModel)serializer.Deserialize(fs);
                    if (_factionData?.Factions == null) _factionData = new FactionDataModel();
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DB] Failed to load FactionData.xml: {ex.Message}");
                _factionData = new FactionDataModel();
            }
        }

        private void LoadPlayerDataFromXml()
        {
            try
            {
                if (File.Exists(_playerDataPath))
                {
                    var serializer = new XmlSerializer(typeof(PlayerDataModel));
                    using (var fs = new FileStream(_playerDataPath, FileMode.Open))
                        _playerData = (PlayerDataModel)serializer.Deserialize(fs);
                    if (_playerData?.Players == null) _playerData = new PlayerDataModel();
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DB] Failed to load PlayerData.xml: {ex.Message}");
                _playerData = new PlayerDataModel();
            }
        }

        private void LoadEventDataFromXml()
        {
            try
            {
                if (File.Exists(_eventDataPath))
                {
                    var serializer = new XmlSerializer(typeof(EventDataModel));
                    using (var fs = new FileStream(_eventDataPath, FileMode.Open))
                        _eventData = (EventDataModel)serializer.Deserialize(fs);
                    if (_eventData == null) _eventData = new EventDataModel();
                    if (_eventData.EventLogs == null) _eventData.EventLogs = new List<EventLogModel>();
                    if (_eventData.DeathHistory == null) _eventData.DeathHistory = new List<DeathHistoryModel>();
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DB] Failed to load EventData.xml: {ex.Message}");
                _eventData = new EventDataModel();
            }
        }

        private void LoadChatDataFromXml()
        {
            try
            {
                if (File.Exists(_chatDataPath))
                {
                    var serializer = new XmlSerializer(typeof(ChatDataModel));
                    using (var fs = new FileStream(_chatDataPath, FileMode.Open))
                        _chatData = (ChatDataModel)serializer.Deserialize(fs);
                    if (_chatData == null) _chatData = new ChatDataModel();
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DB] Failed to load ChatData.xml: {ex.Message}");
                _chatData = new ChatDataModel();
            }
        }

        /// <summary>
        /// Loads VerificationData.xml (verification events only). If legacy MambaTorchDiscordSyncData.xml exists, migrates to separate files.
        /// Verification state (pending/verified) is only in VerificationPlayers.xml - no duplicates.
        /// </summary>
        private void LoadVerificationDataFromXml()
        {
            string legacyPath = Path.Combine(Path.GetDirectoryName(_verificationDataPath), "MambaTorchDiscordSyncData.xml");
            if (File.Exists(legacyPath))
            {
                try
                {
                    var legacySerializer = new XmlSerializer(typeof(LegacyRootDataModel));
                    using (var fs = new FileStream(legacyPath, FileMode.Open))
                    {
                        var legacy = (LegacyRootDataModel)legacySerializer.Deserialize(fs);
                        if (legacy != null)
                        {
                            bool hadLegacyData = (legacy.Factions?.Count ?? 0) > 0 || (legacy.Players?.Count ?? 0) > 0
                                || (legacy.EventLogs?.Count ?? 0) > 0 || (legacy.DeathHistory?.Count ?? 0) > 0;
                            if (hadLegacyData)
                            {
                                if (legacy.Factions?.Count > 0) _factionData.Factions = new List<FactionModel>(legacy.Factions);
                                if (legacy.Players?.Count > 0) _playerData.Players = new List<PlayerModel>(legacy.Players);
                                if (legacy.EventLogs?.Count > 0) _eventData.EventLogs = new List<EventLogModel>(legacy.EventLogs);
                                if (legacy.DeathHistory?.Count > 0) _eventData.DeathHistory = new List<DeathHistoryModel>(legacy.DeathHistory);
                                SaveFactionDataToXml();
                                SavePlayerDataToXml();
                                SaveEventDataToXml();
                                LoggerUtil.LogInfo("[DB] Migrated legacy MambaTorchDiscordSyncData.xml to FactionData.xml, PlayerData.xml, EventData.xml");
                            }
                            // Only verification events go to VerificationData.xml (no Verifications - that would duplicate VerificationPlayers.xml)
                            if (legacy.VerificationHistory?.Count > 0)
                            {
                                _verificationData.VerificationHistory = new List<VerificationHistoryModel>(legacy.VerificationHistory);
                                SaveVerificationDataToXml();
                            }
                            LoggerUtil.LogInfo("[DB] Migrated verification history to VerificationData.xml");
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogWarning($"[DB] Legacy migration skipped: {ex.Message}");
                }
            }

            if (!File.Exists(_verificationDataPath))
                return;
            try
            {
                var serializer = new XmlSerializer(typeof(VerificationDataModel));
                using (var fs = new FileStream(_verificationDataPath, FileMode.Open))
                {
                    _verificationData = (VerificationDataModel)serializer.Deserialize(fs);
                    if (_verificationData == null) _verificationData = new VerificationDataModel();
                    if (_verificationData.VerificationHistory == null) _verificationData.VerificationHistory = new List<VerificationHistoryModel>();
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DB] Failed to load VerificationData.xml: {ex.Message}");
                _verificationData = new VerificationDataModel();
            }
        }

        private void SaveFactionDataToXml()
        {
            try
            {
                var serializer = new XmlSerializer(typeof(FactionDataModel));
                using (var fs = new FileStream(_factionDataPath, FileMode.Create))
                    serializer.Serialize(fs, _factionData);
            }
            catch (Exception ex) { LoggerUtil.LogError($"[DB] Failed to save FactionData.xml: {ex.Message}"); }
        }

        private void SavePlayerDataToXml()
        {
            try
            {
                var serializer = new XmlSerializer(typeof(PlayerDataModel));
                using (var fs = new FileStream(_playerDataPath, FileMode.Create))
                    serializer.Serialize(fs, _playerData);
            }
            catch (Exception ex) { LoggerUtil.LogError($"[DB] Failed to save PlayerData.xml: {ex.Message}"); }
        }

        private void SaveEventDataToXml()
        {
            try
            {
                var serializer = new XmlSerializer(typeof(EventDataModel));
                using (var fs = new FileStream(_eventDataPath, FileMode.Create))
                    serializer.Serialize(fs, _eventData);
            }
            catch (Exception ex) { LoggerUtil.LogError($"[DB] Failed to save EventData.xml: {ex.Message}"); }
        }

        private void SaveChatDataToXml()
        {
            try
            {
                var serializer = new XmlSerializer(typeof(ChatDataModel));
                using (var fs = new FileStream(_chatDataPath, FileMode.Create))
                    serializer.Serialize(fs, _chatData);
            }
            catch (Exception ex) { LoggerUtil.LogError($"[DB] Failed to save ChatData.xml: {ex.Message}"); }
        }

        private void SaveVerificationDataToXml()
        {
            try
            {
                var serializer = new XmlSerializer(typeof(VerificationDataModel));
                using (var fs = new FileStream(_verificationDataPath, FileMode.Create))
                    serializer.Serialize(fs, _verificationData);
            }
            catch (Exception ex) { LoggerUtil.LogError($"[DB] Failed to save VerificationData.xml: {ex.Message}"); }
        }

        /// <summary>
        /// Saves all data to XML files. FactionData and PlayerData always; EventData/ChatData only if enabled in config; VerificationData always.
        /// </summary>
        public void SaveToXml()
        {
            lock (_lock)
            {
                SaveFactionDataToXml();
                SavePlayerDataToXml();
                var cfg = MainConfig.Load();
                if (cfg?.DataStorage != null)
                {
                    if (cfg.DataStorage.SaveEventLogs || cfg.DataStorage.SaveDeathHistory)
                        SaveEventDataToXml();
                    if (cfg.DataStorage.SaveGlobalChat || cfg.DataStorage.SaveFactionChat || cfg.DataStorage.SavePrivateChat)
                        SaveChatDataToXml();
                }
                SaveVerificationDataToXml();
            }
        }

        /// <summary>
        /// Saves or updates a faction in the database. Routes to SQLite (primary) or XML (fallback).
        /// </summary>
        public void SaveFaction(FactionModel faction)
        {
            if (_usingSQLite)
            {
                try
                {
                    _sqlite.SaveFaction(faction);
                    return;
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogError($"[DB] SQLite SaveFaction failed, falling back to XML: {ex.Message}");
                }
            }

            lock (_lock)
            {
                var existing = _factionData.Factions.FirstOrDefault(f => f.FactionID == faction.FactionID);
                if (existing != null)
                {
                    existing.Tag = faction.Tag;
                    existing.Name = faction.Name;
                    existing.DiscordRoleID = faction.DiscordRoleID;
                    existing.DiscordRoleName = faction.DiscordRoleName;
                    existing.DiscordChannelID = faction.DiscordChannelID;
                    existing.DiscordChannelName = faction.DiscordChannelName;
                    existing.DiscordVoiceChannelID = faction.DiscordVoiceChannelID;
                    existing.DiscordVoiceChannelName = faction.DiscordVoiceChannelName;
                    existing.GameFactionChatId = faction.GameFactionChatId;
                    existing.SyncStatus = faction.SyncStatus;
                    existing.SyncedAt = faction.SyncedAt;
                    existing.SyncedBy = faction.SyncedBy;
                    existing.ErrorMessage = faction.ErrorMessage;
                    existing.ChannelsCreated = faction.ChannelsCreated;
                    existing.Players = faction.Players;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    faction.CreatedAt = DateTime.UtcNow;
                    faction.UpdatedAt = DateTime.UtcNow;
                    _factionData.Factions.Add(faction);
                }
                SaveToXml();
            }
        }

        /// <summary>
        /// Retrieves a faction by its ID. Returns null if not found.
        /// </summary>
        public FactionModel GetFaction(int factionID)
        {
            if (_usingSQLite)
            {
                try { return _sqlite.GetFaction(factionID); }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite GetFaction fallback: {ex.Message}"); }
            }
            return _factionData.Factions.FirstOrDefault(f => f.FactionID == factionID);
        }

        /// <summary>
        /// Check if faction exists in database by FactionID.
        /// </summary>
        public bool FactionExists(int factionID)
        {
            if (_usingSQLite)
            {
                try { return _sqlite.FactionExists(factionID); }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite FactionExists fallback: {ex.Message}"); }
            }
            lock (_lock)
            {
                return _factionData.Factions.Any(f => f.FactionID == factionID);
            }
        }

        public List<FactionModel> GetAllFactions()
        {
            if (_usingSQLite)
            {
                try { return _sqlite.GetAllFactions(); }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite GetAllFactions fallback: {ex.Message}"); }
            }
            return new List<FactionModel>(_factionData.Factions);
        }

        /// <summary>
        /// Delete a faction from the database by its ID.
        /// </summary>
        public void DeleteFaction(int factionID)
        {
            if (_usingSQLite)
            {
                try
                {
                    _sqlite.DeleteFaction(factionID);
                    return;
                }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite DeleteFaction fallback: {ex.Message}"); }
            }
            lock (_lock)
            {
                _factionData.Factions.RemoveAll(f => f.FactionID == factionID);
                SaveToXml();
            }
        }

        /// <summary>
        /// Retrieves a faction by its tag. Returns null if not found.
        /// </summary>
        public FactionModel GetFactionByTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            if (_usingSQLite)
            {
                try { return _sqlite.GetFactionByTag(tag); }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite GetFactionByTag fallback: {ex.Message}"); }
            }
            lock (_lock)
            {
                return _factionData.Factions.FirstOrDefault(f => f.Tag == tag);
            }
        }

        /// <summary>
        /// Get faction by game/Torch faction chat channel ID.
        /// </summary>
        public FactionModel GetFactionByGameChatId(long gameFactionChatId)
        {
            if (gameFactionChatId == 0) return null;
            if (_usingSQLite)
            {
                try { return _sqlite.GetFactionByGameChatId(gameFactionChatId); }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite GetFactionByGameChatId fallback: {ex.Message}"); }
            }
            lock (_lock)
            {
                return _factionData.Factions.FirstOrDefault(f => f.GameFactionChatId == gameFactionChatId);
            }
        }

        /// <summary>
        /// Saves or updates a player in the database. Routes to SQLite (primary) or XML (fallback).
        /// </summary>
        public void SavePlayer(PlayerModel player)
        {
            if (_usingSQLite)
            {
                try
                {
                    _sqlite.SavePlayer(player);
                    return;
                }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite SavePlayer fallback: {ex.Message}"); }
            }

            lock (_lock)
            {
                var existing = _playerData.Players.FirstOrDefault(p => p.SteamID == player.SteamID);
                if (existing != null)
                {
                    existing.OriginalNick = player.OriginalNick;
                    existing.SyncedNick = player.SyncedNick;
                    existing.SteamID = player.SteamID;
                    existing.FactionID = player.FactionID;
                    existing.DiscordUserID = player.DiscordUserID;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    player.CreatedAt = DateTime.UtcNow;
                    player.UpdatedAt = DateTime.UtcNow;
                    _playerData.Players.Add(player);
                }
                SaveToXml();
            }
        }

        /// <summary>
        /// Retrieves a player by their SteamID. Returns null if not found.
        /// </summary>
        public PlayerModel GetPlayerBySteamID(long steamID)
        {
            if (_usingSQLite)
            {
                try { return _sqlite.GetPlayerBySteamID(steamID); }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite GetPlayerBySteamID fallback: {ex.Message}"); }
            }
            return _playerData.Players.FirstOrDefault(p => p.SteamID == steamID);
        }

        public void LogEvent(EventLogModel evt)
        {
            if (_usingSQLite)
            {
                try { _sqlite.LogEvent(evt); return; }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite LogEvent fallback: {ex.Message}"); }
            }
            lock (_lock)
            {
                _eventData.EventLogs.Add(evt);
                SaveToXml();
            }
        }

        /// <summary>
        /// Logs a player death event to the database.
        /// </summary>
        public void LogDeath(
            long killerSteamID,
            long victimSteamID,
            string deathType,
            string weapon = null,
            string location = null
        )
        {
            if (_usingSQLite)
            {
                try { _sqlite.LogDeath(killerSteamID, victimSteamID, deathType, weapon, location); return; }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite LogDeath fallback: {ex.Message}"); }
            }
            lock (_lock)
            {
                var entry = new DeathHistoryModel
                {
                    KillerSteamID = killerSteamID,
                    VictimSteamID = victimSteamID,
                    DeathTime = DateTime.UtcNow,
                    DeathType = deathType,
                    Weapon = weapon,
                    Location = location,
                };
                _eventData.DeathHistory.Add(entry);
                SaveToXml();
            }
        }

        public DeathHistoryModel GetLastKill(long killerSteamID, long victimSteamID)
        {
            if (_usingSQLite)
            {
                try { return _sqlite.GetLastKill(killerSteamID, victimSteamID); }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite GetLastKill fallback: {ex.Message}"); }
            }
            return _eventData
                .DeathHistory.Where(d =>
                    d.KillerSteamID == killerSteamID && d.VictimSteamID == victimSteamID
                )
                .OrderByDescending(d => d.DeathTime)
                .FirstOrDefault();
        }

        public void ClearAllData()
        {
            if (_usingSQLite)
            {
                try
                {
                    _sqlite.ClearAllData();
                    // Also clear XML in-memory + files for consistency
                }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite ClearAllData fallback: {ex.Message}"); }
            }
            lock (_lock)
            {
                _factionData = new FactionDataModel();
                _playerData = new PlayerDataModel();
                _eventData = new EventDataModel();
                _chatData = new ChatDataModel();
                _verificationData = new VerificationDataModel();
                SaveToXml();
            }
        }

        // ============================================================
        // VERIFICATION EVENTS - VerificationData.xml (no duplicate with VerificationPlayers.xml)
        // ============================================================
        /// <summary>
        /// Saves verification event. Routes to SQLite (primary) or VerificationData.xml (fallback).
        /// </summary>
        public void SaveVerificationHistory(VerificationHistoryModel entry)
        {
            if (_usingSQLite)
            {
                try { _sqlite.SaveVerificationHistory(entry); return; }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite SaveVerificationHistory fallback: {ex.Message}"); }
            }
            lock (_lock)
            {
                if (entry == null) return;
                var cutoff = entry.VerifiedAt.AddSeconds(-1);
                bool duplicate = _verificationData.VerificationHistory.Any(v =>
                    v.SteamID == entry.SteamID && v.VerifiedAt >= cutoff && v.VerifiedAt <= entry.VerifiedAt.AddSeconds(1));
                if (!duplicate)
                {
                    _verificationData.VerificationHistory.Add(entry);
                    SaveVerificationDataToXml();
                }
            }
        }

        public List<VerificationHistoryModel> GetVerificationHistory(long steamID)
        {
            if (_usingSQLite)
            {
                try { return _sqlite.GetVerificationHistory(steamID); }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite GetVerificationHistory fallback: {ex.Message}"); }
            }
            return _verificationData
                .VerificationHistory.Where(v => v.SteamID == steamID)
                .OrderByDescending(v => v.VerifiedAt)
                .ToList();
        }

        // ============================================================
        // JAVNE METODE ZA VERIFICATIONPLAYERS.XML
        // ============================================================

        private void LoadVerificationPlayersFromXml()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_verificationPlayersPath))
                    {
                        var serializer = new XmlSerializer(typeof(VerificationPlayersData));
                        using (var stream = new FileStream(_verificationPlayersPath, FileMode.Open))
                        {
                            _verificationPlayersData = (VerificationPlayersData)
                                serializer.Deserialize(stream);
                        }
                        LoggerUtil.LogSuccess(
                            $"[DB] Loaded VerificationPlayers.xml - {_verificationPlayersData.PendingVerifications.Count} pending, {_verificationPlayersData.VerifiedPlayers.Count} verified"
                        );
                    }
                    else
                    {
                        _verificationPlayersData = new VerificationPlayersData();
                        SaveVerificationPlayersToXml();
                        LoggerUtil.LogInfo("[DB] Created new VerificationPlayers.xml");
                    }
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogError(
                        $"[DB] Error loading VerificationPlayers.xml: {ex.Message}"
                    );
                    _verificationPlayersData = new VerificationPlayersData();
                }
            }
        }

        private void SaveVerificationPlayersToXml()
        {
            lock (_lock)
            {
                try
                {
                    var serializer = new XmlSerializer(typeof(VerificationPlayersData));
                    using (var stream = new FileStream(_verificationPlayersPath, FileMode.Create))
                    {
                        serializer.Serialize(stream, _verificationPlayersData);
                    }
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogError($"[DB] Error saving VerificationPlayers.xml: {ex.Message}");
                }
            }
        }

        public void AddPendingVerification(
            long steamID,
            string discordUsername,
            string verificationCode,
            int expirationMinutes,
            string gamePlayerName = null
        )
        {
            if (_usingSQLite)
            {
                try
                {
                    _sqlite.AddPendingVerification(steamID, discordUsername, verificationCode, expirationMinutes, gamePlayerName);
                    return;
                }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite AddPendingVerification fallback: {ex.Message}"); }
            }
            lock (_lock)
            {
                // Ukloni ako već postoji
                _verificationPlayersData.PendingVerifications.RemoveAll(p => p.SteamID == steamID);

                var pending = new PendingVerification
                {
                    SteamID = steamID,
                    DiscordUsername = discordUsername,
                    VerificationCode = verificationCode,
                    CodeGeneratedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(expirationMinutes),
                    GamePlayerName = gamePlayerName,
                };

                _verificationPlayersData.PendingVerifications.Add(pending);
                SaveVerificationPlayersToXml();
                LoggerUtil.LogDebug(
                    $"[DB] Added pending verification for SteamID {steamID} (PlayerName: {gamePlayerName})"
                );
            }
        }

        public void MarkAsVerified(
            long steamID,
            string discordUsername,
            ulong discordUserID,
            string gamePlayerName = null
        )
        {
            if (_usingSQLite)
            {
                try
                {
                    _sqlite.MarkAsVerified(steamID, discordUsername, discordUserID, gamePlayerName);
                    return;
                }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite MarkAsVerified fallback: {ex.Message}"); }
            }
            lock (_lock)
            {
                // Get player name from pending verification if not provided
                string playerNameToSave = gamePlayerName;
                if (string.IsNullOrEmpty(playerNameToSave))
                {
                    var pending = _verificationPlayersData.PendingVerifications.Find(p =>
                        p.SteamID == steamID
                    );
                    if (pending != null && !string.IsNullOrEmpty(pending.GamePlayerName))
                    {
                        playerNameToSave = pending.GamePlayerName;
                    }
                }

                // Ukloni sa pending liste
                _verificationPlayersData.PendingVerifications.RemoveAll(p => p.SteamID == steamID);

                // Ukloni ako postoji na verified listi
                _verificationPlayersData.VerifiedPlayers.RemoveAll(v => v.SteamID == steamID);

                // Dodaj na verified listu
                var verified = new VerifiedPlayer
                {
                    SteamID = steamID,
                    DiscordUsername = discordUsername,
                    DiscordUserID = discordUserID,
                    VerifiedAt = DateTime.UtcNow,
                    GamePlayerName = playerNameToSave,
                };

                _verificationPlayersData.VerifiedPlayers.Add(verified);
                SaveVerificationPlayersToXml();
                LoggerUtil.LogDebug(
                    $"[DB] Marked SteamID {steamID} as verified (PlayerName: {playerNameToSave})"
                );
            }
        }

        public PendingVerification GetPendingVerification(long steamID)
        {
            if (_usingSQLite)
            {
                try { return _sqlite.GetPendingVerification(steamID); }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite GetPendingVerification fallback: {ex.Message}"); }
            }
            lock (_lock)
            {
                var pending = _verificationPlayersData.PendingVerifications.Find(p =>
                    p.SteamID == steamID
                );

                // Ako je expired, obriši
                if (pending != null && pending.ExpiresAt < DateTime.UtcNow)
                {
                    _verificationPlayersData.PendingVerifications.Remove(pending);
                    SaveVerificationPlayersToXml();
                    return null;
                }

                return pending;
            }
        }

        public VerifiedPlayer GetVerifiedPlayer(long steamID)
        {
            if (_usingSQLite)
            {
                try { return _sqlite.GetVerifiedPlayer(steamID); }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite GetVerifiedPlayer fallback: {ex.Message}"); }
            }
            lock (_lock)
            {
                return _verificationPlayersData.VerifiedPlayers.Find(v => v.SteamID == steamID);
            }
        }

        public VerifiedPlayer GetVerifiedPlayerByDiscordID(ulong discordUserID)
        {
            if (_usingSQLite)
            {
                try { return _sqlite.GetVerifiedPlayerByDiscordID(discordUserID); }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite GetVerifiedPlayerByDiscordID fallback: {ex.Message}"); }
            }
            lock (_lock)
            {
                return _verificationPlayersData.VerifiedPlayers.Find(v =>
                    v.DiscordUserID == discordUserID
                );
            }
        }

        public List<PendingVerification> GetAllPendingVerifications()
        {
            if (_usingSQLite)
            {
                try { return _sqlite.GetAllPendingVerifications(); }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite GetAllPendingVerifications fallback: {ex.Message}"); }
            }
            lock (_lock)
            {
                return new List<PendingVerification>(_verificationPlayersData.PendingVerifications);
            }
        }

        public List<VerifiedPlayer> GetAllVerifiedPlayers()
        {
            if (_usingSQLite)
            {
                try { return _sqlite.GetAllVerifiedPlayers(); }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite GetAllVerifiedPlayers fallback: {ex.Message}"); }
            }
            lock (_lock)
            {
                return new List<VerifiedPlayer>(_verificationPlayersData.VerifiedPlayers);
            }
        }

        public void DeletePendingVerification(long steamID)
        {
            if (_usingSQLite)
            {
                try { _sqlite.DeletePendingVerification(steamID); return; }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite DeletePendingVerification fallback: {ex.Message}"); }
            }
            lock (_lock)
            {
                _verificationPlayersData.PendingVerifications.RemoveAll(p => p.SteamID == steamID);
                SaveVerificationPlayersToXml();
                LoggerUtil.LogDebug($"[DB] Deleted pending verification for SteamID {steamID}");
            }
        }

        public void DeleteVerifiedPlayer(long steamID)
        {
            if (_usingSQLite)
            {
                try { _sqlite.DeleteVerifiedPlayer(steamID); return; }
                catch (Exception ex) { LoggerUtil.LogError($"[DB] SQLite DeleteVerifiedPlayer fallback: {ex.Message}"); }
            }
            lock (_lock)
            {
                _verificationPlayersData.VerifiedPlayers.RemoveAll(v => v.SteamID == steamID);
                SaveVerificationPlayersToXml();
                LoggerUtil.LogDebug($"[DB] Deleted verified player SteamID {steamID}");
            }
        }
    }

    // ============================================================
    // XML KLASE ZA VERIFICATIONPLAYERS.XML
    // ============================================================

    [XmlRoot("VerificationPlayers")]
    public class VerificationPlayersData
    {
        [XmlArray("PendingVerifications")]
        [XmlArrayItem("Pending")]
        public List<PendingVerification> PendingVerifications { get; set; } =
            new List<PendingVerification>();

        [XmlArray("VerifiedPlayers")]
        [XmlArrayItem("Verified")]
        public List<VerifiedPlayer> VerifiedPlayers { get; set; } = new List<VerifiedPlayer>();
    }

    public class PendingVerification
    {
        [XmlElement]
        public long SteamID { get; set; }

        [XmlElement]
        public string DiscordUsername { get; set; }

        [XmlElement]
        public string VerificationCode { get; set; }

        [XmlElement]
        public DateTime CodeGeneratedAt { get; set; }

        [XmlElement]
        public DateTime ExpiresAt { get; set; }

        [XmlElement]
        public string GamePlayerName { get; set; } // NEW: For in-game notifications
    }

    public class VerifiedPlayer
    {
        [XmlElement]
        public long SteamID { get; set; }

        [XmlElement]
        public string DiscordUsername { get; set; }

        [XmlElement]
        public ulong DiscordUserID { get; set; }

        [XmlElement]
        public DateTime VerifiedAt { get; set; }

        [XmlElement]
        public string GamePlayerName { get; set; } // NEW: For in-game notifications
    }
}
