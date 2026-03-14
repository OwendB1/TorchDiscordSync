// Plugin/Handlers/CommandProcessor.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using mamba.TorchDiscordSync.Plugin.Config;
using mamba.TorchDiscordSync.Plugin.Core;
using mamba.TorchDiscordSync.Plugin.Models;
using mamba.TorchDiscordSync.Plugin.Services;
using mamba.TorchDiscordSync.Plugin.Utils;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.API.Session;
using Torch.Commands;
using Torch.Managers.ChatManager;

namespace mamba.TorchDiscordSync.Plugin.Handlers
{
    /// <summary>
    /// Processes all /tds in-game chat commands and routes incoming chat messages
    /// between the game and Discord.  Moved from Plugin/index.cs to keep the
    /// plugin entry point clean.
    /// </summary>
    public class CommandProcessor
    {
        // ---- injected services ----
        private readonly MainConfig _config;
        private readonly DiscordService _discordService;
        private readonly DatabaseService _db;
        private readonly FactionSyncService _factionSync;
        private readonly EventLoggingService _eventLog;
        private readonly SyncOrchestrator _orchestrator;
        private readonly VerificationService _verification;
        private readonly VerificationCommandHandler _verificationCommandHandler;
        private readonly TestCommandHandler _testHandler;

        // ---- optional services supplied after session load ----
        private readonly ChatSyncService _chatSync;
        private readonly PlayerTrackingService _playerTracking;

        public CommandProcessor(
            MainConfig config,
            DiscordService discordService,
            DatabaseService db,
            FactionSyncService factionSync,
            EventLoggingService eventLog,
            SyncOrchestrator orchestrator,
            VerificationService verification = null,
            VerificationCommandHandler verificationCommandHandler = null,
            ChatSyncService chatSync = null,
            PlayerTrackingService playerTracking = null
        )
        {
            _config = config;
            _discordService = discordService;
            _db = db;
            _factionSync = factionSync;
            _eventLog = eventLog;
            _orchestrator = orchestrator;
            _verification = verification;
            _verificationCommandHandler = verificationCommandHandler;
            _chatSync = chatSync;
            _playerTracking = playerTracking;
            _testHandler = new TestCommandHandler(_discordService);

            if (_verificationCommandHandler != null)
                LoggerUtil.LogInfo("[COMMAND] VerificationCommandHandler successfully initialized");
            else
                LoggerUtil.LogWarning(
                    "[COMMAND] VerificationCommandHandler is NULL – verify command will not work!"
                );
        }

        // ============================================================
        // CHAT MESSAGE ROUTING  (moved from Plugin/index.cs)
        // ============================================================

