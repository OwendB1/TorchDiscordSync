using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using TorchDiscordSync.Plugin.Utils;
using TorchDiscordSync.Shared.Ipc;

namespace TorchDiscordSync.Plugin.Services
{
    /// <summary>
    /// Thin Torch-side wrapper that launches the external Discord host process
    /// and communicates with it over a named pipe.
    /// </summary>
    public class DiscordBotService
    {
        private const string HostExecutableFileName = "TorchDiscordSync.DiscordHost.exe";
        private const string HostManagedDllFileName = "TorchDiscordSync.DiscordHost.dll";
        private const string HostAppHostFileName = "TorchDiscordSync.DiscordHost";
        private const string PluginManifestGuid = "07ce2bbd-f606-418d-aff1-ea95bfc5795d";
        private static readonly string[] PluginArchiveFileNames =
        {
            "plugin.zip",
            "TorchDiscordSync (linux compatible).zip",
        };

        private readonly SemaphoreSlim _lifecycleLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly object _eventDispatchLock = new object();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<DiscordIpcEnvelope>>
            _pendingRequests =
                new ConcurrentDictionary<string, TaskCompletionSource<DiscordIpcEnvelope>>();

        private DiscordRuntimeConfig _config;
        private DiscordConnectionState _connectionState = new DiscordConnectionState();
        private Task _eventDispatchTask = Task.FromResult(0);
        private CancellationTokenSource _pipeCancellation;
        private Task _readerTask;
        private NamedPipeClientStream _pipeStream;
        private Process _hostProcess;
        private string _pipeName;

        public event Func<DiscordIncomingMessage, Task> OnMessageReceivedEvent;
        public event Action<string, ulong, string> OnVerificationAttempt;
        public event Action<DiscordConnectionState> OnConnectionStateChanged;

        public DiscordBotService(DiscordRuntimeConfig config)
        {
            _config = config;
        }

        public bool IsConnected
        {
            get { return _connectionState != null && _connectionState.IsConnected; }
        }

        public bool IsReady
        {
            get { return _connectionState != null && _connectionState.IsReady; }
        }

        public async Task<bool> StartAsync()
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_pipeStream != null && _pipeStream.IsConnected)
                {
                    return await InitializeOrUpdateAsync(DiscordIpcOperations.Initialize)
                        .ConfigureAwait(false);
                }

                if (_pipeStream != null || _hostProcess != null)
                    await StopInternalAsync(false).ConfigureAwait(false);

                if (_config == null)
                {
                    LoggerUtil.LogError("[DISCORD_IPC] Discord runtime config is missing.");
                    return false;
                }

                _pipeName = "tds-" + Guid.NewGuid().ToString("N");
                if (!TryCreateHostProcessStartInfo(out var startInfo))
                {
                    LoggerUtil.LogError("[DISCORD_IPC] Discord host executable was not found.");
                    return false;
                }

                LoggerUtil.LogInfo(
                    "[DISCORD_IPC] Launching Discord host using " + startInfo.FileName);

                _hostProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                _hostProcess.Exited += OnHostProcessExited;

                if (!_hostProcess.Start())
                {
                    LoggerUtil.LogError("[DISCORD_IPC] Failed to start Discord host process.");
                    CleanupProcess();
                    return false;
                }

                if (startInfo.RedirectStandardOutput)
                {
                    _hostProcess.OutputDataReceived += OnHostOutputDataReceived;
                    _hostProcess.BeginOutputReadLine();
                }

                if (startInfo.RedirectStandardError)
                {
                    _hostProcess.ErrorDataReceived += OnHostErrorDataReceived;
                    _hostProcess.BeginErrorReadLine();
                }

                _pipeStream = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

                await Task.Run(delegate { _pipeStream.Connect(10000); }).ConfigureAwait(false);

                _pipeCancellation = new CancellationTokenSource();
                _readerTask = Task.Run(
                    delegate { return ReadLoopAsync(_pipeCancellation.Token); },
                    _pipeCancellation.Token);

                return await InitializeOrUpdateAsync(DiscordIpcOperations.Initialize)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LoggerUtil.LogException("[DISCORD_IPC] StartAsync failed.", ex);
                await StopInternalAsync(false).ConfigureAwait(false);
                return false;
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task<bool> StopAsync()
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await StopInternalAsync(true).ConfigureAwait(false);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task<bool> UpdateConfigurationAsync(DiscordRuntimeConfig config)
        {
            _config = config;

            if (!await EnsureStartedAsync().ConfigureAwait(false))
                return false;

            return await InitializeOrUpdateAsync(DiscordIpcOperations.UpdateConfiguration)
                .ConfigureAwait(false);
        }

        public async Task<DiscordConnectionState> GetConnectionStateAsync()
        {
            if (!await EnsureStartedAsync().ConfigureAwait(false))
                return new DiscordConnectionState();

            var response = await SendRequestAsync(
                    DiscordIpcOperations.GetConnectionState,
                    null,
                    15000)
                .ConfigureAwait(false);

            return response.Payload as DiscordConnectionState ?? new DiscordConnectionState();
        }

