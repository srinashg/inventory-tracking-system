using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TechStore.Database.Entities;

namespace TechStore.Database.Configurations;

public class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("order_items", t =>
        {
            t.HasCheckConstraint("ck_order_items_quantity_positive", "quantity > 0");
            t.HasCheckConstraint("ck_order_items_unit_price_nonnegative", "unit_price >= 0");
        });

        builder.HasKey(i => i.OrderItemId).HasName("pk_order_items");
        builder.Property(i => i.OrderItemId).HasColumnName("order_item_id");

        builder.Property(i => i.OrderId).HasColumnName("order_id");
        builder.Property(i => i.ProductId).HasColumnName("product_id");
        builder.Property(i => i.Quantity).HasColumnName("quantity");
        builder.Property(i => i.UnitPrice).HasColumnName("unit_price").HasColumnType("numeric(12,2)");

        // A product may appear at most once per order.
        builder.HasIndex(i => new { i.OrderId, i.ProductId }).IsUnique().HasDatabaseName("ux_order_items_order_product");
        builder.HasIndex(i => i.ProductId).HasDatabaseName("ix_order_items_product_id");

        builder.HasOne(i => i.Order).WithMany(o => o.Items)
            .HasForeignKey(i => i.OrderId)
            .HasConstraintName("fk_order_items_order")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(i => i.Product).WithMany(p => p.OrderItems)
            .HasForeignKey(i => i.ProductId)
            .HasConstraintName("fk_order_items_product")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
