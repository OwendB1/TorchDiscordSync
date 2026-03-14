// Plugin/Utils/CommandAuthorizationUtil.cs
using System;
using System.Collections.Generic;
using System.Linq;
using mamba.TorchDiscordSync.Plugin.Config;
using mamba.TorchDiscordSync.Plugin.Models;
using System.Threading.Tasks;

namespace mamba.TorchDiscordSync.Plugin.Utils
{
    /// <summary>
    /// Handles command authorization and help text generation
    /// </summary>
    public static class CommandAuthorizationUtil
    {
        /// <summary>
        /// Define all available commands
        /// </summary>
        public static List<CommandModel> GetAllCommands()
        {
            return new List<CommandModel>
            {
                // User commands (no admin required)
                new CommandModel(
                    "verify",
                    "Link your Discord account to Space Engineers character",
                    "/tds verify @DiscordName",
                    requiresAdmin: false,
                    minArgs: 1
                ),

                new CommandModel(
                    "status",
                    "Display current plugin and server status",
                    "/tds status",
                    requiresAdmin: false,
                    minArgs: 0
                ),

                new CommandModel(
                    "help",
                    "Show available commands for your authorization level",
                    "/tds help",
                    requiresAdmin: false,
                    minArgs: 0
                ),

                // Admin commands
                new CommandModel(
                    "sync",
                    "Force immediate faction and player synchronization",
                    "/tds sync",
                    requiresAdmin: true,
                    minArgs: 0
                ),

                new CommandModel(
                    "reset",
                    "Clear all Discord roles and channels (WARNING: irreversible)",
                    "/tds reset",
                    requiresAdmin: true,
                    minArgs: 0
                ),
                
                new CommandModel(
                    "cleanup",
                    "Remove orphaned Discord roles and channels",
                    "/tds cleanup",
                    requiresAdmin: true,
                    minArgs: 0
                ),

                new CommandModel(
                    "reload",
                    "Reload configuration and database",
                    "/tds reload",
                    requiresAdmin: true,
                    minArgs: 0
                ),

                new CommandModel(
                    "unverify",
                    "Remove verification link for a player (by Steam ID)",
                    "/tds unverify STEAMID [reason]",
                    requiresAdmin: true,
                    minArgs: 1
                )
            };
        }

        /// <summary>
        /// Check if command exists and validate authorization
        /// Returns null if command doesn't exist or user is not authorized
        /// </summary>
        public static CommandModel ValidateCommand(string commandName, long playerSteamID,
            MainConfig config)
        {
            var command = GetAllCommands().FirstOrDefault(c => c.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase));

            if (command == null)
                return null;

            if (command.RequiresAdmin)
            {
                if (!SecurityUtil.IsPlayerAdmin(playerSteamID, MainConfig.Load().AdminSteamIDs))
                    return null;
            }

            return command;
        }

        /// <summary>
        /// Get all available commands for a user based on authorization level
        /// </summary>
        public static List<CommandModel> GetAvailableCommands(long playerSteamID, MainConfig config)
        {
            var allCommands = GetAllCommands();
            bool isAdmin = SecurityUtil.IsPlayerAdmin(playerSteamID, MainConfig.Load().AdminSteamIDs);

            var result = new List<CommandModel>();
            for (int i = 0; i < allCommands.Count; i++)
            {
                if (!allCommands[i].RequiresAdmin || isAdmin)
                {
                    result.Add(allCommands[i]);
                }
            }
            return result;
        }

