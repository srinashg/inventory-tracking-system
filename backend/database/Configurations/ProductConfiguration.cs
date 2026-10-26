using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TechStore.Database.Entities;

namespace TechStore.Database.Configurations;

public class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products", t =>
            t.HasCheckConstraint("ck_products_price_nonnegative", "price >= 0"));

        builder.HasKey(p => p.ProductId).HasName("pk_products");
        builder.Property(p => p.ProductId).HasColumnName("product_id");

        builder.Property(p => p.Sku).HasColumnName("sku").HasMaxLength(50).IsRequired();
        builder.HasIndex(p => p.Sku).IsUnique().HasDatabaseName("ux_products_sku");

        builder.Property(p => p.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
        builder.Property(p => p.Price).HasColumnName("price").HasColumnType("numeric(12,2)");

        // The sentinel (true) tells EF "not set" means true, so an explicit false is still inserted as false.
        builder.Property(p => p.IsActive).HasColumnName("is_active")
            .HasDefaultValue(true).HasSentinel(true);
    }
}
