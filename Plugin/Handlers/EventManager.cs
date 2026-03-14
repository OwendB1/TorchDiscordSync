// Plugin/Handlers/EventManager.cs
using System;
using System.Threading.Tasks;
using mamba.TorchDiscordSync.Plugin.Config;
using mamba.TorchDiscordSync.Plugin.Services;
using mamba.TorchDiscordSync.Plugin.Utils;
using Sandbox.Game.Entities.Character.Components; // MyDamageInformation
using Sandbox.ModAPI; // MyAPIGateway
using VRage.Game.ModAPI; // Za dodatne API pozive

namespace mamba.TorchDiscordSync.Plugin.Handlers
{
    public class EventManager
    {
        private readonly MainConfig _config;
        private readonly DiscordService _discordService;
        private readonly EventLoggingService _eventLog;
        private bool _serverShutdownSent = false;

        public EventManager(MainConfig config, DiscordService discordService, EventLoggingService eventLog)
        {
            _config = config;
            _discordService = discordService;
            _eventLog = eventLog;
        }

        public EventManager(MainConfig config, DiscordService discordService)
        {
            _config = config;
            _discordService = discordService;
        }

        // Register player connect/disconnect events - TORCH WAY
        // ZAKOMENTIRANO JER NE POSTOJE PlayerConnected/PlayerDisconnected EVENTOVI
        /*
        public void RegisterEvents()
        {
            try
            {
                // OVO NE POSTOJI U NOVIJIM VERZIJAMA TORCHA
                // MyAPIGateway.Players.PlayerConnected += OnPlayerConnected;
                // MyAPIGateway.Players.PlayerDisconnected += OnPlayerDisconnected;
                
                LoggerUtil.LogInfo("Player connection events would be registered if they existed");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Failed to register player events: " + ex.Message);
            }
        }
        */

        // Player connected event handler - ZAKOMENTIRANO
        /*
        private void OnPlayerConnected(long playerId)
        {
            Task.Run(() => ProcessPlayerConnected(playerId));
        }
        */

        // Player disconnected event handler - ZAKOMENTIRANO
        /*
        private void OnPlayerDisconnected(long playerId)
        {
            Task.Run(() => ProcessPlayerDisconnected(playerId));
        }
        */

        // Register death events - different approach for newer Torch
        // ZAKOMENTIRANO - koristimo PlayerTrackingService i DeathLogService
        /*
        public void RegisterDeathEvents()
        {
            try
            {
                // Death events require different handling in newer Torch versions
                // We can monitor via character components or session events
                LoggerUtil.LogInfo("Death event monitoring initialized");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Failed to register death events: " + ex.Message);
            }
        }
        */

        // Process player connected - ASYNC - ZAKOMENTIRANO
        /*
        private async Task ProcessPlayerConnected(long playerId)
        {
            try
            {
                if (_config.Chat != null && !string.IsNullOrEmpty(_config.Chat.JoinMessage))
                {
                    var player = GetPlayerBySteamId(playerId);
                    if (player != null)
                    {
                        string playerName = player.DisplayName ?? "Unknown";
                        string message = _config.Chat.JoinMessage.Replace("{p}", playerName);
                        
                        // Send to Discord if enabled
                        if (_config.Chat.ServerToDiscord && _discordService != null)
                        {
                            await SendDiscordMessageAsync(message);
                        }
                        
                        LoggerUtil.LogInfo("Player joined: " + playerName);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Error in ProcessPlayerConnected: " + ex.Message);
            }
        }
        */

        // Process player disconnected - ASYNC - ZAKOMENTIRANO
        /*
        private async Task ProcessPlayerDisconnected(long playerId)
        {
            try
            {
                if (_config.Chat != null && !string.IsNullOrEmpty(_config.Chat.LeaveMessage))
                {
                    var player = GetPlayerBySteamId(playerId);
                    if (player != null)
                    {
                        string playerName = player.DisplayName ?? "Unknown";
                        string message = _config.Chat.LeaveMessage.Replace("{p}", playerName);
                        
                        // Send to Discord if enabled
                        if (_config.Chat.ServerToDiscord && _discordService != null)
                        {
                            await SendDiscordMessageAsync(message);
                        }
                        
                        LoggerUtil.LogInfo("Player left: " + playerName);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Error in ProcessPlayerDisconnected: " + ex.Message);
            }
        }
        */

