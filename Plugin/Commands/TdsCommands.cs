using System.Linq;
using Torch.Commands;
using Torch.Commands.Permissions;
using TorchDiscordSync;
using TorchDiscordSync.Plugin.Services;
using VRage.Game.ModAPI;

namespace TorchDiscordSync.Plugin.Commands
{
    public class TdsCommands : CommandModule
    {
        private TorchDiscordSyncPlugin Plugin => (TorchDiscordSyncPlugin)Context.Plugin;

        [Command("tds", "Show Torch Discord Sync help")]
        [Permission(MyPromoteLevel.None)]
        public void Root()
        {
            Plugin.CommandService.ShowHelp(CreateRequest());
        }

        [Command("tds help", "Show Torch Discord Sync help")]
        [Permission(MyPromoteLevel.None)]
        public void Help()
        {
            Plugin.CommandService.ShowHelp(CreateRequest());
        }

        [Command("tds status", "Show Torch Discord Sync status")]
        [Permission(MyPromoteLevel.None)]
        public void Status()
        {
            Plugin.CommandService.ShowStatus(CreateRequest());
        }

        [Command("tds admin sync", "Synchronize factions to Discord")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminSync()
        {
            Plugin.CommandService.RunFullSync(CreateRequest());
        }

        [Command("tds admin sync check", "Check sync state")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminSyncCheck()
        {
            Plugin.CommandService.RunAdminSyncCheck(CreateRequest());
        }

        [Command("tds admin sync status", "Show sync status")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminSyncStatus()
        {
            Plugin.CommandService.RunAdminSyncStatus(CreateRequest());
        }

        [Command("tds admin sync undo", "Undo sync for one faction")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminSyncUndo()
        {
            Plugin.CommandService.RunAdminSyncUndo(CreateRequest(), Context.Args.FirstOrDefault());
        }

        [Command("tds admin sync undoall", "Undo sync for all factions")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminSyncUndoAll()
        {
            Plugin.CommandService.RunAdminSyncUndoAll(CreateRequest());
        }

        [Command("tds admin sync cleanup", "Clean up orphaned Discord objects")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminSyncCleanup()
        {
            Plugin.CommandService.RunAdminSyncCleanup(CreateRequest());
        }

        [Command("tds admin reset", "Reset Discord faction data")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminReset()
        {
            Plugin.CommandService.RunReset(CreateRequest());
        }

        [Command("tds admin cleanup", "Run Discord cleanup")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminCleanup()
        {
            Plugin.CommandService.RunCleanup(CreateRequest());
        }

        [Command("tds admin reload", "Reload plugin configuration")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminReload()
        {
            Plugin.CommandService.RunReload(CreateRequest());
        }

        private TdsCommandRequest CreateRequest()
        {
            var isAdmin = Context.Player == null || Context.Player.PromoteLevel >= MyPromoteLevel.Admin;
            var steamId = Context.Player != null ? (long)Context.Player.SteamUserId : 0;
            var playerName = Context.Player?.DisplayName ?? "Server";

            return new TdsCommandRequest
            {
                IsAdmin = isAdmin,
                PlayerName = playerName,
                SteamId = steamId,
                Respond = message => Context.Respond(message),
            };
        }
    }
}
