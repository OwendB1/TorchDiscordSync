// Plugin/Models/EventDataModel.cs
using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace mamba.TorchDiscordSync.Plugin.Models
{
    /// <summary>
    /// Root model for EventData.xml
    /// Contains event logs and death history separated from other data
    /// Saved only if DataStorage config flags are enabled
    /// </summary>
    [XmlRoot("EventData")]
    public class EventDataModel
    {
        /// <summary>
        /// List of all event logs (if SaveEventLogs is enabled)
        /// </summary>
        [XmlArray("EventLogs")]
        [XmlArrayItem("Event")]
        public List<EventLogModel> EventLogs { get; set; } = new List<EventLogModel>();

        /// <summary>
        /// List of all death records (if SaveDeathHistory is enabled)
        /// </summary>
        [XmlArray("DeathHistory")]
        [XmlArrayItem("Death")]
        public List<DeathHistoryModel> DeathHistory { get; set; } = new List<DeathHistoryModel>();

        /// <summary>
        /// Default constructor
        /// </summary>
        public EventDataModel()
        {
            EventLogs = new List<EventLogModel>();
            DeathHistory = new List<DeathHistoryModel>();
        }
    }
}