        public async Task<bool> SendVerificationDMAsync(string discordUsername, string verificationCode)
        {
            if (!await EnsureStartedAsync().ConfigureAwait(false))
                return false;

            var response = await SendRequestAsync(
                    DiscordIpcOperations.SendVerificationDm,
                    new DiscordVerificationRequest
                    {
                        DiscordUsername = discordUsername,
                        VerificationCode = verificationCode,
                    })
                .ConfigureAwait(false);

            return response.Success;
        }

        public async Task<bool> SendVerificationResultDMAsync(
            string discordUsername,
            ulong discordUserID,
            string resultMessage,
            bool success)
        {
            if (!await EnsureStartedAsync().ConfigureAwait(false))
                return false;

            var response = await SendRequestAsync(
                    DiscordIpcOperations.SendVerificationResultDm,
                    new DiscordVerificationResultMessage
                    {
                        DiscordUsername = discordUsername,
                        DiscordUserId = discordUserID,
                        Message = resultMessage,
                        IsSuccess = success,
                    })
                .ConfigureAwait(false);

            return response.Success;
        }

        public async Task<bool> SendChannelMessageAsync(ulong channelID, string message)
        {
            if (!await EnsureStartedAsync().ConfigureAwait(false))
                return false;

            var response = await SendRequestAsync(
                    DiscordIpcOperations.SendChannelMessage,
                    new DiscordMessageRequest
                    {
                        ChannelId = channelID,
                        Content = message,
                    })
                .ConfigureAwait(false);

            return response.Success;
        }

        public async Task<bool> SendEmbedAsync(ulong channelID, DiscordEmbedModel embed)
        {
            if (!await EnsureStartedAsync().ConfigureAwait(false))
                return false;

            var response = await SendRequestAsync(
                    DiscordIpcOperations.SendEmbedMessage,
                    new DiscordSendEmbedRequest
                    {
                        ChannelId = channelID,
                        Embed = embed,
                    })
                .ConfigureAwait(false);

            return response.Success;
        }

        public async Task<ulong> CreateRoleAsync(string roleName)
        {
            if (!await EnsureStartedAsync().ConfigureAwait(false))
                return 0;

            var response = await SendRequestAsync(
                    DiscordIpcOperations.CreateRole,
                    new DiscordCreateRoleRequest { RoleName = roleName })
                .ConfigureAwait(false);

            return GetIdResult(response);
        }

        public async Task<bool> DeleteRoleAsync(ulong roleID)
        {
            if (!await EnsureStartedAsync().ConfigureAwait(false))
                return false;

            var response = await SendRequestAsync(
                    DiscordIpcOperations.DeleteRole,
                    new DiscordDeleteRoleRequest { RoleId = roleID })
                .ConfigureAwait(false);

            return response.Success;
        }

        public async Task<DiscordRoleInfo> GetRoleInfoAsync(ulong roleId)
        {
            if (!await EnsureStartedAsync().ConfigureAwait(false))
                return null;

            var response = await SendRequestAsync(
                    DiscordIpcOperations.GetRoleInfo,
                    new DiscordRoleQueryRequest { RoleId = roleId })
                .ConfigureAwait(false);

            return response.Payload as DiscordRoleInfo;
        }

        public async Task<ulong> FindRoleByNameAsync(string name)
        {
            if (!await EnsureStartedAsync().ConfigureAwait(false))
                return 0;

            var response = await SendRequestAsync(
                    DiscordIpcOperations.FindRoleByName,
                    new DiscordRoleQueryRequest { RoleName = name })
                .ConfigureAwait(false);

            return GetIdResult(response);
        }

        public async Task<ulong> CreateChannelAsync(
            string channelName,
            DiscordChannelKind channelKind,
            ulong? categoryId,
            ulong? roleId)
        {
            if (!await EnsureStartedAsync().ConfigureAwait(false))
                return 0;

            var response = await SendRequestAsync(
                    DiscordIpcOperations.CreateChannel,
                    new DiscordCreateChannelRequest
                    {
                        ChannelName = channelName,
                        ChannelKind = channelKind,
                        CategoryId = categoryId,
                        RoleId = roleId,
                    })
                .ConfigureAwait(false);

            return GetIdResult(response);
        }

        public async Task<bool> DeleteChannelAsync(ulong channelID)
        {
            if (!await EnsureStartedAsync().ConfigureAwait(false))
                return false;

            var response = await SendRequestAsync(
                    DiscordIpcOperations.DeleteChannel,
                    new DiscordDeleteChannelRequest { ChannelId = channelID })
                .ConfigureAwait(false);

            return response.Success;
        }

        public async Task<DiscordChannelInfo> GetChannelInfoAsync(
            ulong channelId,
            DiscordChannelKind expectedKind)
        {
            if (!await EnsureStartedAsync().ConfigureAwait(false))
                return null;

            var response = await SendRequestAsync(
                    DiscordIpcOperations.GetChannelInfo,
                    new DiscordChannelQueryRequest
                    {
                        ChannelId = channelId,
                        ExpectedKind = expectedKind,
                    })
                .ConfigureAwait(false);

            return response.Payload as DiscordChannelInfo;
        }

        public async Task<ulong> FindChannelByNameAsync(string name, DiscordChannelKind expectedKind)
        {
            if (!await EnsureStartedAsync().ConfigureAwait(false))
                return 0;

            var response = await SendRequestAsync(
                    DiscordIpcOperations.FindChannelByName,
                    new DiscordChannelQueryRequest
                    {
                        ChannelName = name,
                        ExpectedKind = expectedKind,
                    })
                .ConfigureAwait(false);

            return GetIdResult(response);
        }

