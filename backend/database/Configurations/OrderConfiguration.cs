using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TechStore.Database.Entities;

namespace TechStore.Database.Configurations;

public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        // "order" is a reserved word in SQL, so the table is named "orders".
        builder.ToTable("orders", t =>
            t.HasCheckConstraint("ck_orders_status",
                "status IN ('Draft','Reserved','Picking','Packed','Shipped','Canceled')"));

        builder.HasKey(o => o.OrderId).HasName("pk_orders");
        builder.Property(o => o.OrderId).HasColumnName("order_id");

        builder.Property(o => o.CustomerId).HasColumnName("customer_id");
        builder.Property(o => o.WarehouseId).HasColumnName("warehouse_id");

        // Stored as text ("Draft", "Reserved", ...) rather than a number, so the data is readable in SQL.
        builder.Property(o => o.Status).HasColumnName("status").HasMaxLength(20).HasConversion<string>()
            .HasDefaultValueSql("'Draft'")
            .HasSentinel(OrderStatus.Draft); // "Draft" means "not set", so the database default ('Draft') applies
        builder.Property(o => o.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");

        builder.HasIndex(o => o.CustomerId).HasDatabaseName("ix_orders_customer_id");
        builder.HasIndex(o => o.WarehouseId).HasDatabaseName("ix_orders_warehouse_id");
        builder.HasIndex(o => o.Status).HasDatabaseName("ix_orders_status");

        builder.HasOne(o => o.Customer).WithMany(c => c.Orders)
            .HasForeignKey(o => o.CustomerId)
            .HasConstraintName("fk_orders_customer")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(o => o.Warehouse).WithMany(w => w.Orders)
            .HasForeignKey(o => o.WarehouseId)
            .HasConstraintName("fk_orders_warehouse")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
