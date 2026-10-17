using ToTrak.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace ToTrak.Api.Data;

public sealed class TasksDbContext(DbContextOptions<TasksDbContext> options)
    : DbContext(options)
{
    public DbSet<ToTrak> ToTraks => Set<ToTrak>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ToTrak>(entity =>
        {
            entity.HasKey(item => item.AssignmentId);
            entity.Property(item => item.Title).HasMaxLength(100).IsRequired();
        });
    }
}
