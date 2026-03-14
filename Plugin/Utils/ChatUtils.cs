// Plugin/Utils/ChatUtils.cs
using System;
using mamba.TorchDiscordSync.Plugin.Config;
using mamba.TorchDiscordSync.Plugin.Services;
using mamba.TorchDiscordSync.Plugin.Utils;
using Sandbox.Game;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace mamba.TorchDiscordSync.Plugin.Utils
{
    /// <summary>
    /// Chat utility methods for sending messages to players in-game.
    /// All Send* methods accept an optional steamId parameter:
    ///   steamId = 0  → broadcast to ALL players  [G]
    ///   steamId > 0  → private message to that player only  [W]
    /// The PRIVATE_PREFIX tag on every command-response message prevents
    /// CommandProcessor from forwarding it back to Discord.
    /// </summary>
    public static class ChatUtils
    {
        // Prefix for private messages - used to filter out from Discord forwarding
        private const string PRIVATE_PREFIX = "[PRIVATE_CMD]";

        // ============================================================
        // BROADCAST HELPERS (no steamId - intentionally global)
        // ============================================================

        /// <summary>
        /// Send message to server console only.
        /// </summary>
        public static void SendServerMessage(string message)
        {
            try
            {
                Console.WriteLine($"[SERVER] {message}");
                LoggerUtil.LogDebug($"[CONSOLE] {message}");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"Chat message send error: {ex.Message}");
            }
        }

        /// <summary>
        /// Broadcast a message to ALL players in-game (playerId=0).
        /// Used for server-wide announcements (join/leave/death).
        /// </summary>
        public static void BroadcastToServer(string message)
        {
            try
            {
                LoggerUtil.LogDebug($"[G][BROADCAST] {message}");
                MyVisualScriptLogicProvider.SendChatMessage(message, "Server", 0, "White");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"Broadcast error: {ex.Message}");
            }
        }

        // ============================================================
        // TARGETED SEND HELPERS  (steamId > 0 → private; 0 → broadcast)
        // ============================================================

        /// <summary>
        /// Send a warning message. Private to steamId if provided, broadcast otherwise.
        /// </summary>
        public static void SendWarning(string message, long steamId = 0)
        {
            try
            {
                string formattedMsg = $"[!] {message}";
                LoggerUtil.LogDebug($"[WARNING] {message}");
                SendPrivateToPlayer(formattedMsg, "Yellow", steamId);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"Warning error: {ex.Message}");
            }
        }

        /// <summary>
        /// Send an error message. Private to steamId if provided, broadcast otherwise.
        /// </summary>
        public static void SendError(string message, long steamId = 0)
        {
            try
            {
                string formattedMsg = $"[FAIL] {message}";
                LoggerUtil.LogDebug($"[ERROR] {message}");
                SendPrivateToPlayer(formattedMsg, "Red", steamId);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"Error send failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Send a success message. Private to steamId if provided, broadcast otherwise.
        /// </summary>
        public static void SendSuccess(string message, long steamId = 0)
        {
            try
            {
                string formattedMsg = $"[OK] {message}";
                LoggerUtil.LogDebug($"[SUCCESS] {message}");
                SendPrivateToPlayer(formattedMsg, "Green", steamId);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"Success send failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Send an info message. Private to steamId if provided, broadcast otherwise.
        /// </summary>
        public static void SendInfo(string message, long steamId = 0)
        {
            try
            {
                string formattedMsg = $"[I] {message}";
                LoggerUtil.LogDebug($"[INFO] {message}");
                SendPrivateToPlayer(formattedMsg, "Blue", steamId);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"Info send failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Send multi-line help text. Private to steamId if provided, broadcast otherwise.
        /// </summary>
        public static void SendHelpText(string helpText, long steamId = 0)
        {
            try
            {
                LoggerUtil.LogDebug($"[HELP] Sending help text");
                string prefixedText = $"{PRIVATE_PREFIX}\n{helpText}";
                long entityId = ResolveEntityId(steamId);
                string tag = entityId != 0 ? "[W]" : "[G]";
                LoggerUtil.LogDebug($"{tag} SendHelpText → entityId={entityId}");
                MyVisualScriptLogicProvider.SendChatMessage(prefixedText, "TDS", entityId, "Green");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"Help text send failed: {ex.Message}");
            }
        }

        // ============================================================
        // PRIVATE IMPLEMENTATION
        // ============================================================

        /// <summary>
        /// Core send method.
        /// Resolves EntityId from steamId (if > 0) and sends a targeted private message.
        /// Falls back to broadcast (playerId=0) when steamId is 0 or player is offline.
        /// All messages carry PRIVATE_PREFIX so HandleChatMessage never forwards them to Discord.
        /// </summary>
        private static void SendPrivateToPlayer(string message, string color, long steamId)
        {
            try
            {
                string prefixedMessage = $"{PRIVATE_PREFIX} {message}";
                long entityId = ResolveEntityId(steamId);
                string tag = entityId != 0 ? "[W]" : "[G]";
                LoggerUtil.LogDebug(
                    $"{tag} SendPrivateToPlayer steamId={steamId} entityId={entityId} [{color}] {message}"
                );
                MyVisualScriptLogicProvider.SendChatMessage(
                    prefixedMessage,
                    "TDS",
                    entityId,
                    color
                );
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"Failed to send private chat message: {ex.Message}");
                SendServerMessage(message);
            }
        }

        /// <summary>
        /// Resolves a player's Character EntityId from their SteamId.
        /// Returns 0 if steamId is 0, player is offline, or has no character –
        /// in that case the caller falls back to broadcast.
        /// </summary>
        private static long ResolveEntityId(long steamId)
        {
            if (steamId <= 0)
                return 0;

            try
            {
                var players = new System.Collections.Generic.List<IMyPlayer>();
                MyAPIGateway.Players.GetPlayers(players);

                foreach (var p in players)
                {
                    if ((long)p.SteamUserId != steamId)
                        continue;

                    // Prefer Character EntityId; fall back to IdentityId
                    if (p.Character != null && p.Character.EntityId != 0)
                        return p.Character.EntityId;

                    if (p.IdentityId != 0)
                        return p.IdentityId;
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogDebug($"ResolveEntityId failed for steamId={steamId}: {ex.Message}");
            }

            return 0;
        }

        // ============================================================
        // FILTER HELPERS
        // ============================================================

        /// <summary>
        /// Returns true when a message was sent by this plugin as a command response.
        /// Used by HandleChatMessage to skip forwarding to Discord.
        /// </summary>
        public static bool IsPrivateMessage(string message)
        {
            return !string.IsNullOrEmpty(message) && message.Contains(PRIVATE_PREFIX);
        }

        public static string GetPrivateMessagePrefix()
        {
            return PRIVATE_PREFIX;
        }

        // ============================================================
        // PROCESS CHAT MESSAGE (moved from Plugin/index.cs)
        // ============================================================

        /// <summary>
        /// Route an incoming chat message to the correct destination.
        /// </summary>
        public static void ProcessChatMessage(
            string message,
            string author,
            string channel,
            ChatSyncService chatSync,
            PlayerTrackingService playerTracking,
            MainConfig config
        )
        {
            LoggerUtil.LogDebug(
                $@"[CHAT PROCESS] Channel: {channel} | Author: {author} | Message: {message}"
            );

            if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(author))
            {
                LoggerUtil.LogDebug($"[CHAT PROCESS] - returned due to null/empty");
                return;
            }

            // Prevent duplication: skip Server death messages already sent from death event
            if (author == "Server" && (message.Contains("died") || message.Contains("killed")))
            {
                LoggerUtil.LogDebug(
                    "[CHAT PROCESS] Skipped Server death message to prevent duplication on Discord"
                );
                return;
            }

            // System messages
            if (channel == "System" && playerTracking != null)
            {
                LoggerUtil.LogDebug("[CHAT PROCESS] Forwarding system message to tracking");
                playerTracking.ProcessSystemChatMessage(message);
                return;
            }

            // Normal chat → Discord
            if (chatSync != null && config?.Chat != null)
            {
                bool enabled = config.Chat.ServerToDiscord;
                LoggerUtil.LogDebug($"[CHAT PROCESS] ServerToDiscord enabled: {enabled}");

                if (enabled)
                {
                    if (message.StartsWith("/"))
                    {
                        LoggerUtil.LogDebug("[CHAT PROCESS] Skipped command");
                        return;
                    }

                    if (channel == "Global")
                    {
                        LoggerUtil.LogDebug("[CHAT PROCESS] Global chat - sending to Discord");
                        _ = chatSync.SendGameMessageToDiscordAsync(author, message);
                    }
                    else if (channel.StartsWith("Faction:"))
                    {
                        LoggerUtil.LogDebug("[CHAT PROCESS] Faction chat - skipped for now");
                    }
                    else if (channel == "Private")
                    {
                        LoggerUtil.LogDebug("[CHAT PROCESS] Private chat - skipped for security");
                    }
                    else
                    {
                        LoggerUtil.LogDebug("[CHAT PROCESS] Unknown channel - fallback to global");
                        _ = chatSync.SendGameMessageToDiscordAsync(author, message);
                    }
                }
                else
                {
                    LoggerUtil.LogDebug("[CHAT PROCESS] ServerToDiscord disabled in config");
                }
            }
            else
            {
                LoggerUtil.LogWarning("[CHAT PROCESS] ChatSyncService or config null");
            }
        }
    }
}
