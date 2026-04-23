// Plugin/Services/PlayerTrackingService.cs
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Sandbox.Game;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Torch.API;
using TorchDiscordSync.Plugin.Config;
using TorchDiscordSync.Plugin.Handlers;
using TorchDiscordSync.Plugin.Models;
using TorchDiscordSync.Plugin.Utils;
using VRage.Game.ModAPI;
using VRageMath;

namespace TorchDiscordSync.Plugin.Services
{
    /// <summary>
    /// Service for tracking player joins/leaves and DEATHS via IMyCharacter.CharacterDied event
    /// ENHANCED: Better respawn detection, comprehensive death logging with location & details
    /// </summary>
    public class PlayerTrackingService
    {
        private readonly EventLoggingService _eventLog;
        private readonly ITorchBase _torch;
        private readonly DeathLogService _deathLog;
        private readonly MainConfig _config;
        private readonly DeathMessageHandler _deathHandler;
        private DeathMessagesConfig _deathMessagesConfig;

        // Cache player names for join/leave messages (prevents SteamID display on leave)
        private Dictionary<ulong, string> _playerNames = new Dictionary<ulong, string>();
        private readonly Dictionary<string, DateTime> _recentChatEvents =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _expectedSelfEchoEvents =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private System.Timers.Timer _pollingTimer;
        private HashSet<ulong> _knownPlayers = new HashSet<ulong>();

        // ENHANCED: Track both character entities and their entity IDs for respawn detection
        private Dictionary<ulong, IMyCharacter> _trackedCharacters = new Dictionary<ulong, IMyCharacter>();
        private Dictionary<ulong, long> _trackedCharacterEntityIds = new Dictionary<ulong, long>();

        // ENHANCED: Track death event counters for debugging
        private Dictionary<ulong, int> _deathEventCounters = new Dictionary<ulong, int>();

        private object _lockObject = new object();
        private const int CHAT_EVENT_DEDUP_SECONDS = 15;

        public event Action OnlinePlayersChanged;

        public PlayerTrackingService(
            EventLoggingService eventLog,
            ITorchBase torch,
            DeathLogService deathLog,
            MainConfig config = null,
            DeathMessageHandler deathHandler = null
        )
        {
            _eventLog = eventLog;
            _torch = torch;
            _deathLog = deathLog;
            _config = config;
            _deathHandler = deathHandler;
            _deathMessagesConfig = DeathMessagesConfig.Load();

            LoggerUtil.LogDebug("[TRACKING] PlayerTrackingService initialized");
        }

        public void Initialize()
        {
            try
            {
                LoggerUtil.LogDebug(
                    "Initializing PlayerTrackingService with CharacterDied event hooking..."
                );
                InitializePolling();
                InitializeDeathTracking();
                LoggerUtil.LogSuccess("Player tracking initialized (event-based death detection)");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Player tracking initialization failed: " + ex.Message);
            }
        }

        private void InitializePolling()
        {
            _pollingTimer = new System.Timers.Timer(1000);
            _pollingTimer.Elapsed += OnPollingTick;
            _pollingTimer.AutoReset = true;
            _pollingTimer.Start();
            LoggerUtil.LogInfo("Player polling timer started (1-second fallback intervals)");
        }

        private void InitializeDeathTracking()
        {
            if (MyAPIGateway.Players == null)
                return;

            var allPlayers = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(allPlayers);

            foreach (var player in allPlayers)
            {
                if (player?.Character == null)
                    continue;

                HookCharacterDeath(player.Character, player.SteamUserId, player.DisplayName);
                _knownPlayers.Add(player.SteamUserId);
                _playerNames[player.SteamUserId] = player.DisplayName;
                _deathEventCounters[player.SteamUserId] = 0;
            }

            LoggerUtil.LogInfo($"Death tracking hooked for {_knownPlayers.Count} players");
        }

