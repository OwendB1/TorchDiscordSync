// Plugin/Models/VerificationHistoryModel.cs
using System;
using System.Xml.Serialization;

namespace mamba.TorchDiscordSync.Plugin.Models
{
    [Serializable]
    public class VerificationHistoryModel
    {
        [XmlElement]
        public long SteamID { get; set; }

        [XmlElement]
        public string DiscordUsername { get; set; }

        [XmlElement]
        public ulong DiscordUserID { get; set; }

        [XmlElement]
        public DateTime VerifiedAt { get; set; }

        [XmlElement]
        public string Status { get; set; } // "Success", "Failed", "Expired", "Removed"
    }
}
