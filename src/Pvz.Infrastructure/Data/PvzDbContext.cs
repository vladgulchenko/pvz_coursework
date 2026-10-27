using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pvz.Core;

namespace Pvz.Infrastructure.Data;

public sealed class PvzDbContext(DbContextOptions<PvzDbContext> options) : DbContext(options)
{
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<StorageLocation> StorageLocations => Set<StorageLocation>();
    public DbSet<OrderStatusHistory> OrderStatusHistory => Set<OrderStatusHistory>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var utc = new ValueConverter<DateTime, DateTime>(
            value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        model.Entity<Employee>(e =>
        {
            e.ToTable("Employees", t =>
            {
                t.HasCheckConstraint("CK_Employees_Role", "Role IN (0, 1)");
                t.HasCheckConstraint("CK_Employees_Name", "length(trim(Name)) BETWEEN 1 AND 120");
                t.HasCheckConstraint("CK_Employees_Login", "length(trim(Login)) BETWEEN 1 AND 80 AND length(NormalizedLogin) > 0");
                t.HasCheckConstraint("CK_Employees_Hash", "length(PasswordHash) > 0");
            });
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.NormalizedLogin).IsUnique();
            e.Property(x => x.Name).IsRequired().HasMaxLength(120);
            e.Property(x => x.Login).IsRequired().HasMaxLength(80);
            e.Property(x => x.NormalizedLogin).IsRequired().HasMaxLength(80);
            e.Property(x => x.PasswordHash).IsRequired();
        });
        model.Entity<StorageLocation>(e =>
        {
            e.ToTable("StorageLocations", t =>
            {
                t.HasCheckConstraint("CK_StorageLocations_Rack", "length(Rack) BETWEEN 1 AND 40 AND Rack = upper(trim(Rack))");
                t.HasCheckConstraint("CK_StorageLocations_Cell", "length(Cell) BETWEEN 1 AND 40 AND Cell = upper(trim(Cell))");
            });
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Rack, x.Cell }).IsUnique();
            e.Property(x => x.Rack).IsRequired().HasMaxLength(40);
            e.Property(x => x.Cell).IsRequired().HasMaxLength(40);
        });
        model.Entity<Order>(e =>
        {
            e.ToTable("Orders", t =>
            {
                t.HasCheckConstraint("CK_Orders_Number", "length(Number) BETWEEN 1 AND 80 AND Number = upper(trim(Number))");
                t.HasCheckConstraint("CK_Orders_Recipient", "length(trim(RecipientName)) BETWEEN 1 AND 120 AND length(trim(RecipientPhone)) BETWEEN 1 AND 40");
                t.HasCheckConstraint("CK_Orders_Dates", "StorageUntil > ReceivedAt");
                t.HasCheckConstraint("CK_Orders_Status", "Status IN (0, 1, 2, 3)");
                t.HasCheckConstraint("CK_Orders_Placement", "(Status IN (0, 2) AND StorageLocationId IS NOT NULL) OR (Status IN (1, 3) AND StorageLocationId IS NULL)");
            });
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.Number).IsRequired().HasMaxLength(80);
            e.Property(x => x.RecipientName).IsRequired().HasMaxLength(120);
            e.Property(x => x.RecipientPhone).IsRequired().HasMaxLength(40);
            e.Property(x => x.Status).IsConcurrencyToken();
            e.Property(x => x.ReceivedAt).HasConversion(utc);
            e.Property(x => x.StorageUntil).HasConversion(utc);
            e.HasOne(x => x.StorageLocation).WithMany().HasForeignKey(x => x.StorageLocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.History).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<OrderItem>(e =>
        {
            e.ToTable("OrderItems", t =>
            {
                t.HasCheckConstraint("CK_OrderItems_Quantity", "Quantity > 0");
                t.HasCheckConstraint("CK_OrderItems_Name", "length(trim(Name)) BETWEEN 1 AND 200");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
        });
        model.Entity<OrderStatusHistory>(e =>
        {
            e.ToTable("OrderStatusHistory", t =>
            {
                t.HasCheckConstraint("CK_History_Statuses", "NewStatus IN (0, 1, 2, 3) AND (PreviousStatus IS NULL OR PreviousStatus IN (0, 1, 2, 3))");
                t.HasCheckConstraint("CK_History_Reason", "NewStatus <> 2 OR length(trim(ReturnReason)) > 0 AND ReturnReason IS NOT NULL");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.ChangedAt).HasConversion(utc);
            e.Property(x => x.ReturnReason).HasMaxLength(500);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
