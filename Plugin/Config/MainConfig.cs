// Plugin/Config/MainConfig.cs
using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using TorchDiscordSync.Plugin.Utils;

namespace TorchDiscordSync.Plugin.Config
{
    [XmlRoot("MainConfig")]
    public class MainConfig
    {
        // Static field for instance-specific config directory name
        // ============================================================
        // CENTRAL PATH MANAGEMENT - Single Point of Control
        // ============================================================

        /// <summary>
        /// Plugin directory name - used for all plugin files and configs
        /// Change this one constant to change plugin directory name everywhere!
        /// </summary>
        public static readonly string PLUGIN_DIR_NAME = "TDSSaveData";

        /// <summary>
        /// Get the base instance directory (where Torch stores data)
        /// Tries environment variable first, falls back to default
        /// </summary>
        public static string GetInstancePath()
        {
            // Try to get from environment variable set by Torch
            string instancePath = Environment.GetEnvironmentVariable("TORCH_INSTANCE_PATH");

            // Fallback to default location if not set
            if (string.IsNullOrEmpty(instancePath))
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                instancePath = Path.Combine(baseDir, "Instance");
            }

            return instancePath;
        }

        /// <summary>
        /// Get the plugin directory (for configs, data, logs)
        /// Example: C:\Path\To\Torch\Instance\TDSSaveData
        /// </summary>
        public static string GetPluginDirectory()
        {
            string pluginDir = Path.Combine(GetInstancePath(), PLUGIN_DIR_NAME);

            // Ensure directory exists
            if (!Directory.Exists(pluginDir))
            {
                try
                {
                    Directory.CreateDirectory(pluginDir);
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogException(
                        $"Failed to create plugin directory {pluginDir}.",
                        ex);
                }
            }

            return pluginDir;
        }

        /// <summary>
        /// Get the config directory (for XML configs)
        /// Returns: [PluginDirectory]/configs
        /// </summary>
        public static string GetConfigDirectory()
        {
            string configDir = Path.Combine(GetPluginDirectory(), "configs");

            if (!Directory.Exists(configDir))
            {
                try
                {
                    Directory.CreateDirectory(configDir);
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogException(
                        $"Failed to create config directory {configDir}.",
                        ex);
                }
            }

            return configDir;
        }

        /// <summary>
        /// Get the data directory (for database files, sync data)
        /// Returns: [PluginDirectory]/data
        /// </summary>
        public static string GetDataDirectory()
        {
            string dataDir = Path.Combine(GetPluginDirectory(), "data");

            if (!Directory.Exists(dataDir))
            {
                try
                {
                    Directory.CreateDirectory(dataDir);
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogException(
                        $"Failed to create data directory {dataDir}.",
                        ex);
                }
            }

            return dataDir;
        }

        /// <summary>
        /// Get the log directory (for plugin logs)
        /// Returns: [PluginDirectory]/Logging
        /// </summary>
        public static string GetLogDirectory()
        {
            string logDir = Path.Combine(GetPluginDirectory(), "Logging");

            if (!Directory.Exists(logDir))
            {
                try
                {
                    Directory.CreateDirectory(logDir);
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogException(
                        $"Failed to create log directory {logDir}.",
                        ex);
                }
            }

            return logDir;
        }

        /// <summary>
        /// Get correct config path (for backward compatibility)
        /// </summary>
        private static string ConfigPath
        {
            get { return Path.Combine(GetConfigDirectory(), "MainConfig.xml"); }
        }

        // ========== CORE SETTINGS ==========
        [XmlElement]
        public bool Enabled { get; set; }

        [XmlElement]
        public bool Debug { get; set; }

        [XmlArray("AdminSteamIDs")]
        [XmlArrayItem("SteamID")]
        public long[] AdminSteamIDs { get; set; }

        // ========== DISCORD BOT SETTINGS ==========
        [XmlElement]
        public DiscordConfig Discord { get; set; }

        // ========== CHAT SYNC SETTINGS ==========
        [XmlElement]
        public ChatConfig Chat { get; set; }

        // ========== DEATH LOGGING SETTINGS ==========
        [XmlElement]
        public DeathConfig Death { get; set; }

        // ========== SERVER MONITORING SETTINGS ==========
        [XmlElement]
        public MonitoringConfig Monitoring { get; set; }

        // ========== FACTION SETTINGS ==========
        [XmlElement]
        public FactionConfig Faction { get; set; }

        // ========== SERVICE CLEANUP INTERVALS (TASK 1) ==========
        /// <summary>
        /// Cleanup interval for services (in seconds)
        /// Default: 30 seconds
        /// </summary>
        [XmlElement]
        public int CleanupIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// Maximum age for damage history records (in seconds)
        /// Default: 15 seconds
        /// </summary>
        [XmlElement]
        public int DamageHistoryMaxSeconds { get; set; } = 15;

