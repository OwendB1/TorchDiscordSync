// Plugin/index.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using Discord.WebSocket;
using mamba.TorchDiscordSync.Plugin.Config;
using mamba.TorchDiscordSync.Plugin.Core;
using mamba.TorchDiscordSync.Plugin.Handlers;
using mamba.TorchDiscordSync.Plugin.Models;
using mamba.TorchDiscordSync.Plugin.Services;
using mamba.TorchDiscordSync.Plugin.Utils;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.API.Session;
using Torch.Commands;
using Torch.Managers.ChatManager;

namespace mamba.TorchDiscordSync
{
    /// <summary>
    /// mamba.TorchDiscordSync.Plugin – Space Engineers faction/Discord sync plugin.
    /// Features: faction sync, bidirectional chat relay, death logging, server
    /// monitoring, player verification, and admin commands.
    /// </summary>
    public class MambaTorchDiscordSyncPlugin : TorchPluginBase
    {
        // ---- core services ----
        private DatabaseService             _db;
        private FactionSyncService          _factionSync;
        private DiscordBotService           _discordBot;
        private DiscordService              _discordWrapper;
        private EventLoggingService         _eventLog;
        private ITorchBase                  _torch;
        private ChatSyncService             _chatSync;
        private DeathLogService             _deathLog;
        private VerificationService         _verification;
        private VerificationCommandHandler  _verificationCommandHandler;
        private SyncOrchestrator            _orchestrator;
        private DeathMessageHandler         _deathMessageHandler;
        private PlayerTrackingService       _playerTracking;
        private DamageTrackingService       _damageTracking;
        private MonitoringService           _monitoringService;
        private TdsCommandService           _tdsCommandService;

        // ---- handlers ----
        private CommandProcessor            _commandProcessor;
        private EventManager                _eventManager;
        private ChatModerator               _chatModerator;
        private DiscordAdminCommandService  _adminCommandService;

        // ---- configuration ----
        private MainConfig                  _config;
        private DiscordBotConfig            _discordBotConfig;

        /// <summary>Read-only access to the loaded plugin configuration.</summary>
        public MainConfig Config => _config;
        public TdsCommandService CommandService => _tdsCommandService;

        // ---- state flags ----
        private Timer           _syncTimer;
        private ITorchSession   _currentSession;
        private bool            _isInitialized              = false;
        private bool            _serverStartupLogged        = false;
        private bool            _playerTrackingInitialized  = false;
        private bool            _damageTrackingInitialized  = false;

        // ============================================================
        // INIT
        // ============================================================

