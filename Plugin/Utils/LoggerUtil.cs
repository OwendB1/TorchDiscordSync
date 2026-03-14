// Plugin/Utils/LoggerUtil.cs
/// Utility class for logging messages to console and file
using System;
using System.IO;
using mamba.TorchDiscordSync.Plugin.Config;

namespace mamba.TorchDiscordSync.Plugin.Utils
{
    /// <summary>
    /// Utility class for logging messages to console and file
    /// </summary>
    public static class LoggerUtil
    {
        private const string PREFIX = "[mamba.TorchDiscordSync.Plugin]";
        private static readonly object _lock = new object();
        private static bool _debugMode = false;
        private static string _currentLogFile = null;

        static LoggerUtil()
        {
            // Check if debug mode is enabled
            try
            {
                string dataDir = MainConfig.GetDataDirectory();
                string configPath = Path.Combine(dataDir, "MainConfig.xml");
                if (File.Exists(configPath))
                {
                    string configContent = File.ReadAllText(configPath);
                    _debugMode = configContent.Contains("<Debug>true</Debug>");
                }
            }
            catch
            {
                // Ignore errors in debug mode detection
                LogWarning("Failed to determine debug mode from config. Defaulting to false.");
            }
        }

        /// <summary>
        /// Get the full path to the log file with timestamp
        /// </summary>
        private static string GetLogFilePath()
        {
            try
            {
                string logDir = MainConfig.GetLogDirectory();
                string logSubDir = Path.Combine(logDir, "archive");
                if (!Directory.Exists(logSubDir))
                {
                    Directory.CreateDirectory(logSubDir);
                }
                // Create new log file with timestamp if not already created
                if (string.IsNullOrEmpty(_currentLogFile))
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                    _currentLogFile = Path.Combine(logSubDir, $"{timestamp}_TDS_plugin.log");
                }
                return _currentLogFile;
            }
            catch
            {
                // Fallback to temp directory
                string tempLogDir = Path.Combine(Path.GetTempPath(), "mambaSaveData", "log");
                if (!Directory.Exists(tempLogDir))
                {
                    Directory.CreateDirectory(tempLogDir);
                }
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                return Path.Combine(tempLogDir, $"{timestamp}_TDS_plugin.log");
            }
        }

        /// <summary>
        /// Log a message with specified category
        /// </summary>
        public static void Log(string category, string message)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string consoleMessage = $"{PREFIX} [{timestamp}] [{category}] {message}";
            
            // Write to console
            Console.WriteLine(consoleMessage);
            
            // Write to file (thread-safe)
            try
            {
                string fileMessage = $"[{timestamp}] [{category}] {message}";
                lock (_lock)
                {
                    string logFilePath = GetLogFilePath();
                    File.AppendAllText(logFilePath, fileMessage + Environment.NewLine);
                }
            }
            catch
            {
                // Ignore file logging errors to prevent crashes
            }
        }

        /// <summary>
        /// Log an informational message
        /// </summary>
        public static void LogInfo(string message)
        {
            Log("INFO", message);
        }

        /// <summary>
        /// Log a warning message
        /// </summary>
        public static void LogWarning(string message)
        {
            Log("WARN", message);
        }

        /// <summary>
        /// Log an error message
        /// </summary>
        public static void LogError(string message)
        {
            Log("ERROR", message);
        }

        /// <summary>
        /// Log a debug message (only when debug mode is enabled)
        /// </summary>
        public static void LogDebug(string message, bool debugMode = false)
        {
            // Use global debug mode setting or passed parameter
            if (_debugMode || debugMode)
                Log("DEBUG", message);
        }

        /// <summary>
        /// Log a success message
        /// </summary>
        public static void LogSuccess(string message)
        {
            Log("SUCCESS", message);
        }
    }
}
