// Plugin/Models/FactionDataModel.cs
using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace mamba.TorchDiscordSync.Plugin.Models
{
    /// <summary>
    /// Root model for FactionData.xml
    /// Contains all faction information separated from other data
    /// </summary>
    [XmlRoot("FactionData")]
    public class FactionDataModel
    {
        /// <summary>
        /// List of all factions with Discord synchronization data
        /// </summary>
        [XmlArray("Factions")]
        [XmlArrayItem("Faction")]
        public List<FactionModel> Factions { get; set; } = new List<FactionModel>();

        /// <summary>
        /// Default constructor
        /// </summary>
        public FactionDataModel()
        {
            Factions = new List<FactionModel>();
        }
    }
}