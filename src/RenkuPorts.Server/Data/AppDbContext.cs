using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace RenkuPorts.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<BuildClient> BuildClients => Set<BuildClient>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Package> Packages => Set<Package>();
    public DbSet<JobBuild> JobBuilds => Set<JobBuild>();
    public DbSet<StagedPackage> StagedPackages => Set<StagedPackage>();
    public DbSet<DateEmbeddingPort> DateEmbeddingPorts => Set<DateEmbeddingPort>();
    public DbSet<Import> Imports => Set<Import>();
    public DbSet<AutoUpdateSettings> AutoUpdateSettings => Set<AutoUpdateSettings>();
    public DbSet<EgressSettings> EgressSettings => Set<EgressSettings>();
    public DbSet<EgressUsage> EgressUsage => Set<EgressUsage>();
    public DbSet<PortPriority> PortPriorities => Set<PortPriority>();
    public DbSet<PortDependency> PortDependencies => Set<PortDependency>();

    /// <summary>All times are UTC; SQL Server drops the Kind, so mark values read back as UTC (JSON then ends in "Z").</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        c.Properties<DateTime>().HaveConversion<UtcConverter>();
        c.Properties<DateTime?>().HaveConversion<UtcConverter>();
    }

    private sealed class UtcConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
        v => v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<BuildClient>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Platform).HasMaxLength(16);
            e.Property(x => x.SecretHash).HasMaxLength(64);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Recipe>(e =>
        {
            e.Property(x => x.PortName).HasMaxLength(200);
            e.Property(x => x.Category).HasMaxLength(100);
            e.Property(x => x.Version).HasMaxLength(100);
            e.Property(x => x.RecipePath).HasMaxLength(400);
            e.HasIndex(x => x.RecipePath).IsUnique();
            e.HasIndex(x => new { x.PortName, x.Version });
        });

        b.Entity<Job>(e =>
        {
            // Status is stored as text; the claim SQL in BuildClientApi depends on these names.
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Platform).HasMaxLength(16);
            e.Property(x => x.FailureSummary).HasMaxLength(2000);
            e.HasIndex(x => new { x.Status, x.Platform, x.QueuedAt });
            e.HasOne(x => x.Recipe).WithMany().HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.QueuedBy).WithMany().HasForeignKey(x => x.QueuedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ClaimedBy).WithMany().HasForeignKey(x => x.ClaimedByClientId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Packages).WithOne(p => p.Job).HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.NoAction);
            e.Property(x => x.Approval).HasMaxLength(16);
            e.Property(x => x.ApprovedBy).HasMaxLength(256);
            e.Property(x => x.ApprovalReason).HasMaxLength(1000);
        });

        b.Entity<JobBuild>(e =>
        {
            // Status is stored as text; the claim and lease SQL in BuildClientApi depend on these names.
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ClientName).HasMaxLength(100);
            e.Property(x => x.Digest).HasMaxLength(64);
            e.HasIndex(x => new { x.JobId, x.Number }).IsUnique();
            // NoAction both ways: SQL Server rejects a second cascade path from BuildClients (via Jobs); jobs are not deleted.
            e.HasOne(x => x.Job).WithMany(j => j.Builds).HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<BuildClient>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<StagedPackage>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.BlobName).HasMaxLength(400);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.Digest).HasMaxLength(64);
            e.HasIndex(x => new { x.BuildId, x.FileName }).IsUnique();
            e.HasIndex(x => x.BlobName);
            e.HasOne(x => x.Build).WithMany(b => b.Packages).HasForeignKey(x => x.BuildId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DateEmbeddingPort>(e =>
        {
            e.HasKey(x => x.PortName);
            e.Property(x => x.PortName).HasMaxLength(200);
            e.Property(x => x.SetBy).HasMaxLength(256);
            e.Property(x => x.Reason).HasMaxLength(1000);
        });

        b.Entity<Package>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.Platform).HasMaxLength(16);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.BlobName).HasMaxLength(400);
            e.Property(x => x.PortName).HasMaxLength(200);
            e.HasIndex(x => new { x.Platform, x.FileName }).IsUnique();
            e.HasOne(x => x.BuiltBy).WithMany().HasForeignKey(x => x.BuiltByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BuildClient>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Import).WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<AutoUpdateSettings>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Platforms).HasMaxLength(100);
            e.Property(x => x.LastCommit).HasMaxLength(64);
            e.HasData(new AutoUpdateSettings { Id = Data.AutoUpdateSettings.SingletonId, Enabled = false, IntervalHours = 6, Platforms = "arm64" });
        });

        b.Entity<EgressSettings>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasData(new EgressSettings { Id = Data.EgressSettings.SingletonId, MonthlyCapGb = 800 });
        });

        b.Entity<EgressUsage>(e =>
        {
            e.HasKey(x => x.Month);
            e.Property(x => x.Month).HasMaxLength(7);
        });

        b.Entity<PortPriority>(e =>
        {
            // The job claim SQL in BuildClientApi reads this table by these names.
            e.HasKey(x => new { x.Platform, x.PortName });
            e.Property(x => x.Platform).HasMaxLength(16);
            e.Property(x => x.PortName).HasMaxLength(200);
            e.HasIndex(x => new { x.Platform, x.Priority });
            e.HasOne(x => x.Recipe).WithMany().HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PortDependency>(e =>
        {
            e.HasKey(x => new { x.Platform, x.PortName, x.DependsOn });
            e.Property(x => x.Platform).HasMaxLength(16);
            e.Property(x => x.PortName).HasMaxLength(200);
            e.Property(x => x.DependsOn).HasMaxLength(200);
        });

        b.Entity<Import>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.SourceUrl).HasMaxLength(2000);
            e.Property(x => x.SourceHost).HasMaxLength(255);
            e.Property(x => x.Platform).HasMaxLength(16);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.OwnsMany(x => x.Skipped, s => s.ToJson());
            // At most one Running import per platform.
            e.HasIndex(x => x.Platform).IsUnique().HasFilter("[Status] = 'Running'");
            e.HasOne(x => x.StartedBy).WithMany().HasForeignKey(x => x.StartedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
