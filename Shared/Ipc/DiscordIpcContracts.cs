using System.Collections.Generic;
using System.Runtime.Serialization;

namespace TorchDiscordSync.Shared.Ipc
{
    public static class DiscordIpcKinds
    {
        public const string Request = "request";
        public const string Response = "response";
        public const string Event = "event";
    }

    public static class DiscordIpcOperations
    {
        public const string Initialize = "Initialize";
        public const string UpdateConfiguration = "UpdateConfiguration";
        public const string UpdatePresence = "UpdatePresence";
        public const string Shutdown = "Shutdown";
        public const string GetConnectionState = "GetConnectionState";
        public const string SendChannelMessage = "SendChannelMessage";
        public const string SendEmbedMessage = "SendEmbedMessage";
        public const string SendVerificationDm = "SendVerificationDm";
        public const string SendVerificationResultDm = "SendVerificationResultDm";
        public const string CreateRole = "CreateRole";
        public const string DeleteRole = "DeleteRole";
        public const string GetRoleInfo = "GetRoleInfo";
        public const string FindRoleByName = "FindRoleByName";
        public const string CreateChannel = "CreateChannel";
        public const string DeleteChannel = "DeleteChannel";
        public const string GetChannelInfo = "GetChannelInfo";
        public const string FindChannelByName = "FindChannelByName";
        public const string AssignRole = "AssignRole";
        public const string SyncRoleMembers = "SyncRoleMembers";
        public const string GetOrCreateVerifiedRole = "GetOrCreateVerifiedRole";
        public const string UpdateChannelName = "UpdateChannelName";
    }

    public static class DiscordIpcEvents
    {
        public const string MessageReceived = "MessageReceived";
        public const string VerificationAttempt = "VerificationAttempt";
        public const string ConnectionStateChanged = "ConnectionStateChanged";
    }

    [DataContract]
    public enum DiscordChannelKind
    {
        [EnumMember]
        Unknown = 0,

        [EnumMember]
        Text = 1,

        [EnumMember]
        Voice = 2,

        [EnumMember]
        Category = 3,
    }

    [DataContract]
    public sealed class DiscordIpcEnvelope
    {
        [DataMember(Order = 1)]
        public string Kind { get; set; }

        [DataMember(Order = 2, EmitDefaultValue = false)]
        public string RequestId { get; set; }

        [DataMember(Order = 3)]
        public string Operation { get; set; }

        [DataMember(Order = 4)]
        public bool Success { get; set; }

        [DataMember(Order = 5, EmitDefaultValue = false)]
        public string Error { get; set; }

        [DataMember(Order = 6, EmitDefaultValue = false)]
        public object Payload { get; set; }
    }

    [DataContract]
    public sealed class DiscordRuntimeConfig
    {
        [DataMember(Order = 1)]
        public string BotToken { get; set; }

        [DataMember(Order = 2)]
        public ulong GuildId { get; set; }

        [DataMember(Order = 3)]
        public string BotPrefix { get; set; }

        [DataMember(Order = 4)]
        public bool EnableDmNotifications { get; set; }

        [DataMember(Order = 5)]
        public int VerificationCodeExpirationMinutes { get; set; }

        [DataMember(Order = 6)]
        public ulong AdminBotChannelId { get; set; }
    }

    [DataContract]
    public sealed class DiscordConnectionState
    {
        [DataMember(Order = 1)]
        public bool IsConnected { get; set; }

        [DataMember(Order = 2)]
        public bool IsReady { get; set; }
    }

    [DataContract]
    public sealed class DiscordIdResult
    {
        [DataMember(Order = 1)]
        public ulong Value { get; set; }
    }

    [DataContract]
    public sealed class DiscordMessageRequest
    {
        [DataMember(Order = 1)]
        public ulong ChannelId { get; set; }

        [DataMember(Order = 2)]
        public string Content { get; set; }
    }

    [DataContract]
    public sealed class DiscordVerificationRequest
    {
        [DataMember(Order = 1)]
        public string DiscordUsername { get; set; }

        [DataMember(Order = 2)]
        public string VerificationCode { get; set; }
    }

    [DataContract]
    public sealed class DiscordVerificationResultMessage
    {
        [DataMember(Order = 1)]
        public string DiscordUsername { get; set; }

        [DataMember(Order = 2)]
        public ulong DiscordUserId { get; set; }

        [DataMember(Order = 3)]
        public string Message { get; set; }

        [DataMember(Order = 4)]
        public bool IsSuccess { get; set; }
    }

    [DataContract]
    public sealed class DiscordCreateRoleRequest
    {
        [DataMember(Order = 1)]
        public string RoleName { get; set; }
    }

    [DataContract]
    public sealed class DiscordDeleteRoleRequest
    {
        [DataMember(Order = 1)]
        public ulong RoleId { get; set; }
    }

    [DataContract]
    public sealed class DiscordRoleQueryRequest
    {
        [DataMember(Order = 1)]
        public ulong RoleId { get; set; }

