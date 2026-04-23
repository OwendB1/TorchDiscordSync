// Plugin/Utils/PluginUtils.cs
using System;
using System.Timers;
using TorchDiscordSync.Plugin.Config;
using TorchDiscordSync.Plugin.Utils;
using Sandbox;
using Sandbox.Game;

namespace TorchDiscordSync.Plugin.Utils
{
    /// <summary>
    /// Helper methods for TorchDiscordSyncPlugin to reduce duplication.
    /// Contains config checks, SimSpeed retrieval, and timer management.
    /// </summary>
    public static class PluginUtils
    {
        /// <summary>
        /// Checks if faction synchronization is enabled in config.
        /// </summary>
        public static bool IsFactionSyncEnabled(MainConfig config)
        {
            return config != null && config.Faction != null && config.Faction.Enabled;
        }

        /// <summary>
        /// Checks if server monitoring is enabled in config.
        /// </summary>
        public static bool IsMonitoringEnabled(MainConfig config)
        {
            return config != null && config.Monitoring != null && config.Monitoring.Enabled;
        }

        /// <summary>
        /// Checks if chat server-to-discord sync is enabled in config.
        /// </summary>
        public static bool IsChatServerToDiscordEnabled(MainConfig config)
        {
            return config != null && config.Chat != null && config.Chat.ServerToDiscord;
        }

        /// <summary>
        /// Retrieves the actual server simulation speed.
        /// Uses Sync.ServerSimulationRatio as confirmed by DLL inspection for best accuracy.
        /// </summary>
        /// <returns>Float representing SimSpeed (0.0 to 1.0)</returns>
        public static float GetCurrentSimSpeed()
        {
            try
            {
                // Based on DLL report: [NS: Sandbox.Game.Multiplayer] -> Class: Sync -> [P] [ST] float ServerSimulationRatio
                float simSpeed = Sandbox.Game.Multiplayer.Sync.ServerSimulationRatio;

                // Check for invalid values during server startup or physics freezes
                if (float.IsNaN(simSpeed) || float.IsInfinity(simSpeed))
                {
                    // Returning 0.0 is more honest than 1.0 during startup/crashes
                    return 0.0f;
                }

                return simSpeed;
            }
            catch (Exception ex)
            {
                // Fail-safe for cases where the Sync class is not yet initialized in memory
                LoggerUtil.LogError("Error getting SimSpeed: " + ex.Message);
                return 0.0f;
            }
        }

        /// <summary>
        /// Creates and configures the sync timer only if faction sync is enabled and interval is valid.
        /// Returns the timer instance or null if not started.
        /// </summary>
        public static Timer CreateSyncTimerIfEnabled(
            MainConfig config,
            ElapsedEventHandler elapsedHandler,
            Action onPeriodicCleanup
        )
        {
            if (!IsFactionSyncEnabled(config))
            {
                LoggerUtil.LogInfo(
                    "Faction sync timer NOT created - faction sync is disabled in config"
                );
                return null;
            }

            int intervalSeconds = config.Discord?.SyncIntervalSeconds ?? 30;
            if (intervalSeconds <= 0) intervalSeconds = 30;

            int intervalMs = intervalSeconds * 1000;

            Timer timer = new Timer(intervalMs);
            timer.Elapsed += elapsedHandler;
            timer.AutoReset = true;

            LoggerUtil.LogInfo(
                $"Faction sync timer created (interval: {intervalSeconds}s / {intervalMs}ms)"
            );

            return timer;
        }

        /// <summary>
        /// Starts the sync timer if it exists and faction sync is still enabled.
        /// </summary>
        public static void StartSyncTimerIfEnabled(Timer timer, MainConfig config)
        {
            if (timer != null && !timer.Enabled && IsFactionSyncEnabled(config))
            {
                timer.Start();
                LoggerUtil.LogSuccess("Faction sync timer started");
            }
        }

        /// <summary>
        /// Stops the sync timer safely if it exists.
        /// </summary>
        public static void StopSyncTimer(Timer timer)
        {
            if (timer != null && timer.Enabled)
            {
                timer.Stop();
                LoggerUtil.LogInfo("Faction sync timer stopped");
            }
        }

        /// <summary>
        /// Print plugin banner to console
        /// </summary>
        public static void PrintBanner(string title)
        {
            Console.WriteLine("");
            Console.WriteLine("-====================================================¬");
            Console.WriteLine(
                $"¦ {VersionUtil.GetPluginName()} {VersionUtil.GetVersionString()} - {title.PadRight(20)}¦"
            );
            Console.WriteLine("L====================================================-");
            Console.WriteLine("");
        }
    }
}
