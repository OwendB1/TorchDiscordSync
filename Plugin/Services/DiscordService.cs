using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TorchDiscordSync.Plugin.Utils;
using TorchDiscordSync.Shared.Ipc;

namespace TorchDiscordSync.Plugin.Services
{
    /// <summary>
    /// Thin adapter around the named-pipe Discord host client.
    /// Exposes Discord operations to the rest of the Torch plugin without
    /// referencing Discord.Net types.
    /// </summary>
    public class DiscordService
    {
        private const int DISCORD_MESSAGE_MAX_LENGTH = 2000;

        private readonly DiscordBotService _botService;
        public DiscordService(DiscordBotService botService)
        {
            _botService = botService;
        }

        public bool IsConnected
        {
            get { return _botService != null && _botService.IsConnected; }
        }

        public bool IsReady
        {
            get { return _botService != null && _botService.IsReady; }
        }

        public Task<bool> StartAsync()
        {
            return _botService != null ? _botService.StartAsync() : Task.FromResult(false);
        }

        public Task<bool> StopAsync()
        {
            return _botService != null ? _botService.StopAsync() : Task.FromResult(true);
        }

        public Task<bool> UpdateConfigurationAsync(DiscordRuntimeConfig config)
        {
            return _botService != null
                ? _botService.UpdateConfigurationAsync(config)
                : Task.FromResult(false);
        }

        public Task<DiscordConnectionState> GetConnectionStateAsync()
        {
            return _botService != null
                ? _botService.GetConnectionStateAsync()
                : Task.FromResult(new DiscordConnectionState());
        }

        public async Task<bool> SendLogAsync(ulong channelID, string message)
        {
            try
            {
                if (channelID == 0 || _botService == null)
                    return false;

                bool allSent = true;
                foreach (string chunk in SplitDiscordMessage(message))
                {
                    bool sent = await _botService.SendChannelMessageAsync(channelID, chunk)
                        .ConfigureAwait(false);
                    allSent = allSent && sent;
                    if (!sent)
                        break;
                }

                return allSent;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD] Send log error: " + ex.Message);
                return false;
            }
        }

        private static IEnumerable<string> SplitDiscordMessage(string message)
        {
            if (string.IsNullOrEmpty(message) || message.Length <= DISCORD_MESSAGE_MAX_LENGTH)
            {
                yield return message ?? string.Empty;
                yield break;
            }

            for (int index = 0; index < message.Length; index += DISCORD_MESSAGE_MAX_LENGTH)
            {
                int length = Math.Min(DISCORD_MESSAGE_MAX_LENGTH, message.Length - index);
                yield return message.Substring(index, length);
            }
        }

