using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TorchDiscordSync.Plugin.Config;
using TorchDiscordSync.Plugin.Core;
using TorchDiscordSync.Plugin.Utils;
using TorchDiscordSync.Shared.Ipc;

namespace TorchDiscordSync.Plugin.Services
{
    /// <summary>
    /// Handles Discord admin commands received from the external Discord host.
    /// </summary>
    public class DiscordAdminCommandService
    {
        private readonly DiscordService _discord;
        private readonly DatabaseService _db;
        private readonly FactionSyncService _factionSync;
        private readonly SyncOrchestrator _orchestrator;
        private readonly EventLoggingService _eventLog;
        private readonly MainConfig _config;

        public DiscordAdminCommandService(
            DiscordService discord,
            DatabaseService db,
            FactionSyncService factionSync,
            SyncOrchestrator orchestrator,
            EventLoggingService eventLog,
            MainConfig config)
        {
            _discord = discord;
            _db = db;
            _factionSync = factionSync;
            _orchestrator = orchestrator;
            _eventLog = eventLog;
            _config = config;
        }

        public async Task HandleMessageAsync(DiscordIncomingMessage msg)
        {
            try
            {
                var adminChannelId = _config?.Discord?.AdminBotChannelId ?? 0;
                if (adminChannelId == 0 || msg == null || msg.ChannelId != adminChannelId)
                    return;

                if (msg.AuthorIsBot)
                    return;

                var content = msg.Content != null ? msg.Content.Trim() : string.Empty;
                if (!content.StartsWith("!tds", StringComparison.OrdinalIgnoreCase))
                    return;

                var authorTag = msg.AuthorUsername;
                if (!string.IsNullOrWhiteSpace(msg.AuthorDiscriminator)
                    && msg.AuthorDiscriminator != "0")
                {
                    authorTag += "#" + msg.AuthorDiscriminator;
                }

                LoggerUtil.LogInfo($"[ADMIN_BOT] Command from {authorTag}: {content}");

                var subRaw = content.Length > 4 ? content.Substring(4).Trim() : string.Empty;
                var normalizedSubRaw = NormalizeDiscordAdminCommand(subRaw);

                await ExecuteAsync(msg, normalizedSubRaw, authorTag).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[ADMIN_BOT] HandleMessageAsync error: {ex.Message}");
            }
        }

        private static string NormalizeDiscordAdminCommand(string subRaw)
        {
            if (string.IsNullOrWhiteSpace(subRaw))
                return string.Empty;

            if (subRaw.StartsWith("admin:", StringComparison.OrdinalIgnoreCase))
                return subRaw.Substring("admin:".Length);

            return subRaw;
        }

