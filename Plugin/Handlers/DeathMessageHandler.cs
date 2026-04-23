// Plugin/Handlers/DeathMessageHandler.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TorchDiscordSync.Plugin.Config;
using TorchDiscordSync.Plugin.Models;
using TorchDiscordSync.Plugin.Services;
using TorchDiscordSync.Plugin.Utils;
using Sandbox.Game;
using VRage.Game.ModAPI;

namespace TorchDiscordSync.Plugin.Handlers
{
    /// <summary>
    /// COMPLETE: Death message handler with DamageTracking integration
    /// Generates contextual death messages based on killer, location, and death cause
    /// Sends messages to game chat and Discord
    /// </summary>
    public class DeathMessageHandler
    {
        // ════════════════════════════════════════════════════════════════════════
        // FIELDS
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>Event logging service for Discord integration</summary>
        private readonly EventLoggingService _eventLog;

        /// <summary>Configuration for death messages and zones</summary>
        private readonly MainConfig _config;

        /// <summary>Death message templates and configuration</summary>
        private readonly DeathMessagesConfig _deathMessagesConfig;

        /// <summary>Service to detect who killed the character</summary>
        private readonly KillerDetectionService _killerDetector;

        /// <summary>Service to detect death location (planet, orbit, etc.)</summary>
        private readonly DeathLocationService _locationService;

        /// <summary>NEW: DamageTracking service for accurate killer detection</summary>
        private readonly DamageTrackingService _damageTracking;

        // ════════════════════════════════════════════════════════════════════════
        // CONSTRUCTOR
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Initialize DeathMessageHandler
        /// NEW: Accept DamageTrackingService as optional parameter
        /// </summary>
        /// <param name="eventLog">Event logging service</param>
        /// <param name="config">Main configuration</param>
        /// <param name="damageTracking">Optional DamageTrackingService for accurate killer detection</param>
        public DeathMessageHandler(EventLoggingService eventLog, MainConfig config, DamageTrackingService damageTracking = null)
        {
            _eventLog = eventLog;
            _config = config;
            _deathMessagesConfig = DeathMessagesConfig.Load();

            // Store DamageTracking reference and pass to KillerDetectionService
            _damageTracking = damageTracking;
            _killerDetector = new KillerDetectionService(config, _damageTracking);
            _locationService = new DeathLocationService(config);

            LoggerUtil.LogInfo(
                "[DEATH_HANDLER] Initialized with proximity detection and surface/orbit zones"
            );
        }