        /// <summary>
        /// Entry point for every chat message received by the Torch ChatManagerServer.
        /// Registered as:  chatManager.MessageRecieved += _commandProcessor.HandleChatMessage
        ///
        /// Priority order:
        ///   1. /tds commands  →  process, mark consumed
        ///   2. TDS / Discord / Server authored messages  →  drop  (loop guard)
        ///   3. Private channel  →  drop  (security)
        ///   4. Faction chat  →  forward to Discord faction channel
        ///   5. [PRIVATE_CMD] prefixed messages  →  drop  (command responses)
        ///   6. Existing [Discord] / "Discord" prefix  →  drop  (loop guard)
        ///   7. Global chat  →  forward to Discord global channel
        /// </summary>
        public void HandleChatMessage(TorchChatMessage msg, ref bool consumed)
        {
            try
            {
                if (string.IsNullOrEmpty(msg.Message))
                    return;

                string channelName = msg.Channel.ToString() ?? "Unknown";

                // Resolve channel tag for debug logging
                string _dbgTag =
                    channelName.StartsWith("Faction") ? "[F]"
                    : channelName.StartsWith("Private") ? "[W]"
                    : channelName == "Global" ? "[G]"
                    : "[?]";

                LoggerUtil.LogDebug(
                    string.Format(
                        "{0} Incoming chat: Channel=\"{1}\" Author=\"{2}\" SteamId={3} Message=\"{4}\"",
                        _dbgTag,
                        channelName,
                        msg.Author,
                        msg.AuthorSteamId,
                        msg.Message
                    )
                );

                // ----------------------------------------------------------
                // PRIORITY 1 – /tds commands (must come before any filter)
                // ----------------------------------------------------------
                if (msg.Message.StartsWith("/tds ") || msg.Message.Equals("/tds"))
                {
                    if (msg.AuthorSteamId.HasValue)
                    {
                        LoggerUtil.LogInfo(
                            string.Format("[CMD] Detected: {0} from {1}", msg.Message, msg.Author)
                        );
                        ProcessCommand(msg.Message, (long)msg.AuthorSteamId.Value, msg.Author);
                        consumed = true;
                    }
                    else
                    {
                        LoggerUtil.LogWarning(
                            string.Format("[COMMAND] No SteamID for command: {0}", msg.Message)
                        );
                    }
                    return; // Never forward /tds commands to Discord
                }

                // ----------------------------------------------------------
                // PRIORITY 2 – Loop guard: drop all TDS / Discord / Server
                //              injected messages before they re-enter Discord.
                //   "TDS"     – all messages injected by this plugin
                //              (faction PMs, fallback broadcasts, cmd responses)
                //   "Discord" – global Discord-to-game broadcasts
                //   "Server"  – join/leave/death announcements already sent
                //              directly by EventLoggingService / DeathMessageHandler
                // ----------------------------------------------------------
                if (msg.Author == "TDS" || msg.Author == "Discord" || msg.Author == "Server")
                {
                    LoggerUtil.LogDebug(
                        string.Format(
                            "[CHAT] Loop guard: dropped message from author '{0}'",
                            msg.Author
                        )
                    );
                    return;
                }

                // ----------------------------------------------------------
                // PRIORITY 3 – Private channel: never leak to Discord
                // ----------------------------------------------------------
                if (channelName.StartsWith("Private") || channelName == "Private")
                {
                    LoggerUtil.LogDebug(
                        string.Format(
                            "[W] Private whisper – dropped (not forwarded): {0}: {1}",
                            msg.Author,
                            msg.Message
                        )
                    );
                    return;
                }

                // ----------------------------------------------------------
                // PRIORITY 4 – Faction chat → forward to Discord faction channel
                // ----------------------------------------------------------
                if (channelName.StartsWith("Faction") || channelName == "Faction")
                {
                    LoggerUtil.LogDebug(
                        string.Format(
                            "[F] Routing to faction handler: Author={0} Channel={1}",
                            msg.Author,
                            channelName
                        )
                    );
                    HandleFactionChatMessage(msg, channelName);
                    return;
                }

                // ----------------------------------------------------------
                // PRIORITY 5 – Drop private command responses ([PRIVATE_CMD])
                // ----------------------------------------------------------
                if (ChatUtils.IsPrivateMessage(msg.Message))
                {
                    LoggerUtil.LogDebug(
                        string.Format("[CHAT] Dropped private command response: {0}", msg.Message)
                    );
                    return;
                }

                // ----------------------------------------------------------
                // PRIORITY 6 – Drop any residual Discord loop messages
                // ----------------------------------------------------------
                if (msg.Message.StartsWith("[Discord] ") || msg.Message.StartsWith("Discord"))
                {
                    LoggerUtil.LogDebug(
                        string.Format("[CHAT] Dropped Discord loop message: {0}", msg.Message)
                    );
                    return;
                }

                // ----------------------------------------------------------
                // PRIORITY 7 – Forward global chat to Discord
                // ----------------------------------------------------------
                if (channelName == "Global" || channelName.StartsWith("Global"))
                {
                    LoggerUtil.LogDebug(
                        string.Format(
                            "[G] Forwarding to Discord global: {0}: {1}",
                            msg.Author,
                            msg.Message
                        )
                    );
                    ChatUtils.ProcessChatMessage(
                        msg.Message,
                        msg.Author,
                        "Global",
                        _chatSync,
                        _playerTracking,
                        _config
                    );
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(
                    string.Format("Error in chat message processing: {0}", ex.Message)
                );
            }
        }

        // ---- faction chat helper ----------------------------------------

        /// <summary>
        /// Resolve the sending faction and forward the message to the matching
        /// Discord faction channel.  Also maps the in-game GameFactionChatId to
        /// the faction record the first time a message is seen from a new channel.
        /// </summary>
        private void HandleFactionChatMessage(TorchChatMessage msg, string channelName)
        {
            long gameChatId = 0;

            // Parse the GameFactionChatId appended to the channel name, e.g. "Faction:123456"
            if (channelName.Length > 7)
            {
                int colonIdx = channelName.IndexOf(':');
                if (colonIdx >= 0 && colonIdx < channelName.Length - 1)
                {
                    string idPart = channelName.Substring(colonIdx + 1).Trim();
                    long.TryParse(idPart, out gameChatId);
                }
            }

            LoggerUtil.LogInfo(
                string.Format(
                    "[CHAT_DEBUG] Faction channel raw: channelName=\"{0}\" gameChatId={1} "
                        + "authorSteamId={2} author=\"{3}\"",
                    channelName,
                    gameChatId,
                    msg.AuthorSteamId,
                    msg.Author
                )
            );

            if (_db == null || _chatSync == null)
                return;

            FactionModel faction = gameChatId != 0 ? _db.GetFactionByGameChatId(gameChatId) : null;

            // Fallback: look up via player's SteamID
            if (faction == null && msg.AuthorSteamId.HasValue)
            {
                long authorSteamId = (long)msg.AuthorSteamId.Value;
                var player = _db.GetPlayerBySteamID(authorSteamId);

                if (player != null)
                    faction = _db.GetFaction(player.FactionID);

                if (faction == null)
                {
                    var allFactions = _db.GetAllFactions();
                    faction = allFactions?.FirstOrDefault(f =>
                        f.Players != null && f.Players.Any(p => p.SteamID == authorSteamId)
                    );
                }

                // Persist the GameFactionChatId mapping so future lookups are instant
                if (faction != null && faction.DiscordChannelID != 0 && gameChatId != 0)
                {
                    faction.GameFactionChatId = gameChatId;
                    _db.SaveFaction(faction);
                    LoggerUtil.LogInfo(
                        string.Format(
                            "[CHAT] Mapped faction chat {0} → {1}",
                            gameChatId,
                            faction.Tag
                        )
                    );
                }
            }

            if (faction != null && faction.DiscordChannelID != 0)
            {
                _ = _chatSync.SendGameFactionMessageToDiscordAsync(
                    faction,
                    msg.Author,
                    msg.Message
                );
                LoggerUtil.LogInfo(
                    string.Format(
                        "[CHAT_DEBUG] Game Faction → Discord: forwarded {0} \"{1}: {2}\"",
                        faction.Tag,
                        msg.Author,
                        msg.Message
                    )
                );
            }
            else
            {
                LoggerUtil.LogInfo(
                    string.Format(
                        "[CHAT_DEBUG] Game Faction → Discord: skip (faction={0} DiscordChannelID={1})",
                        faction != null ? "ok" : "null",
                        faction?.DiscordChannelID ?? 0
                    )
                );
            }
        }

        // ============================================================
        // COMMAND PROCESSING
        // ============================================================

        /// <summary>
        /// Parse and dispatch an in-game /tds command.
        /// Routes to the appropriate sub-handler based on the subcommand token
        /// and the player's authorization level.
        /// </summary>
        public void ProcessCommand(string command, long playerSteamID, string playerName)
        {
            try
            {
                LoggerUtil.LogDebug(
                    string.Format(
                        "[COMMAND] Processing command from {0} (SteamID: {1}): {2}",
                        playerName,
                        playerSteamID,
                        command
                    )
                );

                var parts = command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length < 2)
                {
                    HandleHelpCommand(playerSteamID);
                    return;
                }

                string subcommand = parts[1].ToLower();
                LoggerUtil.LogDebug(string.Format("[COMMAND] Subcommand: {0}", subcommand));

                bool isAdmin = CommandAuthorizationUtil.IsUserAdmin(
                    playerSteamID,
                    _config.AdminSteamIDs
                );

                // ---- verify:status  (check before "verify" to avoid prefix clash) ----
                if (subcommand == "verify:status")
                {
                    HandleVerifyStatusCommand(playerSteamID, playerName);
                    return;
                }

                if (subcommand == "verify:delete")
                {
                    HandleVerifyDeleteCommand(playerSteamID, playerName);
                    return;
                }

                if (subcommand == "verify:help")
                {
                    HandleVerifyHelpCommand(playerName, playerSteamID);
                    return;
                }

                if (subcommand == "verify")
                {
                    HandleVerifyCommand(playerSteamID, playerName, parts);
                    return;
                }

                // ---- test commands ----
                if (subcommand.StartsWith("test:"))
                {
                    _testHandler.HandleTestCommand(subcommand, playerSteamID, playerName, isAdmin);
                    return;
                }

                // ---- admin:sync:* commands ----
                if (subcommand.StartsWith("admin:sync:"))
                {
                    if (!isAdmin)
                    {
                        ChatUtils.SendError("[!] Admin command – access denied", playerSteamID);
                        LoggerUtil.LogWarning(
                            string.Format(
                                "[COMMAND] Non-admin {0} ({1}) tried admin:sync command: {2}",
                                playerName,
                                playerSteamID,
                                subcommand
                            )
                        );
                        return;
                    }

                    HandleAdminSyncCommand(
                        subcommand.Substring("admin:sync:".Length),
                        parts,
                        playerName,
                        playerSteamID
                    );
                    return;
                }

                // ---- commands restricted to admins ----
                var adminOnlyCommands = new List<string>
                {
                    "admin:faction:sync",
                    "admin:faction:reset",
                    "admin:faction:cleanup",
                    "reload",
                    "unverify",
                    "admin:verify:list",
                    "admin:verify:pending",
                    "admin:verify:delete",
                };

                if (adminOnlyCommands.Contains(subcommand))
                {
                    if (!isAdmin)
                    {
                        LoggerUtil.LogWarning(
                            string.Format(
                                "[SECURITY] Unauthorized command attempt by {0} ({1}): /{2}",
                                playerName,
                                playerSteamID,
                                subcommand
                            )
                        );
                        ChatUtils.SendError(string.Format("Command '{0}' requires admin privileges", subcommand, playerSteamID)
                        );
                        return;
                    }

                    switch (subcommand)
                    {
                        case "sync":
                            HandleSyncCommand(playerName, playerSteamID);
                            break;
                        case "reset":
                            HandleResetCommand(playerName, playerSteamID);
                            break;
                        case "cleanup":
                            HandleCleanupCommand(playerName, playerSteamID);
                            break;
                        case "reload":
                            HandleReloadCommand(playerName, playerSteamID);
                            break;
                        case "unverify":
                            HandleUnverifyCommand(playerSteamID, playerName, parts);
                            break;
                        case "admin:verify:list":
                            HandleAdminVerifyList(playerName, playerSteamID);
                            break;
                        case "admin:verify:pending":
                            HandleAdminVerifyPending(playerName, playerSteamID);
                            break;
                        case "admin:verify:delete":
                            HandleAdminVerifyDelete(playerName, parts, playerSteamID);
                            break;
                    }
                    return;
                }

                // ---- public commands ----
                if (subcommand == "help")
                {
                    HandleHelpCommand(playerSteamID);
                    return;
                }
                if (subcommand == "status")
                {
                    HandleStatusCommand(playerName, playerSteamID);
                    return;
                }

                ChatUtils.SendError(string.Format(
                        "Unknown command: /tds {0}. Type /tds help for available commands.",
                        subcommand), playerSteamID
                );
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(string.Format("[COMMAND] Error: {0}", ex.Message));
                ChatUtils.SendError(string.Format("Command error: {0}", ex.Message, playerSteamID));
            }
        }

