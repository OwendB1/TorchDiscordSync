// Plugin/Utils/ChatUtils.cs
using System;
using System.Collections.Generic;
using Sandbox.Game;
using Sandbox.ModAPI;
using TorchDiscordSync.Plugin.Config;
using TorchDiscordSync.Plugin.Services;
using VRage.Game.ModAPI;

namespace TorchDiscordSync.Plugin.Utils
{
    /// <summary>
    /// Shared helpers for server chat output and chat relay filtering.
    /// </summary>
    public static class ChatUtils
    {
        private const string PRIVATE_PREFIX = "[PRIVATE_CMD]";
        private const string COMMAND_AUTHOR = "TDS";
        private const string SERVER_AUTHOR = "Server";
        private const string DEFAULT_COLOR = "White";

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

        public static void BroadcastToServer(string message)
        {
            SendChatMessage(message, SERVER_AUTHOR, DEFAULT_COLOR, 0, false);
        }

        public static void SendWarning(string message, long steamId = 0)
        {
            SendChatMessage($"[!] {message}", COMMAND_AUTHOR, "Yellow", steamId, true);
        }

        public static void SendError(string message, long steamId = 0)
        {
            SendChatMessage($"[FAIL] {message}", COMMAND_AUTHOR, "Red", steamId, true);
        }

        public static void SendSuccess(string message, long steamId = 0)
        {
            SendChatMessage($"[OK] {message}", COMMAND_AUTHOR, "Green", steamId, true);
        }

        public static void SendInfo(string message, long steamId = 0)
        {
            SendChatMessage($"[I] {message}", COMMAND_AUTHOR, "Blue", steamId, true);
        }

        public static void SendHelpText(string helpText, long steamId = 0)
        {
            SendChatMessage(helpText, COMMAND_AUTHOR, "Green", steamId, true);
        }

        private static void SendChatMessage(
            string message,
            string author,
            string color,
            long steamId,
            bool markPrivate)
        {
            try
            {
                var entityId = ResolveEntityId(steamId);
                var tag = entityId != 0 ? "[W]" : "[G]";
                var payload = markPrivate ? $"{PRIVATE_PREFIX} {message}" : message;
                LoggerUtil.LogDebug($"{tag} {author} [{color}] {message}");
                MyVisualScriptLogicProvider.SendChatMessage(payload, author, entityId, color);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"Chat send failed: {ex.Message}");
                SendServerMessage(message);
            }
        }

        private static long ResolveEntityId(long steamId)
        {
            if (steamId <= 0)
                return 0;

            try
            {
                var players = new List<IMyPlayer>();
                MyAPIGateway.Players.GetPlayers(players);

                foreach (var p in players)
                {
                    if ((long)p.SteamUserId != steamId)
                        continue;

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

        public static bool IsPrivateMessage(string message)
        {
            return !string.IsNullOrEmpty(message)
                && message.StartsWith(PRIVATE_PREFIX, StringComparison.Ordinal);
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
                var enabled = config.Chat.ServerToDiscord;
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
