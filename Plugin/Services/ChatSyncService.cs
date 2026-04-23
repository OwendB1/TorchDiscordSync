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
using Sandbox.Game;
using Sandbox.ModAPI;
using TorchDiscordSync.Plugin.Config;
using TorchDiscordSync.Plugin.Models;
using TorchDiscordSync.Plugin.Utils;
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
        private readonly HashSet<string> _syncedMessages = [];
        private readonly Dictionary<string, DateTime> _lastMessageTime = new();

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

                var messageKey = playerName + ":" + message;
                if (_syncedMessages.Contains(messageKey))
                {
                    LoggerUtil.LogDebug("Chat: duplicate game message suppressed");
                    return;
                }

                _syncedMessages.Add(messageKey);
                TrimSyncedMessages();

                var discordMessage = FormatGameMessageForDiscord(playerName, message);
                if (string.IsNullOrWhiteSpace(discordMessage) || discordMessage.StartsWith("/"))
                    return;

                var targetChannel = _config?.Discord?.ChatChannelId ?? 0;
                if (targetChannel == 0)
                {
                    LoggerUtil.LogWarning("Chat: no Discord ChatChannelId configured");
                    return;
                }

                var sent = await _discord.SendLogAsync(targetChannel, discordMessage);
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
                        $"Chat: failed to send to Discord channel {targetChannel}"
                    );
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(
                    $"Error sending game message to Discord: {ex.Message}"
                );
            }
        }

        /// <summary>
        /// Forward a Discord global channel message to all players in-game as a
        /// server-wide broadcast.
        /// NOTE: The author is set to "Discord" so that OnChatMessageProcessing
        /// can filter it out and prevent the message from looping back to Discord.
        /// </summary>
        public void SendDiscordMessageToGame(string discordUsername, string message)
        {
            try
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(discordUsername) || string.IsNullOrWhiteSpace(message))

                    // Prevent echo of TDS-relayed messages
                    if (discordUsername != null && discordUsername.IndexOf("TDS:", StringComparison.OrdinalIgnoreCase) >= 0)
                        return;

                    if (discordUsername?.IndexOf("bot", StringComparison.OrdinalIgnoreCase) >= 0) return;
                    if (!CheckRateLimit(discordUsername + "_discord")) return;

                    var messageKey = discordUsername + ":" + message;
                    if (!_syncedMessages.Add(messageKey))
                    {
                        LoggerUtil.LogDebug("Chat: duplicate Discord message suppressed");
                        return;
                    }

                    var gameMessage = FormatDiscordMessageForGame(discordUsername, message);

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
                        LoggerUtil.LogSuccess($"[G] Broadcasted to all players in game: {gameMessage}"
                        );
                        LogChatMessage(discordUsername, message, "discord", "global");
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogError($"Failed to broadcast chat message to game: {ex.Message}"
                        );
                    }
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogError($"Error sending Discord message to game: {ex.Message}"
                    );
                }


            }
            catch (Exception)
            {
                // ignored
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
                    LoggerUtil.LogInfo($"[CHAT_DEBUG] Discord→Faction: faction {factionId} has no players"
                    );
                    return;
                }

                // Strip emoji before delivering to in-game chat
                var cleanMessage = StripEmojisIfEnabled(message);
                var factionMsg = string.Format(
                    "[Discord] {0}: {1}",
                    discordUsername,
                    cleanMessage
                );

                var players = new List<IMyPlayer>();
                MyAPIGateway.Players.GetPlayers(players);

                var sent = 0;
                LoggerUtil.LogInfo(
                    $"[CHAT_DEBUG] Discord→Faction: sending to {faction.Players.Count} faction members"
                );

                foreach (var fp in faction.Players)
                {
                    var player = players.FirstOrDefault(p => (long)p.SteamUserId == fp.SteamID);
                    if (player == null)
                    {
                        LoggerUtil.LogInfo(
                            $"[CHAT_DEBUG] Discord→Faction: SteamID {fp.SteamID} not in game – skip"
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
                            $"[CHAT_DEBUG] Discord→Faction: SteamID {fp.SteamID} no Character/Identity – skip"
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
                            var broadcastMsg = string.Format(
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
                                $"[G][F] Faction global fallback broadcast: {broadcastMsg}"
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
                LoggerUtil.LogError($"[CHAT] Faction message error: {ex.Message}");
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
                        $"[CHAT_DEBUG] Game Faction→Discord: skip (DiscordChannelID=0 for {faction.Tag})"
                    );
                    return;
                }

                if (string.IsNullOrWhiteSpace(authorName) || string.IsNullOrWhiteSpace(message))
                    return;

                var discordText = $"{authorName}: {message}";
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

                var sent = await _discord.SendLogAsync(faction.DiscordChannelID, discordText);
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
                    $"[CHAT] Game faction → Discord error: {ex.Message}"
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
                LoggerUtil.LogError($"Error clearing chat cache: {ex.Message}");
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
                var cleanMessage = CleanMessageText(message);
                if (cleanMessage.StartsWith("/"))
                    return null;

                var formatted = $"{playerName}: {cleanMessage}";
                if (formatted.Length > GAME_TO_DISCORD_MAX_LENGTH)
                    formatted = formatted.Substring(0, GAME_TO_DISCORD_MAX_LENGTH - 3) + "...";

                return formatted;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogDebug(
                    $"Error formatting game message for Discord: {ex.Message}"
                );
                return $"{playerName}: {message}";
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
                var cleanMessage = CleanMessageText(message);

                // Limit length for in-game chat readability
                if (cleanMessage.Length > DISCORD_TO_GAME_MAX_LENGTH)
                    cleanMessage = cleanMessage.Substring(0, DISCORD_TO_GAME_MAX_LENGTH - 3) + "...";

                return $"[Discord] {discordUser}: {cleanMessage}";
            }
            catch (Exception ex)
            {
                LoggerUtil.LogDebug(
                    $"Error formatting Discord message for game: {ex.Message}"
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

                var msSinceLast = (int)(DateTime.UtcNow - lastTime).TotalMilliseconds;
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
                for (var i = arr.Length / 2; i < arr.Length; i++)
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
                LoggerUtil.LogDebug($"Error logging chat message: {ex.Message}");
            }
        }
    }
}
