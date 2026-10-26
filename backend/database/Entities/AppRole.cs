using Microsoft.AspNetCore.Identity;

namespace TechStore.Database.Entities;

/// <summary>A named set of permissions: Admin, Sales or Warehouse. Maps to the "roles" table.</summary>
public class AppRole : IdentityRole<long>
{
    public AppRole()
    {
    }

    public AppRole(string name) : base(name)
    {
    }
}