        private async Task ExecuteAsync(
            DiscordIncomingMessage msg,
            string subRaw,
            string authorTag)
        {
            var parts = subRaw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var sub = parts.Length > 0 ? parts[0].ToLower() : "help";

            try
            {
                switch (sub)
                {
                    case "":
                    case "help":
                        await ReplyHelpAsync(msg).ConfigureAwait(false);
                        break;

                    case "sync:check":
                        {
                            var result = _factionSync.AdminSyncCheck();
                            await ReplySuccessAsync(msg, "sync:check", result).ConfigureAwait(false);
                            LoggerUtil.LogInfo($"[ADMIN_BOT] {authorTag} ran sync:check");
                            break;
                        }

                    case "sync:status":
                        {
                            var result = _factionSync.AdminSyncStatus();
                            await ReplyInfoAsync(msg, "sync:status", result).ConfigureAwait(false);
                            LoggerUtil.LogInfo($"[ADMIN_BOT] {authorTag} ran sync:status");
                            break;
                        }

                    case "sync:undo":
                        {
                            if (parts.Length < 2)
                            {
                                await ReplyErrorAsync(
                                    msg,
                                    "sync:undo",
                                    "Usage: `/tds sync undo faction-tag:<TAG>`").ConfigureAwait(false);
                                return;
                            }

                            var tag = parts[1].ToUpper();
                            LoggerUtil.LogWarning(
                                $"[ADMIN_BOT] {authorTag} running sync:undo for {tag}");
                            var result = await _factionSync.AdminSyncUndo(tag).ConfigureAwait(false);
                            await ReplySuccessAsync(msg, "sync:undo " + tag, result).ConfigureAwait(false);
                            break;
                        }

                    case "sync:undo_all":
                        {
                            LoggerUtil.LogWarning(
                                $"[ADMIN_BOT] {authorTag} running sync:undo_all");
                            var result = await _factionSync.AdminSyncUndoAll().ConfigureAwait(false);
                            await ReplySuccessAsync(msg, "sync:undo_all", result).ConfigureAwait(false);
                            break;
                        }

                    case "sync:cleanup":
                        {
                            LoggerUtil.LogInfo(
                                $"[ADMIN_BOT] {authorTag} running sync:cleanup");
                            var result = await _factionSync.AdminSyncCleanup().ConfigureAwait(false);
                            await ReplySuccessAsync(msg, "sync:cleanup", result).ConfigureAwait(false);
                            break;
                        }

                    case "sync":
                        {
                            LoggerUtil.LogInfo(
                                $"[ADMIN_BOT] {authorTag} running full faction sync");
                            await _orchestrator.SyncFactionsAsync().ConfigureAwait(false);
                            await ReplySuccessAsync(
                                msg,
                                "sync",
                                "Full faction synchronization complete.").ConfigureAwait(false);
                            break;
                        }

                    case "reset":
                        {
                            LoggerUtil.LogWarning(
                                $"[ADMIN_BOT] {authorTag} running RESET (destructive)");
                            await _factionSync.ResetDiscordAsync().ConfigureAwait(false);
                            await ReplySuccessAsync(
                                msg,
                                "reset",
                                "Discord reset complete. All roles, channels and DB entries removed.").ConfigureAwait(false);
                            break;
                        }

                    case "reload":
                        {
                            var newCfg = MainConfig.Load();
                            if (newCfg != null)
                            {
                                await ReplySuccessAsync(
                                    msg,
                                    "reload",
                                    "Configuration reloaded successfully.").ConfigureAwait(false);
                                LoggerUtil.LogSuccess(
                                    $"[ADMIN_BOT] Config reloaded by {authorTag}");
                            }
                            else
                            {
                                await ReplyErrorAsync(
                                    msg,
                                    "reload",
                                    "Failed to reload configuration. Keeping old config.").ConfigureAwait(false);
                            }

                            break;
                        }

                    case "status":
                        {
                            var factions = _db?.GetAllFactions();
                            var totalFactions = factions?.Count ?? 0;
                            var totalPlayers = factions?.Sum(f => f.Players?.Count ?? 0) ?? 0;
                            var synced = factions?.Count(f => f.SyncStatus == "Synced") ?? 0;

                            var sb = new StringBuilder();
                            sb.AppendLine($"**Factions:** {totalFactions} ({synced} synced)");
                            sb.AppendLine($"**Players tracked:** {totalPlayers}");
                            sb.AppendLine($"**Chat sync:** {(_config?.Chat?.Enabled == true ? "YES" : "NO")}");
                            sb.AppendLine($"**Death logging:** {(_config?.Death?.Enabled == true ? "YES" : "NO")}");
                            await ReplyInfoAsync(msg, "status", sb.ToString()).ConfigureAwait(false);
                            break;
                        }

                    default:
                        await ReplyErrorAsync(
                            msg,
                            sub,
                            $"Unknown command `/tds {sub}`. Type `/tds help` for available commands.").ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(
                    $"[ADMIN_BOT] ExecuteAsync error for '{subRaw}': {ex.Message}");
                await ReplyErrorAsync(msg, sub, $"Internal error: {ex.Message}").ConfigureAwait(false);
            }
        }

        private async Task ReplyHelpAsync(DiscordIncomingMessage msg)
        {
            var embed = new DiscordEmbedModel
            {
                Title = "TDS Admin Commands",
                ColorRgb = 0x5865F2,
                Description = "Available commands in this admin channel:",
                Footer = "TorchDiscordSync admin bot channel",
                IncludeTimestamp = true,
            };

            embed.Fields.Add(
                new DiscordEmbedFieldModel
                {
                    Name = "Sync Commands",
                    Value =
                        "`/tds sync run`\n"
                        + "`/tds sync check`\n"
                        + "`/tds sync status`\n"
                        + "`/tds cleanup`\n"
                        + "`/tds sync undo faction-tag:<TAG>`\n"
                        + "`/tds sync undoall`",
                    Inline = false,
                });
            embed.Fields.Add(
                new DiscordEmbedFieldModel
                {
                    Name = "General Commands",
                    Value =
                        "`/tds status`\n"
                        + "`/tds reload`\n"
                        + "`/tds reset`\n"
                        + "`/tds help`",
                    Inline = false,
                });

            await SendEmbedAsync(msg.ChannelId, embed).ConfigureAwait(false);
        }

        private async Task ReplySuccessAsync(
            DiscordIncomingMessage msg,
            string cmdName,
            string result)
        {
            await SendEmbedAsync(
                msg.ChannelId,
                new DiscordEmbedModel
                {
                    Title = "SUCCESS " + cmdName,
                    ColorRgb = 0x57F287,
                    Description = TruncateForEmbed(result),
                    IncludeTimestamp = true,
                }).ConfigureAwait(false);
        }

        private async Task ReplyErrorAsync(
            DiscordIncomingMessage msg,
            string cmdName,
            string error)
        {
            await SendEmbedAsync(
                msg.ChannelId,
                new DiscordEmbedModel
                {
                    Title = "ERROR " + cmdName,
                    ColorRgb = 0xED4245,
                    Description = TruncateForEmbed(error),
                    IncludeTimestamp = true,
                }).ConfigureAwait(false);
        }

        private async Task ReplyInfoAsync(
            DiscordIncomingMessage msg,
            string cmdName,
            string info)
        {
            await SendEmbedAsync(
                msg.ChannelId,
                new DiscordEmbedModel
                {
                    Title = "INFO " + cmdName,
                    ColorRgb = 0x5865F2,
                    Description = TruncateForEmbed(info),
                    IncludeTimestamp = true,
                }).ConfigureAwait(false);
        }

        private async Task SendEmbedAsync(ulong channelId, DiscordEmbedModel embed)
        {
            var sent = await _discord.SendEmbedAsync(channelId, embed).ConfigureAwait(false);
            if (!sent)
                LoggerUtil.LogWarning("[ADMIN_BOT] Failed to send embed reply to Discord");
        }

        private static string TruncateForEmbed(string s, int max = 3900)
        {
            if (string.IsNullOrEmpty(s))
                return s;

            return s.Length <= max ? s : s.Substring(0, max) + "\n...(truncated)";
        }

        private static string EscapeMd(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;

            return s.Replace("*", "\\*")
                .Replace("_", "\\_")
                .Replace("`", "\\`")
                .Replace("~", "\\~");
        }
    }
}
