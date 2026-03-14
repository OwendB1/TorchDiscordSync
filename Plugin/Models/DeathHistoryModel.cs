// Plugin/Models/DeathHistoryModel.cs
using System;

namespace mamba.TorchDiscordSync.Plugin.Models
{
    [Serializable]
    public class DeathHistoryModel
    {
        public long KillerSteamID { get; set; }
        public long VictimSteamID { get; set; }
        public DateTime DeathTime { get; set; }
        public string DeathType { get; set; }

        public string KillerName { get; set; }
        public string VictimName { get; set; }
        public string Weapon { get; set; }
        public string Location { get; set; }
    }
}
