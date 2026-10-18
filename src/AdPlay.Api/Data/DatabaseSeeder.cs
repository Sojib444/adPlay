using AdPlay.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AdPlay.Api.Data;

/// <summary>Development only. Production schema comes from sql/01_schema.sql.</summary>
public static class DatabaseSeeder
{
    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);
        if (await db.Departments.AnyAsync(ct)) return;

        var engineering = new Department { Name = "Engineering" };
        var sales = new Department { Name = "Sales" };
        var finance = new Department { Name = "Finance" }; // no employees on purpose
        db.Departments.AddRange(engineering, sales, finance);

        var start = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.Employees.AddRange(
            new Employee { Name = "Ayesha", Department = engineering, Salary = 180000, JoiningDate = start },
            new Employee { Name = "Rahim", Department = engineering, Salary = 150000, JoiningDate = start.AddMonths(3) },
            new Employee { Name = "Karim", Department = engineering, Salary = 150000, JoiningDate = start.AddMonths(6) },
            new Employee { Name = "Nusrat", Department = engineering, Salary = 120000, JoiningDate = start.AddMonths(9) },
            new Employee { Name = "Tanvir", Department = sales, Salary = 90000, JoiningDate = start.AddMonths(1) },
            new Employee { Name = "Farzana", Department = sales, Salary = 110000, JoiningDate = start.AddMonths(2) });

        var phones = new Category { Name = "Phones" };
        var laptops = new Category { Name = "Laptops" };
        var acme = new Brand { Name = "Acme" };
        var globex = new Brand { Name = "Globex" };
        db.AddRange(phones, laptops, acme, globex);

        var created = DateTime.UtcNow.Date;
        for (var i = 1; i <= 60; i++)
        {
            db.Products.Add(new Product
            {
                Name = $"{(i % 2 == 0 ? "Phone" : "Laptop")} Model {i:000}",
                Price = 100 + i * 25,
                Category = i % 2 == 0 ? phones : laptops,
                Brand = i % 3 == 0 ? globex : acme,
                CreatedAt = created.AddMinutes(-i)
            });
        }

        db.Users.AddRange(new User { Mobile = "8801700000001" }, new User { Mobile = "8801700000002" });
        await db.SaveChangesAsync(ct);
    }
}
