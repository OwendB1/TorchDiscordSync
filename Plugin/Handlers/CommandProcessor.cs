using System;
using System.Collections.Generic;
using System.Linq;
using TorchDiscordSync.Plugin.Config;
using TorchDiscordSync.Plugin.Core;
using TorchDiscordSync.Plugin.Models;
using TorchDiscordSync.Plugin.Services;
using TorchDiscordSync.Plugin.Utils;
using Torch.API.Managers;

namespace TorchDiscordSync.Plugin.Handlers
{
    /// <summary>
    /// Handles chat relay between the game and Discord and keeps a thin
    /// compatibility bridge for legacy /tds chat commands.
    /// </summary>
    public class CommandProcessor
    {
        private readonly MainConfig _config;
        private readonly ChatSyncService _chatSync;
        private readonly DatabaseService _db;
        private readonly PlayerTrackingService _playerTracking;
        private readonly TdsCommandService _tdsCommands;
        private readonly SyncOrchestrator _orchestrator;

        public CommandProcessor(
            MainConfig config,
            DatabaseService db,
            ChatSyncService chatSync,
            PlayerTrackingService playerTracking,
            TdsCommandService tdsCommands,
            SyncOrchestrator orchestrator = null)
        {
            _config = config;
            _db = db;
            _chatSync = chatSync;
            _playerTracking = playerTracking;
            _tdsCommands = tdsCommands;
            _orchestrator = orchestrator;
        }

        public void HandleChatMessage(TorchChatMessage msg, ref bool consumed)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(msg.Message))
                    return;

                string channelName = msg.Channel.ToString() ?? "Unknown";
                LoggerUtil.LogDebug(
                    $"[CHAT] Channel=\"{channelName}\" Author=\"{msg.Author}\" SteamId={msg.AuthorSteamId} Message=\"{msg.Message}\"");

                ProcessImmediateChatSignals(msg, channelName);

                if (msg.Message.StartsWith("/tds", StringComparison.OrdinalIgnoreCase))
                {
                    if (msg.AuthorSteamId.HasValue)
                    {
                        HandleLegacyChatCommand(msg.Message, (long)msg.AuthorSteamId.Value, msg.Author);
                        consumed = true;
                    }
                    else
                    {
                        LoggerUtil.LogWarning("[CHAT] /tds command ignored because SteamID is missing");
                    }

                    return;
                }

                if (IsPluginRelayAuthor(msg.Author)
                    || string.Equals(msg.Author, "Server", StringComparison.OrdinalIgnoreCase))
                    return;

                if (channelName.StartsWith("Private", StringComparison.OrdinalIgnoreCase))
                    return;

                if (channelName.StartsWith("Faction", StringComparison.OrdinalIgnoreCase))
                {
                    HandleFactionChatMessage(msg, channelName);
                    return;
                }

                if (ChatUtils.IsPrivateMessage(msg.Message))
                    return;

                if (msg.Message.StartsWith("[Discord] ", StringComparison.OrdinalIgnoreCase))
                    return;