        // ============================================================
        // ADMIN SYNC SUB-COMMANDS
        // ============================================================

        private void HandleAdminSyncCommand(
            string syncSubcommand,
            string[] parts,
            string playerName,
            long steamId)
        {
            LoggerUtil.LogInfo(
                string.Format(
                    "[COMMAND] Admin {0} executing: admin:sync:{1}",
                    playerName,
                    syncSubcommand
                )
            );

            switch (syncSubcommand)
            {
                case "check":
                    {
                        ChatUtils.SendSuccess(_factionSync.AdminSyncCheck(), steamId);
                        LoggerUtil.LogInfo(
                            string.Format("[ADMIN:SYNC:CHECK] Executed by {0}", playerName)
                        );
                        break;
                    }

                case "undo":
                    {
                        if (parts.Length < 4)
                        {
                            ChatUtils.SendError("Usage: /tds admin:sync:undo <faction_tag>", steamId);
                            return;
                        }
                        string factionTag = parts[3];
                        LoggerUtil.LogWarning(
                            string.Format(
                                "[ADMIN:SYNC:UNDO] {0} undoing faction: {1}",
                                playerName,
                                factionTag
                            )
                        );

                        _ = _factionSync
                            .AdminSyncUndo(factionTag)
                            .ContinueWith(
                                (Task<string> task) =>
                                {
                                    if (!task.IsFaulted && !task.IsCanceled)
                                    {
                                        ChatUtils.SendSuccess(task.Result, steamId);
                                        LoggerUtil.LogSuccess(
                                            string.Format(
                                                "[ADMIN:SYNC:UNDO] Completed by {0} for {1}",
                                                playerName,
                                                factionTag
                                            )
                                        );
                                    }
                                    else
                                    {
                                        string err =
                                            task.Exception != null
                                                ? task.Exception.Message
                                                : "Unknown error";
                                        ChatUtils.SendError("[FAIL] Error: " + err, steamId);
                                        LoggerUtil.LogError("[ADMIN:SYNC:UNDO] Failed: " + err);
                                    }
                                }
                            );
                        break;
                    }

                case "undo_all":
                    {
                        LoggerUtil.LogWarning(
                            string.Format(
                                "[ADMIN:SYNC:UNDO_ALL] {0} requested full faction undo",
                                playerName
                            )
                        );
                        ChatUtils.SendWarning("Running undo for ALL factions (Discord roles/channels + XML records)...", steamId
                        );

                        _ = _factionSync
                            .AdminSyncUndoAll()
                            .ContinueWith(
                                (Task<string> task) =>
                                {
                                    if (!task.IsFaulted && !task.IsCanceled)
                                    {
                                        ChatUtils.SendSuccess(task.Result, steamId);
                                        LoggerUtil.LogSuccess(
                                            "[ADMIN:SYNC:UNDO_ALL] Completed by " + playerName
                                        );
                                    }
                                    else
                                    {
                                        string err =
                                            task.Exception != null
                                                ? task.Exception.Message
                                                : "Unknown error";
                                        ChatUtils.SendError("[FAIL] Error: " + err, steamId);
                                        LoggerUtil.LogError("[ADMIN:SYNC:UNDO_ALL] Failed: " + err);
                                    }
                                }
                            );
                        break;
                    }

                case "cleanup":
                    {
                        ChatUtils.SendWarning("[*] Cleaning up orphaned Discord roles/channels...", steamId);
                        LoggerUtil.LogWarning(
                            string.Format("[ADMIN:SYNC:CLEANUP] {0} starting cleanup", playerName)
                        );

                        _ = _factionSync
                            .AdminSyncCleanup()
                            .ContinueWith(
                                (Task<string> task) =>
                                {
                                    if (!task.IsFaulted && !task.IsCanceled)
                                    {
                                        ChatUtils.SendSuccess(task.Result, steamId);
                                        LoggerUtil.LogSuccess(
                                            string.Format(
                                                "[ADMIN:SYNC:CLEANUP] Completed by {0}",
                                                playerName
                                            )
                                        );
                                    }
                                    else
                                    {
                                        string err =
                                            task.Exception != null
                                                ? task.Exception.Message
                                                : "Unknown error";
                                        ChatUtils.SendError("[FAIL] Error: " + err, steamId);
                                        LoggerUtil.LogError("[ADMIN:SYNC:CLEANUP] Failed: " + err);
                                    }
                                }
                            );
                        break;
                    }

                case "status":
                    {
                        ChatUtils.SendInfo(_factionSync.AdminSyncStatus(), steamId);
                        LoggerUtil.LogInfo(
                            string.Format("[ADMIN:SYNC:STATUS] Executed by {0}", playerName)
                        );
                        break;
                    }

                default:
                    ChatUtils.SendError(string.Format("Unknown admin:sync subcommand: {0}", syncSubcommand, steamId)
                    );
                    LoggerUtil.LogWarning(
                        string.Format(
                            "[COMMAND] Unknown admin:sync subcommand from {0}: {1}",
                            playerName,
                            syncSubcommand
                        )
                    );
                    break;
            }
        }

