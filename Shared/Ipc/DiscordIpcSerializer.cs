using System;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TorchDiscordSync.Shared.Ipc
{
    public static class DiscordIpcSerializer
    {
        private static readonly Type[] KnownTypes =
        {
            typeof(DiscordAssignRoleRequest),
            typeof(DiscordChannelInfo),
            typeof(DiscordChannelQueryRequest),
            typeof(DiscordConnectionState),
            typeof(DiscordCreateChannelRequest),
            typeof(DiscordCreateRoleRequest),
            typeof(DiscordDeleteChannelRequest),
            typeof(DiscordDeleteRoleRequest),
            typeof(DiscordEmbedFieldModel),
            typeof(DiscordEmbedModel),
            typeof(DiscordIdResult),
            typeof(DiscordIncomingMessage),
            typeof(DiscordMessageRequest),
            typeof(DiscordRoleInfo),
            typeof(DiscordRoleQueryRequest),
            typeof(DiscordRuntimeConfig),
            typeof(DiscordSendEmbedRequest),
            typeof(DiscordSyncRoleMembersRequest),
            typeof(DiscordUpdateChannelNameRequest),
            typeof(DiscordUpdatePresenceRequest),
        };

        private static readonly DataContractSerializer Serializer =
            new DataContractSerializer(typeof(DiscordIpcEnvelope), KnownTypes);

        public static async Task WriteAsync(
            Stream stream,
            DiscordIpcEnvelope envelope,
            CancellationToken cancellationToken)
        {
            using (var buffer = new MemoryStream())
            {
                buffer.Position = sizeof(int);
                Serializer.WriteObject(buffer, envelope);
                var frame = buffer.GetBuffer();
                var payloadLength = checked((int)buffer.Length - sizeof(int));
                // The IPC length prefix is a little-endian Int32.
                frame[0] = (byte)payloadLength;
                frame[1] = (byte)(payloadLength >> 8);
                frame[2] = (byte)(payloadLength >> 16);
                frame[3] = (byte)(payloadLength >> 24);

                await stream.WriteAsync(frame, 0, (int)buffer.Length, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public static async Task<DiscordIpcEnvelope> ReadAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            var lengthPrefix = new byte[sizeof(int)];
            var lengthRead = await ReadExactAsync(
                stream,
                lengthPrefix,
                0,
                lengthPrefix.Length,
                cancellationToken).ConfigureAwait(false);

            if (lengthRead == 0)
                return null;

            if (lengthRead != lengthPrefix.Length)
                throw new EndOfStreamException("Pipe closed while reading message length.");

            var payloadLength = lengthPrefix[0] | (lengthPrefix[1] << 8)
                | (lengthPrefix[2] << 16) | (lengthPrefix[3] << 24);
            if (payloadLength <= 0)
                throw new InvalidDataException("Pipe message length must be positive.");

            var payload = new byte[payloadLength];
            var payloadRead = await ReadExactAsync(
                stream,
                payload,
                0,
                payload.Length,
                cancellationToken).ConfigureAwait(false);

            if (payloadRead != payload.Length)
                throw new EndOfStreamException("Pipe closed while reading message body.");

            using (var buffer = new MemoryStream(payload))
            {
                return (DiscordIpcEnvelope)Serializer.ReadObject(buffer);
            }
        }

        private static async Task<int> ReadExactAsync(
            Stream stream,
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var totalRead = 0;
            while (totalRead < count)
            {
                var read = await stream.ReadAsync(
                        buffer,
                        offset + totalRead,
                        count - totalRead,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                    return totalRead;

                totalRead += read;
            }

            return totalRead;
        }
    }
}
