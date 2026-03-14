// Plugin/Handlers/ChatModerator.cs
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using mamba.TorchDiscordSync.Plugin.Config;
using mamba.TorchDiscordSync.Plugin.Services;
using mamba.TorchDiscordSync.Plugin.Utils;
using Torch.API.Managers;

namespace mamba.TorchDiscordSync.Plugin.Handlers
{
    // Class for tracking user violations
    public class UserViolationRecord
    {
        public ulong UserId { get; set; }
        public int WarningCount { get; set; }
        public int MuteCount { get; set; }
        public DateTime LastMuteTime { get; set; }
        public bool IsMuted { get; set; }
        public DateTime MuteEndTime { get; set; }
    }

    public class ChatModerator
    {
        private readonly MainConfig _config;
        private readonly DiscordService _discordService;
        private readonly DatabaseService _db;
        
        // ========== NEW - TASK 2 ==========
        /// <summary>
        /// Blacklist configuration loaded from separate BlacklistConfig.xml file
        /// Instead of using _config.Chat.BlacklistedWords (which no longer exists)
        /// </summary>
        private readonly BlacklistConfig _blacklistConfig;
        // ========== END NEW ==========
        
        // Dictionary for tracking user violations
        private Dictionary<ulong, UserViolationRecord> _userViolations = new Dictionary<ulong, UserViolationRecord>();
        private object _lockObject = new object();
        private static Dictionary<int, DateTime> _lastMessages = new Dictionary<int, DateTime>();
        private const int DEFAULT_DEDUP_WINDOW = 3; // seconds

        /// <summary>
        /// Constructor - Initialize ChatModerator with config, services, and load BlacklistConfig
        /// 
        /// BEFORE:
        /// public ChatModerator(MainConfig config, DiscordService discordService, DatabaseService db)
        /// {
        ///     _config = config;
        ///     _discordService = discordService;
        ///     _db = db;
        /// }
        /// 
        /// AFTER (Updated for TASK 2):
        /// </summary>
        public ChatModerator(MainConfig config, DiscordService discordService, DatabaseService db)
        {
            _config = config;
            _discordService = discordService;
            _db = db;
            
            // ========== NEW - TASK 2 ==========
            // Load BlacklistConfig from separate BlacklistConfig.xml file
            try
            {
                _blacklistConfig = BlacklistConfig.Load();
                LoggerUtil.LogSuccess($"[CHAT_MODERATOR] Loaded blacklist with {_blacklistConfig.GetWordsArray().Length} words");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[CHAT_MODERATOR] Failed to load BlacklistConfig: {ex.Message}");
                _blacklistConfig = new BlacklistConfig();  // Fallback to default
            }
            // ========== END NEW ==========
        }

        // Process Discord message with moderation
        public void ProcessDiscordMessage(string message, string username, ulong channelId, ulong userId = 0)
        {
            try
            {
                // Check if moderation is enabled
                if (_config.Chat.EnableModeration && userId != 0)
                {
                    // Check if user is muted
                    if (IsUserMuted(userId))
                    {
                        LoggerUtil.LogInfo($"Blocked message from muted user {username}");
                        return;
                    }

                    string foundWord;
                    bool hasBlacklistedContent = ContainsBlacklistedWords(message, out foundWord) || 
                                               ContainsAttachmentsOrLinks(message);

                    if (hasBlacklistedContent)
                    {
                        // Add violation to user
                        var record = AddViolation(userId, username, message, "warning");
                        
                        // Check if user should be muted
                        if (record.WarningCount >= _config.Chat.MaxWarningsBeforeMute)
                        {
                            // Mute user
                            record.IsMuted = true;
                            record.MuteEndTime = DateTime.Now.AddMinutes(_config.Chat.MuteDurationMinutes);
                            record.MuteCount++;
                            record.WarningCount = 0;
                            
                            // Send mute message
                            string muteMsg = _config.Chat.MuteMessage.Replace("{minutes}", _config.Chat.MuteDurationMinutes.ToString());
                            // TODO: Send DM to user
                            
                            // Check if user should be kicked
                            if (record.MuteCount >= _config.Chat.MaxMutesBeforeKick)
                            {
                                record.MuteCount = 0; // Reset for kick
                                string kickMsg = _config.Chat.KickMessage;
                                // TODO: Kick user from channel
                            }
                        }
                        else
                        {
                            // Send warning message
                            // TODO: Send warning DM to user
                        }
                        
                        LoggerUtil.LogWarning($"Blocked message from {username}: contains '{foundWord}'");
                        return;
                    }
                }

                // Process normal message
                ProcessNormalDiscordMessage(message, username, channelId);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Error processing Discord message: " + ex.Message);
            }
        }

