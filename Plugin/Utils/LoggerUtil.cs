// Plugin/Utils/LoggerUtil.cs
/// Utility class for logging messages to console and file
using System;
using System.IO;
using System.Linq;
using System.Text;
using TorchDiscordSync.Plugin.Config;

namespace TorchDiscordSync.Plugin.Utils
{
    /// <summary>
    /// Utility class for logging messages to console and file
    /// </summary>
    public static class LoggerUtil
    {
        private const string PREFIX = "[TorchDiscordSync.Plugin]";
        private const int MaxRetainedLogFiles = 3;
        private static readonly object _lock = new object();
        private static bool _debugMode = false;
        private static string _currentLogFile = null;

        static LoggerUtil()
        {
            try
            {
                var configPath = Path.Combine(
                    MainConfig.GetInstancePath(),
                    MainConfig.PLUGIN_DIR_NAME,
                    "configs",
                    "MainConfig.xml");

                if (File.Exists(configPath))
                {
                    var configContent = File.ReadAllText(configPath);
                    _debugMode = configContent.Contains("<Debug>true</Debug>");
                }
            }
            catch
            {
                _debugMode = false;
            }
        }

        public static void SetDebugMode(bool enabled)
        {
            _debugMode = enabled;
        }

        /// <summary>
        /// Get the full path to the log file with timestamp
        /// </summary>
        private static string GetLogFilePath()
        {
            try
            {
                var logDir = Path.Combine(
                    MainConfig.GetInstancePath(),
                    MainConfig.PLUGIN_DIR_NAME,
                    "Logging");
                return EnsureLogFilePath(logDir, "*_TDS_plugin.log", "_TDS_plugin.log");
            }
            catch
            {
                // Fallback to temp directory
                var tempLogDir = Path.Combine(Path.GetTempPath(), "TDSSaveData", "Logging");
                return EnsureLogFilePath(tempLogDir, "*_TDS_plugin.log", "_TDS_plugin.log");
            }
        }

        private static string EnsureLogFilePath(string logDir, string searchPattern, string suffix)
        {
            Directory.CreateDirectory(logDir);

            if (string.IsNullOrEmpty(_currentLogFile))
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                _currentLogFile = Path.Combine(logDir, $"{timestamp}{suffix}");
                CleanupOldLogs(logDir, searchPattern, _currentLogFile);
            }

            return _currentLogFile;
        }

        private static void CleanupOldLogs(string logDir, string searchPattern, string currentLogFile)
        {
            try
            {
                var retainedExistingLogs = Math.Max(0, MaxRetainedLogFiles - 1);
                var staleLogs = new DirectoryInfo(logDir)
                    .GetFiles(searchPattern)
                    .OrderByDescending(file => file.CreationTimeUtc)
                    .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                    .Skip(retainedExistingLogs)
                    .Where(file => !string.Equals(file.FullName, currentLogFile, StringComparison.OrdinalIgnoreCase));

                foreach (var staleLog in staleLogs)
                    staleLog.Delete();
            }
            catch
            {
                // Ignore cleanup errors to prevent crashes
            }
        }

        /// <summary>
        /// Log a message with specified category
        /// </summary>
        public static void Log(string category, string message)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var safeMessage = message ?? string.Empty;
            var consoleMessage = $"{PREFIX} [{timestamp}] [{category}] {safeMessage}";

            try
            {
                Console.WriteLine(consoleMessage);
            }
            catch
            {
                // ignored
            }

            try
            {
                var fileMessage = $"[{timestamp}] [{category}] {safeMessage}";
                lock (_lock)
                {
                    var logFilePath = GetLogFilePath();
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

        public static void LogError(string message, Exception ex)
        {
            Log("ERROR", AppendException(message, ex));
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

        public static void LogException(string context, Exception ex)
        {
            Log("ERROR", AppendException(context, ex));
        }

        private static string AppendException(string context, Exception ex)
        {
            if (ex == null)
                return context ?? string.Empty;

            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(context))
            {
                builder.Append(context);
                builder.Append(" | ");
            }

            var depth = 0;
            var current = ex;
            while (current != null)
            {
                if (depth > 0)
                    builder.Append(" --> ");

                builder.Append(current.GetType().FullName);
                builder.Append(": ");
                builder.Append(current.Message);

                if (!string.IsNullOrWhiteSpace(current.StackTrace))
                {
                    builder.Append(Environment.NewLine);
                    builder.Append(current.StackTrace);
                }

                current = current.InnerException;
                depth++;
            }

            return builder.ToString();
        }
    }
}
