using System;
using System.IO;
using System.Linq;

namespace TorchDiscordSync.DiscordHost.Logging
{
    internal static class HostLogger
    {
        private const int MaxRetainedLogFiles = 3;
        private static readonly object SyncRoot = new object();
        private static string _logFilePath;

        public static void Initialize(string pluginDirectory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pluginDirectory))
                    return;

                var logDirectory = Path.Combine(pluginDirectory, "Logging");
                _logFilePath = EnsureLogFilePath(logDirectory);
            }
            catch
            {
                _logFilePath = null;
            }
        }

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Warn(string message)
        {
            Write("WARN", message);
        }

        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        public static void Debug(string message)
        {
            Write("DEBUG", message);
        }

        private static void Write(string level, string message)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var line = $"[DISCORD_HOST] [{timestamp}] [{level}] {message}";

            try
            {
                Console.WriteLine(line);
            }
            catch
            {
            }

            if (string.IsNullOrEmpty(_logFilePath))
                return;

            try
            {
                lock (SyncRoot)
                {
                    File.AppendAllText(_logFilePath, line + Environment.NewLine);
                }
            }
            catch
            {
            }
        }

        private static string EnsureLogFilePath(string logDirectory)
        {
            Directory.CreateDirectory(logDirectory);

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            var currentLogFile = Path.Combine(logDirectory, $"{timestamp}_TDS_discord_host.log");
            CleanupOldLogs(logDirectory, currentLogFile);
            return currentLogFile;
        }

        private static void CleanupOldLogs(string logDirectory, string currentLogFile)
        {
            try
            {
                var retainedExistingLogs = Math.Max(0, MaxRetainedLogFiles - 1);
                var staleLogs = new DirectoryInfo(logDirectory)
                    .GetFiles("*_TDS_discord_host.log")
                    .OrderByDescending(file => file.CreationTimeUtc)
                    .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                    .Skip(retainedExistingLogs)
                    .Where(file => !string.Equals(file.FullName, currentLogFile, StringComparison.OrdinalIgnoreCase));

                foreach (var staleLog in staleLogs)
                    staleLog.Delete();
            }
            catch
            {
            }
        }
    }
}
