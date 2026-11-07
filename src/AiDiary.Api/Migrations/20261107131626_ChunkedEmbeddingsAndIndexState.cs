using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiDiary.Api.Migrations
{
    /// <inheritdoc />
    public partial class ChunkedEmbeddingsAndIndexState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every note is re-indexed into parts below, so the old single
            // vectors go now rather than carrying unknown part offsets.
            migrationBuilder.Sql("DELETE FROM note_embeddings;");

            migrationBuilder.DropPrimaryKey(
                name: "PK_note_embeddings",
                table: "note_embeddings");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ContentChangedAt",
                table: "notes",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<bool>(
                name: "HasText",
                table: "notes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "IndexedContentAt",
                table: "notes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ChunkIndex",
                table: "note_embeddings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CharEnd",
                table: "note_embeddings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CharStart",
                table: "note_embeddings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ChunkCount",
                table: "note_embeddings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "EntryDate",
                table: "note_embeddings",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<byte[]>(
                name: "TitleEnc",
                table: "note_embeddings",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddPrimaryKey(
                name: "PK_note_embeddings",
                table: "note_embeddings",
                columns: new[] { "NoteId", "ChunkIndex" });

            // Existing notes: assume text (the worker corrects it), mark every
            // note as not indexed, and queue indexing for the live ones.
            migrationBuilder.Sql("""
                UPDATE notes SET "HasText" = TRUE, "ContentChangedAt" = "UpdatedAt", "IndexedContentAt" = NULL;
                DELETE FROM background_jobs WHERE "Kind" = 'embed_note' AND "Status" = 'pending';
                INSERT INTO background_jobs ("Id", "Kind", "Payload", "Status", "Attempts", "CreatedAt", "RunAt")
                SELECT gen_random_uuid(), 'embed_note', '{"note_id":"' || "Id"::text || '"}', 'pending', 0, now(), now()
                FROM notes WHERE "DeletedAt" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Parts can't fit the old one-row-per-note key; the old reaper re-indexes.
            migrationBuilder.Sql("DELETE FROM note_embeddings;");

            migrationBuilder.DropPrimaryKey(
                name: "PK_note_embeddings",
                table: "note_embeddings");

            migrationBuilder.DropColumn(
                name: "ContentChangedAt",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "HasText",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "IndexedContentAt",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "ChunkIndex",
                table: "note_embeddings");

            migrationBuilder.DropColumn(
                name: "CharEnd",
                table: "note_embeddings");

            migrationBuilder.DropColumn(
                name: "CharStart",
                table: "note_embeddings");

            migrationBuilder.DropColumn(
                name: "ChunkCount",
                table: "note_embeddings");

            migrationBuilder.DropColumn(
                name: "EntryDate",
                table: "note_embeddings");

            migrationBuilder.DropColumn(
                name: "TitleEnc",
                table: "note_embeddings");

            migrationBuilder.AddPrimaryKey(
                name: "PK_note_embeddings",
                table: "note_embeddings",
                column: "NoteId");
        }
    }
}
