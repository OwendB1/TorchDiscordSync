using System;
using System.Text;
using System.Threading.Tasks;
using mamba.TorchDiscordSync;
using mamba.TorchDiscordSync.Plugin.Config;
using mamba.TorchDiscordSync.Plugin.Core;
using mamba.TorchDiscordSync.Plugin.Utils;

namespace mamba.TorchDiscordSync.Plugin.Services
{
    public sealed class TdsCommandRequest
    {
        public bool IsAdmin { get; set; }
        public string PlayerName { get; set; }
        public long SteamId { get; set; }
        public Action<string> Respond { get; set; }
    }

    /// <summary>
    /// Central command execution layer shared by the Torch command module and
    /// the legacy /tds chat bridge.
    /// </summary>
    public class TdsCommandService
    {
        private readonly MainConfig _config;
        private readonly DatabaseService _db;
        private readonly EventLoggingService _eventLog;
        private readonly FactionSyncService _factionSync;
        private readonly MambaTorchDiscordSyncPlugin _plugin;
        private readonly SyncOrchestrator _orchestrator;
        private readonly VerificationCommandHandler _verificationCommandHandler;

        public TdsCommandService(
            MambaTorchDiscordSyncPlugin plugin,
            MainConfig config,
            DatabaseService db,
            FactionSyncService factionSync,
            EventLoggingService eventLog,
            SyncOrchestrator orchestrator,
            VerificationCommandHandler verificationCommandHandler)
        {
            _plugin = plugin;
            _config = config;
            _db = db;
            _factionSync = factionSync;
            _eventLog = eventLog;
            _orchestrator = orchestrator;
            _verificationCommandHandler = verificationCommandHandler;
        }

        public void ShowHelp(TdsCommandRequest request)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Torch Discord Sync ===");
            sb.AppendLine("Primary syntax: !tds ...");
            sb.AppendLine();
            sb.AppendLine("[USER]");
            sb.AppendLine("!tds help");
            sb.AppendLine("!tds status");
            sb.AppendLine("!tds verify <DiscordNameOrId>");
            sb.AppendLine("!tds verify status");
            sb.AppendLine("!tds verify delete");
            sb.AppendLine("!tds verify help");

            if (request.IsAdmin)
            {
                sb.AppendLine();
                sb.AppendLine("[ADMIN]");
                sb.AppendLine("!tds admin sync");
                sb.AppendLine("!tds admin sync check");
                sb.AppendLine("!tds admin sync status");
                sb.AppendLine("!tds admin sync undo <FactionTag>");
                sb.AppendLine("!tds admin sync undoall");
                sb.AppendLine("!tds admin sync cleanup");
                sb.AppendLine("!tds admin reset");
                sb.AppendLine("!tds admin cleanup");
                sb.AppendLine("!tds admin reload");
                sb.AppendLine("!tds admin unverify <SteamId> [reason]");
                sb.AppendLine("!tds admin verify list");
                sb.AppendLine("!tds admin verify pending");
                sb.AppendLine("!tds admin verify delete <SteamId>");
            }

            Respond(request, sb.ToString().TrimEnd());
        }

