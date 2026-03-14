// Plugin/Models/PlayerDataModel.cs
using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace mamba.TorchDiscordSync.Plugin.Models
{
    /// <summary>
    /// Root model for PlayerData.xml
    /// Contains all player information separated from other data
    /// </summary>
    [XmlRoot("PlayerData")]
    public class PlayerDataModel
    {
        /// <summary>
        /// List of all players with faction associations
        /// </summary>
        [XmlArray("Players")]
        [XmlArrayItem("Player")]
        public List<PlayerModel> Players { get; set; } = new List<PlayerModel>();

        /// <summary>
        /// Default constructor
        /// </summary>
        public PlayerDataModel()
        {
            Players = new List<PlayerModel>();
        }
    }
}