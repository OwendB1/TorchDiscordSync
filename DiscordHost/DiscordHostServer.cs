using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using TorchDiscordSync.DiscordHost.Logging;
using TorchDiscordSync.DiscordHost.Services;
using TorchDiscordSync.Shared.Ipc;

namespace TorchDiscordSync.DiscordHost
{
    internal sealed class DiscordHostServer
    {
        private const int MaxConcurrentRequests = 8;

        private readonly string _pipeName;
        private readonly int _parentProcessId;
        private readonly DiscordGatewayService _gatewayService;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _requestConcurrency =
            new SemaphoreSlim(MaxConcurrentRequests, MaxConcurrentRequests);
        private NamedPipeServerStream _pipeStream;

        public DiscordHostServer(string pipeName, int parentProcessId)
        {
            _pipeName = pipeName;
            _parentProcessId = parentProcessId;
            _gatewayService = new DiscordGatewayService();
            _gatewayService.ConnectionStateChanged += state => SendEventAsync(
                DiscordIpcEvents.ConnectionStateChanged,
                state,
                CancellationToken.None);
            _gatewayService.MessageReceived += message => SendEventAsync(
                DiscordIpcEvents.MessageReceived,
                message,
                CancellationToken.None);
        }

        public async Task<int> RunAsync(CancellationToken cancellationToken)
        {
            using (var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous))
            {
                _pipeStream = pipe;
                _ = MonitorParentProcessAsync(linkedSource);

                HostLogger.Info("Waiting for Torch plugin pipe connection: " + _pipeName);
                await pipe.WaitForConnectionAsync(linkedSource.Token).ConfigureAwait(false);
                HostLogger.Info("Torch plugin connected to Discord host pipe.");

                while (!linkedSource.IsCancellationRequested && pipe.IsConnected)
                {
                    DiscordIpcEnvelope request;
                    try
                    {
                        request = await DiscordIpcSerializer.ReadAsync(pipe, linkedSource.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        HostLogger.Error("Pipe read failed: " + ex.Message);
                        break;
                    }

                    if (request == null)
                        break;

                    if (!string.Equals(request.Kind, DiscordIpcKinds.Request, StringComparison.Ordinal))
                        continue;

                    if (IsBlockingOperation(request.Operation))
                    {
                        if (!await ProcessRequestAsync(request, linkedSource.Token, true).ConfigureAwait(false))
                            break;

                        if (string.Equals(request.Operation, DiscordIpcOperations.Shutdown, StringComparison.Ordinal))
                        {
                            linkedSource.Cancel();
                            break;
                        }

                        continue;
                    }

                    _ = ProcessRequestAsync(request, linkedSource.Token);
                }

                await _gatewayService.StopAsync().ConfigureAwait(false);
            }

            return 0;
        }

