using Microsoft.EntityFrameworkCore;
using Zouriel.Workflows.EntityFrameworkCore;

namespace Zouriel.Workflows.Sample;

// A small scholarship office: applications for programmes, with supporting documents.

public enum ApplicationStatus { Draft, Submitted, UnderReview, Approved, Rejected, Withdrawn }

public enum Priority { Low, Normal, High }

public sealed class Programme
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? CoordinatorId { get; set; }
    public decimal Budget { get; set; }
}

public sealed class Application
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public ApplicationStatus? Status { get; set; }
    public Priority Priority { get; set; } = Priority.Normal;
    public decimal Amount { get; set; }
    public string? ApplicantId { get; set; }
    public string? ApplicantEmail { get; set; }
    public string? ReviewerId { get; set; }
    public DateOnly? Deadline { get; set; }
    public bool Paid { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? ProgrammeId { get; set; }
    public Programme? Programme { get; set; }
    public List<Document> Documents { get; set; } = [];
}

public sealed class Document
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public string Name { get; set; } = "";
    public bool Verified { get; set; }
}

public sealed class SampleDb(DbContextOptions<SampleDb> options) : DbContext(options)
{
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<Programme> Programmes => Set<Programme>();
    public DbSet<Document> Documents => Set<Document>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Application>(e =>
        {
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.Priority).HasConversion<string>().HasMaxLength(10);
            e.Property(a => a.Amount).HasConversion<double>();
        });
        b.Entity<Programme>().Property(p => p.Budget).HasConversion<double>();
        // The workflow tables live in this same database, so records and workflows commit together.
        // SQLite cannot compare DateTimeOffset values, so instants are stored as ticks there.
        b.AddWorkflows(o => o.InstantsAsTicks = Database.IsSqlite());
    }
}
