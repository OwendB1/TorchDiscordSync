// Plugin/Models/VerificationDataModel.cs
// VerificationData.xml - only verification events (history). No duplicate of VerificationPlayers.xml.
using System.Collections.Generic;
using System.Xml.Serialization;

namespace mamba.TorchDiscordSync.Plugin.Models
{
    [XmlRoot("VerificationData")]
    public class VerificationDataModel
    {
        [XmlArray("VerificationHistory")]
        [XmlArrayItem("Entry")]
        public List<VerificationHistoryModel> VerificationHistory { get; set; } = new List<VerificationHistoryModel>();

        public VerificationDataModel()
        {
            VerificationHistory = new List<VerificationHistoryModel>();
        }
    }
}
