// Plugin/Models/CommandModel.cs
using System;

namespace mamba.TorchDiscordSync.Plugin.Models
{
    /// <summary>
    /// Defines command properties and authorization level
    /// </summary>
    [Serializable]
    public class CommandModel
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Usage { get; set; }
        public bool RequiresAdmin { get; set; }
        public int MinimumArguments { get; set; }
        public bool IsAuthorized { get; set; }

        public CommandModel() 
        { 
            IsAuthorized = false;
        }

        public CommandModel(string name, string description, string usage, bool requiresAdmin, int minArgs)
        {
            Name = name;
            Description = description;
            Usage = usage;
            RequiresAdmin = requiresAdmin;
            MinimumArguments = minArgs;
            IsAuthorized = false;
        }

        public string GetHelpText()
        {
            return Usage + "\n  └─ " + Description;
        }
    }
}
