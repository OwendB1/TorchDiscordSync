using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using TorchDiscordSync.DiscordHost.Logging;
using TorchDiscordSync.Shared.Ipc;

namespace TorchDiscordSync.DiscordHost.Services
{
    internal sealed class DiscordGatewayService
    {
        private readonly SemaphoreSlim _stateLock = new(1, 1);
        private DiscordRuntimeConfig _config;
        private DiscordSocketClient _client;
        private bool _isConnected;
        private bool _isReady;
        private bool _applicationCommandsRegistered;
        private bool _suppressReconnect;
        private int _isReconnecting;

        public event Func<DiscordIncomingMessage, Task> MessageReceived;
        public event Func<DiscordVerificationAttempt, Task> VerificationAttemptReceived;
        public event Func<DiscordConnectionState, Task> ConnectionStateChanged;

        public DiscordConnectionState GetConnectionState()
        {
            return new DiscordConnectionState
            {
                IsConnected = _isConnected,
                IsReady = _isReady,
            };
        }

        public async Task<bool> ApplyConfigurationAsync(DiscordRuntimeConfig config)
        {
            if (config == null)
                return false;

            if (string.IsNullOrWhiteSpace(config.BotToken) || config.GuildId == 0)
            {
                HostLogger.Warn("Discord host configuration is incomplete; skipping connect.");
                return false;
            }

            await _stateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var requiresReconnect =
                    _client == null
                    || _config == null
                    || !string.Equals(_config.BotToken, config.BotToken, StringComparison.Ordinal)
                    || _config.GuildId != config.GuildId;

                _config = config;

                if (!requiresReconnect)
                    return true;

                _suppressReconnect = true;
                await DisconnectCoreAsync().ConfigureAwait(false);
                _suppressReconnect = false;

                return await ConnectCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _stateLock.Release();
            }
        }