        private async Task<bool> ProcessRequestAsync(
            DiscordIpcEnvelope request,
            CancellationToken cancellationToken,
            bool exclusive = false)
        {
            var stopwatch = Stopwatch.StartNew();
            var acquiredSlots = 0;

            try
            {
                acquiredSlots = await AcquireRequestSlotsAsync(exclusive, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    var response = await HandleRequestAsync(request, cancellationToken).ConfigureAwait(false);
                    if (response != null)
                    {
                        await SendAsync(response, cancellationToken).ConfigureAwait(false);
                    }

                    return true;
                }
                finally
                {
                    _requestConcurrency.Release(acquiredSlots);

                    if (stopwatch.ElapsedMilliseconds >= 2000)
                    {
                        HostLogger.Warn(
                            "IPC request '" + request.Operation + "' took "
                            + stopwatch.ElapsedMilliseconds + "ms.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                HostLogger.Error(
                    "IPC request '" + request.Operation + "' failed: " + ex.Message);
                return false;
            }
        }

        private async Task<int> AcquireRequestSlotsAsync(bool exclusive, CancellationToken cancellationToken)
        {
            var requestedSlots = exclusive ? MaxConcurrentRequests : 1;
            var acquiredSlots = 0;

            try
            {
                while (acquiredSlots < requestedSlots)
                {
                    await _requestConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                    acquiredSlots++;
                }

                return acquiredSlots;
            }
            catch
            {
                if (acquiredSlots > 0)
                    _requestConcurrency.Release(acquiredSlots);
                throw;
            }
        }

        private static bool IsBlockingOperation(string operation)
        {
            return string.Equals(operation, DiscordIpcOperations.Initialize, StringComparison.Ordinal)
                   || string.Equals(operation, DiscordIpcOperations.UpdateConfiguration, StringComparison.Ordinal)
                   || string.Equals(operation, DiscordIpcOperations.Shutdown, StringComparison.Ordinal);
        }

        private async Task<DiscordIpcEnvelope> HandleRequestAsync(
            DiscordIpcEnvelope request,
            CancellationToken cancellationToken)
        {
            try
            {
                switch (request.Operation)
                {
                    case DiscordIpcOperations.Initialize:
                        return Response(
                            request,
                            await _gatewayService.ApplyConfigurationAsync((DiscordRuntimeConfig)request.Payload)
                                .ConfigureAwait(false),
                            _gatewayService.GetConnectionState());

                    case DiscordIpcOperations.UpdateConfiguration:
                        return Response(
                            request,
                            await _gatewayService.ApplyConfigurationAsync((DiscordRuntimeConfig)request.Payload)
                                .ConfigureAwait(false),
                            _gatewayService.GetConnectionState());

                    case DiscordIpcOperations.UpdatePresence:
                        return Response(
                            request,
                            await _gatewayService.UpdatePresenceAsync((DiscordUpdatePresenceRequest)request.Payload)
                                .ConfigureAwait(false));

                    case DiscordIpcOperations.GetConnectionState:
                        return Response(request, true, _gatewayService.GetConnectionState());

                    case DiscordIpcOperations.SendChannelMessage:
                        return Response(
                            request,
                            await _gatewayService.SendChannelMessageAsync((DiscordMessageRequest)request.Payload)
                                .ConfigureAwait(false));

                    case DiscordIpcOperations.SendEmbedMessage:
                        return Response(
                            request,
                            await _gatewayService.SendEmbedMessageAsync((DiscordSendEmbedRequest)request.Payload)
                                .ConfigureAwait(false));

                    case DiscordIpcOperations.CreateRole:
                        return IdResponse(
                            request,
                            await _gatewayService.CreateRoleAsync((DiscordCreateRoleRequest)request.Payload)
                                .ConfigureAwait(false));

                    case DiscordIpcOperations.DeleteRole:
                        return Response(
                            request,
                            await _gatewayService.DeleteRoleAsync((DiscordDeleteRoleRequest)request.Payload)
                                .ConfigureAwait(false));

                    case DiscordIpcOperations.GetRoleInfo:
                        return Response(request, true, _gatewayService.GetRoleInfo((DiscordRoleQueryRequest)request.Payload));

                    case DiscordIpcOperations.FindRoleByName:
                        return IdResponse(request, _gatewayService.FindRoleByName((DiscordRoleQueryRequest)request.Payload));

                    case DiscordIpcOperations.CreateChannel:
                        return IdResponse(
                            request,
                            await _gatewayService.CreateChannelAsync((DiscordCreateChannelRequest)request.Payload)
                                .ConfigureAwait(false));

                    case DiscordIpcOperations.DeleteChannel:
                        return Response(
                            request,
                            await _gatewayService.DeleteChannelAsync((DiscordDeleteChannelRequest)request.Payload)
                                .ConfigureAwait(false));

                    case DiscordIpcOperations.GetChannelInfo:
                        return Response(
                            request,
                            true,
                            _gatewayService.GetChannelInfo((DiscordChannelQueryRequest)request.Payload));

                    case DiscordIpcOperations.FindChannelByName:
                        return IdResponse(
                            request,
                            _gatewayService.FindChannelByName((DiscordChannelQueryRequest)request.Payload));

                    case DiscordIpcOperations.AssignRole:
                        return Response(
                            request,
                            await _gatewayService.AssignRoleAsync((DiscordAssignRoleRequest)request.Payload)
                                .ConfigureAwait(false));

                    case DiscordIpcOperations.SyncRoleMembers:
                        return Response(
                            request,
                            await _gatewayService.SyncRoleMembersAsync((DiscordSyncRoleMembersRequest)request.Payload)
                                .ConfigureAwait(false));

                    case DiscordIpcOperations.UpdateChannelName:
                        return Response(
                            request,
                            await _gatewayService.UpdateChannelNameAsync((DiscordUpdateChannelNameRequest)request.Payload)
                                .ConfigureAwait(false));

                    case DiscordIpcOperations.Shutdown:
                        await _gatewayService.StopAsync().ConfigureAwait(false);
                        return Response(request, true);

                    default:
                        return new DiscordIpcEnvelope
                        {
                            Kind = DiscordIpcKinds.Response,
                            RequestId = request.RequestId,
                            Operation = request.Operation,
                            Success = false,
                            Error = "Unknown IPC operation: " + request.Operation,
                        };
                }
            }
            catch (Exception ex)
            {
                return new DiscordIpcEnvelope
                {
                    Kind = DiscordIpcKinds.Response,
                    RequestId = request.RequestId,
                    Operation = request.Operation,
                    Success = false,
                    Error = ex.Message,
                };
            }
        }

        private async Task SendEventAsync(string operation, object payload, CancellationToken cancellationToken)
        {
            if (_pipeStream == null || !_pipeStream.IsConnected)
                return;

            try
            {
                await SendAsync(
                    new DiscordIpcEnvelope
                    {
                        Kind = DiscordIpcKinds.Event,
                        Operation = operation,
                        Success = true,
                        Payload = payload,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HostLogger.Error("Failed to send host event '" + operation + "': " + ex.Message);
            }
        }

        private async Task SendAsync(DiscordIpcEnvelope envelope, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await DiscordIpcSerializer.WriteAsync(_pipeStream, envelope, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task MonitorParentProcessAsync(CancellationTokenSource linkedSource)
        {
            if (_parentProcessId <= 0)
                return;

            try
            {
                var parent = Process.GetProcessById(_parentProcessId);
                while (!linkedSource.IsCancellationRequested)
                {
                    if (parent.HasExited)
                    {
                        HostLogger.Warn("Torch process exited; shutting down Discord host.");
                        linkedSource.Cancel();
                        return;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2), linkedSource.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                linkedSource.Cancel();
            }
        }

        private static DiscordIpcEnvelope Response(
            DiscordIpcEnvelope request,
            bool success,
            object payload = null)
        {
            return new DiscordIpcEnvelope
            {
                Kind = DiscordIpcKinds.Response,
                RequestId = request.RequestId,
                Operation = request.Operation,
                Success = success,
                Payload = payload,
            };
        }

        private static DiscordIpcEnvelope IdResponse(DiscordIpcEnvelope request, ulong value)
        {
            return new DiscordIpcEnvelope
            {
                Kind = DiscordIpcKinds.Response,
                RequestId = request.RequestId,
                Operation = request.Operation,
                Success = value != 0,
                Payload = new DiscordIdResult { Value = value },
            };
        }
    }
}