        public async Task<bool> SendEmbedAsync(ulong channelId, DiscordEmbedModel embed)
        {
            try
            {
                if (channelId == 0 || _botService == null)
                    return false;

                return await _botService.SendEmbedAsync(channelId, embed).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD] Send embed error: " + ex.Message);
                return false;
            }
        }

        public async Task<ulong> CreateRoleAsync(string roleName)
        {
            try
            {
                return _botService != null
                    ? await _botService.CreateRoleAsync(roleName).ConfigureAwait(false)
                    : 0;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD] Create role error: " + ex.Message);
                return 0;
            }
        }

        public async Task<ulong> CreateChannelAsync(
            string channelName,
            ulong? factionCategoryId = null,
            ulong? roleID = null)
        {
            try
            {
                if (_botService == null)
                    return 0;

                return await _botService.CreateChannelAsync(
                        channelName,
                        DiscordChannelKind.Text,
                        factionCategoryId,
                        roleID)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD] Create channel error: " + ex.Message);
                return 0;
            }
        }

        public async Task<ulong> CreateVoiceChannelAsync(
            string channelName,
            ulong? categoryID = null,
            ulong? roleID = null)
        {
            try
            {
                if (_botService == null)
                    return 0;

                return await _botService.CreateChannelAsync(
                        channelName,
                        DiscordChannelKind.Voice,
                        categoryID,
                        roleID)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD] Create voice channel error: " + ex.Message);
                return 0;
            }
        }

        public async Task<bool> DeleteRoleAsync(ulong roleID)
        {
            try
            {
                return _botService != null
                    ? await _botService.DeleteRoleAsync(roleID).ConfigureAwait(false)
                    : false;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD] Delete role error: " + ex.Message);
                return false;
            }
        }

        public async Task<bool> DeleteChannelAsync(ulong channelID)
        {
            try
            {
                return _botService != null
                    ? await _botService.DeleteChannelAsync(channelID).ConfigureAwait(false)
                    : false;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD] Delete channel error: " + ex.Message);
                return false;
            }
        }

        public DiscordRoleInfo GetExistingRole(ulong roleId)
        {
            try
            {
                return _botService != null
                    ? _botService.GetRoleInfoAsync(roleId).GetAwaiter().GetResult()
                    : null;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogWarning("[DISCORD] Error checking role: " + ex.Message);
                return null;
            }
        }

        public async Task<DiscordChannelInfo> GetExistingChannelAsync(ulong channelId)
        {
            try
            {
                return _botService != null
                    ? await _botService.GetChannelInfoAsync(channelId, DiscordChannelKind.Text)
                        .ConfigureAwait(false)
                    : null;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogWarning("[DISCORD] Error checking channel: " + ex.Message);
                return null;
            }
        }

        public DiscordChannelInfo GetExistingChannel(ulong channelId)
        {
            try
            {
                return GetExistingChannelAsync(channelId).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                LoggerUtil.LogWarning("[DISCORD] Error checking channel (sync): " + ex.Message);
                return null;
            }
        }

        public DiscordChannelInfo GetExistingVoiceChannel(ulong channelId)
        {
            try
            {
                return _botService != null
                    ? _botService.GetChannelInfoAsync(channelId, DiscordChannelKind.Voice)
                        .GetAwaiter()
                        .GetResult()
                    : null;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogWarning("[DISCORD] Error checking voice channel: " + ex.Message);
                return null;
            }
        }

        public async Task<bool> AssignRoleToUserAsync(ulong userId, ulong roleId)
        {
            try
            {
                return _botService != null
                    ? await _botService.AssignRoleAsync(userId, roleId).ConfigureAwait(false)
                    : false;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD] Assign role error: " + ex.Message);
                return false;
            }
        }

        public async Task<bool> SyncRoleMembersAsync(
            ulong roleId,
            IEnumerable<ulong> desiredUserIds,
            string roleName)
        {
            try
            {
                return _botService != null
                    ? await _botService.SyncRoleMembersAsync(roleId, desiredUserIds, roleName)
                        .ConfigureAwait(false)
                    : false;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD] Sync role members error: " + ex.Message);
                return false;
            }
        }

        public async Task<bool> UpdateChannelNameAsync(ulong channelId, string newName)
        {
            try
            {
                return _botService != null
                    ? await _botService.UpdateChannelNameAsync(channelId, newName)
                        .ConfigureAwait(false)
                    : false;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD] Update channel name error: " + ex.Message);
                return false;
            }
        }

        public async Task<bool> UpdatePresenceAsync(string statusText)
        {
            try
            {
                return _botService != null
                    ? await _botService.UpdatePresenceAsync(statusText).ConfigureAwait(false)
                    : false;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD] Update presence error: " + ex.Message);
                return false;
            }
        }

        public ulong FindRoleByName(string name)
        {
            try
            {
                return _botService != null
                    ? _botService.FindRoleByNameAsync(name).GetAwaiter().GetResult()
                    : 0;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogDebug("[DISCORD] FindRoleByName error: " + ex.Message);
                return 0;
            }
        }

        public ulong FindTextChannelByName(string name)
        {
            try
            {
                return _botService != null
                    ? _botService.FindChannelByNameAsync(name, DiscordChannelKind.Text)
                        .GetAwaiter()
                        .GetResult()
                    : 0;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogDebug("[DISCORD] FindTextChannelByName error: " + ex.Message);
                return 0;
            }
        }

        public ulong FindVoiceChannelByName(string name)
        {
            try
            {
                return _botService != null
                    ? _botService.FindChannelByNameAsync(name, DiscordChannelKind.Voice)
                        .GetAwaiter()
                        .GetResult()
                    : 0;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogDebug("[DISCORD] FindVoiceChannelByName error: " + ex.Message);
                return 0;
            }
        }

    }
}