        public async Task StopAsync()
        {
            await _stateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _suppressReconnect = true;
                await DisconnectCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _stateLock.Release();
            }
        }

        public async Task<bool> SendVerificationDmAsync(DiscordVerificationRequest request)
        {
            try
            {
                if (!EnsureReady())
                    return false;

                var user = FindUserByUsername(request.DiscordUsername);
                if (user == null)
                {
                    HostLogger.Warn("Verification DM target not found: " + request.DiscordUsername);
                    return false;
                }

                var dmChannel = await user.CreateDMChannelAsync().ConfigureAwait(false);
                if (dmChannel == null) return false;

                var embed = new EmbedBuilder()
                    .WithColor(Color.Green)
                    .WithTitle("Verification Request")
                    .WithDescription(
                        "Someone has requested to link your Discord account to a Space Engineers account.")
                    .AddField("Verification Code", "```" + request.VerificationCode + "```")
                    .AddField(
                        "Complete Verification",
                        "Use `/verify` in Discord and paste the code above.")
                    .AddField(
                        "Expires",
                        $"This code will expire in {_config.VerificationCodeExpirationMinutes} minutes")
                    .WithFooter("If you didn't request this, ignore this message")
                    .WithTimestamp(DateTime.UtcNow)
                    .Build();

                await dmChannel.SendMessageAsync(embed: embed).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                HostLogger.Error("SendVerificationDmAsync failed: " + ex.Message);
                return false;
            }
        }

        public async Task<bool> SendVerificationResultDmAsync(DiscordVerificationResultMessage request)
        {
            try
            {
                if (!EnsureReady())
                    return false;

                var user = _client.GetUser(request.DiscordUserId);
                if (user == null)
                    return false;

                var dmChannel = await user.CreateDMChannelAsync().ConfigureAwait(false);
                if (dmChannel == null)
                    return false;

                var embed = new EmbedBuilder()
                    .WithColor(request.IsSuccess ? Color.Green : Color.Red)
                    .WithTitle(request.IsSuccess ? "Verification Successful" : "Verification Failed")
                    .WithDescription(request.Message)
                    .WithTimestamp(DateTime.UtcNow)
                    .Build();

                await dmChannel.SendMessageAsync(embed: embed).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                HostLogger.Error("SendVerificationResultDmAsync failed: " + ex.Message);
                return false;
            }
        }

        public async Task<bool> SendChannelMessageAsync(DiscordMessageRequest request)
        {
            try
            {
                if (!EnsureReady())
                    return false;

                var channel = _client.GetChannel(request.ChannelId) as IMessageChannel;
                if (channel == null)
                    return false;

                await channel.SendMessageAsync(request.Content ?? string.Empty).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                HostLogger.Error("SendChannelMessageAsync failed: " + ex.Message);
                return false;
            }
        }

        public async Task<bool> SendEmbedMessageAsync(DiscordSendEmbedRequest request)
        {
            try
            {
                if (!EnsureReady())
                    return false;

                var channel = _client.GetChannel(request.ChannelId) as IMessageChannel;
                if (channel == null)
                    return false;

                await channel.SendMessageAsync(embed: BuildEmbed(request.Embed)).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                HostLogger.Error("SendEmbedMessageAsync failed: " + ex.Message);
                return false;
            }
        }

        public async Task<ulong> CreateRoleAsync(DiscordCreateRoleRequest request)
        {
            try
            {
                var guild = GetGuild();
                if (guild == null)
                    return 0;

                var role = await guild.CreateRoleAsync(
                    request.RoleName,
                    color: null,
                    isHoisted: false,
                    isMentionable: false).ConfigureAwait(false);
                return role.Id;
            }
            catch (Exception ex)
            {
                HostLogger.Error("CreateRoleAsync failed: " + ex.Message);
                return 0;
            }
        }

        public async Task<bool> DeleteRoleAsync(DiscordDeleteRoleRequest request)
        {
            try
            {
                var guild = GetGuild();
                if (guild == null)
                    return false;

                var role = guild.GetRole(request.RoleId);
                if (role == null)
                    return false;

                await role.DeleteAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                HostLogger.Error("DeleteRoleAsync failed: " + ex.Message);
                return false;
            }
        }

        public DiscordRoleInfo GetRoleInfo(DiscordRoleQueryRequest request)
        {
            try
            {
                var guild = GetGuild();
                if (guild == null || request.RoleId == 0)
                    return null;

                var role = guild.GetRole(request.RoleId);
                if (role == null)
                    return null;

                return new DiscordRoleInfo
                {
                    RoleId = role.Id,
                    RoleName = role.Name,
                };
            }
            catch (Exception ex)
            {
                HostLogger.Error("GetRoleInfo failed: " + ex.Message);
                return null;
            }
        }

        public ulong FindRoleByName(DiscordRoleQueryRequest request)
        {
            try
            {
                var guild = GetGuild();
                if (guild == null || string.IsNullOrWhiteSpace(request.RoleName))
                    return 0;

                var role = guild.Roles.FirstOrDefault(r =>
                    string.Equals(r.Name, request.RoleName, StringComparison.OrdinalIgnoreCase));
                return role != null ? role.Id : 0;
            }
            catch (Exception ex)
            {
                HostLogger.Error("FindRoleByName failed: " + ex.Message);
                return 0;
            }
        }

        public async Task<ulong> CreateChannelAsync(DiscordCreateChannelRequest request)
        {
            try
            {
                var guild = GetGuild();
                if (guild == null)
                    return 0;

                switch (request.ChannelKind)
                {
                    case DiscordChannelKind.Text:
                        return await CreateTextChannelAsync(guild, request).ConfigureAwait(false);
                    case DiscordChannelKind.Voice:
                        return await CreateVoiceChannelAsync(guild, request).ConfigureAwait(false);
                    default:
                        return 0;
                }
            }
            catch (Exception ex)
            {
                HostLogger.Error("CreateChannelAsync failed: " + ex.Message);
                return 0;
            }
        }

        public async Task<bool> DeleteChannelAsync(DiscordDeleteChannelRequest request)
        {
            try
            {
                var guild = GetGuild();
                if (guild == null)
                    return false;

                var channel = guild.GetChannel(request.ChannelId) as IGuildChannel;
                if (channel == null)
                    return false;

                await channel.DeleteAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                HostLogger.Error("DeleteChannelAsync failed: " + ex.Message);
                return false;
            }
        }

        public DiscordChannelInfo GetChannelInfo(DiscordChannelQueryRequest request)
        {
            try
            {
                var guild = GetGuild();
                if (guild == null || request.ChannelId == 0)
                    return null;

                var channel = guild.GetChannel(request.ChannelId);
                if (channel == null)
                    return null;

                var kind = GetChannelKind(channel);
                if (request.ExpectedKind != DiscordChannelKind.Unknown && kind != request.ExpectedKind)
                    return null;

                return new DiscordChannelInfo
                {
                    ChannelId = channel.Id,
                    ChannelName = channel.Name,
                    ChannelKind = kind,
                };
            }
            catch (Exception ex)
            {
                HostLogger.Error("GetChannelInfo failed: " + ex.Message);
                return null;
            }
        }

        public ulong FindChannelByName(DiscordChannelQueryRequest request)
        {
            try
            {
                var guild = GetGuild();
                if (guild == null || string.IsNullOrWhiteSpace(request.ChannelName))
                    return 0;

                switch (request.ExpectedKind)
                {
                    case DiscordChannelKind.Text:
                        var hyphenated = request.ChannelName.Replace(' ', '-');
                        var text = guild.TextChannels.FirstOrDefault(c =>
                            string.Equals(c.Name, request.ChannelName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(c.Name, hyphenated, StringComparison.OrdinalIgnoreCase));
                        return text != null ? text.Id : 0;

                    case DiscordChannelKind.Voice:
                        var voice = guild.VoiceChannels.FirstOrDefault(c =>
                            string.Equals(c.Name, request.ChannelName, StringComparison.OrdinalIgnoreCase));
                        return voice != null ? voice.Id : 0;

                    default:
                        return 0;
                }
            }
            catch (Exception ex)
            {
                HostLogger.Error("FindChannelByName failed: " + ex.Message);
                return 0;
            }
        }

        public async Task<bool> AssignRoleAsync(DiscordAssignRoleRequest request)
        {
            try
            {
                var guild = GetGuild();
                if (guild == null)
                    return false;

                var user = guild.GetUser(request.UserId);
                var role = guild.GetRole(request.RoleId);
                if (user == null || role == null)
                    return false;

                if (!user.Roles.Any(r => r.Id == role.Id))
                    await user.AddRoleAsync(role).ConfigureAwait(false);

                return true;
            }
            catch (Exception ex)
            {
                HostLogger.Error("AssignRoleAsync failed: " + ex.Message);
                return false;
            }
        }

        public async Task<bool> SyncRoleMembersAsync(DiscordSyncRoleMembersRequest request)
        {
            try
            {
                var guild = GetGuild();
                if (guild == null)
                    return false;

                var role = guild.GetRole(request.RoleId);
                if (role == null)
                    return false;

                var desiredUserIds = new HashSet<ulong>(request.DesiredUserIds ?? new List<ulong>());

                foreach (var user in guild.Users.Where(u => !u.IsBot))
                {
                    var hasRole = user.Roles.Any(r => r.Id == role.Id);
                    var shouldHaveRole = desiredUserIds.Contains(user.Id);

                    if (shouldHaveRole && !hasRole)
                    {
                        await user.AddRoleAsync(role).ConfigureAwait(false);
                    }
                    else if (!shouldHaveRole && hasRole)
                    {
                        await user.RemoveRoleAsync(role).ConfigureAwait(false);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                HostLogger.Error("SyncRoleMembersAsync failed: " + ex.Message);
                return false;
            }
        }

        public async Task<ulong> GetOrCreateVerifiedRoleAsync()
        {
            try
            {
                var guild = GetGuild();
                if (guild == null)
                    return 0;

                var role = guild.Roles.FirstOrDefault(r =>
                    string.Equals(r.Name, "Verified", StringComparison.OrdinalIgnoreCase));
                if (role != null)
                    return role.Id;

                var created = await guild.CreateRoleAsync(
                    "Verified",
                    color: new Color(0, 176, 240),
                    isHoisted: false,
                    isMentionable: false).ConfigureAwait(false);
                return created.Id;
            }
            catch (Exception ex)
            {
                HostLogger.Error("GetOrCreateVerifiedRoleAsync failed: " + ex.Message);
                return 0;
            }
        }

        public async Task<bool> UpdateChannelNameAsync(DiscordUpdateChannelNameRequest request)
        {
            try
            {
                var guild = GetGuild();
                if (guild == null)
                    return false;

                var channel = guild.GetChannel(request.ChannelId) as IGuildChannel;
                if (channel == null)
                    return false;

                await channel.ModifyAsync(props => { props.Name = request.NewName; }).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                HostLogger.Error("UpdateChannelNameAsync failed: " + ex.Message);
                return false;
            }
        }

        public async Task<bool> UpdatePresenceAsync(DiscordUpdatePresenceRequest request)
        {
            try
            {
                if (!EnsureReady() || request == null || string.IsNullOrWhiteSpace(request.StatusText))
                    return false;

                await _client.SetStatusAsync(UserStatus.Online).ConfigureAwait(false);
                await _client.SetGameAsync(
                    request.StatusText,
                    null,
                    ActivityType.Watching).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                HostLogger.Error("UpdatePresenceAsync failed: " + ex.Message);
                return false;
            }
        }

        private async Task<bool> ConnectCoreAsync()
        {
            if (_isConnected || _config == null)
                return true;

            const GatewayIntents messageContentIntent = (GatewayIntents)32768;

            var config = new DiscordSocketConfig
            {
                GatewayIntents =
                    GatewayIntents.DirectMessages
                    | GatewayIntents.Guilds
                    | GatewayIntents.GuildMessages
                    | GatewayIntents.GuildMembers
                    | GatewayIntents.GuildPresences
                    | messageContentIntent,
                AlwaysDownloadUsers = false,
            };

            _client = new DiscordSocketClient(config);
            _client.Log += OnClientLog;
            _client.Ready += OnBotReady;
            _client.Disconnected += OnBotDisconnected;
            _client.MessageReceived += OnMessageReceivedAsync;
            _client.SlashCommandExecuted += OnSlashCommandExecutedAsync;
            _client.UserJoined += OnUserJoinedAsync;

            await _client.LoginAsync(TokenType.Bot, _config.BotToken).ConfigureAwait(false);
            await _client.StartAsync().ConfigureAwait(false);

            _isConnected = true;
            _applicationCommandsRegistered = false;
            await PublishStateAsync().ConfigureAwait(false);
            HostLogger.Info("Discord gateway start requested.");
            return true;
        }

        private async Task DisconnectCoreAsync()
        {
            if (_client == null)
            {
                _isConnected = false;
                _isReady = false;
                await PublishStateAsync().ConfigureAwait(false);
                return;
            }

            try
            {
                _client.Log -= OnClientLog;
                _client.Ready -= OnBotReady;
                _client.Disconnected -= OnBotDisconnected;
                _client.MessageReceived -= OnMessageReceivedAsync;
                _client.SlashCommandExecuted -= OnSlashCommandExecutedAsync;
                _client.UserJoined -= OnUserJoinedAsync;

                try
                {
                    await _client.LogoutAsync().ConfigureAwait(false);
                }
                catch
                {
                    // ignored
                }

                try
                {
                    await _client.StopAsync().ConfigureAwait(false);
                }
                catch
                {
                    // ignored
                }
            }
            finally
            {
                _client.Dispose();
                _client = null;
                _isConnected = false;
                _isReady = false;
                _applicationCommandsRegistered = false;
                await PublishStateAsync().ConfigureAwait(false);
            }
        }

        private bool EnsureReady()
        {
            return _isReady && _client != null && _config != null;
        }

        private SocketGuild GetGuild()
        {
            if (!EnsureReady())
                return null;

            return _client.GetGuild(_config.GuildId);
        }

        private async Task OnBotReady()
        {
            _isConnected = true;
            _isReady = true;
            await PublishStateAsync().ConfigureAwait(false);

            try
            {
                await _client.SetStatusAsync(UserStatus.Online).ConfigureAwait(false);
                await _client.SetGameAsync(
                    "/tds help",
                    null,
                    ActivityType.Listening).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HostLogger.Warn("Failed to update Discord presence: " + ex.Message);
            }

            try
            {
                await RegisterApplicationCommandsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HostLogger.Warn("Failed to register application commands: " + ex.Message);
            }

            try
            {
                var guild = _client.GetGuild(_config.GuildId);
                if (guild != null)
                    _ = guild.DownloadUsersAsync();
            }
            catch (Exception ex)
            {
                HostLogger.Warn("Failed to warm guild user cache: " + ex.Message);
            }
        }

        private Task OnBotDisconnected(Exception exception)
        {
            _isConnected = false;
            _isReady = false;
            _ = PublishStateAsync();

            if (_suppressReconnect)
                return Task.CompletedTask;

            if (Interlocked.Exchange(ref _isReconnecting, 1) == 1)
                return Task.CompletedTask;

            _ = Task.Run(async delegate
            {
                try
                {
                    for (var attempt = 1; attempt <= 5; attempt++)
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                            if (_client != null && _client.ConnectionState != ConnectionState.Connected)
                            {
                                await _client.StartAsync().ConfigureAwait(false);
                                HostLogger.Info(
                                    string.Format("Reconnect attempt {0}/5 requested.", attempt));
                                return;
                            }
                        }
                        catch (Exception reconnectEx)
                        {
                            HostLogger.Warn(
                                string.Format(
                                    "Reconnect attempt {0}/5 failed: {1}",
                                    attempt,
                                    reconnectEx.Message));
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _isReconnecting, 0);
                }
            });

            return Task.CompletedTask;
        }

        private async Task OnMessageReceivedAsync(SocketMessage message)
        {
            try
            {
                if (message.Author.IsBot)
                    return;

                if (message.Channel is IDMChannel)
                {
                    await HandleDirectMessageAsync(message).ConfigureAwait(false);
                    return;
                }

                if (IsAdminCommandChannel(message.Channel.Id))
                {
                    await HandleLegacyAdminChannelMessageAsync(message).ConfigureAwait(false);
                    return;
                }

                if (MessageReceived != null)
                {
                    var incoming = new DiscordIncomingMessage
                    {
                        ChannelId = message.Channel.Id,
                        ChannelName = message.Channel.Name,
                        Content = message.Content ?? string.Empty,
                        IsDirectMessage = false,
                        AuthorId = message.Author.Id,
                        AuthorUsername = message.Author.Username,
                        AuthorDiscriminator = message.Author.Discriminator,
                        AuthorIsBot = message.Author.IsBot,
                    };

                    await MessageReceived.Invoke(incoming).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(message.Content))
                    return;
            }
            catch (Exception ex)
            {
                HostLogger.Error("OnMessageReceivedAsync failed: " + ex.Message);
            }
        }

        private async Task HandleDirectMessageAsync(SocketMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
                return;

            var normalized = message.Content.Trim();
            var verifyPrefix = _config.BotPrefix + "verify";
            var helpPrefix = _config.BotPrefix + "help";
            if (!normalized.StartsWith(verifyPrefix, StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith(helpPrefix, StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith("verify", StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith("help", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            
            await message.Author.SendMessageAsync(
                "Text commands are no longer supported here. Use the `/verify` application command and enter your verification code.")
                .ConfigureAwait(false);
        }

        private static async Task OnUserJoinedAsync(SocketGuildUser user)
        {
            try
            {
                var embed = new EmbedBuilder()
                    .WithColor(Color.Gold)
                    .WithTitle("Welcome")
                    .WithDescription("Welcome to the Space Engineers community.")
                    .AddField("Link Your Account", "Use `/tds verify @YourDiscordName` in-game")
                    .AddField("Complete Verification", "Use `/verify` after the bot DMs your code")
                    .Build();

                await user.SendMessageAsync(embed: embed).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HostLogger.Warn("OnUserJoinedAsync failed: " + ex.Message);
            }
        }

        private Task OnClientLog(LogMessage message)
        {
            var line = $"[{message.Severity}] {message.Message}";
            if (message.Exception != null)
                line += " | " + message.Exception.Message;

            switch (message.Severity)
            {
                case LogSeverity.Critical:
                case LogSeverity.Error:
                    HostLogger.Error(line);
                    break;
                case LogSeverity.Warning:
                    HostLogger.Warn(line);
                    break;
                default:
                    HostLogger.Info(line);
                    break;
            }

            return Task.CompletedTask;
        }

        private async Task PublishStateAsync()
        {
            if (ConnectionStateChanged == null)
                return;

            await ConnectionStateChanged.Invoke(GetConnectionState()).ConfigureAwait(false);
        }

        private async Task SendVerificationPendingAsync(IUser author)
        {
            var embed = new EmbedBuilder()
                .WithColor(Color.Blue)
                .WithTitle("Verifying")
                .WithDescription("Your verification code is being processed.")
                .WithFooter("You will receive a confirmation shortly")
                .Build();

            await author.SendMessageAsync(embed: embed).ConfigureAwait(false);
        }

        private async Task RegisterApplicationCommandsAsync()
        {
            if (_applicationCommandsRegistered || _client == null || _config == null)
                return;

            var guild = _client.GetGuild(_config.GuildId);
            if (guild == null)
            {
                HostLogger.Warn("Application command registration skipped because the guild is unavailable.");
                return;
            }

            var guildCommands = new ApplicationCommandProperties[]
            {
                BuildTdsCommand().Build(),
                BuildVerifyCommand(allowDm: false).Build(),
            };
            await guild.BulkOverwriteApplicationCommandAsync(guildCommands).ConfigureAwait(false);

            var globalCommands = new ApplicationCommandProperties[]
            {
                BuildVerifyCommand(allowDm: true).Build(),
            };
            await ((IDiscordClient)_client).BulkOverwriteGlobalApplicationCommand(globalCommands)
                .ConfigureAwait(false);

            _applicationCommandsRegistered = true;
            HostLogger.Info("Discord application commands registered.");
        }

        private async Task OnSlashCommandExecutedAsync(SocketSlashCommand command)
        {
            try
            {
                if (command?.User == null || command.User.IsBot)
                    return;

                switch (command.CommandName)
                {
                    case "verify":
                        await HandleVerifySlashCommandAsync(command).ConfigureAwait(false);
                        break;

                    case "tds":
                        await HandleTdsSlashCommandAsync(command).ConfigureAwait(false);
                        break;

                    default:
                        await command.RespondAsync("Unknown command.", ephemeral: true).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                HostLogger.Error("OnSlashCommandExecutedAsync failed: " + ex.Message);
                try
                {
                    if (command != null && !command.HasResponded)
                    {
                        await command.RespondAsync(
                            "Command handling failed. Check the host log for details.",
                            ephemeral: true).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // ignored
                }
            }
        }

        private async Task HandleVerifySlashCommandAsync(SocketSlashCommand command)
        {
            var code = GetOptionValue(command.Data.Options, "code");
            if (string.IsNullOrWhiteSpace(code))
            {
                await command.RespondAsync(
                    "Use `/verify` and provide the verification code from the bot DM.",
                    ephemeral: command.GuildId.HasValue).ConfigureAwait(false);
                return;
            }

            await command.RespondAsync(
                "Your verification code is being processed. You will receive a confirmation shortly.",
                ephemeral: command.GuildId.HasValue).ConfigureAwait(false);

            await PublishVerificationAttemptAsync(
                code.Trim().ToUpperInvariant(),
                command.User.Id,
                command.User.Username).ConfigureAwait(false);
        }

        private async Task HandleTdsSlashCommandAsync(SocketSlashCommand command)
        {
            if (_config == null || _config.AdminBotChannelId == 0)
            {
                await command.RespondAsync(
                    "Discord admin commands are disabled because `AdminBotChannelId` is not configured.",
                    ephemeral: true).ConfigureAwait(false);
                return;
            }

            if (command.ChannelId != _config.AdminBotChannelId)
            {
                await command.RespondAsync(
                    "Use `/tds` in <#" + _config.AdminBotChannelId + ">.",
                    ephemeral: true).ConfigureAwait(false);
                return;
            }

            if (!TryBuildAdminCommandText(command, out var content, out var displayText))
            {
                await command.RespondAsync(
                    "Unsupported `/tds` subcommand. Use `/tds help`.",
                    ephemeral: true).ConfigureAwait(false);
                return;
            }

            await command.RespondAsync(
                "Executing `" + displayText + "`...",
                ephemeral: true).ConfigureAwait(false);

            if (MessageReceived == null)
                return;

            var incoming = new DiscordIncomingMessage
            {
                ChannelId = command.ChannelId.GetValueOrDefault(),
                ChannelName = command.Channel is SocketGuildChannel guildChannel
                    ? guildChannel.Name
                    : "Direct Message",
                Content = content,
                IsDirectMessage = false,
                AuthorId = command.User.Id,
                AuthorUsername = command.User.Username,
                AuthorDiscriminator = command.User.Discriminator,
                AuthorIsBot = false,
            };

            await MessageReceived.Invoke(incoming).ConfigureAwait(false);
        }

        private async Task PublishVerificationAttemptAsync(string code, ulong discordUserId, string username)
        {
            if (VerificationAttemptReceived == null)
                return;

            await VerificationAttemptReceived.Invoke(
                new DiscordVerificationAttempt
                {
                    VerificationCode = code,
                    DiscordUserId = discordUserId,
                    DiscordUsername = username,
                }).ConfigureAwait(false);
        }

        private async Task HandleLegacyAdminChannelMessageAsync(SocketMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
                return;

            if (!message.Content.StartsWith(
                    _config.BotPrefix + "tds",
                    StringComparison.OrdinalIgnoreCase))
                return;

            await message.Channel.SendMessageAsync(
                $"{message.Author.Mention} text admin commands are disabled here. Use `/tds` application commands instead.")
                .ConfigureAwait(false);
        }

        private bool IsAdminCommandChannel(ulong channelId)
        {
            return _config != null
                   && _config.AdminBotChannelId != 0
                   && channelId == _config.AdminBotChannelId;
        }

        private static SlashCommandBuilder BuildVerifyCommand(bool allowDm)
        {
            var builder = new SlashCommandBuilder()
                .WithName("verify")
                .WithDescription("Complete Torch Discord Sync account verification")
                .AddOption(
                    "code",
                    ApplicationCommandOptionType.String,
                    "The verification code from the bot DM",
                    isRequired: false);

            if (allowDm)
            {
                builder.WithContextTypes(
                    InteractionContextType.Guild,
                    InteractionContextType.BotDm,
                    InteractionContextType.PrivateChannel);
            }
            else
            {
                builder.WithContextTypes(InteractionContextType.Guild);
            }

            return builder;
        }

        private static SlashCommandBuilder BuildTdsCommand()
        {
            var builder = new SlashCommandBuilder()
                .WithName("tds")
                .WithDescription("Torch Discord Sync admin commands")
                .WithContextTypes(InteractionContextType.Guild);

            builder.AddOption(BuildSubCommand("help", "Show available admin commands"));
            builder.AddOption(BuildSubCommand("status", "Show Torch Discord Sync status"));
            builder.AddOption(BuildSubCommand("cleanup", "Clean up orphaned Discord roles and channels"));
            builder.AddOption(BuildSubCommand("reset", "Reset Discord faction roles and channels"));
            builder.AddOption(BuildSubCommand("reload", "Reload plugin configuration"));
            builder.AddOption(
                BuildSubCommand(
                    "unverify",
                    "Remove a verification",
                    new SlashCommandOptionBuilder()
                        .WithName("steamid")
                        .WithDescription("SteamID to unverify")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true),
                    new SlashCommandOptionBuilder()
                        .WithName("reason")
                        .WithDescription("Reason for the removal")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(false)));

            builder.AddOption(
                new SlashCommandOptionBuilder()
                    .WithName("sync")
                    .WithDescription("Faction synchronization commands")
                    .WithType(ApplicationCommandOptionType.SubCommandGroup)
                    .AddOption(BuildSubCommand("run", "Synchronize all factions to Discord"))
                    .AddOption(BuildSubCommand("check", "Check sync state"))
                    .AddOption(BuildSubCommand("status", "Show sync status"))
                    .AddOption(
                        BuildSubCommand(
                            "undo",
                            "Undo sync for one faction",
                            new SlashCommandOptionBuilder()
                                .WithName("faction-tag")
                                .WithDescription("Faction tag to undo")
                                .WithType(ApplicationCommandOptionType.String)
                                .WithRequired(true)))
                    .AddOption(BuildSubCommand("undoall", "Undo sync for all factions")));

            builder.AddOption(
                new SlashCommandOptionBuilder()
                    .WithName("verify")
                    .WithDescription("Verification administration commands")
                    .WithType(ApplicationCommandOptionType.SubCommandGroup)
                    .AddOption(BuildSubCommand("list", "List verified users"))
                    .AddOption(BuildSubCommand("pending", "List pending verifications"))
                    .AddOption(
                        BuildSubCommand(
                            "delete",
                            "Delete a verification record",
                            new SlashCommandOptionBuilder()
                                .WithName("steamid")
                                .WithDescription("SteamID to delete")
                                .WithType(ApplicationCommandOptionType.String)
                                .WithRequired(true))));

            return builder;
        }

        private static SlashCommandOptionBuilder BuildSubCommand(
            string name,
            string description,
            params SlashCommandOptionBuilder[] options)
        {
            var builder = new SlashCommandOptionBuilder()
                .WithName(name)
                .WithDescription(description)
                .WithType(ApplicationCommandOptionType.SubCommand);

            if (options != null)
            {
                foreach (var option in options)
                {
                    builder.AddOption(option);
                }
            }

            return builder;
        }

        private static bool TryBuildAdminCommandText(
            SocketSlashCommand command,
            out string content,
            out string displayText)
        {
            content = null;
            displayText = "/tds help";

            var rootOption = command.Data.Options.FirstOrDefault();
            if (rootOption == null)
            {
                content = "!tds help";
                return true;
            }

            if (rootOption.Type == ApplicationCommandOptionType.SubCommand)
            {
                switch (rootOption.Name)
                {
                    case "help":
                        content = "!tds help";
                        displayText = "/tds help";
                        return true;
                    case "status":
                        content = "!tds status";
                        displayText = "/tds status";
                        return true;
                    case "cleanup":
                        content = "!tds sync:cleanup";
                        displayText = "/tds cleanup";
                        return true;
                    case "reset":
                        content = "!tds reset";
                        displayText = "/tds reset";
                        return true;
                    case "reload":
                        content = "!tds reload";
                        displayText = "/tds reload";
                        return true;
                    case "unverify":
                        var unverifySteamId = GetOptionValue(rootOption.Options, "steamid");
                        if (string.IsNullOrWhiteSpace(unverifySteamId))
                            return false;

                        var reason = GetOptionValue(rootOption.Options, "reason");
                        content = string.IsNullOrWhiteSpace(reason)
                            ? "!tds unverify " + unverifySteamId
                            : "!tds unverify " + unverifySteamId + " " + reason;
                        displayText = string.IsNullOrWhiteSpace(reason)
                            ? "/tds unverify steamid:" + unverifySteamId
                            : "/tds unverify steamid:" + unverifySteamId + " reason:" + reason;
                        return true;
                }

                return false;
            }

            if (rootOption.Type != ApplicationCommandOptionType.SubCommandGroup)
                return false;

            var subCommand = rootOption.Options.FirstOrDefault();
            if (subCommand == null || subCommand.Type != ApplicationCommandOptionType.SubCommand)
                return false;

            switch (rootOption.Name)
            {
                case "sync":
                    switch (subCommand.Name)
                    {
                        case "run":
                            content = "!tds sync";
                            displayText = "/tds sync run";
                            return true;
                        case "check":
                            content = "!tds sync:check";
                            displayText = "/tds sync check";
                            return true;
                        case "status":
                            content = "!tds sync:status";
                            displayText = "/tds sync status";
                            return true;
                        case "undo":
                            var factionTag = GetOptionValue(subCommand.Options, "faction-tag");
                            if (string.IsNullOrWhiteSpace(factionTag))
                                return false;

                            content = "!tds sync:undo " + factionTag;
                            displayText = "/tds sync undo faction-tag:" + factionTag;
                            return true;
                        case "undoall":
                            content = "!tds sync:undo_all";
                            displayText = "/tds sync undoall";
                            return true;
                    }

                    return false;

                case "verify":
                    switch (subCommand.Name)
                    {
                        case "list":
                            content = "!tds verify:list";
                            displayText = "/tds verify list";
                            return true;
                        case "pending":
                            content = "!tds verify:pending";
                            displayText = "/tds verify pending";
                            return true;
                        case "delete":
                            var steamId = GetOptionValue(subCommand.Options, "steamid");
                            if (string.IsNullOrWhiteSpace(steamId))
                                return false;

                            content = "!tds verify:delete " + steamId;
                            displayText = "/tds verify delete steamid:" + steamId;
                            return true;
                    }

                    return false;
            }

            return false;
        }

        private static string GetOptionValue(
            IReadOnlyCollection<SocketSlashCommandDataOption> options,
            string name)
        {
            if (options == null)
                return null;

            var option = options.FirstOrDefault(o =>
                string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
            return option?.Value?.ToString();
        }

        private SocketGuildUser FindUserByUsername(string searchTerm)
        {
            try
            {
                var guild = GetGuild();
                if (guild == null || string.IsNullOrWhiteSpace(searchTerm))
                    return null;

                var search = searchTerm.ToLowerInvariant().Replace("@", string.Empty).Trim();
                if (ulong.TryParse(search, out var userId))
                    return guild.GetUser(userId);

                foreach (var user in guild.Users)
                {
                    if (user.IsBot)
                        continue;

                    if (string.Equals(user.Username, search, StringComparison.OrdinalIgnoreCase))
                        return user;

                    if (!string.IsNullOrWhiteSpace(user.Nickname)
                        && string.Equals(user.Nickname, search, StringComparison.OrdinalIgnoreCase))
                        return user;
                }

                return guild.Users.FirstOrDefault(user =>
                    !user.IsBot
                    && !string.IsNullOrWhiteSpace(user.Username)
                    && user.Username.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch (Exception ex)
            {
                HostLogger.Warn("FindUserByUsername failed: " + ex.Message);
                return null;
            }
        }

        private static Embed BuildEmbed(DiscordEmbedModel model)
        {
            var builder = new EmbedBuilder();

            if (model == null)
                return builder.Build();

            if (!string.IsNullOrWhiteSpace(model.Title))
                builder.WithTitle(model.Title);

            if (!string.IsNullOrWhiteSpace(model.Description))
                builder.WithDescription(model.Description);

            if (model.ColorRgb != 0)
                builder.WithColor(new Color((uint)model.ColorRgb));

            if (!string.IsNullOrWhiteSpace(model.Footer))
                builder.WithFooter(model.Footer);

            if (model.IncludeTimestamp)
                builder.WithTimestamp(DateTime.UtcNow);

            if (model.Fields != null)
            {
                foreach (var field in model.Fields)
                {
                    builder.AddField(field.Name ?? string.Empty, field.Value ?? string.Empty, field.Inline);
                }
            }

            return builder.Build();
        }

        private static DiscordChannelKind GetChannelKind(SocketGuildChannel channel)
        {
            return channel switch
            {
                SocketTextChannel => DiscordChannelKind.Text,
                SocketCategoryChannel => DiscordChannelKind.Category,
                _ => DiscordChannelKind.Unknown
            };
        }

        private static async Task<ulong> CreateTextChannelAsync(
            SocketGuild guild,
            DiscordCreateChannelRequest request)
        {
            RestTextChannel channel;

            if (request.CategoryId.HasValue && request.CategoryId.Value > 0)
            {
                var category = guild.GetCategoryChannel(request.CategoryId.Value);
                channel = category != null
                    ? await guild.CreateTextChannelAsync(
                        request.ChannelName,
                        props => { props.CategoryId = category.Id; }).ConfigureAwait(false)
                    : await guild.CreateTextChannelAsync(request.ChannelName).ConfigureAwait(false);
            }
            else
            {
                channel = await guild.CreateTextChannelAsync(request.ChannelName).ConfigureAwait(false);
            }

            if (request.RoleId.HasValue && request.RoleId.Value > 0)
            {
                var role = guild.GetRole(request.RoleId.Value);
                if (role != null)
                {
                    await channel.AddPermissionOverwriteAsync(
                        guild.EveryoneRole,
                        new OverwritePermissions(viewChannel: PermValue.Deny)).ConfigureAwait(false);
                    await channel.AddPermissionOverwriteAsync(
                        role,
                        new OverwritePermissions(
                            viewChannel: PermValue.Allow,
                            sendMessages: PermValue.Allow)).ConfigureAwait(false);
                }
            }

            return channel.Id;
        }

        private static async Task<ulong> CreateVoiceChannelAsync(
            SocketGuild guild,
            DiscordCreateChannelRequest request)
        {
            RestVoiceChannel channel;

            if (request.CategoryId.HasValue && request.CategoryId.Value > 0)
            {
                var category = guild.GetCategoryChannel(request.CategoryId.Value);
                channel = category != null
                    ? await guild.CreateVoiceChannelAsync(
                        request.ChannelName,
                        props => { props.CategoryId = category.Id; }).ConfigureAwait(false)
                    : await guild.CreateVoiceChannelAsync(request.ChannelName).ConfigureAwait(false);
            }
            else
            {
                channel = await guild.CreateVoiceChannelAsync(request.ChannelName).ConfigureAwait(false);
            }

            if (request.RoleId.HasValue && request.RoleId.Value > 0)
            {
                var role = guild.GetRole(request.RoleId.Value);
                if (role != null)
                {
                    await channel.AddPermissionOverwriteAsync(
                        guild.EveryoneRole,
                        new OverwritePermissions(viewChannel: PermValue.Deny)).ConfigureAwait(false);
                    await channel.AddPermissionOverwriteAsync(
                        role,
                        new OverwritePermissions(
                            viewChannel: PermValue.Allow,
                            connect: PermValue.Allow,
                            speak: PermValue.Allow)).ConfigureAwait(false);
                }
            }

            return channel.Id;
        }
    }
}