        public void ShowStatus(TdsCommandRequest request)
        {
            try
            {
                var factions = _db?.GetAllFactions();
                int totalFactions = factions?.Count ?? 0;
                int totalPlayers = 0;

                if (factions != null)
                {
                    foreach (var faction in factions)
                    {
                        if (faction.Players != null)
                            totalPlayers += faction.Players.Count;
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine("=== TDS Status ===");
                sb.AppendLine("Status: ONLINE");
                sb.AppendLine($"Factions: {totalFactions}");
                sb.AppendLine($"Players: {totalPlayers}");
                sb.AppendLine($"Chat Sync: {BoolStatus(_config?.Chat?.Enabled == true)}");
                sb.AppendLine($"Death Logging: {BoolStatus(_config?.Death?.Enabled == true)}");
                sb.AppendLine($"Verification: {BoolStatus(_verificationCommandHandler != null)}");

                Respond(request, sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[TDS:STATUS] " + ex.Message);
                Respond(request, "[FAIL] Status error: " + ex.Message);
            }
        }

        public void StartVerification(TdsCommandRequest request, string discordIdentity)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(discordIdentity))
                {
                    Respond(request, "Usage: !tds verify <DiscordNameOrId>");
                    return;
                }

                if (_verificationCommandHandler == null)
                {
                    LoggerUtil.LogError("[TDS:VERIFY] VerificationCommandHandler is null");
                    Respond(request, "[FAIL] Verification system is not initialized.");
                    return;
                }

                _ = _eventLog?.LogAsync(
                    "VerificationAttempt",
                    $"Player: {request.PlayerName} | SteamID: {request.SteamId} | Discord: {discordIdentity}");

                Task.Run(async () =>
                {
                    try
                    {
                        string result = await _verificationCommandHandler.HandleVerifyCommandAsync(
                            request.SteamId,
                            request.PlayerName,
                            discordIdentity);

                        Respond(request, result);
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogError("[TDS:VERIFY] " + ex.Message);
                        Respond(request, "[FAIL] Verification error: " + ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[TDS:VERIFY] " + ex.Message);
                Respond(request, "[FAIL] Verification error: " + ex.Message);
            }
        }

        public void ShowVerificationStatus(TdsCommandRequest request)
        {
            try
            {
                var verified = _db?.GetVerifiedPlayer(request.SteamId);
                var pending = _db?.GetPendingVerification(request.SteamId);

                if (verified != null)
                {
                    Respond(
                        request,
                        $"[OK] Verified\nDiscord: {verified.DiscordUsername}\nVerified: {verified.VerifiedAt:yyyy-MM-dd HH:mm}");
                    return;
                }

                if (pending != null && pending.ExpiresAt > DateTime.UtcNow)
                {
                    var remaining = pending.ExpiresAt - DateTime.UtcNow;
                    Respond(
                        request,
                        $"[I] Verification pending\nDiscord: {pending.DiscordUsername}\nCode: {pending.VerificationCode}\nExpires in: {(int)remaining.TotalMinutes}m {remaining.Seconds}s");
                    return;
                }

                Respond(request, "[I] No active verification. Use !tds verify <DiscordNameOrId>.");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[TDS:VERIFY:STATUS] " + ex.Message);
                Respond(request, "[FAIL] Status error: " + ex.Message);
            }
        }

        public void DeletePendingVerification(TdsCommandRequest request)
        {
            try
            {
                var verified = _db?.GetVerifiedPlayer(request.SteamId);
                if (verified != null)
                {
                    Respond(request, "[FAIL] You are already verified. Contact an admin to remove it.");
                    return;
                }

                var pending = _db?.GetPendingVerification(request.SteamId);
                if (pending == null)
                {
                    Respond(request, "[I] No pending verification found.");
                    return;
                }

                _db.DeletePendingVerification(request.SteamId);
                _ = _eventLog?.LogAsync(
                    "VerificationDeleted",
                    $"Player: {request.PlayerName} | SteamID: {request.SteamId} | Deleted pending verification");

                Respond(request, "[OK] Pending verification deleted.");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[TDS:VERIFY:DELETE] " + ex.Message);
                Respond(request, "[FAIL] Delete error: " + ex.Message);
            }
        }

        public void ShowVerificationHelp(TdsCommandRequest request)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Verification Guide ===");
            sb.AppendLine("1. Run: !tds verify <DiscordNameOrId>");
            sb.AppendLine("2. Check your Discord DM from the bot.");
            sb.AppendLine("3. Reply in Discord with: !verify <code>");
            sb.AppendLine("4. Confirm with: !tds verify status");
            sb.AppendLine();
            sb.AppendLine($"Codes expire after {_config.VerificationCodeExpirationMinutes} minutes.");
            Respond(request, sb.ToString().TrimEnd());
        }

        public void RunFullSync(TdsCommandRequest request)
        {
            if (!RequireAdmin(request))
                return;

            Respond(request, "[I] Starting faction synchronization...");
            Task.Run(async () =>
            {
                try
                {
                    await _orchestrator.SyncFactionsAsync();
                    _ = _eventLog?.LogAsync("AdminCommand", $"{request.PlayerName} executed admin sync");
                    Respond(request, "[OK] Faction synchronization complete.");
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogError("[TDS:ADMIN:SYNC] " + ex.Message);
                    Respond(request, "[FAIL] Sync error: " + ex.Message);
                }
            });
        }

        public void RunAdminSyncCheck(TdsCommandRequest request)
        {
            if (!RequireAdmin(request))
                return;

            try
            {
                Respond(request, _factionSync.AdminSyncCheck());
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[TDS:ADMIN:SYNC:CHECK] " + ex.Message);
                Respond(request, "[FAIL] Sync check error: " + ex.Message);
            }
        }

        public void RunAdminSyncStatus(TdsCommandRequest request)
        {
            if (!RequireAdmin(request))
                return;

            try
            {
                Respond(request, _factionSync.AdminSyncStatus());
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[TDS:ADMIN:SYNC:STATUS] " + ex.Message);
                Respond(request, "[FAIL] Sync status error: " + ex.Message);
            }
        }

        public void RunAdminSyncUndo(TdsCommandRequest request, string factionTag)
        {
            if (!RequireAdmin(request))
                return;

            if (string.IsNullOrWhiteSpace(factionTag))
            {
                Respond(request, "Usage: !tds admin sync undo <FactionTag>");
                return;
            }

            Respond(request, $"[I] Undoing sync for {factionTag.ToUpperInvariant()}...");
            Task.Run(async () =>
            {
                try
                {
                    string result = await _factionSync.AdminSyncUndo(factionTag.ToUpperInvariant());
                    Respond(request, result);
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogError("[TDS:ADMIN:SYNC:UNDO] " + ex.Message);
                    Respond(request, "[FAIL] Undo error: " + ex.Message);
                }
            });
        }

        public void RunAdminSyncUndoAll(TdsCommandRequest request)
        {
            if (!RequireAdmin(request))
                return;

            Respond(request, "[I] Undoing sync for all factions...");
            Task.Run(async () =>
            {
                try
                {
                    string result = await _factionSync.AdminSyncUndoAll();
                    Respond(request, result);
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogError("[TDS:ADMIN:SYNC:UNDOALL] " + ex.Message);
                    Respond(request, "[FAIL] Undo-all error: " + ex.Message);
                }
            });
        }

        public void RunAdminSyncCleanup(TdsCommandRequest request)
        {
            if (!RequireAdmin(request))
                return;

            Respond(request, "[I] Cleaning up orphaned Discord roles and channels...");
            Task.Run(async () =>
            {
                try
                {
                    string result = await _factionSync.AdminSyncCleanup();
                    Respond(request, result);
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogError("[TDS:ADMIN:SYNC:CLEANUP] " + ex.Message);
                    Respond(request, "[FAIL] Cleanup error: " + ex.Message);
                }
            });
        }

        public void RunReset(TdsCommandRequest request)
        {
            if (!RequireAdmin(request))
                return;

            Respond(request, "[I] Resetting Discord faction roles and channels...");
            Task.Run(async () =>
            {
                try
                {
                    await _factionSync.ResetDiscordAsync();
                    _ = _eventLog?.LogAsync("AdminCommand", $"{request.PlayerName} executed admin reset");
                    Respond(request, "[OK] Discord reset complete.");
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogError("[TDS:ADMIN:RESET] " + ex.Message);
                    Respond(request, "[FAIL] Reset error: " + ex.Message);
                }
            });
        }

        public void RunCleanup(TdsCommandRequest request)
        {
            RunAdminSyncCleanup(request);
        }

        public void RunReload(TdsCommandRequest request)
        {
            if (!RequireAdmin(request))
                return;

            try
            {
                if (_plugin.ReloadConfiguration())
                {
                    _ = _eventLog?.LogAsync("AdminCommand", $"{request.PlayerName} executed admin reload");
                    Respond(request, "[OK] Configuration reloaded.");
                    return;
                }

                Respond(request, "[FAIL] Failed to reload configuration.");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[TDS:ADMIN:RELOAD] " + ex.Message);
                Respond(request, "[FAIL] Reload error: " + ex.Message);
            }
        }

        public void RunUnverify(TdsCommandRequest request, string steamIdText, string reason)
        {
            if (!RequireAdmin(request))
                return;

            if (!long.TryParse(steamIdText, out long targetSteamId))
            {
                Respond(request, "Usage: !tds admin unverify <SteamId> [reason]");
                return;
            }

            try
            {
                var verified = _db?.GetVerifiedPlayer(targetSteamId);
                var pending = _db?.GetPendingVerification(targetSteamId);
                if (verified == null && pending == null)
                {
                    Respond(request, "[I] Verification not found for that SteamID.");
                    return;
                }

                string displayName = verified?.DiscordUsername ?? pending?.DiscordUsername ?? targetSteamId.ToString();

                _db.DeletePendingVerification(targetSteamId);
                _db.DeleteVerifiedPlayer(targetSteamId);
                _ = _eventLog?.LogAsync(
                    "UnverifyCommand",
                    $"Admin: {request.PlayerName} | Unverified SteamID: {targetSteamId} | Discord: {displayName} | Reason: {reason}");

                Respond(request, $"[OK] Verification removed for {displayName} ({targetSteamId}).");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[TDS:ADMIN:UNVERIFY] " + ex.Message);
                Respond(request, "[FAIL] Unverify error: " + ex.Message);
            }
        }

        public void ListVerifiedUsers(TdsCommandRequest request)
        {
            if (!RequireAdmin(request))
                return;

            try
            {
                var verified = _db?.GetAllVerifiedPlayers();
                if (verified == null || verified.Count == 0)
                {
                    Respond(request, "[I] No verified users found.");
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("[VERIFIED USERS]");
                for (int i = 0; i < verified.Count; i++)
                {
                    var item = verified[i];
                    sb.AppendLine(
                        $"{i + 1}. {item.DiscordUsername} | SteamID: {item.SteamID} | Player: {item.GamePlayerName ?? "?"} | Verified: {item.VerifiedAt:yyyy-MM-dd HH:mm}");
                }

                Respond(request, sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[TDS:ADMIN:VERIFY:LIST] " + ex.Message);
                Respond(request, "[FAIL] List error: " + ex.Message);
            }
        }

        public void ListPendingVerifications(TdsCommandRequest request)
        {
            if (!RequireAdmin(request))
                return;

            try
            {
                var pending = _db?.GetAllPendingVerifications();
                if (pending == null || pending.Count == 0)
                {
                    Respond(request, "[I] No pending verifications.");
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("[PENDING VERIFICATIONS]");
                for (int i = 0; i < pending.Count; i++)
                {
                    var item = pending[i];
                    var age = DateTime.UtcNow - item.CodeGeneratedAt;
                    string ageText = age.TotalMinutes < 1
                        ? $"{(int)age.TotalSeconds}s"
                        : $"{(int)age.TotalMinutes}m";

                    sb.AppendLine(
                        $"{i + 1}. {item.DiscordUsername} | SteamID: {item.SteamID} | Code: {item.VerificationCode} | Age: {ageText}");
                }

                Respond(request, sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[TDS:ADMIN:VERIFY:PENDING] " + ex.Message);
                Respond(request, "[FAIL] Pending list error: " + ex.Message);
            }
        }

        public void DeleteVerificationRecord(TdsCommandRequest request, string steamIdText)
        {
            if (!RequireAdmin(request))
                return;

            if (!long.TryParse(steamIdText, out long targetSteamId))
            {
                Respond(request, "Usage: !tds admin verify delete <SteamId>");
                return;
            }

            try
            {
                var verified = _db?.GetVerifiedPlayer(targetSteamId);
                var pending = _db?.GetPendingVerification(targetSteamId);
                if (verified == null && pending == null)
                {
                    Respond(request, "[I] Verification not found for that SteamID.");
                    return;
                }

                string displayName = verified?.DiscordUsername ?? pending?.DiscordUsername ?? targetSteamId.ToString();

                _db.DeletePendingVerification(targetSteamId);
                _db.DeleteVerifiedPlayer(targetSteamId);
                _ = _eventLog?.LogAsync(
                    "VerificationDeleted",
                    $"Admin: {request.PlayerName} | Deleted SteamID: {targetSteamId} | Discord: {displayName}");

                Respond(request, $"[OK] Verification deleted for {displayName} ({targetSteamId}).");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[TDS:ADMIN:VERIFY:DELETE] " + ex.Message);
                Respond(request, "[FAIL] Delete error: " + ex.Message);
            }
        }

        private static string BoolStatus(bool value)
        {
            return value ? "Enabled" : "Disabled";
        }

        private bool RequireAdmin(TdsCommandRequest request)
        {
            if (request.IsAdmin)
                return true;

            Respond(request, "[FAIL] Admin permissions are required for this command.");
            return false;
        }

        private void Respond(TdsCommandRequest request, string message)
        {
            if (request?.Respond == null || string.IsNullOrWhiteSpace(message))
                return;

            if (_plugin?.Torch != null)
            {
                _plugin.Torch.InvokeAsync(() => request.Respond(message), nameof(TdsCommandService));
                return;
            }

            request.Respond(message);
        }
    }
}