        /// <summary>
        /// Plugin entry point – called once by Torch when the plugin is loaded.
        /// Initializes all services, hooks the session state callback, and
        /// optionally starts the faction sync timer.
        /// </summary>
        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            try
            {
                PluginUtils.PrintBanner("INITIALIZING");
                _torch = torch;

                // ---- load configuration ----
                _config = MainConfig.Load();
                if (_config == null)
                {
                    LoggerUtil.LogError("Failed to load configuration!");
                    return;
                }
                LoggerUtil.LogInfo("Configuration loaded - Debug mode: " + _config.Debug);

                // Build a DiscordBotConfig shim from MainConfig for backward compat
                _discordBotConfig = new DiscordBotConfig
                {
                    BotToken                          = _config.Discord.BotToken,
                    GuildID                           = _config.Discord.GuildID,
                    BotPrefix                         = _config.Discord.BotPrefix,
                    EnableDMNotifications             = _config.Discord.EnableDMNotifications,
                    VerificationCodeExpirationMinutes =
                        _config.Discord.VerificationCodeExpirationMinutes,
                };

                // ---- database (XML-based) ----
                _db = new DatabaseService();
                LoggerUtil.LogSuccess("Database service initialized (XML-based)");

                // ---- Discord bot ----
                _discordBot = new DiscordBotService(_discordBotConfig);
                Task.Run(delegate { return ConnectBotAsync(); });

                _discordWrapper = new DiscordService(_discordBot);

                // ---- verification ----
                _verification = new VerificationService(
                    _db, _config.VerificationCodeExpirationMinutes);

                // ---- event logging ----
                _eventLog = new EventLoggingService(_db, _discordWrapper, _config);

                // ---- death tracking ----
                _deathLog = new DeathLogService(_db, _eventLog, _config);

                try
                {
                    _damageTracking = new DamageTrackingService(_config);
                    LoggerUtil.LogInfo(
                        "[INIT] DamageTrackingService instance created (Init deferred to session load)");
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogError(
                        "[INIT] Failed to create DamageTrackingService: " + ex.Message);
                    _damageTracking = null;
                }

                // Torch.TorchBase is required by PlayerTrackingService
                var torchBase = torch as Torch.TorchBase;
                if (torchBase == null)
                {
                    LoggerUtil.LogError(
                        "Torch instance is not TorchBase! " +
                        "Compatibility with this Torch version is not guaranteed.");
                    _playerTracking = null;
                    return;
                }

                // Death handler must be created before PlayerTrackingService
                _deathMessageHandler = new DeathMessageHandler(
                    _eventLog, _config, _damageTracking);

                _playerTracking = new PlayerTrackingService(
                    _eventLog, _torch, _deathLog, _config, _deathMessageHandler);

                // ---- faction sync ----
                _factionSync = new FactionSyncService(_db, _discordWrapper, _config);

                // ---- chat sync ----
                _chatSync = new ChatSyncService(_discordWrapper, _config, _db);

                // ---- Discord → game message routing ----
                if (_discordBot != null)
                {
                    _discordBot.OnMessageReceivedEvent += async msg =>
                    {
                        // ── Admin bot channel: handled exclusively by DiscordAdminCommandService ──
                        if (_config.Discord.AdminBotChannelId > 0
                            && msg.Channel.Id == _config.Discord.AdminBotChannelId)
                        {
                            if (_adminCommandService != null)
                                await _adminCommandService.HandleMessageAsync(msg);
                            return;
                        }

                        // Ignore bots and bot-prefix commands for all other channels
                        if (msg.Author.IsBot ||
                            msg.Content.StartsWith(_config.Discord.BotPrefix))
                            return;

                        if (!(msg.Channel is SocketTextChannel textChannel))
                            return;

                        ulong channelId = textChannel.Id;

                        // Global Discord channel → game global chat
                        if (channelId == _config.Discord.ChatChannelId)
                        {
                            await _chatSync.SendDiscordMessageToGameAsync(
                                msg.Author.Username, msg.Content);
                            LoggerUtil.LogDebug(
                                "[DISCORD>GAME] Forwarded message from " +
                                msg.Author.Username);
                            return;
                        }

                        // Faction Discord channel → game faction members (private message)
                        var factions = _db?.GetAllFactions();
                        if (factions != null)
                        {
                            var faction = factions.FirstOrDefault(
                                f => f.DiscordChannelID == channelId);
                            if (faction != null)
                            {
                                await _chatSync.SendDiscordMessageToFactionInGameAsync(
                                    faction.FactionID,
                                    msg.Author.Username,
                                    msg.Content);
                                LoggerUtil.LogDebug(string.Format(
                                    "[DISCORD>FACTION] {0} from {1}",
                                    faction.Tag, msg.Author.Username));
                            }
                        }
                    };
                }

                // ---- orchestrator ----
                _orchestrator = new SyncOrchestrator(
                    _db, _discordWrapper, _factionSync, _eventLog, _config);

                // ---- admin Discord command service ----
                _adminCommandService = new DiscordAdminCommandService(
                    _discordBot, _db, _factionSync, _orchestrator, _eventLog, _config);
                if (_config.Discord.AdminBotChannelId > 0)
                    LoggerUtil.LogSuccess(
                        "[INIT] DiscordAdminCommandService ready – admin channel ID: " +
                        _config.Discord.AdminBotChannelId);
                else
                    LoggerUtil.LogWarning(
                        "[INIT] DiscordAdminCommandService: AdminBotChannelId not set – Discord admin commands disabled");

                // ---- verification command handler ----
                _verificationCommandHandler = new VerificationCommandHandler(
                    _verification, _eventLog, _config, _discordBot, _discordBotConfig);
                LoggerUtil.LogInfo("[INIT] VerificationCommandHandler created and ready");

                _tdsCommandService = new TdsCommandService(
                    this,
                    _config,
                    _db,
                    _factionSync,
                    _eventLog,
                    _orchestrator,
                    _verificationCommandHandler);

                // ---- command processor (chat routing + legacy /tds bridge) ----
                _commandProcessor = new CommandProcessor(
                    _config,
                    _db,
                    _chatSync,
                    _playerTracking,
                    _tdsCommandService
                );
                LoggerUtil.LogInfo("[INIT] CommandProcessor created");

                _eventManager = new EventManager(_config, _discordWrapper, _eventLog);
                _chatModerator = new ChatModerator(_config, _discordWrapper, _db);

                LoggerUtil.LogSuccess("All services initialized");

                // ---- Discord bot verification event ----
                if (_discordBot != null)
                {
                    _discordBot.OnVerificationAttempt += delegate(
                        string code, ulong discordID, string discordUsername)
                    {
                        Task.Run(delegate
                        {
                            return HandleVerificationAsync(code, discordID, discordUsername);
                        });
                    };
                }

                // ---- session state callback ----
                var sessionManagerObj =
                    torch.Managers.GetManager(typeof(ITorchSessionManager));
                var sessionManager = sessionManagerObj as ITorchSessionManager;
                if (sessionManager != null)
                {
                    sessionManager.SessionStateChanged += OnSessionStateChanged;
                    LoggerUtil.LogSuccess("Session manager hooked");
                }
                else
                {
                    LoggerUtil.LogError(
                        "Session manager not available! Check Torch version or references.");
                }

                LoggerUtil.LogInfo(
                    "Player tracking will initialize when session loads");

                // ---- faction sync timer ----
                int syncInterval = _config.Discord.SyncIntervalSeconds * 1000;
                if (syncInterval <= 0 ||
                    (_config.Faction != null && !_config.Faction.Enabled))
                {
                    LoggerUtil.LogInfo(
                        "Faction sync timer NOT created – disabled or interval is 0");
                }
                else
                {
                    _syncTimer = new Timer(syncInterval);
                    _syncTimer.Elapsed  += OnSyncTimerElapsed;
                    _syncTimer.AutoReset = true;
                    LoggerUtil.LogInfo(string.Format(
                        "Faction sync timer created (interval: {0}ms)", syncInterval));
                }

                _isInitialized = true;
                PluginUtils.PrintBanner("INITIALIZATION COMPLETE");

                // Persist any merged/default values back to disk
                _config.Save();
                LoggerUtil.LogInfo("Configuration saved after initialization/load");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Plugin initialization failed: " + ex.Message);
                LoggerUtil.LogError("Stack trace: " + ex.StackTrace);
                _isInitialized = false;
            }
        }

