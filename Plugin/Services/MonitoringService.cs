// Plugin/Services/MonitoringService.cs
using System;
using System.Threading.Tasks;
using System.Timers;
using Discord;
using Discord.WebSocket;
using mamba.TorchDiscordSync.Plugin.Config;
using mamba.TorchDiscordSync.Plugin.Services;
using mamba.TorchDiscordSync.Plugin.Utils;
using Sandbox.Game.World;
using VRage.Game.ModAPI;

namespace mamba.TorchDiscordSync.Plugin.Services
{
    public class MonitoringService : IDisposable
    {
        private readonly MainConfig _config;
        private readonly DiscordBotService _discordBot;
        private Timer _monitoringTimer;
        private bool _isDisposed = false;

        // Last known values to avoid unnecessary Discord API calls
        private float _lastSimSpeed = -1f;
        private int _lastPlayerCount = -1;

        // NOVO: Cooldown za SimSpeed alert - ne spam-uje više
        private DateTime _lastSimSpeedAlertTime = DateTime.MinValue;

        // NEW: Do not send SimSpeed alerts on very first check (server still starting)
        private bool _simSpeedAlertsReady = false;

        public MonitoringService(MainConfig config, DiscordBotService discordBot)
        {
            _config = config;
            _discordBot = discordBot;

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

                int intervalSeconds = _config.Monitoring.StatusUpdateIntervalSeconds;
                if (intervalSeconds <= 0)
                {
                    LoggerUtil.LogWarning(
                        "[MONITORING] Invalid monitoring interval, using default 30s"
                    );
                    intervalSeconds = 30;
                }

                int intervalMs = intervalSeconds * 1000;

                _monitoringTimer = new Timer(intervalMs);
                _monitoringTimer.Elapsed += OnMonitoringTimerElapsed;
                _monitoringTimer.AutoReset = true;
                _monitoringTimer.Start();

                LoggerUtil.LogSuccess(
                    $"[MONITORING] Monitoring service started (interval: {intervalSeconds}s)"
                );

                // Do initial update immediately
                Task.Run(async () => await UpdateChannelNamesAsync());
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
                Task.Run(async () => await UpdateChannelNamesAsync());
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[MONITORING] Timer callback error: {ex.Message}");
            }
        }

