// Plugin/Utils/PluginUtils.cs
using System;

namespace TorchDiscordSync.Plugin.Utils
{
    /// <summary>
    /// Helper methods for TorchDiscordSyncPlugin to reduce duplication.
    /// Contains config checks, SimSpeed retrieval, and timer management.
    /// </summary>
    public static class PluginUtils
    {
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
                var simSpeed = Sandbox.Game.Multiplayer.Sync.ServerSimulationRatio;

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
