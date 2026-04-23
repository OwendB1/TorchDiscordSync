using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Sandbox.Game.World;
using TorchDiscordSync.Plugin.Config;
using TorchDiscordSync.Plugin.Utils;

namespace TorchDiscordSync.Plugin.Services
{
    public sealed class DiscordPresenceService : IDisposable
    {
        private const int DefaultIntervalSeconds = 1;
        private const int DefaultMaxPlayers = 20;
        private const string OfflinePresenceText = "Server offline";

        private readonly MainConfig _config;
        private readonly DiscordService _discord;
        private readonly System.Timers.Timer _presenceTimer;

        private int _updateInProgress;
        private bool _isDisposed;
        private bool _lastReadyState;
        private string _lastPresenceText;
        private DateTime _lastFailureLogTime = DateTime.MinValue;

        public DiscordPresenceService(MainConfig config, DiscordService discord)
        {
            _config = config;
            _discord = discord;

            int intervalSeconds = GetIntervalSeconds();
            _presenceTimer = new System.Timers.Timer(intervalSeconds * 1000.0);
            _presenceTimer.AutoReset = true;
            _presenceTimer.Elapsed += OnPresenceTimerElapsed;
        }

        public void Start()
        {
            if (_isDisposed || _presenceTimer.Enabled)
                return;

            _presenceTimer.Start();
            LoggerUtil.LogInfo(
                $"[PRESENCE] Discord presence updates started (interval: {GetIntervalSeconds()}s)"
            );

            QueuePresenceUpdate();
        }

        public void Stop(bool updateOfflinePresence = true)
        {
            if (_presenceTimer.Enabled)
                _presenceTimer.Stop();

            _lastReadyState = false;

            if (!updateOfflinePresence || _isDisposed)
                return;

            try
            {
                UpdatePresenceAsync(OfflinePresenceText, true).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                LoggerUtil.LogDebug("[PRESENCE] Offline presence update failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            Stop();
            _presenceTimer.Dispose();
            _isDisposed = true;
        }

        private void OnPresenceTimerElapsed(object sender, ElapsedEventArgs e)
        {
            QueuePresenceUpdate();
        }

        private void QueuePresenceUpdate()
        {
            if (_isDisposed || Interlocked.Exchange(ref _updateInProgress, 1) == 1)
                return;

            Task.Run(async delegate
            {
                try
                {
                    await UpdatePresenceAsync(BuildPresenceText(), false).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref _updateInProgress, 0);
                }
            });
        }

        private async Task UpdatePresenceAsync(string presenceText, bool forceUpdate)
        {
            bool isReady = _discord != null && _discord.IsReady;
            if (!isReady)
            {
                _lastReadyState = false;
                return;
            }

            bool shouldForceUpdate = forceUpdate || !_lastReadyState;
            _lastReadyState = true;

            if (
                !shouldForceUpdate
                && string.Equals(presenceText, _lastPresenceText, StringComparison.Ordinal)
            )
            {
                return;
            }

            bool updated = await _discord.UpdatePresenceAsync(presenceText).ConfigureAwait(false);
            if (updated)
            {
                _lastPresenceText = presenceText;
                _lastFailureLogTime = DateTime.MinValue;
                return;
            }

            if ((DateTime.UtcNow - _lastFailureLogTime).TotalSeconds < 30)
                return;

            _lastFailureLogTime = DateTime.UtcNow;
            LoggerUtil.LogWarning("[PRESENCE] Failed to update Discord presence");
        }

        private string BuildPresenceText()
        {
            float simSpeed = PluginUtils.GetCurrentSimSpeed();
            int playerCount = GetOnlinePlayerCount();
            int maxPlayers = GetMaxPlayerCount();

            return string.Format(
                CultureInfo.InvariantCulture,
                "SimSpeed {0:0.00} | {1}/{2} players",
                simSpeed,
                playerCount,
                maxPlayers
            );
        }

        private int GetIntervalSeconds()
        {
            int intervalSeconds = _config?.Discord?.PresenceUpdateIntervalSeconds ?? DefaultIntervalSeconds;
            return intervalSeconds > 0 ? intervalSeconds : DefaultIntervalSeconds;
        }

        private static int GetOnlinePlayerCount()
        {
            try
            {
                return MySession.Static?.Players?.GetOnlinePlayerCount() ?? 0;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[PRESENCE] Error getting online player count: " + ex.Message);
                return 0;
            }
        }

        private static int GetMaxPlayerCount()
        {
            try
            {
                return MySession.Static?.Settings?.MaxPlayers ?? DefaultMaxPlayers;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[PRESENCE] Error getting max player count: " + ex.Message);
                return DefaultMaxPlayers;
            }
        }
    }
}
