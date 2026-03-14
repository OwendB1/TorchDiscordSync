// Plugin/Services/EventLoggingService.cs
using System;
using System.Threading.Tasks;
using mamba.TorchDiscordSync.Plugin.Config;
using mamba.TorchDiscordSync.Plugin.Models;
using mamba.TorchDiscordSync.Plugin.Utils;

namespace mamba.TorchDiscordSync.Plugin.Services
{
    public class EventLoggingService
    {
        private readonly DatabaseService _db;
        private readonly DiscordService _discord;
        private readonly MainConfig _config;

        public EventLoggingService(DatabaseService db, DiscordService discord, MainConfig config)
        {
            _db = db;
            _discord = discord;
            _config = config;
        }

        public Task LogAsync(string eventType, string details)
        {
            try
            {
                var evt = new EventLogModel
                {
                    EventType = eventType,
                    Details = details,
                    Timestamp = DateTime.UtcNow,
                };

                if (_db != null)
                {
                    _db.LogEvent(evt);
                }

                if (_config != null && _config.Discord != null && _config.Discord.StaffLog != 0)
                {
                    if (_discord != null)
                    {
                        return _discord.SendLogAsync(
                            _config.Discord.StaffLog,
                            "[" + eventType + "] " + details
                        );
                    }
                }

                if (_config != null && _config.Debug)
                {
                    LoggerUtil.LogDebug("Event logged: " + eventType + " - " + details);
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Event logging error: " + ex.Message);
            }

            return Task.FromResult(0);
        }

        /// <summary>
        /// Log server status changes (started, stopped, sim speed) to Discord and database
        /// </summary>
        /// <param name="status"></param>
        /// <param name="simSpeed"></param>
        /// <returns></returns>
        public Task LogServerStatusAsync(string status, float simSpeed)
        {
            try
            {
                string message = "";

                // Use custom messages from config if available
                if (_config != null && _config.Monitoring != null)
                {
                    switch (status.ToUpper())
                    {
                        case "STARTED":
                            message =
                                _config.Monitoring.ServerStartedMessage
                                ?? ":white_check_mark: Server Started!";
                            break;
                        case "STOPPED":
                            message =
                                _config.Monitoring.ServerStoppedMessage ?? ":x: Server Stopped!";
                            break;
                        default:
                            message =
                                "Server " + status + " | SimSpeed: " + simSpeed.ToString("F2");
                            break;
                    }
                }
                else
                {
                    message = "Server " + status + " | SimSpeed: " + simSpeed.ToString("F2");
                }

                // Send to ChatChannelId instead of StatusChannelId (as per your preference)
                if (
                    _config != null
                    && _config.Discord != null
                    && _config.Discord.ChatChannelId != 0
                )
                {
                    if (_discord != null)
                    {
                        return _discord.SendLogAsync(_config.Discord.ChatChannelId, message);
                    }
                }

                return LogAsync(
                    "ServerStatus",
                    status + " (SimSpeed: " + simSpeed.ToString("F2") + ")"
                );
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Server status logging error: " + ex.Message);
            }

            return Task.FromResult(0);
        }

        /// Log SimSpeed warnings to Discord and database
        public Task LogSimSpeedWarningAsync(float simSpeed)
        {
            try
            {
                string threshold =
                    _config != null && _config.Monitoring != null
                        ? _config.Monitoring.SimSpeedThreshold.ToString("F2")
                        : "0.60";
                string message =
                    "SIMSPEED ALERT - Current: "
                    + simSpeed.ToString("F2")
                    + " (Threshold: "
                    + threshold
                    + ")";

                if (
                    _config != null
                    && _config.Discord != null
                    && _config.Discord.StatusChannelId != 0
                )
                {
                    if (_discord != null)
                    {
                        return _discord.SendLogAsync(_config.Discord.StatusChannelId, message);
                    }
                }

                return LogAsync(
                    "SimSpeedWarning",
                    "SimSpeed below threshold: " + simSpeed.ToString("F2")
                );
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("SimSpeed warning error: " + ex.Message);
            }

            return Task.FromResult(0);
        }

        /// Log player deaths to Discord and database
        public Task LogDeathAsync(string deathMessage)
        {
            try
            {
                // Send to ChatChannelId (global channel)
                if (_config?.Discord?.ChatChannelId != 0 && _discord != null)
                {
                    _ = _discord.SendLogAsync(_config.Discord.ChatChannelId, deathMessage);
                }

                // Also log to database
                if (_db != null)
                {
                    var evt = new EventLogModel
                    {
                        EventType = "Death",
                        Details = deathMessage,
                        Timestamp = DateTime.UtcNow,
                    };
                    _db.LogEvent(evt);
                }

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Death logging error: " + ex.Message);
                return Task.CompletedTask;
            }
        }

        public Task LogPlayerJoinAsync(string playerName, ulong steamID)
        {
            try
            {
                string template = _config.Chat.JoinMessage ?? ":sunny: {p} joined the server";
                string message = template.Replace("{p}", playerName);

                // ili sa SteamID
                // if (steamID != 0 && !_config.Privacy.HideSteamId) message += " (" + steamID + ")";

                // SEND TO GAME CHAT FIRST (from configuration)
                try
                {
                    // Remove Discord formatting for game chat
                    string gameMessage = message.Replace(":sunny:", "").Trim();
                    Sandbox.Game.MyVisualScriptLogicProvider.SendChatMessage(
                        gameMessage,
                        "Server",
                        0,
                        "Yellow"
                    );
                    LoggerUtil.LogInfo($"[PLAYER_JOIN] In-game message: {gameMessage}");
                }
                catch (Exception exGame)
                {
                    LoggerUtil.LogError($"Failed to send join message to game: {exGame.Message}");
                }

                // THEN SEND TO DISCORD
                if (
                    _config != null
                    && _config.Discord != null
                    && _config.Discord.ChatChannelId != 0
                )
                {
                    if (_discord != null)
                    {
                        return _discord.SendLogAsync(_config.Discord.ChatChannelId, message);
                    }
                }

                return LogAsync("PlayerJoin", playerName + " (" + steamID + ")");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Player join logging error: " + ex.Message);
            }

            return Task.FromResult(0);
        }

        public Task LogPlayerLeaveAsync(string playerName, ulong steamID)
        {
            try
            {
                string template = _config.Chat.LeaveMessage ?? ":sunny: {p} left the server";
                string message = template.Replace("{p}", playerName);
                // string message = playerName + " (" + steamID + ") left the server";

                // SEND TO GAME CHAT FIRST (from configuration)
                try
                {
                    // Remove Discord formatting for game chat
                    string gameMessage = message.Replace(":sunny:", "").Trim();
                    Sandbox.Game.MyVisualScriptLogicProvider.SendChatMessage(
                        gameMessage,
                        "Server",
                        0,
                        "Yellow"
                    );
                    LoggerUtil.LogInfo($"[PLAYER_LEAVE] In-game message: {gameMessage}");
                }
                catch (Exception exGame)
                {
                    LoggerUtil.LogError($"Failed to send leave message to game: {exGame.Message}");
                }

                // THEN SEND TO DISCORD
                if (
                    _config != null
                    && _config.Discord != null
                    && _config.Discord.ChatChannelId != 0
                )
                {
                    if (_discord != null)
                    {
                        return _discord.SendLogAsync(_config.Discord.ChatChannelId, message);
                    }
                }

                return LogAsync("PlayerLeave", playerName + " (" + steamID + ")");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Player leave logging error: " + ex.Message);
            }

            return Task.FromResult(0);
        }

        public Task LogSyncCompleteAsync(int factionsCount, int playersCount)
        {
            try
            {
                string message =
                    "Sync Complete - Factions: " + factionsCount + ", Players: " + playersCount;

                if (_config != null && _config.Discord != null && _config.Discord.StaffLog != 0)
                {
                    if (_discord != null)
                    {
                        return _discord.SendLogAsync(_config.Discord.StaffLog, message);
                    }
                }

                return LogAsync(
                    "SyncComplete",
                    factionsCount + " factions, " + playersCount + " players"
                );
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Sync logging error: " + ex.Message);
            }

            return Task.FromResult(0);
        }
    }
}
