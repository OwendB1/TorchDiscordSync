// Plugin/Config/MainConfig.cs
// MAMBA CONFIGURATION MODULE
using System;
using System.IO;
using System.Xml.Serialization;
using mamba.TorchDiscordSync.Plugin.Utils;

namespace mamba.TorchDiscordSync.Plugin.Config
{
    [XmlRoot("MainConfig")]
    public class MainConfig
    {
        // ===================================================================
        // VERSION (Managed by build.py)
        // ===================================================================
        public string PluginVersion { get; set; } = "2.4.65";

        // ============================================================
        // CENTRAL PATH MANAGEMENT - Single Point of Control
        // ============================================================

        /// <summary>
        /// Plugin directory name - used for all plugin files and configs
        /// Change this one constant to change plugin directory name everywhere!
        /// </summary>
        public static readonly string PLUGIN_DIR_NAME = "mambaSaveData";

        public static string GetInstancePath()
        {
            string instancePath = Environment.GetEnvironmentVariable("TORCH_INSTANCE_PATH");
            if (string.IsNullOrEmpty(instancePath))
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                instancePath = Path.Combine(baseDir, "Instance");
            }
            return instancePath;
        }

        public static string GetPluginDirectory()
        {
            string pluginDir = Path.Combine(GetInstancePath(), PLUGIN_DIR_NAME);
            if (!Directory.Exists(pluginDir))
            {
                try { Directory.CreateDirectory(pluginDir); }
                catch (Exception ex) { LoggerUtil.LogError($"Failed to create plugin directory {pluginDir}: {ex.Message}"); }
            }
            return pluginDir;
        }

        public static string GetConfigDirectory()
        {
            string configDir = Path.Combine(GetPluginDirectory(), "configs");
            if (!Directory.Exists(configDir))
            {
                try { Directory.CreateDirectory(configDir); }
                catch (Exception ex) { LoggerUtil.LogError($"Failed to create config directory {configDir}: {ex.Message}"); }
            }
            return configDir;
        }

        public static string GetDataDirectory()
        {
            string dataDir = Path.Combine(GetPluginDirectory(), "data");
            if (!Directory.Exists(dataDir))
            {
                try { Directory.CreateDirectory(dataDir); }
                catch (Exception ex) { LoggerUtil.LogError($"Failed to create data directory {dataDir}: {ex.Message}"); }
            }
            return dataDir;
        }

        public static string GetLogDirectory()
        {
            string logDir = Path.Combine(GetPluginDirectory(), "logs");
            if (!Directory.Exists(logDir))
            {
                try { Directory.CreateDirectory(logDir); }
                catch (Exception ex) { LoggerUtil.LogError($"Failed to create log directory {logDir}: {ex.Message}"); }
            }
            return logDir;
        }

        private static string ConfigPath
        {
            get { return Path.Combine(GetConfigDirectory(), "MainConfig.xml"); }
        }

        // ========== CORE SETTINGS ==========
        [XmlElement]
        public bool Enabled { get; set; }

        [XmlElement]
        public bool Debug { get; set; }

        /// <summary>
        /// Deprecated: moved to Discord.SyncIntervalSeconds.
        /// Kept for backward XML compatibility.
        /// </summary>
        [XmlElement]
        public int SyncIntervalSeconds { get; set; }

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

        // ========== VERIFICATION SETTINGS ==========
        [XmlElement]
        public int VerificationCodeExpirationMinutes { get; set; } = 15;

        // ========== SERVICE CLEANUP INTERVALS ==========
        /// <summary>
        /// Cleanup interval for services (in seconds). Default: 30
        /// </summary>
        [XmlElement]
        public int CleanupIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// Maximum age for damage history records (in seconds). Default: 15
        /// </summary>
        [XmlElement]
        public int DamageHistoryMaxSeconds { get; set; } = 15;

        // ========== NPC ACTIVITY FILTER ==========
        /// <summary>
        /// When false (default), NPC characters (wolves, saberiods, pirate drones)
        /// are excluded from join/leave announcements and death messages.
        /// NPCs are identified by IsBot == true or SteamUserId == 0.
        /// Set to true only if you want NPC activity shown in chat and Discord.
        /// </summary>
        [XmlElement]
        public bool ShowNpcActivity { get; set; }

        // ========== LOCALIZATION ==========
        /// <summary>
        /// BCP-47 language code for the active translation profile.
        /// The plugin looks for [Language].ini in the configs directory.
        /// If the file is missing or the code is "en-US", built-in English strings are used.
        /// Example values: en-US, hr-HR, de-DE, pt-BR
        /// Default: en-US
        /// </summary>
        [XmlElement]
        public string Language { get; set; }

        // ========== DATA STORAGE SETTINGS ==========
        /// <summary>
        /// Data storage configuration - controls what data is saved to XML/SQLite
        /// </summary>
        [XmlElement]
        public DataStorageConfig DataStorage { get; set; }

        public MainConfig()
        {
            Enabled = true;
            Debug = false;
            SyncIntervalSeconds = 30;
            AdminSteamIDs = new long[]
            {
                76561198020205461,
                76561198000000001
            };
            Discord = new DiscordConfig();
            Chat = new ChatConfig();
            Death = new DeathConfig();
            Monitoring = new MonitoringConfig();
            Faction = new FactionConfig();
            DataStorage = new DataStorageConfig();
            ShowNpcActivity = false;
            Language = "en-US";
        }

