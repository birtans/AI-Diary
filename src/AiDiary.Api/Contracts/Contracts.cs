namespace AiDiary.Api.Contracts;

// ---------- Notes ----------

public sealed record NoteLocationRequest(double Lat, double Lon, string? Label);

public sealed record CreateNoteRequest(
    string? Title,
    DateOnly? EntryDate,
    string? Timezone,
    NoteLocationRequest? Location,
    string? Origin,
    string Body);

public sealed record SaveNoteRequest(string ExpectedRevision, string Body);

public sealed record PatchNoteMetaRequest(
    string? Title,
    DateOnly? EntryDate,
    string? Timezone,
    NoteLocationRequest? Location,
    bool ClearLocation);

public sealed record NoteListItem(
    Guid Id,
    string Title,
    DateOnly EntryDate,
    int CurrentVersion,
    DateTimeOffset? DeletedAt,
    string Origin);

public sealed record NoteDetail(
    Guid Id,
    string Title,
    DateOnly EntryDate,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Timezone,
    NoteLocationResponse? Location,
    int CurrentVersion,
    string Revision,
    string Origin,
    DateTimeOffset? DeletedAt,
    string PresignedUrl,
    string Dek);

public sealed record NoteLocationResponse(double Lat, double Lon, string? Label);

public sealed record NoteVersionItem(
    int VersionNo,
    string Filename,
    int ByteSize,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SupersededAt);

public sealed record DiffResponse(int VersionA, int VersionB, string Html);

// ---------- Search ----------

public sealed record SearchRequest(string Query, DateOnly? DateFrom, DateOnly? DateTo, int? Limit);

public sealed record SearchResultItem(
    Guid NoteId,
    DateOnly EntryDate,
    string Title,
    string Excerpt,
    double Score);

// ---------- Chat ----------

public sealed record ChatRequest(Guid? ConversationId, string Message);

public sealed record ConversationListItem(Guid Id, string Title, DateTimeOffset LastMessageAt);

public sealed record ChatMessageItem(
    Guid Id,
    string Role,
    string Body,
    string[] CitedNoteIds,
    DateTimeOffset CreatedAt);

// ---------- Settings ----------

public sealed record SettingsResponse(string? EndpointUrl, string? Model, bool HasApiKey);

public sealed record UpdateSettingsRequest(string? EndpointUrl, string? Model, string? ApiKey);

// ---------- Access requests ----------

public sealed record AccessRequestRequest(string Email, string? Message, string? CaptchaToken);

public sealed record AccessRequestItem(
    Guid Id,
    string Email,
    string? Message,
    string Status,
    DateTimeOffset RequestedAt);

// ---------- Recovery ----------

public sealed record RecoveryStatusResponse(string Status);

public sealed record RecoverySetupResponse(
    string Status,
    string? RecoveryKey,
    Guid? UserId);

public sealed record AckRecoveryRequest(string RecoveryKey);

public sealed record RecoveryExportResponse(Guid UserId, string RecoveryWrappedUkek);
