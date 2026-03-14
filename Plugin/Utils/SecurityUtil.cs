// Plugin/Utils/SecurityUtil.cs
using System;
using System.Collections.Generic;
using System.Linq;
using mamba.TorchDiscordSync.Plugin.Config;

namespace mamba.TorchDiscordSync.Plugin.Utils
{
    public static class SecurityUtil
    {
        // Accept long[] (MainConfig.AdminSteamIDs)
        public static bool IsPlayerAdmin(long steamId, long[] adminSteamIds)
        {
            if (adminSteamIds == null || adminSteamIds.Length == 0) 
                return false;
            
            for (int i = 0; i < adminSteamIds.Length; i++)
            {
                if (adminSteamIds[i] == steamId) 
                    return true;
            }
            return false;
        }

        // Backwards-compatible overload for List<string>
        public static bool IsPlayerAdmin(long steamId, List<string> adminSteamIds)
        {
            if (adminSteamIds == null || adminSteamIds.Count == 0) 
                return false;
            
            for (int i = 0; i < adminSteamIds.Count; i++)
            {
                long parsed = 0;
                if (long.TryParse(adminSteamIds[i], out parsed) && parsed == steamId)
                    return true;
            }
            return false;
        }

        public static bool IsPlayerAuthorized(long steamId, long[] adminSteamIds)
        {
            return IsPlayerAdmin(steamId, adminSteamIds);
        }

        public static bool IsPlayerAuthorized(long steamId, List<string> adminSteamIds)
        {
            return IsPlayerAdmin(steamId, adminSteamIds);
        }

        public static bool CanExecuteAdminCommand(long steamId, long[] adminSteamIds)
        {
            return IsPlayerAdmin(steamId, adminSteamIds);
        }

        public static bool CanExecuteAdminCommand(long steamId, List<string> adminSteamIds)
        {
            return IsPlayerAdmin(steamId, adminSteamIds);
        }

        public static string SanitizeMessage(string input, int maxLength = 2000)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "";

            // Uklanja opasne znakove za Discord
            var sanitized = input
                .Replace("@", "ᴀ")
                .Replace("<", "[")
                .Replace(">", "]")
                .Replace("```", "''");

            // Skrati ako je predugo
            if (sanitized.Length > maxLength)
                sanitized = sanitized.Substring(0, maxLength - 3) + "...";

            return sanitized;
        }

        public static bool IsValidSteamID(long steamID)
        {
            // Steam ID bi trebao biti između 76561198000000000 i 76561202255233023
            return steamID >= 76561198000000000 && steamID <= 76561202255233023;
        }

        public static bool ValidateCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return false;

            // Dozvoljeni znakovi: a-z, 0-9, space, /
            return command.All(c => char.IsLetterOrDigit(c) || c == ' ' || c == '/');
        }
    }
}
