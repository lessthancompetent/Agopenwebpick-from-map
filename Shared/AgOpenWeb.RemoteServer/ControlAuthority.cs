// Remote actuation authority (Phase 2 safety layer). A single client may hold
// "control" at a time; only the fresh holder's Tier-2 (live actuation) commands
// are honored by the hub. Presence must be refreshed (heartbeat) within the
// deadman window or the hold is revoked. A socket drop or a stale hold fires
// Revoked → the host runs its failsafe (Phase 3: disengage what the remote
// actuated). All state behind a lock; touched from the hub (per-connection) and
// the broadcaster sweep.

namespace AgOpenWeb.RemoteServer;

public sealed class ControlAuthority
{
    // No presence for this long (ms) while holding → deadman revoke. The client
    // heartbeats at ~2 Hz foreground, but browsers throttle background-tab timers
    // to ≥1 Hz (sometimes worse), and a throttled-but-connected operator UI must
    // not flap the seat — every flap force-disengages autosteer AND the section
    // auto master (the failsafe), which reads as "auto keeps turning itself off".
    // 5 s still fails fast on a genuinely dead client (socket drop is instant
    // anyway — Drop() fires on close; the deadman only covers zombie sockets).
    private const long DeadmanMs = 5000;

    private readonly object _lock = new();
    private Guid? _holder;
    private string _holderName = "";
    private long _lastPresenceTicks;

    /// <summary>Authority state changed (acquired / released / revoked). Carries the
    /// new state so the host can broadcast it and drive the native banner.</summary>
    public event Action<ControlStateDto>? Changed;

    /// <summary>Authority lost involuntarily (disconnect / deadman). The host runs
    /// its failsafe. Not raised on a voluntary Release.</summary>
    public event Action<string>? Revoked;

    public ControlStateDto Snapshot()
    {
        lock (_lock)
            return new ControlStateDto(_holder.HasValue, _holder?.ToString() ?? "", _holderName);
    }

    /// <summary>True only when <paramref name="conn"/> holds control AND its presence
    /// is fresh — the gate the hub applies to every Tier-2 command.</summary>
    public bool HoldsFresh(Guid conn)
    {
        lock (_lock)
            return _holder == conn && (Environment.TickCount64 - _lastPresenceTicks) <= DeadmanMs;
    }

    /// <summary>Take control. Granted if free, already held by this connection, or held
    /// by a connection whose presence has lapsed the deadman (a stale hold is no hold);
    /// denied only if another FRESH connection holds it (first-come, single authority).</summary>
    public bool Acquire(Guid conn, string name)
    {
        ControlStateDto? snap = null;
        bool revokedStale = false;
        lock (_lock)
        {
            if (_holder.HasValue && _holder != conn)
            {
                // A holder that has stopped heart-beating has no valid claim. Refusing a
                // fresh claimant on its behalf left a reconnecting kiosk (after a deploy
                // restart / link drop) stuck as Observer until the periodic sweep ran —
                // every AUTO press silently dropped with nothing in the log.
                bool stale = (Environment.TickCount64 - _lastPresenceTicks) > DeadmanMs;
                if (!stale) return false;
                revokedStale = true;
            }
            _holder = conn;
            _holderName = string.IsNullOrWhiteSpace(name) ? "Remote" : name.Trim();
            _lastPresenceTicks = Environment.TickCount64;
            snap = new ControlStateDto(true, _holder.ToString()!, _holderName);
        }
        // Like Takeover: the seat never goes empty (handed straight to the new claimant),
        // so do NOT raise Revoked — that runs the host failsafe and would disengage the
        // steering the new operator just took. The dead holder is simply superseded.
        _ = revokedStale;
        Changed?.Invoke(snap);
        return true;
    }

    /// <summary>
    /// Deliberate operator takeover: reassign the seat to <paramref name="conn"/> even if
    /// another connection holds it. Unlike <see cref="Acquire"/> (first-come, used for the
    /// implicit connect-time claim), this backs the explicit tap-the-role-badge action —
    /// without it a second browser (tablet in the cab vs the launcher's own WebView, which
    /// always connects first and wins the implicit claim) is stuck as Observer forever and
    /// every Tier-2 command it sends is silently dropped. The seat never goes empty during
    /// the handover, so no failsafe fires; the old holder's client sees the control-state
    /// frame and drops to Observer.
    /// </summary>
    public void Takeover(Guid conn, string name)
    {
        ControlStateDto snap;
        lock (_lock)
        {
            _holder = conn;
            _holderName = string.IsNullOrWhiteSpace(name) ? "Remote" : name.Trim();
            _lastPresenceTicks = Environment.TickCount64;
            snap = new ControlStateDto(true, _holder.ToString()!, _holderName);
        }
        Changed?.Invoke(snap);
    }

    /// <summary>Heartbeat — keeps a hold alive (deadman reset). No-op for non-holders.</summary>
    public void Refresh(Guid conn)
    {
        lock (_lock) { if (_holder == conn) _lastPresenceTicks = Environment.TickCount64; }
    }

    /// <summary>Implicit presence: any traffic from the holder proves liveness.</summary>
    public void RefreshIfHolder(Guid conn) => Refresh(conn);

    /// <summary>Voluntary release. No failsafe (the holder chose to let go).</summary>
    public void Release(Guid conn)
    {
        if (ClearIfHolder(conn)) Changed?.Invoke(Snapshot());
    }

    /// <summary>Socket dropped. If it held control, fire the failsafe.</summary>
    public void Drop(Guid conn)
    {
        if (ClearIfHolder(conn))
        {
            Revoked?.Invoke("client disconnected");
            Changed?.Invoke(Snapshot());
        }
    }

    /// <summary>Called periodically by the broadcaster. Revokes a stale holder
    /// (deadman) and fires the failsafe. Returns true if it revoked one.</summary>
    public bool SweepStale()
    {
        bool revoked;
        lock (_lock)
        {
            revoked = _holder.HasValue && (Environment.TickCount64 - _lastPresenceTicks) > DeadmanMs;
            if (revoked) { _holder = null; _holderName = ""; }
        }
        if (revoked)
        {
            Revoked?.Invoke("presence lost (deadman)");
            Changed?.Invoke(Snapshot());
        }
        return revoked;
    }

    private bool ClearIfHolder(Guid conn)
    {
        lock (_lock)
        {
            if (_holder != conn) return false;
            _holder = null; _holderName = "";
            return true;
        }
    }
}