        // Helper to get player by Steam ID (Torch way)
        private IMyPlayer GetPlayerBySteamId(long steamId)
        {
            try
            {
                var players = new System.Collections.Generic.List<IMyPlayer>();
                MyAPIGateway.Players.GetPlayers(players);

                foreach (var player in players)
                {
                    if ((long)player.SteamUserId == steamId)
                        return player;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        // Build detailed death message based on damage information
        private string BuildDeathMessage(string playerName, MyDamageInformation damageInfo)
        {
            try
            {
                string cause = "unknown";

                // Determine death cause based on damage type and attacker
                if (damageInfo.AttackerId == 0)
                {
                    // Environmental death
                    if (damageInfo.Amount > 0)
                    {
                        cause = damageInfo.Type.ToString().ToLower();
                    }
                    else
                    {
                        cause = "environment";
                    }
                }
                else
                {
                    // Check if attacker is a player
                    var attackerPlayer = GetPlayerBySteamId((long)damageInfo.AttackerId);
                    if (attackerPlayer != null)
                    {
                        if ((long)attackerPlayer.SteamUserId == (long)GetPlayerByName(playerName)?.SteamUserId)
                        {
                            cause = "suicide";
                        }
                        else
                        {
                            cause = "killed by " + attackerPlayer.DisplayName;
                        }
                    }
                    else
                    {
                        // Could be grid, NPC, or other entity
                        cause = "killed by entity #" + damageInfo.AttackerId;
                    }
                }

                return playerName + " was killed by " + cause + " (" + damageInfo.Type + ")";
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Error building death message: " + ex.Message);
                return playerName + " died";
            }
        }

        // Helper to get player by name
        private IMyPlayer GetPlayerByName(string playerName)
        {
            try
            {
                var players = new System.Collections.Generic.List<IMyPlayer>();
                MyAPIGateway.Players.GetPlayers(players);

                foreach (var player in players)
                {
                    if (player.DisplayName.Equals(playerName, StringComparison.OrdinalIgnoreCase))
                        return player;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        // Async helper method using existing SendLogAsync
        private async Task SendDiscordMessageAsync(string message)
        {
            try
            {
                // Use status channel or chat channel from config
                ulong channelId = 0;
                if (_config.Discord != null)
                {
                    channelId = _config.Discord.StatusChannelId != 0 ?
                               _config.Discord.StatusChannelId :
                               _config.Discord.ChatChannelId;
                }

                if (channelId != 0 && _discordService != null)
                {
                    await _discordService.SendLogAsync(channelId, message);
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Failed to send Discord message: " + ex.Message);
            }
        }

        // Send server startup message using config values - ASYNC
        public void SendServerStartupMessage()
        {
            try
            {
                if (_config.Monitoring != null &&
                    _config.Monitoring.Enabled &&
                    !string.IsNullOrEmpty(_config.Monitoring.ServerStartedMessage) &&
                    _discordService != null)
                {
                    Task.Run(async () =>
                    {
                        await SendDiscordMessageAsync(_config.Monitoring.ServerStartedMessage);
                    });
                    LoggerUtil.LogInfo("Server startup message sent: " + _config.Monitoring.ServerStartedMessage);
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Failed to send server startup message: " + ex.Message);
            }
        }

        // Send server shutdown message using config values - ASYNC
        public void SendServerShutdownMessage()
        {
            try
            {
                // Prevent duplicate messages
                if (_serverShutdownSent)
                    return;

                if (_config.Monitoring != null &&
                    _config.Monitoring.Enabled &&
                    !string.IsNullOrEmpty(_config.Monitoring.ServerStoppedMessage) &&
                    _discordService != null)
                {
                    Task.Run(async () =>
                    {
                        await SendDiscordMessageAsync(_config.Monitoring.ServerStoppedMessage);
                    });
                    _serverShutdownSent = true;
                    LoggerUtil.LogInfo("Server shutdown message sent: " + _config.Monitoring.ServerStoppedMessage);
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Failed to send server shutdown message: " + ex.Message);
            }
        }
    }
}
