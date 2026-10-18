namespace AdPlay.Api.Domain;

public class Department
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class Employee
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int DepartmentId { get; set; }
    public Department? Department { get; set; }
    public decimal Salary { get; set; }
    public DateTime JoiningDate { get; set; }
}

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Country { get; set; } = "";
    public string City { get; set; } = "";
    public List<Order> Orders { get; set; } = [];
}

public class Order
{
    public long Id { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? ProductId { get; set; }
    public DateTime OrderDate { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = "Pending";
}

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class Brand
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public int CategoryId { get; set; }
    public Category? Category { get; set; }
    public int BrandId { get; set; }
    public Brand? Brand { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

public class User
{
    public int Id { get; set; }
    public string Mobile { get; set; } = "";
    public bool IsSubscribed { get; set; }
    public DateTime? SubscribedAt { get; set; }
}

/// <summary>Append-only audit row; the unique index on UserId is the last line of defence against double subscription.</summary>
public class Subscription
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum PaymentStatus
{
    Pending,     // row created, provider not yet called
    Processing,  // provider call in flight
    Succeeded,
    Failed,
    Unknown      // provider timed out / ambiguous; reconciliation decides
}

public class Payment
{
    public Guid Id { get; set; }
    public string MerchantId { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string OrderId { get; set; } = "";

    /// <summary>
    /// Equals OrderId while the payment is live (Pending/Processing/Succeeded/Unknown) and NULL once Failed.
    /// A UNIQUE index on this column allows many failed attempts but only one live payment per order
    /// (MySQL has no partial indexes; UNIQUE ignores NULLs).
    /// </summary>
    public string? ActiveOrderId { get; set; }

    public decimal Amount { get; set; }
    public string Currency { get; set; } = "BDT";
    public PaymentStatus Status { get; set; }
    public string? ProviderReference { get; set; }
    public string? FailureReason { get; set; }
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class WebhookEvent
{
    public long Id { get; set; }
    public string Provider { get; set; } = "";
    public string ProviderEventId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string? ProviderReference { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}