        [DataMember(Order = 2)]
        public string RoleName { get; set; }
    }

    [DataContract]
    public sealed class DiscordRoleInfo
    {
        [DataMember(Order = 1)]
        public ulong RoleId { get; set; }

        [DataMember(Order = 2)]
        public string RoleName { get; set; }
    }

    [DataContract]
    public sealed class DiscordCreateChannelRequest
    {
        [DataMember(Order = 1)]
        public string ChannelName { get; set; }

        [DataMember(Order = 2)]
        public DiscordChannelKind ChannelKind { get; set; }

        [DataMember(Order = 3, EmitDefaultValue = false)]
        public ulong? CategoryId { get; set; }

        [DataMember(Order = 4, EmitDefaultValue = false)]
        public ulong? RoleId { get; set; }
    }

    [DataContract]
    public sealed class DiscordDeleteChannelRequest
    {
        [DataMember(Order = 1)]
        public ulong ChannelId { get; set; }
    }

    [DataContract]
    public sealed class DiscordChannelQueryRequest
    {
        [DataMember(Order = 1)]
        public ulong ChannelId { get; set; }

        [DataMember(Order = 2)]
        public string ChannelName { get; set; }

        [DataMember(Order = 3)]
        public DiscordChannelKind ExpectedKind { get; set; }
    }

    [DataContract]
    public sealed class DiscordChannelInfo
    {
        [DataMember(Order = 1)]
        public ulong ChannelId { get; set; }

        [DataMember(Order = 2)]
        public string ChannelName { get; set; }

        [DataMember(Order = 3)]
        public DiscordChannelKind ChannelKind { get; set; }
    }

    [DataContract]
    public sealed class DiscordAssignRoleRequest
    {
        [DataMember(Order = 1)]
        public ulong UserId { get; set; }

        [DataMember(Order = 2)]
        public ulong RoleId { get; set; }
    }

    [DataContract]
    public sealed class DiscordSyncRoleMembersRequest
    {
        [DataMember(Order = 1)]
        public ulong RoleId { get; set; }

        [DataMember(Order = 2)]
        public string RoleName { get; set; }

        [DataMember(Order = 3)]
        public List<ulong> DesiredUserIds { get; set; } = new List<ulong>();
    }

    [DataContract]
    public sealed class DiscordUpdateChannelNameRequest
    {
        [DataMember(Order = 1)]
        public ulong ChannelId { get; set; }

        [DataMember(Order = 2)]
        public string NewName { get; set; }
    }

    [DataContract]
    public sealed class DiscordUpdatePresenceRequest
    {
        [DataMember(Order = 1)]
        public string StatusText { get; set; }
    }

    [DataContract]
    public sealed class DiscordEmbedFieldModel
    {
        [DataMember(Order = 1)]
        public string Name { get; set; }

        [DataMember(Order = 2)]
        public string Value { get; set; }

        [DataMember(Order = 3)]
        public bool Inline { get; set; }
    }

    [DataContract]
    public sealed class DiscordEmbedModel
    {
        [DataMember(Order = 1)]
        public string Title { get; set; }

        [DataMember(Order = 2)]
        public string Description { get; set; }

        [DataMember(Order = 3)]
        public int ColorRgb { get; set; }

        [DataMember(Order = 4)]
        public string Footer { get; set; }

        [DataMember(Order = 5)]
        public bool IncludeTimestamp { get; set; }

        [DataMember(Order = 6)]
        public List<DiscordEmbedFieldModel> Fields { get; set; } = new List<DiscordEmbedFieldModel>();
    }

    [DataContract]
    public sealed class DiscordSendEmbedRequest
    {
        [DataMember(Order = 1)]
        public ulong ChannelId { get; set; }

        [DataMember(Order = 2)]
        public DiscordEmbedModel Embed { get; set; }
    }

    [DataContract]
    public sealed class DiscordIncomingMessage
    {
        [DataMember(Order = 1)]
        public ulong ChannelId { get; set; }

        [DataMember(Order = 2)]
        public string ChannelName { get; set; }

        [DataMember(Order = 3)]
        public string Content { get; set; }

        [DataMember(Order = 4)]
        public bool IsDirectMessage { get; set; }

        [DataMember(Order = 5)]
        public ulong AuthorId { get; set; }

        [DataMember(Order = 6)]
        public string AuthorUsername { get; set; }

        [DataMember(Order = 7)]
        public string AuthorDiscriminator { get; set; }

        [DataMember(Order = 8)]
        public bool AuthorIsBot { get; set; }
    }

    [DataContract]
    public sealed class DiscordVerificationAttempt
    {
        [DataMember(Order = 1)]
        public string VerificationCode { get; set; }

        [DataMember(Order = 2)]
        public ulong DiscordUserId { get; set; }

        [DataMember(Order = 3)]
        public string DiscordUsername { get; set; }
    }
}
