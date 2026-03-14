// Plugin/Models/RootDataModel.cs
// Main data file: MambaTorchDiscordSyncData.xml - only verification data.
// Factions -> FactionData.xml, Players -> PlayerData.xml, Events/Deaths -> EventData.xml, Chat -> ChatData.xml.
using System.Collections.Generic;
using System.Xml.Serialization;

namespace mamba.TorchDiscordSync.Plugin.Models
{
    [XmlRoot("MambaTorchDiscordSyncData")]
    public class RootDataModel
    {
        [XmlArray("Verifications")]
        [XmlArrayItem("Verification")]
        public List<VerificationModel> Verifications { get; set; } = new List<VerificationModel>();

        [XmlArray("VerificationHistory")]
        [XmlArrayItem("Entry")]
        public List<VerificationHistoryModel> VerificationHistory { get; set; } = new List<VerificationHistoryModel>();
    }
}
