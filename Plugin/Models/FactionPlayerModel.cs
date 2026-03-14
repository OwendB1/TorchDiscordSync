// Plugin/Models/FactionPlayerModel.cs
using System;
using System.Xml.Serialization;

namespace mamba.TorchDiscordSync.Plugin.Models
{
    [Serializable]
    public class FactionPlayerModel
    {
        [XmlElement]
        public int PlayerID { get; set; }

        [XmlElement]
        public long SteamID { get; set; }

        [XmlElement]
        public string OriginalNick { get; set; }

        [XmlElement]
        public string SyncedNick { get; set; }

        [XmlElement]
        public ulong DiscordUserID { get; set; }

        [XmlElement]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [XmlElement]
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
