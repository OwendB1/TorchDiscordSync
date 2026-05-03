// Plugin/index.cs

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Controls;
using Sandbox.Game;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.API.Session;
using Torch.Managers.ChatManager;
using TorchDiscordSync.Plugin.Config;
using TorchDiscordSync.Plugin.Core;
using TorchDiscordSync.Plugin.Handlers;
using TorchDiscordSync.Plugin.Services;
using TorchDiscordSync.Plugin.UI;
using TorchDiscordSync.Plugin.Utils;
using TorchDiscordSync.Shared.Ipc;

namespace TorchDiscordSync
{
    /// <summary>
    /// TorchDiscordSync.Plugin – Space Engineers faction/Discord sync plugin.
    /// Features: faction sync, bidirectional chat relay, death logging, server
    /// monitoring, and admin commands.
    /// </summary>
    public class TorchDiscordSyncPlugin : TorchPluginBase, IWpfPlugin
    {
        private const string LegacyDiscordTextCommandPrefix = "!";

        // ---- core services ----
        private DatabaseService             _db;
        private FactionSyncService          _factionSync;
        private DiscordBotService           _discordBot;
        private DiscordService              _discordWrapper;
        private EventLoggingService         _eventLog;
        private ITorchBase                  _torch;
        private ChatSyncService             _chatSync;
        private DeathLogService             _deathLog;
        private SyncOrchestrator            _orchestrator;
        private DeathMessageHandler         _deathMessageHandler;
        private PlayerTrackingService       _playerTracking;
        private DamageTrackingService       _damageTracking;
        private MonitoringService           _monitoringService;
        private DiscordPresenceService      _discordPresenceService;
        private TdsCommandService           _tdsCommandService;

        // ---- handlers ----
        private CommandProcessor            _commandProcessor;
        private EventManager                _eventManager;
        private DiscordAdminCommandService  _adminCommandService;

        // ---- configuration ----
        private MainConfig                  _config;

        /// <summary>Read-only access to the loaded plugin configuration.</summary>
        public MainConfig Config => _config;
        public TdsCommandService CommandService => _tdsCommandService;

        // ---- state flags ----
        private Timer           _syncTimer;
        private ITorchSession   _currentSession;
        private TorchSessionState _sessionState            = TorchSessionState.Unloaded;
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
                LoggerUtil.SetDebugMode(_config.Debug);
                LoggerUtil.LogInfo("Configuration loaded - Debug mode: " + _config.Debug);

                // ---- database (XML-based) ----
                _db = new DatabaseService();
                LoggerUtil.LogSuccess("Database service initialized (XML-based)");

                // ---- Discord bot ----
                _discordBot = new DiscordBotService(CreateDiscordRuntimeConfig());
                _discordWrapper = new DiscordService(_discordBot);
                _discordPresenceService = new DiscordPresenceService(_config, _discordWrapper);
                Task.Run(delegate { return ConnectBotAsync(); });

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
                var torchBase = torch as TorchBase;
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
                _playerTracking.OnlinePlayersChanged += OnOnlinePlayersChanged;

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
                            && msg.ChannelId == _config.Discord.AdminBotChannelId)
                        {
                            if (_adminCommandService != null)
                                await _adminCommandService.HandleMessageAsync(msg).ConfigureAwait(false);
                            return;
                        }

                        // Ignore bots and legacy text commands for all other channels
                        if (msg.AuthorIsBot ||
                            string.IsNullOrWhiteSpace(msg.Content) ||
                            msg.Content.StartsWith(
                                LegacyDiscordTextCommandPrefix,
                                StringComparison.Ordinal))
                            return;

                        var channelId = msg.ChannelId;

