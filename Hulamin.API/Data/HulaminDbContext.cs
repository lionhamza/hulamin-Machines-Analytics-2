using Microsoft.EntityFrameworkCore;

public class HulaminDbContext : DbContext
{
    public HulaminDbContext(DbContextOptions<HulaminDbContext> options)
        : base(options) { }

    public DbSet<Production> Production { get; set; }
    public DbSet<Machine> Machines { get; set; }
    public DbSet<DateDimension> Dates { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<Production>()
        .ToTable("production")
        .HasKey(p => p.id);

    modelBuilder.Entity<Machine>()
        .ToTable("machines")
        .HasKey(m => m.machine_id);

    modelBuilder.Entity<DateDimension>()
        .ToTable("dates")
        .HasKey(d => d.date);
}
}