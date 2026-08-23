using Microsoft.EntityFrameworkCore;
using PatientIntakeApp.Data.Entities;

namespace PatientIntakeApp.Data;

public class PatientIntakeDbContext : DbContext
{
    public PatientIntakeDbContext(DbContextOptions<PatientIntakeDbContext> options) : base(options)
    {
    }

    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<UserPresenceEntity> UserPresence => Set<UserPresenceEntity>();
    public DbSet<FacilityEntity> Facilities => Set<FacilityEntity>();
    public DbSet<RuleEntity> Rules => Set<RuleEntity>();
    public DbSet<PatientEntity> Patients => Set<PatientEntity>();
    public DbSet<ReferralEntity> Referrals => Set<ReferralEntity>();
    public DbSet<ReviewSessionEntity> ReviewSessions => Set<ReviewSessionEntity>();
    public DbSet<FindingEntity> Findings => Set<FindingEntity>();
    public DbSet<ReferralEventEntity> ReferralEvents => Set<ReferralEventEntity>();
    public DbSet<ExternalCheckEntity> ExternalChecks => Set<ExternalCheckEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        var isSqlServer = Database.IsSqlServer();

        modelBuilder.Entity<UserEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Username).IsUnique();
            b.Property(x => x.Username).HasMaxLength(64).IsRequired();
            b.Property(x => x.DisplayName).HasMaxLength(128);
            b.Property(x => x.PasswordHash).HasMaxLength(256).IsRequired();
            b.Property(x => x.PasswordSalt).HasMaxLength(256).IsRequired();
            if (isSqlServer) b.Property(x => x.RowVersion).IsRowVersion();
            else b.Ignore(x => x.RowVersion);
        });

        modelBuilder.Entity<UserPresenceEntity>(b =>
        {
            b.HasKey(x => x.UserId);
            b.HasIndex(x => x.LastSeenAtUtc);
            b.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FacilityEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Name).IsUnique();
            // SQL Server needs a filtered unique index to allow multiple NULLs in a unique column.
            // SQLite allows multiple NULLs in a unique index, but does not always support SQL Server-style filters.
            var legacyIdx = b.HasIndex(x => x.LegacyId).IsUnique();
            if (isSqlServer)
            {
                legacyIdx.HasFilter("[LegacyId] IS NOT NULL");
            }
            b.Property(x => x.LegacyId).HasMaxLength(64);
            b.Property(x => x.Name).HasMaxLength(256).IsRequired();
            if (isSqlServer) b.Property(x => x.RowVersion).IsRowVersion();
            else b.Ignore(x => x.RowVersion);
        });

        modelBuilder.Entity<RuleEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.FacilityId, x.Kind, x.Text });
            b.Property(x => x.Text).HasMaxLength(512).IsRequired();
            if (isSqlServer) b.Property(x => x.RowVersion).IsRowVersion();
            else b.Ignore(x => x.RowVersion);
            b.HasOne(x => x.Facility)
                .WithMany(x => x.Rules)
                .HasForeignKey(x => x.FacilityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PatientEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.NormalizedKey);
            b.Property(x => x.FirstName).HasMaxLength(128);
            b.Property(x => x.LastName).HasMaxLength(128);
            b.Property(x => x.ExternalMrn).HasMaxLength(64);
            b.Property(x => x.NormalizedKey).HasMaxLength(256).IsRequired();
            if (isSqlServer) b.Property(x => x.RowVersion).IsRowVersion();
            else b.Ignore(x => x.RowVersion);
        });

        modelBuilder.Entity<ReferralEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.SourceFileHash);
            b.HasIndex(x => new { x.PatientId, x.IngestedAt });
            b.Property(x => x.SourceFileName).HasMaxLength(512).IsRequired();
            b.Property(x => x.SourceFilePath).HasMaxLength(1024).IsRequired();
            b.Property(x => x.SourceFileHash).HasMaxLength(128).IsRequired();
            if (isSqlServer) b.Property(x => x.RowVersion).IsRowVersion();
            else b.Ignore(x => x.RowVersion);

            b.HasOne(x => x.Patient)
                .WithMany(x => x.Referrals)
                .HasForeignKey(x => x.PatientId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasOne(x => x.Facility)
                .WithMany(x => x.Referrals)
                .HasForeignKey(x => x.FacilityId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasOne(x => x.CurrentAssigneeUser)
                .WithMany()
                .HasForeignKey(x => x.CurrentAssigneeUserId)
                .OnDelete(DeleteBehavior.SetNull);

            b.HasOne(x => x.DuplicateOfReferral)
                .WithMany()
                .HasForeignKey(x => x.DuplicateOfReferralId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ReviewSessionEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.ReferralId, x.State });
            b.Property(x => x.PauseReason).HasMaxLength(512);
            if (isSqlServer) b.Property(x => x.RowVersion).IsRowVersion();
            else b.Ignore(x => x.RowVersion);

            b.HasOne(x => x.Referral)
                .WithMany(x => x.ReviewSessions)
                .HasForeignKey(x => x.ReferralId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(x => x.ReviewerUser)
                .WithMany()
                .HasForeignKey(x => x.ReviewerUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FindingEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.ReviewSessionId);
            b.Property(x => x.Term).HasMaxLength(512).IsRequired();
            b.Property(x => x.Category).HasMaxLength(128).IsRequired();
            b.Property(x => x.Context).HasMaxLength(4000);
            b.Property(x => x.FalseFlagReason).HasMaxLength(512);
            if (isSqlServer) b.Property(x => x.RowVersion).IsRowVersion();
            else b.Ignore(x => x.RowVersion);

            b.HasOne(x => x.ReviewSession)
                .WithMany(x => x.Findings)
                .HasForeignKey(x => x.ReviewSessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReferralEventEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.ReferralId, x.At });
            b.Property(x => x.PayloadJson).HasMaxLength(8000);
            if (isSqlServer) b.Property(x => x.RowVersion).IsRowVersion();
            else b.Ignore(x => x.RowVersion);

            b.HasOne(x => x.Referral)
                .WithMany(x => x.Events)
                .HasForeignKey(x => x.ReferralId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(x => x.ActorUser)
                .WithMany()
                .HasForeignKey(x => x.ActorUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ExternalCheckEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.ReferralId, x.Type });
            b.Property(x => x.Provider).HasMaxLength(128);
            b.Property(x => x.CorrelationId).HasMaxLength(128);
            b.Property(x => x.ResultJson).HasMaxLength(8000);
            if (isSqlServer) b.Property(x => x.RowVersion).IsRowVersion();
            else b.Ignore(x => x.RowVersion);

            b.HasOne(x => x.Referral)
                .WithMany(x => x.ExternalChecks)
                .HasForeignKey(x => x.ReferralId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