        // ========== DATA STORAGE SETTINGS (TASK 2) ==========
        /// <summary>
        /// Data storage configuration - controls what data is saved to XML files
        /// Includes event logging, death history, and chat message archiving
        /// </summary>
        [XmlElement]
        public DataStorageConfig DataStorage { get; set; }

        public MainConfig()
        {
            Enabled = true;
            Debug = false;
            AdminSteamIDs = new long[] { 
                76561198020205461, // Replace with actual admin SteamIDs
                76561198000000001  // Add actual admin SteamIDs here
                };
            Discord = new DiscordConfig();
            Chat = new ChatConfig();
            Death = new DeathConfig();
            Monitoring = new MonitoringConfig();
            Faction = new FactionConfig();
            DataStorage = new DataStorageConfig();
        }

        // Updated Load method to use correct path
        public static MainConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    int legacyRootSyncInterval = TryReadLegacyRootSyncInterval();
                    XmlSerializer serializer = new XmlSerializer(typeof(MainConfig));
                    using (FileStream fs = new FileStream(ConfigPath, FileMode.Open))
                    {
                        MainConfig config = (MainConfig)serializer.Deserialize(fs);
                        if (config == null) return new MainConfig();

                        if (config.Discord == null)
                            config.Discord = new DiscordConfig();

                        if (config.Discord.SyncIntervalSeconds <= 0 && legacyRootSyncInterval > 0)
                            config.Discord.SyncIntervalSeconds = legacyRootSyncInterval;
                        if (config.Discord.SyncIntervalSeconds <= 0)
                            config.Discord.SyncIntervalSeconds = 30;
                        if (config.Discord.PresenceUpdateIntervalSeconds <= 0)
                            config.Discord.PresenceUpdateIntervalSeconds = 1;

                        return config;
                    }
                }
                else
                {
                    MainConfig config = new MainConfig();
                    config.Save();
                    return config;
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogException("Failed to load MainConfig.", ex);
            }
            return new MainConfig();
        }

        private static int TryReadLegacyRootSyncInterval()
        {
            try
            {
                var document = new XmlDocument();
                document.Load(ConfigPath);
                var node = document.DocumentElement?.SelectSingleNode("SyncIntervalSeconds");
                if (node == null)
                    return 0;

                return int.TryParse(node.InnerText, out var seconds) ? seconds : 0;
            }
            catch
            {
                return 0;
            }
        }

        // Updated Save method to use correct path
        public void Save()
        {
            try
            {
                // Use the updated ConfigPath property
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                XmlSerializer serializer = new XmlSerializer(typeof(MainConfig));
                using (FileStream fs = new FileStream(ConfigPath, FileMode.Create))
                {
                    serializer.Serialize(fs, this);
                }
                LoggerUtil.LogSuccess("MainConfig saved successfully");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogException("Failed to save MainConfig.", ex);
            }
        }
    }

    // ========== DISCORD CONFIGURATION ==========
    [XmlType("DiscordConfig")]
    public class DiscordConfig
    {
        /// <summary>
        /// How often (in seconds) faction sync runs.
        /// Default: 30 seconds.
        /// </summary>
        [XmlElement]
        public int SyncIntervalSeconds { get; set; }

        [XmlElement]
        public string BotToken { get; set; }

        [XmlElement]
        public ulong GuildID { get; set; }

        [XmlElement]
        public ulong ChatChannelId { get; set; }

        [XmlElement]
        public ulong StaffLog { get; set; }

        [XmlElement]
        public ulong StatusChannelId { get; set; }

        [XmlElement]
        public ulong SimSpeedChannelId { get; set; }

        [XmlElement]
        public ulong PlayerCountChannelId { get; set; }

        [XmlElement]
        public ulong FactionCategoryId { get; set; }

        [XmlElement]
        public ulong AdminAlertChannelId { get; set; }

        /// <summary>
        /// Discord channel ID for admin bot commands (!tds ...).
        /// Only messages in this channel are processed as admin commands.
        /// NOT visible to regular players.
        /// </summary>
        [XmlElement]
        public ulong AdminBotChannelId { get; set; }

        [XmlElement]
        public int PresenceUpdateIntervalSeconds { get; set; }

        public DiscordConfig()
        {
            SyncIntervalSeconds = 30;
            BotToken = "YOUR_BOT_TOKEN";
            GuildID = 0;
            ChatChannelId = 0;
            StaffLog = 0;
            StatusChannelId = 0;
            SimSpeedChannelId = 0;
            PlayerCountChannelId = 0;
            FactionCategoryId = 0;
            AdminAlertChannelId = 1470032530139906178;
            AdminBotChannelId   = 1478357809044131980;
            PresenceUpdateIntervalSeconds = 1;
        }
    }

    // ========== CHAT SYNCHRONIZATION CONFIGURATION ==========
    [XmlType("ChatConfig")]
    public class ChatConfig
    {  
        [XmlElement]
        public bool Enabled { get; set; }

        [XmlElement]
        public ulong AdminLogChannelId { get; set; }

        [XmlElement]
        public bool BotToGame { get; set; }

        [XmlElement]
        public bool ServerToDiscord { get; set; }

        [XmlElement]
        public string GameToDiscordFormat { get; set; }

        [XmlElement]
        public string DiscordToGameFormat { get; set; }

        [XmlElement]
        public string ConnectMessage { get; set; }

        [XmlElement]
        public string JoinMessage { get; set; }

        [XmlElement]
        public string LeaveMessage { get; set; }

        [XmlElement]
        public bool UseFactionChat { get; set; }

        [XmlElement]
        public string FactionChatFormat { get; set; }

        [XmlElement]
        public string FactionDiscordFormat { get; set; }

        [XmlElement]
        public string GlobalColor { get; set; }

        [XmlElement]
        public string FactionColor { get; set; }

        [XmlElement]
        public bool StripEmojisForInGameChat { get; set; }

        public ChatConfig()
        {
            Enabled = false;
            BotToGame = false;
            ServerToDiscord = false;
            GameToDiscordFormat = ":rocket: **{p}**: {msg}";
            DiscordToGameFormat = "[Discord] {p}: {msg}";
            ConnectMessage = ":key: {p} connected to server";
            JoinMessage = ":sunny: {p} joined the server";
            LeaveMessage = ":new_moon: {p} left the server";
            UseFactionChat = false;
            FactionChatFormat = ":ledger: **{p}**: {msg}";
            FactionDiscordFormat = "[SE-Faction] {p}: {msg}";
            GlobalColor = "White";
            FactionColor = "Green";
            StripEmojisForInGameChat = true;
            AdminLogChannelId = 0;
        }
    }

    // ========== DEATH LOGGING CONFIGURATION ==========
    [XmlType("DeathConfig")]
    public class DeathConfig
    {
        [XmlElement]
        public bool Enabled { get; set; }

        [XmlElement]
        public bool LogToDiscord { get; set; }

        [XmlElement]
        public bool AnnounceInGame { get; set; }

        [XmlElement]
        public bool DetectRetaliation { get; set; }

        [XmlElement]
        public int RetaliationWindowMinutes { get; set; }

        [XmlElement]
        public int OldRevengeWindowHours { get; set; }

        [XmlElement]
        public bool EnableLocationZones { get; set; }

        [XmlElement]
        public bool GridDetectionEnabled { get; set; }

        [XmlElement]
        public bool ShowGridName { get; set; }

        [XmlElement]
        public string DeathMessageEmotes { get; set; }

        [XmlElement]
        public int MessageDeduplicationWindowSeconds { get; set; }

        [XmlElement]
        public double InnerSystemMaxKm { get; set; }

        [XmlElement]
        public double OuterSpaceMaxKm { get; set; }

        [XmlElement]
        public double PlanetProximityMultiplier { get; set; }

        public DeathConfig()
        {
            Enabled = false;
            LogToDiscord = false;
            AnnounceInGame = false;
            DetectRetaliation = false;
            RetaliationWindowMinutes = 60;
            OldRevengeWindowHours = 24;
            EnableLocationZones = true;
            GridDetectionEnabled = true;
            ShowGridName = true;
            DeathMessageEmotes = "📢,⚔️,💀,🔥,⚡";
            MessageDeduplicationWindowSeconds = 3;
            InnerSystemMaxKm = 5000.0;
            OuterSpaceMaxKm = 10000.0;
            PlanetProximityMultiplier = 3.0;
        }
    }

    // ========== SERVER MONITORING CONFIGURATION ==========
    [XmlType("MonitoringConfig")]
    public class MonitoringConfig
    {
        [XmlElement]
        public bool Enabled { get; set; }

        [XmlElement]
        public float SimSpeedThreshold { get; set; }

        [XmlElement]
        public int StatusUpdateIntervalSeconds { get; set; }

        [XmlElement]
        public bool EnableSimSpeedMonitoring { get; set; }

        [XmlElement]
        public string SimSpeedChannelNameFormat { get; set; }

        [XmlElement]
        public string SimSpeedNormalEmoji { get; set; }

        [XmlElement]
        public string SimSpeedWarningEmoji { get; set; }

        [XmlElement]
        public bool EnableSimSpeedAlerts { get; set; }

        [XmlElement]
        public string SimSpeedAlertMessage { get; set; }

        [XmlElement]
        public int SimSpeedAlertCooldownSeconds { get; set; }

        [XmlElement]
        public bool EnablePlayerCountMonitoring { get; set; }

        [XmlElement]
        public string PlayerCountChannelNameFormat { get; set; }

        [XmlElement]
        public bool EnablePlayerCountAlerts { get; set; }

        [XmlElement]
        public int PlayerCountAlertThreshold { get; set; }

        [XmlElement]
        public string PlayerCountAlertMessage { get; set; }

        [XmlElement]
        public bool EnableAdminAlerts { get; set; }

        [XmlElement]
        public string ServerStartedMessage { get; set; }

        [XmlElement]
        public string ServerStoppedMessage { get; set; }

        [XmlElement]
        public string ServerRestartedMessage { get; set; }

        [XmlElement]
        public string ServerCrashedMessage { get; set; }

        public MonitoringConfig()
        {
            Enabled = true;
            SimSpeedThreshold = 0.6f;
            StatusUpdateIntervalSeconds = 30;
            EnableSimSpeedMonitoring = true;
            SimSpeedChannelNameFormat = "{emoji} SimSpeed: {ss}";
            SimSpeedNormalEmoji = "🔧";
            SimSpeedWarningEmoji = "⚠️";
            EnableSimSpeedAlerts = true;
            SimSpeedAlertMessage = "🚨 **SIMSPEED WARNING** 🚨\nCurrent: **{ss}**\nThreshold: **{threshold}**";
            SimSpeedAlertCooldownSeconds = 1200;
            EnablePlayerCountMonitoring = true;
            PlayerCountChannelNameFormat = "👥 {p}/{pp} players";
            EnablePlayerCountAlerts = false;
            PlayerCountAlertThreshold = 10;
            PlayerCountAlertMessage = "📊 Player count: **{p}** / {pp}";
            EnableAdminAlerts = true;
            ServerStartedMessage = "✅ Server Started!";
            ServerStoppedMessage = "❌ Server Stopped!";
            ServerRestartedMessage = "🔄 Server Restarted!";
            ServerCrashedMessage = "💥 **CRITICAL: SERVER CRASHED** - Manual restart required!";
        }
    }

    // ========== FACTION CONFIGURATION ==========
    [XmlType("FactionConfig")]
    public class FactionConfig
    {
        [XmlElement]
        public bool Enabled { get; set; }

        [XmlElement]
        public bool AutoCreateChannels { get; set; }

        [XmlElement]
        public bool AutoCreateVoice { get; set; }

        /// <summary>
        /// When true, Discord faction messages are also sent to global chat with prefix [TAG Discord]
        /// so they are visible. Use if PM/EntityId delivery does not show in your client.
        /// Default: true so Discord→faction messages are visible in-game.
        /// </summary>
        [XmlElement]
        public bool FactionDiscordToGlobalFallback { get; set; }

        public FactionConfig()
        {
            Enabled = false;
            AutoCreateChannels = false;
            AutoCreateVoice = false;
            FactionDiscordToGlobalFallback = true;
        }
    }

    // ========== DATA STORAGE CONFIGURATION (TASK 2) ==========
    [XmlType("DataStorageConfig")]
    public class DataStorageConfig
    {
        /// <summary>
        /// Use SQLite as primary database instead of XML.
        /// XML files are kept as fallback if SQLite fails to initialize or encounters errors.
        /// Default: true (SQLite is used when available)
        /// </summary>
        [XmlElement]
        public bool UseSQLite { get; set; }

        /// <summary>
        /// Save event logs to EventData.xml
        /// Default: true (events are logged)
        /// </summary>
        [XmlElement]
        public bool SaveEventLogs { get; set; }

        /// <summary>
        /// Save death history to EventData.xml
        /// Default: true (deaths are logged)
        /// </summary>
        [XmlElement]
        public bool SaveDeathHistory { get; set; }

        /// <summary>
        /// Save global chat messages to ChatData.xml
        /// Default: false (global chat not logged by default)
        /// </summary>
        [XmlElement]
        public bool SaveGlobalChat { get; set; }

        /// <summary>
        /// Save faction chat messages to ChatData.xml
        /// Default: false (faction chat not logged by default)
        /// </summary>
        [XmlElement]
        public bool SaveFactionChat { get; set; }

        /// <summary>
        /// Save private chat messages to ChatData.xml
        /// Default: false (private chat not logged by default for privacy)
        /// </summary>
        [XmlElement]
        public bool SavePrivateChat { get; set; }

        /// <summary>
        /// Default constructor with recommended settings
        /// </summary>
        public DataStorageConfig()
        {
            UseSQLite = true;
            SaveEventLogs = true;
            SaveDeathHistory = true;
            SaveGlobalChat = false;
            SaveFactionChat = false;
            SavePrivateChat = false;
        }
    }
}
