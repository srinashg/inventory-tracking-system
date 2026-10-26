using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TechStore.Database.Entities;

namespace TechStore.Database.Configurations;

public class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> builder)
    {
        builder.ToTable("warehouses");

        builder.HasKey(w => w.WarehouseId).HasName("pk_warehouses");
        builder.Property(w => w.WarehouseId).HasColumnName("warehouse_id");

        builder.Property(w => w.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
        builder.Property(w => w.Location).HasColumnName("location").HasMaxLength(255).IsRequired();
    }
}