        public static MainConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(MainConfig));
                    using (FileStream fs = new FileStream(ConfigPath, FileMode.Open))
                    {
                        MainConfig config = (MainConfig)serializer.Deserialize(fs);
                        if (config == null) return new MainConfig();

                        // Migration: root SyncIntervalSeconds -> Discord.SyncIntervalSeconds
                        if (config.SyncIntervalSeconds > 0)
                        {
                            if (config.Discord == null) config.Discord = new DiscordConfig();
                            if (config.Discord.SyncIntervalSeconds <= 0)
                                config.Discord.SyncIntervalSeconds = config.SyncIntervalSeconds;
                            config.SyncIntervalSeconds = 0;
                        }
                        if (config.Discord != null && config.Discord.SyncIntervalSeconds <= 0)
                            config.Discord.SyncIntervalSeconds = 30;

                        // Ensure Language has a default if missing from old XML
                        if (string.IsNullOrEmpty(config.Language))
                            config.Language = "en-US";

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
                LoggerUtil.LogError("Failed to load MainConfig: " + ex.Message);
            }
            return new MainConfig();
        }

        public void Save()
        {
            try
            {
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
                LoggerUtil.LogError("Failed to save MainConfig: " + ex.Message);
            }
        }
    }

    // ========== DISCORD CONFIGURATION ==========
    [XmlType("DiscordConfig")]
    public class DiscordConfig
    {
        [XmlElement]
        public int SyncIntervalSeconds { get; set; }

        [XmlElement]
        public string BotToken { get; set; }

        [XmlElement]
        public ulong GuildID { get; set; }

        [XmlElement]
        public string BotPrefix { get; set; }

        [XmlElement]
        public bool EnableDMNotifications { get; set; }

        [XmlElement]
        public int VerificationCodeExpirationMinutes { get; set; }

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
        /// </summary>
        [XmlElement]
        public ulong AdminBotChannelId { get; set; }

        [XmlElement]
        public ulong VerifiedRoleId { get; set; }

        public DiscordConfig()
        {
            SyncIntervalSeconds = 30;
            BotToken = "YOUR_BOT_TOKEN";
            GuildID = 0;
            BotPrefix = "!";
            EnableDMNotifications = true;
            VerificationCodeExpirationMinutes = 15;
            ChatChannelId = 0;
            StaffLog = 0;
            StatusChannelId = 0;
            SimSpeedChannelId = 0;
            PlayerCountChannelId = 0;
            FactionCategoryId = 0;
            AdminAlertChannelId = 1470032530139906178;
            AdminBotChannelId   = 1478357809044131980;
            VerifiedRoleId = 0;
        }
    }

    // ========== CHAT SYNCHRONIZATION CONFIGURATION ==========
    [XmlType("ChatConfig")]
    public class ChatConfig
    {
        [XmlElement]
        public bool Enabled { get; set; }

        [XmlElement]
        public bool EnableModeration { get; set; }

        [XmlElement]
        public int MaxWarningsBeforeMute { get; set; }

        [XmlElement]
        public int MuteDurationMinutes { get; set; }

        [XmlElement]
        public int MaxMutesBeforeKick { get; set; }

        [XmlElement]
        public string WarningMessage { get; set; }

        [XmlElement]
        public string MuteMessage { get; set; }

        [XmlElement]
        public string KickMessage { get; set; }

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

        // ========== THIRD-PARTY CHAT BOT FILTER ==========
        /// <summary>
        /// Author names to silently drop before they reach Discord/the relay logic.
        /// Use this when another bot or mod on the server echoes/reposts player
        /// messages back into in-game chat under its own display name (e.g. "Good.bot"),
        /// which would otherwise be forwarded to Discord a second time, duplicating
        /// every message. Comparison is case-insensitive. Empty by default — add the
        /// exact author name as shown in the duplicated chat line to silence it.
        /// </summary>
        [XmlArray("IgnoredChatAuthors")]
        [XmlArrayItem("Author")]
        public string[] IgnoredChatAuthors { get; set; }

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
            EnableModeration = false;
            MaxWarningsBeforeMute = 3;
            MuteDurationMinutes = 10;
            MaxMutesBeforeKick = 2;
            WarningMessage = "?? Please avoid using inappropriate language.";
            MuteMessage = "?? You have been muted for {minutes} minutes due to repeated violations.";
            KickMessage = "?? You have been removed from the channel for repeated violations.";
            AdminLogChannelId = 0;
            IgnoredChatAuthors = new string[0];
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
        public bool AutoCreateForum { get; set; }

        [XmlElement]
        public bool AutoCreateVoice { get; set; }

        /// <summary>
        /// When true, Discord faction messages are also sent to global chat.
        /// Default: true so Discord→faction messages are visible in-game.
        /// </summary>
        [XmlElement]
        public bool FactionDiscordToGlobalFallback { get; set; }

        public FactionConfig()
        {
            Enabled = false;
            AutoCreateChannels = false;
            AutoCreateForum = false;
            AutoCreateVoice = false;
            FactionDiscordToGlobalFallback = true;
        }
    }

    // ========== DATA STORAGE CONFIGURATION ==========
    [XmlType("DataStorageConfig")]
    public class DataStorageConfig
    {
        /// <summary>
        /// Use SQLite as primary database instead of XML.
        /// Falls back to XML if SQLite DLL is absent.
        /// Default: true
        /// </summary>
        [XmlElement]
        public bool UseSQLite { get; set; }

        [XmlElement]
        public bool SaveEventLogs { get; set; }

        [XmlElement]
        public bool SaveDeathHistory { get; set; }

        [XmlElement]
        public bool SaveGlobalChat { get; set; }

        [XmlElement]
        public bool SaveFactionChat { get; set; }

        [XmlElement]
        public bool SavePrivateChat { get; set; }

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