        // ============================================================
        // DISPOSE
        // ============================================================

        public override void Dispose()
        {
            // Clean up death message handler
            _deathMessageHandler?.Cleanup();

            // Clean up player tracking
            _playerTracking?.Dispose();

            // Stop and dispose sync timer
            if (_syncTimer != null)
            {
                _syncTimer.Stop();
                _syncTimer.Dispose();
            }

            // Detach Discord bot events
            if (_discordBot != null)
                _discordBot.OnVerificationAttempt -= null;

            // Clean up monitoring service
            if (_monitoringService != null)
            {
                _monitoringService.Dispose();
                _monitoringService = null;
            }

            // Unhook chat message handler
            try
            {
                var torchServer = _torch as ITorchServer;
                if (torchServer?.CurrentSession?.Managers != null)
                {
                    var chatManager =
                        torchServer.CurrentSession.Managers.GetManager<ChatManagerServer>();
                    if (chatManager != null)
                    {
                        chatManager.MessageRecieved -= _commandProcessor.HandleChatMessage;
                        LoggerUtil.LogInfo("Unhooked chat message handler");
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Error unhooking chat handler: " + ex.Message);
            }

            base.Dispose();
        }

        // ============================================================
        // PUBLIC COMMAND ENTRY POINT
        // (kept here so external callers have a stable API; delegates to
        //  the legacy /tds bridge for compatibility)
        // ============================================================

        /// <summary>
        /// Handle a /tds command string received from the in-game chat.
        /// Delegates immediately to CommandProcessor for actual processing.
        /// </summary>
        public void HandleChatCommand(
            string command, long playerSteamID, string playerName)
        {
            if (_commandProcessor != null)
            {
                LoggerUtil.LogDebug("[COMMAND] Forwarding legacy chat command: " + command);
                _commandProcessor.HandleLegacyChatCommand(command, playerSteamID, playerName);
            }
            else
            {
                LoggerUtil.LogError(
                    "[COMMAND] CommandProcessor is null – cannot process command");
            }
        }

        public bool ReloadConfiguration()
        {
            var newConfig = MainConfig.Load();
            if (newConfig == null)
                return false;

            _config.Enabled = newConfig.Enabled;
            _config.Debug = newConfig.Debug;
            _config.SyncIntervalSeconds = newConfig.SyncIntervalSeconds;
            _config.AdminSteamIDs = newConfig.AdminSteamIDs;
            _config.Discord = newConfig.Discord;
            _config.Chat = newConfig.Chat;
            _config.Death = newConfig.Death;
            _config.Monitoring = newConfig.Monitoring;
            _config.Faction = newConfig.Faction;
            _config.VerificationCodeExpirationMinutes = newConfig.VerificationCodeExpirationMinutes;
            _config.CleanupIntervalSeconds = newConfig.CleanupIntervalSeconds;
            _config.DamageHistoryMaxSeconds = newConfig.DamageHistoryMaxSeconds;
            _config.DataStorage = newConfig.DataStorage;
            _config.PluginVersion = newConfig.PluginVersion;

            if (_discordBotConfig != null && _config.Discord != null)
            {
                _discordBotConfig.BotToken = _config.Discord.BotToken;
                _discordBotConfig.GuildID = _config.Discord.GuildID;
                _discordBotConfig.BotPrefix = _config.Discord.BotPrefix;
                _discordBotConfig.EnableDMNotifications = _config.Discord.EnableDMNotifications;
                _discordBotConfig.VerificationCodeExpirationMinutes =
                    _config.Discord.VerificationCodeExpirationMinutes;
            }

            return true;
        }

        // ============================================================
        // PRIVATE – SESSION LIFECYCLE
        // ============================================================

        /// <summary>
        /// Invoked by Torch whenever the server session changes state.
        /// Handles service initialization/teardown at the correct lifecycle phase.
        /// </summary>
        private void OnSessionStateChanged(
            ITorchSession session, TorchSessionState state)
        {
            _currentSession = session;

            switch (state)
            {
                case TorchSessionState.Loading:
                    LoggerUtil.LogInfo("=== Server session LOADING ===");
                    _serverStartupLogged = false;
                    break;

                case TorchSessionState.Loaded:
                    LoggerUtil.LogSuccess("=== Server session LOADED ===");
                    _serverStartupLogged = false;

                    // 1. Initialize DamageTrackingService
                    if (_damageTracking != null && !_damageTrackingInitialized)
                    {
                        try
                        {
                            _damageTracking.Init();
                            _damageTrackingInitialized = true;
                            LoggerUtil.LogSuccess(
                                "[DAMAGE_TRACK] DamageTrackingService initialized");
                        }
                        catch (Exception ex)
                        {
                            LoggerUtil.LogError(
                                "[DAMAGE_TRACK] Failed to initialize: " + ex.Message);
                        }
                    }

                    // 2. Initialize KillerDetectionService
                    if (_deathMessageHandler != null)
                    {
                        try
                        {
                            _deathMessageHandler.InitializeKillerDetection();
                            LoggerUtil.LogSuccess("[KILLER_DETECTION] Service initialized");
                        }
                        catch (Exception ex)
                        {
                            LoggerUtil.LogError(
                                "[KILLER_DETECTION] Failed to initialize: " + ex.Message);
                        }
                    }

                    // 3. Initialize PlayerTrackingService (requires ChatManagerServer)
                    if (_playerTracking != null && !_playerTrackingInitialized)
                    {
                        try
                        {
                            _playerTracking.Initialize();
                            _playerTrackingInitialized = true;
                            LoggerUtil.LogSuccess(
                                "Player tracking service initialized after session load");
                        }
                        catch (Exception ex)
                        {
                            LoggerUtil.LogError(
                                "Failed to initialize player tracking: " + ex.Message);
                        }
                    }

                    // 4. Hook chat message handler via CommandProcessor
                    try
                    {
                        var torchServer = _torch as ITorchServer;
                        if (torchServer?.CurrentSession?.Managers != null)
                        {
                            var chatManager =
                                torchServer.CurrentSession.Managers
                                    .GetManager<ChatManagerServer>();
                            if (chatManager != null)
                            {
                                // All chat routing and command parsing is handled
                                // inside CommandProcessor.HandleChatMessage.
                                chatManager.MessageRecieved +=
                                    _commandProcessor.HandleChatMessage;
                                LoggerUtil.LogSuccess(
                                    "Torch chat message handler registered for /tds commands");
                            }
                            else
                            {
                                LoggerUtil.LogWarning(
                                    "ChatManagerServer is null after session loaded – " +
                                    "chat commands disabled");
                            }
                        }
                        else
                        {
                            LoggerUtil.LogWarning(
                                "CurrentSession or Managers is null after session loaded – " +
                                "chat commands disabled");
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogError(
                            "Failed to hook chat commands after session load: " +
                            ex.Message);
                    }

                    // 5. Initialize MonitoringService
                    try
                    {
                        if (_monitoringService == null && _discordBot != null)
                        {
                            _monitoringService = new MonitoringService(_config, _discordBot);
                            _monitoringService.Initialize();
                            LoggerUtil.LogSuccess(
                                "[MONITORING] MonitoringService initialized after session load");
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogError(
                            "[MONITORING] Failed to initialize MonitoringService: " +
                            ex.Message);
                    }

                    // 6. Send server-started status to Discord (delayed for stable SimSpeed)
                    if (_config?.Monitoring?.Enabled == true && _eventLog != null)
                    {
                        Task.Run(async () =>
                        {
                            await Task.Delay(3000);
                            float stableSimSpeed = PluginUtils.GetCurrentSimSpeed();
                            await _eventLog.LogServerStatusAsync("STARTED", stableSimSpeed);
                        });
                    }

                    // 7. Run startup routines (faction sync, timer start, etc.)
                    if (_isInitialized)
                        OnServerLoadedAsync(session);
                    break;

                case TorchSessionState.Unloading:
                    LoggerUtil.LogInfo("=== Server session UNLOADING ===");
                    if (_syncTimer != null && _syncTimer.Enabled)
                        _syncTimer.Stop();
                    if (_monitoringService != null)
                    {
                        _monitoringService.Stop();
                        LoggerUtil.LogInfo("[MONITORING] MonitoringService stopped");
                    }
                    break;

                case TorchSessionState.Unloaded:
                    LoggerUtil.LogWarning("=== Server session UNLOADED ===");
                    _playerTrackingInitialized = false;

                    if (_config?.Monitoring?.Enabled == true && _eventLog != null)
                    {
                        Task.Run(async () =>
                        {
                            await _eventLog.LogServerStatusAsync("STOPPED", 0);
                        });
                    }
                    break;
            }
        }

        /// <summary>
        /// Called once after the session has fully loaded.
        /// Performs the initial faction sync and starts the periodic sync timer.
        /// </summary>
        private void OnServerLoadedAsync(ITorchSession session)
        {
            try
            {
                if (_serverStartupLogged)
                    return;
                _serverStartupLogged = true;
                LoggerUtil.LogInfo("[STARTUP] Initializing server sync...");

                // Report stable SimSpeed after physics settle
                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(5000);
                        float stableSimSpeed = PluginUtils.GetCurrentSimSpeed();

                        if (_orchestrator != null)
                        {
                            await _orchestrator.CheckServerStatusAsync(stableSimSpeed);
                            LoggerUtil.LogSuccess(string.Format(
                                "[STARTUP] Post-load status reported. SimSpeed: {0:F2}",
                                stableSimSpeed));
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogError(
                            "Error in delayed status check: " + ex.Message);
                    }
                });

                // Load factions from the running game session and sync to Discord
                var factions = _factionSync.LoadFactionsFromGame();
                if (factions.Count > 0)
                {
                    LoggerUtil.LogInfo(string.Format(
                        "[STARTUP] Found {0} player factions", factions.Count));
                    _orchestrator.ExecuteFullSyncAsync(factions).Wait();
                }
                else
                {
                    LoggerUtil.LogWarning(
                        "[STARTUP] No player factions found (tag length != 3)");
                }

                // Start the periodic sync timer
                if (_syncTimer != null && !_syncTimer.Enabled)
                {
                    _syncTimer.Start();
                    LoggerUtil.LogSuccess(string.Format(
                        "[STARTUP] Sync timer started (interval: {0}s)",
                        _config.Discord.SyncIntervalSeconds));
                }

                LoggerUtil.LogSuccess("[STARTUP] Server startup sync complete!");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[STARTUP] Error: " + ex.Message);
                _eventLog?.LogAsync("StartupError", ex.Message).Wait();
            }
        }

        /// <summary>
        /// Periodic faction sync timer callback.
        /// Only executes when faction sync is enabled in the configuration.
        /// </summary>
        private void OnSyncTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (_config?.Faction?.Enabled == true && _orchestrator != null)
                _orchestrator.SyncFactionsAsync().Wait();
            else
                LoggerUtil.LogDebug(
                    "[SYNC] Faction sync disabled – timer fired but skipped");
        }

        // ============================================================
        // PRIVATE – DISCORD BOT HELPERS
        // ============================================================

        /// <summary>
        /// Establish the Discord bot connection asynchronously.
        /// </summary>
        private Task ConnectBotAsync()
        {
            if (_discordBot == null)
                return Task.FromResult(0);

            return _discordBot.ConnectAsync().ContinueWith(t =>
            {
                if (t.Result)
                    LoggerUtil.LogInfo(
                        "Discord Bot login/start completed; waiting for Ready event"
                    );
            });
        }

        /// <summary>
        /// Handle a verification attempt initiated from the Discord side.
        /// Assigns roles on success and notifies the player both via Discord DM
        /// and an in-game private message.
        /// </summary>
        private Task HandleVerificationAsync(
            string code, ulong discordID, string discordUsername)
        {
            if (_verificationCommandHandler == null)
                return Task.FromResult(0);

            return _verificationCommandHandler
                .VerifyFromDiscordAsync(code, discordID, discordUsername)
                .ContinueWith(async t =>
                {
                    try
                    {
                        var result    = t.Result;
                        bool isSuccess = result.IsSuccess;
                        LoggerUtil.LogInfo("[VERIFY] Verification result: " + result.Message);

                        var verifiedPlayer = isSuccess
                            ? _db?.GetVerifiedPlayerByDiscordID(discordID)
                            : null;

                        // Assign "Verified" Discord role on success
                        if (isSuccess && verifiedPlayer != null && _discordBot != null)
                        {
                            ulong verifiedRoleId =
                                await _discordBot.GetOrCreateVerifiedRoleAsync();
                            if (verifiedRoleId != 0)
                            {
                                _config.Discord.VerifiedRoleId = verifiedRoleId;
                                _config.Save();

                                var discordClient = _discordBot.GetClient();
                                if (discordClient != null)
                                {
                                    var discordUser = discordClient.GetUser(discordID);
                                    if (discordUser != null)
                                    {
                                        bool roleAssigned =
                                            await _discordBot.AssignVerifiedRoleAsync(
                                                discordUser, verifiedRoleId);
                                        if (roleAssigned)
                                            LoggerUtil.LogSuccess(
                                                "[VERIFY] Assigned Verified role to " +
                                                discordUsername);
                                    }
                                }
                            }

                            // Also assign the player's faction role
                            if (_db != null)
                            {
                                var playerFaction = _db.GetAllFactions()?.FirstOrDefault(f =>
                                    f.Players != null &&
                                    f.Players.Any(p => p.SteamID == verifiedPlayer.SteamID));

                                if (playerFaction != null)
                                {
                                    var discordClient = _discordBot.GetClient();
                                    if (discordClient != null)
                                    {
                                        var discordUser = discordClient.GetUser(discordID);
                                        if (discordUser != null)
                                        {
                                            bool factionRoleAssigned =
                                                await _discordBot.AssignFactionRoleAsync(
                                                    discordUser, playerFaction.Tag);
                                            if (factionRoleAssigned)
                                                LoggerUtil.LogSuccess(string.Format(
                                                    "[VERIFY] Assigned faction role '{0}' to {1}",
                                                    playerFaction.Tag, discordUsername));
                                        }
                                    }
                                }
                            }
                        }

                        // Send result via Discord DM
                        if (_discordBot != null)
                        {
                            await _discordBot.SendVerificationResultDMAsync(
                                discordUsername, discordID, result.Message, isSuccess);
                            LoggerUtil.LogDebug(
                                "[VERIFY] Sent verification result DM to " + discordUsername);
                        }

                        // Send in-game notification to the player
                        if (result.SteamIdForNotify.HasValue &&
                            !string.IsNullOrEmpty(result.GamePlayerName))
                        {
                            SendInGameNotification(
                                result.SteamIdForNotify.Value,
                                result.GamePlayerName,
                                isSuccess,
                                result.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogError(
                            "[VERIFY] Error in HandleVerificationAsync: " + ex.Message);
                    }
                });
        }

        /// <summary>
        /// Send an in-game private chat notification to a player about their
        /// verification result.  Only visible to the target player.
        /// </summary>
        private void SendInGameNotification(
            long steamID, string playerName, bool isSuccess, string message)
        {
            try
            {
                var players = new List<VRage.Game.ModAPI.IMyPlayer>();
                MyAPIGateway.Players.GetPlayers(players);

                var player = players.FirstOrDefault(p => (long)p.SteamUserId == steamID);
                if (player?.Character == null)
                    return;

                string notificationMsg = isSuccess
                    ? "[OK] Verification successful! Discord account linked."
                    : "[FAIL] Verification failed: " + message;

                Sandbox.Game.MyVisualScriptLogicProvider.SendChatMessage(
                    notificationMsg, "TDS", player.Character.EntityId, "Blue");
                LoggerUtil.LogSuccess("[VERIFY_NOTIFY] Sent to " + playerName);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[VERIFY_NOTIFY] Error: " + ex.Message);
            }
        }
    }
}
