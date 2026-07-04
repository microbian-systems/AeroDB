namespace AeroDB.Samples.Shared;

public class TripStarted : IDayEvent
{
    public int Day { get; set; }
}

public class TripEnded : IDayEvent
{
    public int Day { get; set; }
    public string State { get; set; } = "";
}

public class Travel : IDayEvent
{
    public int Day { get; set; }
    public IList<Movement> Movements { get; set; } = new List<Movement>();
    public List<Stop> Stops { get; set; } = new();

    public double TotalDistance() => Movements.Sum(x => x.Distance);
}

public class Arrival
{
    public int Day { get; set; }
    public string State { get; set; } = "";
}

public class Departure
{
    public int Day { get; set; }
    public string State { get; set; } = "";
}

public class TripAborted { }

public class Breakdown
{
    public bool IsCritical { get; set; }
}

public class VacationOver { }

public class FailingEvent { }