        public async Task<bool> AssignRoleAsync(ulong userID, ulong roleID)
        {
            if (!await EnsureStartedAsync().ConfigureAwait(false))
                return false;

            var response = await SendRequestAsync(
                    DiscordIpcOperations.AssignRole,
                    new DiscordAssignRoleRequest
                    {
                        UserId = userID,
                        RoleId = roleID,
                    })
                .ConfigureAwait(false);

            return response.Success;
        }

        public async Task<bool> SyncRoleMembersAsync(
            ulong roleId,
            IEnumerable<ulong> desiredUserIds,
            string roleName)
        {
            if (!await EnsureStartedAsync().ConfigureAwait(false))
                return false;

            var response = await SendRequestAsync(
                    DiscordIpcOperations.SyncRoleMembers,
                    new DiscordSyncRoleMembersRequest
                    {
                        RoleId = roleId,
                        RoleName = roleName,
                        DesiredUserIds = desiredUserIds != null
                            ? new List<ulong>(desiredUserIds)
                            : new List<ulong>(),
                    },
                    60000)
                .ConfigureAwait(false);

            return response.Success;
        }

        public async Task<ulong> GetOrCreateVerifiedRoleAsync()
        {
            if (!await EnsureStartedAsync().ConfigureAwait(false))
                return 0;

            var response = await SendRequestAsync(
                    DiscordIpcOperations.GetOrCreateVerifiedRole,
                    null)
                .ConfigureAwait(false);

            return GetIdResult(response);
        }

        public async Task<bool> UpdateChannelNameAsync(ulong channelId, string newName)
        {
            if (!await EnsureStartedAsync().ConfigureAwait(false))
                return false;

            var response = await SendRequestAsync(
                    DiscordIpcOperations.UpdateChannelName,
                    new DiscordUpdateChannelNameRequest
                    {
                        ChannelId = channelId,
                        NewName = newName,
                    })
                .ConfigureAwait(false);

            return response.Success;
        }

        public async Task<bool> UpdatePresenceAsync(string statusText)
        {
            if (!await EnsureStartedAsync().ConfigureAwait(false))
                return false;

            var response = await SendRequestAsync(
                    DiscordIpcOperations.UpdatePresence,
                    new DiscordUpdatePresenceRequest
                    {
                        StatusText = statusText,
                    })
                .ConfigureAwait(false);

            return response.Success;
        }

        private async Task<bool> EnsureStartedAsync()
        {
            if (_pipeStream != null && _pipeStream.IsConnected)
                return true;

            return await StartAsync().ConfigureAwait(false);
        }

        private async Task<bool> InitializeOrUpdateAsync(string operation)
        {
            var response = await SendRequestAsync(operation, _config, 60000).ConfigureAwait(false);
            ApplyConnectionState(response.Payload as DiscordConnectionState);

            if (!response.Success && !string.IsNullOrWhiteSpace(response.Error))
            {
                LoggerUtil.LogError(
                    "[DISCORD_IPC] " + operation + " failed: " + response.Error);
            }

            return response.Success;
        }