        // Process normal Discord message (without moderation)
        private void ProcessNormalDiscordMessage(string message, string username, ulong channelId)
        {
            try
            {
                if (_db == null)
                    return;

                var factions = _db.GetAllFactions();
                if (factions == null || factions.Count == 0)
                    return;

                var faction = null as Models.FactionModel;
                for (int i = 0; i < factions.Count; i++)
                {
                    if (factions[i].DiscordChannelID == channelId)
                    {
                        faction = factions[i];
                        break;
                    }
                }

                if (faction == null)
                    return;

                string sanitizedMsg = SecurityUtil.SanitizeMessage(message);
                string formattedMsg = "[" + faction.Tag + " - Discord] " + username + ": " + sanitizedMsg;

                Console.WriteLine("[CHAT_SYNC] " + formattedMsg);

                if (_config != null && _config.Debug)
                {
                    LoggerUtil.LogDebug("Discord -> Game (" + faction.Tag + "): " + formattedMsg);
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("ChatSync error: " + ex.Message);
            }
        }

        // Process game message to Discord
        public void ProcessGameMessage(string message, string playerName, long steamId)
        {
            try
            {
                // Check moderation if enabled (for game messages too)
                if (_config.Chat.EnableModeration)
                {
                    string foundWord;
                    if (ContainsBlacklistedWords(message, out foundWord) || ContainsAttachmentsOrLinks(message))
                    {
                        LoggerUtil.LogWarning($"Blocked message from {playerName}: contains blacklisted content");
                        return; // Block message
                    }
                }

                if (_db == null)
                    return;

                var factions = _db.GetAllFactions();
                if (factions == null || factions.Count == 0)
                    return;

                var playerFaction = null as Models.FactionModel;
                for (int i = 0; i < factions.Count; i++)
                {
                    if (factions[i].Players != null)
                    {
                        for (int j = 0; j < factions[i].Players.Count; j++)
                        {
                            if (factions[i].Players[j].SteamID == steamId)
                            {
                                playerFaction = factions[i];
                                break;
                            }
                        }
                    }
                    if (playerFaction != null) break;
                }

                if (playerFaction == null)
                    return;

                string sanitizedMsg = SecurityUtil.SanitizeMessage(message);
                string formattedMsg = playerName + ": " + sanitizedMsg;

                if (playerFaction.DiscordChannelID != 0 && _discordService != null)
                {
                    // LINIJA 198 - POPRAVLJENO: Dodan await
                    var _ = _discordService.SendLogAsync(playerFaction.DiscordChannelID, formattedMsg);
                }

                if (_config != null && _config.Debug)
                {
                    LoggerUtil.LogDebug("Game -> Discord (" + playerFaction.Tag + "): " + formattedMsg);
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("ChatSync error: " + ex.Message);
            }
        }

        // Check if user is muted
        private bool IsUserMuted(ulong userId)
        {
            lock (_lockObject)
            {
                if (_userViolations.ContainsKey(userId))
                {
                    var record = _userViolations[userId];
                    if (record.IsMuted && DateTime.Now < record.MuteEndTime)
                    {
                        return true;
                    }
                    else if (record.IsMuted && DateTime.Now >= record.MuteEndTime)
                    {
                        // Mute expired
                        record.IsMuted = false;
                    }
                }
            }
            return false;
        }

        // Add violation to user
        private UserViolationRecord AddViolation(ulong userId, string username, string message, string violationType)
        {
            UserViolationRecord record;
            
            lock (_lockObject)
            {
                if (!_userViolations.ContainsKey(userId))
                {
                    _userViolations[userId] = new UserViolationRecord { UserId = userId };
                }
                
                record = _userViolations[userId];
                
                if (violationType == "warning")
                {
                    record.WarningCount++;
                }
                else if (violationType == "mute")
                {
                    record.MuteCount++;
                    record.WarningCount = 0; // Reset warnings
                }
            }

            // Log violation to admin channel
            if (_config.Chat.AdminLogChannelId != 0 && _discordService != null)
            {
                string logMessage = $"🛡️ **Chat Violation**\nUser: {username} ({userId})\nMessage: {message}\nType: {violationType}\nWarnings: {record.WarningCount}, Mutes: {record.MuteCount}";
                // LINIJA 263 - POPRAVLJENO: Dodan await
                var _ = _discordService.SendLogAsync(_config.Chat.AdminLogChannelId, logMessage);
            }

            return record;
        }

        // ========== UPDATED METHOD - TASK 2 ==========
        /// <summary>
        /// Check for blacklisted words using BlacklistConfig instead of MainConfig
        /// 
        /// BEFORE (Old - using MainConfig.Chat.BlacklistedWords):
        /// private bool ContainsBlacklistedWords(string message, out string foundWord)
        /// {
        ///     foundWord = null;
        ///     if (_config.Chat.BlacklistedWords == null || _config.Chat.BlacklistedWords.Length == 0)
        ///         return false;
        ///
        ///     string lowerMessage = message.ToLower();
        ///     for (int i = 0; i < _config.Chat.BlacklistedWords.Length; i++)
        ///     {
        ///         string word = _config.Chat.BlacklistedWords[i].ToLower();
        ///         if (lowerMessage.Contains(word))
        ///         {
        ///             foundWord = word;
        ///             return true;
        ///         }
        ///     }
        ///     return false;
        /// }
        /// 
        /// AFTER (New - using BlacklistConfig):
        /// </summary>
        private bool ContainsBlacklistedWords(string message, out string foundWord)
        {
            foundWord = null;
            
            // Get words from BlacklistConfig instead of MainConfig
            var words = _blacklistConfig.GetWordsArray();
            if (words == null || words.Length == 0)
                return false;

            // Determine message to check based on case sensitivity setting
            string messageToCheck = _blacklistConfig.CaseSensitive 
                ? message 
                : message.ToLower();
            
            for (int i = 0; i < words.Length; i++)
            {
                // Apply case sensitivity setting
                string word = _blacklistConfig.CaseSensitive 
                    ? words[i] 
                    : words[i].ToLower();
                
                if (_blacklistConfig.PartialMatch)
                {
                    // Match partial word: "hack" matches "hacking"
                    if (messageToCheck.Contains(word))
                    {
                        foundWord = word;
                        return true;
                    }
                }
                else
                {
                    // Match whole word only: "hack" does NOT match "hacking"
                    if (ContainsWholeWord(messageToCheck, word))
                    {
                        foundWord = word;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Helper method to check if message contains a whole word (not partial)
        /// Used when PartialMatch is disabled in BlacklistConfig
        /// </summary>
        private bool ContainsWholeWord(string message, string word)
        {
            try
            {
                // Use regex for whole word matching with word boundaries
                string pattern = @"\b" + Regex.Escape(word) + @"\b";
                return Regex.IsMatch(message, pattern);
            }
            catch
            {
                // Fallback to simple contains if regex fails
                return message.Contains(word);
            }
        }
        // ========== END UPDATED METHOD ==========

        // Check for attachments or links
        private bool ContainsAttachmentsOrLinks(string message)
        {
            // Check for http/https links
            if (message.Contains("http://") || message.Contains("https://"))
                return true;
                
            // Check for Discord attachment formats
            if (message.Contains("```") && message.Contains("attachment"))
                return true;
                
            return false;
        }

        public bool ShouldBlockMessage(TorchChatMessage msg, string channelName)
        {
            try
            {
                // Check if message is empty
                if (string.IsNullOrEmpty(msg.Message))
                    return true;

                // PRIORITY 1: Block /tds commands (handled separately)
                if (msg.Message.StartsWith("/tds "))
                    return true;

                // PRIORITY 2: Block private messages
                if (channelName.StartsWith("Private"))
                    return true;

                // PRIORITY 3: Block faction messages
                if (channelName.StartsWith("Faction"))
                    return true;

                // PRIORITY 4: Block Discord loop messages
                if (msg.Message.StartsWith("[Discord] "))
                    return true;

                // PRIORITY 5: Block death messages from reaching Discord
                // Death messages come from "Server" with emoticons configured in DeathMessageEmotes
                if (IsDeathMessage(msg.Author, msg.Message))
                {
                    LoggerUtil.LogDebug($"[CHAT_MOD] Blocked death message: {msg.Message}");
                    return true; // Already sent to Discord via LogDeathAsync
                }

                // PRIORITY 6: Block duplicate messages within time window
                if (IsRecentDuplicate(msg.Message))
                {
                    LoggerUtil.LogDebug(
                        "[CHAT_MOD] Blocked duplicate message (within dedup window)"
                    );
                    return true;
                }

                return false; // Message should be processed
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[CHAT_MOD] Error filtering message: {ex.Message}");
                return false; // On error, allow message through
            }
        }        

        /// <summary>
        /// Check if chat message should be blocked (for game->Discord forwarding).
        /// This is different from ProcessDiscordMessage - this is for game messages going to Discord.
        /// Returns true if message should be BLOCKED, false if it should be processed.
        /// </summary>
        public bool ShouldBlockGameMessage(string author, string message, string channelName)
        {
            try
            {
                // Check if message is empty
                if (string.IsNullOrEmpty(message))
                    return true;

                // Block private messages
                if (channelName.StartsWith("Private"))
                    return true;

                // Block faction messages
                if (channelName.StartsWith("Faction"))
                    return true;

                // Block Discord loop messages
                if (message.StartsWith("[Discord] "))
                    return true;

                // IMPORTANT: Block death messages from reaching Discord
                // Death messages come from "Server" with emoticons configured in DeathMessageEmotes
                if (IsDeathMessage(author, message))
                {
                    LoggerUtil.LogDebug($"[CHAT_MOD] Blocked death message: {message}");
                    return true; // Already sent to Discord via DeathMessageHandler
                }

                // Block duplicate messages within time window
                if (IsRecentDuplicate(message))
                {
                    LoggerUtil.LogDebug(
                        "[CHAT_MOD] Blocked duplicate message (within dedup window)"
                    );
                    return true;
                }

                return false; // Message should be processed
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[CHAT_MOD] Error filtering game message: {ex.Message}");
                return false; // On error, allow message through
            }
        }

        /// <summary>
        /// Check if message is a death message by detecting emoticons from config.
        /// Reads emoticons from MainConfig.Death.DeathMessageEmotes
        /// </summary>
        private bool IsDeathMessage(string author, string message)
        {
            try
            {
                // Must come from Server
                if (author != "Server")
                    return false;

                // Check for configured emoticons in message
                if (
                    _config?.Death == null
                    || string.IsNullOrEmpty(_config.Death.DeathMessageEmotes)
                )
                {
                    // Fallback: check for default emoticons
                    return message.Contains("📢")
                        || message.Contains("⚔️")
                        || message.Contains("💀")
                        || message.Contains("🔥")
                        || message.Contains("⚡");
                }

                var emotes = _config.Death.DeathMessageEmotes.Split(',');
                foreach (var emote in emotes)
                {
                    if (message.Contains(emote.Trim()))
                    {
                        LoggerUtil.LogDebug($"[CHAT_MOD] Detected death emoticon: {emote}");
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[CHAT_MOD] Error detecting death message: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if message is a recent duplicate within the deduplication window.
        /// Uses message hash and time-based check (no new service needed).
        /// </summary>
        private bool IsRecentDuplicate(string message)
        {
            try
            {
                int hash = message.GetHashCode();
                DateTime now = DateTime.UtcNow;
                int dedupWindow =
                    _config?.Death?.MessageDeduplicationWindowSeconds ?? DEFAULT_DEDUP_WINDOW;

                if (_lastMessages.TryGetValue(hash, out var lastTime))
                {
                    if ((now - lastTime).TotalSeconds < dedupWindow)
                    {
                        LoggerUtil.LogDebug(
                            $"[CHAT_MOD] Duplicate detected (hash: {hash}, window: {dedupWindow}s)"
                        );
                        return true; // DUPLICATE
                    }
                }

                _lastMessages[hash] = now;
                return false; // NEW message
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[CHAT_MOD] Error checking duplicate: {ex.Message}");
                return false; // On error, allow through
            }
        }

        /// <summary>
        /// Clean old message hashes from cache (prevents memory leak).
        /// Call periodically from plugin timer.
        /// </summary>
        public void CleanOldEntries()
        {
            try
            {
                var now = DateTime.UtcNow;
                var toRemove = new List<int>();
                int dedupWindow =
                    _config?.Death?.MessageDeduplicationWindowSeconds ?? DEFAULT_DEDUP_WINDOW;

                foreach (var kvp in _lastMessages)
                {
                    if ((now - kvp.Value).TotalSeconds > dedupWindow * 2)
                    {
                        toRemove.Add(kvp.Key);
                    }
                }

                foreach (var hash in toRemove)
                {
                    _lastMessages.Remove(hash);
                }

                if (toRemove.Count > 0)
                {
                    LoggerUtil.LogDebug($"[CHAT_MOD] Cleaned {toRemove.Count} old cache entries");
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[CHAT_MOD] Error cleaning cache: {ex.Message}");
            }
        }
    }
}