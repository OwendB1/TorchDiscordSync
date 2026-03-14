// Plugin/Models/EventLogModel.cs
using System;

namespace mamba.TorchDiscordSync.Plugin.Models
{
    [System.Serializable]
    public class EventLogModel
    {
        public int EventID { get; set; }
        public string EventType { get; set; } // "Death", "Join", "Leave", "Sync"
        public string Details { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
