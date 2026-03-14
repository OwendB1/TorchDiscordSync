// Plugin/Services/DiscordAdminCommandService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using mamba.TorchDiscordSync.Plugin.Config;
using mamba.TorchDiscordSync.Plugin.Core;
using mamba.TorchDiscordSync.Plugin.Services;
using mamba.TorchDiscordSync.Plugin.Utils;

namespace mamba.TorchDiscordSync.Plugin.Services
{
    /// <summary>
    /// Listens to the private AdminBotChannel on Discord and executes
    /// TDS admin commands (!tds ...) posted there.
    ///
    /// Flow per message:
    ///   1. Validate: channel == AdminBotChannelId, author not bot, starts with "!tds"
    ///   2. Post "⚙️ Executing..." acknowledgement immediately
    ///   3. Run command (async where needed)
    ///   4. Post result embed (success / error / info)
    ///
    /// All commands that exist as /tds admin:* in-game are available here.
    /// </summary>
    public class DiscordAdminCommandService
    {
        private readonly DiscordBotService   _bot;
        private readonly DatabaseService     _db;
        private readonly FactionSyncService  _factionSync;
        private readonly SyncOrchestrator    _orchestrator;
        private readonly EventLoggingService _eventLog;
        private readonly MainConfig          _config;

        // Sentinel SteamID used when a command is issued from Discord (no real player)
        private const long DISCORD_ADMIN_STEAM_ID = 0L;
        private const string DISCORD_ADMIN_NAME   = "DiscordAdmin";

        public DiscordAdminCommandService(
            DiscordBotService   bot,
            DatabaseService     db,
            FactionSyncService  factionSync,
            SyncOrchestrator    orchestrator,
            EventLoggingService eventLog,
            MainConfig          config)
        {
            _bot         = bot;
            _db          = db;
            _factionSync = factionSync;
            _orchestrator = orchestrator;
            _eventLog    = eventLog;
            _config      = config;
        }

        // ================================================================
        // ENTRY POINT  – hooked to DiscordBotService.OnMessageReceivedEvent
        // ================================================================

        /// <summary>
        /// Called for every Discord message. Filters to AdminBotChannelId only.
        /// </summary>
        public async Task HandleMessageAsync(SocketMessage msg)
        {
            try
            {
                // Only process messages in the configured admin bot channel
                ulong adminChannelId = _config?.Discord?.AdminBotChannelId ?? 0;
                if (adminChannelId == 0 || msg.Channel.Id != adminChannelId)
                    return;

                // Ignore bot messages (prevents feedback loops)
                if (msg.Author.IsBot)
                    return;

                string content = msg.Content?.Trim() ?? "";

                // Accept: "!tds <cmd>" or just "!tds"
                if (!content.StartsWith("!tds", StringComparison.OrdinalIgnoreCase))
                    return;

                string authorTag = msg.Author.Username + "#" + msg.Author.Discriminator;
                if (msg.Author.Discriminator == "0")
                    authorTag = msg.Author.Username; // new Discord username format

                LoggerUtil.LogInfo(
                    $"[ADMIN_BOT] Command from {authorTag}: {content}");

                // Extract sub-command (everything after "!tds")
                string subRaw = content.Length > 4
                    ? content.Substring(4).Trim()
                    : "";

                // ── Acknowledge immediately ──────────────────────────────
                await ReplyAckAsync(msg, subRaw);

                // ── Parse and execute ─────────────────────────────────────
                await ExecuteAsync(msg, subRaw, authorTag);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[ADMIN_BOT] HandleMessageAsync error: {ex.Message}");
            }
        }

        // ================================================================
        // ACKNOWLEDGEMENT
        // ================================================================

        private async Task ReplyAckAsync(SocketMessage msg, string subRaw)
        {
            try
            {
                string display = string.IsNullOrEmpty(subRaw) ? "help" : subRaw;
                var embed = new EmbedBuilder()
                    .WithColor(Color.Orange)
                    .WithDescription($"⚙️ Executing `!tds {display}`…")
                    .WithCurrentTimestamp()
                    .Build();

                await ((IMessageChannel)msg.Channel).SendMessageAsync(embed: embed);
            }
            catch { /* non-fatal */ }
        }

        // ================================================================
        // COMMAND DISPATCH
        // ================================================================