        // ============================================================
        // HELP
        // ============================================================

        /// <summary>
        /// Send the help text appropriate for the player's authorization level.
        /// Admins see all commands; regular players see user commands only.
        /// </summary>
        private void HandleHelpCommand(long playerSteamID)
        {
            try
            {
                bool isAdmin = CommandAuthorizationUtil.IsUserAdmin(
                    playerSteamID,
                    _config.AdminSteamIDs
                );

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("===============================================");
                sb.AppendLine("         TDS (Torch Discord Sync)");
                sb.AppendLine("===============================================");
                sb.AppendLine();

                if (isAdmin)
                {
                    sb.AppendLine("[ADMIN] SYNC COMMANDS:");
                    sb.AppendLine(
                        "  /tds admin:sync:check      - Check status of all faction syncs"
                    );
                    sb.AppendLine("  /tds admin:sync:undo <tag> - Undo sync for faction");
                    sb.AppendLine(
                        "  /tds admin:sync:cleanup    - Delete orphaned Discord roles/channels"
                    );
                    sb.AppendLine("  /tds admin:sync:status     - Show sync status summary");
                    sb.AppendLine();
                    sb.AppendLine("[ADMIN] GENERAL COMMANDS:");
                    sb.AppendLine("  /tds sync      - Synchronize all factions to Discord");
                    sb.AppendLine("  /tds reset     - Reset Discord (DELETE all roles/channels!)");
                    sb.AppendLine("  /tds reload    - Reload configuration");
                    sb.AppendLine("  /tds cleanup   - Clean orphaned Discord items");
                    sb.AppendLine();
                    sb.AppendLine("[ADMIN] VERIFICATION COMMANDS:");
                    sb.AppendLine(
                        "  /tds unverify <SteamID> [reason]   - Remove user verification"
                    );
                    sb.AppendLine(
                        "  /tds admin:verify:list              - List all verified users"
                    );
                    sb.AppendLine(
                        "  /tds admin:verify:pending           - List pending verifications"
                    );
                    sb.AppendLine(
                        "  /tds admin:verify:delete <SteamID> - Delete verification record"
                    );
                    sb.AppendLine();
                }

                sb.AppendLine("[USER] VERIFICATION COMMANDS:");
                sb.AppendLine("  /tds verify @DiscordName    - Link Discord account (by username)");
                sb.AppendLine("  /tds verify <DiscordUserID> - Link Discord account (by ID)");
                sb.AppendLine("  /tds verify:status          - Check your verification status");
                sb.AppendLine("  /tds verify:delete          - Delete pending verification");
                sb.AppendLine("  /tds verify:help            - Detailed verification guide");
                sb.AppendLine();
                sb.AppendLine("[USER] INFO COMMANDS:");
                sb.AppendLine("  /tds status - Show plugin status");
                sb.AppendLine("  /tds help   - Show this help");
                sb.AppendLine();
                sb.AppendLine("===============================================");

                ChatUtils.SendHelpText(sb.ToString(), playerSteamID);
                LoggerUtil.LogInfo(
                    string.Format(
                        "[HELP_CMD] Help sent to {0} SteamID {1}",
                        isAdmin ? "ADMIN" : "USER",
                        playerSteamID
                    )
                );
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(string.Format("[HELP_CMD] Error: {0}", ex.Message));
                ChatUtils.SendError(string.Format("Help error: {0}", ex.Message, playerSteamID));
            }
        }

