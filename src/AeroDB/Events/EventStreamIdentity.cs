namespace AeroDB;

public readonly record struct EventStreamIdentity(string Value, Guid Key)
{
    public static implicit operator string(EventStreamIdentity identity) => identity.Value;

    public static bool operator ==(EventStreamIdentity identity, Guid key) => identity.Key == key;

    public static bool operator !=(EventStreamIdentity identity, Guid key) => identity.Key != key;

    public static bool operator ==(Guid key, EventStreamIdentity identity) => identity.Key == key;

    public static bool operator !=(Guid key, EventStreamIdentity identity) => identity.Key != key;

    public override string ToString() => Value;
}
