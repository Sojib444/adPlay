using AdPlay.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AdPlay.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Department>(e => e.Property(x => x.Name).HasMaxLength(100));

        b.Entity<Employee>(e =>
        {
            e.ToTable("Employee");
            e.Property(x => x.Name).HasMaxLength(150);
            e.Property(x => x.Salary).HasPrecision(12, 2);
            e.HasIndex(x => new { x.DepartmentId, x.Salary });
        });

        b.Entity<Customer>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(150);
            e.Property(x => x.Country).HasMaxLength(2).IsFixedLength();
            e.Property(x => x.City).HasMaxLength(100);
            e.HasIndex(x => new { x.Country, x.City });
            e.HasIndex(x => x.Name);
        });

        b.Entity<Order>(e =>
        {
            e.Property(x => x.Amount).HasPrecision(12, 2);
            e.Property(x => x.Status).HasMaxLength(20);
            e.HasIndex(x => new { x.CustomerId, x.OrderDate, x.Id });
            e.HasIndex(x => x.OrderDate);
            e.HasIndex(x => new { x.Status, x.OrderDate });
        });

        b.Entity<Category>(e => e.Property(x => x.Name).HasMaxLength(100));
        b.Entity<Brand>(e => e.Property(x => x.Name).HasMaxLength(100));

        b.Entity<Product>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Price).HasPrecision(12, 2);
            e.HasIndex(x => new { x.IsActive, x.CategoryId, x.Price });
            e.HasIndex(x => new { x.IsActive, x.BrandId, x.Price });
            e.HasIndex(x => new { x.IsActive, x.CreatedAt, x.Id }); // keyset pagination
            e.HasIndex(x => x.Name);
        });

        b.Entity<User>(e =>
        {
            e.Property(x => x.Mobile).HasMaxLength(20);
            e.HasIndex(x => x.Mobile).IsUnique();
        });

        b.Entity<Subscription>(e => e.HasIndex(x => x.UserId).IsUnique());

        b.Entity<Payment>(e =>
        {
            e.Property(x => x.MerchantId).HasMaxLength(64);
            e.Property(x => x.IdempotencyKey).HasMaxLength(64);
            e.Property(x => x.RequestHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.OrderId).HasMaxLength(64);
            e.Property(x => x.ActiveOrderId).HasMaxLength(64);
            e.Property(x => x.Amount).HasPrecision(12, 2);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ProviderReference).HasMaxLength(100);
            e.Property(x => x.FailureReason).HasMaxLength(200);

            e.HasIndex(x => new { x.MerchantId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.MerchantId, x.ActiveOrderId }).IsUnique();
            e.HasIndex(x => x.ProviderReference);
            e.HasIndex(x => new { x.Status, x.UpdatedAt }); // reconciliation sweep
        });

        b.Entity<WebhookEvent>(e =>
        {
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.ProviderEventId).HasMaxLength(100);
            e.Property(x => x.EventType).HasMaxLength(50);
            e.Property(x => x.ProviderReference).HasMaxLength(100);
            e.HasIndex(x => new { x.Provider, x.ProviderEventId }).IsUnique();
        });
    }
}