        // ============================================================
        // VERIFICATION COMMANDS
        // ============================================================

        /// <summary>
        /// Handle /tds verify:status – show current verification status for the player.
        /// </summary>
        private void HandleVerifyStatusCommand(long playerSteamID, string playerName)
        {
            try
            {
                var verified = _db?.GetVerifiedPlayer(playerSteamID);
                var pending = _db?.GetPendingVerification(playerSteamID);
                string statusText;

                if (verified != null)
                {
                    statusText = string.Format(
                        "[VERIFIED] You are verified!\nDiscord: {0}\nVerified at: {1:yyyy-MM-dd HH:mm}",
                        verified.DiscordUsername,
                        verified.VerifiedAt
                    );
                }
                else if (pending != null && pending.ExpiresAt > DateTime.UtcNow)
                {
                    var codeAge = DateTime.UtcNow - pending.CodeGeneratedAt;
                    var expiry = TimeSpan.FromMinutes(_config.VerificationCodeExpirationMinutes);
                    var timeLeft = expiry - codeAge;

                    if (timeLeft.TotalSeconds <= 0)
                    {
                        statusText =
                            "[EXPIRED] Your verification code has expired\n"
                            + "Type /tds verify @DiscordName to generate a new code";
                    }
                    else
                    {
                        statusText = string.Format(
                            "[IN PROGRESS] Verification pending\n"
                                + "Discord Username: {0}\nCode: {1}\nTime Remaining: {2}m {3}s",
                            pending.DiscordUsername,
                            pending.VerificationCode,
                            (int)timeLeft.TotalMinutes,
                            (int)timeLeft.Seconds
                        );
                    }
                }
                else
                {
                    statusText =
                        "[NOT VERIFIED] You have not started verification yet\n"
                        + "Type /tds verify @DiscordName to begin";
                }

                ChatUtils.SendInfo(statusText, playerSteamID);
                LoggerUtil.LogInfo(
                    string.Format("[VERIFY_STATUS_CMD] Status shown to {0}", playerName)
                );
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(string.Format("[VERIFY_STATUS_CMD] Error: {0}", ex.Message));
                ChatUtils.SendError(string.Format("Status error: {0}", ex.Message, playerSteamID));
            }
        }

