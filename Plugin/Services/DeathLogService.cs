// Plugin/Services/DeathLogService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using mamba.TorchDiscordSync.Plugin.Config;
using mamba.TorchDiscordSync.Plugin.Models;
using mamba.TorchDiscordSync.Plugin.Utils;
using VRage.Game.ModAPI;

namespace mamba.TorchDiscordSync.Plugin.Services
{
    /// <summary>
    /// Service responsible for tracking, logging, and analyzing player death events.
    /// Handles in-memory history, database persistence, and Discord notifications.
    /// </summary>
    public class DeathLogService
    {
        private readonly DatabaseService _db;
        private readonly EventLoggingService _eventLog;
        private readonly DeathMessagesConfig _deathMessages;
        private readonly DeathLocationService _deathLocation;
        private readonly MainConfig _config;

        /// <summary>
        /// In-memory cache for player death history to determine kill streaks and retaliation.
        /// Key: Victim Name, Value: List of death records.
        /// </summary>
        private readonly Dictionary<string, List<DeathHistoryModel>> _playerDeathHistory =
            new Dictionary<string, List<DeathHistoryModel>>();

        public DeathLogService(DatabaseService db, EventLoggingService eventLog, MainConfig config)
        {
            _db = db;
            _eventLog = eventLog;
            _config = config;
            _deathMessages = DeathMessagesConfig.Load();
            _deathLocation = new DeathLocationService(_config);

            LoggerUtil.LogDebug("DeathLogService initialized and configuration loaded.");
        }

        /// <summary>
        /// Main entry point: Processes a player death event.
        /// Orchestrates database logging, Discord messaging, and local history tracking.
        /// </summary>
        public async Task LogPlayerDeathAsync(
            string killerName,
            string victimName,
            string weaponType,
            long killerId,
            long victimId,
            string location,
            IMyCharacter character = null
            )
        {
            try
            {
                LoggerUtil.LogInfo($"[DEATH EVENT] {killerName} killed {victimName} using {weaponType} at {location}");

                // Ensure history list exists for the victim
                if (!_playerDeathHistory.ContainsKey(victimName))
                {
                    _playerDeathHistory[victimName] = new List<DeathHistoryModel>();
                }

                // Identify the nature of death (Suicide, PvP, First Blood, etc.)
                DeathTypeEnum deathType = DetermineDeathType(killerName, victimName);

                // 1. Persistence: Log to Database
                if (_db != null)
                {
                    try
                    {
                        // Passing deathType as string for DB compatibility
                        _db.LogDeath(killerId, victimId, deathType.ToString(), weaponType, location);
                        LoggerUtil.LogDebug("Death record successfully saved to database.");
                    }
                    catch (Exception dbEx)
                    {
                        LoggerUtil.LogWarning($"Database logging failed: {dbEx.Message}");
                    }
                }

                // 2. Notification: Generate and send Discord message with location processing
                string finalLocation = location;
                if (character != null && _deathLocation != null && _config != null && _config.Death != null && _config.Death.EnableLocationZones)
                {
                    try
                    {
                        var zoneResult = _deathLocation.DetectDeathZone(character);
                        if (zoneResult != null)
                        {
                            finalLocation = _deathLocation.GenerateLocationText(zoneResult, _config.Death.GridDetectionEnabled);
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogError($"Error processing death location: {ex.Message}");
                    }
                }

                string discordMessage = GenerateDeathMessage(killerName, victimName, weaponType, deathType, finalLocation);
                if (_eventLog != null)
                {
                    await _eventLog.LogDeathAsync(discordMessage);
                }

                // 3. Tracking: Record in local history using Reflection for flexibility with Model changes
                try
                {
                    var deathRecord = new DeathHistoryModel();
                    SetPropertyIfExists(deathRecord, "VictimName", victimName);
                    SetPropertyIfExists(deathRecord, "VictimSteamId", victimId.ToString());
                    SetPropertyIfExists(deathRecord, "KillerName", killerName);
                    SetPropertyIfExists(deathRecord, "KillerSteamId", killerId.ToString());
                    SetPropertyIfExists(deathRecord, "Weapon", weaponType);
                    SetPropertyIfExists(deathRecord, "WeaponType", weaponType); // Backup for different model versions
                    SetPropertyIfExists(deathRecord, "Location", location);
                    SetPropertyIfExists(deathRecord, "Timestamp", DateTime.UtcNow);
                    SetPropertyIfExists(deathRecord, "DeathType", deathType.ToString());

                    _playerDeathHistory[victimName].Add(deathRecord);
                }
                catch (Exception historyEx)
                {
                    LoggerUtil.LogDebug($"Failed to update local history: {historyEx.Message}");
                }

                LoggerUtil.LogSuccess($"Death processing complete for {victimName}");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"Critical error in LogPlayerDeathAsync: {ex.Message}");
            }
        }