        // ════════════════════════════════════════════════════════════════════════
        // MAIN METHOD - Handle Player Death
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// ENHANCED: Process death with full context detection
        /// Analyzes killer, location, cause, and generates contextual message
        /// </summary>
        public async Task HandlePlayerDeathAsync(string playerName, IMyCharacter character = null)
        {
            try
            {
                // ────────────────────────────────────────────────────────────────
                // STEP 1: Sanitize player name
                // Remove special characters and emoticons
                // ────────────────────────────────────────────────────────────────
                string sanitizedName = TextSanitizationUtil.SanitizePlayerName(playerName);
                LoggerUtil.LogInfo($"[DEATH] ═══ Processing death for: {sanitizedName} ═══");

                // ────────────────────────────────────────────────────────────────
                // STEP 2: Detect killer
                // ────────────────────────────────────────────────────────────────
                KillerDetectionService.KillerInfo killerInfo = null;
                if (character != null)
                {
                    killerInfo = _killerDetector.DetectKiller(character);
                    LoggerUtil.LogDebug(
                        $"[DEATH] Killer: {killerInfo.KillerName}, Weapon: {killerInfo.WeaponName}, Cause: {killerInfo.Cause}"
                    );
                }
                else
                {
                    LoggerUtil.LogWarning("[DEATH] Character is null - cannot detect killer");
                    killerInfo = new KillerDetectionService.KillerInfo();
                }

                // ────────────────────────────────────────────────────────────────
                // STEP 3: Detect location
                // Determine if death was on surface, orbit, specific planet, etc.
                // ────────────────────────────────────────────────────────────────
                LocationZoneResult locationInfo = null;
                if (character != null && _config?.Death?.EnableLocationZones == true)
                {
                    locationInfo = _locationService.DetectDeathZone(character);
                    LoggerUtil.LogDebug(
                        $"[DEATH] Location: Zone={locationInfo.Zone}, Planet={locationInfo.PlanetName ?? "N/A"}"
                    );
                }
                else
                {
                    locationInfo = new LocationZoneResult();
                }

                // ────────────────────────────────────────────────────────────────
                // STEP 4: Generate death message
                // Creates contextual message based on killer, location, and cause
                // ────────────────────────────────────────────────────────────────
                string deathMessage = GenerateDeathMessage(sanitizedName, killerInfo, locationInfo);
                LoggerUtil.LogInfo($"[DEATH] Generated: {deathMessage}");

                // ────────────────────────────────────────────────────────────────
                // STEP 5: Send to game chat
                // ────────────────────────────────────────────────────────────────
                SendToGameChat(deathMessage);

                // ────────────────────────────────────────────────────────────────
                // STEP 6: Send to Discord
                // ────────────────────────────────────────────────────────────────
                string discordMessage = AddEmotePrefix(deathMessage);
                await SendToDiscordAsync(discordMessage);

                LoggerUtil.LogSuccess($"[DEATH] ═══ Complete for {sanitizedName} ═══");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DEATH_HANDLER] Error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // STEP 4: Generate Death Message
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Generate death message with all context
        /// Combines killer, location, and cause into contextual message
        /// </summary>
        private string GenerateDeathMessage(
            string victimName,
            KillerDetectionService.KillerInfo killerInfo,
            LocationZoneResult locationInfo
        )
        {
            try
            {
                // Determine death type based on killer info
                DeathTypeEnum deathType = DetermineDeathType(killerInfo);

                // Get random message template for this death type
                string template = _deathMessagesConfig.GetRandomMessage(deathType);

                // Build message from template
                string message = template;

                // Replace victim (sanitized name)
                message = message.Replace("{victim}", victimName);
                message = message.Replace("{1}", victimName);

                // Replace killer name
                string killerName = GetKillerDisplayName(killerInfo);
                message = message.Replace("{killer}", killerName);
                message = message.Replace("{0}", killerName);

                // Replace weapon name
                string weaponName = GetWeaponDisplayName(killerInfo);
                message = message.Replace("{weapon}", weaponName);
                message = message.Replace("{2}", weaponName);

                // Add location information
                string locationText = GenerateLocationText(locationInfo);
                if (!string.IsNullOrEmpty(locationText))
                {
                    message = $"{message} {locationText}";
                }

                return message;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DEATH_MSG] Error: {ex.Message}");
                return $"{victimName} died";
            }
        }

        /// <summary>
        /// Determine death type based on killer information
        /// Maps DeathCause to DeathTypeEnum for message selection
        /// </summary>
        private DeathTypeEnum DetermineDeathType(KillerDetectionService.KillerInfo killerInfo)
        {
            if (killerInfo == null)
                return DeathTypeEnum.Accident;

            switch (killerInfo.Cause)
            {
                case KillerDetectionService.DeathCause.Player:
                    return DeathTypeEnum.PvP;

                case KillerDetectionService.DeathCause.Turret:
                case KillerDetectionService.DeathCause.NpcFaction:
                    return DeathTypeEnum.Turret;

                case KillerDetectionService.DeathCause.Collision:
                    return DeathTypeEnum.Grid;

                case KillerDetectionService.DeathCause.Fall:
                    return DeathTypeEnum.Environment_Collision;

                case KillerDetectionService.DeathCause.Oxygen:
                    return DeathTypeEnum.Environment_Oxygen;

                case KillerDetectionService.DeathCause.Pressure:
                    return DeathTypeEnum.Environment_Pressure;

                case KillerDetectionService.DeathCause.Suicide:
                    return DeathTypeEnum.Suicide;

                case KillerDetectionService.DeathCause.Environment:
                    return DeathTypeEnum.Environment_Collision;

                default:
                    return DeathTypeEnum.Accident;
            }
        }

