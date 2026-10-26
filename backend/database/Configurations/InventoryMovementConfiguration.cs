using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TechStore.Database.Entities;

namespace TechStore.Database.Configurations;

public class InventoryMovementConfiguration : IEntityTypeConfiguration<InventoryMovement>
{
    public void Configure(EntityTypeBuilder<InventoryMovement> builder)
    {
        builder.ToTable("inventory_movements", t =>
        {
            t.HasCheckConstraint("ck_inventory_movements_change_nonzero", "quantity_change <> 0");
            t.HasCheckConstraint("ck_inventory_movements_type",
                "type IN ('Receipt','Adjustment','Reservation','Release','Shipment')");
            t.HasCheckConstraint("ck_inventory_movements_adjustment_has_reason",
                "type <> 'Adjustment' OR (reason IS NOT NULL AND length(trim(reason)) > 0)");
            t.HasCheckConstraint("ck_inventory_movements_receipt_positive",
                "type <> 'Receipt' OR quantity_change > 0");
            t.HasCheckConstraint("ck_inventory_movements_reservation_positive",
                "type <> 'Reservation' OR quantity_change > 0");
            t.HasCheckConstraint("ck_inventory_movements_release_shipment_negative",
                "type NOT IN ('Release','Shipment') OR quantity_change < 0");
        });

        builder.HasKey(m => m.MovementId).HasName("pk_inventory_movements");
        builder.Property(m => m.MovementId).HasColumnName("movement_id");

        builder.Property(m => m.ProductId).HasColumnName("product_id");
        builder.Property(m => m.WarehouseId).HasColumnName("warehouse_id");
        builder.Property(m => m.OrderId).HasColumnName("order_id");
        builder.Property(m => m.QuantityChange).HasColumnName("quantity_change");

        // Stored as text ("Receipt", "Shipment", ...) rather than a number, so the data is readable in SQL.
        builder.Property(m => m.Type).HasColumnName("type").HasMaxLength(30).HasConversion<string>();
        builder.Property(m => m.Reason).HasColumnName("reason").HasMaxLength(255);
        builder.Property(m => m.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");

        builder.HasIndex(m => new { m.ProductId, m.WarehouseId, m.CreatedAt })
            .HasDatabaseName("ix_inventory_movements_product_warehouse_created");
        builder.HasIndex(m => m.OrderId).HasDatabaseName("ix_inventory_movements_order_id");
        builder.HasIndex(m => m.WarehouseId).HasDatabaseName("ix_inventory_movements_warehouse_id");

        builder.HasOne(m => m.Product).WithMany()
            .HasForeignKey(m => m.ProductId)
            .HasConstraintName("fk_inventory_movements_product")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(m => m.Warehouse).WithMany()
            .HasForeignKey(m => m.WarehouseId)
            .HasConstraintName("fk_inventory_movements_warehouse")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(m => m.Order).WithMany(o => o.Movements)
            .HasForeignKey(m => m.OrderId)
            .HasConstraintName("fk_inventory_movements_order")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
