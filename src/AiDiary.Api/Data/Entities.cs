using System.Net;
using Pgvector;

namespace AiDiary.Api.Data;

/// <summary>Mirrors Keycloak; owns key material and app state. Credentials live in Keycloak.</summary>
public sealed class AppUser
{
    public Guid Id { get; set; }
    public string KeycloakSub { get; set; } = "";
    public string Email { get; set; } = "";
    public byte[]? WrappedUkek { get; set; }
    public byte[]? RecoveryWrappedUkek { get; set; }
    public DateTimeOffset? RecoveryAcknowledgedAt { get; set; }
    public string? LlmEndpointUrl { get; set; }
    public byte[]? LlmApiKeyEnc { get; set; }
    public string? LlmModel { get; set; }
    public bool IsAdmin { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<Note> Notes { get; set; } = [];
}

public sealed class Note
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public byte[] WrappedDek { get; set; } = [];
    public byte[] TitleEnc { get; set; } = [];
    public DateOnly EntryDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string Timezone { get; set; } = "UTC";
    public double? LocationLat { get; set; }
    public double? LocationLon { get; set; }
    public byte[]? LocationLabelEnc { get; set; }
    public int CurrentVersion { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string Origin { get; set; } = "user";

    public List<NoteVersion> Versions { get; set; } = [];
    public NoteEmbedding? Embedding { get; set; }
}

public sealed class NoteVersion
{
    public Guid Id { get; set; }
    public Guid NoteId { get; set; }
    public Note Note { get; set; } = null!;
    public int VersionNo { get; set; }
    public string S3Key { get; set; } = "";
    public string Filename { get; set; } = "";
    public int ByteSize { get; set; }
    public byte[] ContentHash { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Null means this is the current version. Starts the retention clock.</summary>
    public DateTimeOffset? SupersededAt { get; set; }
}

public sealed class NoteEmbedding
{
    public Guid NoteId { get; set; }
    public Note Note { get; set; } = null!;
    public Guid UserId { get; set; }
    public Vector? Embedding { get; set; }
    public string Model { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
}

public sealed class Conversation
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public byte[] TitleEnc { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastMessageAt { get; set; }
    public List<Message> Messages { get; set; } = [];
}

public sealed class Message
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    public string Role { get; set; } = "";
    public byte[] BodyEnc { get; set; } = [];
    public Guid[] CitedNoteIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AccessRequest
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string? Message { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public IPAddress? SourceIp { get; set; }
}

/// <summary>Postgres-backed job queue; polled by the embedding worker.</summary>
public sealed class BackgroundJob
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset RunAt { get; set; }
    public string? LastError { get; set; }
}
