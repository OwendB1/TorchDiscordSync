using System;
using System.IO;

namespace TorchDiscordSync.DiscordHost.Logging
{
    internal static class HostLogger
    {
        private static readonly object SyncRoot = new object();
        private static string _logFilePath;

        public static void Initialize(string pluginDirectory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pluginDirectory))
                    return;

                var logDirectory = Path.Combine(pluginDirectory, "Logging");
                Directory.CreateDirectory(logDirectory);
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                _logFilePath = Path.Combine(
                    logDirectory,
                    $"{timestamp}_TDS_discord_host.log");
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
            var line = $"[DiscordHost] [{timestamp}] [{level}] {message}";

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
    }
}