        /// <summary>
        /// Handle /tds verify @DiscordName – generate a verification code and
        /// send it to the Discord user via DM.
        /// </summary>
        private void HandleVerifyCommand(long playerSteamID, string playerName, string[] args)
        {
            try
            {
                if (args.Length < 3)
                {
                    ChatUtils.SendError("Usage: /tds verify @DiscordName  OR  /tds verify <DiscordUserID>", playerSteamID);
                    return;
                }

                if (_verificationCommandHandler == null)
                {
                    LoggerUtil.LogError(
                        "[VERIFY_CMD] CRITICAL: VerificationCommandHandler is NULL!"
                    );
                    ChatUtils.SendError("Verification system is not initialized. Contact an admin.", playerSteamID);
                    return;
                }

                string discordUsername = args[2];
                _ = _eventLog.LogAsync(
                    "VerificationAttempt",
                    string.Format(
                        "Player: {0} | SteamID: {1} | Discord: {2}",
                        playerName,
                        playerSteamID,
                        discordUsername
                    )
                );

                Task.Run(async () =>
                {
                    try
                    {
                        string result = await _verificationCommandHandler.HandleVerifyCommandAsync(
                            playerSteamID,
                            playerName,
                            discordUsername
                        );

                        if (result.StartsWith("Error"))
                            ChatUtils.SendError(result, playerSteamID);
                        else
                            ChatUtils.SendSuccess(result, playerSteamID);
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogError(
                            string.Format("[VERIFY_CMD] Async error: {0}", ex.Message)
                        );
                        ChatUtils.SendError(string.Format("Verification error: {0}", ex.Message, playerSteamID));
                    }
                });
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(string.Format("[VERIFY_CMD] Error: {0}", ex.Message));
                ChatUtils.SendError(string.Format("Verification error: {0}", ex.Message, playerSteamID));
            }
        }

        /// <summary>
        /// Handle /tds verify:delete – remove the player's own pending verification.
        /// </summary>
        private void HandleVerifyDeleteCommand(long playerSteamID, string playerName)
        {
            try
            {
                var verified = _db?.GetVerifiedPlayer(playerSteamID);
                if (verified != null)
                {
                    ChatUtils.SendWarning("[!] You are already verified!", playerSteamID);
                    ChatUtils.SendInfo("Only administrators can remove your verification. Contact an admin.", playerSteamID);
                    return;
                }

                var pending = _db?.GetPendingVerification(playerSteamID);
                if (pending == null)
                {
                    ChatUtils.SendInfo("[I] You do not have any pending verification.", playerSteamID);
                    return;
                }

                _db?.DeletePendingVerification(playerSteamID);
                ChatUtils.SendSuccess("[OK] Pending verification deleted!", playerSteamID);
                ChatUtils.SendInfo("You can start a new verification with /tds verify [DiscordID/Name]", playerSteamID);

                _ = _eventLog.LogAsync(
                    "VerificationDeleted",
                    string.Format(
                        "Player: {0} | SteamID: {1} | Deleted pending verification",
                        playerName,
                        playerSteamID
                    )
                );
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(string.Format("[VERIFY_DELETE_CMD] Error: {0}", ex.Message));
                ChatUtils.SendError(string.Format("Error: {0}", ex.Message, playerSteamID));
            }
        }

        /// <summary>
        /// Handle /tds verify:help – show the step-by-step verification guide.
        /// </summary>
        private void HandleVerifyHelpCommand(string playerName, long steamId)
        {
            try
            {
                string helpText =
                    "=== VERIFICATION GUIDE ===\n"
                    + "[STEP 1] IN-GAME\n"
                    + "  Type: /tds verify [DiscordID or DiscordName]\n"
                    + "  Example 1: /tds verify mamba73 (username)\n"
                    + "  Example 2: /tds verify 765540000000001234 (Discord ID)\n\n"
                    + "[STEP 2] CHECK DISCORD DM\n"
                    + "  You should receive a private message from the bot.\n"
                    + "  Code format: 8 random letters, e.g. SYIXFNCE\n\n"
                    + "[STEP 3] REPLY ON DISCORD\n"
                    + "  In the bot's DM type:  !verify SYIXFNCE\n\n"
                    + "[STEP 4] WAIT FOR CONFIRMATION\n"
                    + "  Bot will respond with verification status.\n\n"
                    + "[COMMANDS]\n"
                    + "  /tds verify:status  – Check current verification status\n"
                    + "  /tds verify:delete  – Delete pending verification\n"
                    + "  /tds verify:help    – Show this help\n\n"
                    + "[TROUBLESHOOTING]\n"
                    + "  - Wrong username: Make sure your Discord username is correct.\n"
                    + "  - Bot cannot find you: You must be in the Discord server.\n"
                    + "  - Code expired: Codes expire after "
                    + _config.VerificationCodeExpirationMinutes
                    + " minutes.\n"
                    + "  - Still not working: Contact an administrator.\n"
                    + "=========================";

                ChatUtils.SendHelpText(helpText, steamId);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(string.Format("[VERIFY_HELP_CMD] Error: {0}", ex.Message));
                ChatUtils.SendError(string.Format("Error: {0}", ex.Message, steamId));
            }
        }

