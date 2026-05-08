// Plugin/Services/MonitoringService.cs
using System;
using System.Threading.Tasks;
using System.Timers;
using Sandbox.Game.World;
using TorchDiscordSync.Plugin.Config;
using TorchDiscordSync.Plugin.Utils;
using VRage.Game.ModAPI;

namespace TorchDiscordSync.Plugin.Services
{
    public class MonitoringService : IDisposable
    {
        private readonly MainConfig _config;
        private readonly DiscordService _discord;
        private readonly GameThreadInvoker _gameThread;
        private Timer _monitoringTimer;
        private bool _isDisposed = false;
        private int _updateInProgress;

        // Last known values to avoid unnecessary Discord API calls
        private float _lastSimSpeed = -1f;
        private int _lastPlayerCount = -1;

        // NOVO: Cooldown za SimSpeed alert - ne spam-uje više
        private DateTime _lastSimSpeedAlertTime = DateTime.MinValue;

        // NEW: Do not send SimSpeed alerts on very first check (server still starting)
        private bool _simSpeedAlertsReady = false;

        private sealed class MonitoringSnapshot
        {
            public float SimSpeed { get; set; }
            public int PlayerCount { get; set; }
            public int MaxPlayers { get; set; }
        }

        public MonitoringService(MainConfig config, DiscordService discord, GameThreadInvoker gameThread)
        {
            _config = config;
            _discord = discord;
            _gameThread = gameThread;

            LoggerUtil.LogDebug("[MONITORING] MonitoringService instance created");
        }

        public void Initialize()
        {
            try
            {
                if (_config?.Monitoring?.Enabled != true)
                {
                    LoggerUtil.LogInfo("[MONITORING] Monitoring disabled in config");
                    return;
                }

                var intervalSeconds = _config.Monitoring.StatusUpdateIntervalSeconds;
                if (intervalSeconds <= 0)
                {
                    LoggerUtil.LogWarning(
                        "[MONITORING] Invalid monitoring interval, using default 30s"
                    );
                    intervalSeconds = 30;
                }

                var intervalMs = intervalSeconds * 1000;

                _monitoringTimer = new Timer(intervalMs);
                _monitoringTimer.Elapsed += OnMonitoringTimerElapsed;
                _monitoringTimer.AutoReset = true;
                _monitoringTimer.Start();

                LoggerUtil.LogSuccess(
                    $"[MONITORING] Monitoring service started (interval: {intervalSeconds}s)"
                );

                // Do initial update immediately
                QueueChannelNameUpdate();
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[MONITORING] Initialization failed: {ex.Message}");
            }
        }

