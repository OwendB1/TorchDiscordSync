// Plugin/Models/FactionModel.cs
using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace mamba.TorchDiscordSync.Plugin.Models
{
    [Serializable]
    public class FactionModel
    {
        [XmlElement]
        public int FactionID { get; set; }

        [XmlElement]
        public string Tag { get; set; }

        [XmlElement]
        public string Name { get; set; }

        [XmlElement]
        public ulong DiscordRoleID { get; set; }

        [XmlElement]
        public ulong DiscordChannelID { get; set; }

        // ========== NEW: TRACKING FIELDS ==========
        [XmlElement]
        public string DiscordRoleName { get; set; }  // = Tag (BLB, sVz)

        [XmlElement]
        public string DiscordChannelName { get; set; }  // = lowercase Name (blind leading blind)

        [XmlElement]
        public string SyncStatus { get; set; } = "Pending";  // Pending, Synced, Failed, Orphaned

        [XmlElement]
        public DateTime? SyncedAt { get; set; }

        [XmlElement]
        public string SyncedBy { get; set; }  // "SyncOrchestrator" or admin name

        [XmlElement]
        public string ErrorMessage { get; set; }

        [XmlElement]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [XmlElement]
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        [XmlArray("Players")]
        [XmlArrayItem("Player")]
        public List<FactionPlayerModel> Players { get; set; } = new List<FactionPlayerModel>();

        // ========== FACTION CHAT CHANNELS (NEW) ==========
        /// <summary>
        /// Discord forum channel ID for faction
        /// Used for organized discussions
        /// </summary>
        [XmlElement]
        public ulong DiscordForumID { get; set; }

        /// <summary>
        /// Discord forum channel name (same as faction name lowercase)
        /// </summary>
        [XmlElement]
        public string DiscordForumName { get; set; }

        /// <summary>
        /// Discord voice channel ID for faction
        /// Used for voice communications
        /// </summary>
        [XmlElement]
        public ulong DiscordVoiceChannelID { get; set; }

        /// <summary>
        /// Discord voice channel name
        /// Format: "[TAG]-voice"
        /// </summary>
        [XmlElement]
        public string DiscordVoiceChannelName { get; set; }

        /// <summary>
        /// Game/Torch faction chat channel ID (e.g. from "Faction:233056185186241842").
        /// Used to match in-game faction chat to this synced faction and forward to Discord.
        /// </summary>
        [XmlElement]
        public long GameFactionChatId { get; set; }

        /// <summary>
        /// Track all Discord channels created for this faction
        /// Used for complete undo capability
        /// </summary>
        [XmlArray("DiscordChannelsCreated")]
        [XmlArrayItem("Channel")]
        public List<DiscordChannelCreated> ChannelsCreated { get; set; } = new List<DiscordChannelCreated>();
    }

    /// <summary>
    /// Track Discord channels created for faction
    /// Used to implement complete undo/cleanup functionality
    /// </summary>
    [XmlType("DiscordChannelCreated")]
    public class DiscordChannelCreated
    {
        /// <summary>
        /// Discord channel ID
        /// </summary>
        [XmlElement]
        public ulong ChannelID { get; set; }

        /// <summary>
        /// Discord channel name
        /// </summary>
        [XmlElement]
        public string ChannelName { get; set; }

        /// <summary>
        /// Channel type: "Text", "Forum", or "Voice"
        /// </summary>
        [XmlElement]
        public string ChannelType { get; set; }

        /// <summary>
        /// When the channel was created
        /// </summary>
        [XmlElement]
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Track if channel was already deleted on undo
        /// </summary>
        [XmlElement]
        public bool DeletedOnUndo { get; set; } = false;

        /// <summary>
        /// Default constructor
        /// </summary>
        public DiscordChannelCreated()
        {
            ChannelID = 0;
            ChannelName = "";
            ChannelType = "";
            CreatedAt = DateTime.UtcNow;
            DeletedOnUndo = false;
        }
    }

}
