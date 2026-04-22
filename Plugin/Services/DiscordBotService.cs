// Plugin/Services/DiscordBotService.cs
using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Net.Providers.WS4Net;
using Discord.Net.WebSockets;
using Discord.Rest;
using Discord.WebSocket;
using mamba.TorchDiscordSync.Plugin.Config;
using mamba.TorchDiscordSync.Plugin.Utils;

namespace mamba.TorchDiscordSync.Plugin.Services
{
    /// <summary>
    /// Core Discord bot service using Discord.Net
    /// Handles connection, events, commands and message processing
    /// </summary>
    public class DiscordBotService
    {
        private readonly DiscordBotConfig _config;
        private DiscordSocketClient _client;
        private bool _isConnected = false;
        private bool _isReady = false;

        /// <summary>
        /// Event triggered when a message is received (used for chat sync)
        /// </summary>
        public event Func<SocketMessage, Task> OnMessageReceivedEvent;

        public DiscordBotService(DiscordBotConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Get the underlying Discord client (for advanced operations if needed)
        /// </summary>
        public DiscordSocketClient GetClient()
        {
            return _client;
        }

        public ulong GetGuildId()
        {
            return _config != null ? _config.GuildID : 0;
        }

        /// <summary>
        /// Initialize and connect the Discord bot
        /// </summary>
        public async Task<bool> ConnectAsync()
        {
            try
            {
                if (_isConnected)
                    return true;

                LoggerUtil.LogInfo("[DISCORD_BOT] Initializing Discord bot...");
                const GatewayIntents messageContentIntent = (GatewayIntents)32768;

                var config = new DiscordSocketConfig
                {
                    GatewayIntents =
                        GatewayIntents.DirectMessages
                        | GatewayIntents.Guilds
                        | GatewayIntents.GuildMessages
                        | GatewayIntents.GuildMembers
                        // Discord.Net 2.4.0 predates the named MessageContent flag.
                        | messageContentIntent
                        |GatewayIntents.GuildPresences,
                    WebSocketProvider = WS4NetProvider.Instance,
                };

                _client = new DiscordSocketClient(config);

                // Hook events
                _client.Log += OnClientLog;
                _client.Ready += OnBotReady;
                _client.Disconnected += OnBotDisconnected;
                _client.MessageReceived += OnMessageReceived;
                _client.UserJoined += OnUserJoined;

                await _client.LoginAsync(TokenType.Bot, _config.BotToken);
                await _client.StartAsync();

                _isConnected = true;
                LoggerUtil.LogInfo(
                    "[DISCORD_BOT] Login succeeded and gateway start requested; waiting for Ready event"
                );

                return true;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Connection failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Disconnect and cleanup the bot
        /// </summary>
        public async Task DisconnectAsync()
        {
            try
            {
                if (_client != null)
                {
                    // Unhook events to prevent memory leaks
                    _client.Log -= OnClientLog;
                    _client.Ready -= OnBotReady;
                    _client.Disconnected -= OnBotDisconnected;
                    _client.MessageReceived -= OnMessageReceived;
                    _client.UserJoined -= OnUserJoined;

                    await _client.LogoutAsync();
                    await _client.StopAsync();
                    _client.Dispose();
                    _client = null;
                }

                _isConnected = false;
                _isReady = false;
                LoggerUtil.LogInfo("[DISCORD_BOT] Bot disconnected and cleaned up");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Disconnect error: " + ex.Message);
            }
        }

        /// <summary>
        /// Get the Discord guild (server) object
        /// Used by DiscordService wrapper to check for existing roles/channels
        /// </summary>
        public IGuild GetGuild()
        {
            try
            {
                if (!_isReady || _client == null)
                {
                    LoggerUtil.LogWarning("[DISCORD_BOT] Bot not ready or client is null");
                    return null;
                }

                var guild = _client.GetGuild(_config.GuildID);
                if (guild == null)
                {
                    LoggerUtil.LogWarning(
                        "[DISCORD_BOT] Guild not found (ID: " + _config.GuildID + ")"
                    );
                }

                return guild;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Error getting guild: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Send direct message to a user (e.g. verification code)
        /// </summary>
        public async Task<bool> SendVerificationDMAsync(
            string discordUsername,
            string verificationCode
        )
        {
            try
            {
                if (!_isReady)
                {
                    LoggerUtil.LogError("[DISCORD_BOT] Bot not ready, cannot send DM");
                    return false;
                }

                var user = FindUserByUsername(discordUsername);
                if (user == null)
                {
                    LoggerUtil.LogWarning("[DISCORD_BOT] User not found: " + discordUsername);
                    return false;
                }

                var dmChannel = await user.GetOrCreateDMChannelAsync();
                if (dmChannel == null)
                {
                    LoggerUtil.LogWarning(
                        "[DISCORD_BOT] Could not open DM with " + discordUsername
                    );
                    return false;
                }

                var embed = new EmbedBuilder()
                    .WithColor(Color.Green)
                    .WithTitle("🔐 Space Engineers Verification Request")
                    .WithDescription(
                        "Someone has requested to link your Discord account to a Space Engineers account."
                    )
                    .AddField("Verification Code", "```" + verificationCode + "```", false)
                    .AddField(
                        "To complete type here in this DM:\n",
                        "```" + _config.BotPrefix + "verify key:" + verificationCode + "```",
                        false
                    )
                    .AddField(
                        "⏱️ Expires",
                        "This code will expire in "
                            + _config.VerificationCodeExpirationMinutes
                            + " minutes",
                        false
                    )
                    .WithFooter("If you didn't request this, ignore this message")
                    .WithTimestamp(DateTime.UtcNow)
                    .Build();

                await dmChannel.SendMessageAsync(embed: embed);
                LoggerUtil.LogSuccess("[DISCORD_BOT] Sent verification DM to " + discordUsername);
                return true;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Send DM error: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Send success notification DM after verification
        /// </summary>
        public async Task<bool> SendVerificationSuccessDMAsync(
            string discordUsername,
            string playerName,
            long steamID
        )
        {
            try
            {
                if (!_isReady)
                    return false;

                var user = FindUserByUsername(discordUsername);
                if (user == null)
                    return false;

                var dmChannel = await user.GetOrCreateDMChannelAsync();
                if (dmChannel == null)
                    return false;

                var embed = new EmbedBuilder()
                    .WithColor(Color.Green)
                    .WithTitle("✅ Verification Successful!")
                    .WithDescription("Your Discord account has been linked to Space Engineers.")
                    .AddField("Game Player", playerName, true)
                    .AddField("Steam ID", steamID.ToString(), true)
                    .AddField(
                        "✨ Features unlocked",
                        "Faction channels, death notifications, chat sync",
                        false
                    )
                    .WithFooter("Welcome to the server!")
                    .WithTimestamp(DateTime.UtcNow)
                    .Build();

                await dmChannel.SendMessageAsync(embed: embed);
                LoggerUtil.LogSuccess("[DISCORD_BOT] Sent success DM to " + discordUsername);
                return true;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Send success DM error: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Send verification result DM to user after they complete verification
        /// Called by Plugin.HandleVerificationAsync() after VerifyFromDiscordAsync() completes
        /// NEW: Tells user if their verification was successful or failed
        /// </summary>
        public async Task<bool> SendVerificationResultDMAsync(
            string discordUsername,
            ulong discordUserID,
            string resultMessage,
            bool success
        )
        {
            try
            {
                // Check if bot is ready to send messages
                if (!_isReady)
                {
                    LoggerUtil.LogError(
                        "[DISCORD_BOT] Bot not ready, cannot send verification result"
                    );
                    return false;
                }

                // Find user by Discord ID
                var user = _client.GetUser(discordUserID);
                if (user == null)
                {
                    LoggerUtil.LogWarning(
                        "[DISCORD_BOT] User not found by Discord ID: " + discordUserID
                    );
                    return false;
                }

                // Open DM channel with user
                var dmChannel = await user.GetOrCreateDMChannelAsync();
                if (dmChannel == null)
                {
                    LoggerUtil.LogWarning(
                        "[DISCORD_BOT] Could not open DM with " + discordUsername
                    );
                    return false;
                }

                // Create embed with result
                var embed = new EmbedBuilder()
                    .WithColor(success ? Color.Green : Color.Red) // Green for success, red for failure
                    .WithTitle(success ? "✅ Verification Successful!" : "❌ Verification Failed")
                    .WithDescription(resultMessage) // Show the detailed result message
                    .WithTimestamp(DateTime.UtcNow)
                    .Build();

                // Send the embed message to user's DM
                await dmChannel.SendMessageAsync(embed: embed);

                LoggerUtil.LogSuccess(
                    "[DISCORD_BOT] Sent verification result DM to "
                        + discordUsername
                        + " (Status: "
                        + (success ? "SUCCESS" : "FAILED")
                        + ")"
                );
                return true;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(
                    "[DISCORD_BOT] Send verification result DM error: " + ex.Message
                );
                return false;
            }
        }

        /// <summary>
        /// Send message to a specific channel
        /// </summary>
        public async Task<bool> SendChannelMessageAsync(ulong channelID, string message)
        {
            try
            {
                if (!_isReady)
                    return false;

                var channel = _client.GetChannel(channelID) as IMessageChannel;
                if (channel == null)
                {
                    LoggerUtil.LogWarning("[DISCORD_BOT] Channel not found: " + channelID);
                    return false;
                }

                await channel.SendMessageAsync(message);
                return true;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Send channel message error: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Send embed message to a channel
        /// </summary>
        public async Task<bool> SendEmbedAsync(ulong channelID, Embed embed)
        {
            try
            {
                if (!_isReady)
                    return false;

                var channel = _client.GetChannel(channelID) as IMessageChannel;
                if (channel == null)
                    return false;

                await channel.SendMessageAsync(embed: embed);
                return true;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Send embed error: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Create a new role in the guild
        /// </summary>
        public async Task<ulong> CreateRoleAsync(string roleName, Color? color = null)
        {
            try
            {
                if (!_isReady)
                    return 0;

                var guild = _client.GetGuild(_config.GuildID);
                if (guild == null)
                {
                    LoggerUtil.LogError("[DISCORD_BOT] Guild not found");
                    return 0;
                }

                var role = await guild.CreateRoleAsync(roleName, color: color, isHoisted: false, isMentionable: false);
                LoggerUtil.LogSuccess("[DISCORD_BOT] Created role: " + roleName);
                return role.Id;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Create role error: " + ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Delete a role by ID
        /// </summary>
        public async Task<bool> DeleteRoleAsync(ulong roleID)
        {
            try
            {
                if (!_isReady)
                    return false;

                var guild = _client.GetGuild(_config.GuildID);
                if (guild == null)
                    return false;

                var role = guild.GetRole(roleID);
                if (role == null)
                    return false;

                await role.DeleteAsync();
                LoggerUtil.LogSuccess("[DISCORD_BOT] Deleted role: " + roleID);
                return true;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Delete role error: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Create text channel in Discord with optional category and role permissions
        /// FIXED: Creates channel with categoryID from the beginning
        /// FIXED: Sets up permissions for faction role
        /// </summary>
        public async Task<ulong> CreateChannelAsync(
            string channelName,
            ulong? categoryID = null,
            ulong? roleID = null
        )
        {
            try
            {
                if (!_isReady)
                {
                    LoggerUtil.LogError("[DISCORD_BOT] Bot not ready, cannot create channel");
                    return 0;
                }

                var guild = _client.GetGuild(_config.GuildID);
                if (guild == null)
                {
                    LoggerUtil.LogError("[DISCORD_BOT] Guild not found for channel creation");
                    return 0;
                }

                // ============================================================
                // FIXED: Create channel with categoryID from the beginning
                // ============================================================
                RestTextChannel channel = null;

                if (categoryID.HasValue && categoryID.Value > 0)
                {
                    var category = guild.GetCategoryChannel(categoryID.Value);
                    if (category != null)
                    {
                        try
                        {
                            // Create directly in category
                            channel = await guild.CreateTextChannelAsync(
                                channelName,
                                x => x.CategoryId = categoryID.Value
                            );
                            LoggerUtil.LogDebug(
                                "[DISCORD_BOT] Created channel in category (ID: "
                                    + categoryID.Value
                                    + ")"
                            );
                        }
                        catch (Exception ex)
                        {
                            LoggerUtil.LogWarning(
                                "[DISCORD_BOT] Failed to create in category: " + ex.Message
                            );
                            // Fallback: create without category
                            channel = await guild.CreateTextChannelAsync(channelName);
                        }
                    }
                    else
                    {
                        LoggerUtil.LogWarning(
                            "[DISCORD_BOT] Category not found (ID: "
                                + categoryID.Value
                                + ") - creating channel without category"
                        );
                        channel = await guild.CreateTextChannelAsync(channelName);
                    }
                }
                else
                {
                    // No category specified
                    channel = await guild.CreateTextChannelAsync(channelName);
                }

                if (channel == null)
                {
                    LoggerUtil.LogError("[DISCORD_BOT] Failed to create channel: " + channelName);
                    return 0;
                }

                // ============================================================
                // FIXED: Set up permissions if roleID is provided
                // ============================================================
                if (roleID.HasValue && roleID.Value > 0)
                {
                    try
                    {
                        var role = guild.GetRole(roleID.Value);
                        if (role != null)
                        {
                            // Deny access for @everyone
                            await channel.AddPermissionOverwriteAsync(
                                guild.EveryoneRole,
                                new OverwritePermissions(viewChannel: PermValue.Deny)
                            );

                            // Allow access for faction role
                            await channel.AddPermissionOverwriteAsync(
                                role,
                                new OverwritePermissions(
                                    viewChannel: PermValue.Allow,
                                    sendMessages: PermValue.Allow
                                )
                            );

                            LoggerUtil.LogDebug(
                                "[DISCORD_BOT] Set permissions for channel "
                                    + channelName
                                    + " (Role: "
                                    + role.Name
                                    + ")"
                            );
                        }
                        else
                        {
                            LoggerUtil.LogWarning(
                                "[DISCORD_BOT] Role not found for permission setup (ID: "
                                    + roleID.Value
                                    + ")"
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogWarning(
                            "[DISCORD_BOT] Failed to set channel permissions: " + ex.Message
                        );
                    }
                }

                LoggerUtil.LogSuccess(
                    "[DISCORD_BOT] Created text channel: "
                        + channelName
                        + " (ID: "
                        + channel.Id
                        + ")"
                );
                return channel.Id;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Create channel error: " + ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Create voice channel with same category and role permissions as faction. Name = lowercase (same as faction).
        /// </summary>
        public async Task<ulong> CreateVoiceChannelAsync(
            string channelName,
            ulong? categoryID = null,
            ulong? roleID = null
        )
        {
            try
            {
                if (!_isReady)
                {
                    LoggerUtil.LogError("[DISCORD_BOT] Bot not ready");
                    return 0;
                }
                var guild = _client.GetGuild(_config.GuildID);
                if (guild == null)
                {
                    LoggerUtil.LogError("[DISCORD_BOT] Guild not found");
                    return 0;
                }

                RestVoiceChannel channel = null;
                if (categoryID.HasValue && categoryID.Value > 0)
                {
                    var cat = guild.GetCategoryChannel(categoryID.Value);
                    if (cat != null)
                        channel = await guild.CreateVoiceChannelAsync(
                            channelName,
                            x => x.CategoryId = categoryID.Value
                        );
                    else
                        channel = await guild.CreateVoiceChannelAsync(channelName);
                }
                else
                    channel = await guild.CreateVoiceChannelAsync(channelName);

                if (channel == null)
                    return 0;
                if (roleID.HasValue && roleID.Value > 0)
                {
                    var role = guild.GetRole(roleID.Value);
                    if (role != null)
                    {
                        await channel.AddPermissionOverwriteAsync(
                            guild.EveryoneRole,
                            new OverwritePermissions(viewChannel: PermValue.Deny)
                        );
                        await channel.AddPermissionOverwriteAsync(
                            role,
                            new OverwritePermissions(
                                viewChannel: PermValue.Allow,
                                connect: PermValue.Allow,
                                speak: PermValue.Allow
                            )
                        );
                    }
                }
                LoggerUtil.LogSuccess(
                    "[DISCORD_BOT] Created voice channel: "
                        + channelName
                        + " (ID: "
                        + channel.Id
                        + ")"
                );
                return channel.Id;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Create voice channel error: " + ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Delete a channel by ID
        /// </summary>
        public async Task<bool> DeleteChannelAsync(ulong channelID)
        {
            try
            {
                if (!_isReady)
                    return false;

                var guild = _client.GetGuild(_config.GuildID);
                if (guild == null)
                    return false;

                var channel = guild.GetChannel(channelID);
                if (channel == null)
                {
                    LoggerUtil.LogWarning(
                        "[DISCORD_BOT] Channel not found for deletion: " + channelID
                    );
                    return false;
                }

                // Cast to IGuildChannel - ima DeleteAsync()
                var guildChannel = channel as IGuildChannel;
                if (guildChannel == null)
                {
                    LoggerUtil.LogWarning(
                        "[DISCORD_BOT] Channel is not a guild channel: " + channelID
                    );
                    return false;
                }

                await guildChannel.DeleteAsync();
                LoggerUtil.LogSuccess("[DISCORD_BOT] Deleted channel: " + channelID);
                return true;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Delete channel error: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Assign role to a user
        /// </summary>
        public async Task<bool> AssignRoleAsync(ulong userID, ulong roleID)
        {
            try
            {
                if (!_isReady)
                    return false;

                var guild = _client.GetGuild(_config.GuildID);
                if (guild == null)
                    return false;

                var user = guild.GetUser(userID);
                if (user == null)
                    return false;

                var role = guild.GetRole(roleID);
                if (role == null)
                    return false;

                await user.AddRoleAsync(role);
                LoggerUtil.LogSuccess(
                    "[DISCORD_BOT] Assigned role " + roleID + " to user " + userID
                );
                return true;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Assign role error: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Remove role from a user
        /// </summary>
        public async Task<bool> RemoveRoleAsync(ulong userID, ulong roleID)
        {
            try
            {
                if (!_isReady)
                    return false;

                var guild = _client.GetGuild(_config.GuildID);
                if (guild == null)
                    return false;

                var user = guild.GetUser(userID);
                if (user == null)
                    return false;

                var role = guild.GetRole(roleID);
                if (role == null)
                    return false;

                await user.RemoveRoleAsync(role);
                LoggerUtil.LogSuccess(
                    "[DISCORD_BOT] Removed role " + roleID + " from user " + userID
                );
                return true;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Remove role error: " + ex.Message);
                return false;
            }
        }

        public bool IsReady => _isReady;
        public bool IsConnected => _isConnected;

        // ============================================================
        // PRIVATE EVENT HANDLERS
        // ============================================================

        private async Task OnBotReady()
        {
            _isConnected = true;
            _isReady = true;
            LoggerUtil.LogSuccess("[DISCORD_BOT] Bot is ready and listening!");

            try
            {
                if (_client != null)
                    await _client.SetStatusAsync(UserStatus.Online);

                if (_client != null)
                    await _client.SetGameAsync(
                    _config.BotPrefix + "tds help",
                    null,
                    ActivityType.Listening
                );

                LoggerUtil.LogInfo(
                    "[DISCORD_BOT] Presence set to online and listening for commands"
                );
            }
            catch (Exception ex)
            {
                LoggerUtil.LogWarning(
                    "[DISCORD_BOT] Failed to set presence: " + ex.Message
                );
            }

            // NEW: Pre-load guild users so username/ID lookups work immediately
            try
            {
                var guild = _client != null ? _client.GetGuild(_config.GuildID) : null;
                if (guild != null)
                {
                    LoggerUtil.LogDebug(
                        "[DISCORD_BOT] Downloading guild users to warm up cache..."
                    );
                    var _ = guild.DownloadUsersAsync();
                }
                else
                {
                    LoggerUtil.LogWarning(
                        "[DISCORD_BOT] Guild not found on Ready - user search may be limited"
                    );
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogWarning(
                    "[DISCORD_BOT] Failed to download guild users on Ready: " + ex.Message
                );
            }
        }

        /// Handle disconnection and attempt reconnection
        private async Task OnBotDisconnected(Exception ex)
        {
            _isConnected = false;
            _isReady = false;
            string exMsg = ex?.Message ?? "Unknown error";
            LoggerUtil.LogWarning("[DISCORD_BOT] Bot disconnected: " + exMsg);

            // Auto-reconnect logic
            int maxAttempts = 5;
            int delayMs = 5000;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    LoggerUtil.LogInfo(
                        $"[DISCORD_BOT] Reconnection attempt {attempt}/{maxAttempts}..."
                    );
                    await Task.Delay(delayMs);

                    if (_client != null && _client.ConnectionState != ConnectionState.Connected)
                    {
                        await _client.StartAsync();
                        _isConnected = true;
                        LoggerUtil.LogSuccess(
                            "[DISCORD_BOT] Reconnection start requested; waiting for Ready event"
                        );
                        return;
                    }
                }
                catch (Exception reconnectEx)
                {
                    LoggerUtil.LogWarning(
                        $"[DISCORD_BOT] Reconnection attempt {attempt} failed: {reconnectEx.Message}"
                    );
                }
            }

            LoggerUtil.LogError("[DISCORD_BOT] Failed to reconnect after all attempts");
        }

        /// <summary>
        /// Main message handler - processes both commands and chat messages
        /// </summary>
        private async Task OnMessageReceived(SocketMessage message)
        {
            try
            {
                if (message.Author.IsBot)
                    return;

                LoggerUtil.LogDebug(
                    "[DISCORD_BOT] Message received from "
                        + message.Author.Username
                        + " in channel: "
                        + message.Channel.Name
                );

                // Check if this is a DM (direct message)
                if (message.Channel is IDMChannel dmChannel)
                {
                    LoggerUtil.LogDebug("[DISCORD_BOT] DM message detected - processing command");

                    // Command handling for DM messages
                    if (!message.Content.StartsWith(_config.BotPrefix))
                    {
                        LoggerUtil.LogDebug(
                            "[DISCORD_BOT] DM message does not start with bot prefix '"
                                + _config.BotPrefix
                                + "'"
                        );
                        return;
                    }

                    var args = message
                        .Content.Substring(_config.BotPrefix.Length)
                        .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    if (args.Length == 0)
                        return;

                    var command = args[0].ToLower();
                    LoggerUtil.LogDebug("[DISCORD_BOT] DM command: " + command);

                    if (command == "verify")
                    {
                        await HandleVerifyCommand(message, args);
                    }
                    else if (command == "help")
                    {
                        await HandleHelpCommand(message);
                    }
                    return; // Don't forward DM to chat sync
                }

                if (string.IsNullOrWhiteSpace(message.Content))
                {
                    LoggerUtil.LogWarning(
                        "[DISCORD_BOT] Received a guild message with no content. Enable the Message Content intent for this bot in the Discord developer portal if prefix commands should work in guild channels."
                    );
                    return;
                }

                // If not DM, forward to chat sync (guild messages)
                if (OnMessageReceivedEvent != null)
                {
                    await OnMessageReceivedEvent.Invoke(message);
                }

                // Command handling for guild messages only if message starts with prefix
                if (!message.Content.StartsWith(_config.BotPrefix))
                    return;

                var guildArgs = message
                    .Content.Substring(_config.BotPrefix.Length)
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (guildArgs.Length == 0)
                    return;

                var guildCommand = guildArgs[0].ToLower();

                if (guildCommand == "verify")
                {
                    await HandleVerifyCommand(message, guildArgs);
                }
                else if (guildCommand == "help")
                {
                    await HandleHelpCommand(message);
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Message handler error: " + ex.Message);
            }
        }

        private Task OnClientLog(LogMessage message)
        {
            string text = "[DISCORD_BOT] [" + message.Severity + "] " + message.Message;
            if (message.Exception != null)
                text += " | " + message.Exception.Message;

            switch (message.Severity)
            {
                case LogSeverity.Critical:
                case LogSeverity.Error:
                    LoggerUtil.LogError(text);
                    break;
                case LogSeverity.Warning:
                    LoggerUtil.LogWarning(text);
                    break;
                case LogSeverity.Info:
                    LoggerUtil.LogInfo(text);
                    break;
                default:
                    LoggerUtil.LogDebug(text);
                    break;
            }

            return Task.CompletedTask;
        }

        private async Task HandleVerifyCommand(SocketMessage message, string[] args)
        {
            try
            {
                LoggerUtil.LogDebug(
                    "[DISCORD_BOT] Handling verify command for user: " + message.Author.Username
                );
                if (args.Length < 2)
                {
                    await message.Author.SendMessageAsync(
                        "❌ Usage:\n"
                            + "  !verify CODE\n"
                            + "  !verify key:CODE\n"
                            + "  !verify username:YourGameName\n"
                            + "  !verify steamid:YOUR_STEAM_ID\n\n"
                            + "Examples:\n"
                            + "  !verify ABC12345\n"
                            + "  !verify key:ABC12345\n"
                            + "  !verify username:mamba\n"
                            + "  !verify steamid:76561198000000000"
                    );
                    return;
                }

                string rawArg = args[1].Trim();

                // NEW: !verify key:CODE → submit verification code
                if (rawArg.StartsWith("key:", StringComparison.OrdinalIgnoreCase))
                {
                    string keyCode = rawArg.Substring("key:".Length).Trim();
                    if (string.IsNullOrEmpty(keyCode))
                    {
                        await message.Author.SendMessageAsync(
                            "❌ Invalid format. Usage: !verify key:CODE"
                        );
                        return;
                    }

                    string normalizedCode = keyCode.ToUpper();
                    LoggerUtil.LogDebug(
                        "[DISCORD_BOT] VERIFY key: flow with code: " + normalizedCode
                    );

                    OnVerificationAttempt?.Invoke(
                        normalizedCode,
                        message.Author.Id,
                        message.Author.Username
                    );

                    var embedKey = new EmbedBuilder()
                        .WithColor(Color.Blue)
                        .WithTitle("⏳ Verifying...")
                        .WithDescription("Your verification code is being processed.")
                        .WithFooter("You will receive a confirmation shortly")
                        .Build();

                    await message.Author.SendMessageAsync(embed: embedKey);
                    return;
                }

                // NEW: !verify username:mamba → send instructions how to start verification from game
                if (rawArg.StartsWith("username:", StringComparison.OrdinalIgnoreCase))
                {
                    string targetUser = rawArg.Substring("username:".Length).Trim();
                    if (string.IsNullOrEmpty(targetUser))
                    {
                        await message.Author.SendMessageAsync(
                            "❌ Invalid format. Usage: !verify username:YourGameName"
                        );
                        return;
                    }

                    LoggerUtil.LogDebug(
                        "[DISCORD_BOT] VERIFY username: helper requested for: " + targetUser
                    );

                    var embedUser = new EmbedBuilder()
                        .WithColor(Color.Green)
                        .WithTitle("🛈 How to start verification")
                        .WithDescription(
                            "To link your Discord with your Space Engineers account, you must run a command **in-game**."
                        )
                        .AddField(
                            "Step 1 - In Game",
                            "Open chat and type:\n```/tds verify @" + targetUser + "```",
                            false
                        )
                        .AddField(
                            "Step 2 - Discord DM",
                            "You will receive a DM from this bot with a **verification code**.\n"
                                + "Follow the instructions in that DM to complete verification.",
                            false
                        )
                        .WithFooter("This message does NOT verify you, it only shows instructions.")
                        .Build();

                    await message.Author.SendMessageAsync(embed: embedUser);
                    return;
                }

                // NEW: !verify steamid:123... → send instructions with SteamID hint
                if (rawArg.StartsWith("steamid:", StringComparison.OrdinalIgnoreCase))
                {
                    string targetSteam = rawArg.Substring("steamid:".Length).Trim();
                    if (string.IsNullOrEmpty(targetSteam))
                    {
                        await message.Author.SendMessageAsync(
                            "❌ Invalid format. Usage: !verify steamid:YOUR_STEAM_ID"
                        );
                        return;
                    }

                    LoggerUtil.LogDebug(
                        "[DISCORD_BOT] VERIFY steamid: helper requested for: " + targetSteam
                    );

                    var embedSteam = new EmbedBuilder()
                        .WithColor(Color.Green)
                        .WithTitle("🛈 How to start verification")
                        .WithDescription(
                            "To link your Discord with your Space Engineers account, you must run a command **in-game**."
                        )
                        .AddField(
                            "Step 1 - In Game",
                            "Open chat and type (replace with your Discord name or ID):\n"
                                + "```/tds verify @YourDiscordName```\n"
                                + "or\n"
                                + "```/tds verify YourDiscordID```",
                            false
                        )
                        .AddField(
                            "Step 2 - Discord DM",
                            "You will receive a DM from this bot with a **verification code**.\n"
                                + "Follow the instructions in that DM to complete verification.",
                            false
                        )
                        .AddField(
                            "Info",
                            "SteamID you sent: `" + targetSteam + "` (for admin reference only).",
                            false
                        )
                        .WithFooter("This message does NOT verify you, it only shows instructions.")
                        .Build();

                    await message.Author.SendMessageAsync(embed: embedSteam);
                    return;
                }

                // BACKWARD COMPATIBLE: old syntax !verify CODE
                string code = rawArg.ToUpper();
                LoggerUtil.LogDebug("[DISCORD_BOT] VERIFY legacy flow with code: " + code);

                OnVerificationAttempt?.Invoke(code, message.Author.Id, message.Author.Username);

                var embed = new EmbedBuilder()
                    .WithColor(Color.Blue)
                    .WithTitle("⏳ Verifying...")
                    .WithDescription("Your verification code is being processed.")
                    .WithFooter("You will receive a confirmation shortly")
                    .Build();

                await message.Author.SendMessageAsync(embed: embed);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Verify command error: " + ex.Message);
            }
        }

        private async Task HandleHelpCommand(SocketMessage message)
        {
            try
            {
                var embed = new EmbedBuilder()
                    .WithColor(Color.Blue)
                    .WithTitle("🤖 mamba.TorchDiscordSync.Plugin Bot Help")
                    .AddField(
                        "Verification",
                        _config.BotPrefix + "verify CODE - Verify your Space Engineers account",
                        false
                    )
                    .AddField("Help", _config.BotPrefix + "help - Show this message", false)
                    .WithFooter("Bot will respond via DM")
                    .Build();

                await message.Author.SendMessageAsync(embed: embed);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Help command error: " + ex.Message);
            }
        }

        /// <summary>
        /// Welcome new users with a DM and instructions on how to verify
        /// </summary>
        /// <param name="user"></param>
        /// <returns></returns>
        private async Task OnUserJoined(SocketGuildUser user)
        {
            try
            {
                LoggerUtil.LogInfo("[DISCORD_BOT] New user joined: " + user.Username);

                var embed = new EmbedBuilder()
                    .WithColor(Color.Gold)
                    .WithTitle("👋 Welcome!")
                    .WithDescription("Welcome to the Space Engineers community!")
                    .AddField(
                        "Link Your Account",
                        "Use `/tds verify @YourDiscordName` in-game",
                        false
                    )
                    .AddField("Need Help?", "Type `!help` for commands", false)
                    .Build();

                await user.SendMessageAsync(embed: embed);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] User joined handler error: " + ex.Message);
            }
        }

        /// <summary>
        /// Find user by username or nickname in the guild
        /// </summary>
        private SocketUser FindUserByUsername(string searchTerm)
        {
            try
            {
                var guild = _client.GetGuild(_config.GuildID);
                if (guild == null)
                {
                    LoggerUtil.LogError("[DISCORD_BOT] Guild not found");
                    return null;
                }

                if (string.IsNullOrWhiteSpace(searchTerm))
                {
                    LoggerUtil.LogWarning("[DISCORD_BOT] Empty search term");
                    return null;
                }

                // Normalize search term - remove @ if present
                string search = searchTerm.ToLower().Replace("@", "").Trim();
                LoggerUtil.LogDebug(
                    "[DISCORD_BOT] Searching for Discord user: '"
                        + search
                        + "' (Guild users cached: "
                        + guild.Users.Count
                        + ", Members: "
                        + guild.MemberCount
                        + ")"
                );

                // FIRST: Try Discord ID match DIRECTLY (if search term is numeric)
                if (ulong.TryParse(search, out ulong userId))
                {
                    LoggerUtil.LogDebug(
                        "[DISCORD_BOT] Search term is numeric, trying Discord ID: " + userId
                    );
                    var userById = guild.GetUser(userId);
                    if (userById != null)
                    {
                        LoggerUtil.LogSuccess("[DISCORD_BOT] Found user by Discord ID: " + userId);
                        return userById;
                    }
                    else
                    {
                        LoggerUtil.LogWarning(
                            "[DISCORD_BOT] User not found by ID: " + userId + " - not in guild"
                        );
                        return null;
                    }
                }

                // SECOND: Try exact and partial matches on username/nickname
                foreach (var user in guild.Users)
                {
                    // Skip bots only for username/nickname matching
                    if (user.IsBot)
                    {
                        LoggerUtil.LogDebug("[DISCORD_BOT] Skipping bot account: " + user.Username);
                        continue;
                    }

                    // Method 1: Exact match on Username (Discord username)
                    if (!string.IsNullOrEmpty(user.Username))
                    {
                        if (user.Username.Equals(search, StringComparison.OrdinalIgnoreCase))
                        {
                            LoggerUtil.LogSuccess(
                                "[DISCORD_BOT] Found user by Username: " + user.Username
                            );
                            return user;
                        }
                    }

                    // Method 2: Exact match on Nickname (server nickname)
                    if (!string.IsNullOrEmpty(user.Nickname))
                    {
                        if (user.Nickname.Equals(search, StringComparison.OrdinalIgnoreCase))
                        {
                            LoggerUtil.LogSuccess(
                                "[DISCORD_BOT] Found user by Nickname: " + user.Nickname
                            );
                            return user;
                        }
                    }
                }

                // Try partial matches if no exact match found
                LoggerUtil.LogDebug(
                    "[DISCORD_BOT] No exact match found, trying partial matches..."
                );
                foreach (var user in guild.Users)
                {
                    // Skip bots only for username/nickname matching
                    if (user.IsBot)
                    {
                        continue;
                    }

                    // Method 3: Partial match on Username
                    if (!string.IsNullOrEmpty(user.Username))
                    {
                        if (user.Username.ToLower().Contains(search))
                        {
                            LoggerUtil.LogSuccess(
                                "[DISCORD_BOT] Found user by partial Username: " + user.Username
                            );
                            return user;
                        }
                    }
                }

                LoggerUtil.LogWarning("[DISCORD_BOT] User NOT found: '" + searchTerm + "'");
                return null;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Find user error: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Get role by ID (for checking if role already exists)
        /// </summary>
        public IRole GetRoleAsync(ulong roleId)
        {
            try
            {
                if (!_isReady)
                    return null;

                var guild = _client.GetGuild(_config.GuildID);
                if (guild == null)
                    return null;

                return guild.GetRole(roleId);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Get role error: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Get channel by ID (for checking if channel already exists)
        /// </summary>
        public IChannel GetChannelAsync(ulong channelId)
        {
            try
            {
                if (!_isReady)
                    return null;

                var guild = _client.GetGuild(_config.GuildID);
                if (guild == null)
                    return null;

                return guild.GetChannel(channelId);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[DISCORD_BOT] Get channel error: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Check if a role already exists by ID
        /// Used to avoid recreating roles when they already exist on Discord
        /// Returns null if role doesn't exist or bot is not ready
        /// </summary>
        public IRole GetExistingRole(ulong roleId)
        {
            try
            {
                // Don't proceed if bot is not ready
                if (!_isReady)
                {
                    LoggerUtil.LogDebug("[DISCORD_BOT] Bot not ready to check roles");
                    return null;
                }

                // Get the guild (Discord server)
                var guild = _client.GetGuild(_config.GuildID);
                if (guild == null)
                {
                    LoggerUtil.LogWarning("[DISCORD_BOT] Guild not found while checking for role");
                    return null;
                }

                // Try to get the role by ID
                var role = guild.GetRole(roleId);

                if (role != null)
                {
                    LoggerUtil.LogDebug(
                        "[DISCORD_BOT] Found existing role: " + role.Name + " (ID: " + roleId + ")"
                    );
                }
                else
                {
                    LoggerUtil.LogDebug("[DISCORD_BOT] Role not found (ID: " + roleId + ")");
                }

                return role;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogWarning(
                    "[DISCORD_BOT] Error checking for existing role: " + ex.Message
                );
                return null;
            }
        }

        /// <summary>
        /// Check if a channel already exists by ID
        /// Used to avoid recreating channels when they already exist on Discord
        /// Returns null if channel doesn't exist or bot is not ready
        /// </summary>
        public IChannel GetExistingChannel(ulong channelId)
        {
            try
            {
                // Don't proceed if bot is not ready
                if (!_isReady)
                {
                    LoggerUtil.LogDebug("[DISCORD_BOT] Bot not ready to check channels");
                    return null;
                }

                // Get the guild (Discord server)
                var guild = _client.GetGuild(_config.GuildID);
                if (guild == null)
                {
                    LoggerUtil.LogWarning(
                        "[DISCORD_BOT] Guild not found while checking for channel"
                    );
                    return null;
                }

                // Try to get the channel by ID
                var channel = guild.GetChannel(channelId);

                if (channel != null)
                {
                    LoggerUtil.LogDebug(
                        "[DISCORD_BOT] Found existing channel: "
                            + channel.Name
                            + " (ID: "
                            + channelId
                            + ")"
                    );
                }
                else
                {
                    LoggerUtil.LogDebug("[DISCORD_BOT] Channel not found (ID: " + channelId + ")");
                }

                return channel;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogWarning(
                    "[DISCORD_BOT] Error checking for existing channel: " + ex.Message
                );
                return null;
            }
        }

        // ============================================================
        // NEW: VERIFIED ROLE MANAGEMENT
        // ============================================================

        /// <summary>
        /// Get or create the "Verified" role on the Discord server
        /// Returns the role ID or 0 if failed
        /// </summary>
        public async Task<ulong> GetOrCreateVerifiedRoleAsync()
        {
            try
            {
                if (!_isReady)
                    return 0;

                var guild = _client.GetGuild(_config.GuildID);
                if (guild == null)
                    return 0;

                var existingRole = guild.Roles.FirstOrDefault(r => r.Name == "Verified");
                if (existingRole != null)
                    return existingRole.Id;

                var newRole = await guild.CreateRoleAsync(
                    "Verified",
                    color: new Color(0, 176, 240),
                    isHoisted: false,
                    isMentionable: false
                );
                LoggerUtil.LogSuccess($"[DISCORD_BOT] Created Verified role");
                return newRole.Id;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DISCORD_BOT] Create role error: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Assign the Verified role to a Discord user
        /// </summary>
        public async Task<bool> AssignVerifiedRoleAsync(IUser user, ulong roleId)
        {
            try
            {
                if (!_isReady || roleId == 0)
                    return false;

                var guild = _client.GetGuild(_config.GuildID);
                var role = guild.GetRole(roleId);
                var guildUser = guild.GetUser(user.Id);

                if (guildUser == null || role == null)
                    return false;

                await guildUser.AddRoleAsync(role);
                LoggerUtil.LogSuccess($"[DISCORD_BOT] Assigned Verified role");
                return true;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DISCORD_BOT] Assign role error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Assign a faction role to a Discord user
        /// </summary>
        public async Task<bool> AssignFactionRoleAsync(IUser user, string factionTag)
        {
            try
            {
                if (!_isReady)
                    return false;

                var guild = _client.GetGuild(_config.GuildID);
                var role = guild.Roles.FirstOrDefault(r => r.Name == factionTag);
                var guildUser = guild.GetUser(user.Id);

                if (guildUser == null || role == null)
                    return false;

                await guildUser.AddRoleAsync(role);
                LoggerUtil.LogSuccess($"[DISCORD_BOT] Assigned faction role");
                return true;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[DISCORD_BOT] Faction role error: {ex.Message}");
                return false;
            }
        }

        // ============================================================
        // PUBLIC EVENTS
        // ============================================================

        /// <summary>
        /// Event for verification attempts (triggered by !verify command)
        /// </summary>
        public event Action<string, ulong, string> OnVerificationAttempt;
    }
}
