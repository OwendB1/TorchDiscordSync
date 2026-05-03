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

        /// <summary>
        /// Init XML-backed database service.
        /// </summary>
        public DatabaseService(string configPath = null)
        {
            var dataDir = MainConfig.GetDataDirectory();
            var legacySqlitePath = Path.Combine(dataDir, "TorchDiscordSync.db");
            var deprecatedVerificationDataPath = Path.Combine(dataDir, "VerificationData.xml");
            var deprecatedVerificationPlayersPath = Path.Combine(dataDir, "VerificationPlayers.xml");

            if (File.Exists(legacySqlitePath))
                LoggerUtil.LogWarning($"[DB] Legacy SQLite database detected at {legacySqlitePath}. XML storage is now authoritative.");
            if (File.Exists(deprecatedVerificationDataPath))
                LoggerUtil.LogWarning($"[DB] Deprecated verification data ignored: {deprecatedVerificationDataPath}");
            if (File.Exists(deprecatedVerificationPlayersPath))
                LoggerUtil.LogWarning($"[DB] Deprecated verification data ignored: {deprecatedVerificationPlayersPath}");

            _factionDataPath = Path.Combine(dataDir, "FactionData.xml");
            _playerDataPath = Path.Combine(dataDir, "PlayerData.xml");
            _eventDataPath = Path.Combine(dataDir, "EventData.xml");
            _chatDataPath = Path.Combine(dataDir, "ChatData.xml");

            _factionData = new FactionDataModel();
            _playerData = new PlayerDataModel();
            _eventData = new EventDataModel();
            _chatData = new ChatDataModel();

            // Always load XML (used as fallback / migration source)
            LoadFactionDataFromXml();
            LoadPlayerDataFromXml();
            LoadEventDataFromXml();
            LoadChatDataFromXml();
            MigrateLegacyDataFromXml(dataDir);
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

        private void MigrateLegacyDataFromXml(string dataDir)
        {
            var legacyPath = Path.Combine(dataDir, "MambaTorchDiscordSyncData.xml");
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
                            var hadLegacyData = (legacy.Factions?.Count ?? 0) > 0 || (legacy.Players?.Count ?? 0) > 0
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
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogWarning($"[DB] Legacy migration skipped: {ex.Message}");
                }
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

        /// <summary>
        /// Saves all data to XML files. FactionData and PlayerData always; EventData/ChatData only if enabled in config.
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
            }
        }

        /// <summary>
        /// Saves or updates a faction in XML storage.
        /// </summary>
        public void SaveFaction(FactionModel faction)
        {
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
            return _factionData.Factions.FirstOrDefault(f => f.FactionID == factionID);
        }

        /// <summary>
        /// Check if faction exists in database by FactionID.
        /// </summary>
        public bool FactionExists(int factionID)
        {
            lock (_lock)
            {
                return _factionData.Factions.Any(f => f.FactionID == factionID);
            }
        }

        public List<FactionModel> GetAllFactions()
        {
            return new List<FactionModel>(_factionData.Factions);
        }

        /// <summary>
        /// Delete a faction from the database by its ID.
        /// </summary>
        public void DeleteFaction(int factionID)
        {
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
            lock (_lock)
            {
                return _factionData.Factions.FirstOrDefault(f => f.GameFactionChatId == gameFactionChatId);
            }
        }

        /// <summary>
        /// Saves or updates a player in XML storage.
        /// </summary>
        public void SavePlayer(PlayerModel player)
        {
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
            return _playerData.Players.FirstOrDefault(p => p.SteamID == steamID);
        }

        public void LogEvent(EventLogModel evt)
        {
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
            return _eventData
                .DeathHistory.Where(d =>
                    d.KillerSteamID == killerSteamID && d.VictimSteamID == victimSteamID
                )
                .OrderByDescending(d => d.DeathTime)
                .FirstOrDefault();
        }

        public void ClearAllData()
        {
            lock (_lock)
            {
                _factionData = new FactionDataModel();
                _playerData = new PlayerDataModel();
                _eventData = new EventDataModel();
                _chatData = new ChatDataModel();
                SaveToXml();
            }
        }
    }
}
