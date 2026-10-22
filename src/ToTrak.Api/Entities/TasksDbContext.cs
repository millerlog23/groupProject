using ToTrak.Api.Entities;
using Microsoft.EntityFrameworkCore;
using AssignmentEntity = ToTrak.Api.Entities.ToTrak;

namespace ToTrak.Api.Data;

public sealed class TasksDbContext(DbContextOptions<TasksDbContext> options)
    : DbContext(options)
{
    public DbSet<AssignmentEntity> Assignments => Set<AssignmentEntity>();
    public DbSet<AuditRecord> Audit => Set<AuditRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AssignmentEntity>(entity =>
        {
            entity.ToTable("Assignments");
            entity.HasKey(item => item.AssignmentId);
            entity.Property(item => item.Title).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<AuditRecord>(entity =>
        {
            entity.ToTable("Audit");
            entity.HasKey(item => item.ChangeId);
            entity.HasIndex(item => item.ClientChangeId).IsUnique();
            entity.Property(item => item.EntityChanged).HasMaxLength(100).IsRequired();
            entity.Property(item => item.ChangeType).HasMaxLength(50).IsRequired();
        });
    }
}
