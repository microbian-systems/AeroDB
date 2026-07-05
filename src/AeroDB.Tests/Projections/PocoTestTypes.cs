using SurrealDb.Net.Models;

namespace AeroDB.Tests.Projections;

// ── POCO document types with various identity properties ──

public class PocoTrip
{
    public Guid Id { get; set; }
    public string State { get; set; } = "";
    public int StartedOn { get; set; }
    public bool Active { get; set; }
}

public class PocoDay
{
    public int Id { get; set; }
    public int Started { get; set; }
    public int Ended { get; set; }
}

public class PocoDistance
{
    public int Id { get; set; }
    public double Total { get; set; }
    public int Day { get; set; }
}

public class PocoSnapshot
{
    public Guid Id { get; set; }
    public int Count { get; set; }
    public string Name { get; set; } = "";

    public void Apply(PocoSnapshotIncremented e) => Count += e.Amount;
    public void Apply(PocoSnapshotRenamed e) => Name = e.NewName;
    public void When(PocoSnapshotIncremented e) => Count += e.Amount; // alias for Apply
}

// ── Record-based types (backward compatibility test) ──

public class RecordTrip : Record
{
    public string State { get; set; } = "";
    public int StartedOn { get; set; }
    public bool Active { get; set; }
}

// ── Event types (plain C# records) ──

public record TripStarted(int Day);
public record TripEnded(int Day);
public record Travel(int Day, double Distance);
public record Arrival(int Day, string State);
public record IDayEvent(int Day); // base

public record PocoSnapshotIncremented(int Amount);
public record PocoSnapshotRenamed(string NewName);
