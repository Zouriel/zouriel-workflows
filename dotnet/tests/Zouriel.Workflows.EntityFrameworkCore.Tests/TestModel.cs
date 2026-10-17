using Microsoft.EntityFrameworkCore;
using Zouriel.Workflows.EntityFrameworkCore;

namespace Zouriel.Workflows.EntityFrameworkCore.Tests;

public enum ApplicationStatus { Draft, Submitted, Approved, Rejected }

public sealed class Programme
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? CoordinatorId { get; set; }
}

public sealed class Application
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public ApplicationStatus? Status { get; set; }
    public decimal Amount { get; set; }
    public string? Stage { get; set; }
    public string? Notes { get; set; }
    public string? ApplicantId { get; set; }
    public DateOnly? Deadline { get; set; }
    public int? ProgrammeId { get; set; }
    public Programme? Programme { get; set; }
    public List<Document> Documents { get; set; } = [];
}

public sealed class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int ApplicationId { get; set; }
    public string Name { get; set; } = "";
    public bool Verified { get; set; }
}

public sealed class AppDb(DbContextOptions<AppDb> options, bool sqlite = false) : DbContext(options)
{
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<Programme> Programmes => Set<Programme>();
    public DbSet<Document> Documents => Set<Document>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Application>().Property(a => a.Status).HasConversion<string>();
        b.AddWorkflows(o => o.InstantsAsTicks = sqlite);
    }
}
