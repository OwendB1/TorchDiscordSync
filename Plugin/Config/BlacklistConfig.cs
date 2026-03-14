// Plugin/Config/BlacklistConfig.cs
using System;
using System.IO;
using System.Xml.Serialization;
using mamba.TorchDiscordSync.Plugin.Utils;

namespace mamba.TorchDiscordSync.Plugin.Config
{
    /// <summary>
    /// Blacklist configuration - separated for easier user management
    /// Words are comma-separated for simplicity
    /// </summary>
    [XmlRoot("BlacklistConfig")]
    public class BlacklistConfig
    {
        /// <summary>
        /// Get correct config path using centralized MainConfig
        /// </summary>
        private static string ConfigPath
        {
            get { return Path.Combine(MainConfig.GetConfigDirectory(), "BlacklistConfig.xml"); }
        }

        /// <summary>
        /// Comma-separated list of blacklisted words
        /// Example: "hack, cheat, exploit, http, https"
        /// </summary>
        [XmlElement]
        public string Words { get; set; }

        /// <summary>
        /// Enable case-sensitive matching
        /// Default: false (case-insensitive)
        /// </summary>
        [XmlElement]
        public bool CaseSensitive { get; set; }

        /// <summary>
        /// Enable partial word matching
        /// Default: true (matches "hack" in "hacking")
        /// </summary>
        [XmlElement]
        public bool PartialMatch { get; set; }

        /// <summary>
        /// Default constructor with sample words
        /// </summary>
        public BlacklistConfig()
        {
            Words = "hack, cheat, exploit, http, https, discord.gg, bit.ly";
            CaseSensitive = false;
            PartialMatch = true;
        }

        /// <summary>
        /// Get blacklist as string array
        /// Splits comma-separated words and trims whitespace
        /// </summary>
        public string[] GetWordsArray()
        {
            if (string.IsNullOrWhiteSpace(Words))
                return new string[0];

            // Split by comma and trim whitespace
            var parts = Words.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] = parts[i].Trim();
            }

            return parts;
        }

        /// <summary>
        /// Load blacklist configuration from XML file
        /// Creates default file if it doesn't exist
        /// </summary>
        public static BlacklistConfig Load()
        {
            try
            {
                string path = ConfigPath;
                LoggerUtil.LogDebug($"[BLACKLIST_CONFIG] Loading from: {path}");

                if (!File.Exists(path))
                {
                    LoggerUtil.LogInfo("[BLACKLIST_CONFIG] File not found, creating default...");
                    BlacklistConfig defaultConfig = new BlacklistConfig();
                    defaultConfig.Save();
                    return defaultConfig;
                }

                XmlSerializer serializer = new XmlSerializer(typeof(BlacklistConfig));
                using (FileStream fs = new FileStream(path, FileMode.Open))
                {
                    BlacklistConfig config = (BlacklistConfig)serializer.Deserialize(fs);

                    // Log loaded words
                    var words = config.GetWordsArray();
                    LoggerUtil.LogSuccess(
                        $"[BLACKLIST_CONFIG] Loaded {words.Length} blacklisted words"
                    );
                    LoggerUtil.LogDebug(
                        $"[BLACKLIST_CONFIG] Words: {string.Join(", ", words)}"
                    );

                    return config;
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[BLACKLIST_CONFIG] Load error: " + ex.Message);
                LoggerUtil.LogWarning("[BLACKLIST_CONFIG] Using default configuration");
                return new BlacklistConfig();
            }
        }

        /// <summary>
        /// Save blacklist configuration to XML file
        /// </summary>
        public void Save()
        {
            try
            {
                string path = ConfigPath;
                string dir = Path.GetDirectoryName(path);

                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                XmlSerializer serializer = new XmlSerializer(typeof(BlacklistConfig));
                using (FileStream fs = new FileStream(path, FileMode.Create))
                {
                    serializer.Serialize(fs, this);
                }

                LoggerUtil.LogSuccess($"[BLACKLIST_CONFIG] Saved to: {path}");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[BLACKLIST_CONFIG] Save error: " + ex.Message);
            }
        }
    }
}
