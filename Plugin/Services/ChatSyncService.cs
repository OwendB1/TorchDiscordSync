// Plugin/Services/ChatSyncService.cs
// Bidirectional chat synchronization service.
// Game global chat  →  Discord global channel
// Discord global channel  →  Game global chat  (broadcast)
// Game faction chat  →  Discord faction channel
// Discord faction channel  →  Game faction members  (private message per member)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TorchDiscordSync.Plugin.Config;
using TorchDiscordSync.Plugin.Models;
using TorchDiscordSync.Plugin.Utils;
using Sandbox.Game;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace TorchDiscordSync.Plugin.Services
{
    /// <summary>
    /// Bidirectional chat synchronization service.
    /// Handles routing of messages between Space Engineers global/faction chat
    /// and the configured Discord channels.
    /// </summary>
    public class ChatSyncService
    {
        private readonly DiscordService _discord;
        private readonly MainConfig _config;
        private readonly DatabaseService _db;

        // ---- duplicate / rate-limit tracking ----
        private readonly HashSet<string> _syncedMessages = new HashSet<string>();
        private readonly Dictionary<string, DateTime> _lastMessageTime =
            new Dictionary<string, DateTime>();

        // Rate-limit: minimum milliseconds between messages from the same source
        private const int MESSAGE_THROTTLE_MS = 500;
        private const int DISCORD_SINGLE_MESSAGE_MAX_LENGTH = 2000;
        private const int GAME_TO_DISCORD_MAX_LENGTH = DISCORD_SINGLE_MESSAGE_MAX_LENGTH * 2;
        private const int DISCORD_TO_GAME_MAX_LENGTH = 200;
        private const int FACTION_GLOBAL_FALLBACK_MAX_LENGTH = 400;

        public ChatSyncService(DiscordService discord, MainConfig config, DatabaseService db)
        {
            _discord = discord;
            _config = config;
            _db = db;
            LoggerUtil.LogDebug("ChatSyncService initialized");
        }

        // ============================================================
        // GAME → DISCORD
        // ============================================================

        /// <summary>
        /// Forward a player's in-game global chat message to the Discord global channel.
        /// </summary>
        public async Task SendGameMessageToDiscordAsync(string playerName, string message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(message))
                    return;

                if (!CheckRateLimit(playerName + "_game"))
                    return;

                string messageKey = playerName + ":" + message;
                if (_syncedMessages.Contains(messageKey))
                {
                    LoggerUtil.LogDebug("Chat: duplicate game message suppressed");
                    return;
                }

                _syncedMessages.Add(messageKey);
                TrimSyncedMessages();

                string discordMessage = FormatGameMessageForDiscord(playerName, message);
                if (string.IsNullOrWhiteSpace(discordMessage) || discordMessage.StartsWith("/"))
                    return;

                ulong targetChannel = _config?.Discord?.ChatChannelId ?? 0;
                if (targetChannel == 0)
                {
                    LoggerUtil.LogWarning("Chat: no Discord ChatChannelId configured");
                    return;
                }

                bool sent = await _discord.SendLogAsync(targetChannel, discordMessage);
                if (sent)
                {
                    LoggerUtil.LogInfo(
                        string.Format(
                            "[G] Game global → Discord channel {0}: {1}: {2}",
                            targetChannel,
                            playerName,
                            message
                        )
                    );
                    LogChatMessage(playerName, message, "game", "global");
                }
                else
                {
                    LoggerUtil.LogWarning(
                        string.Format("Chat: failed to send to Discord channel {0}", targetChannel)
                    );
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(
                    string.Format("Error sending game message to Discord: {0}", ex.Message)
                );
            }
        }

        /// <summary>
        /// Forward a Discord global channel message to all players in-game as a
        /// server-wide broadcast.
        /// NOTE: The author is set to "Discord" so that OnChatMessageProcessing
        /// can filter it out and prevent the message from looping back to Discord.
        /// </summary>
        public async Task SendDiscordMessageToGameAsync(string discordUsername, string message)
        {
            try
            {
                if (
                    string.IsNullOrWhiteSpace(discordUsername) || string.IsNullOrWhiteSpace(message)
                )
                    return;

                // Prevent echo of TDS-relayed messages
                if (discordUsername.IndexOf("TDS:", StringComparison.OrdinalIgnoreCase) >= 0)
                    return;

                if (discordUsername.IndexOf("bot", StringComparison.OrdinalIgnoreCase) >= 0)
                    return;

                if (!CheckRateLimit(discordUsername + "_discord"))
                    return;

                string messageKey = discordUsername + ":" + message;
                if (_syncedMessages.Contains(messageKey))
                {
                    LoggerUtil.LogDebug("Chat: duplicate Discord message suppressed");
                    return;
                }

                _syncedMessages.Add(messageKey);

                string gameMessage = FormatDiscordMessageForGame(discordUsername, message);

                LoggerUtil.LogInfo(
                    string.Format(
                        "[G] Discord → Game global broadcast: {0}: {1}",
                        discordUsername,
                        message
                    )
                );

                try
                {
                    // Author is "Discord" – the loop filter in CommandProcessor.HandleChatMessage
                    // blocks all messages with Author == "Discord" or "TDS" from being re-sent.
                    // playerId=0 → broadcast to ALL players.
                    MyVisualScriptLogicProvider.SendChatMessage(gameMessage, "Discord", 0, "Blue");
                    LoggerUtil.LogSuccess(
                        string.Format("[G] Broadcasted to all players in game: {0}", gameMessage)
                    );
                    LogChatMessage(discordUsername, message, "discord", "global");
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogError(
                        string.Format("Failed to broadcast chat message to game: {0}", ex.Message)
                    );
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(
                    string.Format("Error sending Discord message to game: {0}", ex.Message)
                );
            }
        }

        /// <summary>
        /// Forward a Discord faction channel message to all online faction members
        /// as a private in-game message.
        /// The FactionDiscordToGlobalFallback config flag can optionally also
        /// broadcast to global chat; however that broadcast uses the "TDS" author
        /// which is filtered by the loop-guard in CommandProcessor.HandleChatMessage.
        /// </summary>
        public async Task SendDiscordMessageToFactionInGameAsync(
            int factionId,
            string discordUsername,
            string message
        )
        {
            try
            {
                if (
                    string.IsNullOrWhiteSpace(discordUsername) || string.IsNullOrWhiteSpace(message)
                )
                    return;

                var faction = _db?.GetFaction(factionId);
                if (faction?.Players == null || faction.Players.Count == 0)
                {
                    LoggerUtil.LogInfo(
                        string.Format(
                            "[CHAT_DEBUG] Discord→Faction: faction {0} has no players",
                            factionId
                        )
                    );
                    return;
                }

                // Strip emoji before delivering to in-game chat
                string cleanMessage = StripEmojisIfEnabled(message);
                string factionMsg = string.Format(
                    "[Discord] {0}: {1}",
                    discordUsername,
                    cleanMessage
                );

                var players = new List<IMyPlayer>();
                MyAPIGateway.Players.GetPlayers(players);

                int sent = 0;
                LoggerUtil.LogInfo(
                    string.Format(
                        "[CHAT_DEBUG] Discord→Faction: sending to {0} faction members",
                        faction.Players.Count
                    )
                );

                foreach (var fp in faction.Players)
                {
                    var player = players.FirstOrDefault(p => (long)p.SteamUserId == fp.SteamID);
                    if (player == null)
                    {
                        LoggerUtil.LogInfo(
                            string.Format(
                                "[CHAT_DEBUG] Discord→Faction: SteamID {0} not in game – skip",
                                fp.SteamID
                            )
                        );
                        continue;
                    }

                    long targetId = 0;
                    if (player.Character != null)
                        targetId = player.Character.EntityId;
                    if (targetId == 0 && player.IdentityId != 0)
                        targetId = player.IdentityId;

                    if (targetId == 0)
                    {
                        LoggerUtil.LogInfo(
                            string.Format(
                                "[CHAT_DEBUG] Discord→Faction: SteamID {0} no Character/Identity – skip",
                                fp.SteamID
                            )
                        );
                        continue;
                    }

                    try
                    {
                        // Author is "TDS"; the loop-guard in HandleChatMessage
                        // ensures this never gets forwarded back to Discord.
                        // playerId=EntityId → private message to this player only.
                        MyVisualScriptLogicProvider.SendChatMessage(
                            factionMsg,
                            "TDS",
                            targetId,
                            "Blue"
                        );
                        sent++;
                        LoggerUtil.LogInfo(
                            string.Format(
                                "[F][W] Discord→Faction private: sent to SteamID {0} (EntityId={1})",
                                fp.SteamID,
                                targetId
                            )
                        );
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogError(
                            string.Format(
                                "[CHAT_DEBUG] Discord→Faction: SendChatMessage failed for SteamID {0}: {1}",
                                fp.SteamID,
                                ex.Message
                            )
                        );
                    }
                }

                if (sent > 0)
                {
                    // Optional global fallback (author="TDS" → loop-safe)
                    if (_config?.Faction?.FactionDiscordToGlobalFallback == true)
                    {
                        try
                        {
                            string broadcastMsg = string.Format(
                                "[{0} Discord] {1}: {2}",
                                faction.Tag,
                                discordUsername,
                                cleanMessage
                            );

                            if (broadcastMsg.Length > FACTION_GLOBAL_FALLBACK_MAX_LENGTH)
                                broadcastMsg = broadcastMsg.Substring(
                                    0,
                                    FACTION_GLOBAL_FALLBACK_MAX_LENGTH - 3) + "...";

                            // Author "TDS" is filtered by the loop-guard in HandleChatMessage.
                            // playerId=0 → global broadcast fallback (visible to all).
                            LoggerUtil.LogDebug(
                                string.Format(
                                    "[G][F] Faction global fallback broadcast: {0}",
                                    broadcastMsg
                                )
                            );
                            MyVisualScriptLogicProvider.SendChatMessage(
                                broadcastMsg,
                                "TDS",
                                0,
                                "Blue"
                            );
                        }
                        catch
                        { /* non-critical */
                        }
                    }

                    LoggerUtil.LogInfo(
                        string.Format(
                            "[CHAT] Discord → Faction {0}: {1}: {2} (sent to {3} members)",
                            faction.Tag,
                            discordUsername,
                            message,
                            sent
                        )
                    );
                }
                else
                {
                    LoggerUtil.LogInfo(
                        "[CHAT_DEBUG] Discord→Faction: sent=0 (no in-game player received)"
                    );
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(string.Format("[CHAT] Faction message error: {0}", ex.Message));
            }

            await Task.CompletedTask;
        }

        // ============================================================
        // GAME → DISCORD (faction)
        // ============================================================

        /// <summary>
        /// Forward an in-game faction chat message to the faction's Discord channel.
        /// Called when a player writes in faction chat and the faction has a mapped
        /// Discord channel ID.
        /// </summary>
        public async Task SendGameFactionMessageToDiscordAsync(
            FactionModel faction,
            string authorName,
            string message
        )
        {
            try
            {
                if (faction == null)
                    return;

                if (faction.DiscordChannelID == 0)
                {
                    LoggerUtil.LogInfo(
                        string.Format(
                            "[CHAT_DEBUG] Game Faction→Discord: skip (DiscordChannelID=0 for {0})",
                            faction.Tag
                        )
                    );
                    return;
                }

                if (string.IsNullOrWhiteSpace(authorName) || string.IsNullOrWhiteSpace(message))
                    return;

                string discordText = string.Format("{0}: {1}", authorName, message);
                if (discordText.Length > GAME_TO_DISCORD_MAX_LENGTH)
                    discordText = discordText.Substring(0, GAME_TO_DISCORD_MAX_LENGTH - 3) + "...";

                LoggerUtil.LogInfo(
                    string.Format(
                        "[CHAT_DEBUG] Game Faction→Discord: sending to channel {0} ({1}): \"{2}\"",
                        faction.DiscordChannelID,
                        faction.Tag,
                        discordText
                    )
                );

                bool sent = await _discord.SendLogAsync(faction.DiscordChannelID, discordText);
                if (sent)
                    LoggerUtil.LogInfo(
                        string.Format(
                            "[F] Game faction {0} → Discord channel {1}: {2}: {3}",
                            faction.Tag,
                            faction.DiscordChannelID,
                            authorName,
                            message
                        )
                    );
                else
                    LoggerUtil.LogInfo(
                        "[CHAT_DEBUG] Game Faction→Discord: SendLogAsync returned false"
                    );
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(
                    string.Format("[CHAT] Game faction → Discord error: {0}", ex.Message)
                );
            }

            await Task.CompletedTask;
        }

        // ============================================================
        // CACHE MANAGEMENT
        // ============================================================

        /// <summary>
        /// Clear internal duplicate-detection caches.
        /// </summary>
        public void ClearCache()
        {
            try
            {
                _syncedMessages.Clear();
                _lastMessageTime.Clear();
                LoggerUtil.LogDebug("Chat sync cache cleared");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(string.Format("Error clearing chat cache: {0}", ex.Message));
            }
        }

        // ============================================================
        // PRIVATE HELPERS
        // ============================================================

        /// <summary>
        /// Format an in-game message for posting to Discord.
        /// </summary>
        private string FormatGameMessageForDiscord(string playerName, string message)
        {
            try
            {
                string cleanMessage = CleanMessageText(message);
                if (cleanMessage.StartsWith("/"))
                    return null;

                string formatted = string.Format("{0}: {1}", playerName, cleanMessage);
                if (formatted.Length > GAME_TO_DISCORD_MAX_LENGTH)
                    formatted = formatted.Substring(0, GAME_TO_DISCORD_MAX_LENGTH - 3) + "...";

                return formatted;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogDebug(
                    string.Format("Error formatting game message for Discord: {0}", ex.Message)
                );
                return string.Format("{0}: {1}", playerName, message);
            }
        }

        /// <summary>
        /// Format a Discord message for display in the in-game chat.
        /// Applies emoji stripping when the config flag is enabled.
        /// </summary>
        private string FormatDiscordMessageForGame(string discordUser, string message)
        {
            try
            {
                string cleanMessage = CleanMessageText(message);

                // Limit length for in-game chat readability
                if (cleanMessage.Length > DISCORD_TO_GAME_MAX_LENGTH)
                    cleanMessage = cleanMessage.Substring(0, DISCORD_TO_GAME_MAX_LENGTH - 3) + "...";

                return string.Format("[Discord] {0}: {1}", discordUser, cleanMessage);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogDebug(
                    string.Format("Error formatting Discord message for game: {0}", ex.Message)
                );
                return message;
            }
        }

        /// <summary>
        /// Clean raw message text: strip Discord mentions, replace URLs, normalise
        /// whitespace, and optionally strip emoji.
        /// </summary>
        private string CleanMessageText(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text))
                    return "";

                // Remove Discord user/role/channel mentions  <@123>  <@!123>  <#123>
                text = Regex.Replace(text, @"<[@#!&][0-9]+>", "");

                // Replace URLs with a placeholder
                text = Regex.Replace(text, @"https?://[^\s]+", "[URL]");

                // Strip emoji when the config flag is set
                text = StripEmojisIfEnabled(text);

                // Normalise whitespace
                text = Regex.Replace(text, @"\s+", " ");

                return text.Trim();
            }
            catch
            {
                return text;
            }
        }

        /// <summary>
        /// Strip emoji from text when ChatConfig.StripEmojisForInGameChat == true.
        /// Delegates to TextSanitizationUtil.StripEmojisFromDiscordMessage.
        /// </summary>
        private string StripEmojisIfEnabled(string text)
        {
            if (_config?.Chat?.StripEmojisForInGameChat == true)
                return TextSanitizationUtil.StripEmojisFromDiscordMessage(text);
            return text;
        }

        /// <summary>
        /// Simple per-source rate limiter.
        /// Returns false when the source sends faster than MESSAGE_THROTTLE_MS.
        /// </summary>
        private bool CheckRateLimit(string key)
        {
            try
            {
                DateTime lastTime;
                if (!_lastMessageTime.TryGetValue(key, out lastTime))
                {
                    _lastMessageTime[key] = DateTime.UtcNow;
                    return true;
                }

                int msSinceLast = (int)(DateTime.UtcNow - lastTime).TotalMilliseconds;
                if (msSinceLast < MESSAGE_THROTTLE_MS)
                    return false;

                _lastMessageTime[key] = DateTime.UtcNow;
                return true;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Prevent unbounded growth of the duplicate-message cache.
        /// </summary>
        private void TrimSyncedMessages()
        {
            if (_syncedMessages.Count > 1000)
            {
                // HashSet has no O(1) front-removal; rebuild with the last 500 entries
                var arr = new string[_syncedMessages.Count];
                _syncedMessages.CopyTo(arr);
                _syncedMessages.Clear();
                for (int i = arr.Length / 2; i < arr.Length; i++)
                    _syncedMessages.Add(arr[i]);
            }
        }

        /// <summary>
        /// Append a chat event to the database event log.
        /// </summary>
        private void LogChatMessage(string author, string message, string source, string channel)
        {
            try
            {
                if (_db != null)
                {
                    var evt = new EventLogModel
                    {
                        EventType = "Chat",
                        Details = string.Format(
                            "[{0}] {1}: {2} ({3})",
                            source.ToUpper(),
                            author,
                            message,
                            channel
                        ),
                        Timestamp = DateTime.UtcNow,
                    };
                    _db.LogEvent(evt);
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogDebug(string.Format("Error logging chat message: {0}", ex.Message));
            }
        }
    }
}