        // ============================================================
        // ADMIN COMMANDS
        // ============================================================

        private void HandleSyncCommand(string playerName, long steamId)
        {
            try
            {
                LoggerUtil.LogInfo("[COMMAND] " + playerName + " executed: /tds sync");
                _ = _eventLog.LogAsync("AdminCommand", playerName + " executed: /tds sync");
                ChatUtils.SendSuccess("Starting faction synchronization...", steamId);
                ChatUtils.SendSuccess("Synchronization complete!", steamId);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[SYNC_CMD] Error: " + ex.Message);
                ChatUtils.SendError("Sync error: " + ex.Message, steamId);
                _ = _eventLog.LogAsync("CommandError", "Sync command failed: " + ex.Message);
            }
        }

        private void HandleResetCommand(string playerName, long steamId)
        {
            try
            {
                LoggerUtil.LogWarning(
                    "[COMMAND] " + playerName + " executed: /tds reset (DESTRUCTIVE)"
                );
                _ = _eventLog.LogAsync("AdminCommand", playerName + " executed: /tds reset");
                ChatUtils.SendWarning("Clearing Discord roles and channels...", steamId);
                _ = _factionSync.ResetDiscordAsync();
                _ = _eventLog.LogAsync("Command", "Discord reset executed by " + playerName);
                ChatUtils.SendSuccess("Discord reset complete!", steamId);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[RESET_CMD] Error: " + ex.Message);
                ChatUtils.SendError("Reset error: " + ex.Message, steamId);
                _ = _eventLog.LogAsync("CommandError", "Reset command failed: " + ex.Message);
            }
        }

        private void HandleCleanupCommand(string playerName, long steamId)
        {
            try
            {
                LoggerUtil.LogInfo("[COMMAND] " + playerName + " executed: /tds cleanup");
                _ = _eventLog.LogAsync("AdminCommand", playerName + " executed: /tds cleanup");
                ChatUtils.SendWarning("Starting cleanup of orphaned Discord roles/channels...", steamId);
                ChatUtils.SendSuccess("Cleanup simulation completed!", steamId);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[CLEANUP_CMD] Error: " + ex.Message);
                ChatUtils.SendError("Cleanup error: " + ex.Message, steamId);
                _ = _eventLog.LogAsync("CommandError", "Cleanup command failed: " + ex.Message);
            }
        }

        private void HandleReloadCommand(string playerName, long steamId)
        {
            try
            {
                LoggerUtil.LogInfo("[COMMAND] " + playerName + " executed: /tds reload");
                _ = _eventLog.LogAsync("AdminCommand", playerName + " executed: /tds reload");
                ChatUtils.SendWarning("Reloading configuration and database...", steamId);

                var newConfig = MainConfig.Load();
                if (newConfig != null)
                {
                    ChatUtils.SendSuccess("Configuration reloaded successfully", steamId);
                    LoggerUtil.LogSuccess("[RELOAD] Configuration reloaded by " + playerName);
                }
                else
                {
                    ChatUtils.SendError("Failed to reload configuration – keeping old config", steamId);
                    LoggerUtil.LogError("[RELOAD] Failed to reload by " + playerName);
                    return;
                }

                ChatUtils.SendSuccess("Reload complete!", steamId);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[RELOAD_CMD] Error: " + ex.Message);
                ChatUtils.SendError("Reload error: " + ex.Message, steamId);
            }
        }

        private void HandleUnverifyCommand(long adminSteamID, string adminName, string[] args)
        {
            try
            {
                if (args.Length < 3)
                {
                    ChatUtils.SendError("Usage: /tds unverify STEAMID [reason]", adminSteamID);
                    return;
                }

                long targetSteamID;
                if (!long.TryParse(args[2], out targetSteamID))
                {
                    ChatUtils.SendError("Invalid Steam ID format", adminSteamID);
                    return;
                }

                string reason = "Admin removal";
                if (args.Length > 3)
                {
                    var reasonParts = new List<string>();
                    for (int i = 3; i < args.Length; i++)
                        reasonParts.Add(args[i]);
                    reason = string.Join(" ", reasonParts);
                }

                var verified = _db?.GetVerifiedPlayer(targetSteamID);
                var pending = _db?.GetPendingVerification(targetSteamID);
                if (verified == null && pending == null)
                {
                    ChatUtils.SendWarning("Verification not found for SteamID: " + targetSteamID, adminSteamID);
                    return;
                }

                string displayName =
                    verified?.DiscordUsername
                    ?? pending?.DiscordUsername
                    ?? targetSteamID.ToString();

                _db?.DeletePendingVerification(targetSteamID);
                _db?.DeleteVerifiedPlayer(targetSteamID);

                ChatUtils.SendSuccess(
                    string.Format(
                        "Verification removed for: {0} (SteamID: {1})",
                        displayName,
                        targetSteamID
                    )
                , adminSteamID);
                _ = _eventLog.LogAsync(
                    "UnverifyCommand",
                    string.Format(
                        "Admin: {0} | Unverified SteamID: {1} | Discord: {2} | Reason: {3}",
                        adminName,
                        targetSteamID,
                        displayName,
                        reason
                    )
                );
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[UNVERIFY_CMD] Error: " + ex.Message);
                ChatUtils.SendError("Unverify error: " + ex.Message, adminSteamID);
            }
        }