        /// <summary>
        /// Get killer display name
        /// Sanitizes player names to remove special characters
        /// </summary>
        private string GetKillerDisplayName(KillerDetectionService.KillerInfo killerInfo)
        {
            if (killerInfo == null)
                return "Unknown";

            // NPC faction - use tag directly
            if (killerInfo.IsNpcFaction && !string.IsNullOrEmpty(killerInfo.NpcFactionTag))
            {
                return killerInfo.NpcFactionTag;
            }

            // Player-owned turret or direct player kill - sanitize name
            if (!string.IsNullOrEmpty(killerInfo.KillerName))
            {
                return TextSanitizationUtil.SanitizePlayerName(killerInfo.KillerName);
            }

            return "Unknown";
        }

        /// <summary>
        /// Get weapon display name
        /// </summary>
        private string GetWeaponDisplayName(KillerDetectionService.KillerInfo killerInfo)
        {
            if (killerInfo == null || string.IsNullOrEmpty(killerInfo.WeaponName))
                return "Unknown";

            return killerInfo.WeaponName;
        }

        /// <summary>
        /// Generate location text with surface/orbit support
        /// Adds location context to death message
        /// </summary>
        private string GenerateLocationText(LocationZoneResult locationInfo)
        {
            if (locationInfo == null || !(_config?.Death?.EnableLocationZones ?? false))
                return "";

            try
            {
                bool showGridName = _config?.Death?.ShowGridName ?? true;
                return _locationService.GenerateLocationText(locationInfo, showGridName);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DEATH_LOC] Error: {ex.Message}");
                return "";
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // STEP 5: Send to Game Chat
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Send death message to game chat
        /// Uses MyVisualScriptLogicProvider for in-game messaging
        /// </summary>
        private void SendToGameChat(string deathMessage)
        {
            try
            {
                MyVisualScriptLogicProvider.SendChatMessage(deathMessage, "Server", 0, "Red");
                LoggerUtil.LogDebug($"[DEATH_GAME] Sent: {deathMessage}");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DEATH_GAME] Error: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // STEP 6: Send to Discord
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Send death message to Discord
        /// Uses EventLoggingService for Discord integration
        /// </summary>
        private async Task SendToDiscordAsync(string discordMessage)
        {
            try
            {
                if (_eventLog == null)
                {
                    LoggerUtil.LogWarning("[DEATH_DISCORD] EventLoggingService is null");
                    return;
                }

                await _eventLog.LogDeathAsync(discordMessage);
                LoggerUtil.LogDebug($"[DEATH_DISCORD] Sent: {discordMessage}");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DEATH_DISCORD] Error: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // HELPER: Add Emoticon Prefix
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Add random emoticon prefix to Discord message
        /// Makes messages more visually appealing
        /// </summary>
        private string AddEmotePrefix(string message)
        {
            try
            {
                if (
                    _config?.Death == null
                    || string.IsNullOrEmpty(_config.Death.DeathMessageEmotes)
                )
                    return $"💀 {message}";

                // Parse comma-separated emotes
                var emotes = _config.Death.DeathMessageEmotes.Split(',');
                if (emotes.Length == 0)
                    return $"💀 {message}";

                // Select random emote
                string randomEmote = emotes[new Random().Next(emotes.Length)].Trim();
                return $"{randomEmote} {message}";
            }
            catch
            {
                return $"💀 {message}";
            }
        }

        /// <summary>
        /// Initialize killer detection service
        /// </summary> <summary>
        /// 
        /// </summary>
        public void InitializeKillerDetection()
        {
            try
            {
                _killerDetector.Init();
                LoggerUtil.LogSuccess("[DEATH_HANDLER] KillerDetectionService initialized");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DEATH_HANDLER] Failed to initialize killer detection: {ex.Message}");
            }
        }

        /// <summary>
        /// Cleanup resources used by killer detection service
        /// </summary>
        public void Cleanup()
        {
            try
            {
                _killerDetector?.Cleanup();
                LoggerUtil.LogInfo("[DEATH_HANDLER] Cleanup complete");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DEATH_HANDLER] Cleanup error: {ex.Message}");
            }
        }

    }
}
