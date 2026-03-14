// Plugin/Services/KillerDetectionService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using mamba.TorchDiscordSync.Plugin.Config;
using mamba.TorchDiscordSync.Plugin.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace mamba.TorchDiscordSync.Plugin.Services
{
    /// <summary>
    /// FINAL: Complete killer detection system
    /// - DamageType detection (Oxygen, Fall, Collision)
    /// - DamageTracking integration
    /// - Reflection fallback
    /// - Environmental detection
    /// </summary>
    public class KillerDetectionService
    {
        private DamageTrackingService damageTracking;
        private readonly Dictionary<long, DamageRecord> _localDamageBuffer = new Dictionary<long, DamageRecord>();
        private bool _isHooked = false;
        private DateTime _lastCleanup = DateTime.Now;
        //private const int CLEANUP_INTERVAL_SECONDS = 30;
        //private const int BUFFER_RETENTION_SECONDS = 10;
        private readonly MainConfig _config;
        private int CleanupIntervalSeconds => _config.CleanupIntervalSeconds;
        private int BufferRetentionSeconds => _config.DamageHistoryMaxSeconds;

        /// <summary>
        /// Local damage record for tracking DamageType
        /// </summary>
        private class DamageRecord
        {
            public string DamageType;
            public long AttackerId;
            public DateTime Timestamp;

            public override string ToString()
            {
                return $"[{Timestamp:HH:mm:ss}] Type={DamageType}, AttackerId={AttackerId}";
            }
        }

        /// <summary>
        /// Initialize KillerDetectionService with MainConfig
        /// </summary>
        /// <param name="config">MainConfig for cleanup intervals</param>
        /// <param name="damageTracking">Optional damage tracking service</param>
        public KillerDetectionService(MainConfig config, DamageTrackingService damageTracking = null)
        {
            _config = config ?? new MainConfig();
            this.damageTracking = damageTracking;
        }

        /// <summary>
        /// MUST be called from plugin Init() or OnSessionStateChanged(Loaded)
        /// Registers the BeforeDamageHandler to capture DamageType
        /// </summary>
        public void Init()
        {
            if (!_isHooked && MyAPIGateway.Session != null)
            {
                try
                {
                    MyAPIGateway.Session.DamageSystem.RegisterBeforeDamageHandler(1, OnDamageReceived);
                    _isHooked = true;
                    LoggerUtil.LogSuccess("[KILLER_DETECTION] BeforeDamageHandler registered (Priority: 1)");
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogError($"[KILLER_DETECTION] Failed to register handler: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Cleanup old records periodically
        /// Call this from a timer or during Dispose()
        /// </summary>
        public void Cleanup()
        {
            try
            {
                lock (_localDamageBuffer)
                {
                    // Periodic cleanup every 30 seconds
                    if ((DateTime.Now - _lastCleanup).TotalSeconds < CleanupIntervalSeconds)
                        return;

                    var cutoffTime = DateTime.Now.AddSeconds(-BufferRetentionSeconds);
                    var keysToRemove = _localDamageBuffer
                        .Where(x => x.Value.Timestamp < cutoffTime)
                        .Select(x => x.Key)
                        .ToList();

                    foreach (var key in keysToRemove)
                        _localDamageBuffer.Remove(key);

                    if (keysToRemove.Count > 0)
                        LoggerUtil.LogDebug($"[KILLER_CLEANUP] Removed {keysToRemove.Count} old damage records");

                    _lastCleanup = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[KILLER_CLEANUP] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Hook called when damage is received
        /// Captures DamageType BEFORE character dies
        /// </summary>
        private void OnDamageReceived(object target, ref MyDamageInformation info)
        {
            try
            {
                var character = target as IMyCharacter;
                if (character == null)
                    return;

                lock (_localDamageBuffer)
                {
                    // Store the damage record with DamageType
                    var record = new DamageRecord
                    {
                        DamageType = info.Type.String,
                        AttackerId = info.AttackerId,
                        Timestamp = DateTime.Now
                    };

                    _localDamageBuffer[character.EntityId] = record;
                    LoggerUtil.LogDebug($"[KILLER_DAMAGE] Recorded: {character.DisplayName} ← {record}");
                }

                // Periodic cleanup
                Cleanup();
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[KILLER_DAMAGE] Error: {ex.Message}");
            }
        }

        // ====================================================================
        // PUBLIC DETECTION METHOD
        // ====================================================================

        public class KillerInfo
        {
            public bool IsPlayerKill { get; set; }
            public string KillerName { get; set; }
            public string WeaponName { get; set; }
            public bool IsTurretKill { get; set; }
            public string TurretOwnerName { get; set; }
            public bool IsNpcFaction { get; set; }
            public string NpcFactionTag { get; set; }
            public DeathCause Cause { get; set; }

            public KillerInfo()
            {
                IsPlayerKill = false;
                KillerName = "Unknown";
                WeaponName = "Unknown";
                IsTurretKill = false;
                TurretOwnerName = null;
                IsNpcFaction = false;
                NpcFactionTag = null;
                Cause = DeathCause.Unknown;
            }

            public override string ToString()
            {
                return $"Killer={KillerName}, Weapon={WeaponName}, Cause={Cause}";
            }
        }

        public enum DeathCause
        {
            Unknown,
            Player,
            Turret,
            NpcFaction,
            Collision,
            Fall,
            Oxygen,
            Pressure,
            Suicide,
            Environment,
            Grinding
        }

        /// <summary>
        /// Main detection method - called from DeathMessageHandler
        /// Uses multi-step approach: DamageType → DamageTracking → Reflection → Fallback
        /// </summary>
        public KillerInfo DetectKiller(IMyCharacter victim)
        {
            var info = new KillerInfo();

            try
            {
                if (victim == null)
                {
                    LoggerUtil.LogDebug("[KILLER] Victim is null");
                    return info;
                }

                LoggerUtil.LogDebug($"[KILLER] ═══ Detecting killer for {victim.DisplayName} ═══");

                var myCharacter = victim as MyCharacter;
                if (myCharacter == null)
                {
                    LoggerUtil.LogDebug("[KILLER] Cannot cast to MyCharacter");
                    return info;
                }

                // ================================================================
                // STEP 1: CHECK LOCAL BUFFER (DamageType - MOST ACCURATE)
                // ================================================================
                if (TryDetectFromLocalBuffer(victim, info))
                {
                    LoggerUtil.LogSuccess("[KILLER] Detected from DamageType buffer");
                    return info;
                }

                // ================================================================
                // STEP 2: CHECK DAMAGETRACKING BUFFER
                // ================================================================
                if (damageTracking != null && TryDetectFromDamageTracking(victim, info))
                {
                    LoggerUtil.LogSuccess("[KILLER] Detected from DamageTracking buffer");
                    return info;
                }

                // ================================================================
                // STEP 3: CHECK REFLECTION
                // ================================================================
                long lastDamageDealerId = GetLastDamageDealerId(myCharacter);
                if (lastDamageDealerId != 0 && TryAnalyzeAttacker(lastDamageDealerId, info))
                {
                    LoggerUtil.LogSuccess("[KILLER] Detected via reflection");
                    return info;
                }

                // ================================================================
                // STEP 4: CHECK OXYGEN STAT (FALLBACK)
                // ================================================================
                if (CheckOxygenDeath(myCharacter, info))
                {
                    LoggerUtil.LogSuccess("[KILLER] Detected oxygen depletion");
                    return info;
                }

                // ================================================================
                // STEP 5: ENVIRONMENTAL FALLBACK
                // ================================================================
                DetectEnvironmentalDeath(myCharacter, info);

                LoggerUtil.LogDebug(
                    $"[KILLER] Final: Cause={info.Cause}, Killer={info.KillerName}, Weapon={info.WeaponName}"
                );
                LoggerUtil.LogDebug($"[KILLER] ═══════════════════════════════════════");

                return info;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[KILLER] Error: {ex.Message}\n{ex.StackTrace}");
                return info;
            }
        }

        // ====================================================================
        // STEP 1: LOCAL BUFFER DETECTION (DamageType)
        // ====================================================================

        private bool TryDetectFromLocalBuffer(IMyCharacter victim, KillerInfo info)
        {
            try
            {
                lock (_localDamageBuffer)
                {
                    if (!_localDamageBuffer.TryGetValue(victim.EntityId, out var record))
                    {
                        LoggerUtil.LogDebug("[KILLER_LOCAL] No local damage record found");
                        return false;
                    }

                    // Only use if recent (within 2 seconds)
                    if ((DateTime.Now - record.Timestamp).TotalSeconds >= 2.0)
                    {
                        LoggerUtil.LogDebug("[KILLER_LOCAL] Record too old");
                        return false;
                    }

                    LoggerUtil.LogDebug($"[KILLER_LOCAL] Found damage type: {record.DamageType}");

                    // Detect Oxygen/Vacuum
                    if (record.DamageType == "LowPressure" || record.DamageType == "Asphyxia")
                    {
                        info.Cause = DeathCause.Oxygen;
                        info.KillerName = "Vacuum";
                        info.WeaponName = "Suffocation";
                        LoggerUtil.LogDebug("[KILLER_LOCAL] Detected: LowPressure/Asphyxia");
                        return true;
                    }

                    // Detect Fall/Gravity
                    if (record.DamageType == "Fall")
                    {
                        info.Cause = DeathCause.Fall;
                        info.KillerName = "Gravity";
                        info.WeaponName = "Ground Impact";
                        LoggerUtil.LogDebug("[KILLER_LOCAL] Detected: Fall");
                        return true;
                    }

                    // Detect Collision/Deformation
                    if (record.DamageType == "Deformation")
                    {
                        info.Cause = DeathCause.Collision;
                        info.KillerName = "Collision";
                        info.WeaponName = "High Velocity Impact";
                        LoggerUtil.LogDebug("[KILLER_LOCAL] Detected: Deformation");
                        return true;
                    }

                    // Detect Heat/Pressure
                    if (record.DamageType == "Heat")
                    {
                        info.Cause = DeathCause.Pressure;
                        info.KillerName = "Heat";
                        info.WeaponName = "Environmental Hazard";
                        LoggerUtil.LogDebug("[KILLER_LOCAL] Detected: Heat");
                        return true;
                    }

                    // Detect Suicide (self-inflicted damage)
                    if (record.DamageType == "Suicide")
                    {
                        info.Cause = DeathCause.Suicide;
                        info.KillerName = "Self";
                        info.WeaponName = "Self-Inflicted";
                        LoggerUtil.LogDebug("[KILLER_LOCAL] Detected: Suicide");
                        return true;
                    }

                    // ✅ OXYGEN/VACUUM DEATHS
                    if (record.DamageType == "LowPressure" || record.DamageType == "Asphyxia")
                    {
                        info.Cause = DeathCause.Oxygen;
                        info.KillerName = "Vacuum";
                        info.WeaponName = "Suffocation";
                        LoggerUtil.LogDebug("[KILLER_LOCAL] Detected: LowPressure/Asphyxia");
                        return true;
                    }

                    // ✅ GRAVITY/IMPACT DEATHS
                    if (record.DamageType == "Fall")
                    {
                        info.Cause = DeathCause.Fall;
                        info.KillerName = "Gravity";
                        info.WeaponName = "Ground Impact";
                        LoggerUtil.LogDebug("[KILLER_LOCAL] Detected: Fall");
                        return true;
                    }

                    // ✅ COLLISION/GRID DEATHS
                    if (record.DamageType == "Deformation")
                    {
                        info.Cause = DeathCause.Collision;
                        info.KillerName = "Collision";
                        info.WeaponName = "High Velocity Impact";
                        LoggerUtil.LogDebug("[KILLER_LOCAL] Detected: Deformation");
                        return true;
                    }

                    // ✅ SELF-INFLICTED DEATHS
                    if (record.DamageType == "Suicide")
                    {
                        info.Cause = DeathCause.Suicide;
                        info.KillerName = "Self";
                        info.WeaponName = "Self-Inflicted";
                        LoggerUtil.LogDebug("[KILLER_LOCAL] Detected: Suicide");
                        return true;
                    }

                    // ✅ GRINDING/DESTRUCTION
                    if (record.DamageType == "Grind")
                    {
                        info.Cause = DeathCause.Grinding;
                        info.KillerName = "Grinder";
                        info.WeaponName = "Grinding Tool";
                        LoggerUtil.LogDebug("[KILLER_LOCAL] Detected: Grind");
                        return true;
                    }

                    // ✅ TEMPERATURE/HEAT DAMAGE
                    if (record.DamageType == "Temperature" || record.DamageType == "Fire")
                    {
                        info.Cause = DeathCause.Pressure;  // Using Pressure as generic environmental hazard
                        info.KillerName = "Heat";
                        info.WeaponName = "Extreme Temperature";
                        LoggerUtil.LogDebug("[KILLER_LOCAL] Detected: Temperature/Fire");
                        return true;
                    }

                    // ✅ RADIATION DAMAGE
                    if (record.DamageType == "Radioactivity")
                    {
                        info.Cause = DeathCause.Pressure;  // Using Pressure as environmental hazard
                        info.KillerName = "Radiation";
                        info.WeaponName = "Radioactive Exposure";
                        LoggerUtil.LogDebug("[KILLER_LOCAL] Detected: Radioactivity");
                        return true;
                    }

                    // ✅ CREATURE ATTACKS (Wolf/Spider)
                    if (record.DamageType == "Wolf" || record.DamageType == "Spider")
                    {
                        info.Cause = DeathCause.Environment;  // Creatures count as environmental
                        info.KillerName = record.DamageType == "Wolf" ? "Wolf" : "Spider";
                        info.WeaponName = "Animal Attack";
                        LoggerUtil.LogDebug($"[KILLER_LOCAL] Detected: {record.DamageType}");
                        return true;
                    }

                    // ✅ HUNGER/STARVATION
                    if (record.DamageType == "Hunger")
                    {
                        info.Cause = DeathCause.Environment;
                        info.KillerName = "Starvation";
                        info.WeaponName = "Hunger";
                        LoggerUtil.LogDebug("[KILLER_LOCAL] Detected: Hunger");
                        return true;
                    }

                    // ✅ WEATHER/STORMS
                    if (record.DamageType == "Weather")
                    {
                        info.Cause = DeathCause.Environment;
                        info.KillerName = "Weather";
                        info.WeaponName = "Environmental Storm";
                        LoggerUtil.LogDebug("[KILLER_LOCAL] Detected: Weather");
                        return true;
                    }

                    // ✅ PRESSURE DAMAGE (Squeeze)
                    if (record.DamageType == "Squeez")  // Note: SE spells it "Squeez" not "Squeeze"
                    {
                        info.Cause = DeathCause.Pressure;
                        info.KillerName = "Pressure";
                        info.WeaponName = "Compression";
                        LoggerUtil.LogDebug("[KILLER_LOCAL] Detected: Squeeze");
                        return true;
                    }

                    // ✅ OUT OF BOUNDS
                    if (record.DamageType == "OutOfBounds")
                    {
                        info.Cause = DeathCause.Environment;
                        info.KillerName = "Map Boundary";
                        info.WeaponName = "Out of Bounds";
                        LoggerUtil.LogDebug("[KILLER_LOCAL] Detected: OutOfBounds");
                        return true;
                    }

                    // ❓ UNKNOWN/UNIMPLEMENTED TYPES
                    // These are captured but not specifically handled:
                    // - Explosion, Rocket, Bullet, Mine, Weapon, Bolt
                    // - Thruster, Drill, Weld, Destruction
                    // - Debug (testing only)
                    LoggerUtil.LogDebug($"[KILLER_LOCAL] Unhandled damage type: {record.DamageType}");
                    return false;

                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[KILLER_LOCAL] Error: {ex.Message}");
                return false;
            }
        }

        // ====================================================================
        // STEP 2: DAMAGETRACKING BUFFER DETECTION
        // ====================================================================

        private bool TryDetectFromDamageTracking(IMyCharacter victim, KillerInfo info)
        {
            try
            {
                if (damageTracking == null)
                {
                    LoggerUtil.LogDebug("[KILLER_BUFFER] DamageTracking not available");
                    return false;
                }

                var lastDamage = damageTracking.GetLastDamage(victim.EntityId, secondsBack: 5);
                if (lastDamage == null || (DateTime.Now - lastDamage.Timestamp).TotalSeconds >= 5)
                {
                    LoggerUtil.LogDebug("[KILLER_BUFFER] No recent damage records");
                    return false;
                }

                MyEntity attackerEntity;
                if (!MyEntities.TryGetEntityById(lastDamage.AttackerId, out attackerEntity))
                {
                    LoggerUtil.LogDebug("[KILLER_BUFFER] Attacker entity no longer exists");
                    return false;
                }

                // Analyze the attacker
                if (AnalyzeDamageDealer(attackerEntity, info))
                {
                    LoggerUtil.LogDebug("[KILLER_BUFFER] Analyzed attacker entity");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[KILLER_BUFFER] Error: {ex.Message}");
                return false;
            }
        }

        // ====================================================================
        // STEP 3: REFLECTION DETECTION
        // ====================================================================

        private long GetLastDamageDealerId(MyCharacter character)
        {
            try
            {
                var characterType = character.GetType();
                string[] possibleFieldNames = new string[]
                {
                    "m_lastDamageDealer",
                    "m_lastAttacker",
                    "m_lastDamageSource",
                    "LastDamageDealer",
                    "LastAttacker"
                };

                foreach (var fieldName in possibleFieldNames)
                {
                    var field = characterType.GetField(
                        fieldName,
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
                    );
                    if (field != null)
                    {
                        var value = field.GetValue(character);
                        if (value is long)
                            return (long)value;
                        else if (value is MyEntity)
                            return ((MyEntity)value).EntityId;
                    }
                }

                LoggerUtil.LogDebug("[KILLER_REFLECT] No damage dealer field found");
                return 0;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[KILLER_REFLECT] Error: {ex.Message}");
                return 0;
            }
        }

        private bool TryAnalyzeAttacker(long attackerId, KillerInfo info)
        {
            try
            {
                MyEntity attacker;
                if (!MyEntities.TryGetEntityById(attackerId, out attacker))
                    return false;

                return AnalyzeDamageDealer(attacker, info);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[KILLER] Error analyzing attacker: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Analyze attacker entity (character, turret, grid, etc.)
        /// </summary>
        private bool AnalyzeDamageDealer(MyEntity damageDealer, KillerInfo info)
        {
            try
            {
                if (damageDealer == null)
                    return false;

                LoggerUtil.LogDebug($"[KILLER_ANALYZE] Entity type: {damageDealer.GetType().Name}");

                // Character attack
                var attackerChar = damageDealer as IMyCharacter;
                if (attackerChar != null)
                {
                    info.IsPlayerKill = true;
                    info.KillerName = attackerChar.DisplayName;
                    info.Cause = DeathCause.Player;
                    info.WeaponName = "Weapon";
                    LoggerUtil.LogDebug($"[KILLER_ANALYZE] Player kill: {info.KillerName}");
                    return true;
                }

                // Turret attack
                var turret = damageDealer as IMyLargeTurretBase;
                if (turret != null)
                {
                    info.IsTurretKill = true;
                    info.Cause = DeathCause.Turret;
                    info.WeaponName = GetTurretTypeName(turret);
                    DetectTurretOwner(turret, info);
                    LoggerUtil.LogDebug($"[KILLER_ANALYZE] Turret kill: {info.KillerName} with {info.WeaponName}");
                    return true;
                }

                // Grid collision
                var grid = damageDealer as MyCubeGrid;
                if (grid != null)
                {
                    info.Cause = DeathCause.Collision;
                    info.KillerName = grid.DisplayName ?? "a ship";
                    info.WeaponName = "Collision";
                    LoggerUtil.LogDebug($"[KILLER_ANALYZE] Grid collision: {info.KillerName}");
                    return true;
                }

                LoggerUtil.LogDebug($"[KILLER_ANALYZE] Unknown type: {damageDealer.GetType().Name}");
                return false;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[KILLER_ANALYZE] Error: {ex.Message}");
                return false;
            }
        }

        private string GetTurretTypeName(IMyLargeTurretBase turret)
        {
            try
            {
                string subtypeName = turret.BlockDefinition.SubtypeName;
                if (subtypeName.Contains("Gatling"))
                    return "Gatling Turret";
                if (subtypeName.Contains("Missile"))
                    return "Missile Turret";
                if (subtypeName.Contains("Interior"))
                    return "Interior Turret";
                if (subtypeName.Contains("Rocket"))
                    return "Rocket Turret";
                if (subtypeName.Contains("Autocannon"))
                    return "Autocannon Turret";
                if (subtypeName.Contains("Artillery"))
                    return "Artillery Turret";
                return turret.CustomName ?? turret.DisplayName ?? "Turret";
            }
            catch
            {
                return "Turret";
            }
        }

        private void DetectTurretOwner(IMyLargeTurretBase turret, KillerInfo info)
        {
            try
            {
                var turretBlock = turret as MyCubeBlock;
                if (turretBlock == null || turretBlock.OwnerId == 0)
                {
                    LoggerUtil.LogDebug("[KILLER_OWNER] Turret has no owner");
                    return;
                }

                long ownerId = turretBlock.OwnerId;
                var playerIdentity = MySession.Static?.Players?.TryGetIdentity(ownerId);
                if (playerIdentity != null)
                {
                    info.TurretOwnerName = playerIdentity.DisplayName;
                    info.KillerName = playerIdentity.DisplayName;
                    info.IsPlayerKill = true;
                    LoggerUtil.LogDebug($"[KILLER_OWNER] Player: {info.TurretOwnerName}");
                    return;
                }

                var faction = MySession.Static?.Factions?.TryGetPlayerFaction(ownerId);
                if (faction != null)
                {
                    info.IsNpcFaction = !faction.AcceptHumans;
                    if (info.IsNpcFaction)
                    {
                        info.NpcFactionTag = faction.Tag;
                        info.KillerName = faction.Tag;
                        info.Cause = DeathCause.NpcFaction;
                        LoggerUtil.LogDebug($"[KILLER_OWNER] NPC Faction: {info.NpcFactionTag}");
                    }
                    else
                    {
                        info.TurretOwnerName = faction.Name;
                        info.KillerName = faction.Name;
                        LoggerUtil.LogDebug($"[KILLER_OWNER] Player faction: {info.TurretOwnerName}");
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[KILLER_OWNER] Error: {ex.Message}");
            }
        }

        // ====================================================================
        // STEP 4: OXYGEN DETECTION (FALLBACK)
        // ====================================================================

        private bool CheckOxygenDeath(MyCharacter character, KillerInfo info)
        {
            try
            {
                if (character.StatComp == null)
                    return false;

                MyEntityStat oxygenStat;
                if (character.StatComp.TryGetStat(MyStringHash.GetOrCompute("oxygen"), out oxygenStat))
                {
                    if (oxygenStat != null && oxygenStat.Value <= 0.1f)
                    {
                        info.Cause = DeathCause.Oxygen;
                        info.KillerName = "Oxygen Depletion";
                        info.WeaponName = "Asphyxiation";
                        LoggerUtil.LogDebug($"[KILLER_OXYGEN] Oxygen: {oxygenStat.Value}");
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[KILLER_OXYGEN] Error: {ex.Message}");
                return false;
            }
        }

        // ====================================================================
        // STEP 5: ENVIRONMENTAL FALLBACK
        // ====================================================================

        private void DetectEnvironmentalDeath(MyCharacter character, KillerInfo info)
        {
            try
            {
                if (info.Cause == DeathCause.Unknown)
                {
                    info.Cause = DeathCause.Environment;
                    info.KillerName = "Environment";
                    info.WeaponName = "Accident";
                    LoggerUtil.LogDebug("[KILLER_ENV] Environmental death (fallback)");
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[KILLER_ENV] Error: {ex.Message}");
            }
        }
    }
}
