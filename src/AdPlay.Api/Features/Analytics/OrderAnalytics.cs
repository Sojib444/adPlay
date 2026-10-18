using AdPlay.Api.Domain;

namespace AdPlay.Api.Features.Analytics;

public sealed record CustomerOrderSummary(
    int CustomerId,
    int TotalOrders,
    decimal? HighestOrderAmount,
    decimal? AverageOrderAmount,
    DateTime? LatestOrderDate);

public static class OrderAnalytics
{
    /// <summary>One row per customer that has at least one order.</summary>
    public static List<CustomerOrderSummary> Summarize(IEnumerable<Order> orders) =>
        orders
            .GroupBy(o => o.CustomerId)
            .Select(g => new CustomerOrderSummary(
                g.Key,
                g.Count(),
                g.Max(o => o.Amount),
                Math.Round(g.Average(o => o.Amount), 2, MidpointRounding.AwayFromZero),
                g.Max(o => o.OrderDate)))
            .OrderBy(s => s.CustomerId)
            .ToList();

    /// <summary>
    /// Left-joins so customers without orders are kept, with TotalOrders = 0 and null aggregates
    /// ("no orders" is not "an order of 0").
    /// </summary>
    public static List<CustomerOrderSummary> Summarize(IEnumerable<Customer> customers, IEnumerable<Order> orders) =>
        customers
            .GroupJoin(orders, c => c.Id, o => o.CustomerId, (c, os) => new { c.Id, Orders = os.ToList() })
            .Select(x => new CustomerOrderSummary(
                x.Id,
                x.Orders.Count,
                x.Orders.Count == 0 ? null : x.Orders.Max(o => o.Amount),
                x.Orders.Count == 0 ? null : Math.Round(x.Orders.Average(o => o.Amount), 2, MidpointRounding.AwayFromZero),
                x.Orders.Count == 0 ? null : x.Orders.Max(o => o.OrderDate)))
            .OrderBy(s => s.CustomerId)
            .ToList();
}
