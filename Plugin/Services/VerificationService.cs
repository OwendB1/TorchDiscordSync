// Plugin/Services/VerificationService.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using mamba.TorchDiscordSync.Plugin.Models;
using mamba.TorchDiscordSync.Plugin.Utils;

namespace mamba.TorchDiscordSync.Plugin.Services
{
    public class VerificationService
    {
        private readonly DatabaseService _db;
        private readonly int _verificationCodeExpirationMinutes;
        private const int CodeLength = 8;
        private static readonly Random _random = new Random();

        public VerificationService(DatabaseService db, int verificationCodeExpirationMinutes = 15)
        {
            _db = db;
            _verificationCodeExpirationMinutes = verificationCodeExpirationMinutes;
            LoggerUtil.LogInfo(
                $"[VERIFY_SERVICE] Initialized with {verificationCodeExpirationMinutes} minute expiration"
            );
        }

        /// <summary>
        /// Generate a new verification code for a player
        /// KORISTI: _db.AddPendingVerification() → VerificationPlayers.xml
        /// </summary>
        public string GenerateVerificationCode(
            long steamID,
            string playerName,
            string discordUsername,
            string gamePlayerName = null
        )
        {
            try
            {
                LoggerUtil.LogDebug(
                    $"[VERIFY_SERVICE] GenerateVerificationCode for {playerName} (SteamID: {steamID})"
                );

                // Check if player already has pending verification
                var existing = _db.GetPendingVerification(steamID);
                if (existing != null && existing.ExpiresAt > DateTime.UtcNow)
                {
                    LoggerUtil.LogWarning($"[VERIFY] {playerName}: Already has pending code");
                    return null; // Code still valid, don't generate new one
                }

                // Generate random code
                string code = GenerateRandomCode(CodeLength);

                // Use provided gamePlayerName or fallback to playerName
                string nameToStore = !string.IsNullOrEmpty(gamePlayerName)
                    ? gamePlayerName
                    : playerName;

                // NOVO: Save to VerificationPlayers.xml (NE u staru bazu!)
                _db.AddPendingVerification(
                    steamID,
                    discordUsername,
                    code,
                    _verificationCodeExpirationMinutes,
                    nameToStore
                );

                LoggerUtil.LogSuccess($"[VERIFY] Generated code for {playerName}: {code}");
                LoggerUtil.LogDebug(
                    $"[VERIFY] Saved to VerificationPlayers.xml (expires in {_verificationCodeExpirationMinutes} min)"
                );

                return code;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[VERIFY] Code generation failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// <summary>
        /// Verify the code from Discord bot.
        /// Uses VerificationPlayers.xml; logs event to VerificationData.xml (no duplicate).
        /// Returns result with SteamIdForNotify so in-game message can be sent (success or error).
        /// </summary>
        public Task<VerificationResult> VerifyAsync(string code, ulong discordId, string discordUsername)
        {
            try
            {
                LoggerUtil.LogDebug($"[VERIFY_SERVICE] VerifyAsync called with code: {code}");

                if (string.IsNullOrEmpty(code))
                {
                    LoggerUtil.LogWarning("[VERIFY] Empty verification code");
                    return Task.FromResult(new VerificationResult { Message = "Invalid verification code", IsSuccess = false });
                }

                var allPending = _db.GetAllPendingVerifications();
                var verification = allPending.Find(p => p.VerificationCode == code);

                if (verification == null)
                {
                    LoggerUtil.LogWarning($"[VERIFY] Code not found: {code}");
                    return Task.FromResult(new VerificationResult { Message = "Verification code not found!", IsSuccess = false });
                }

                if (verification.ExpiresAt < DateTime.UtcNow)
                {
                    LoggerUtil.LogWarning($"[VERIFY] Code expired: {code}");
                    long steamId = verification.SteamID;
                    string gameName = verification.GamePlayerName ?? steamId.ToString();
                    _db.DeletePendingVerification(verification.SteamID);
                    return Task.FromResult(new VerificationResult
                    {
                        Message = "Verification code has expired!",
                        IsSuccess = false,
                        SteamIdForNotify = steamId,
                        GamePlayerName = gameName
                    });
                }

                long steamID = verification.SteamID;
                string gamePlayerName = verification.GamePlayerName ?? steamID.ToString();
                _db.MarkAsVerified(steamID, discordUsername, discordId);

                _db.SaveVerificationHistory(new VerificationHistoryModel
                {
                    SteamID = steamID,
                    DiscordUsername = discordUsername,
                    DiscordUserID = discordId,
                    VerifiedAt = DateTime.UtcNow,
                    Status = "Success"
                });

                LoggerUtil.LogSuccess(
                    $"[VERIFY] Verified: SteamID {steamID} → Discord {discordUsername} (ID: {discordId})"
                );

                return Task.FromResult(new VerificationResult
                {
                    Message = "Verification successful!",
                    IsSuccess = true,
                    SteamIdForNotify = steamID,
                    GamePlayerName = gamePlayerName
                });
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[VERIFY] Verification failed: {ex.Message}");
                return Task.FromResult(new VerificationResult { Message = $"Verification error: {ex.Message}", IsSuccess = false });
            }
        }

        /// <summary>
        /// Check if player je već verificiran
        /// </summary>
        public bool IsPlayerVerified(long steamID)
        {
            try
            {
                var verified = _db.GetVerifiedPlayer(steamID);
                return verified != null;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[VERIFY] Error checking verification status: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if player has a pending verification code
        /// </summary>
        public bool HasPendingVerification(long steamID)
        {
            try
            {
                var pending = _db.GetPendingVerification(steamID);
                if (pending != null && pending.ExpiresAt > DateTime.UtcNow)
                {
                    LoggerUtil.LogDebug(
                        $"[VERIFY] Player {steamID} has pending verification (expires at {pending.ExpiresAt})"
                    );
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[VERIFY] Error checking pending verification: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get pending verification by Discord username (helper method)
        /// </summary>
        public PendingVerification GetPendingVerificationByDiscord(
            string discordUsername,
            long steamID
        )
        {
            try
            {
                var allPending = _db.GetAllPendingVerifications();
                return allPending.Find(p =>
                    p.SteamID == steamID && p.DiscordUsername == discordUsername
                );
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[VERIFY] Error getting pending verification: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Remove verification (admin command)
        /// </summary>
        public bool RemoveVerification(long steamID, string reason = "Admin removal")
        {
            try
            {
                _db.DeletePendingVerification(steamID);
                _db.DeleteVerifiedPlayer(steamID);
                LoggerUtil.LogInfo(
                    $"[VERIFY] Removed verification for SteamID {steamID}: {reason}"
                );
                return true;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[VERIFY] Remove verification failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Create a pending verification for a player (from in-game /tds verify command)
        /// Stores pending record in VerificationPlayers.xml
        /// </summary>
        public bool CreatePendingVerification(
            long steamID,
            string discordUsername,
            string gamePlayerName
        )
        {
            try
            {
                LoggerUtil.LogDebug(
                    $"[VERIFY] CreatePendingVerification: SteamID={steamID}, Discord={discordUsername}"
                );

                // Generate verification code
                string code = GenerateRandomCode(CodeLength);

                // Save to pending list
                _db.AddPendingVerification(
                    steamID,
                    discordUsername,
                    code,
                    _verificationCodeExpirationMinutes,
                    gamePlayerName
                );

                LoggerUtil.LogSuccess(
                    $"[VERIFY] Created pending verification - Code: {code}, Expires in {_verificationCodeExpirationMinutes} minutes"
                );
                return true;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[VERIFY] CreatePendingVerification failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Complete verification after Discord user submits the code
        /// Deletes pending record and permanently saves SteamId–DiscordId link in XML
        /// Must be called AFTER discord user confirms code
        /// </summary>
        public bool CompleteVerification(
            long steamID,
            string discordUsername,
            ulong discordUserId,
            string gamePlayerName = null
        )
        {
            try
            {
                LoggerUtil.LogDebug(
                    $"[VERIFY] CompleteVerification: SteamID={steamID}, DiscordID={discordUserId}"
                );

                // Get pending record to extract gamePlayerName if not provided
                var pending = _db.GetPendingVerification(steamID);
                if (pending == null)
                {
                    LoggerUtil.LogWarning(
                        $"[VERIFY] No pending verification found for SteamID {steamID}"
                    );
                    return false;
                }

                if (
                    string.IsNullOrEmpty(gamePlayerName)
                    && !string.IsNullOrEmpty(pending.GamePlayerName)
                )
                {
                    gamePlayerName = pending.GamePlayerName;
                }

                // Mark as verified (deletes pending, adds to verified list)
                _db.MarkAsVerified(steamID, discordUsername, discordUserId, gamePlayerName);

                LoggerUtil.LogSuccess(
                    $"[VERIFY] Completed verification - SteamID={steamID} linked to Discord {discordUsername} (ID: {discordUserId})"
                );
                LoggerUtil.LogDebug($"[VERIFY] Saved permanently to VerificationPlayers.xml");
                return true;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[VERIFY] CompleteVerification failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Generate random code sa slučajnim znakovima
        /// </summary>
        private string GenerateRandomCode(int length)
        {
            // const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var result = "";
            for (int i = 0; i < length; i++)
            {
                result += chars[_random.Next(chars.Length)];
            }
            return result;
        }
    }
}
