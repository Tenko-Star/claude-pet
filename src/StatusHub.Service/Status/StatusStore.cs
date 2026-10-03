using StatusHub.Contracts;

namespace StatusHub.Service.Status;

/// <summary>Holds the latest snapshot. Written only by <see cref="StatusReducerService"/>; read by the hub.</summary>
public sealed class StatusStore(TimeProvider time)
{
    private StatusSnapshot _current = StatusSnapshot.Initial(time.GetUtcNow());

    public StatusSnapshot Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}