                        // Global Discord channel → game global chat
                        if (channelId == _config.Discord.ChatChannelId)
                        {
                            _chatSync.SendDiscordMessageToGame(msg.AuthorUsername, msg.Content);
                            LoggerUtil.LogDebug(
                                "[DISCORD>GAME] Forwarded message from " +
                                msg.AuthorUsername);
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
                                    msg.AuthorUsername,
                                    msg.Content).ConfigureAwait(false);
                                LoggerUtil.LogDebug(string.Format(
                                    "[DISCORD>FACTION] {0} from {1}",
                                    faction.Tag, msg.AuthorUsername));
                            }
                        }
                    };
                }

                // ---- orchestrator ----
                _orchestrator = new SyncOrchestrator(_db, _discordWrapper, _factionSync, _eventLog, _config);

                // ---- admin Discord command service ----
                _adminCommandService = new DiscordAdminCommandService(_discordWrapper, _db, _factionSync, _orchestrator, _eventLog, _config);
                if (_config.Discord.AdminBotChannelId > 0)
                    LoggerUtil.LogSuccess(
                        "[INIT] DiscordAdminCommandService ready – admin channel ID: " +
                        _config.Discord.AdminBotChannelId);
                else
                    LoggerUtil.LogWarning(
                        "[INIT] DiscordAdminCommandService: AdminBotChannelId not set – Discord admin commands disabled");

                _tdsCommandService = new TdsCommandService(
                    this,
                    _config,
                    _db,
                    _factionSync,
                    _eventLog,
                    _orchestrator);

                // ---- command processor (chat routing + legacy /tds bridge) ----
                _commandProcessor = new CommandProcessor(
                    _config,
                    _db,
                    _chatSync,
                    _playerTracking,
                    _tdsCommandService,
                    _orchestrator
                );
                LoggerUtil.LogInfo("[INIT] CommandProcessor created");

                _eventManager = new EventManager(_config, _discordWrapper, _eventLog);

                LoggerUtil.LogSuccess("All services initialized");

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
                var syncInterval = _config.Discord.SyncIntervalSeconds * 1000;
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
                    LoggerUtil.LogInfo($"Faction sync timer created (interval: {syncInterval}ms)");
                }

                _isInitialized = true;
                PluginUtils.PrintBanner("INITIALIZATION COMPLETE");

                // Persist any merged/default values back to disk
                _config.Save();
                LoggerUtil.LogInfo("Configuration saved after initialization/load");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogException("Plugin initialization failed.", ex);
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
            if (_playerTracking != null)
                _playerTracking.OnlinePlayersChanged -= OnOnlinePlayersChanged;
            _playerTracking?.Dispose();

            // Stop and dispose sync timer
            if (_syncTimer != null)
            {
                _syncTimer.Stop();
                _syncTimer.Dispose();
            }

            // Clean up monitoring service
            if (_monitoringService != null)
            {
                _monitoringService.Dispose();
                _monitoringService = null;
            }

            if (_discordPresenceService != null)
            {
                _discordPresenceService.Dispose();
                _discordPresenceService = null;
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

            try
            {
                _discordWrapper?.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Error stopping Discord host: " + ex.Message);
            }

            base.Dispose();
        }

        public UserControl GetControl()
        {
            return new TorchDiscordSyncControl(this);
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

            return ApplyConfigurationInternal(
                newConfig,
                saveToDisk: false,
                restartDiscordHost: true,
                out _);
        }

        public bool ApplyConfiguration(MainConfig updatedConfig, out string error)
        {
            return ApplyConfigurationInternal(
                updatedConfig,
                saveToDisk: true,
                restartDiscordHost: true,
                out error);
        }

        private void OnOnlinePlayersChanged()
        {
            _discordPresenceService?.RequestPlayerCountUpdate();
        }

        private bool ApplyConfigurationInternal(
            MainConfig sourceConfig,
            bool saveToDisk,
            bool restartDiscordHost,
            out string error)
        {
            error = null;

            try
            {
                if (sourceConfig == null)
                {
                    error = "Configuration payload was null.";
                    return false;
                }

                if (_config == null)
                    _config = new MainConfig();

                _config.ApplyFrom(sourceConfig);
                LoggerUtil.SetDebugMode(_config.Debug);

                if (saveToDisk && !_config.TrySave(out error))
                    return false;

                ReconfigureRuntime(restartDiscordHost);
                return true;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogException("Failed to apply configuration.", ex);
                error = ex.Message;
                return false;
            }
        }

        private void ReconfigureRuntime(bool restartDiscordHost)
        {
            RebuildSyncTimer();
            RestartDiscordRuntimeServices(restartDiscordHost);
        }

        private void RebuildSyncTimer()
        {
            if (_syncTimer != null)
            {
                _syncTimer.Stop();
                _syncTimer.Dispose();
                _syncTimer = null;
            }

            var syncIntervalSeconds = _config?.Discord?.SyncIntervalSeconds ?? 0;
            if (syncIntervalSeconds <= 0 || (_config?.Faction != null && !_config.Faction.Enabled))
            {
                LoggerUtil.LogInfo(
                    "Faction sync timer NOT recreated – disabled or interval is 0");
                return;
            }

            _syncTimer = new Timer(syncIntervalSeconds * 1000);
            _syncTimer.Elapsed += OnSyncTimerElapsed;
            _syncTimer.AutoReset = true;

            if (_sessionState == TorchSessionState.Loaded)
                _syncTimer.Start();
        }

        private void RestartDiscordRuntimeServices(bool restartDiscordHost)
        {
            StopMonitoringService();
            DisposePresenceService();

            if (_discordWrapper != null)
            {
                if (restartDiscordHost)
                {
                    try
                    {
                        _discordWrapper.StopAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogException(
                            "[DISCORD_IPC] Failed to stop Discord host during config apply.",
                            ex);
                    }

                    try
                    {
                        _discordWrapper.StartAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogException(
                            "[DISCORD_IPC] Failed to start Discord host during config apply.",
                            ex);
                    }
                }
                else
                {
                    try
                    {
                        _discordWrapper.UpdateConfigurationAsync(CreateDiscordRuntimeConfig())
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogException(
                            "[DISCORD_IPC] Failed to push config reload.",
                            ex);
                    }
                }
            }

            EnsurePresenceService();
            StartLiveSessionServices();
        }

        private void StopMonitoringService()
        {
            if (_monitoringService == null)
                return;

            try
            {
                _monitoringService.Stop();
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[MONITORING] Failed to stop MonitoringService: " + ex.Message);
            }

            try
            {
                _monitoringService.Dispose();
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[MONITORING] Failed to dispose MonitoringService: " + ex.Message);
            }

            _monitoringService = null;
        }

        private void DisposePresenceService()
        {
            if (_discordPresenceService == null)
                return;

            try
            {
                _discordPresenceService.Dispose();
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(
                    "[PRESENCE] Failed to dispose DiscordPresenceService: " + ex.Message);
            }

            _discordPresenceService = null;
        }

        private void EnsurePresenceService()
        {
            if (_discordPresenceService == null && _discordWrapper != null)
                _discordPresenceService = new DiscordPresenceService(_config, _discordWrapper);
        }

        private void StartLiveSessionServices()
        {
            if (_sessionState != TorchSessionState.Loaded)
                return;

            try
            {
                if (_monitoringService == null && _discordBot != null)
                {
                    _monitoringService = new MonitoringService(_config, _discordWrapper);
                    _monitoringService.Initialize();
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(
                    "[MONITORING] Failed to restart MonitoringService: " + ex.Message);
            }

            try
            {
                _discordPresenceService?.Start();
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(
                    "[PRESENCE] Failed to restart DiscordPresenceService: " + ex.Message);
            }
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
            _sessionState = state;

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
                            _monitoringService = new MonitoringService(_config, _discordWrapper);
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

                    // 6. Start Discord presence updates for live server stats
                    try
                    {
                        _discordPresenceService?.Start();
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogError(
                            "[PRESENCE] Failed to start DiscordPresenceService: " +
                            ex.Message);
                    }

                    // 7. Send server-started status to Discord (delayed for stable SimSpeed)
                    if (_config?.Monitoring?.Enabled == true && _eventLog != null)
                    {
                        Task.Run(async () =>
                        {
                            await Task.Delay(3000).ConfigureAwait(false);
                            var stableSimSpeed = PluginUtils.GetCurrentSimSpeed();
                            await _eventLog.LogServerStatusAsync("STARTED", stableSimSpeed).ConfigureAwait(false);
                        });
                    }

                    // 8. Run startup routines (faction sync, timer start, etc.)
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
                    if (_discordPresenceService != null)
                    {
                        _discordPresenceService.Stop();
                        LoggerUtil.LogInfo("[PRESENCE] DiscordPresenceService stopped");
                    }
                    break;

                case TorchSessionState.Unloaded:
                    LoggerUtil.LogWarning("=== Server session UNLOADED ===");
                    _playerTrackingInitialized = false;

                    if (_config?.Monitoring?.Enabled == true && _eventLog != null)
                    {
                        Task.Run(async () =>
                        {
                            await _eventLog.LogServerStatusAsync("STOPPED", 0).ConfigureAwait(false);
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
                        await Task.Delay(5000).ConfigureAwait(false);
                        var stableSimSpeed = PluginUtils.GetCurrentSimSpeed();

                        if (_orchestrator != null)
                        {
                            await _orchestrator.CheckServerStatusAsync(stableSimSpeed).ConfigureAwait(false);
                            LoggerUtil.LogSuccess($"[STARTUP] Post-load status reported. SimSpeed: {stableSimSpeed:F2}");
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
                    LoggerUtil.LogInfo($"[STARTUP] Found {factions.Count} player factions");
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
                    LoggerUtil.LogSuccess(
                        $"[STARTUP] Sync timer started (interval: {_config.Discord.SyncIntervalSeconds}s)");
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
            if (_discordWrapper == null)
                return Task.FromResult(0);

            return _discordWrapper.StartAsync().ContinueWith(t =>
            {
                if (t.Result)
                    LoggerUtil.LogInfo(
                        "Discord host launched and IPC initialized; waiting for Ready state"
                    );
            });
        }

        private DiscordRuntimeConfig CreateDiscordRuntimeConfig()
        {
            return new DiscordRuntimeConfig
            {
                BotToken = _config?.Discord?.BotToken,
                GuildId = _config?.Discord?.GuildID ?? 0,
                AdminBotChannelId = _config?.Discord?.AdminBotChannelId ?? 0,
            };
        }
    }
}
