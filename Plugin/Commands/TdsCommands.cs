using System.Linq;
using TorchDiscordSync;
using TorchDiscordSync.Plugin.Services;
using Torch.Commands;
using Torch.Commands.Permissions;
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

        [Command("tds verify", "Start Discord verification")]
        [Permission(MyPromoteLevel.None)]
        public void Verify()
        {
            string identity = Context.Args.FirstOrDefault();
            Plugin.CommandService.StartVerification(CreateRequest(), identity);
        }

        [Command("tds verify status", "Show your verification status")]
        [Permission(MyPromoteLevel.None)]
        public void VerifyStatus()
        {
            Plugin.CommandService.ShowVerificationStatus(CreateRequest());
        }

        [Command("tds verify delete", "Delete your pending verification")]
        [Permission(MyPromoteLevel.None)]
        public void VerifyDelete()
        {
            Plugin.CommandService.DeletePendingVerification(CreateRequest());
        }

        [Command("tds verify help", "Show verification help")]
        [Permission(MyPromoteLevel.None)]
        public void VerifyHelp()
        {
            Plugin.CommandService.ShowVerificationHelp(CreateRequest());
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

        [Command("tds admin unverify", "Remove a verification")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminUnverify()
        {
            string steamId = Context.Args.Count > 0 ? Context.Args[0] : null;
            string reason = Context.Args.Count > 1 ? string.Join(" ", Context.Args.Skip(1)) : "Admin removal";
            Plugin.CommandService.RunUnverify(CreateRequest(), steamId, reason);
        }

        [Command("tds admin verify list", "List verified users")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminVerifyList()
        {
            Plugin.CommandService.ListVerifiedUsers(CreateRequest());
        }

        [Command("tds admin verify pending", "List pending verifications")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminVerifyPending()
        {
            Plugin.CommandService.ListPendingVerifications(CreateRequest());
        }

        [Command("tds admin verify delete", "Delete a verification record")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminVerifyDelete()
        {
            string steamId = Context.Args.FirstOrDefault();
            Plugin.CommandService.DeleteVerificationRecord(CreateRequest(), steamId);
        }

        private TdsCommandRequest CreateRequest()
        {
            bool isAdmin = Context.Player == null || Context.Player.PromoteLevel >= MyPromoteLevel.Admin;
            long steamId = Context.Player != null ? (long)Context.Player.SteamUserId : 0;
            string playerName = Context.Player?.DisplayName ?? "Server";

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