        /// <summary>
        /// Logic to determine specific death categories based on player history.
        /// </summary>
        private DeathTypeEnum DetermineDeathType(string killerName, string victimName)
        {
            if (string.Equals(killerName, victimName, StringComparison.OrdinalIgnoreCase))
                return DeathTypeEnum.Suicide;

            // Check if this is the first time the killer has gotten the victim
            if (!HasPlayerEverKilledPlayer(victimName, killerName))
                return DeathTypeEnum.FirstKill;

            DateTime lastKillTime = GetLastKillTime(victimName, killerName);

            // Retaliation within 1 hour
            if (lastKillTime > DateTime.UtcNow.AddHours(-1))
                return DeathTypeEnum.Retaliation;

            // Long-term revenge within 24 hours
            if (lastKillTime > DateTime.UtcNow.AddHours(-24))
                return DeathTypeEnum.RetaliationOld;

            return DeathTypeEnum.Accident;
        }

        /// <summary>
        /// Formats the death message using templates from configuration.
        /// FIXED: Now supports BOTH {0}/{1}/{2}/{3} AND {victim}/{killer}/{weapon}/{location} placeholders
        /// </summary>
        private string GenerateDeathMessage(string killer, string victim, string weapon, DeathTypeEnum type, string loc)
        {
            try
            {
                if (_deathMessages == null) return $"{killer} killed {victim}";

                // Get random message template for this death type
                string template = _deathMessages.GetRandomMessage(type);

                if (string.IsNullOrEmpty(template))
                    return $"{killer} killed {victim} with {weapon}";

                // CRITICAL FIX: Support BOTH old-style {0}/{1}/{2} AND new-style {victim}/{killer}
                string formatted = template
                    // Old-style numbered placeholders (for backward compatibility)
                    .Replace("{0}", killer)
                    .Replace("{1}", victim)
                    .Replace("{2}", weapon)
                    .Replace("{3}", loc)
                    // New-style named placeholders
                    .Replace("{victim}", victim)
                    .Replace("{killer}", killer)
                    .Replace("{weapon}", weapon)
                    .Replace("{location}", loc);

                LoggerUtil.LogDebug($"Generated death message: {formatted}");
                return formatted;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"Message generation error: {ex.Message}");
                return $"{killer} killed {victim}";
            }
        }

        #region Statistics and Leaderboards

        /// <summary>
        /// Calculates Kills, Deaths, and KDR for a specific player.
        /// </summary>
        public (int Deaths, int Kills, float KDRatio) GetPlayerStats(string playerName)
        {
            int deaths = _playerDeathHistory.TryGetValue(playerName, out var pDeaths) ? pDeaths.Count : 0;
            int kills = 0;

            foreach (var history in _playerDeathHistory.Values)
            {
                kills += history.Count(d => IsPropertyEqual(d, "KillerName", playerName));
            }

            float kd = deaths > 0 ? (float)kills / deaths : kills;
            return (deaths, kills, kd);
        }

        /// <summary>
        /// Generates a list of top killers from current session history.
        /// </summary>
        public List<(string PlayerName, int Kills)> GetTopKillers(int limit = 10)
        {
            var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var history in _playerDeathHistory.Values)
            {
                foreach (var death in history)
                {
                    string killer = GetProperty(death, "KillerName") as string;
                    if (!string.IsNullOrEmpty(killer))
                    {
                        if (!stats.ContainsKey(killer)) stats[killer] = 0;
                        stats[killer]++;
                    }
                }
            }

            return stats.OrderByDescending(x => x.Value).Take(limit).Select(x => (x.Key, x.Value)).ToList();
        }

        #endregion

        #region Reflection Helpers

        /// <summary>
        /// Safely sets a property value on a model if it exists.
        /// </summary>
        private void SetPropertyIfExists(object obj, string propertyName, object value)
        {
            var prop = obj.GetType().GetProperty(propertyName);
            if (prop != null && prop.CanWrite) prop.SetValue(obj, value);
        }

        private object GetProperty(object obj, string propName)
        {
            return obj.GetType().GetProperty(propName)?.GetValue(obj);
        }

        private bool IsPropertyEqual(object obj, string propName, string value)
        {
            return string.Equals(GetProperty(obj, propName) as string, value, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        private bool HasPlayerEverKilledPlayer(string victim, string killer)
        {
            if (!_playerDeathHistory.ContainsKey(victim)) return false;
            return _playerDeathHistory[victim].Any(d => IsPropertyEqual(d, "KillerName", killer));
        }

        private DateTime GetLastKillTime(string victim, string killer)
        {
            if (!_playerDeathHistory.ContainsKey(victim)) return DateTime.MinValue;

            var lastKill = _playerDeathHistory[victim]
                .Where(d => IsPropertyEqual(d, "KillerName", killer))
                .OrderByDescending(d => GetProperty(d, "Timestamp") as DateTime? ?? DateTime.MinValue)
                .FirstOrDefault();

            return lastKill != null ? (GetProperty(lastKill, "Timestamp") as DateTime? ?? DateTime.MinValue) : DateTime.MinValue;
        }

        public void ClearHistory() => _playerDeathHistory.Clear();
    }
}
