using AdPlay.Api.Domain;
using AdPlay.Api.Features.Analytics;

namespace AdPlay.Tests;

public class AnalyticsTests
{
    [Fact]
    public void Order_summary_aggregates_per_customer()
    {
        List<Order> orders =
        [
            new() { CustomerId = 1, Amount = 100, OrderDate = new DateTime(2026, 1, 1) },
            new() { CustomerId = 1, Amount = 250, OrderDate = new DateTime(2026, 3, 1) },
            new() { CustomerId = 2, Amount = 80, OrderDate = new DateTime(2026, 2, 1) },
        ];

        var result = OrderAnalytics.Summarize(orders);

        Assert.Equal(2, result.Count);
        Assert.Equal(new CustomerOrderSummary(1, 2, 250, 175, new DateTime(2026, 3, 1)), result[0]);
        Assert.Equal(new CustomerOrderSummary(2, 1, 80, 80, new DateTime(2026, 2, 1)), result[1]);
    }

    [Fact]
    public void Order_summary_keeps_customers_without_orders_when_joined()
    {
        List<Customer> customers = [new() { Id = 1 }, new() { Id = 2 }];
        List<Order> orders = [new() { CustomerId = 1, Amount = 10, OrderDate = new DateTime(2026, 1, 1) }];

        var result = OrderAnalytics.Summarize(customers, orders);

        Assert.Equal(new CustomerOrderSummary(2, 0, null, null, null), result[1]);
    }

    [Fact]
    public void Salary_stats_handle_odd_even_and_empty_departments()
    {
        List<Employee> employees =
        [
            new() { DepartmentId = 1, Salary = 300 },
            new() { DepartmentId = 1, Salary = 100 },
            new() { DepartmentId = 1, Salary = 200 },
            new() { DepartmentId = 2, Salary = 400 },
            new() { DepartmentId = 2, Salary = 100 },
            new() { DepartmentId = 2, Salary = 150 },
            new() { DepartmentId = 2, Salary = 250 },
        ];

        var stats = SalaryStatistics.Calculate(employees, allDepartmentIds: [1, 2, 3]);

        Assert.Equal(new DepartmentSalaryStats(1, 3, 300, 100, 200, 200), stats[0]);     // odd: middle value
        Assert.Equal(new DepartmentSalaryStats(2, 4, 400, 100, 225, 200), stats[1]);     // even: (150 + 250) / 2
        Assert.Equal(new DepartmentSalaryStats(3, 0, null, null, null, null), stats[2]); // empty department
    }

    [Fact]
    public void Median_of_empty_set_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => SalaryStatistics.Median([]));
    }
}
