// Plugin/Models/DeathMessageModel.cs
using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace mamba.TorchDiscordSync.Plugin.Models
{
    [Serializable]
    [XmlRoot("DeathMessages")]
    public class DeathMessageModel
    {
        [XmlArray("Suicide")]
        [XmlArrayItem("Message")]
        public List<string> SuicideMessages { get; set; } = new List<string>();

        [XmlArray("FirstKill")]
        [XmlArrayItem("Message")]
        public List<string> FirstKillMessages { get; set; } = new List<string>();

        [XmlArray("Retaliate")]
        [XmlArrayItem("Message")]
        public List<string> RetaliateMessages { get; set; } = new List<string>();

        [XmlArray("RetaliateOld")]
        [XmlArrayItem("Message")]
        public List<string> RetaliateOldMessages { get; set; } = new List<string>();

        [XmlArray("Accident")]
        [XmlArrayItem("Message")]
        public List<string> AccidentMessages { get; set; } = new List<string>();

        public string GetRandomMessage(string category)
        {
            List<string> messages = category switch
            {
                "Suicide" => SuicideMessages,
                "FirstKill" => FirstKillMessages,
                "Retaliate" => RetaliateMessages,
                "RetaliateOld" => RetaliateOldMessages,
                "Accident" => AccidentMessages,
                _ => new List<string> { "{0} died." }
            };

            if (messages == null || messages.Count == 0)
                return "{0} died.";

            return messages[new Random().Next(messages.Count)];
        }
    }
}
