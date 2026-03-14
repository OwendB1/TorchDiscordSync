// Plugin/Services/DamageTrackingService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using mamba.TorchDiscordSync.Plugin.Config;
using mamba.TorchDiscordSync.Plugin.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;

namespace mamba.TorchDiscordSync.Plugin.Services
{
    /// <summary>
    /// Real-time damage tracking service
    /// Hooks into the DamageSystem BEFORE damage is applied
    /// Stores damage records in a circular buffer for accurate killer detection
    /// 
    /// PURPOSE:
    /// - Capture attacker information before death event occurs
    /// - Replace unreliable reflection-based killer detection
    /// - Enable accurate turret owner identification
    /// - Support complex scenarios (collision after turret fire, etc.)
    /// 
    /// KEY FEATURES:
    /// - Circular buffer (20 hits per player)
    /// - Thread-safe with lock protection
    /// - Auto-cleanup every 30 seconds
    /// - Optional XML logging for debugging
    /// - Stores owner info when entity no longer exists
    /// </summary>
    public class DamageTrackingService
    {
        /// <summary>
        /// Damage record stored in circular buffer
        /// Serializable for optional XML logging
        /// Captures complete attacker info before character death
        /// </summary>
        [Serializable]
        public class DamageRecord
        {
            /// <summary>Victim character entity ID</summary>
            public long VictimId { get; set; }

            /// <summary>Victim character display name</summary>
            public string VictimName { get; set; }

            /// <summary>Attacker entity ID (turret, missile, character, etc.)</summary>
            public long AttackerId { get; set; }

            /// <summary>Attacker entity display name</summary>
            public string AttackerName { get; set; }

            /// <summary>Damage amount dealt</summary>
            public float DamageAmount { get; set; }

            /// <summary>When damage was dealt</summary>
            public DateTime Timestamp { get; set; }

            /// <summary>Owner ID (for when attacker entity no longer exists)</summary>
            public long OwnerId { get; set; }

            /// <summary>Owner name (for when attacker entity no longer exists)</summary>
            public string OwnerName { get; set; }

            /// <summary>Faction tag (for NPC factions or player factions)</summary>
            public string FactionTag { get; set; }