        /// <summary>
        /// ENHANCED: Hook character death with better respawn detection
        /// </summary>
        private void HookCharacterDeath(IMyCharacter character, ulong steamId, string playerName)
        {
            if (character == null)
            {
                LoggerUtil.LogDebug(
                    $"[HOOK_DEBUG] HookCharacterDeath called but character is NULL for {playerName} (SteamID {steamId})"
                );
                return;
            }

            lock (_lockObject)
            {
                var newEntityId = character.EntityId;

                // Check if we already have this exact character hooked
                if (_trackedCharacters.ContainsKey(steamId))
                {
                    var existing = _trackedCharacters[steamId];
                    var oldEntityId = _trackedCharacterEntityIds.GetValueOrDefault(steamId, 0);

                    if (existing == character && oldEntityId == newEntityId)
                    {
                        LoggerUtil.LogDebug($"[HOOK_DEBUG] Character already hooked for {playerName} (EntityID: {newEntityId})");
                        return;
                    }

                    // Character changed (respawn detected)
                    LoggerUtil.LogInfo(
                        $"[HOOK_RESPAWN] Character changed for {playerName}: OldEntityID={oldEntityId}, NewEntityID={newEntityId}"
                    );

                    // Try to unhook old character (may fail if already dead/disposed)
                    try
                    {
                        if (existing != null && !existing.MarkedForClose && !existing.Closed)
                        {
                            // Can't directly unhook, but we'll replace the reference
                            LoggerUtil.LogDebug($"[HOOK_DEBUG] Old character still exists, will be replaced");
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogDebug($"[HOOK_DEBUG] Error checking old character: {ex.Message}");
                    }
                }

                try
                {
                    // Hook the new character
                    character.CharacterDied += deadChar => OnCharacterDied(deadChar, steamId, playerName);
                    _trackedCharacters[steamId] = character;
                    _trackedCharacterEntityIds[steamId] = newEntityId;

                    LoggerUtil.LogSuccess(
                        $"[HOOK] CharacterDied hooked for {playerName} (SteamID: {steamId}, EntityID: {newEntityId}, DeathCount: {_deathEventCounters.GetValueOrDefault(steamId, 0)})"
                    );
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogError(
                        $"[HOOK_ERROR] Failed to hook character for {playerName}: {ex.Message}"
                    );
                }
            }
        }

        private void OnPollingTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            CheckPlayerChanges();
            HookNewPlayers();
        }

        private void CheckPlayerChanges()
        {
            if (MyAPIGateway.Players == null)
                return;

            var currentPlayers = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(currentPlayers);

            var currentSteamIds = new HashSet<ulong>();
            var onlinePlayersChanged = false;
            foreach (var player in currentPlayers)
            {
                currentSteamIds.Add(player.SteamUserId);

                if (!_knownPlayers.Contains(player.SteamUserId))
                {
                    onlinePlayersChanged = true;
                    _knownPlayers.Add(player.SteamUserId);
                    _playerNames[player.SteamUserId] = player.DisplayName;
                    _deathEventCounters[player.SteamUserId] = 0;
                    LoggerUtil.LogInfo(
                        $"Player joined: {player.DisplayName} ({player.SteamUserId})"
                    );
                    if (WasChatEventHandledRecently("join", player.DisplayName))
                    {
                        LoggerUtil.LogDebug(
                            $"[TRACKING] Poll join suppressed; chat event already handled for {player.DisplayName}");
                    }
                    else
                    {
                        RememberExpectedSelfEcho("join", player.DisplayName);
                        _ = _eventLog.LogPlayerJoinAsync(player.DisplayName, player.SteamUserId);
                    }
                }
            }

            var disconnected = new List<ulong>();
            foreach (var steamId in _knownPlayers)
            {
                if (!currentSteamIds.Contains(steamId))
                    disconnected.Add(steamId);
            }

            foreach (var steamId in disconnected)
            {
                onlinePlayersChanged = true;
                _knownPlayers.Remove(steamId);

                lock (_lockObject)
                {
                    _trackedCharacters.Remove(steamId);
                    _trackedCharacterEntityIds.Remove(steamId);
                    _deathEventCounters.Remove(steamId);
                }

                var playerName = _playerNames.TryGetValue(steamId, out var name)
                    ? name
                    : steamId.ToString();
                _playerNames.Remove(steamId);

                LoggerUtil.LogInfo($"Player left: {playerName} ({steamId})");
                if (WasChatEventHandledRecently("leave", playerName))
                {
                    LoggerUtil.LogDebug(
                        $"[TRACKING] Poll leave suppressed; chat event already handled for {playerName}");
                }
                else
                {
                    RememberExpectedSelfEcho("leave", playerName);
                    _ = _eventLog.LogPlayerLeaveAsync(playerName, steamId);
                }
            }

            if (onlinePlayersChanged)
                NotifyOnlinePlayersChanged();
        }

