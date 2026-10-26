using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TechStore.Database.Entities;

namespace TechStore.Database.Configurations;

public class InventoryBalanceConfiguration : IEntityTypeConfiguration<InventoryBalance>
{
    public void Configure(EntityTypeBuilder<InventoryBalance> builder)
    {
        builder.ToTable("inventory_balances", t =>
        {
            t.HasCheckConstraint("ck_inventory_balances_on_hand_nonnegative", "on_hand >= 0");
            t.HasCheckConstraint("ck_inventory_balances_reserved_nonnegative", "reserved >= 0");
            // Keeps available (on_hand - reserved) from ever going negative.
            t.HasCheckConstraint("ck_inventory_balances_reserved_within_on_hand", "reserved <= on_hand");
        });

        // Composite key: exactly one balance per product-warehouse pair.
        builder.HasKey(b => new { b.ProductId, b.WarehouseId }).HasName("pk_inventory_balances");

        builder.Property(b => b.ProductId).HasColumnName("product_id");
        builder.Property(b => b.WarehouseId).HasColumnName("warehouse_id");
        builder.Property(b => b.OnHand).HasColumnName("on_hand").HasDefaultValue(0);
        builder.Property(b => b.Reserved).HasColumnName("reserved").HasDefaultValue(0);

        // Available is calculated in C#; there is no column for it.
        builder.Ignore(b => b.Available);

        builder.HasIndex(b => b.WarehouseId).HasDatabaseName("ix_inventory_balances_warehouse_id");

        builder.HasOne(b => b.Product).WithMany(p => p.InventoryBalances)
            .HasForeignKey(b => b.ProductId)
            .HasConstraintName("fk_inventory_balances_product")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(b => b.Warehouse).WithMany(w => w.InventoryBalances)
            .HasForeignKey(b => b.WarehouseId)
            .HasConstraintName("fk_inventory_balances_warehouse")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
