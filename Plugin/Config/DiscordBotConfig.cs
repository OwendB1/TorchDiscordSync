// Plugin/Config/DiscordBotConfig.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace mamba.TorchDiscordSync.Plugin.Config
{
    public class DiscordBotConfig
    {
        // Shared fields moved to MainConfig.Discord.
        // Keep properties here for backward compatibility in code but ignore them for XML.

        [XmlIgnore]
        public string BotToken { get; set; }

        [XmlIgnore]
        public ulong GuildID { get; set; }

        [XmlElement]
        public string BotPrefix { get; set; } = "!";

        [XmlElement]
        public bool EnableDMNotifications { get; set; } = true;

        [XmlElement]
        public int VerificationCodeExpirationMinutes { get; set; } = 15;

        // Add module-specific serialized fields here if needed.
    }
}
