using System.Collections.Concurrent;

namespace Rbac.Api;

/// <summary>In-memory order store, seeded with sample data. Reset on every restart.</summary>
public sealed class OrderStore
{
    private readonly ConcurrentDictionary<int, Order> _orders = new();
    private int _nextId;

    public OrderStore()
    {
        Add("Contoso Ltd", 1250.00m);
        Add("Fabrikam Inc", 430.50m);
        Add("Northwind Traders", 89.99m);
    }

    public IReadOnlyList<Order> All() => _orders.Values.OrderBy(o => o.Id).ToList();

    public Order Add(string customer, decimal amount)
    {
        var order = new Order(Interlocked.Increment(ref _nextId), customer, amount, DateTimeOffset.UtcNow);
        _orders[order.Id] = order;
        return order;
    }

    public bool Remove(int id, out Order? removed) => _orders.TryRemove(id, out removed);
}

/// <summary>In-memory audit trail of who did what. Only admins may read it.</summary>
public sealed class AuditLog
{
    private readonly ConcurrentQueue<AuditEntry> _entries = new();

    public void Record(string user, string action, string details) =>
        _entries.Enqueue(new AuditEntry(DateTimeOffset.UtcNow, user, action, details));

    public IReadOnlyList<AuditEntry> All() => _entries.OrderByDescending(e => e.At).ToList();
}
