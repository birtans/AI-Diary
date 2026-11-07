using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AiDiary.Api.Data;

/// <summary>Design-time factory so EF tooling never runs the application host.</summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("DIARY_DB_CONNECTION")
            ?? "Host=localhost;Database=diary;Username=diary;Password=diary";
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.UseNpgsql(connection, npgsql => npgsql.UseVector());
        return new AppDbContext(builder.Options);
    }
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<NoteVersion> NoteVersions => Set<NoteVersion>();
    public DbSet<NoteEmbedding> NoteEmbeddings => Set<NoteEmbedding>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();
    public DbSet<BackgroundJob> BackgroundJobs => Set<BackgroundJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppUser>(e =>
        {
            e.ToTable("users");
            e.Property(u => u.Email).HasColumnType("citext");
            e.HasIndex(u => u.KeycloakSub).IsUnique();
            e.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<Note>(e =>
        {
            e.ToTable("notes");
            e.Property(n => n.EntryDate).HasColumnType("date");
            e.Property(n => n.Origin).HasMaxLength(16);
            e.Property(n => n.Timezone).HasMaxLength(64);
            e.HasIndex(n => new { n.UserId, n.EntryDate }).IsDescending(false, true)
                .HasFilter("\"DeletedAt\" IS NULL");
            e.HasIndex(n => new { n.UserId, n.DeletedAt });
            e.HasOne(n => n.User).WithMany(u => u.Notes).HasForeignKey(n => n.UserId);
            e.Ignore(n => n.NeedsIndex);
        });

        modelBuilder.Entity<NoteVersion>(e =>
        {
            e.ToTable("note_versions");
            e.HasIndex(v => new { v.NoteId, v.VersionNo }).IsUnique();
            e.HasIndex(v => v.SupersededAt).HasFilter("\"SupersededAt\" IS NOT NULL");
            e.HasOne(v => v.Note).WithMany(n => n.Versions).HasForeignKey(v => v.NoteId);
        });

        modelBuilder.Entity<NoteEmbedding>(e =>
        {
            e.ToTable("note_embeddings");
            e.HasKey(x => new { x.NoteId, x.ChunkIndex });
            e.Property(x => x.EntryDate).HasColumnType("date");
            e.Property(x => x.Embedding).HasColumnType("vector(1024)");
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.Note).WithMany(n => n.Embeddings).HasForeignKey(x => x.NoteId);
        });

        modelBuilder.Entity<Conversation>(e =>
        {
            e.ToTable("conversations");
            e.HasIndex(c => new { c.UserId, c.LastMessageAt });
        });

        modelBuilder.Entity<Message>(e =>
        {
            e.ToTable("messages");
            e.HasIndex(m => new { m.ConversationId, m.CreatedAt });
            e.HasOne(m => m.Conversation).WithMany(c => c.Messages).HasForeignKey(m => m.ConversationId);
        });

        modelBuilder.Entity<AccessRequest>(e =>
        {
            e.ToTable("access_requests");
            e.Property(a => a.Email).HasColumnType("citext");
            e.Property(a => a.Status).HasMaxLength(16);
            e.HasIndex(a => new { a.Status, a.RequestedAt });
        });

        modelBuilder.Entity<BackgroundJob>(e =>
        {
            e.ToTable("background_jobs");
            e.Property(j => j.Kind).HasMaxLength(64);
            e.Property(j => j.Status).HasMaxLength(16);
            e.HasIndex(j => new { j.Status, j.RunAt });
        });
    }
}