        private void OnMonitoringTimerElapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                LoggerUtil.LogDebug("[MONITORING] Timer elapsed - updating channel names");
                QueueChannelNameUpdate();
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[MONITORING] Timer callback error: {ex.Message}");
            }
        }

        private void QueueChannelNameUpdate()
        {
            if (_isDisposed || System.Threading.Interlocked.Exchange(ref _updateInProgress, 1) == 1)
            {
                LoggerUtil.LogDebug("[MONITORING] Previous channel-name update still running; skipping tick");
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    await UpdateChannelNamesAsync().ConfigureAwait(false);
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _updateInProgress, 0);
                }
            });
        }

        private async Task UpdateChannelNamesAsync()
        {
            try
            {
                LoggerUtil.LogDebug("[MONITORING_UPDATE] Starting channel name update...");

                var snapshot = await CaptureSnapshotAsync().ConfigureAwait(false);
                var currentSimSpeed = snapshot.SimSpeed;
                LoggerUtil.LogDebug($"[MONITORING_UPDATE] Current SimSpeed: {currentSimSpeed:F2}");

                var currentPlayerCount = snapshot.PlayerCount;
                LoggerUtil.LogDebug(
                    $"[MONITORING_UPDATE] Current player count: {currentPlayerCount}"
                );

                if (_config.Monitoring.EnableSimSpeedMonitoring)
                {
                    if (Math.Abs(currentSimSpeed - _lastSimSpeed) > 0.01f)
                    {
                        LoggerUtil.LogDebug(
                            $"[MONITORING_UPDATE] SimSpeed changed: {_lastSimSpeed:F2} → {currentSimSpeed:F2}"
                        );
                        await UpdateSimSpeedChannelAsync(currentSimSpeed).ConfigureAwait(false);
                        _lastSimSpeed = currentSimSpeed;
                    }
                    else
                    {
                        LoggerUtil.LogDebug(
                            "[MONITORING_UPDATE] SimSpeed unchanged, skipping update"
                        );
                    }
                }

                if (currentPlayerCount != _lastPlayerCount)
                {
                    LoggerUtil.LogDebug(
                        $"[MONITORING_UPDATE] Player count changed: {_lastPlayerCount} → {currentPlayerCount}"
                    );
                    await UpdatePlayerCountChannelAsync(currentPlayerCount, snapshot.MaxPlayers).ConfigureAwait(false);
                    _lastPlayerCount = currentPlayerCount;
                }
                else
                {
                    LoggerUtil.LogDebug(
                        "[MONITORING_UPDATE] Player count unchanged, skipping update"
                    );
                }

                LoggerUtil.LogDebug("[MONITORING_UPDATE] Channel name update complete");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[MONITORING_UPDATE] Error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private async Task UpdateSimSpeedChannelAsync(float simSpeed)
        {
            try
            {
                LoggerUtil.LogDebug(
                    "[MONITORING_SIMSPEED] Updating SimSpeed channel to " + simSpeed.ToString("F2")
                );

                var channelId = _config.Discord.SimSpeedChannelId;
                if (channelId == 0)
                {
                    LoggerUtil.LogWarning("[MONITORING_SIMSPEED] SimSpeedChannelId not configured");
                    return;
                }

                if (_discord == null || !_discord.IsReady)
                {
                    LoggerUtil.LogError("[MONITORING_SIMSPEED] Discord bot not ready");
                    return;
                }

                var emoji =
                    simSpeed >= _config.Monitoring.SimSpeedThreshold
                        ? _config.Monitoring.SimSpeedNormalEmoji
                        : _config.Monitoring.SimSpeedWarningEmoji;

                var newName = _config
                    .Monitoring.SimSpeedChannelNameFormat.Replace("{emoji}", emoji)
                    .Replace("{ss}", simSpeed.ToString("F2"));

                LoggerUtil.LogDebug("[MONITORING_SIMSPEED] Setting channel name to: " + newName);

                var updated = await _discord.UpdateChannelNameAsync(channelId, newName).ConfigureAwait(false);
                if (!updated)
                {
                    LoggerUtil.LogError(
                        "[MONITORING_SIMSPEED] Failed to update channel name for " + channelId
                    );
                    return;
                }

                LoggerUtil.LogSuccess("[MONITORING_SIMSPEED] Channel updated: " + newName);

                // ============================================================
                // NOVO: Send alert sa COOLDOWN check-om!
                // ============================================================
                if (
                    _simSpeedAlertsReady
                    && simSpeed < _config.Monitoring.SimSpeedThreshold
                    && _config.Monitoring.EnableSimSpeedAlerts
                )
                {
                    // Check cooldown - ne spam-uj
                    var timeSinceLastAlert = DateTime.UtcNow - _lastSimSpeedAlertTime;
                    var cooldownSeconds = _config.Monitoring.SimSpeedAlertCooldownSeconds;

                    if (timeSinceLastAlert.TotalSeconds >= cooldownSeconds)
                    {
                        // Cooldown je prošao - šalji alert!
                        await SendAdminAlertAsync(
                            _config
                                .Monitoring.SimSpeedAlertMessage.Replace(
                                    "{ss}",
                                    simSpeed.ToString("F2")
                                )
                                .Replace(
                                    "{threshold}",
                                    _config.Monitoring.SimSpeedThreshold.ToString("F2")
                                )
                        ).ConfigureAwait(false);

                        _lastSimSpeedAlertTime = DateTime.UtcNow; // Update timestamp
                        LoggerUtil.LogInfo("[MONITORING] SimSpeed alert sent (cooldown reset)");
                    }
                    else
                    {
                        // Cooldown nije prošao - skip alert
                        var remainingSeconds = cooldownSeconds - timeSinceLastAlert.TotalSeconds;
                        LoggerUtil.LogDebug(
                            $"[MONITORING] SimSpeed alert on cooldown ({remainingSeconds:F0}s remaining)"
                        );
                    }
                }

                // After first successful update, enable SimSpeed alerts for subsequent checks
                if (!_simSpeedAlertsReady)
                {
                    _simSpeedAlertsReady = true;
                    LoggerUtil.LogDebug(
                        "[MONITORING_SIMSPEED] Initial SimSpeed check completed, alerts now enabled for next interval"
                    );
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(
                    "[MONITORING_SIMSPEED] Failed to update channel: " + ex.Message
                );
            }
        }

        private async Task UpdatePlayerCountChannelAsync(int playerCount, int maxPlayers)
        {
            try
            {
                LoggerUtil.LogDebug(
                    "[MONITORING_PLAYERS] Updating player count channel to " + playerCount
                );

                var channelId = _config.Discord.PlayerCountChannelId;
                if (channelId == 0)
                {
                    LoggerUtil.LogWarning(
                        "[MONITORING_PLAYERS] PlayerCountChannelId not configured"
                    );
                    return;
                }

                if (_discord == null || !_discord.IsReady)
                {
                    LoggerUtil.LogError("[MONITORING_PLAYERS] Discord bot not ready");
                    return;
                }

                var newName = _config
                    .Monitoring.PlayerCountChannelNameFormat.Replace("{p}", playerCount.ToString())
                    .Replace("{pp}", maxPlayers.ToString());

                LoggerUtil.LogDebug("[MONITORING_PLAYERS] Setting channel name to: " + newName);

                var updated = await _discord.UpdateChannelNameAsync(channelId, newName).ConfigureAwait(false);
                if (!updated)
                {
                    LoggerUtil.LogError(
                        "[MONITORING_PLAYERS] Failed to update channel name for " + channelId
                    );
                    return;
                }

                LoggerUtil.LogSuccess("[MONITORING_PLAYERS] Channel updated: " + newName);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[MONITORING_PLAYERS] Failed to update channel: " + ex.Message);
            }
        }

        private async Task SendAdminAlertAsync(string message)
        {
            try
            {
                if (!_config.Monitoring.EnableAdminAlerts)
                {
                    LoggerUtil.LogDebug("[MONITORING] Admin alerts disabled");
                    return;
                }

                var channelId = _config.Discord.AdminAlertChannelId;
                if (channelId == 0)
                {
                    channelId = _config.Discord.StaffLog;
                }

                if (channelId == 0)
                {
                    LoggerUtil.LogWarning("[MONITORING] Admin alert channel not configured");
                    return;
                }

                if (_discord == null || !_discord.IsReady)
                {
                    LoggerUtil.LogWarning("[MONITORING] Discord client not ready for alert");
                    return;
                }

                var sent = await _discord.SendLogAsync(channelId, message).ConfigureAwait(false);
                if (!sent)
                {
                    LoggerUtil.LogWarning(
                        "[MONITORING] Failed to send admin alert to channel: " + channelId
                    );
                    return;
                }
                LoggerUtil.LogSuccess("[MONITORING] Admin alert sent");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[MONITORING] Send admin alert error: " + ex.Message);
            }
        }

        private Task<MonitoringSnapshot> CaptureSnapshotAsync()
        {
            if (_gameThread == null)
                return Task.FromResult(CaptureSnapshotCore());

            return _gameThread.RunAsync(CaptureSnapshotCore, nameof(MonitoringService));
        }

        private MonitoringSnapshot CaptureSnapshotCore()
        {
            var snapshot = new MonitoringSnapshot
            {
                SimSpeed = PluginUtils.GetCurrentSimSpeed(),
                PlayerCount = 0,
                MaxPlayers = 20,
            };

            try
            {
                LoggerUtil.LogDebug("[MONITORING_COUNT] Getting online player count...");
                snapshot.PlayerCount = MySession.Static?.Players?.GetOnlinePlayerCount() ?? 0;
                LoggerUtil.LogDebug($"[MONITORING_COUNT] Found {snapshot.PlayerCount} online players");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[MONITORING_COUNT] Error getting player count: {ex.Message}");
            }

            try
            {
                snapshot.MaxPlayers = MySession.Static?.Settings?.MaxPlayers ?? 20;
                LoggerUtil.LogDebug(
                    $"[MONITORING_MAX] Max players from settings: {snapshot.MaxPlayers}"
                );
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(
                    $"[MONITORING_MAX] Error getting max player count: {ex.Message}"
                );
            }

            return snapshot;
        }

        public void Stop()
        {
            try
            {
                if (_monitoringTimer != null)
                {
                    _monitoringTimer.Stop();
                    _monitoringTimer.Dispose();
                    _monitoringTimer = null;
                    LoggerUtil.LogInfo("[MONITORING] Monitoring service stopped");
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[MONITORING] Stop error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                Stop();
                _isDisposed = true;
                LoggerUtil.LogDebug("[MONITORING] MonitoringService disposed");
            }
        }
    }
}
