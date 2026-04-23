using System;
using System.Text;
using System.Threading.Tasks;
using TorchDiscordSync;
using TorchDiscordSync.Plugin.Config;
using TorchDiscordSync.Plugin.Core;
using TorchDiscordSync.Plugin.Utils;

namespace TorchDiscordSync.Plugin.Services
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
        private readonly TorchDiscordSyncPlugin _plugin;
        private readonly SyncOrchestrator _orchestrator;

        public TdsCommandService(
            TorchDiscordSyncPlugin plugin,
            MainConfig config,
            DatabaseService db,
            FactionSyncService factionSync,
            EventLoggingService eventLog,
            SyncOrchestrator orchestrator)
        {
            _plugin = plugin;
            _config = config;
            _db = db;
            _factionSync = factionSync;
            _eventLog = eventLog;
            _orchestrator = orchestrator;
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
            }

            Respond(request, sb.ToString().TrimEnd());
        }

        public void ShowStatus(TdsCommandRequest request)
        {
            try
            {
                var factions = _db?.GetAllFactions();
                var totalFactions = factions?.Count ?? 0;
                var totalPlayers = 0;

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

                Respond(request, sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[TDS:STATUS] " + ex.Message);
                Respond(request, "[FAIL] Status error: " + ex.Message);
            }
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
                    var result = await _factionSync.AdminSyncUndo(factionTag.ToUpperInvariant());
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
                    var result = await _factionSync.AdminSyncUndoAll();
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
                    var result = await _factionSync.AdminSyncCleanup();
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
