// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// Licensed under GNU GPL v3. See LICENSE.md.
//
// Native rate control — Phase 1 of the AOG_RC (RateController) integration.
// Speaks the RC module plane directly (listen :29999, send broadcast :28888),
// so existing RC hardware (RCnano / RC teensy / ESP32 boards) works unchanged
// with no WinForms RateController app in the loop. Speed and section state come
// from this app's own pipeline — no AgIO PGN hop. Domain math ported from
// SK21/AOG_RC (GPL v3): see RcPgn.cs for the wire contract and RateProduct for
// the per-product model.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.RateControl;

public interface IRateControlService
{
    IReadOnlyList<RateProduct> Products { get; }
    bool PlaneActive { get; }
    void Start();
    void Stop();
    void SetProductValue(int index, string key, string value);
    void ResetQuantity(int index);
    void ResetArea(int index);
    void CalibrationStart(int index);
    void CalibrationStop(int index);
    void CalibrationApply(int index, double actualUnits);
    /// <summary>Snapshot every product's quantity as the job baseline (called
    /// when a job becomes active).</summary>
    void MarkJobStart();
    /// <summary>Measured product applied since the job baseline: the enabled,
    /// module-measured product with the largest delta, or null when nothing
    /// meaningful flowed. Feeds the job record's applied amount.</summary>
    (string Product, double Amount, string Unit)? GetMeasuredJobApplied();
    string BuildStatusJson();

    // Product catalogue: what products exist, shared across tools and jobs. The
    // channel keeps the machine's own settings (meter cal above all).
    void CatalogAddOrUpdate(string name, string units, double defaultRate);
    void CatalogRemove(string name);
    void AssignProduct(int index, string name);

    // Module setup — settings the module keeps in EEPROM (PGN 32700/32507/32502).
    // Sent only on demand: they make the module save, and pin changes restart it.
    string BuildModuleSetupJson();
    void SetModuleSetupValue(int moduleId, int sensorId, string key, string value);
    void PushControlSettings(int moduleId, int sensorId);
    void PushSensorPins(int moduleId, int sensorId);
    /// <summary>assignId is commissioning only — EVERY listening module adopts the
    /// id, so the caller must have confirmed a single connected board.</summary>
    void PushModuleConfig(int moduleId, bool assignId = false);
    void PushAll(int moduleId);
    /// <summary>Stage a board type's factory defaults locally for review before
    /// sending. Board is "esp32" (RC15), "teensy" (RC11-2) or "nano" (RC12-3).</summary>
    void LoadModuleDefaults(int moduleId, string board = "esp32");
    /// <summary>Create a setup entry for a module id that does not have one yet.</summary>
    void AddModule(int moduleId);
    /// <summary>Put every relay back to driving its own section.</summary>
    void ResetRelays(int moduleId);
    /// <summary>Renumber the section-driving relays consecutively from a start.</summary>
    void RenumberRelays(int moduleId, int startSection);

    // Virtual switchbox — stands in for the physical AOG_RC box (PGN 32618).
    bool MasterOn { get; }
    void SetMaster(bool on);
    /// <summary>A physical switchbox's auto-section toggle moved (value = its
    /// new position). Raised on the RC receive thread — the wiring marshals it
    /// onto the section auto-master command.</summary>
    event Action<bool>? PhysicalAutoSectionChanged;
    void BumpRate(int index, double percent);
    void ResetRate(int index);
    /// <summary>Behaviour of the switches (master mode, auto rate, section
    /// allocation) — the native Machine &gt; Switches page, per tool.</summary>
    RcSwitchboxSettings Switchbox { get; }
    RcPrimedSettings Primed { get; }
    void SetSwitchboxValue(string key, string value);
    /// <summary>Primed start: run the enabled channels at a simulated speed
    /// while parked, for the configured time. Native Machine &gt; Primed Start.</summary>
    void StartPrimed();
    void CancelPrimed();
    bool PrimedActive { get; }
    /// <summary>Move the module's wired subnet; it reboots onto the new one.</summary>
    void PushSubnet(int moduleId, byte ip0, byte ip1, byte ip2);
}

public sealed class RateControlService : IRateControlService, IDisposable
{
    private const int ProductCount = 5;      // AOG_RC's fixed product slots
    private const int SendIntervalMs = 250;  // 4 Hz settings/target push (RC parity)

    private readonly ApplicationState _state;
    private readonly IAutoSteerService? _autoSteer;
    private readonly ConfigurationStore _configStore;
    private readonly ISectionControlService _sections;
    private readonly ISettingsService _settings;
    private readonly List<RateProduct> _products = new();
    private readonly object _ioLock = new();

    private Socket? _socket;
    private Timer? _sendTimer;
    private Thread? _rxThread;
    private volatile bool _running;
    private DateTime _lastTickUtc = DateTime.UtcNow;
    private DateTime _lastSaveUtc = DateTime.UtcNow;
    private bool _dirty;
    private double _lastActiveHaPerMin;
    private double _lastTotalHaPerMin;
    private List<IPEndPoint> _broadcastEndpoints = new();
    private DateTime _lastEndpointRefresh = DateTime.MinValue;
    /// <summary>Learned module addresses (moduleId → last frame source, when
    /// heard). The RC boards' W5500 drops directed broadcasts, so once a module
    /// is heard we also unicast to it directly — proven on the bench: the
    /// broadcast path alone never delivered a single config frame.
    ///
    /// Entries EXPIRE from the
    /// heard list after <see cref="ModuleHeardWindowSeconds"/>: a module that was
    /// re-branded to a new id leaves its old id behind here, and that ghost both
    /// misled the "no modules heard" diagnosis and tripped the assign-ID guard's
    /// "more than one module answering" refusal with only one physical board.
    /// The address itself is still used for unicast while stale — sending to a
    /// gone address is harmless, and it bridges brief gaps.</summary>
    private readonly Dictionary<int, (IPAddress Address, DateTime LastHeardUtc)> _moduleAddresses = new();
    private const int ModuleHeardWindowSeconds = 10;

    // Switch behaviour + primed start, per tool (Machine > Switches / Primed Start).
    private RcSwitchboxSettings _switchbox = new();
    private RcPrimedSettings _primed = new();
    private string? _switchboxToolKey;
    private DateTime _primedUntilUtc = DateTime.MinValue;

    // Physical switchbox (PGN 32618) — same step the on-screen box's ± buttons send.
    private readonly RcPhysicalSwitchbox _physBox = new();
    private int _lastSwitchboxTraceBits = -1;
    private ushort _lastSwitchboxTraceSections;
    private const double SwitchboxRateStepPercent = 5;

    /// <summary><paramref name="autoSteer"/> is optional so tests and headless
    /// hosts can build the service without the steering stack; without it the
    /// tram / lift / geo-stop relay types simply stay off.</summary>
    public RateControlService(ApplicationState state, ConfigurationStore configStore,
        ISectionControlService sections, ISettingsService settings,
        IAutoSteerService? autoSteer = null)
    {
        _state = state;
        _configStore = configStore;
        _sections = sections;
        _settings = settings;
        _autoSteer = autoSteer;
    }

    /// <summary>Channels for the ACTIVE tool (reloads when the tool changes).</summary>
    public IReadOnlyList<RateProduct> Products { get { EnsureToolChannels(); return _products; } }

    public bool PlaneActive { get; private set; }

    private string RateDir
    {
        get
        {
            string root = Path.GetDirectoryName(_settings.Settings.FieldsDirectory)
                ?? _settings.Settings.FieldsDirectory;
            return Path.Combine(root, "RateController");
        }
    }

    /// <summary>Pre-split single global product list. Kept only to migrate from.</summary>
    private string LegacyProductsFile => Path.Combine(RateDir, "products.json");

    /// <summary>The product catalogue — what products exist, shared by all tools.</summary>
    private string CatalogFile => Path.Combine(RateDir, "products-catalog.json");

    /// <summary>Channels belong to the TOOL: the meter calibration is a property of
    /// that implement's flow meter, and must not follow a product to another
    /// machine. A drill and a spreader both running DAP keep separate cals.
    ///
    /// Keyed on the tool the IN-MEMORY channels belong to, NOT the active one:
    /// on a tool switch the config store flips first, so keying this on the live
    /// tool wrote the outgoing implement's channels into the incoming
    /// implement's file and destroyed its calibration.</summary>
    private string ProductsFile => ProductsFileFor(
        string.IsNullOrEmpty(_productsToolKey) ? ToolKey : _productsToolKey);

    private string ProductsFileFor(string toolKey) => Path.Combine(RateDir, $"channels-{toolKey}.json");

