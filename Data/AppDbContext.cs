using Microsoft.EntityFrameworkCore;
using ProductOrderApi.Domain;

namespace ProductOrderApi.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("Products", table =>
            {
                table.HasCheckConstraint("CK_Products_Stock_NonNegative", "[Stock] >= 0");
                table.HasCheckConstraint("CK_Products_Price_Positive", "[Price] > 0");
            });
            entity.HasKey(product => product.Id);
            entity.Property(product => product.Name).HasMaxLength(200).IsRequired();
            entity.Property(product => product.Price).HasPrecision(18, 2);
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("Orders");
            entity.HasKey(order => order.Id);
            entity.Property(order => order.Status)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();
            entity.Property(order => order.IdempotencyKey).HasMaxLength(200);
            entity.Property(order => order.RequestHash).HasMaxLength(64).IsFixedLength();
            entity.HasIndex(order => order.IdempotencyKey)
                .IsUnique()
                .HasFilter("[IdempotencyKey] IS NOT NULL");
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.ToTable("OrderItems", table =>
                table.HasCheckConstraint("CK_OrderItems_Quantity_Positive", "[Quantity] > 0"));
            entity.HasKey(item => new { item.OrderId, item.ProductId });
            entity.Property(item => item.UnitPrice).HasPrecision(18, 2);
            entity.HasIndex(item => item.ProductId);
            entity.HasOne(item => item.Order)
                .WithMany(order => order.Items)
                .HasForeignKey(item => item.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.Product)
                .WithMany(product => product.OrderItems)
                .HasForeignKey(item => item.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
