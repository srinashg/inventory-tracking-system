using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TechStore.Database.Configurations;
using TechStore.Database.Entities;

namespace TechStore.Database;

/// <summary>
/// EF Core's gateway to PostgreSQL. Each DbSet is a table; services query these sets with LINQ
/// and call SaveChangesAsync() to write pending changes in one transaction.
/// It also holds the ASP.NET Core Identity tables (users, roles, user_roles, ...) for sign-in.
/// The inventory and order services never touch them.
/// </summary>
public class TechStoreDbContext : IdentityDbContext<AppUser, AppRole, long>
{
    public TechStoreDbContext(DbContextOptions<TechStoreDbContext> options) : base(options)
    {
    }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<InventoryBalance> InventoryBalances => Set<InventoryBalance>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // the Identity tables

        // Picks up every IEntityTypeConfiguration<T> class in the Configurations folder.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TechStoreDbContext).Assembly);

        modelBuilder.ConfigureIdentityTables();
    }
}
