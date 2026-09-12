namespace Wolfstare.Service.Enforcement;

public enum EnforcementStatus
{
    Ok,

    /// <summary>Some enforcement is running, but not all of it — e.g. the DNS port was taken.</summary>
    Degraded,
}

/// <summary>
/// The health of the enforcement subsystems, reported through <c>GET /api/status</c>.
///
/// A degraded state is deliberately sticky and loud rather than silently recovered: if the DNS
/// port could not be bound, the user is relying on a weaker fallback and should be told, not
/// left believing the block is airtight.
/// </summary>
public sealed class EnforcementHealth
{
    private readonly Lock _gate = new();
    private EnforcementStatus _status = EnforcementStatus.Ok;
    private string _detail = "All enforcement subsystems are running.";

    public EnforcementStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public string Detail
    {
        get { lock (_gate) return _detail; }
    }

    public void Degrade(string detail)
    {
        lock (_gate)
        {
            _status = EnforcementStatus.Degraded;
            _detail = detail;
        }
    }
}
