using Microsoft.EntityFrameworkCore;
using TranscriptionPipeline.Models;

namespace TranscriptionPipeline.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<TranscriptionJob> Jobs => Set<TranscriptionJob>();
    public DbSet<TranscriptSegment> Segments => Set<TranscriptSegment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TranscriptionJob>()
            .HasMany(j => j.Segments)
            .WithOne(s => s.Job)
            .HasForeignKey(s => s.TranscriptionJobId);

        modelBuilder.Entity<TranscriptionJob>()
            .Property(j => j.Status)
            .HasConversion<string>();
    }
}
