// Plugin/Core/VerificationCommandHandler.cs
using System;
using System.Threading.Tasks;
using mamba.TorchDiscordSync.Plugin.Config;
using mamba.TorchDiscordSync.Plugin.Models;
using mamba.TorchDiscordSync.Plugin.Services;
using mamba.TorchDiscordSync.Plugin.Utils;

namespace mamba.TorchDiscordSync.Plugin.Core
{
    public class VerificationCommandHandler
    {
        private readonly MainConfig _config;
        private readonly VerificationService _verification;
        private readonly EventLoggingService _eventLog;
        private readonly DiscordBotService _discordBot;
        private readonly DiscordBotConfig _discordBotConfig;

        public VerificationCommandHandler(
            VerificationService verification,
            EventLoggingService evtLog,
            MainConfig config,
            DiscordBotService bot,
            DiscordBotConfig botConfig
        )
        {
            _verification = verification;
            _eventLog = evtLog;
            _config = config;
            _discordBot = bot;
            _discordBotConfig = botConfig;
        }

        /// <summary>
        /// Handle in-game /tds verify @DiscordUsername command
        /// Creates pending verification and sends DM to Discord user
        /// KORAK 2: Create pending verification without marking as verified immediately
        /// </summary>
        public async Task<string> HandleVerifyCommandAsync(
            long playerSteamID,
            string playerName,
            string discordUsername
        )
        {
            try
            {
                if (string.IsNullOrWhiteSpace(discordUsername))
                    return "Error: Discord username is required. Usage: /tds verify @DiscordUsername";

                discordUsername = discordUsername.TrimStart('@').Trim();

                if (discordUsername.Length < 2 || discordUsername.Length > 32)
                    return "Error: Invalid Discord username length (2-32 characters)";

                LoggerUtil.LogDebug(
                    $"[VERIFY_CMD] HandleVerifyCommandAsync: SteamID={playerSteamID}, Player={playerName}, Discord={discordUsername}"
                );

                // KORAK 2.1: Create pending verification (generates code and stores in XML)
                bool pendingCreated = _verification.CreatePendingVerification(
                    playerSteamID,
                    discordUsername,
                    playerName
                );

                if (!pendingCreated)
                    return "Error: Could not create pending verification. Please try again.";

                // KORAK 2.2: Get the generated code from pending verification
                var pending = _verification.GetPendingVerificationByDiscord(
                    discordUsername,
                    playerSteamID
                );
                if (pending == null)
                    return "Error: Verification code not found. Please try again.";

                string code = pending.VerificationCode;

                // KORAK 2.3: Send verification code via Discord DM
                LoggerUtil.LogDebug(
                    $"[VERIFY_CMD] Sending DM to {discordUsername} with code: {code}"
                );
                bool dmSent = await _discordBot.SendVerificationDMAsync(discordUsername, code);

                if (!dmSent)
                {
                    LoggerUtil.LogWarning($"[VERIFY_CMD] Failed to send DM to {discordUsername}");
                    return "Error: Could not find Discord user '"
                        + discordUsername
                        + "' or send DM.\n"
                        + "Make sure:\n"
                        + "  - Username is correct\n"
                        + "  - User is in the Discord server\n"
                        + "  - Bot has DM permissions";
                }

                await _eventLog.LogAsync(
                    "VerificationRequest",
                    playerName
                        + " ("
                        + playerSteamID
                        + ") requested verification as "
                        + discordUsername
                        + ". DM sent with code: "
                        + code
                );

                LoggerUtil.LogSuccess(
                    "[VERIFY_CMD] "
                        + playerName
                        + ": "
                        + discordUsername
                        + " - Code: "
                        + code
                        + " - DM sent successfully"
                );

                string message =
                    "Verification code sent to "
                    + discordUsername
                    + " via DM!\n"
                    + "Check your Discord private messages\n"
                    + "Code expires in "
                    + _discordBotConfig.VerificationCodeExpirationMinutes
                    + " minutes";

                return message;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[VERIFY_CMD] Command error: " + ex.Message);
                return "Error: " + ex.Message;
            }
        }

        /// <summary>
        /// Handle verification from Discord bot (!verify CODE command)
        /// Called when user completes verification on Discord
        /// </summary>
        public async Task<VerificationResult> VerifyFromDiscordAsync(
            string code,
            ulong discordId,
            string discordUsername
        )
        {
            if (string.IsNullOrEmpty(code))
            {
                return new VerificationResult { Message = "Invalid verification code", IsSuccess = false };
            }

            if (_verification != null)
            {
                return await _verification.VerifyAsync(code, discordId, discordUsername);
            }

            return new VerificationResult { Message = "Verification service unavailable", IsSuccess = false };
        }

        /// <summary>
        /// Handle /tds unverify STEAMID [reason] command (admin only)
        /// Removes verification link for a player
        /// </summary>
        public async Task<string> HandleUnverifyCommandAsync(
            long steamID,
            string reason = "Admin removal"
        )
        {
            try
            {
                bool success = _verification.RemoveVerification(steamID, reason);

                if (success)
                {
                    await _eventLog.LogAsync(
                        "VerificationRemoved",
                        "SteamID " + steamID + " unverified: " + reason
                    );

                    LoggerUtil.LogSuccess("[VERIFY] Unverified SteamID " + steamID + ": " + reason);
                    return "Verification removed";
                }
                else
                {
                    return "Error: Verification not found for this Steam ID";
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[VERIFY] Unverify error: " + ex.Message);
                return "Error: " + ex.Message;
            }
        }
    }
}
