// Plugin/Models/DeathTypeEnum.cs
namespace mamba.TorchDiscordSync.Plugin.Models
{
    /// <summary>
    /// ENHANCED: Death type enumeration with Turret distinction
    /// </summary>
    public enum DeathTypeEnum
    {
        Unknown,
        Suicide,
        PvP, // Direct player vs player
        Turret, // NEW: Turret kills (player-owned or NPC)
        FirstKill,
        Retaliation,
        RetaliationOld,
        Grid, // Grid collision
        Environment_Oxygen,
        Environment_Pressure,
        Environment_Collision,
        Accident,
    }
}