        /// <summary>
        /// Generate help text based on user authorization
        /// </summary>
        public static string GenerateHelpText(long playerSteamID, MainConfig config)
        {
            var availableCommands = GetAvailableCommands(playerSteamID, config);
            bool isAdmin = SecurityUtil.IsPlayerAdmin(playerSteamID, MainConfig.Load().AdminSteamIDs);

            var helpLines = new List<string>();

            helpLines.Add("");
            helpLines.Add("╔════════════════════════════════════════════════════╗");
            helpLines.Add("║ " + VersionUtil.GetPluginName() + " " + VersionUtil.GetVersionString() + " - Command Help ║");
            helpLines.Add("╚════════════════════════════════════════════════════╝");
            helpLines.Add("");

            var userCommands = new List<CommandModel>();
            var adminCommands = new List<CommandModel>();
            
            for (int i = 0; i < availableCommands.Count; i++)
            {
                if (!availableCommands[i].RequiresAdmin)
                    userCommands.Add(availableCommands[i]);
                else
                    adminCommands.Add(availableCommands[i]);
            }

            if (userCommands.Count > 0)
            {
                helpLines.Add("PUBLIC COMMANDS:");
                helpLines.Add("");
                for (int i = 0; i < userCommands.Count; i++)
                {
                    helpLines.Add("  " + userCommands[i].GetHelpText());
                }
                helpLines.Add("");
            }

            if (isAdmin && adminCommands.Count > 0)
            {
                helpLines.Add("ADMIN COMMANDS (Restricted):");
                helpLines.Add("");
                for (int i = 0; i < adminCommands.Count; i++)
                {
                    helpLines.Add("  " + adminCommands[i].GetHelpText());
                }
                helpLines.Add("");
            }

            if (!isAdmin)
            {
                helpLines.Add("For more commands, contact a server administrator");
            }

            helpLines.Add("");

            return string.Join("\n", helpLines);
        }

        public static bool IsUserAdmin(long steamId, long[] adminSteamIds)
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

        public static bool IsUserAdmin(long steamId, List<string> adminSteamIdsAsStrings)
        {
            if (adminSteamIdsAsStrings == null || adminSteamIdsAsStrings.Count == 0) 
                return false;
            
            for (int i = 0; i < adminSteamIdsAsStrings.Count; i++)
            {
                long parsed = 0;
                if (long.TryParse(adminSteamIdsAsStrings[i], out parsed) && parsed == steamId)
                    return true;
            }
            return false;
        }

        public static bool IsUserAdminFromConfig(long steamId)
        {
            MainConfig cfg = MainConfig.Load();
            if (cfg == null || cfg.AdminSteamIDs == null) 
                return false;
            return IsUserAdmin(steamId, cfg.AdminSteamIDs);
        }

        public static bool ValidateCommand(string cmd, long steamId, long[] adminSteamIds)
        {
            if (string.IsNullOrEmpty(cmd)) 
                return false;
            return IsUserAdmin(steamId, adminSteamIds);
        }

        public static bool ValidateCommand(string cmd, long steamId, List<string> adminSteamIds)
        {
            if (string.IsNullOrEmpty(cmd)) 
                return false;
            return IsUserAdmin(steamId, adminSteamIds);
        }

        public static CommandModel ParseCommand(string input, long steamId, long[] adminSteamIds)
        {
            CommandModel model = new CommandModel();
            if (string.IsNullOrEmpty(input)) 
                return model;
            
            model.IsAuthorized = IsUserAdmin(steamId, adminSteamIds);
            return model;
        }

        public static CommandModel ParseCommand(string input, long steamId, List<string> adminSteamIds)
        {
            CommandModel model = new CommandModel();
            if (string.IsNullOrEmpty(input)) 
                return model;
            
            model.IsAuthorized = IsUserAdmin(steamId, adminSteamIds);
            return model;
        }

        public static bool CheckAuthorization(string command, long steamId, long[] admins)
        {
            return IsUserAdmin(steamId, admins);
        }

        public static bool CheckAuthorization(string command, long steamId, List<string> admins)
        {
            return IsUserAdmin(steamId, admins);
        }

        public static CommandModel AuthorizeCommand(CommandModel cmd, long steamId, long[] admins)
        {
            if (cmd == null) cmd = new CommandModel();
            cmd.IsAuthorized = IsUserAdmin(steamId, admins);
            return cmd;
        }

        public static CommandModel AuthorizeCommand(CommandModel cmd, long steamId, List<string> admins)
        {
            if (cmd == null) cmd = new CommandModel();
            cmd.IsAuthorized = IsUserAdmin(steamId, admins);
            return cmd;
        }

        public static bool ValidateAdminAction(string action, long steamId, long[] adminSteamIds)
        {
            return ValidateCommand(action, steamId, adminSteamIds);
        }

        public static bool ValidateAdminAction(string action, long steamId, List<string> adminSteamIds)
        {
            return ValidateCommand(action, steamId, adminSteamIds);
        }
    }
}