        private async Task UpdateChannelNamesAsync()
        {
            try
            {
                LoggerUtil.LogDebug("[MONITORING_UPDATE] Starting channel name update...");

                float currentSimSpeed = PluginUtils.GetCurrentSimSpeed();
                LoggerUtil.LogDebug($"[MONITORING_UPDATE] Current SimSpeed: {currentSimSpeed:F2}");

                int currentPlayerCount = GetOnlinePlayerCount();
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
                        await UpdateSimSpeedChannelAsync(currentSimSpeed);
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
                    await UpdatePlayerCountChannelAsync(currentPlayerCount);
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

                ulong channelId = _config.Discord.SimSpeedChannelId;
                if (channelId == 0)
                {
                    LoggerUtil.LogWarning("[MONITORING_SIMSPEED] SimSpeedChannelId not configured");
                    return;
                }

                if (_discordBot == null || _discordBot.GetClient() == null)
                {
                    LoggerUtil.LogError("[MONITORING_SIMSPEED] Discord bot not ready");
                    return;
                }

                var client = _discordBot.GetClient();
                var channel = client.GetChannel(channelId) as SocketVoiceChannel;

                if (channel == null)
                {
                    LoggerUtil.LogError(
                        "[MONITORING_SIMSPEED] Channel "
                            + channelId
                            + " not found or not a voice channel"
                    );
                    return;
                }

                string emoji =
                    simSpeed >= _config.Monitoring.SimSpeedThreshold
                        ? _config.Monitoring.SimSpeedNormalEmoji
                        : _config.Monitoring.SimSpeedWarningEmoji;

                string newName = _config
                    .Monitoring.SimSpeedChannelNameFormat.Replace("{emoji}", emoji)
                    .Replace("{ss}", simSpeed.ToString("F2"));

                LoggerUtil.LogDebug("[MONITORING_SIMSPEED] Setting channel name to: " + newName);

                await channel.ModifyAsync(props =>
                {
                    props.Name = newName;
                });

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
                    TimeSpan timeSinceLastAlert = DateTime.UtcNow - _lastSimSpeedAlertTime;
                    int cooldownSeconds = _config.Monitoring.SimSpeedAlertCooldownSeconds;

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
                        );

                        _lastSimSpeedAlertTime = DateTime.UtcNow; // Update timestamp
                        LoggerUtil.LogInfo("[MONITORING] SimSpeed alert sent (cooldown reset)");
                    }
                    else
                    {
                        // Cooldown nije prošao - skip alert
                        double remainingSeconds = cooldownSeconds - timeSinceLastAlert.TotalSeconds;
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

        private async Task UpdatePlayerCountChannelAsync(int playerCount)
        {
            try
            {
                LoggerUtil.LogDebug(
                    "[MONITORING_PLAYERS] Updating player count channel to " + playerCount
                );

                ulong channelId = _config.Discord.PlayerCountChannelId;
                if (channelId == 0)
                {
                    LoggerUtil.LogWarning(
                        "[MONITORING_PLAYERS] PlayerCountChannelId not configured"
                    );
                    return;
                }

                if (_discordBot == null || _discordBot.GetClient() == null)
                {
                    LoggerUtil.LogError("[MONITORING_PLAYERS] Discord bot not ready");
                    return;
                }

                var client = _discordBot.GetClient();
                var channel = client.GetChannel(channelId) as SocketVoiceChannel;

                if (channel == null)
                {
                    LoggerUtil.LogError("[MONITORING_PLAYERS] Channel " + channelId + " not found");
                    return;
                }

                int maxPlayers = GetMaxPlayerCount();

                string newName = _config
                    .Monitoring.PlayerCountChannelNameFormat.Replace("{p}", playerCount.ToString())
                    .Replace("{pp}", maxPlayers.ToString());

                LoggerUtil.LogDebug("[MONITORING_PLAYERS] Setting channel name to: " + newName);

                await channel.ModifyAsync(props =>
                {
                    props.Name = newName;
                });

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

                ulong channelId = _config.Discord.AdminAlertChannelId;
                if (channelId == 0)
                {
                    channelId = _config.Discord.StaffLog;
                }

                if (channelId == 0)
                {
                    LoggerUtil.LogWarning("[MONITORING] Admin alert channel not configured");
                    return;
                }

                var client = _discordBot.GetClient();
                if (client == null)
                {
                    LoggerUtil.LogWarning("[MONITORING] Discord client not ready for alert");
                    return;
                }

                var channel = client.GetChannel(channelId) as IMessageChannel;
                if (channel == null)
                {
                    LoggerUtil.LogWarning(
                        "[MONITORING] Admin alert channel not found: " + channelId
                    );
                    return;
                }

                await channel.SendMessageAsync(message);
                LoggerUtil.LogSuccess("[MONITORING] Admin alert sent");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[MONITORING] Send admin alert error: " + ex.Message);
            }
        }

        private int GetOnlinePlayerCount()
        {
            try
            {
                LoggerUtil.LogDebug("[MONITORING_COUNT] Getting online player count...");

                if (MySession.Static == null || MySession.Static.Players == null)
                {
                    LoggerUtil.LogWarning("[MONITORING_COUNT] Session or Players is null");
                    return 0;
                }

                int count = MySession.Static.Players.GetOnlinePlayerCount();
                LoggerUtil.LogDebug($"[MONITORING_COUNT] Found {count} online players");
                return count;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[MONITORING_COUNT] Error getting player count: {ex.Message}");
                return 0;
            }
        }

        private int GetMaxPlayerCount()
        {
            try
            {
                int maxPlayers = 20;

                if (MySession.Static != null && MySession.Static.Settings != null)
                {
                    maxPlayers = MySession.Static.Settings.MaxPlayers;
                    LoggerUtil.LogDebug(
                        $"[MONITORING_MAX] Max players from settings: {maxPlayers}"
                    );
                }

                return maxPlayers;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(
                    $"[MONITORING_MAX] Error getting max player count: {ex.Message}"
                );
                return 20;
            }
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