        /// <summary>
        /// ENHANCED: Better detection of character changes (respawns)
        /// </summary>
        private void HookNewPlayers()
        {
            if (MyAPIGateway.Players == null)
                return;

            var allPlayers = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(allPlayers);

            foreach (var player in allPlayers)
            {
                if (player?.Character == null)
                    continue;

                var playerName = player.DisplayName;

                lock (_lockObject)
                {
                    if (!_trackedCharacters.ContainsKey(player.SteamUserId))
                    {
                        // New player, hook for first time
                        HookCharacterDeath(player.Character, player.SteamUserId, playerName);
                        _playerNames[player.SteamUserId] = playerName;
                        _knownPlayers.Add(player.SteamUserId);
                        LoggerUtil.LogDebug($"[HOOK] New player hooked: {playerName}");
                    }
                    else
                    {
                        // Existing player - check if character changed (respawn)
                        var oldCharacter = _trackedCharacters[player.SteamUserId];
                        var oldEntityId = _trackedCharacterEntityIds.GetValueOrDefault(player.SteamUserId, 0);
                        var newEntityId = player.Character.EntityId;

                        // CRITICAL: Re-hook if EntityID changed OR if character reference changed
                        if (oldCharacter != player.Character || oldEntityId != newEntityId)
                        {
                            LoggerUtil.LogInfo(
                                $"[HOOK_REHOOK] Character/EntityID changed for {playerName} - re-hooking death event (Old: {oldEntityId}, New: {newEntityId})"
                            );
                            HookCharacterDeath(player.Character, player.SteamUserId, playerName);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// ENHANCED: Event handler for character death with comprehensive logging
        /// </summary>
        private async void OnCharacterDied(IMyCharacter deadCharacter, ulong steamId, string originalPlayerName)
        {
            try
            {
                // Increment death counter for debugging
                var deathCount = 0;
                lock (_lockObject)
                {
                    _deathEventCounters[steamId] = _deathEventCounters.GetValueOrDefault(steamId, 0) + 1;
                    deathCount = _deathEventCounters[steamId];
                }

                var playerName = deadCharacter?.DisplayName ?? originalPlayerName;

                LoggerUtil.LogInfo($"[DEATH_EVENT #{deathCount}] ═══════════════════════════════════════");
                LoggerUtil.LogInfo($"[DEATH_EVENT #{deathCount}] Player: {playerName}");
                LoggerUtil.LogInfo($"[DEATH_EVENT #{deathCount}] SteamID: {steamId}");

                if (deadCharacter != null)
                {
                    var position = deadCharacter.GetPosition();
                    var entityId = deadCharacter.EntityId;
                    var isClosed = deadCharacter.Closed;
                    var isMarkedForClose = deadCharacter.MarkedForClose;

                    LoggerUtil.LogInfo($"[DEATH_EVENT #{deathCount}] EntityID: {entityId}");
                    LoggerUtil.LogInfo($"[DEATH_EVENT #{deathCount}] Position: X={position.X:F1}, Y={position.Y:F1}, Z={position.Z:F1}");
                    LoggerUtil.LogInfo($"[DEATH_EVENT #{deathCount}] Closed: {isClosed}, MarkedForClose: {isMarkedForClose}");
                }
                else
                {
                    LoggerUtil.LogWarning($"[DEATH_EVENT #{deathCount}] Character is NULL!");
                }

                // Delegate to DeathMessageHandler if available
                if (_deathHandler != null)
                {
                    LoggerUtil.LogDebug($"[DEATH_EVENT #{deathCount}] Calling DeathMessageHandler with character...");
                    // CRITICAL: Pass deadCharacter for location detection!
                    await _deathHandler.HandlePlayerDeathAsync(playerName, deadCharacter);
                    LoggerUtil.LogSuccess($"[DEATH_EVENT #{deathCount}] DeathMessageHandler completed");
                }
                else
                {
                    // Fallback: log directly
                    LoggerUtil.LogWarning($"[DEATH_EVENT #{deathCount}] DeathMessageHandler is NULL - using fallback");
                    if (_eventLog != null)
                    {
                        await _eventLog.LogDeathAsync($"💀 {playerName} died (Death #{deathCount})");
                    }
                }

                LoggerUtil.LogInfo($"[DEATH_EVENT #{deathCount}] ═══════════════════════════════════════");

                // CRITICAL: Re-hook immediately after death to prepare for respawn
                // The player will likely respawn with a new character entity
                LoggerUtil.LogDebug($"[DEATH_EVENT #{deathCount}] Death processing complete, waiting for respawn...");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DEATH_ERROR] Error in OnCharacterDied: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Processes system chat messages to detect deaths
        /// </summary>
        public void ProcessSystemChatMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            if (TryExtractPlayerJoin(message, out var joinedPlayer))
            {
                if (TryConsumeExpectedSelfEcho("join", joinedPlayer))
                    return;

                HandleChatDrivenJoin(joinedPlayer);
                return;
            }

            if (TryExtractPlayerLeave(message, out var leftPlayer))
            {
                if (TryConsumeExpectedSelfEcho("leave", leftPlayer))
                    return;

                HandleChatDrivenLeave(leftPlayer);
                return;
            }

            if ((message.Contains(" died") || message.Contains(" was killed"))
                && _deathHandler == null
                && TryRememberChatEvent("death", message))
            {
                _ = _eventLog.LogDeathAsync(message);
            }
        }

        private void HandleChatDrivenJoin(string playerName)
        {
            if (!TryRememberChatEvent("join", playerName))
                return;

            ulong steamId = 0;
            var player = FindOnlinePlayerByName(playerName);
            if (player != null)
            {
                steamId = player.SteamUserId;
                RememberKnownPlayer(player);
            }

            LoggerUtil.LogInfo($"[TRACKING] Chat-driven join detected: {playerName} ({steamId})");
            if (_eventLog != null)
                _ = _eventLog.LogPlayerJoinAsync(playerName, steamId, false);

            NotifyOnlinePlayersChanged();
        }

        private void HandleChatDrivenLeave(string playerName)
        {
            if (!TryRememberChatEvent("leave", playerName))
                return;

            var steamId = FindKnownSteamIdByName(playerName);
            LoggerUtil.LogInfo($"[TRACKING] Chat-driven leave detected: {playerName} ({steamId})");
            if (_eventLog != null)
                _ = _eventLog.LogPlayerLeaveAsync(playerName, steamId, false);

            NotifyOnlinePlayersChanged();
        }

        private bool TryExtractPlayerJoin(string message, out string playerName)
        {
            return TryExtractPlayerEvent(
                message,
                @"^(?<player>.+?)\s+(has\s+)?joined\s+(the\s+)?server\.?$",
                out playerName)
                || TryExtractPlayerEvent(
                    message,
                    @"^(?<player>.+?)\s+connected\s+(to\s+(the\s+)?)?server\.?$",
                    out playerName);
        }

        private bool TryExtractPlayerLeave(string message, out string playerName)
        {
            return TryExtractPlayerEvent(
                message,
                @"^(?<player>.+?)\s+(has\s+)?left\s+(the\s+)?server\.?$",
                out playerName)
                || TryExtractPlayerEvent(
                    message,
                    @"^(?<player>.+?)\s+disconnected(\s+from\s+(the\s+)?server)?\.?$",
                    out playerName);
        }

        private bool TryExtractPlayerEvent(string message, string pattern, out string playerName)
        {
            playerName = null;
            var normalized = NormalizeSystemChatMessage(message);
            var match = Regex.Match(normalized, pattern, RegexOptions.IgnoreCase);
            if (!match.Success)
                return false;

            playerName = match.Groups["player"].Value.Trim();
            return IsPlausiblePlayerName(playerName);
        }

        private static string NormalizeSystemChatMessage(string message)
        {
            var text = (message ?? string.Empty).Trim();
            text = text.Replace(":sunny:", string.Empty);
            text = text.Replace(":new_moon:", string.Empty);
            text = text.Trim();
            if (text.EndsWith(".", StringComparison.Ordinal))
                text = text.Substring(0, text.Length - 1).Trim();
            return text;
        }

        private static bool IsPlausiblePlayerName(string playerName)
        {
            if (string.IsNullOrWhiteSpace(playerName))
                return false;

            var name = playerName.Trim();
            return name.Length <= 64
                   && !name.Equals("Server", StringComparison.OrdinalIgnoreCase)
                   && !name.Equals("Discord", StringComparison.OrdinalIgnoreCase)
                   && !name.Equals("TDS", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryRememberChatEvent(string eventType, string subject)
        {
            lock (_lockObject)
            {
                CleanupRecentChatEvents();

                var key = BuildChatEventKey(eventType, subject);
                if (_recentChatEvents.TryGetValue(key, out var lastSeen)
                    && (DateTime.UtcNow - lastSeen).TotalSeconds < CHAT_EVENT_DEDUP_SECONDS)
                {
                    return false;
                }

                _recentChatEvents[key] = DateTime.UtcNow;
                return true;
            }
        }

        private void RememberExpectedSelfEcho(string eventType, string subject)
        {
            lock (_lockObject)
            {
                CleanupExpectedSelfEchoEvents();
                _expectedSelfEchoEvents[BuildChatEventKey(eventType, subject)] = DateTime.UtcNow;
            }
        }

        private bool TryConsumeExpectedSelfEcho(string eventType, string subject)
        {
            lock (_lockObject)
            {
                CleanupExpectedSelfEchoEvents();
                var key = BuildChatEventKey(eventType, subject);
                if (!_expectedSelfEchoEvents.ContainsKey(key))
                    return false;

                _expectedSelfEchoEvents.Remove(key);
                LoggerUtil.LogDebug(
                    $"[TRACKING] Ignored self-echoed {eventType} chat message for {subject}");
                return true;
            }
        }

        private bool WasChatEventHandledRecently(string eventType, string subject)
        {
            lock (_lockObject)
            {
                CleanupRecentChatEvents();

                var key = BuildChatEventKey(eventType, subject);
                return _recentChatEvents.TryGetValue(key, out var lastSeen)
                       && (DateTime.UtcNow - lastSeen).TotalSeconds < CHAT_EVENT_DEDUP_SECONDS;
            }
        }

        private void CleanupRecentChatEvents()
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-CHAT_EVENT_DEDUP_SECONDS);
            var expired = new List<string>();
            foreach (var entry in _recentChatEvents)
            {
                if (entry.Value < cutoff)
                    expired.Add(entry.Key);
            }

            foreach (var key in expired)
                _recentChatEvents.Remove(key);
        }

        private void CleanupExpectedSelfEchoEvents()
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-5);
            var expired = new List<string>();
            foreach (var entry in _expectedSelfEchoEvents)
            {
                if (entry.Value < cutoff)
                    expired.Add(entry.Key);
            }

            foreach (var key in expired)
                _expectedSelfEchoEvents.Remove(key);
        }

        private static string BuildChatEventKey(string eventType, string subject)
        {
            return (eventType ?? string.Empty).Trim().ToLowerInvariant()
                   + ":"
                   + (subject ?? string.Empty).Trim().ToLowerInvariant();
        }

        private IMyPlayer FindOnlinePlayerByName(string playerName)
        {
            try
            {
                if (MyAPIGateway.Players == null || string.IsNullOrWhiteSpace(playerName))
                    return null;

                var players = new List<IMyPlayer>();
                MyAPIGateway.Players.GetPlayers(players);
                foreach (var player in players)
                {
                    if (player != null
                        && string.Equals(
                            player.DisplayName,
                            playerName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return player;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogDebug("[TRACKING] FindOnlinePlayerByName failed: " + ex.Message);
            }

            return null;
        }

        private ulong FindKnownSteamIdByName(string playerName)
        {
            lock (_lockObject)
            {
                foreach (var entry in _playerNames)
                {
                    if (string.Equals(entry.Value, playerName, StringComparison.OrdinalIgnoreCase))
                        return entry.Key;
                }
            }

            return 0;
        }

        private void RememberKnownPlayer(IMyPlayer player)
        {
            if (player == null)
                return;

            lock (_lockObject)
            {
                _knownPlayers.Add(player.SteamUserId);
                _playerNames[player.SteamUserId] = player.DisplayName;
                if (!_deathEventCounters.ContainsKey(player.SteamUserId))
                    _deathEventCounters[player.SteamUserId] = 0;
            }

            if (player.Character != null)
                HookCharacterDeath(player.Character, player.SteamUserId, player.DisplayName);
        }

        private void NotifyOnlinePlayersChanged()
        {
            try
            {
                OnlinePlayersChanged?.Invoke();
            }
            catch (Exception ex)
            {
                LoggerUtil.LogDebug("[TRACKING] OnlinePlayersChanged handler failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (_pollingTimer != null)
            {
                _pollingTimer.Stop();
                _pollingTimer.Dispose();
            }

            lock (_lockObject)
            {
                _trackedCharacters.Clear();
                _trackedCharacterEntityIds.Clear();
                _deathEventCounters.Clear();
                _knownPlayers.Clear();
                _playerNames.Clear();
                _recentChatEvents.Clear();
                _expectedSelfEchoEvents.Clear();
            }
        }
    }
}