        private async Task ExecuteAsync(SocketMessage msg, string subRaw, string authorTag)
        {
            // Tokenize
            var parts = subRaw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string sub = parts.Length > 0 ? parts[0].ToLower() : "help";

            try
            {
                switch (sub)
                {
                    // ── help ────────────────────────────────────────────
                    case "":
                    case "help":
                        await ReplyHelpAsync(msg);
                        break;

                    // ── sync:check ──────────────────────────────────────
                    case "sync:check":
                        {
                            string result = _factionSync.AdminSyncCheck();
                            await ReplySuccessAsync(msg, "sync:check", result);
                            LoggerUtil.LogInfo($"[ADMIN_BOT] {authorTag} ran sync:check");
                            break;
                        }

                    // ── sync:status ─────────────────────────────────────
                    case "sync:status":
                        {
                            string result = _factionSync.AdminSyncStatus();
                            await ReplyInfoAsync(msg, "sync:status", result);
                            LoggerUtil.LogInfo($"[ADMIN_BOT] {authorTag} ran sync:status");
                            break;
                        }

                    // ── sync:undo <TAG> ──────────────────────────────────
                    case "sync:undo":
                        {
                            if (parts.Length < 2)
                            {
                                await ReplyErrorAsync(msg, "sync:undo", "Usage: `!tds sync:undo <faction_tag>`");
                                return;
                            }
                            string tag = parts[1].ToUpper();
                            LoggerUtil.LogWarning($"[ADMIN_BOT] {authorTag} running sync:undo for {tag}");
                            string result = await _factionSync.AdminSyncUndo(tag);
                            await ReplySuccessAsync(msg, $"sync:undo {tag}", result);
                            break;
                        }

                    // ── sync:undo_all ────────────────────────────────────
                    case "sync:undo_all":
                        {
                            LoggerUtil.LogWarning($"[ADMIN_BOT] {authorTag} running sync:undo_all");
                            string result = await _factionSync.AdminSyncUndoAll();
                            await ReplySuccessAsync(msg, "sync:undo_all", result);
                            break;
                        }

                    // ── sync:cleanup ─────────────────────────────────────
                    case "sync:cleanup":
                        {
                            LoggerUtil.LogInfo($"[ADMIN_BOT] {authorTag} running sync:cleanup");
                            string result = await _factionSync.AdminSyncCleanup();
                            await ReplySuccessAsync(msg, "sync:cleanup", result);
                            break;
                        }

                    // ── sync  (full faction sync) ──────────────────────
                    case "sync":
                        {
                            LoggerUtil.LogInfo($"[ADMIN_BOT] {authorTag} running full faction sync");
                            await _orchestrator.SyncFactionsAsync();
                            await ReplySuccessAsync(msg, "sync", "Full faction synchronization complete.");
                            break;
                        }

                    // ── reset  (DESTRUCTIVE) ───────────────────────────
                    case "reset":
                        {
                            LoggerUtil.LogWarning($"[ADMIN_BOT] {authorTag} running RESET (destructive)");
                            await _factionSync.ResetDiscordAsync();
                            await ReplySuccessAsync(msg, "reset",
                                "⚠️ Discord reset complete – all roles, channels and DB entries removed.");
                            break;
                        }

                    // ── reload ─────────────────────────────────────────
                    case "reload":
                        {
                            var newCfg = MainConfig.Load();
                            if (newCfg != null)
                            {
                                await ReplySuccessAsync(msg, "reload", "Configuration reloaded successfully.");
                                LoggerUtil.LogSuccess($"[ADMIN_BOT] Config reloaded by {authorTag}");
                            }
                            else
                            {
                                await ReplyErrorAsync(msg, "reload",
                                    "Failed to reload configuration – keeping old config.");
                            }
                            break;
                        }

                    // ── verify:list ────────────────────────────────────
                    case "verify:list":
                        {
                            var verified = _db?.GetAllVerifiedPlayers();
                            if (verified == null || verified.Count == 0)
                            {
                                await ReplyInfoAsync(msg, "verify:list", "No verified users found.");
                                break;
                            }
                            var sb = new StringBuilder();
                            int i = 1;
                            foreach (var v in verified)
                                sb.AppendLine(
                                    $"{i++}. **{EscapeMd(v.DiscordUsername)}** | SteamID: `{v.SteamID}` | " +
                                    $"Player: {EscapeMd(v.GamePlayerName ?? "?")} | " +
                                    $"Since: {v.VerifiedAt:yyyy-MM-dd HH:mm}");
                            await ReplyInfoAsync(msg, $"verify:list ({verified.Count} users)", sb.ToString());
                            break;
                        }

                    // ── verify:pending ─────────────────────────────────
                    case "verify:pending":
                        {
                            var pending = _db?.GetAllPendingVerifications();
                            if (pending == null || pending.Count == 0)
                            {
                                await ReplyInfoAsync(msg, "verify:pending", "No pending verifications.");
                                break;
                            }
                            var sb = new StringBuilder();
                            int i = 1;
                            foreach (var p in pending)
                            {
                                var left = p.ExpiresAt - DateTime.UtcNow;
                                string timeLeft = left.TotalSeconds > 0
                                    ? $"{(int)left.TotalMinutes}m {left.Seconds}s left"
                                    : "expired";
                                sb.AppendLine(
                                    $"{i++}. **{EscapeMd(p.DiscordUsername)}** | SteamID: `{p.SteamID}` | " +
                                    $"Code: `{p.VerificationCode}` | {timeLeft}");
                            }
                            await ReplyInfoAsync(msg, $"verify:pending ({pending.Count})", sb.ToString());
                            break;
                        }

                    // ── verify:delete <STEAMID> ────────────────────────
                    case "verify:delete":
                        {
                            if (parts.Length < 2)
                            {
                                await ReplyErrorAsync(msg, "verify:delete",
                                    "Usage: `!tds verify:delete <SteamID>`");
                                return;
                            }
                            if (!long.TryParse(parts[1], out long delSteamId))
                            {
                                await ReplyErrorAsync(msg, "verify:delete",
                                    $"Invalid SteamID: `{parts[1]}`");
                                return;
                            }
                            var vp = _db?.GetVerifiedPlayer(delSteamId);
                            var pp = _db?.GetPendingVerification(delSteamId);
                            if (vp == null && pp == null)
                            {
                                await ReplyErrorAsync(msg, "verify:delete",
                                    $"No verification record found for SteamID `{delSteamId}`");
                                return;
                            }
                            string name = vp?.DiscordUsername ?? pp?.DiscordUsername ?? delSteamId.ToString();
                            _db?.DeletePendingVerification(delSteamId);
                            _db?.DeleteVerifiedPlayer(delSteamId);
                            await ReplySuccessAsync(msg, "verify:delete",
                                $"Verification removed for **{EscapeMd(name)}** (SteamID: `{delSteamId}`)");
                            LoggerUtil.LogInfo($"[ADMIN_BOT] {authorTag} deleted verification for {delSteamId}");
                            break;
                        }

                    // ── unverify <STEAMID> [reason] ────────────────────
                    case "unverify":
                        {
                            if (parts.Length < 2)
                            {
                                await ReplyErrorAsync(msg, "unverify",
                                    "Usage: `!tds unverify <SteamID> [reason]`");
                                return;
                            }
                            if (!long.TryParse(parts[1], out long uvSteamId))
                            {
                                await ReplyErrorAsync(msg, "unverify",
                                    $"Invalid SteamID: `{parts[1]}`");
                                return;
                            }
                            string reason = parts.Length > 2
                                ? string.Join(" ", parts.Skip(2))
                                : "Removed by Discord admin";
                            var vp = _db?.GetVerifiedPlayer(uvSteamId);
                            var pp = _db?.GetPendingVerification(uvSteamId);
                            if (vp == null && pp == null)
                            {
                                await ReplyErrorAsync(msg, "unverify",
                                    $"No verification record found for SteamID `{uvSteamId}`");
                                return;
                            }
                            string name = vp?.DiscordUsername ?? pp?.DiscordUsername ?? uvSteamId.ToString();
                            _db?.DeletePendingVerification(uvSteamId);
                            _db?.DeleteVerifiedPlayer(uvSteamId);
                            await ReplySuccessAsync(msg, "unverify",
                                $"Verification removed for **{EscapeMd(name)}** (SteamID: `{uvSteamId}`)\nReason: {EscapeMd(reason)}");
                            _ = _eventLog?.LogAsync("UnverifyCommand",
                                $"Discord admin {authorTag} | unverified {uvSteamId} | Reason: {reason}");
                            break;
                        }

                    // ── status ─────────────────────────────────────────
                    case "status":
                        {
                            var factions = _db?.GetAllFactions();
                            int totalFactions = factions?.Count ?? 0;
                            int totalPlayers  = factions?.Sum(f => f.Players?.Count ?? 0) ?? 0;
                            int synced        = factions?.Count(f => f.SyncStatus == "Synced") ?? 0;
                            int verified      = _db?.GetAllVerifiedPlayers()?.Count ?? 0;
                            int pending       = _db?.GetAllPendingVerifications()?.Count ?? 0;

                            var sb = new StringBuilder();
                            sb.AppendLine($"**Factions:** {totalFactions} ({synced} synced)");
                            sb.AppendLine($"**Players tracked:** {totalPlayers}");
                            sb.AppendLine($"**Verified players:** {verified}");
                            sb.AppendLine($"**Pending verifications:** {pending}");
                            sb.AppendLine($"**Chat sync:** {(_config?.Chat?.Enabled == true ? "✅" : "❌")}");
                            sb.AppendLine($"**Death logging:** {(_config?.Death?.Enabled == true ? "✅" : "❌")}");
                            await ReplyInfoAsync(msg, "status", sb.ToString());
                            break;
                        }

                    // ── unknown ─────────────────────────────────────────
                    default:
                        await ReplyErrorAsync(msg, sub,
                            $"Unknown command `!tds {sub}`. Type `!tds help` for available commands.");
                        break;
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[ADMIN_BOT] ExecuteAsync error for '{subRaw}': {ex.Message}");
                await ReplyErrorAsync(msg, sub, $"Internal error: {ex.Message}");
            }
        }

        // ================================================================
        // HELP EMBED
        // ================================================================

        private async Task ReplyHelpAsync(SocketMessage msg)
        {
            var eb = new EmbedBuilder()
                .WithTitle("📋 TDS Admin Commands")
                .WithColor(new Color(0x5865F2))  // Discord blurple
                .WithDescription("Available commands in this admin channel:")
                .WithFooter("TorchDiscordSync · admin bot channel")
                .WithCurrentTimestamp();

            eb.AddField("🔄 Sync Commands",
                "`!tds sync`              – Full faction synchronization\n" +
                "`!tds sync:check`        – Check status of all faction syncs\n" +
                "`!tds sync:status`       – Summary (synced/pending/failed)\n" +
                "`!tds sync:cleanup`      – Delete orphaned Discord roles/channels\n" +
                "`!tds sync:undo <TAG>`   – Undo sync for specific faction\n" +
                "`!tds sync:undo_all`     – ⚠️ Undo ALL faction syncs",
                inline: false);

            eb.AddField("🔒 Verification Commands",
                "`!tds verify:list`             – List all verified players\n" +
                "`!tds verify:pending`          – List pending verifications\n" +
                "`!tds verify:delete <SteamID>` – Delete verification record\n" +
                "`!tds unverify <SteamID> [reason]` – Remove player verification",
                inline: false);

            eb.AddField("⚙️ General Commands",
                "`!tds status`  – Plugin and server status\n" +
                "`!tds reload`  – Reload configuration from disk\n" +
                "`!tds reset`   – ⚠️ DESTRUCTIVE: delete all Discord roles/channels\n" +
                "`!tds help`    – Show this help",
                inline: false);

            eb.AddField("ℹ️ Notes",
                "• All commands are logged\n" +
                "• Bot replies with result after execution\n" +
                "• This channel is admin-only, not visible to players",
                inline: false);

            await ((IMessageChannel)msg.Channel).SendMessageAsync(embed: eb.Build());
        }

        // ================================================================
        // REPLY HELPERS
        // ================================================================

        private async Task ReplySuccessAsync(SocketMessage msg, string cmdName, string result)
        {
            string description = TruncateForEmbed(result);
            var embed = new EmbedBuilder()
                .WithTitle($"✅ {cmdName}")
                .WithColor(Color.Green)
                .WithDescription(string.IsNullOrEmpty(description) ? "Done." : description)
                .WithCurrentTimestamp()
                .Build();
            await ((IMessageChannel)msg.Channel).SendMessageAsync(embed: embed);
        }

        private async Task ReplyErrorAsync(SocketMessage msg, string cmdName, string error)
        {
            var embed = new EmbedBuilder()
                .WithTitle($"❌ {cmdName}")
                .WithColor(Color.Red)
                .WithDescription(TruncateForEmbed(error))
                .WithCurrentTimestamp()
                .Build();
            await ((IMessageChannel)msg.Channel).SendMessageAsync(embed: embed);
        }

        private async Task ReplyInfoAsync(SocketMessage msg, string cmdName, string info)
        {
            var embed = new EmbedBuilder()
                .WithTitle($"ℹ️ {cmdName}")
                .WithColor(Color.Blue)
                .WithDescription(TruncateForEmbed(info))
                .WithCurrentTimestamp()
                .Build();
            await ((IMessageChannel)msg.Channel).SendMessageAsync(embed: embed);
        }

        // ================================================================
        // UTILITIES
        // ================================================================

        /// <summary>Discord embed description max is 4096 chars.</summary>
        private static string TruncateForEmbed(string s, int max = 3900)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max) + "\n…(truncated)";
        }

        private static string EscapeMd(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`").Replace("~", "\\~");
        }
    }
}
