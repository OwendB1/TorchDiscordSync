// Plugin/Models/ChatDataModel.cs
using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace mamba.TorchDiscordSync.Plugin.Models
{
    /// <summary>
    /// Root model for ChatData.xml
    /// Contains chat messages separated by type (global, faction, private)
    /// Saved only if corresponding DataStorage config flags are enabled
    /// </summary>
    [XmlRoot("ChatData")]
    public class ChatDataModel
    {
        /// <summary>
        /// Global server messages (if SaveGlobalChat is enabled)
        /// </summary>
        [XmlArray("GlobalMessages")]
        [XmlArrayItem("Message")]
        public List<ChatMessageModel> GlobalMessages { get; set; } = new List<ChatMessageModel>();

        /// <summary>
        /// Faction-specific messages (if SaveFactionChat is enabled)
        /// </summary>
        [XmlArray("FactionMessages")]
        [XmlArrayItem("Message")]
        public List<ChatMessageModel> FactionMessages { get; set; } = new List<ChatMessageModel>();

        /// <summary>
        /// Private player-to-player messages (if SavePrivateChat is enabled)
        /// </summary>
        [XmlArray("PrivateMessages")]
        [XmlArrayItem("Message")]
        public List<ChatMessageModel> PrivateMessages { get; set; } = new List<ChatMessageModel>();

        /// <summary>
        /// Default constructor
        /// </summary>
        public ChatDataModel()
        {
            GlobalMessages = new List<ChatMessageModel>();
            FactionMessages = new List<ChatMessageModel>();
            PrivateMessages = new List<ChatMessageModel>();
        }
    }

    /// <summary>
    /// Individual chat message record
    /// Contains metadata about source, sender, and content
    /// </summary>
    [XmlType("ChatMessageModel")]
    public class ChatMessageModel
    {
        /// <summary>
        /// Unique message identifier
        /// </summary>
        [XmlElement]
        public int MessageID { get; set; }

        /// <summary>
        /// Faction ID - only set for faction messages
        /// Foreign key to FactionData.Factions
        /// </summary>
        [XmlElement]
        public int? FactionID { get; set; }

        /// <summary>
        /// Sender Steam ID - for private messages
        /// </summary>
        [XmlElement]
        public long? FromSteamID { get; set; }

        /// <summary>
        /// Recipient Steam ID - for private messages
        /// </summary>
        [XmlElement]
        public long? ToSteamID { get; set; }

        /// <summary>
        /// Display name of message author
        /// </summary>
        [XmlElement]
        public string AuthorName { get; set; }

        /// <summary>
        /// Steam ID of message author (0 if from Discord)
        /// </summary>
        [XmlElement]
        public long AuthorSteamID { get; set; }

        /// <summary>
        /// Message content text
        /// </summary>
        [XmlElement]
        public string Content { get; set; }

        /// <summary>
        /// When message was sent
        /// </summary>
        [XmlElement]
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Message source: "Game" or "Discord"
        /// </summary>
        [XmlElement]
        public string Source { get; set; }

        /// <summary>
        /// Default constructor
        /// </summary>
        public ChatMessageModel()
        {
            MessageID = 0;
            FactionID = null;
            FromSteamID = null;
            ToSteamID = null;
            AuthorName = "";
            AuthorSteamID = 0;
            Content = "";
            Timestamp = DateTime.UtcNow;
            Source = "Game";
        }
    }
}