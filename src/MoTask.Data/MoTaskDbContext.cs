using Microsoft.EntityFrameworkCore;
using MoTask.Core.Model;

namespace MoTask.Data;

public sealed class MoTaskDbContext : DbContext
{
    public MoTaskDbContext(DbContextOptions<MoTaskDbContext> options) : base(options)
    {
    }

    public DbSet<Board> Boards => Set<Board>();
    public DbSet<Column> Columns => Set<Column>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<HistoryEntry> History => Set<HistoryEntry>();
    public DbSet<AiJob> AiJobs => Set<AiJob>();
    public DbSet<PlanningRun> PlanningRuns => Set<PlanningRun>();
    public DbSet<TriageCandidate> TriageCandidates => Set<TriageCandidate>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Board>(e =>
        {
            e.ToTable("Boards");
            e.Property(x => x.Name).IsRequired();
            e.HasMany(x => x.Columns).WithOne().HasForeignKey(x => x.BoardId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Column>(e =>
        {
            e.ToTable("Columns");
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.BoardId, x.Order });
            e.HasMany(x => x.Tasks).WithOne().HasForeignKey(x => x.ColumnId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<TaskItem>(e =>
        {
            e.ToTable("Tasks");
            e.Property(x => x.Title).IsRequired();
            e.Property(x => x.Description).IsRequired().HasDefaultValue("");
            e.HasIndex(x => new { x.ColumnId, x.Position });
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Labels).WithMany().UsingEntity<TaskLabel>(
                right => right.HasOne<Label>().WithMany().HasForeignKey(x => x.LabelId).OnDelete(DeleteBehavior.Cascade),
                left => left.HasOne<TaskItem>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade),
                join =>
                {
                    join.ToTable("TaskLabels");
                    join.HasKey(x => new { x.TaskId, x.LabelId });
                });
        });

        b.Entity<Project>(e =>
        {
            e.ToTable("Projects");
            e.Property(x => x.Name).IsRequired();
        });

        b.Entity<Label>(e =>
        {
            e.ToTable("Labels");
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.Color).IsRequired();
        });

        b.Entity<HistoryEntry>(e =>
        {
            e.ToTable("History");
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Detail).IsRequired().HasDefaultValue("");
            e.HasIndex(x => x.TaskId);
            e.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AiJob>(e =>
        {
            e.ToTable("AiJobs");
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.Instruction).IsRequired().HasDefaultValue("");
            e.Property(x => x.WorkingDirectory).IsRequired().HasDefaultValue("");
            e.Property(x => x.JobFolder).IsRequired().HasDefaultValue("");
            e.Property(x => x.ProcessedLines).HasDefaultValue(0);
            e.HasIndex(x => x.TaskId);
            e.HasIndex(x => x.Status);
            e.HasOne<TaskItem>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PlanningRun>(e =>
        {
            e.ToTable("PlanningRuns");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Instruction).IsRequired().HasDefaultValue("");
            e.Property(x => x.JobFolder).IsRequired().HasDefaultValue("");
            e.Property(x => x.PlanJson).IsRequired().HasDefaultValue("");
            e.Property(x => x.ProcessedLines).HasDefaultValue(0);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.Date);
        });

        b.Entity<TriageCandidate>(e =>
        {
            e.ToTable("TriageCandidates");
            e.Property(x => x.ExternalId).IsRequired();
            e.Property(x => x.Source).IsRequired().HasDefaultValue("");
            // From は SQLite の予約語だが、EF は識別子を必ず引用符で囲むので列名はこのままでよい。
            e.Property(x => x.From).IsRequired().HasDefaultValue("");
            e.Property(x => x.Title).IsRequired().HasDefaultValue("");
            e.Property(x => x.Evidence).IsRequired().HasDefaultValue("");
            e.Property(x => x.Link).IsRequired().HasDefaultValue("");
            e.Property(x => x.Reasoning).IsRequired().HasDefaultValue("");
            e.Property(x => x.SuggestedProject).IsRequired().HasDefaultValue("");
            e.Property(x => x.SuggestedAction).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            // 却下した候補を翌朝また拾わないための鍵（仕様 §9）
            e.HasIndex(x => x.ExternalId).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.PlanningRunId);
            e.HasOne<PlanningRun>().WithMany().HasForeignKey(x => x.PlanningRunId).OnDelete(DeleteBehavior.Cascade);
            // タスクが消えても候補の記録は残す
            e.HasOne<TaskItem>().WithMany().HasForeignKey(x => x.ResultTaskId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}

public static class MoTaskDbContextOptions
{
    public static DbContextOptions<MoTaskDbContext> Create(string connectionString)
        => new DbContextOptionsBuilder<MoTaskDbContext>().UseSqlite(connectionString).Options;
}
