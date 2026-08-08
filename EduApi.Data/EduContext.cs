using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using EduApi.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EduApi.Data;

public class EduContext(DbContextOptions<EduContext> options) : DbContext(options)
{
    public DbSet<ScoreResponse> Scores { get; set; }
    public DbSet<ElectricitySubscription> ElectricitySubscriptions { get; set; }
    public DbSet<ElectricityNotificationLog> ElectricityNotificationLogs { get; set; }
    public DbSet<MapPoiModel> MapPois { get; set; }
    public DbSet<ExamRecord> ExamRecords { get; set; }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ScoreResponse>(entity =>
        {
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.Semester);
            entity.HasIndex(e => new { e.UserId, e.Semester });
        });

        modelBuilder.Entity<ElectricitySubscription>(entity =>
        {
            entity.HasIndex(e => new { e.Email, e.ElectricityUrl }).IsUnique();
            entity.HasIndex(e => new { e.IsActive, e.NextCheckAt });
            entity.Property(e => e.Threshold).HasDefaultValue(0);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<ElectricityNotificationLog>(entity =>
        {
            entity.HasIndex(e => new { e.SubscriptionId, e.CreatedAt });
            entity.HasOne(e => e.Subscription)
                .WithMany(s => s.NotificationLogs)
                .HasForeignKey(e => e.SubscriptionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MapPoiModel>(entity =>
        {
            entity.HasIndex(e => e.Category);
            entity.HasIndex(e => e.Campus);
            entity.HasIndex(e => e.IsActive);
            entity.HasIndex(e => new { e.Latitude, e.Longitude });
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<ExamRecord>(entity =>
        {
            entity.HasIndex(e => e.StudentId);
            entity.HasIndex(e => e.ExamTime);
            entity.HasIndex(e => new { e.StudentId, e.ExamTime });
        });

        base.OnModelCreating(modelBuilder);
    }
}

[Serializable]
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<EduContext>
{
    public EduContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EduContext>();
        var sqlConnectionString = Environment.GetEnvironmentVariable("SQL");

        optionsBuilder.UseNpgsql(sqlConnectionString);

        return new EduContext(optionsBuilder.Options);
    }
}

public static class DataTool
{
    public static string StringToHash(string s)
    {
        var data = Encoding.UTF8.GetBytes(s);
        var hash = MD5.HashData(data);
        var hashStringBuilder = new StringBuilder();
        foreach (var t in hash)
            hashStringBuilder.Append(t.ToString("x2"));
        return hashStringBuilder.ToString();
    }

    public static string ToHash(this object t) => StringToHash(t.ToString()!);

    public static string GetProperties<T>(T t)
    {
        StringBuilder builder = new StringBuilder();
        if (t == null) return builder.ToString();

        var properties = t.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);

        if (properties.Length <= 0) return builder.ToString();

        foreach (var item in properties)
        {
            var name = item.Name;
            var value = item.GetValue(t, null);
            if (item.PropertyType.IsValueType || item.PropertyType.Name.StartsWith("String"))
            {
                builder.Append($"{name}:{value ?? "null"},");
            }
        }

        return builder.ToString();
    }
}

public abstract class DataModel
{
    public override string ToString() => $"{GetType()} : {DataTool.GetProperties(this)}";
    public string GetHashKey() => DataTool.StringToHash(ToString());
}
