// Plugin/Models/LegacyRootDataModel.cs
// Used only for migrating old MambaTorchDiscordSyncData.xml (all-in-one) into separate XML files.
using System.Collections.Generic;
using System.Xml.Serialization;

namespace TorchDiscordSync.Plugin.Models
{
    [XmlRoot("MambaTorchDiscordSyncData")]
    public class LegacyRootDataModel
    {
        [XmlArray("Factions")]
        [XmlArrayItem("Faction")]
        public List<FactionModel> Factions { get; set; } = new List<FactionModel>();

        [XmlArray("Players")]
        [XmlArrayItem("Player")]
        public List<PlayerModel> Players { get; set; } = new List<PlayerModel>();

        [XmlArray("EventLogs")]
        [XmlArrayItem("Event")]
        public List<EventLogModel> EventLogs { get; set; } = new List<EventLogModel>();

        [XmlArray("DeathHistory")]
        [XmlArrayItem("Death")]
        public List<DeathHistoryModel> DeathHistory { get; set; } = new List<DeathHistoryModel>();

    }
}