    private string ToolKey
    {
        get
        {
            string tool = _configStore.ActiveToolProfileName;
            if (string.IsNullOrWhiteSpace(tool)) tool = "default";
            foreach (char c in Path.GetInvalidFileNameChars()) tool = tool.Replace(c, '_');
            return tool;
        }
    }

    /// <summary>Module setup is scoped to the TOOL: the module is bolted to that
    /// implement, and its calibration must never leak to another one. Falls back
    /// to a shared file when no tool profile is active.</summary>
    private string ModuleSetupFile
    {
        get
        {
            string root = Path.GetDirectoryName(_settings.Settings.FieldsDirectory)
                ?? _settings.Settings.FieldsDirectory;
            string tool = _configStore.ActiveToolProfileName;
            if (string.IsNullOrWhiteSpace(tool)) tool = "default";
            foreach (char c in Path.GetInvalidFileNameChars()) tool = tool.Replace(c, '_');
            return Path.Combine(root, "RateController", $"modules-{tool}.json");
        }
    }

    public void Start()
    {
        if (_running) return;
        LoadProducts();

        try
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            // ReuseAddress: lets the WinForms RC app run in parallel during the
            // transition (broadcast module frames reach both listeners).
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            if (OperatingSystem.IsWindows())
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                _socket.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0 }, null);
            }
            _socket.Bind(new IPEndPoint(IPAddress.Any, RcPgn.HostListenPort));
            PlaneActive = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Rate] module plane unavailable: {ex.Message}");
            _socket?.Dispose();
            _socket = null;
            PlaneActive = false;
        }

        _running = true;
        if (_socket != null)
        {
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "RateControlRx" };
            _rxThread.Start();
            _sendTimer = new Timer(_ => SendTick(), null, SendIntervalMs, SendIntervalMs);
        }
    }

    public void Stop()
    {
        _running = false;
        _sendTimer?.Dispose();
        _sendTimer = null;
        _socket?.Close();
        _socket?.Dispose();
        _socket = null;
        PlaneActive = false;
        SaveProducts();
    }

    public void Dispose() => Stop();

    // ---- inbound: module frames -> product live state ----

    private void ReceiveLoop()
    {
        var buf = new byte[512];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (_running && _socket is { } sock)
        {
            try
            {
                int n = sock.ReceiveFrom(buf, ref from);
                if (n < 3) continue;
                int pgn = buf[1] << 8 | buf[0];
                if (pgn == RcPgn.PGN_SENSOR && RcPgn.TryParseSensor(buf.AsSpan(0, n), out var sf))
                {
                    if (from is IPEndPoint ip)
                        lock (_ioLock) _moduleAddresses[sf.ModuleId] = (ip.Address, DateTime.UtcNow);
                    foreach (var p in _products)
                        if (p.Enabled && p.ModuleId == sf.ModuleId && p.SensorId == sf.SensorId)
                        {
                            lock (_ioLock) p.ApplySensorFrame(sf);
                            _dirty = true;
                        }
                }
                else if (pgn == RcPgn.PGN_MODULE_STATUS && RcPgn.TryParseModuleStatus(buf.AsSpan(0, n), out var mf))
                {
                    if (from is IPEndPoint ip2)
                        lock (_ioLock) _moduleAddresses[mf.ModuleId] = (ip2.Address, DateTime.UtcNow);
                }
                else if (pgn == RcPgn.PGN_SWITCHBOX && RcPgn.TryParseSwitchbox(buf.AsSpan(0, n), out var swf))
                {
                    OnSwitchboxFrame(swf);
                }
                // 32401 module status: liveness rides the sensor frames; nothing
                // further needed for Phase 1 (pressure/wifi diagnostics later).
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (!_running) break; }
            catch { }
        }
    }

    public event Action<bool>? PhysicalAutoSectionChanged;

    /// <summary>A physical AOG_RC switchbox (PGN 32618) drives the SAME paths
    /// the on-screen box does: master on/off, ±5% rate nudge on every enabled
    /// channel, the auto-rate gate, auto section (via the wiring event), the
    /// work switch and the 16 section switches. The buttons are momentary, so
    /// only 0→1 edges act — a held or stuck button does its thing once.</summary>
    private void OnSwitchboxFrame(in RcSwitchboxFrame f)
    {
        // Raw-bit trace on change: which status bits this box actually sets —
        // settles wiring-vs-parse questions when a box doesn't match the
        // standard layout. Logs only on transitions, so steady frames are quiet.
        int stBits = (f.MasterOnPressed ? 2 : 0) | (f.MasterOffPressed ? 4 : 0)
                   | (f.RateUpPressed ? 8 : 0) | (f.RateDownPressed ? 16 : 0)
                   | (f.AutoSectionOn ? 32 : 0) | (f.AutoRateOn ? 64 : 0)
                   | (f.WorkSwitchOn ? 128 : 0);
        if (stBits != _lastSwitchboxTraceBits || f.SectionBits != _lastSwitchboxTraceSections)
        {
            Console.WriteLine($"[Rate] switchbox bits: st=0b{Convert.ToString(stBits, 2).PadLeft(8, '0')} sections=0x{f.SectionBits:X4}");
            _lastSwitchboxTraceBits = stBits;
            _lastSwitchboxTraceSections = f.SectionBits;
        }

        EnsureToolChannels();
        EnsureSwitchboxLoaded();
        // First frame from a box on this tool marks it EXPECTED — from then on
        // its absence alarms even straight after power-on. Cleared in Network IO
        // if the box is removed for good.
        if (!_switchbox.ExpectPhysical)
        {
            _switchbox.ExpectPhysical = true;
            SaveSwitchbox();
        }
        RcSwitchboxActions act;
        bool parked = Math.Abs(_state.Vehicle.Speed) * 3.6 <= 0.5;
        lock (_ioLock) act = _physBox.Apply(f, DateTime.UtcNow, _switchbox.SwitchType == 1,
            _primed.MasterDelayS, parked);

        if (act.SetMaster is bool m) SetMaster(m);
        if (act.PrimeHoldEdge) StartPrimed();   // hold-to-prime from the physical box
        if (act.RateUpEdge || act.RateDownEdge)
        {
            // RateUp wins a simultaneous press, like the reference parser.
            double pct = act.RateUpEdge ? SwitchboxRateStepPercent : -SwitchboxRateStepPercent;
            for (int i = 0; i < _products.Count; i++)
                if (_products[i].Enabled) BumpRate(i, pct);
        }
        if (act.SetAutoRate is bool ar && ar != _switchbox.AutoRate)
        {
            _switchbox.AutoRate = ar;
            SaveSwitchbox();
        }
        if (act.SetAutoSection is bool asw) PhysicalAutoSectionChanged?.Invoke(asw);
    }

    // ---- outbound: 4 Hz settings/target to each enabled product's module ----

    private void SendTick()
    {
        if (!_running || _socket == null) return;
        try
        {
            // Notice a tool change here too: otherwise, after hitching a different
            // implement, we would keep pushing the previous one's calibration and
            // targets to the module until something touched the UI.
            EnsureToolChannels();
            EnsureSwitchboxLoaded();
            var (activeHaPerMin, totalHaPerMin) = HectaresPerMinute();
            // Primed start: the machine is parked, so the real coverage rate is
            // zero — substitute the configured priming speed so the channels get
            // a real target. Only the sections the operator switched on count,
            // which HectaresPerMinute already handles via the section states.
            if (PrimedActive)
            {
                double primedSpeed = _primed.SpeedKmh;
                var (aw, tw) = ActiveWidths();
                activeHaPerMin = aw * primedSpeed / 600.0;
                totalHaPerMin = tw * primedSpeed / 600.0;
            }
            bool master = EffectiveMaster();
            // Kept for the status JSON: the readout needs the same coverage rate
            // the targets were computed from, not one sampled at request time.
            _lastActiveHaPerMin = activeHaPerMin;
            _lastTotalHaPerMin = totalHaPerMin;

            // Area accrues with worked ground regardless of module presence
            // (like AOG_RC, coverage is app-side; quantity is module-measured).
            var now = DateTime.UtcNow;
            double dtMin = Math.Clamp((now - _lastTickUtc).TotalMinutes, 0, 0.1);
            _lastTickUtc = now;

            foreach (var p in _products)
            {
                if (!p.Enabled) continue;

                if (activeHaPerMin > 0)
                {
                    lock (_ioLock) p.AreaApplied += activeHaPerMin * dtMin;
                    _dirty = true;
                }

                double target = p.TargetUpm(activeHaPerMin, totalHaPerMin);
                if (target > 0 && p.MinUpm > 0 && target < p.MinUpm) target = p.MinUpm;

                // Manual / un-metered product (no module reporting flow): there is no
                // sensor to count what actually went out, so ESTIMATE it from the
                // commanded rate — target (units/min) × dt = units this tick (which is
                // rate × area). This decrements the tank so a manual spreader/sprayer
                // still shows an estimated remaining (surfaced as "~ … est" in the UI).
                // A connected module owns the real count, so we never estimate then.
                if (!p.ModuleConnected && target > 0 && dtMin > 0)
                {
                    lock (_ioLock)
                    {
                        double used = target * dtMin;
                        p.QuantityApplied += used;
                        if (p.TankRemaining > 0) p.TankRemaining = Math.Max(0, p.TankRemaining - used);
                    }
                    _dirty = true;
                }

                bool reset;
                lock (_ioLock) { reset = p.ResetQuantityPending; p.ResetQuantityPending = false; }

                // No switchbox in Phase 1: master follows this app's section master
                // implicitly through the UPM (sections off -> target 0); the module
                // additionally wants MasterOn asserted (RC does the same when no
                // switchbox is connected).
                var frame = RcPgn.BuildRateSettings(
                    p.ModuleId, p.SensorId, target, p.MeterCal, p.ControlType,
                    masterOn: master, autoOn: p.AutoOn && _switchbox.AutoRate, resetQuantity: reset,
                    manualPwm: p.ManualPwm, productEnabled: p.Enabled);

                foreach (var ep in BroadcastEndpoints())
                    try { _socket.SendTo(frame, ep); } catch { }
                // Learned module address → also unicast (W5500 broadcast blocking).
                IPAddress? modAddr;
                lock (_ioLock) modAddr = _moduleAddresses.TryGetValue(p.ModuleId, out var e1) ? e1.Address : null;
                if (modAddr != null)
                    try { _socket.SendTo(frame, new IPEndPoint(modAddr, RcPgn.ModuleListenPort)); } catch { }
            }

            // Relay/section states (32501) once per distinct enabled module — the
            // firmware's auto-PID gate requires nonzero relay bits, and modules
            // with relay outputs switch their sections from this.
            ushort secBits = _sections.GetSectionBits();
            // A connected physical switchbox puts its 16 maintained section
            // switches in SERIES with the app's section state: a section only
            // runs while its allocated switch is up. The switch can veto, never
            // force on — coverage/auto stays authoritative.
            if (_physBox.Connected(now))
                secBits &= RcPhysicalSwitchbox.SectionGateMask(_switchbox, _physBox.SectionBits);
            var sentModules = new HashSet<int>();
            foreach (var p in _products)
            {
                if (!p.Enabled || !sentModules.Add(p.ModuleId)) continue;
                // Master mode ControlAll: the master switch kills the section
                // relays too. MasterRelayOnly leaves sections to their own state.
                ushort effBits = (_switchbox.MasterMode == RcMasterMode.ControlAll && !master)
                    ? (ushort)0 : secBits;
                var w = ComputeRelayWords(p.ModuleId, effBits, master);
                var relayFrame = RcPgn.BuildRelaySettings(p.ModuleId,
                    (byte)(w.Relays & 0xFF), (byte)(w.Relays >> 8),
                    (byte)(w.Power & 0xFF), (byte)(w.Power >> 8),
                    (byte)(w.Inverted & 0xFF), (byte)(w.Inverted >> 8),
                    w.FlowMasterIndex);
                foreach (var ep in BroadcastEndpoints())
                    try { _socket.SendTo(relayFrame, ep); } catch { }
                IPAddress? ma;
                lock (_ioLock) ma = _moduleAddresses.TryGetValue(p.ModuleId, out var e2) ? e2.Address : null;
                if (ma != null)
                    try { _socket.SendTo(relayFrame, new IPEndPoint(ma, RcPgn.ModuleListenPort)); } catch { }
            }

            if (_dirty && (now - _lastSaveUtc).TotalSeconds > 10)
            {
                _dirty = false;
                _lastSaveUtc = now;
                SaveProducts();
            }
        }
        catch { /* keep the timer alive */ }
    }

    /// <summary>Per-interface directed broadcasts (x.x.x.255:28888) — a global
    /// 255.255.255.255 broadcast leaves only the default-route NIC on Windows,
    /// which is usually NOT the implement network. Mirrors the AOG plane's
    /// discovery behaviour. Refreshed every 30 s.</summary>
    private List<IPEndPoint> BroadcastEndpoints()
    {
        if ((DateTime.UtcNow - _lastEndpointRefresh).TotalSeconds < 30 && _broadcastEndpoints.Count > 0)
            return _broadcastEndpoints;
        var eps = new List<IPEndPoint>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask?.GetAddressBytes();
                    if (mask == null) continue;
                    var bc = new byte[4];
                    for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                    eps.Add(new IPEndPoint(new IPAddress(bc), RcPgn.ModuleListenPort));
                }
            }
        }
        catch { }
        if (eps.Count == 0) eps.Add(new IPEndPoint(IPAddress.Broadcast, RcPgn.ModuleListenPort));
        _broadcastEndpoints = eps;
        _lastEndpointRefresh = DateTime.UtcNow;
        return eps;
    }

    /// <summary>What this module's relay outputs should be doing right now.
    ///
    /// Relay N used to be assumed to be section N; that is still the default, so
    /// a setup saved before relay functions existed behaves exactly as it did.
    /// Anything else — a master shutoff, a bypass valve, tram markers — now comes
    /// from the module's own relay assignments.</summary>
    private RcRelayWords ComputeRelayWords(int moduleId, ushort sectionBits, bool? masterOverride = null)
    {
        var m = GetOrAddModuleSetup(moduleId);
        // Tram, lift and geo-stop are the steering side's view of the machine —
        // the same values it puts in PGN 239 — so they come from its snapshot
        // rather than being recomputed here.
        var snap = _autoSteer?.LatestSnapshot;
        byte tram = snap?.TramState ?? 0;
        // The lift command is momentary in AOG (1 = raise, 2 = lower); the relay
        // follows it and the machine module keeps its own raise/lower timing.
        byte hyd = _configStore.Machine.HydraulicLiftEnabled ? (snap?.HydLiftState ?? 0) : (byte)0;
        return RcRelayMap.Compute(m.EnsureRelays(), new RcRelayInputs
        {
            MasterOn = masterOverride ?? EffectiveMaster(),
            AutoSectionOn = _state.Operation.IsSectionAutoMaster,
            // A primed run happens parked; the flow master must still open.
            Moving = PrimedActive || Math.Abs(_state.Vehicle.Speed) * 3.6 > 0.1,
            Calibrating = _products.Exists(p => p.CalActive),
            SectionBits = sectionBits,
            // Switch-type relays follow the physical box's real switches when
            // one is talking; the virtual switchbox has none of its own.
            SwitchBits = _physBox.Connected(DateTime.UtcNow) ? _physBox.SectionBits : sectionBits,
            TramRight = (tram & 1) != 0,
            TramLeft = (tram & 2) != 0,
            GeoStop = (snap?.GeoStopState ?? 0) != 0,
            HydUp = hyd == 1,
            HydDown = hyd == 2,
        }, m.FlowMasterMode);
    }

    /// <summary>Active and total implement width in metres, from the section states.</summary>
    private (double Active, double Total) ActiveWidths()
    {
        double activeW = 0, totalW = 0;
        var states = _sections.SectionStates;
        for (int i = 0; i < states.Count; i++)
        {
            double w = Math.Abs(states[i].PositionRight - states[i].PositionLeft);
            totalW += w;
            if (states[i].IsOn) activeW += w;
        }
        if (totalW <= 0) { totalW = _configStore.ActualToolWidth; activeW = 0; }
        return (activeW, totalW);
    }

    /// <summary>Worked area rate: (active width × speed) and (total width × speed),
    /// in hectares/minute — ha/min = width_m × speed_kmh / 600 (AOG_RC formula).</summary>
    private (double Active, double Total) HectaresPerMinute()
    {
        double speedKmh = Math.Abs(_state.Vehicle.Speed) * 3.6;
        double activeW = 0, totalW = 0;
        var states = _sections.SectionStates;
        for (int i = 0; i < states.Count; i++)
        {
            double w = Math.Abs(states[i].PositionRight - states[i].PositionLeft);
            totalW += w;
            if (states[i].IsOn) activeW += w;
        }
        if (totalW <= 0)
        {
            totalW = _configStore.ActualToolWidth;
            activeW = 0;
        }
        return (activeW * speedKmh / 600.0, totalW * speedKmh / 600.0);
    }

    // ---- commands (from the web panel) ----

    public void SetProductValue(int index, string key, string value)
    {
        EnsureToolChannels();
        if (index < 0 || index >= _products.Count) return;
        var p = _products[index];
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        bool B() => value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        double D() => double.TryParse(value, System.Globalization.NumberStyles.Float, inv, out var d) ? d : 0;

        lock (_ioLock)
        {
            switch (key)
            {
                case "name": p.Name = value.Trim(); break;
                case "enabled": p.Enabled = B(); break;
                case "moduleId": p.ModuleId = Math.Clamp((int)D(), 0, 15); break;
                case "sensorId": p.SensorId = Math.Clamp((int)D(), 0, 15); break;
                case "targetRate": p.TargetRate = Math.Max(0, D()); break;
                case "coverageUnits": p.CoverageUnits = Math.Clamp((int)D(), 0, 3); break;
                case "meterCal": p.MeterCal = Math.Max(0, D()); break;
                case "controlType": p.ControlType = (RcControlType)Math.Clamp((int)D(), 0, 5); break;
                case "constantUpm": p.ConstantUpm = B(); break;
                case "minUpm": p.MinUpm = Math.Max(0, D()); break;
                case "tankSize": p.TankSize = Math.Max(0, D()); break;
                case "tankRemaining": p.TankRemaining = Math.Max(0, D()); break;
                // Top-up: ADD to what's in the tank (partial refill or full), rather
                // than overwrite. Clamped to the tank's capacity when one is set.
                case "tankAdd":
                    p.TankRemaining = Math.Max(0, p.TankRemaining + D());
                    if (p.TankSize > 0) p.TankRemaining = Math.Min(p.TankRemaining, p.TankSize);
                    break;
                case "auto": p.AutoOn = B(); break;
                case "manualPwm": p.ManualPwm = Math.Clamp((int)D(), -255, 255); break;
                case "units": p.Units = value.Trim(); break;
            }
            _dirty = true;
        }
        SaveProducts();
    }

    public void ResetQuantity(int index)
    {
        EnsureToolChannels();
        if (index < 0 || index >= _products.Count) return;
        lock (_ioLock)
        {
            _products[index].QuantityApplied = 0;
            _products[index].ResetQuantityPending = true; // also zero the module counter
            _dirty = true;
        }
    }

    public void ResetArea(int index)
    {
        EnsureToolChannels();
        if (index < 0 || index >= _products.Count) return;
        lock (_ioLock) { _products[index].AreaApplied = 0; _dirty = true; }
    }

    // ---- job-record integration ----

    public void MarkJobStart()
    {
        EnsureToolChannels();
        lock (_ioLock)
            foreach (var p in _products) p.JobStartQuantity = p.QuantityApplied;
    }

    public (string Product, double Amount, string Unit)? GetMeasuredJobApplied()
    {
        EnsureToolChannels();
        lock (_ioLock)
        {
            RateProduct? best = null;
            double bestDelta = 0.05; // ignore meter noise
            foreach (var p in _products)
            {
                if (!p.Enabled) continue;
                double delta = p.QuantityApplied - p.JobStartQuantity;
                if (delta > bestDelta) { bestDelta = delta; best = p; }
            }
            return best == null ? null
                : (string.IsNullOrWhiteSpace(best.Name) ? "Product" : best.Name,
                   Math.Round(bestDelta, 1), best.Units);
        }
    }

    // ---- calibration (AOG_RC catch-test method) ----
    // Run the product manually at the panel's PWM into a catch container, then
    // enter the ACTUAL amount caught; the meter cal rescales so indicated
    // matches actual: newCal = oldCal × indicated/actual (pulses are invariant).

    public void CalibrationStart(int index)
    {
        EnsureToolChannels();
        if (index < 0 || index >= _products.Count) return;
        var p = _products[index];
        lock (_ioLock)
        {
            p.CalPriorAuto = p.AutoOn;
            p.AutoOn = false;            // manual drive at ManualPwm
            p.CalStartQuantity = p.QuantityApplied;
            p.CalActive = true;
            p.CalStopped = false;
        }
    }

    public void CalibrationStop(int index)
    {
        EnsureToolChannels();
        if (index < 0 || index >= _products.Count) return;
        var p = _products[index];
        lock (_ioLock)
        {
            if (!p.CalActive) return;
            p.CalActive = false;
            p.CalStopped = true;         // freeze the indicated reading for entry
            p.ManualPwm = 0;             // stop the flow
        }
    }

    public void CalibrationApply(int index, double actualUnits)
    {
        EnsureToolChannels();
        if (index < 0 || index >= _products.Count) return;
        var p = _products[index];
        lock (_ioLock)
        {
            double indicated = p.CalIndicated;
            if (p.CalStopped && indicated > 0.01 && actualUnits > 0.01)
            {
                p.MeterCal = p.MeterCal * indicated / actualUnits;
                // the catch test wasn't field application — take it back out
                p.QuantityApplied = p.CalStartQuantity;
            }
            p.CalStopped = false;
            p.AutoOn = p.CalPriorAuto;
            _dirty = true;
        }
        SaveProducts();
    }

    // ---- status for the web panel ----

    public string BuildStatusJson()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder(1024);
        EnsureToolChannels();
        sb.Append("{\"plane\":").Append(PlaneActive ? "true" : "false");
        sb.Append(",\"tool\":").Append(JsonSerializer.Serialize(_configStore.ActiveToolProfileName ?? ""));
        sb.Append(",\"useRateControl\":").Append(_configStore.Tool.UseRateControl ? "true" : "false");
        sb.Append(",\"masterOn\":").Append(MasterOn ? "true" : "false");
        EnsureSwitchboxLoaded();
        sb.Append(",\"effectiveMaster\":").Append(EffectiveMaster() ? "true" : "false");
        sb.Append(",\"switchbox\":{\"masterMode\":").Append((int)_switchbox.MasterMode)
          .Append(",\"switchType\":").Append(_switchbox.SwitchType)
          .Append(",\"onScreen\":").Append(_switchbox.OnScreenEnabled ? "true" : "false")
          .Append(",\"autoRate\":").Append(_switchbox.AutoRate ? "true" : "false")
          .Append(",\"workGate\":").Append(_switchbox.WorkSwitchGate ? "true" : "false")
          .Append(",\"workSwitchOn\":").Append(WorkSwitchOn() ? "true" : "false")
          // Physical PGN 32618 box: connected + its maintained switch levels.
          .Append(",\"physical\":{\"expected\":")
          .Append(_switchbox.ExpectPhysical ? "true" : "false")
          .Append(",\"connected\":")
          .Append(_physBox.Connected(DateTime.UtcNow) ? "true" : "false")
          .Append(",\"work\":").Append(_physBox.WorkSwitchOn ? "true" : "false")
          .Append(",\"autoSection\":").Append(_physBox.AutoSectionOn ? "true" : "false")
          .Append(",\"autoRate\":").Append(_physBox.AutoRateOn ? "true" : "false")
          .Append(",\"sections\":").Append(_physBox.SectionBits)
          .Append(",\"inoId\":").Append(_physBox.InoId)
          .Append('}')
          .Append(",\"sectionSwitch\":[");
        // The section service keeps its full slot table; the tool knows how
        // many sections actually exist on the implement.
        int nSec = Math.Min(_sections.SectionStates.Count,
            Math.Max(1, _configStore.NumSections));
        for (int i = 0; i < nSec; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(_switchbox.SwitchFor(i));
        }
        sb.Append("]}");
        double primedLeft = (_primedUntilUtc - DateTime.UtcNow).TotalSeconds;
        sb.Append(",\"primed\":{\"onTime\":").Append(_primed.OnTimeS.ToString("0.#", CultureInfo.InvariantCulture))
          .Append(",\"speed\":").Append(_primed.SpeedKmh.ToString("0.#", CultureInfo.InvariantCulture))
          .Append(",\"delay\":").Append(_primed.MasterDelayS.ToString("0.#", CultureInfo.InvariantCulture))
          .Append(",\"resume\":").Append(_primed.Resume ? "true" : "false")
          .Append(",\"active\":").Append(PrimedActive ? "true" : "false")
          .Append(",\"remaining\":").Append((primedLeft > 0 ? primedLeft : 0).ToString("0", CultureInfo.InvariantCulture))
          .Append('}');
        // Live heard-list for the client-side drop-off alarm — rides the same
        // 1 Hz status poll the rate HUD already makes.
        {
            var heardCutoff = DateTime.UtcNow.AddSeconds(-ModuleHeardWindowSeconds);
            var heardIds = new List<int>();
            lock (_ioLock)
                foreach (var kv in _moduleAddresses)
                    if (kv.Value.LastHeardUtc >= heardCutoff) heardIds.Add(kv.Key);
            heardIds.Sort();
            sb.Append(",\"modulesHeard\":[").Append(string.Join(",", heardIds)).Append(']');
        }
        EnsureModuleSetupsLoaded();
        sb.Append(",\"moduleSensorCounts\":{");
        lock (_ioLock)
        {
            for (int mi = 0; mi < _moduleSetups.Count; mi++)
            {
                if (mi > 0) sb.Append(',');
                sb.Append('"').Append(_moduleSetups[mi].ModuleId).Append("\":")
                  .Append(Math.Max(1, (int)_moduleSetups[mi].Config.SensorCount));
            }
        }
        sb.Append('}');
        sb.Append(",\"catalog\":[");
        for (int i = 0; i < _catalog.Items.Count; i++)
        {
            var it = _catalog.Items[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":").Append(JsonSerializer.Serialize(it.Name))
              .Append(",\"units\":").Append(JsonSerializer.Serialize(it.Units))
              .Append(",\"defaultRate\":").Append(it.DefaultRate.ToString(CultureInfo.InvariantCulture))
              .Append('}');
        }
        sb.Append(']');
        sb.Append(",\"products\":[");
        lock (_ioLock)
        {
            for (int i = 0; i < _products.Count; i++)
            {
                var p = _products[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(JsonSerializer.Serialize(p.Name))
                  .Append(",\"enabled\":").Append(p.Enabled ? "true" : "false")
                  .Append(",\"moduleId\":").Append(p.ModuleId)
                  .Append(",\"sensorId\":").Append(p.SensorId)
                  .Append(",\"targetRate\":").Append(p.TargetRate.ToString("0.##", inv))
                  .Append(",\"coverageUnits\":").Append(p.CoverageUnits)
                  .Append(",\"meterCal\":").Append(p.MeterCal.ToString("0.###", inv))
                  .Append(",\"controlType\":").Append((int)p.ControlType)
                  .Append(",\"constantUpm\":").Append(p.ConstantUpm ? "true" : "false")
                  .Append(",\"auto\":").Append(p.AutoOn ? "true" : "false")
                  .Append(",\"manualPwm\":").Append(p.ManualPwm)
                  .Append(",\"connected\":").Append(p.ModuleConnected ? "true" : "false")
                  .Append(",\"upm\":").Append(p.MeasuredUpm.ToString("0.##", inv))
                  // Measured flow expressed in the target's own units, so the
                  // on-map readout can put actual beside target directly.
                  .Append(",\"actualRate\":").Append(p.ActualRate(_lastActiveHaPerMin, _lastTotalHaPerMin).ToString("0.##", inv))
                  .Append(",\"pwm\":").Append(p.ModulePwm)
                  .Append(",\"hz\":").Append(p.Hz.ToString("0.#", inv))
                  .Append(",\"binEmpty\":").Append(p.BinEmpty ? "true" : "false")
                  .Append(",\"qty\":").Append(p.QuantityApplied.ToString("0.#", inv))
                  .Append(",\"area\":").Append(p.AreaApplied.ToString("0.##", inv))
                  .Append(",\"tankSize\":").Append(p.TankSize.ToString("0.#", inv))
                  .Append(",\"tank\":").Append(p.TankRemaining.ToString("0.#", inv))
                  .Append(",\"units\":").Append(JsonSerializer.Serialize(p.Units))
                  .Append(",\"calActive\":").Append(p.CalActive ? "true" : "false")
                  .Append(",\"calStopped\":").Append(p.CalStopped ? "true" : "false")
                  .Append(",\"calIndicated\":").Append(p.CalIndicated.ToString("0.##", inv))
                  .Append('}');
            }
        }
        sb.Append("]}");
        return sb.ToString();
    }

    // ---- persistence ----

    // ── Module setup (PGN 32700 / 32507 / 32502) ────────────────────────────
    // Settings the module keeps in EEPROM. Sent only when asked: they make the
    // module save and, for pin changes, restart — never on the periodic tick.

    private readonly List<RcModuleSetup> _moduleSetups = new();
    private string _moduleSetupsToolKey = string.Empty;

    /// <summary>Module setups for the ACTIVE tool, reloading if the tool changed.</summary>
    public IReadOnlyList<RcModuleSetup> ModuleSetups
    {
        get
        {
            EnsureModuleSetupsLoaded();
            // Snapshot: the live list is mutated under _ioLock from the tick
            // and receive threads; handing it out raw invites the concurrent
            // Add/iterate corruption that once wrote null slots to disk.
            lock (_ioLock) return _moduleSetups.ToArray();
        }
    }

    private void EnsureModuleSetupsLoaded()
    {
        string key = ModuleSetupFile;
        lock (_ioLock)
        {
            if (key == _moduleSetupsToolKey && _moduleSetups.Count > 0) return;
            _moduleSetupsToolKey = key;
            _moduleSetups.Clear();
            try
            {
                if (File.Exists(key))
                {
                    var loaded = JsonSerializer.Deserialize<List<RcModuleSetup>>(File.ReadAllText(key));
                    if (loaded != null)
                        // Drop null entries: files written while the list was
                        // being mutated concurrently (pre-lock builds) persisted
                        // corrupted slots — heal them on load.
                        foreach (var m in loaded)
                            if (m != null) _moduleSetups.Add(m);
                }
            }
            catch { }
            if (_moduleSetups.Count == 0)
                _moduleSetups.Add(new RcModuleSetup { ModuleId = 0, Sensors = { new RcSensorSetup { SensorId = 0 } } });
        }
    }

    private void SaveModuleSetups()
    {
        try
        {
            string path = ModuleSetupFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            List<RcModuleSetup> copy;
            lock (_ioLock) copy = new List<RcModuleSetup>(_moduleSetups);
            File.WriteAllText(path, JsonSerializer.Serialize(copy,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Rate] module setup save failed: {ex.Message}");
        }
    }

    /// <summary>Fill a module's stored setup with the RC board's own factory
    /// defaults — the values its firmware writes in LoadDefaults() (Begin.ino).
    ///
    /// Worth having because nothing can read a module's configuration back: a
    /// fresh or reset board gives no clue what its pins are, and typing 20
    /// fields from a wiki is how a wrong pin ends up driving a valve. These are
    /// staged locally only; the operator reviews them and presses send.</summary>
    // ── Virtual switchbox ───────────────────────────────────────────────────
    // The physical AOG_RC switchbox (PGN 32618) is a box of toggles: master,
    // auto rate, rate up/down, prime. With no box connected this app asserted
    // masterOn permanently, so there was no way to stop product from the screen
    // — not acceptable on a machine metering chemical. These stand in for it.

    /// <summary>Master run/stop across every product. Off commands zero rate at
    /// the module, the same as the physical master switch.</summary>
    public bool MasterOn { get; private set; } = true;

    public void SetMaster(bool on) => MasterOn = on;

    /// <summary>Nudge a channel's target, as the switchbox's rate up/down does.
    /// A percentage of the CURRENT target, so repeated presses behave sensibly
    /// at any rate.</summary>
    public void BumpRate(int index, double percent)
    {
        EnsureToolChannels();
        if (index < 0 || index >= _products.Count) return;
        var p = _products[index];
        p.TargetRate = Math.Max(0, Math.Round(p.TargetRate * (1.0 + percent / 100.0), 2));
        _dirty = true;
        SaveProducts();
    }

    /// <summary>Back to the product's catalogue rate — "what was it meant to be"
    /// after a few nudges.</summary>
    public void ResetRate(int index)
    {
        EnsureToolChannels();
        if (index < 0 || index >= _products.Count) return;
        var p = _products[index];
        var item = _catalog.Find(p.Name);
        if (item == null || item.DefaultRate <= 0) return;
        p.TargetRate = item.DefaultRate;
        _dirty = true;
        SaveProducts();
    }

    /// <summary>Move a module's WIRED subnet (PGN 32503). The module reboots onto
    /// the new subnet and vanishes from this one, so only send it when this host
    /// is on (or moving to) that network.</summary>
    public void PushSubnet(int moduleId, byte ip0, byte ip1, byte ip2)
        => SendToModule(moduleId, RcPgn.BuildSubnetChange(ip0, ip1, ip2));

    /// <summary>Create an entry for a module id that has no setup yet. The UI
    /// could previously only select modules it already knew about, so there was
    /// no way to prepare a board before giving it that id.</summary>
    public void AddModule(int moduleId)
    {
        GetOrAddModuleSetup(moduleId);
        SaveModuleSetups();
    }

    /// <summary>Put every relay back to driving its own section.</summary>
    public void ResetRelays(int moduleId)
    {
        GetOrAddModuleSetup(moduleId).Relays = RcRelayMap.DefaultRelays(moduleId);
        SaveModuleSetups();
    }

    /// <summary>Renumber the section-driving relays consecutively, so a boom
    /// wired in order does not have to be numbered by hand one at a time.</summary>
    public void RenumberRelays(int moduleId, int startSection)
    {
        var m = GetOrAddModuleSetup(moduleId);
        RcRelayMap.Renumber(m.EnsureRelays(), startSection, Math.Max(1, _sections.SectionStates.Count));
        SaveModuleSetups();
    }

    /// <summary>Fill a module's pins, flags and valve tuning with the factory
    /// defaults for a board type, matching the native app's Modules &gt; Boards
    /// page. Staged locally only — nothing reaches the module until pushed.</summary>
    public void LoadModuleDefaults(int moduleId, string board = "esp32")
    {
        RcBoardDefaults.Apply(GetOrAddModuleSetup(moduleId), board);
        SaveModuleSetups();
    }

    public RcModuleSetup GetOrAddModuleSetup(int moduleId)
    {
        EnsureModuleSetupsLoaded();
        RcModuleSetup added;
        lock (_ioLock)
        {
            foreach (var m in _moduleSetups)
                if (m.ModuleId == moduleId) return m;
            added = new RcModuleSetup { ModuleId = moduleId, Sensors = { new RcSensorSetup { SensorId = 0 } } };
            _moduleSetups.Add(added);
        }
        SaveModuleSetups();
        return added;
    }

    private sealed class SwitchboxFile
    {
        public RcSwitchboxSettings Switchbox { get; set; } = new();
        public RcPrimedSettings Primed { get; set; } = new();
    }

    private string SwitchboxPath => Path.Combine(RateDir, $"switchbox-{ToolKey}.json");

    private void EnsureSwitchboxLoaded()
    {
        if (_switchboxToolKey == ToolKey) return;
        _switchboxToolKey = ToolKey;
        _switchbox = new RcSwitchboxSettings();
        _primed = new RcPrimedSettings();
        try
        {
            if (File.Exists(SwitchboxPath))
            {
                var f = JsonSerializer.Deserialize<SwitchboxFile>(File.ReadAllText(SwitchboxPath));
                if (f != null) { _switchbox = f.Switchbox; _primed = f.Primed; }
            }
        }
        catch { }
    }

    private void SaveSwitchbox()
    {
        try
        {
            Directory.CreateDirectory(RateDir);
            File.WriteAllText(SwitchboxPath, JsonSerializer.Serialize(
                new SwitchboxFile { Switchbox = _switchbox, Primed = _primed },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Rate] switchbox save failed: {ex.Message}");
        }
    }

    public RcSwitchboxSettings Switchbox { get { EnsureSwitchboxLoaded(); return _switchbox; } }
    public RcPrimedSettings Primed { get { EnsureSwitchboxLoaded(); return _primed; } }

    public void SetSwitchboxValue(string key, string value)
    {
        EnsureSwitchboxLoaded();
        bool Bo() => value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        double D() => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        switch (key)
        {
            case "masterMode": _switchbox.MasterMode = (RcMasterMode)Math.Clamp((int)D(), 0, 2); break;
            case "switchType": _switchbox.SwitchType = Math.Clamp((int)D(), 0, 1); break;
            case "onScreen": _switchbox.OnScreenEnabled = Bo(); break;
            case "autoRate": _switchbox.AutoRate = Bo(); break;
            case "workGate": _switchbox.WorkSwitchGate = Bo(); break;
            case "expectPhysical": _switchbox.ExpectPhysical = Bo(); break;
            // "secSwitch:<section>" allocates a section to an on-screen switch
            case string k when k.StartsWith("secSwitch:", StringComparison.Ordinal)
                            && int.TryParse(k.AsSpan(10), out var sec) && sec >= 0 && sec < 64:
            {
                var arr = _switchbox.SectionSwitch;
                if (arr.Length <= sec)
                {
                    var grown = new int[sec + 1];
                    for (int i = 0; i < grown.Length; i++)
                        grown[i] = i < arr.Length ? arr[i] : (i < 8 ? i : 7);
                    _switchbox.SectionSwitch = arr = grown;
                }
                arr[sec] = Math.Clamp((int)D(), -1, 7);
                break;
            }
            case "primed.onTime": _primed.OnTimeS = Math.Clamp(D(), 1, 120); break;
            case "primed.speed": _primed.SpeedKmh = Math.Clamp(D(), 0.5, 40); break;
            case "primed.delay": _primed.MasterDelayS = Math.Clamp(D(), 0, 30); break;
            case "primed.resume": _primed.Resume = Bo(); break;
            default: return;
        }
        SaveSwitchbox();
    }

    public bool PrimedActive => DateTime.UtcNow < _primedUntilUtc;

    public void StartPrimed()
    {
        EnsureSwitchboxLoaded();
        // The native rule: not while moving, not with a maintained switch.
        if (_switchbox.SwitchType == 1) return;
        if (Math.Abs(_state.Vehicle.Speed) * 3.6 > 0.5) return;
        _primedUntilUtc = DateTime.UtcNow.AddSeconds(_primed.OnTimeS);
    }

    public void CancelPrimed() => _primedUntilUtc = DateTime.MinValue;

    /// <summary>Work switch reads "on" (implement down) — polarity and
    /// momentary-latch resolution live in the snapshot. A connected physical
    /// switchbox's maintained work switch ORs in, like the reference's WorkOn
    /// (box OR modules).</summary>
    private bool WorkSwitchOn()
    {
        if (_physBox.Connected(DateTime.UtcNow) && _physBox.WorkSwitchOn) return true;
        return _autoSteer?.LatestSnapshot?.WorkSwitchOn ?? false;
    }

    /// <summary>Master as the machine should see it: the raw switch filtered
    /// through the master mode, the work-switch gate and a primed run.</summary>
    private bool EffectiveMaster()
    {
        EnsureSwitchboxLoaded();
        if (PrimedActive) return true;
        if (_switchbox.MasterMode == RcMasterMode.Override) return true;
        // Work-switch gate: master only engages while the implement is down.
        // Only meaningful when a work switch actually exists — a gate on a
        // switch that doesn't would latch the master off. It exists when the
        // tool has one wired, or when a physical switchbox (which carries its
        // own) is talking.
        if (_switchbox.WorkSwitchGate
            && (_configStore.Tool.IsWorkSwitchEnabled || _physBox.Connected(DateTime.UtcNow))
            && !WorkSwitchOn())
        {
            return false;
        }
        return MasterOn;
    }

    /// <summary>Send one frame to a module: per-NIC directed broadcast plus a
    /// direct unicast once the module's address is known (some builds run the
    /// W5500 with broadcast blocking).</summary>
    private void SendToModule(int moduleId, byte[] frame)
    {
        if (_socket == null) return;
        foreach (var ep in BroadcastEndpoints())
            try { _socket.SendTo(frame, ep); } catch { }
        IPAddress? addr;
        lock (_ioLock) addr = _moduleAddresses.TryGetValue(moduleId, out var e3) ? e3.Address : null;
        if (addr != null)
            try { _socket.SendTo(frame, new IPEndPoint(addr, RcPgn.ModuleListenPort)); } catch { }
    }

    /// <summary>Push valve/PID tuning for one sensor (32502).</summary>
    public void PushControlSettings(int moduleId, int sensorId)
    {
        var s = GetOrAddModuleSetup(moduleId).GetOrAddSensor(sensorId);
        SendToModule(moduleId, RcPgn.BuildControlSettings(moduleId, sensorId, s.Control));
    }

    /// <summary>Push pin assignments for one sensor (32507). The module saves and
    /// restarts when a pin actually changes.</summary>
    public void PushSensorPins(int moduleId, int sensorId)
    {
        var s = GetOrAddModuleSetup(moduleId).GetOrAddSensor(sensorId);
        SendToModule(moduleId, RcPgn.BuildSensorPins(moduleId, sensorId, s.Pins));
    }

    /// <summary>Push module-wide config (32700).
    ///
    /// <paramref name="assignId"/> is commissioning only: EVERY module listening
    /// adopts the id, so the caller must have confirmed a single connected board.
    /// It is never persisted — a saved "assign" flag would silently re-brand
    /// modules on later pushes.</summary>
    public void PushModuleConfig(int moduleId, bool assignId = false)
    {
        var m = GetOrAddModuleSetup(moduleId);
        var cfg = m.Config;
        cfg.ModuleId = (byte)moduleId;
        bool prior = cfg.AssignModuleId;
        cfg.AssignModuleId = assignId;
        try
        {
            var frame = RcPgn.BuildModuleConfig(cfg);
            SendToModule(moduleId, frame);
            // Assigning gives a board a NEW id, so the target id has no learned
            // address yet — and the W5500 drops the directed broadcasts, so
            // broadcast-only delivery never arrives on Ethernet (bench-proven:
            // the board only took its id from a direct unicast). Assign is
            // defined as "every listening module adopts it", so unicasting to
            // every address we know is the same semantics with delivery that
            // actually works.
            if (assignId && _socket != null)
            {
                List<IPAddress> known;
                lock (_ioLock)
                {
                    known = new List<IPAddress>();
                    foreach (var e in _moduleAddresses.Values) known.Add(e.Address);
                }
                foreach (var a in known)
                    try { _socket.SendTo(frame, new IPEndPoint(a, RcPgn.ModuleListenPort)); } catch { }
            }
        }
        finally { cfg.AssignModuleId = prior; }
    }

    /// <summary>Push everything for a module: config, then each sensor's pins and
    /// tuning. Used to recommission a replaced or reset board from the tool's
    /// saved record.</summary>
    public void PushAll(int moduleId)
    {
        var m = GetOrAddModuleSetup(moduleId);
        PushModuleConfig(moduleId);
        foreach (var s in m.Sensors)
        {
            PushSensorPins(moduleId, s.SensorId);
            PushControlSettings(moduleId, s.SensorId);
        }
    }

    /// <summary>Edit one module-setup field. Keys are "cfg.*" for module-wide
    /// values, "pins.*" and "ctl.*" for the addressed sensor.</summary>
    public void SetModuleSetupValue(int moduleId, int sensorId, string key, string value)
    {
        var m = GetOrAddModuleSetup(moduleId);
        var sen = m.GetOrAddSensor(sensorId);
        byte B() => byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : (byte)0;
        ushort U() => ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : (ushort)0;
        bool Bo() => value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

        switch (key)
        {
            // module-wide (32700)
            case "cfg.sensorCount": m.Config.SensorCount = B(); break;
            case "cfg.invertRelay": m.Config.InvertRelayControl = Bo(); break;
            case "cfg.invertFlow": m.Config.InvertFlowControl = Bo(); break;
            case "cfg.workMomentary": m.Config.WorkPinMomentary = Bo(); break;
            case "cfg.is3Wire": m.Config.Is3WireValve = Bo(); break;
            case "cfg.ads1115": m.Config.Ads1115Enabled = Bo(); break;
            case "cfg.onboardRelayType": m.Config.OnboardRelayType = B(); break;
            case "cfg.remoteRelayType": m.Config.RemoteRelayType = B(); break;
            case "cfg.workPin": m.Config.WorkPin = B(); break;
            case "cfg.pressurePin": m.Config.PressurePin = B(); break;
            case "cfg.board": m.Board = value; break;
            case "cfg.flowMasterMode":
                m.FlowMasterMode = Enum.TryParse<RcFlowMasterMode>(value, true, out var fm)
                    ? fm : (RcFlowMasterMode)B();
                break;
            // relay functions, "rly<N>.type" / ".section" / ".switch"
            case string k3 when k3.StartsWith("rly", StringComparison.Ordinal) && k3.Contains('.'):
            {
                int dot = k3.IndexOf('.');
                if (!int.TryParse(k3.AsSpan(3, dot - 3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rid))
                    return;
                var rly = m.EnsureRelays().Find(x => x.Id == rid);
                if (rly == null) return;
                switch (k3[(dot + 1)..])
                {
                    case "type": rly.Type = (RcRelayType)B(); break;
                    // -1 (no section) has to survive, so this one is signed.
                    case "section":
                        rly.SectionId = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sid)
                            ? Math.Clamp(sid, -1, 127) : -1;
                        break;
                    case "switch": rly.SwitchId = Math.Clamp((int)B(), 0, 15); break;
                    default: return;
                }
                break;
            }
            // relay output pins, "cfg.relay0".."cfg.relay15"
            case string k2 when k2.StartsWith("cfg.relay", StringComparison.Ordinal)
                             && int.TryParse(k2.Substring(9), out var ri)
                             && ri >= 0 && ri < m.Config.RelayPins.Length:
                m.Config.RelayPins[ri] = B(); break;
            // per-sensor pins (32507)
            case "pins.flow": sen.Pins.FlowPin = B(); break;
            case "pins.dir": sen.Pins.DirPin = B(); break;
            case "pins.pwm": sen.Pins.PwmPin = B(); break;
            case "pins.bin": sen.Pins.BinPin = B(); break;
            case "pins.invertBin": sen.Pins.InvertBinSensor = Bo(); break;
            // per-sensor valve/PID tuning (32502)
            case "ctl.maxPwm": sen.Control.MaxPwm = B(); break;
            case "ctl.minPwm": sen.Control.MinPwm = B(); break;
            case "ctl.kp": sen.Control.Kp = B(); break;
            case "ctl.ki": sen.Control.Ki = B(); break;
            case "ctl.deadband": sen.Control.Deadband = B(); break;
            case "ctl.brakePoint": sen.Control.BrakePoint = B(); break;
            case "ctl.slowAdjust": sen.Control.PidSlowAdjust = B(); break;
            case "ctl.slewRate": sen.Control.SlewRate = B(); break;
            case "ctl.maxIntegral": sen.Control.MaxIntegral = B(); break;
            case "ctl.pidTime": sen.Control.PidTime = B(); break;
            case "ctl.timedMinStart": sen.Control.TimedMinStart = B(); break;
            case "ctl.timedAdjust": sen.Control.TimedAdjust = U(); break;
            case "ctl.timedPause": sen.Control.TimedPause = U(); break;
            case "ctl.pulseMinHz": sen.Control.PulseMinHz = B(); break;
            case "ctl.pulseMaxHz": sen.Control.PulseMaxHz = U(); break;
            case "ctl.sampleWindow": sen.Control.PulseSampleSize = B(); break;
            default: return;
        }
        // Mirror the sensor pins into the module-wide packet, which carries its
        // own copy for sensors 0-1; leaving them stale would undo a pin change
        // the next time the config is pushed.
        for (int i = 0; i < 2; i++)
        {
            var s = m.Sensors.Find(x => x.SensorId == i);
            if (s == null) continue;
            m.Config.SensorPins[i * 3 + 0] = s.Pins.FlowPin;
            m.Config.SensorPins[i * 3 + 1] = s.Pins.DirPin;
            m.Config.SensorPins[i * 3 + 2] = s.Pins.PwmPin;
        }
        SaveModuleSetups();
    }

    /// <summary>Module setup for the web client, plus which modules are actually
    /// answering right now (so the UI can gate ID assignment on a single board).</summary>
    public string BuildModuleSetupJson()
    {
        EnsureModuleSetupsLoaded();
        var sb = new StringBuilder();
        sb.Append("{\"tool\":").Append(JsonSerializer.Serialize(_configStore.ActiveToolProfileName ?? ""));
        sb.Append(",\"useRateControl\":").Append(_configStore.Tool.UseRateControl ? "true" : "false");
        List<int> heard;
        List<KeyValuePair<int, string>> addrs;
        var cutoff = DateTime.UtcNow.AddSeconds(-ModuleHeardWindowSeconds);
        lock (_ioLock)
        {
            heard = new List<int>();
            addrs = new List<KeyValuePair<int, string>>();
            foreach (var kv in _moduleAddresses)
            {
                addrs.Add(new KeyValuePair<int, string>(kv.Key, kv.Value.Address.ToString()));
                if (kv.Value.LastHeardUtc >= cutoff) heard.Add(kv.Key);
            }
        }
        heard.Sort();
        sb.Append(",\"modulesHeard\":[").Append(string.Join(",", heard)).Append(']');
        // Last-known address per module id — for the Network IO panel's rate rows.
        addrs.Sort((a, b) => a.Key.CompareTo(b.Key));
        sb.Append(",\"moduleIps\":{");
        for (int ai = 0; ai < addrs.Count; ai++)
        {
            if (ai > 0) sb.Append(',');
            sb.Append('"').Append(addrs[ai].Key).Append("\":")
              .Append(JsonSerializer.Serialize(addrs[ai].Value));
        }
        sb.Append('}');
        sb.Append(",\"modules\":[");
        List<RcModuleSetup> setups;
        lock (_ioLock) setups = new List<RcModuleSetup>(_moduleSetups);
        for (int i = 0; i < setups.Count; i++)
        {
            var m = setups[i];
            if (i > 0) sb.Append(',');
            var c = m.Config;
            // Live relay word too: the only way to see a relay map is doing what
            // was intended, short of watching the outputs click.
            var live = ComputeRelayWords(m.ModuleId, _sections.GetSectionBits());
            sb.Append("{\"moduleId\":").Append(m.ModuleId)
              .Append(",\"board\":").Append(JsonSerializer.Serialize(m.Board ?? "esp32"))
              .Append(",\"relayPins\":[").Append(string.Join(",", c.RelayPins)).Append(']')
              .Append(",\"flowMasterMode\":").Append((int)m.FlowMasterMode)
              .Append(",\"liveRelays\":").Append(live.Relays)
              .Append(",\"liveValveIndex\":").Append(live.FlowMasterIndex)
              .Append(",\"relays\":[");
            var rl = m.EnsureRelays();
            for (int r = 0; r < rl.Count; r++)
            {
                if (r > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(rl[r].Id)
                  .Append(",\"type\":").Append((int)rl[r].Type)
                  .Append(",\"section\":").Append(rl[r].SectionId)
                  .Append(",\"switch\":").Append(rl[r].SwitchId).Append('}');
            }
            sb.Append(']')
              .Append(",\"cfg\":{\"sensorCount\":").Append(c.SensorCount)
              .Append(",\"invertRelay\":").Append(c.InvertRelayControl ? "true" : "false")
              .Append(",\"invertFlow\":").Append(c.InvertFlowControl ? "true" : "false")
              .Append(",\"workMomentary\":").Append(c.WorkPinMomentary ? "true" : "false")
              .Append(",\"is3Wire\":").Append(c.Is3WireValve ? "true" : "false")
              .Append(",\"ads1115\":").Append(c.Ads1115Enabled ? "true" : "false")
              .Append(",\"onboardRelayType\":").Append(c.OnboardRelayType)
              .Append(",\"remoteRelayType\":").Append(c.RemoteRelayType)
              .Append(",\"workPin\":").Append(c.WorkPin)
              .Append(",\"pressurePin\":").Append(c.PressurePin)
              .Append("},\"sensors\":[");
            for (int j = 0; j < m.Sensors.Count; j++)
            {
                var s = m.Sensors[j];
                if (j > 0) sb.Append(',');
                sb.Append("{\"sensorId\":").Append(s.SensorId)
                  .Append(",\"pins\":{\"flow\":").Append(s.Pins.FlowPin)
                  .Append(",\"dir\":").Append(s.Pins.DirPin)
                  .Append(",\"pwm\":").Append(s.Pins.PwmPin)
                  .Append(",\"bin\":").Append(s.Pins.BinPin)
                  .Append(",\"invertBin\":").Append(s.Pins.InvertBinSensor ? "true" : "false")
                  .Append("},\"ctl\":{\"maxPwm\":").Append(s.Control.MaxPwm)
                  .Append(",\"minPwm\":").Append(s.Control.MinPwm)
                  .Append(",\"kp\":").Append(s.Control.Kp)
                  .Append(",\"ki\":").Append(s.Control.Ki)
                  .Append(",\"deadband\":").Append(s.Control.Deadband)
                  .Append(",\"brakePoint\":").Append(s.Control.BrakePoint)
                  .Append(",\"slowAdjust\":").Append(s.Control.PidSlowAdjust)
                  .Append(",\"slewRate\":").Append(s.Control.SlewRate)
                  .Append(",\"maxIntegral\":").Append(s.Control.MaxIntegral)
                  .Append(",\"pidTime\":").Append(s.Control.PidTime)
                  .Append(",\"timedMinStart\":").Append(s.Control.TimedMinStart)
                  .Append(",\"timedAdjust\":").Append(s.Control.TimedAdjust)
                  .Append(",\"timedPause\":").Append(s.Control.TimedPause)
                  .Append(",\"pulseMinHz\":").Append(s.Control.PulseMinHz)
                  .Append(",\"pulseMaxHz\":").Append(s.Control.PulseMaxHz)
                  .Append(",\"sampleWindow\":").Append(s.Control.PulseSampleSize)
                  .Append("}}");
            }
            sb.Append("]}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private void LoadProducts()
    {
        _products.Clear();
        _productsToolKey = ToolKey;
        try
        {
            string path = ProductsFile;
            // First run after the per-tool split: adopt the old shared list for
            // this tool rather than starting the operator from scratch. The file
            // is left in place — other tools migrate from it the same way, each
            // then diverging (which is the point: separate calibrations).
            if (!File.Exists(path) && File.Exists(LegacyProductsFile))
                path = LegacyProductsFile;
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<List<RateProduct>>(File.ReadAllText(path));
                if (loaded != null) _products.AddRange(loaded);
            }
        }
        catch { }
        while (_products.Count < ProductCount)
            _products.Add(new RateProduct { Name = $"Product {(char)('A' + _products.Count)}" });
        if (_products.Count > ProductCount) _products.RemoveRange(ProductCount, _products.Count - ProductCount);
        LoadCatalog();
        SeedCatalogFromChannels();
    }

    // ── Product catalogue (global) ──────────────────────────────────────────

    private readonly RateCatalog _catalog = new();
    private string _productsToolKey = string.Empty;

    public RateCatalog Catalog { get { EnsureToolChannels(); return _catalog; } }

    /// <summary>Reload this tool's channels when the active tool profile changes,
    /// so switching implements switches calibrations with it.</summary>
    private void EnsureToolChannels()
    {
        if (ToolKey == _productsToolKey) return;
        // Order matters: SaveProducts() resolves its path from _productsToolKey,
        // so it must run BEFORE LoadProducts() moves that key to the new tool.
        SaveProducts();          // outgoing implement's channels, to its own file
        LoadProducts();          // incoming implement's channels + its calibration
    }

    private void LoadCatalog()
    {
        _catalog.Items.Clear();
        try
        {
            if (File.Exists(CatalogFile))
            {
                var loaded = JsonSerializer.Deserialize<RateCatalog>(File.ReadAllText(CatalogFile));
                if (loaded?.Items != null) _catalog.Items.AddRange(loaded.Items);
            }
        }
        catch { }
    }

    private void SaveCatalog()
    {
        try
        {
            Directory.CreateDirectory(RateDir);
            File.WriteAllText(CatalogFile, JsonSerializer.Serialize(_catalog,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Rate] catalog save failed: {ex.Message}");
        }
    }

    /// <summary>Bootstrap the catalogue from whatever the channels already carry,
    /// so an operator upgrading does not find an empty product list. Placeholder
    /// "Product B" style names are skipped.</summary>
    private void SeedCatalogFromChannels()
    {
        bool added = false;
        foreach (var p in _products)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) continue;
            if (p.Name.StartsWith("Product ", StringComparison.OrdinalIgnoreCase) && p.Name.Length <= 10) continue;
            if (_catalog.Find(p.Name) != null) continue;
            _catalog.AddOrUpdate(p.Name, p.Units, p.TargetRate);
            added = true;
        }
        if (added) SaveCatalog();
    }

    public void CatalogAddOrUpdate(string name, string units, double defaultRate)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        EnsureToolChannels();
        _catalog.AddOrUpdate(name, units, defaultRate);
        SaveCatalog();
    }

    public void CatalogRemove(string name)
    {
        EnsureToolChannels();
        if (_catalog.Remove(name)) SaveCatalog();
    }

    /// <summary>Load a catalogue product into a channel. Identity (name, units)
    /// and the usual rate come from the catalogue; the channel keeps its own
    /// hardware settings — module, sensor and above all the meter calibration.</summary>
    public void AssignProduct(int index, string name)
    {
        EnsureToolChannels();
        if (index < 0 || index >= _products.Count) return;
        var item = _catalog.Find(name);
        if (item == null) return;
        var p = _products[index];
        p.Name = item.Name;
        p.Units = item.Units;
        if (item.DefaultRate > 0) p.TargetRate = item.DefaultRate;
        _dirty = true;
        SaveProducts();
    }

    private void SaveProducts()
    {
        try
        {
            string dir = Path.GetDirectoryName(ProductsFile)!;
            Directory.CreateDirectory(dir);
            List<RateProduct> copy;
            lock (_ioLock) copy = new List<RateProduct>(_products);
            File.WriteAllText(ProductsFile, JsonSerializer.Serialize(copy,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Rate] save failed: {ex.Message}");
        }
    }
}
