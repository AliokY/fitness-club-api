using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer;

public class ClubDbContext : DbContext
{
    public ClubDbContext(DbContextOptions<ClubDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();

    public DbSet<Room> Rooms => Set<Room>();

    public DbSet<Class> Classes => Set<Class>();

    public DbSet<Enrollment> Enrollments => Set<Enrollment>();

    public DbSet<Pass> Passes => Set<Pass>();

    public DbSet<WaitlistEntry> WaitlistEntries => Set<WaitlistEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ClubDbContext).Assembly);
    }
}