        private async Task<DiscordIpcEnvelope> SendRequestAsync(
            string operation,
            object payload,
            int timeoutMs = 30000)
        {
            if (_pipeStream == null || !_pipeStream.IsConnected)
            {
                return CreateLocalFailure(operation, "Discord host pipe is not connected.");
            }

            var requestId = Guid.NewGuid().ToString("N");
            var stopwatch = Stopwatch.StartNew();
            var completionSource = new TaskCompletionSource<DiscordIpcEnvelope>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[requestId] = completionSource;

            try
            {
                await _writeLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    await DiscordIpcSerializer.WriteAsync(
                            _pipeStream,
                            new DiscordIpcEnvelope
                            {
                                Kind = DiscordIpcKinds.Request,
                                RequestId = requestId,
                                Operation = operation,
                                Success = true,
                                Payload = payload,
                            },
                            _pipeCancellation != null
                                ? _pipeCancellation.Token
                                : CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }

                var completedTask = await Task.WhenAny(
                        completionSource.Task,
                        Task.Delay(timeoutMs))
                    .ConfigureAwait(false);

                if (completedTask != completionSource.Task)
                {
                    _pendingRequests.TryRemove(requestId, out _);
                    LoggerUtil.LogWarning(
                        "[DISCORD_IPC] " + operation + " timed out after "
                        + stopwatch.ElapsedMilliseconds + "ms; pending="
                        + _pendingRequests.Count);
                    return CreateLocalFailure(operation, "Timed out waiting for Discord host response.");
                }

                var response = await completionSource.Task.ConfigureAwait(false);
                if (stopwatch.ElapsedMilliseconds >= 2000)
                {
                    LoggerUtil.LogWarning(
                        "[DISCORD_IPC] " + operation + " response took "
                        + stopwatch.ElapsedMilliseconds + "ms; pending="
                        + _pendingRequests.Count);
                }

                return response;
            }
            catch (Exception ex)
            {
                _pendingRequests.TryRemove(requestId, out _);
                LoggerUtil.LogException(
                    "[DISCORD_IPC] Request " + operation + " failed.",
                    ex);
                return CreateLocalFailure(operation, ex.Message);
            }
        }

        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested
                       && _pipeStream != null
                       && _pipeStream.IsConnected)
                {
                    var envelope = await DiscordIpcSerializer.ReadAsync(_pipeStream, cancellationToken)
                        .ConfigureAwait(false);

                    if (envelope == null)
                        break;

                    if (string.Equals(envelope.Kind, DiscordIpcKinds.Response, StringComparison.Ordinal))
                    {
                        if (!string.IsNullOrWhiteSpace(envelope.RequestId)
                            && _pendingRequests.TryRemove(envelope.RequestId, out var pending))
                        {
                            pending.TrySetResult(envelope);
                        }

                        continue;
                    }

                    if (string.Equals(envelope.Kind, DiscordIpcKinds.Event, StringComparison.Ordinal))
                    {
                        QueueEventDispatch(envelope);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LoggerUtil.LogException("[DISCORD_IPC] Pipe read loop failed.", ex);
            }
            finally
            {
                CompletePendingRequests("Discord host pipe closed.");
                ApplyConnectionState(new DiscordConnectionState());
            }
        }

        private void QueueEventDispatch(DiscordIpcEnvelope envelope)
        {
            lock (_eventDispatchLock)
            {
                _eventDispatchTask = _eventDispatchTask
                    .ContinueWith(
                        delegate { return HandleEventAsync(envelope); },
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default)
                    .Unwrap();
            }
        }

        private async Task HandleEventAsync(DiscordIpcEnvelope envelope)
        {
            try
            {
                switch (envelope.Operation)
                {
                    case DiscordIpcEvents.MessageReceived:
                        if (OnMessageReceivedEvent != null)
                        {
                            var message = envelope.Payload as DiscordIncomingMessage;
                            if (message != null)
                                await OnMessageReceivedEvent.Invoke(message).ConfigureAwait(false);
                        }
                        break;

                    case DiscordIpcEvents.VerificationAttempt:
                        var attempt = envelope.Payload as DiscordVerificationAttempt;
                        if (attempt != null)
                        {
                            OnVerificationAttempt?.Invoke(
                                attempt.VerificationCode,
                                attempt.DiscordUserId,
                                attempt.DiscordUsername);
                        }
                        break;

                    case DiscordIpcEvents.ConnectionStateChanged:
                        ApplyConnectionState(envelope.Payload as DiscordConnectionState);
                        break;
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogException("[DISCORD_IPC] Event dispatch failed.", ex);
            }
        }

        private void ApplyConnectionState(DiscordConnectionState state)
        {
            _connectionState = state ?? new DiscordConnectionState();
            OnConnectionStateChanged?.Invoke(_connectionState);
        }

        private async Task<bool> StopInternalAsync(bool requestShutdown)
        {
            try
            {
                if (requestShutdown && _pipeStream != null && _pipeStream.IsConnected)
                {
                    await SendRequestAsync(DiscordIpcOperations.Shutdown, null, 5000)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogException("[DISCORD_IPC] Graceful host shutdown failed.", ex);
            }

            try
            {
                if (_pipeCancellation != null)
                {
                    _pipeCancellation.Cancel();
                    _pipeCancellation.Dispose();
                    _pipeCancellation = null;
                }
                
                if (_pipeStream != null)
                {
                    _pipeStream.Dispose();
                    _pipeStream = null;
                }
                
                if (_readerTask != null)
                {
                    await _readerTask.ConfigureAwait(false);
                    _readerTask = null;
                }
            }
            catch
            {
                // ignored
            }
            
            try
            {
                if (_hostProcess is { HasExited: false })
                {
                    if (!_hostProcess.WaitForExit(5000))
                    {
                        _hostProcess.Kill();
                        _hostProcess.WaitForExit(5000);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogException("[DISCORD_IPC] Host process termination warning.", ex);
            }
            finally
            {
                CleanupProcess();
            }

            CompletePendingRequests("Discord host stopped.");
            ApplyConnectionState(new DiscordConnectionState());
            return true;
        }

        private void OnHostProcessExited(object sender, EventArgs e)
        {
            string exitDetails = string.Empty;

            try
            {
                if (_hostProcess != null)
                    exitDetails = " ExitCode=" + _hostProcess.ExitCode;
            }
            catch
            {
                // ignored
            }

            LoggerUtil.LogWarning("[DISCORD_IPC] Discord host process exited." + exitDetails);
            CompletePendingRequests("Discord host process exited.");
            ApplyConnectionState(new DiscordConnectionState());
        }

        private static void OnHostOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                LoggerUtil.LogInfo("[DISCORD_HOST] " + e.Data);
        }

        private static void OnHostErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                LoggerUtil.LogError("[DISCORD_HOST] " + e.Data);
        }

        private bool TryCreateHostProcessStartInfo(out ProcessStartInfo startInfo)
        {
            startInfo = null;

            if (!TryResolveHostArtifacts(out var workingDirectory, out var executablePath, out var dllPath))
                return false;

            var commonArguments =
                "--pipe " + Quote(_pipeName)
                + " --parent-pid " + Process.GetCurrentProcess().Id
                + " --plugin-dir " + Quote(
                    Config.MainConfig.GetPluginDirectory());

            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = commonArguments,
                    WorkingDirectory = workingDirectory,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                LoggerUtil.LogInfo(
                    "[DISCORD_IPC] Using native Discord host executable at " + executablePath);
                return true;
            }

            if (string.IsNullOrWhiteSpace(dllPath)) return false;
            startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = Quote(dllPath) + " " + commonArguments,
                WorkingDirectory = workingDirectory,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            LoggerUtil.LogInfo(
                "[DISCORD_IPC] Using managed Discord host entrypoint via dotnet at " + dllPath);
            return true;
        }

        private static bool TryResolveHostArtifacts(
            out string workingDirectory,
            out string executablePath,
            out string dllPath)
        {
            workingDirectory = null;
            executablePath = null;
            dllPath = null;

            string assemblyDirectory;
            TryGetAssemblyDirectory(out assemblyDirectory);

            if (string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                LoggerUtil.LogInfo(
                    "[DISCORD_IPC] Plugin assembly location is unavailable. Torch likely loaded this plugin from a zip archive.");
            }
            else
            {
                LoggerUtil.LogDebug(
                    "[DISCORD_IPC] Plugin assembly directory: " + assemblyDirectory);
            }

            var hostBaseDirectories = new List<string>();
            if (!string.IsNullOrWhiteSpace(assemblyDirectory))
                hostBaseDirectories.Add(assemblyDirectory);

            var installedPluginDirectory = TryFindInstalledPluginDirectory();
            if (!string.IsNullOrWhiteSpace(installedPluginDirectory)
                && !ContainsPath(hostBaseDirectories, installedPluginDirectory))
            {
                hostBaseDirectories.Add(installedPluginDirectory);
            }

            foreach (var hostBaseDirectory in hostBaseDirectories)
            {
                try
                {
                    if (TryStageBundledHostArtifacts(
                            hostBaseDirectory,
                            out workingDirectory,
                            out executablePath,
                            out dllPath))
                    {
                        LoggerUtil.LogInfo(
                            "[DISCORD_IPC] Staged Discord host artifacts from directory "
                            + hostBaseDirectory);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogException(
                        "[DISCORD_IPC] Failed to stage Discord host artifacts from "
                        + hostBaseDirectory + ".",
                        ex);
                }
            }

            foreach (var archivePath in GetCandidatePluginArchivePaths(hostBaseDirectories))
            {
                try
                {
                    if (TryStageBundledHostArchive(
                            archivePath,
                            out workingDirectory,
                            out executablePath,
                            out dllPath))
                    {
                        LoggerUtil.LogInfo(
                            "[DISCORD_IPC] Staged Discord host artifacts from archive "
                            + archivePath);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogException(
                        "[DISCORD_IPC] Failed to extract Discord host from archive "
                        + archivePath + ".",
                        ex);
                }
            }

            if (string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                LoggerUtil.LogError(
                    "[DISCORD_IPC] Unable to resolve Discord host artifacts from plugin archives.");
                return false;
            }

            var candidateDirectories = new[]
            {
                Path.Combine(assemblyDirectory, "..", "..", "DiscordHost", "bin", "Debug", "net8.0"),
                Path.Combine(assemblyDirectory, "..", "..", "DiscordHost", "bin", "Debug", "net8.0", "win-x64", "publish"),
                Path.Combine(assemblyDirectory, "..", "..", "DiscordHost", "bin", "Release", "net8.0"),
                Path.Combine(assemblyDirectory, "..", "..", "DiscordHost", "bin", "Release", "net8.0", "win-x64", "publish"),
            };

            foreach (var candidate in candidateDirectories)
            {
                try
                {
                    var fullCandidate = Path.GetFullPath(candidate);
                    if (TrySelectHostEntrypoint(
                            fullCandidate,
                            out workingDirectory,
                            out executablePath,
                            out dllPath))
                    {
                        LoggerUtil.LogInfo(
                            "[DISCORD_IPC] Resolved Discord host artifacts from development output "
                            + fullCandidate);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogDebug(
                        "[DISCORD_IPC] Failed to inspect development Discord host directory "
                        + candidate + ": " + ex.Message);
                }
            }

            LoggerUtil.LogError(
                "[DISCORD_IPC] Unable to resolve Discord host artifacts from directories or archives.");
            return false;
        }

        private static void TryGetAssemblyDirectory(out string assemblyDirectory)
        {
            assemblyDirectory = null;

            try
            {
                var location = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrWhiteSpace(location))
                {
                    assemblyDirectory = Path.GetDirectoryName(location);
                }
            }
            catch
            {
                // ignored
            }
        }

        private static string TryFindInstalledPluginDirectory()
        {
            try
            {
                var torchBaseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                if (string.IsNullOrWhiteSpace(torchBaseDirectory))
                    return null;

                var pluginsDirectory = Path.Combine(torchBaseDirectory, "Plugins");
                if (!Directory.Exists(pluginsDirectory))
                    return null;

                foreach (var pluginDirectory in Directory.GetDirectories(pluginsDirectory))
                {
                    if (IsCurrentPluginDirectory(pluginDirectory))
                        return pluginDirectory;
                }
            }
            catch
            {
                // ignored
            }

            return null;
        }

        private static bool IsCurrentPluginDirectory(string pluginDirectory)
        {
            if (string.IsNullOrWhiteSpace(pluginDirectory) || !Directory.Exists(pluginDirectory))
                return false;

            if (DirectoryContainsHostEntrypoint(pluginDirectory))
                return true;

            if (TryGetPluginArchivePath(pluginDirectory, out var archivePath))
            {
                return true;
            }

            var manifestPath = Path.Combine(pluginDirectory, "manifest.xml");
            return File.Exists(manifestPath) && ManifestMatchesCurrentPlugin(manifestPath);
        }

        private static bool ContainsPath(IEnumerable<string> paths, string candidatePath)
        {
            foreach (var existingPath in paths)
            {
                if (string.Equals(existingPath, candidatePath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IsKnownPluginManifestGuid(string manifestGuid)
        {
            return string.Equals(manifestGuid, PluginManifestGuid, StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> GetCandidatePluginArchivePaths(IEnumerable<string> hostBaseDirectories)
        {
            var archivePaths = new List<string>();

            try
            {
                foreach (var hostBaseDirectory in hostBaseDirectories)
                {
                    AddPluginArchiveCandidates(archivePaths, hostBaseDirectory);
                
                    var parentDirectory = Path.GetDirectoryName(hostBaseDirectory);
                    AddPluginArchiveCandidates(archivePaths, parentDirectory);
                
                }
            
                var torchBaseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrWhiteSpace(torchBaseDirectory))
                    AddPluginArchiveCandidates(archivePaths, Path.Combine(torchBaseDirectory, "Plugins"));
            }
            catch (Exception)
            {
                // ignored
            }

            return archivePaths;
        }

        private static void AddPluginArchiveCandidates(List<string> archivePaths, string directory)
        {
            if (archivePaths == null || string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return;

            AddPluginArchiveCandidatesFromDirectory(archivePaths, directory);

            try
            {
                foreach (var childDirectory in Directory.GetDirectories(directory))
                {
                    AddPluginArchiveCandidatesFromDirectory(archivePaths, childDirectory);
                }
            }
            catch
            {
                // ignored
            }
        }

        private static void AddPluginArchiveCandidatesFromDirectory(
            List<string> archivePaths,
            string directory)
        {
            if (TryGetPluginArchivePath(directory, out var archivePath)
                && !ContainsPath(archivePaths, archivePath))
            {
                archivePaths.Add(archivePath);
            }
        }

        private static bool TryGetPluginArchivePath(string directory, out string archivePath)
        {
            archivePath = null;

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return false;

            foreach (var candidatePath in GetPluginArchiveCandidates(directory))
            {
                try
                {
                    if (!ArchiveContainsCurrentPlugin(candidatePath))
                        continue;

                    archivePath = candidatePath;
                    return true;
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogDebug(
                        "[DISCORD_IPC] Failed to inspect candidate plugin archive "
                        + candidatePath + ": " + ex.Message);
                }
            }

            return false;
        }

        private static List<string> GetPluginArchiveCandidates(string directory)
        {
            var archivePaths = new List<string>();

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return archivePaths;

            foreach (var archiveFileName in PluginArchiveFileNames)
            {
                var candidatePath = Path.Combine(directory, archiveFileName);
                if (File.Exists(candidatePath) && !ContainsPath(archivePaths, candidatePath))
                    archivePaths.Add(candidatePath);
            }

            try
            {
                foreach (var candidatePath in Directory.GetFiles(directory, "*.zip"))
                {
                    if (!ContainsPath(archivePaths, candidatePath))
                        archivePaths.Add(candidatePath);
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogDebug(
                    "[DISCORD_IPC] Failed to enumerate zip files in " + directory + ": "
                    + ex.Message);
            }

            return archivePaths;
        }

        private static bool ManifestMatchesCurrentPlugin(string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
                return false;

            try
            {
                var document = new XmlDocument();
                document.Load(manifestPath);
                return XmlDocumentMatchesCurrentPlugin(document);
            }
            catch
            {
                return false;
            }
        }

        private static bool ArchiveContainsCurrentPlugin(string archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                return false;

            try
            {
                using (var stream = File.OpenRead(archivePath))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry manifestEntry = null;
                    bool foundHostEntrypoint = false;

                    foreach (var entry in archive.Entries)
                    {
                        if (IsHostEntrypointFile(entry.Name))
                            foundHostEntrypoint = true;

                        if (manifestEntry == null
                            && string.Equals(
                                entry.Name,
                                "manifest.xml",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            manifestEntry = entry;
                        }
                    }

                    if (manifestEntry == null)
                        return foundHostEntrypoint;

                    using (var manifestStream = manifestEntry.Open())
                    {
                        var document = new XmlDocument();
                        document.Load(manifestStream);
                        return XmlDocumentMatchesCurrentPlugin(document);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogDebug(
                    "[DISCORD_IPC] Failed to inspect plugin archive " + archivePath + ": "
                    + ex.Message);
                return false;
            }
        }

        private static bool XmlDocumentMatchesCurrentPlugin(XmlDocument document)
        {
            if (document == null)
                return false;

            var guidNode = document.SelectSingleNode("/PluginManifest/Guid");
            return guidNode != null && IsKnownPluginManifestGuid(guidNode.InnerText.Trim());
        }

        private static bool TryStageBundledHostArtifacts(
            string hostBaseDirectory,
            out string workingDirectory,
            out string executablePath,
            out string dllPath)
        {
            workingDirectory = null;
            executablePath = null;
            dllPath = null;

            if (string.IsNullOrWhiteSpace(hostBaseDirectory) || !Directory.Exists(hostBaseDirectory))
                return false;

            if (!DirectoryContainsHostEntrypoint(hostBaseDirectory)) return false;
            var stagedDirectory = StageBundledHostArtifacts(
                hostBaseDirectory,
                GetRootHostArtifactFiles(hostBaseDirectory));
            
            return TrySelectHostEntrypoint(
                stagedDirectory,
                out workingDirectory,
                out executablePath,
                out dllPath);
        }

        private static bool TryStageBundledHostArchive(
            string archivePath,
            out string workingDirectory,
            out string executablePath,
            out string dllPath)
        {
            workingDirectory = null;
            executablePath = null;
            dllPath = null;

            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                return false;

            bool foundHostEntrypoint;
            var stagedDirectory = StageBundledHostArtifactsFromArchive(
                archivePath,
                out foundHostEntrypoint);

            return foundHostEntrypoint
                   && TrySelectHostEntrypoint(
                       stagedDirectory,
                       out workingDirectory,
                       out executablePath,
                       out dllPath);
        }

        private static bool DirectoryContainsHostEntrypoint(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return false;

            return File.Exists(Path.Combine(directory, HostExecutableFileName))
                   || File.Exists(Path.Combine(directory, HostManagedDllFileName))
                   || File.Exists(Path.Combine(directory, HostAppHostFileName))
                   || File.Exists(Path.Combine(directory, HostExecutableFileName + ".payload"))
                   || File.Exists(Path.Combine(directory, HostManagedDllFileName + ".payload"))
                   || File.Exists(Path.Combine(directory, HostAppHostFileName + ".payload"));
        }

        private static List<string> GetRootHostArtifactFiles(string directory)
        {
            var hostArtifactFiles = new List<string>();
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return hostArtifactFiles;

            foreach (var fileName in GetRootHostArtifactFileNames())
            {
                var path = Path.Combine(directory, fileName);
                if (File.Exists(path))
                    hostArtifactFiles.Add(path);
            }

            return hostArtifactFiles;
        }

        private static string[] GetRootHostArtifactFileNames()
        {
            return new[]
            {
                HostExecutableFileName,
                HostManagedDllFileName,
                HostAppHostFileName,
                HostExecutableFileName + ".payload",
                HostManagedDllFileName + ".payload",
                HostAppHostFileName + ".payload",
                Path.ChangeExtension(HostManagedDllFileName, ".deps.json"),
                Path.ChangeExtension(HostManagedDllFileName, ".runtimeconfig.json"),
            };
        }

        private static bool IsRootHostArtifactFile(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return false;

            var fileName = Path.GetFileName(relativePath);
            foreach (var hostArtifactFileName in GetRootHostArtifactFileNames())
            {
                if (string.Equals(fileName, hostArtifactFileName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string GetHostArtifactTargetRelativePath(string archiveEntryName)
        {
            if (string.IsNullOrWhiteSpace(archiveEntryName))
                return null;

            var normalizedEntryName = archiveEntryName.Replace('\\', '/');
            if (normalizedEntryName.EndsWith("/", StringComparison.Ordinal))
            {
                return null;
            }

            var fileName = Path.GetFileName(normalizedEntryName);
            if (string.IsNullOrWhiteSpace(fileName))
                return null;

            var targetRelativePath = fileName.EndsWith(".payload", StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - ".payload".Length)
                : fileName;

            return IsRootHostArtifactFile(fileName)
                   || IsRootHostArtifactFile(targetRelativePath)
                ? targetRelativePath
                : null;
        }

        private static string StageBundledHostArtifacts(
            string payloadDirectory,
            IEnumerable<string> sourcePaths)
        {
            // Torch loads plugin assemblies from the plugin install folder. Stage the
            // external .NET host into the writable instance data area before launching it.
            var stagedDirectory = Path.Combine(
                Config.MainConfig.GetPluginDirectory(),
                "runtime",
                "DiscordHost");
            Directory.CreateDirectory(stagedDirectory);

            foreach (var sourcePath in sourcePaths)
            {
                var relativePath = sourcePath.Substring(payloadDirectory.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var targetRelativePath = relativePath.EndsWith(".payload", StringComparison.OrdinalIgnoreCase)
                    ? relativePath.Substring(0, relativePath.Length - ".payload".Length)
                    : relativePath;
                var destinationPath = Path.Combine(stagedDirectory, targetRelativePath);
                var destinationDirectory = Path.GetDirectoryName(destinationPath);

                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                    Directory.CreateDirectory(destinationDirectory);

                if (ShouldCopyFile(sourcePath, destinationPath, targetRelativePath))
                {
                    File.Copy(sourcePath, destinationPath, true);
                    File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
                }
            }

            return stagedDirectory;
        }

        private static string StageBundledHostArtifactsFromArchive(
            string archivePath,
            out bool foundHostEntrypoint)
        {
            foundHostEntrypoint = false;
            int copiedFileCount = 0;
            var stagedDirectory = GetStagedHostDirectory();
            Directory.CreateDirectory(stagedDirectory);

            using (var stream = File.OpenRead(archivePath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    var targetRelativePath = GetHostArtifactTargetRelativePath(entry.FullName);
                    if (string.IsNullOrWhiteSpace(targetRelativePath))
                        continue;

                    if (IsHostEntrypointFile(targetRelativePath))
                        foundHostEntrypoint = true;

                    var destinationPath = Path.Combine(stagedDirectory, targetRelativePath);
                    var destinationDirectory = Path.GetDirectoryName(destinationPath);

                    if (!string.IsNullOrWhiteSpace(destinationDirectory))
                        Directory.CreateDirectory(destinationDirectory);

                    if (!ShouldCopyArchiveEntry(entry, destinationPath, targetRelativePath))
                        continue;

                    using (var source = entry.Open())
                    using (var destination = File.Create(destinationPath))
                    {
                        source.CopyTo(destination);
                    }

                    File.SetLastWriteTimeUtc(destinationPath, entry.LastWriteTime.UtcDateTime);
                    copiedFileCount++;
                }
            }

            if (foundHostEntrypoint)
            {
                LoggerUtil.LogInfo(
                    "[DISCORD_IPC] Staged " + copiedFileCount
                    + " Discord host artifact(s) from archive " + archivePath
                    + " to " + stagedDirectory);
            }
            else
            {
                LoggerUtil.LogWarning(
                    "[DISCORD_IPC] Archive did not contain a Discord host entrypoint: "
                    + archivePath);
            }

            return stagedDirectory;
        }

        private static string GetStagedHostDirectory()
        {
            return Path.Combine(
                Config.MainConfig.GetPluginDirectory(),
                "runtime",
                "DiscordHost");
        }

        private static bool TrySelectHostEntrypoint(
            string candidateDirectory,
            out string workingDirectory,
            out string executablePath,
            out string dllPath)
        {
            workingDirectory = null;
            executablePath = null;
            dllPath = null;

            if (string.IsNullOrWhiteSpace(candidateDirectory) || !Directory.Exists(candidateDirectory))
                return false;

            var windowsExe = Path.Combine(candidateDirectory, HostExecutableFileName);
            var appHost = Path.Combine(candidateDirectory, HostAppHostFileName);
            var managedDll = Path.Combine(candidateDirectory, HostManagedDllFileName);

            if (File.Exists(windowsExe))
            {
                workingDirectory = candidateDirectory;
                executablePath = windowsExe;
                return true;
            }

            if (File.Exists(appHost))
            {
                workingDirectory = candidateDirectory;
                executablePath = appHost;
                return true;
            }

            if (File.Exists(managedDll))
            {
                workingDirectory = candidateDirectory;
                dllPath = managedDll;
                return true;
            }

            return false;
        }

        private static bool ShouldCopyFile(
            string sourcePath,
            string destinationPath,
            string targetRelativePath)
        {
            if (IsHostEntrypointFile(targetRelativePath))
                return true;

            if (!File.Exists(destinationPath))
                return true;

            var sourceInfo = new FileInfo(sourcePath);
            var destinationInfo = new FileInfo(destinationPath);

            return sourceInfo.Length != destinationInfo.Length
                   || sourceInfo.LastWriteTimeUtc != destinationInfo.LastWriteTimeUtc;
        }

        private static bool ShouldCopyArchiveEntry(
            ZipArchiveEntry entry,
            string destinationPath,
            string targetRelativePath)
        {
            if (IsHostEntrypointFile(targetRelativePath))
                return true;

            if (!File.Exists(destinationPath))
                return true;

            var destinationInfo = new FileInfo(destinationPath);
            return entry.Length != destinationInfo.Length
                   || entry.LastWriteTime.UtcDateTime != destinationInfo.LastWriteTimeUtc;
        }

        private static bool IsHostEntrypointFile(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return false;

            var fileName = Path.GetFileName(relativePath);
            return string.Equals(fileName, HostExecutableFileName, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(fileName, HostAppHostFileName, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(fileName, HostManagedDllFileName, StringComparison.OrdinalIgnoreCase);
        }

        private void CleanupProcess()
        {
            if (_hostProcess == null)
                return;

            try
            {
                _hostProcess.OutputDataReceived -= OnHostOutputDataReceived;
                _hostProcess.ErrorDataReceived -= OnHostErrorDataReceived;
                _hostProcess.Exited -= OnHostProcessExited;
                _hostProcess.Dispose();
            }
            catch
            {
                // ignored
            }
            finally
            {
                _hostProcess = null;
            }
        }

        private void CompletePendingRequests(string error)
        {
            foreach (var pending in _pendingRequests)
            {
                if (_pendingRequests.TryRemove(pending.Key, out var completion))
                {
                    completion.TrySetResult(new DiscordIpcEnvelope
                    {
                        Kind = DiscordIpcKinds.Response,
                        Operation = "PendingRequestAborted",
                        Success = false,
                        Error = error,
                    });
                }
            }
        }

        private static DiscordIpcEnvelope CreateLocalFailure(string operation, string error)
        {
            return new DiscordIpcEnvelope
            {
                Kind = DiscordIpcKinds.Response,
                Operation = operation,
                Success = false,
                Error = error,
            };
        }

        private static ulong GetIdResult(DiscordIpcEnvelope response)
        {
            var result = response.Payload as DiscordIdResult;
            return result != null ? result.Value : 0;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}
