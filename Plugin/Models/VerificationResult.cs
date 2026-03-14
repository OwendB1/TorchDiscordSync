namespace mamba.TorchDiscordSync.Plugin.Models
{
    /// <summary>
    /// Result of verification attempt from Discord. Used to send in-game notification (success or error).
    /// </summary>
    public class VerificationResult
    {
        public string Message { get; set; }
        public bool IsSuccess { get; set; }
        /// <summary>SteamID for in-game notification when player is known (success or e.g. expired code).</summary>
        public long? SteamIdForNotify { get; set; }
        public string GamePlayerName { get; set; }
    }
}