                if (channelName.StartsWith("Global", StringComparison.OrdinalIgnoreCase))
                {
                    ChatUtils.ProcessChatMessage(
                        msg.Message,
                        msg.Author,
                        "Global",
                        _chatSync,
                        _playerTracking,
                        _config);
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[CHAT] Error processing message: " + ex.Message);
            }
        }

        private void ProcessImmediateChatSignals(TorchChatMessage msg, string channelName)
        {
            if (string.IsNullOrWhiteSpace(msg.Message))
                return;

            if (!IsSystemChatMessage(msg, channelName))
                return;

            _playerTracking?.ProcessSystemChatMessage(msg.Message);

            if (ShouldTriggerFactionSyncFromChat(msg.Message))
            {
                string reason = "system chat: " + TruncateForLog(msg.Message, 120);
                LoggerUtil.LogInfo("[SYNC] Immediate faction sync requested from chat event");
                _ = _orchestrator.RequestFactionSyncFromChatAsync(reason);
            }
        }

        private bool IsSystemChatMessage(TorchChatMessage msg, string channelName)
        {
            if (IsPluginRelayAuthor(msg.Author))
                return false;

            return string.Equals(msg.Author, "Server", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(msg.Author, "System", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(msg.Author, "Console", StringComparison.OrdinalIgnoreCase)
                   || channelName.StartsWith("System", StringComparison.OrdinalIgnoreCase);
        }

        private bool ShouldTriggerFactionSyncFromChat(string message)
        {
            if (_orchestrator == null || _config?.Faction == null || !_config.Faction.Enabled)
                return false;

            if (string.IsNullOrWhiteSpace(message))
                return false;

            if (message.IndexOf("faction", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            return ContainsAny(
                message,
                "created",
                "joined",
                "left",
                "accepted",
                "kicked",
                "promoted",
                "demoted",
                "disband",
                "member",
                "request",
                "peace",
                "war");
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            foreach (var needle in needles)
            {
                if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static bool IsPluginRelayAuthor(string author)
        {
            return string.Equals(author, "TDS", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(author, "Discord", StringComparison.OrdinalIgnoreCase);
        }

        private static string TruncateForLog(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;

            return text.Substring(0, maxLength - 3) + "...";
        }

        public void HandleLegacyChatCommand(string command, long playerSteamId, string playerName)
        {
            try
            {
                var request = new TdsCommandRequest
                {
                    IsAdmin = CommandAuthorizationUtil.IsUserAdmin(playerSteamId, _config.AdminSteamIDs),
                    PlayerName = playerName,
                    SteamId = playerSteamId,
                    Respond = message => ChatUtils.SendInfo(message, playerSteamId),
                };

                var tokens = NormalizeLegacyTokens(command);
                if (tokens.Count == 0)
                {
                    _tdsCommands.ShowHelp(request);
                    return;
                }

                if (TryHandleLegacyAdminCommand(tokens, request))
                    return;

                switch (tokens[0])
                {
                    case "help":
                        _tdsCommands.ShowHelp(request);
                        return;
                    case "status":
                        _tdsCommands.ShowStatus(request);
                        return;
                    case "verify":
                        HandleLegacyVerifyCommand(tokens, request);
                        return;
                    case "sync":
                        _tdsCommands.RunFullSync(request);
                        return;
                    case "reset":
                        _tdsCommands.RunReset(request);
                        return;
                    case "cleanup":
                        _tdsCommands.RunCleanup(request);
                        return;
                    case "reload":
                        _tdsCommands.RunReload(request);
                        return;
                    case "unverify":
                        _tdsCommands.RunUnverify(
                            request,
                            tokens.Count > 1 ? tokens[1] : null,
                            tokens.Count > 2 ? string.Join(" ", tokens.Skip(2)) : "Admin removal");
                        return;
                    default:
                        ChatUtils.SendError("Unknown command. Use !tds help or /tds help.", playerSteamId);
                        return;
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[CHAT] Legacy /tds bridge error: " + ex.Message);
                ChatUtils.SendError("Command error: " + ex.Message, playerSteamId);
            }
        }

        private void HandleLegacyFactionChatMessage(FactionModel faction, TorchChatMessage msg)
        {
            _ = _chatSync.SendGameFactionMessageToDiscordAsync(faction, msg.Author, msg.Message);
            LoggerUtil.LogInfo($"[CHAT] Forwarded faction message for {faction.Tag}");
        }

        private void HandleFactionChatMessage(TorchChatMessage msg, string channelName)
        {
            if (_db == null || _chatSync == null)
                return;

            long gameChatId = 0;
            int colonIndex = channelName.IndexOf(':');
            if (colonIndex >= 0 && colonIndex < channelName.Length - 1)
                long.TryParse(channelName.Substring(colonIndex + 1), out gameChatId);

            FactionModel faction = gameChatId != 0 ? _db.GetFactionByGameChatId(gameChatId) : null;
            if (faction == null && msg.AuthorSteamId.HasValue)
            {
                long authorSteamId = (long)msg.AuthorSteamId.Value;
                var player = _db.GetPlayerBySteamID(authorSteamId);
                if (player != null)
                    faction = _db.GetFaction(player.FactionID);

                if (faction == null)
                {
                    faction = _db
                        .GetAllFactions()
                        ?.FirstOrDefault(f => f.Players != null && f.Players.Any(p => p.SteamID == authorSteamId));
                }

                if (faction != null && faction.DiscordChannelID != 0 && gameChatId != 0)
                {
                    faction.GameFactionChatId = gameChatId;
                    _db.SaveFaction(faction);
                }
            }

            if (faction != null && faction.DiscordChannelID != 0)
                HandleLegacyFactionChatMessage(faction, msg);
        }

        private void HandleLegacyVerifyCommand(IReadOnlyList<string> tokens, TdsCommandRequest request)
        {
            if (tokens.Count == 1)
            {
                _tdsCommands.StartVerification(request, null);
                return;
            }

            switch (tokens[1])
            {
                case "status":
                    _tdsCommands.ShowVerificationStatus(request);
                    return;
                case "delete":
                    _tdsCommands.DeletePendingVerification(request);
                    return;
                case "help":
                    _tdsCommands.ShowVerificationHelp(request);
                    return;
                default:
                    _tdsCommands.StartVerification(request, tokens[1]);
                    return;
            }
        }

        private bool TryHandleLegacyAdminCommand(IReadOnlyList<string> tokens, TdsCommandRequest request)
        {
            if (tokens.Count == 0 || tokens[0] != "admin")
                return false;

            if (tokens.Count == 1)
            {
                _tdsCommands.ShowHelp(request);
                return true;
            }

            if (tokens[1] == "sync")
            {
                if (tokens.Count == 2)
                {
                    _tdsCommands.RunFullSync(request);
                    return true;
                }

                switch (tokens[2])
                {
                    case "check":
                        _tdsCommands.RunAdminSyncCheck(request);
                        return true;
                    case "status":
                        _tdsCommands.RunAdminSyncStatus(request);
                        return true;
                    case "undo":
                        _tdsCommands.RunAdminSyncUndo(request, tokens.Count > 3 ? tokens[3] : null);
                        return true;
                    case "undoall":
                        _tdsCommands.RunAdminSyncUndoAll(request);
                        return true;
                    case "cleanup":
                        _tdsCommands.RunAdminSyncCleanup(request);
                        return true;
                }
            }

            if (tokens[1] == "verify")
            {
                if (tokens.Count < 3)
                {
                    _tdsCommands.ShowHelp(request);
                    return true;
                }

                switch (tokens[2])
                {
                    case "list":
                        _tdsCommands.ListVerifiedUsers(request);
                        return true;
                    case "pending":
                        _tdsCommands.ListPendingVerifications(request);
                        return true;
                    case "delete":
                        _tdsCommands.DeleteVerificationRecord(request, tokens.Count > 3 ? tokens[3] : null);
                        return true;
                }
            }

            switch (tokens[1])
            {
                case "reset":
                    _tdsCommands.RunReset(request);
                    return true;
                case "cleanup":
                    _tdsCommands.RunCleanup(request);
                    return true;
                case "reload":
                    _tdsCommands.RunReload(request);
                    return true;
                case "unverify":
                    _tdsCommands.RunUnverify(
                        request,
                        tokens.Count > 2 ? tokens[2] : null,
                        tokens.Count > 3 ? string.Join(" ", tokens.Skip(3)) : "Admin removal");
                    return true;
            }

            return false;
        }

        private static List<string> NormalizeLegacyTokens(string command)
        {
            string raw = command.StartsWith("/tds", StringComparison.OrdinalIgnoreCase)
                ? command.Substring(4).Trim()
                : command.Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();

            var tokens = new List<string>();
            foreach (string part in raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var split = part.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string item in split)
                {
                    string normalized = item.Trim().ToLowerInvariant();
                    if (normalized == "undo_all")
                        normalized = "undoall";

                    if (!string.IsNullOrWhiteSpace(normalized))
                        tokens.Add(normalized);
                }
            }

            return tokens;
        }
    }
}