            public override string ToString()
            {
                return $"[{Timestamp:HH:mm:ss.fff}] {VictimName} ← {AttackerName} ({DamageAmount}dmg)";
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // CONSTANTS
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>Size of circular buffer per player (20 hit history)</summary>
        private const int BUFFER_SIZE = 20;

        /// <summary>How often to cleanup old records (seconds)</summary>
        //private const int CLEANUP_INTERVAL_SECONDS = 30;

        /// <summary>Maximum age for records to keep (seconds)</summary>
        //private const int MAX_HISTORY_SECONDS = 15;

        private readonly MainConfig _config;
        private int CleanupIntervalSeconds => _config.CleanupIntervalSeconds;
        private int MaxHistorySeconds => _config.DamageHistoryMaxSeconds;

        // ════════════════════════════════════════════════════════════════════════
        // FIELDS
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>Circular buffer: VictimId -> array of DamageRecords</summary>
        private Dictionary<long, DamageRecord[]> _damageHistory;

        /// <summary>Current write position in each buffer</summary>
        private Dictionary<long, int> _bufferIndices;

        /// <summary>Lock for thread-safe access</summary>
        private object _lock = new object();

        /// <summary>Path for optional XML logging</summary>
        private readonly string _damageLogPath;

        /// <summary>Whether to log damage to XML file</summary>
        private readonly bool _enableLogging;

        /// <summary>Main configuration - for dynamic config values</summary>
        // private readonly MainConfig _config;

        /// <summary>Last cleanup timestamp</summary>
        private DateTime _lastCleanup = DateTime.Now;

        // ════════════════════════════════════════════════════════════════════════
        // CONSTRUCTOR
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Initialize DamageTrackingService
        /// </summary>
        /// <param name="config">MainConfig for cleanup intervals</param>
        /// <param name="enableLogging">Whether to log damage to XML file (optional)</param>
        public DamageTrackingService(MainConfig config, bool enableLogging = false)
        {
            _config = config ?? new MainConfig();
            _damageHistory = new Dictionary<long, DamageRecord[]>();
            _bufferIndices = new Dictionary<long, int>();
            _enableLogging = enableLogging;

            // Setup logging path
            string dataDir = MainConfig.GetDataDirectory();
            _damageLogPath = Path.Combine(dataDir, "DamageHistory.xml");
        }

        // ════════════════════════════════════════════════════════════════════════
        // INITIALIZATION - Register with DamageSystem
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Initialize and register the BeforeDamageHandler
        /// IMPORTANT: Call this during OnSessionStateChanged(Loaded), not in Init()
        /// DamageSystem must be available
        /// </summary>
        public void Init()
        {
            try
            {
                // Register our handler to receive BEFORE damage is applied
                // Priority 0 = normal priority
                MyAPIGateway.Session.DamageSystem.RegisterBeforeDamageHandler(0, OnDamageReceived);
                LoggerUtil.LogSuccess($"[DAMAGE_TRACK] Registered BeforeDamageHandler. Logs: {(_enableLogging ? _damageLogPath : "disabled")}");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DAMAGE_TRACK] Failed to initialize: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // DAMAGE EVENT HANDLER - Called when damage is dealt
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Handle damage events BEFORE they are applied to victim
        /// This is key: we get attacker info before anything gets cleaned up
        /// </summary>
        /// <param name="victim">The entity taking damage (should be IMyCharacter)</param>
        /// <param name="info">Damage information including attacker</param>
        private void OnDamageReceived(object victim, ref MyDamageInformation info)
        {
            try
            {
                // Only track damage to characters
                IMyCharacter character = victim as IMyCharacter;
                if (character == null)
                    return;

                // Get attacker entity
                IMyEntity attackerEntity = MyAPIGateway.Entities.GetEntityById(info.AttackerId);
                if (attackerEntity == null)
                    return;

                // Periodic cleanup of old records
                if ((DateTime.Now - _lastCleanup).TotalSeconds > CleanupIntervalSeconds)
                {
                    CleanupOldRecords();
                    _lastCleanup = DateTime.Now;
                }

                // Create damage record with all information
                var record = new DamageRecord
                {
                    VictimId = character.EntityId,
                    VictimName = character.DisplayName,
                    AttackerId = info.AttackerId,
                    AttackerName = attackerEntity.DisplayName ?? "Unknown",
                    DamageAmount = info.Amount,
                    Timestamp = DateTime.Now,
                    OwnerId = ExtractOwnerId(attackerEntity),
                    OwnerName = ExtractOwnerName(attackerEntity),
                    FactionTag = ExtractFactionTag(attackerEntity)
                };

                // Store in circular buffer (thread-safe)
                lock (_lock)
                {
                    // Create new buffer if first hit on this player
                    if (!_damageHistory.ContainsKey(character.EntityId))
                    {
                        _damageHistory[character.EntityId] = new DamageRecord[BUFFER_SIZE];
                        _bufferIndices[character.EntityId] = 0;
                    }

                    // Add to buffer and advance position
                    int index = _bufferIndices[character.EntityId];
                    _damageHistory[character.EntityId][index] = record;
                    _bufferIndices[character.EntityId] = (index + 1) % BUFFER_SIZE;
                }

                LoggerUtil.LogDebug($"[DAMAGE_TRACK] {record}");

                // Optional: Log to XML for debugging
                if (_enableLogging)
                    LogDamageToXml(record);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DAMAGE_TRACK] Error in OnDamageReceived: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // PUBLIC QUERY METHODS - Used by KillerDetectionService
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Get the most recent damage record within timeframe
        /// Used by KillerDetectionService to find actual attacker
        /// </summary>
        /// <param name="victimId">Entity ID of the victim</param>
        /// <param name="secondsBack">How far back to look (default 5 seconds)</param>
        /// <returns>Most recent DamageRecord or null if none found</returns>
        public DamageRecord GetLastDamage(long victimId, int secondsBack = 5)
        {
            try
            {
                lock (_lock)
                {
                    // Get records for this victim
                    if (!_damageHistory.TryGetValue(victimId, out var records))
                        return null;

                    // Calculate cutoff time
                    var cutoffTime = DateTime.Now.AddSeconds(-secondsBack);

                    // Search backwards from most recent (most recent = last in buffer)
                    for (int i = BUFFER_SIZE - 1; i >= 0; i--)
                    {
                        var record = records[i];
                        if (record != null && record.Timestamp >= cutoffTime)
                            return record;
                    }

                    return null;
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DAMAGE_TRACK] Error in GetLastDamage: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get all damage records within timeframe
        /// Useful for analysis and edge cases
        /// </summary>
        /// <param name="victimId">Entity ID of the victim</param>
        /// <param name="secondsBack">How far back to look (default 10 seconds)</param>
        /// <returns>List of DamageRecords within timeframe</returns>
        public List<DamageRecord> GetAllDamages(long victimId, int secondsBack = 10)
        {
            var result = new List<DamageRecord>();

            try
            {
                lock (_lock)
                {
                    // Get records for this victim
                    if (!_damageHistory.TryGetValue(victimId, out var records))
                        return result;

                    // Calculate cutoff time
                    var cutoffTime = DateTime.Now.AddSeconds(-secondsBack);

                    // Collect all recent records
                    foreach (var record in records)
                    {
                        if (record != null && record.Timestamp >= cutoffTime)
                            result.Add(record);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DAMAGE_TRACK] Error in GetAllDamages: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Log damage history for debugging purposes
        /// </summary>
        /// <param name="victimId">Entity ID of the victim</param>
        public void LogDamageHistory(long victimId)
        {
            try
            {
                lock (_lock)
                {
                    // Get records for this victim
                    if (!_damageHistory.TryGetValue(victimId, out var records))
                    {
                        LoggerUtil.LogDebug("[DAMAGE_TRACK] No records for this victim");
                        return;
                    }

                    LoggerUtil.LogDebug("[DAMAGE_TRACK] ═══ Damage History ═══");
                    foreach (var record in records)
                    {
                        if (record != null)
                            LoggerUtil.LogDebug($"[DAMAGE_TRACK] {record}");
                    }
                    LoggerUtil.LogDebug("[DAMAGE_TRACK] ═══════════════════");
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DAMAGE_TRACK] Error in LogDamageHistory: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // CLEANUP - Remove old records periodically
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Remove old records periodically to prevent memory buildup
        /// Removes all records older than MaxHistorySeconds
        /// Called automatically by OnDamageReceived
        /// </summary>
        private void CleanupOldRecords()
        {
            try
            {
                lock (_lock)
                {
                    // Records older than this are removed
                    var cutoffTime = DateTime.Now.AddSeconds(-MaxHistorySeconds);
                    var keysToRemove = new List<long>();

                    // Go through each victim's history
                    foreach (var kvp in _damageHistory)
                    {
                        bool allNull = true;

                        // Mark old records as null
                        for (int i = 0; i < kvp.Value.Length; i++)
                        {
                            if (kvp.Value[i] != null && kvp.Value[i].Timestamp < cutoffTime)
                            {
                                kvp.Value[i] = null;
                            }
                            if (kvp.Value[i] != null)
                            {
                                allNull = false;
                            }
                        }

                        // If all records are null, remove this victim's entry
                        if (allNull)
                        {
                            keysToRemove.Add(kvp.Key);
                        }
                    }

                    // Remove empty entries
                    foreach (var key in keysToRemove)
                    {
                        _damageHistory.Remove(key);
                        _bufferIndices.Remove(key);
                    }

                    if (keysToRemove.Count > 0)
                        LoggerUtil.LogDebug($"[DAMAGE_TRACK_CLEANUP] Removed {keysToRemove.Count} old player entries");
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DAMAGE_TRACK] Cleanup error: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // HELPER METHODS - Extract info from entities
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Extract owner ID from attacker entity
        /// Tries multiple sources: block, character, etc.
        /// </summary>
        private long ExtractOwnerId(IMyEntity entity)
        {
            try
            {
                // If it's a block (turret, missile launcher, etc.)
                if (entity is MyCubeBlock block && block.OwnerId != 0)
                    return block.OwnerId;

                // If it's a character
                if (entity is IMyCharacter character && character.ControllerInfo?.ControllingIdentityId != null)
                    return character.ControllerInfo.ControllingIdentityId;

                return 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Extract owner name from attacker entity
        /// </summary>
        private string ExtractOwnerName(IMyEntity entity)
        {
            try
            {
                long ownerId = ExtractOwnerId(entity);
                if (ownerId == 0)
                    return null;

                // Get player identity
                var identity = MySession.Static?.Players?.TryGetIdentity(ownerId);
                return identity?.DisplayName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extract faction tag from attacker entity
        /// </summary>
        private string ExtractFactionTag(IMyEntity entity)
        {
            try
            {
                long ownerId = ExtractOwnerId(entity);
                if (ownerId == 0)
                    return null;

                // Get player faction
                var faction = MySession.Static?.Factions?.TryGetPlayerFaction(ownerId);
                return faction?.Tag;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Log damage record to XML file (optional)
        /// Only if enableLogging is true
        /// </summary>
        private void LogDamageToXml(DamageRecord record)
        {
            try
            {
                if (!_enableLogging)
                    return;

                lock (_lock)
                {
                    var serializer = new XmlSerializer(typeof(DamageRecord));
                    using (var sw = new StreamWriter(_damageLogPath, true))
                    {
                        serializer.Serialize(sw, record);
                        sw.WriteLine();
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogWarning($"[DAMAGE_TRACK_LOG] Failed to write XML: {ex.Message}");
            }
        }
    }
}