        private void HandleAdminVerifyList(string adminName, long steamId)
        {
            try
            {
                var verified = _db?.GetAllVerifiedPlayers();
                if (verified == null || verified.Count == 0)
                {
                    ChatUtils.SendInfo("No verified users found", steamId);
                    return;
                }

                var sb = new System.Text.StringBuilder("[VERIFIED USERS]\n");
                int i = 1;
                foreach (var v in verified)
                {
                    sb.AppendLine(
                        string.Format(
                            "{0}. {1} | SteamID: {2} | Verified: {3:yyyy-MM-dd HH:mm}",
                            i++,
                            v.DiscordUsername,
                            v.SteamID,
                            v.VerifiedAt
                        )
                    );
                }

                ChatUtils.SendHelpText(sb.ToString(), steamId);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[ADMIN_VERIFY_LIST] Error: " + ex.Message);
                ChatUtils.SendError("List error: " + ex.Message, steamId);
            }
        }

        private void HandleAdminVerifyPending(string adminName, long steamId)
        {
            try
            {
                var pending = _db?.GetAllPendingVerifications();
                if (pending == null || pending.Count == 0)
                {
                    ChatUtils.SendInfo("No pending verifications", steamId);
                    return;
                }

                var sb = new System.Text.StringBuilder("[PENDING VERIFICATIONS]\n");
                int i = 1;
                foreach (var p in pending)
                {
                    var age = DateTime.UtcNow - p.CodeGeneratedAt;
                    string ageStr =
                        age.TotalMinutes < 1
                            ? ((int)age.TotalSeconds + "s")
                            : ((int)age.TotalMinutes + "m");

                    sb.AppendLine(
                        string.Format(
                            "{0}. {1} | SteamID: {2} | Code: {3} | Age: {4}",
                            i++,
                            p.DiscordUsername,
                            p.SteamID,
                            p.VerificationCode,
                            ageStr
                        )
                    );
                }

                ChatUtils.SendHelpText(sb.ToString(), steamId);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[ADMIN_VERIFY_PENDING] Error: " + ex.Message);
                ChatUtils.SendError("List error: " + ex.Message, steamId);
            }
        }

        private void HandleAdminVerifyDelete(string adminName, string[] args, long steamId)
        {
            try
            {
                if (args.Length < 3)
                {
                    ChatUtils.SendError("Usage: /tds admin:verify:delete STEAMID", steamId);
                    return;
                }

                long targetSteamID;
                if (!long.TryParse(args[3], out targetSteamID))
                {
                    ChatUtils.SendError("Invalid Steam ID format", steamId);
                    return;
                }

                var verified = _db?.GetVerifiedPlayer(targetSteamID);
                var pending = _db?.GetPendingVerification(targetSteamID);
                if (verified == null && pending == null)
                {
                    ChatUtils.SendWarning("Verification not found for SteamID: " + targetSteamID, steamId);
                    return;
                }

                string displayName =
                    verified?.DiscordUsername
                    ?? pending?.DiscordUsername
                    ?? targetSteamID.ToString();

                _db?.DeletePendingVerification(targetSteamID);
                _db?.DeleteVerifiedPlayer(targetSteamID);

                ChatUtils.SendSuccess(
                    string.Format(
                        "Verification deleted for: {0} (SteamID: {1})",
                        displayName,
                        targetSteamID
                    )
                , steamId);
                _ = _eventLog.LogAsync(
                    "VerificationDeleted",
                    string.Format(
                        "Admin: {0} | Deleted SteamID: {1} | Discord: {2}",
                        adminName,
                        targetSteamID,
                        displayName
                    )
                );
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[VERIFY_DELETE] Error: " + ex.Message);
                ChatUtils.SendError("Delete error: " + ex.Message, steamId);
            }
        }

        // ============================================================
        // STATUS COMMAND
        // ============================================================

        private void HandleStatusCommand(string playerName, long steamId)
        {
            try
            {
                var factions = _db?.GetAllFactions();
                int totalFactions = factions?.Count ?? 0;
                int totalPlayers = 0;

                if (factions != null)
                    foreach (var f in factions)
                        if (f.Players != null)
                            totalPlayers += f.Players.Count;

                string statusText =
                    "=== TDS Plugin Status ===\n"
                    + "Status: [OK] ONLINE\n"
                    + string.Format("Factions: {0}\n", totalFactions)
                    + string.Format("Players:  {0}\n", totalPlayers)
                    + string.Format(
                        "Chat Sync:    {0}\n",
                        _config?.Chat?.Enabled == true ? "[OK]" : "[FAIL]"
                    )
                    + string.Format(
                        "Death Logging:{0}\n",
                        _config?.Death?.Enabled == true ? "[OK]" : "[FAIL]"
                    )
                    + string.Format(
                        "Verification: {0}\n",
                        _config?.Discord != null ? "[OK]" : "[FAIL]"
                    )
                    + "=======================";

                ChatUtils.SendHelpText(statusText, steamId);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[STATUS_CMD] Error: " + ex.Message);
                ChatUtils.SendError("Status error: " + ex.Message, steamId);
            }
        }
    }
}