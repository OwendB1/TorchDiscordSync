// Plugin/Models/VerificationModel.cs
using System;
using System.Xml.Serialization;

namespace mamba.TorchDiscordSync.Plugin.Models
{
    [Serializable]
    public class VerificationModel
    {
        [XmlElement]
        public long SteamID { get; set; }

        [XmlElement]
        public string VerificationCode { get; set; }

        [XmlElement]
        public DateTime CodeGeneratedAt { get; set; } = DateTime.UtcNow;

        [XmlElement]
        public string DiscordUsername { get; set; }

        [XmlElement]
        public bool IsVerified { get; set; } = false;

        [XmlElement]
        public DateTime VerifiedAt { get; set; }

        [XmlElement]
        public ulong DiscordUserID { get; set; }
    }
}
