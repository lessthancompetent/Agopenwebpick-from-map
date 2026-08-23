// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;

// Aliases to disambiguate from colliding namespaces.
using TrackModel = AgOpenWeb.Models.Track.Track;
using TrackInput = AgOpenWeb.Models.Track.TrackGuidanceInput;
using ApplicationState = AgOpenWeb.Models.State.ApplicationState;

namespace AgOpenWeb.Services.AutoSteer;

/// <summary>
/// Zero-copy AutoSteer pipeline service.
/// Coordinates GPS→Parse→Guidance→PGN flow with minimum latency.
/// Single VehicleState instance is updated in place - no allocations per cycle.
/// </summary>
public class AutoSteerService : IAutoSteerService
{
    // Core state - single instance, updated in place
    private VehicleState _state;

    // Dependencies
    private readonly ITrackGuidanceService _guidanceService;
    private readonly IUdpCommunicationService _udpService;
    private readonly IGpsService _gpsService;
    private readonly ApplicationState _appState;
    private readonly ConfigurationStore _configStore;
    private ITramLineService? _tramLineService;
    private ISmartWasCalibrationService? _smartWas;

    // Drift compensation applied after LocalPlane → local coordinate conversion.
    // LocalPlane itself is owned by ApplicationState.Field.LocalPlane — single shared instance
    // across AutoSteer and the cycle worker. Created by field-open (UI thread) or by the
    // cycle worker on first valid fix; never written here (receive thread).
    private double _driftEasting;
    private double _driftNorthing;

    // Current track for guidance (set by MainViewModel)
    private TrackModel? _currentTrack;
    private TrackInput _guidanceInput;

    // Latency tracking (circular buffer for last 10 samples)
    private readonly double[] _latencySamples = new double[10];
    private int _latencySampleIndex;
    private long _cycleCount;
    private long _parseFailures;

    // Service state
    private bool _isEnabled;
    private bool _isEngaged;

    // Config-change → PGN 251/252 emission. The wizard and the
    // AutoSteer config dialog both mutate ConfigStore.AutoSteer (and
    // Tool.IsSteerSwitchEnabled); previously only the dialog's
    // Apply/Reset paths emitted the PGNs. Result: wizard-step writes
    // weren't visible to the module/simulator until the dialog was
    // opened. This service is the single owner of the emit: subscribe
    // to PropertyChanged, debounce, and re-emit both PGNs.
    private readonly Timer _configEmitTimer;
    private int _configEmitDelayMs = 150;
    private bool _configSubscribed;
    private AutoSteerConfig? _subscribedAutoSteer;
    private ToolConfig? _subscribedTool;
    private MachineConfig? _subscribedMachine;
    private ConfigurationStore? _subscribedStore;

    // Machine-module config (PGN 238/236/235) gets the same treatment: a
    // baseline at Start, a debounced re-emit on any machine / section-layout
    // change, and a re-push when the machine board's hello reappears after a
    // gap. Before this the trio only ever left on the manual "Send" button,
    // so a rebooted or freshly flashed board ran on whatever its EEPROM held.
    private readonly Timer _machineEmitTimer;

    /// <summary>
    /// Test seam: lets tests shorten the debounce so they don't have
    /// to sleep the wall clock to observe coalescing behaviour.
    /// </summary>
    internal int ConfigEmitDebounceMilliseconds
    {
        get => _configEmitDelayMs;
        set => _configEmitDelayMs = value;
    }

    public event EventHandler<VehicleStateSnapshot>? StateUpdated;

    public bool IsEnabled => _isEnabled;
    public bool IsEngaged => _isEngaged;
    public bool IsInFreeDriveMode => _state.IsInFreeDriveMode;
    public double FreeDriveSteerAngle => _state.FreeDriveSteerAngle;

    public AutoSteerService(
        ITrackGuidanceService guidanceService,
        IUdpCommunicationService udpService,
        IGpsService gpsService,
        ApplicationState appState,
        ConfigurationStore configStore)
    {
        _guidanceService = guidanceService;
        _udpService = udpService;
        _gpsService = gpsService;
        _appState = appState;
        _configStore = configStore;

        // Initialize state
        _state = new VehicleState();
        _guidanceInput = new TrackInput();

        // Debounce timer: parked Infinite until a PropertyChanged event
        // arms it. Coalesces bursts (Reset-to-Defaults touches many
        // fields in quick succession; we want exactly one PGN emit
        // pair, not one per field).
        _configEmitTimer = new Timer(_ => EmitSteerConfigPgns(),
            state: null,
            dueTime: Timeout.Infinite,
            period: Timeout.Infinite);
        _machineEmitTimer = new Timer(_ => EmitMachineConfigPgns(),
            state: null,
            dueTime: Timeout.Infinite,
            period: Timeout.Infinite);
    }

    /// <summary>
    /// Set the tram line service for real-time wheel detection in PGN 239.
    /// </summary>
    public void SetTramLineService(ITramLineService tramLineService)
    {
        _tramLineService = tramLineService;
    }

    /// <summary>
    /// Set the Smart WAS calibration service so each PGN 253 sample is
    /// forwarded to its analyzer. Setter-injected to avoid the constructor
    /// cycle (Smart WAS depends on IAutoSteerService).
    /// </summary>
    public void SetSmartWasService(ISmartWasCalibrationService smartWas)
    {
        _smartWas = smartWas;
    }

    public void Start()
    {
        _isEnabled = true;
        _udpService.DataReceived += OnUdpDataReceived;
        _udpService.ModuleConnectionChanged += OnModuleConnectionChanged;

        // Subscribe before the initial emission so a settings write that
        // races against startup still re-fires through the debounce.
        SubscribeToConfigChanges();

        // Initial baseline: send current ConfigStore.AutoSteer values
        // once so the module / simulator sees them without waiting for
        // the operator to touch a setting. Prevents the "simulator's
        // Steer Switch toggle stays greyed until I open the AutoSteer
        // config dialog" symptom — the simulator reads switch-type from
        // PGN 251 byte 5 and we'd previously never send it on startup.
        EmitSteerConfigPgns();
        // Same baseline for the machine board (238/236/235).
        EmitMachineConfigPgns();
    }

    public void Stop()
    {
        _udpService.DataReceived -= OnUdpDataReceived;
        _udpService.ModuleConnectionChanged -= OnModuleConnectionChanged;
        UnsubscribeFromConfigChanges();
        _isEnabled = false;
        _isEngaged = false;
    }

    /// <summary>
    /// A steer module whose hello just (re)appeared may have rebooted with
    /// blank or stale EEPROM — and the startup PGN 251/252 emission goes to
    /// nobody when the app boots before the module powers up. Push the saved
    /// profile config whenever the module comes (back) online so it always
    /// matches the app without the operator touching a setting. Routed
    /// through the debounce timer to coalesce with any concurrent config
    /// burst; the module's UDP stack is up by the time it hellos.
    /// </summary>
    private void OnModuleConnectionChanged(object? sender, ModuleConnectionEventArgs e)
    {
        if (!e.IsConnected) return;
        if (e.ModuleType == ModuleType.AutoSteer)
        {
            Console.WriteLine($"[AutoSteer] Steer module online at {e.IPAddress} - re-emitting config (PGN 251/252)");
            _configEmitTimer.Change(_configEmitDelayMs, Timeout.Infinite);
        }
        else if (e.ModuleType == ModuleType.Machine)
        {
            Console.WriteLine($"[Machine] Machine module online at {e.IPAddress} - re-emitting config (PGN 238/236/235)");
            _machineEmitTimer.Change(_configEmitDelayMs, Timeout.Infinite);
        }
    }

    private void SubscribeToConfigChanges()
    {
        if (_configSubscribed) return;
        _subscribedAutoSteer = _configStore.AutoSteer;
        _subscribedTool = _configStore.Tool;
        _subscribedMachine = _configStore.Machine;
        _subscribedStore = _configStore;
        _subscribedAutoSteer.PropertyChanged += OnConfigPropertyChanged;
        _subscribedTool.PropertyChanged += OnConfigPropertyChanged;
        _subscribedMachine.PropertyChanged += OnMachineConfigPropertyChanged;
        _subscribedStore.PropertyChanged += OnStorePropertyChanged;
        _configSubscribed = true;
    }

    private void UnsubscribeFromConfigChanges()
    {
        if (!_configSubscribed) return;
        if (_subscribedAutoSteer != null)
            _subscribedAutoSteer.PropertyChanged -= OnConfigPropertyChanged;
        if (_subscribedTool != null)
            _subscribedTool.PropertyChanged -= OnConfigPropertyChanged;
        if (_subscribedMachine != null)
            _subscribedMachine.PropertyChanged -= OnMachineConfigPropertyChanged;
        if (_subscribedStore != null)
            _subscribedStore.PropertyChanged -= OnStorePropertyChanged;
        _configSubscribed = false;
    }

    /// <summary>
    /// PropertyChanged handler for AutoSteer / Tool config. Any change
    /// re-arms the debounce timer; the timer fires the actual PGN
    /// emission once the storm subsides. PGN 251 and 252 are always
    /// emitted as a pair — the cost is two small UDP packets, and
    /// splitting "which PGN does this property belong to" would
    /// duplicate the bit-packing knowledge from <c>PgnBuilder</c>.
    /// </summary>
    private void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _configEmitTimer.Change(_configEmitDelayMs, Timeout.Infinite);
        // Section widths ride PGN 235 to the machine plane as well.
        if (ReferenceEquals(sender, _subscribedTool)
            && (e.PropertyName == nameof(ToolConfig.SectionWidths)
                || e.PropertyName == nameof(ToolConfig.TotalSectionWidth)))
            _machineEmitTimer.Change(_configEmitDelayMs, Timeout.Infinite);
    }

    private void OnMachineConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _machineEmitTimer.Change(_configEmitDelayMs, Timeout.Infinite);
    }

    private void OnStorePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConfigurationStore.NumSections))
            _machineEmitTimer.Change(_configEmitDelayMs, Timeout.Infinite);
    }

    private void EmitMachineConfigPgns()
    {
        if (!_isEnabled) return; // raced past Stop()
        try
        {
            SendMachineConfigAll();
            Console.WriteLine("[Machine] PGN 238/236/235 emitted to modules");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoSteerService] machine PGN emit failed: {ex.Message}");
        }
    }

    private void EmitSteerConfigPgns()
    {
        if (!_isEnabled) return; // raced past Stop()
        try
        {
            var cfg = _configStore.AutoSteer;
            _udpService.SendToModules(PgnBuilder.BuildSteerConfigPgn(cfg));
            _udpService.SendToModules(PgnBuilder.BuildSteerSettingsPgn(cfg));
            Console.WriteLine("[AutoSteer] PGN 251/252 pair emitted to modules");
        }
        catch (Exception ex)
        {
            // Don't let an emit failure tear down the timer / service.
            Debug.WriteLine($"[AutoSteerService] PGN emit failed: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Module Data Reception (PGN 253, 250)
    // ═══════════════════════════════════════════════════════════════════════

    // Latest module data - updated from PGN 253/250
    private SteerModuleData _lastSteerData;
    private SensorModuleData _lastSensorData;
    private double _sensorPercent;

    /// <summary>Latest steer data from module (PGN 253).</summary>
    public SteerModuleData LastSteerData => _lastSteerData;

    private DateTime _lastSteerDataUtc = DateTime.MinValue;
    /// <summary>Age of <see cref="LastSteerData"/>; <see cref="TimeSpan.MaxValue"/> until the module first reports.</summary>
    public TimeSpan LastSteerDataAge => _lastSteerDataUtc == DateTime.MinValue
        ? TimeSpan.MaxValue
        : DateTime.UtcNow - _lastSteerDataUtc;

    /// <summary>Latest sensor data from module (PGN 250).</summary>
    public SensorModuleData LastSensorData => _lastSensorData;

    /// <summary>Sensor reading as percentage (0-100).</summary>
    public double SensorPercent => _sensorPercent;

    /// <inheritdoc/>
    public VehicleStateSnapshot? LatestSnapshot => _latestSnapshot;
    private VehicleStateSnapshot? _latestSnapshot;

    /// <summary>
    /// Handle incoming UDP data from steering module.
    /// </summary>
    private void OnUdpDataReceived(object? sender, UdpDataReceivedEventArgs e)
    {
        switch (e.PGN)
        {
            case PgnNumbers.AUTOSTEER_DATA: // 253 - Steer Data from module
                ProcessSteerData(e.Data);
                break;

            case PgnNumbers.SENSOR_DATA: // 250 - Sensor Data from module
                ProcessSensorData(e.Data);
                break;

            case PgnNumbers.FROM_MACHINE: // 237 - machine status (work switch)
                ProcessMachineData(e.Data);
                break;
        }
    }

    /// <summary>Process PGN 237 — machine module status. Only the work-switch
    /// report matters here; relay echo/tramline are diagnostics.</summary>
    private void ProcessMachineData(byte[] data)
    {
        if (!PgnBuilder.TryParseMachineWorkSwitch(data, out bool present, out bool active))
            return;
        bool changed = _state.MachineWorkSwitchPresent != present
                    || _state.MachineWorkSwitchActive != active;
        _state.MachineWorkSwitchPresent = present;
        _state.MachineWorkSwitchActive = active;
        // Same reasoning as the steer switches: a flipped switch should be
        // visible immediately, not on the next GPS tick.
        if (changed) NotifyStateUpdated();
    }

    /// <summary>
    /// Process PGN 253 - Steer Data from module.
    /// Contains actual steer angle, switch states, PWM feedback.
    /// </summary>
    private void ProcessSteerData(byte[] data)
    {
        if (!PgnBuilder.TryParseSteerData(data, out var steerData))
            return;

        // Track whether anything UI-relevant changed so we only burn a
        // snapshot allocation + handler dispatch when there's news.
        // The motor calibration wizard's physical-switch gate is the
        // motivating case: without firing StateUpdated here, the gate
        // would only re-evaluate on the next GPS packet, leaving the
        // operator waiting for several ticks after flipping the switch.
        bool switchChanged =
            _lastSteerData.SteerSwitchActive != steerData.SteerSwitchActive ||
            _lastSteerData.WorkSwitchActive != steerData.WorkSwitchActive ||
            _lastSteerData.RemoteButtonPressed != steerData.RemoteButtonPressed;

        _lastSteerData = steerData;
        _lastSteerDataUtc = DateTime.UtcNow;

        // Update vehicle state with actual angle from WAS
        _state.ActualSteerAngle = steerData.ActualSteerAngle;
        _state.SteerSwitchActive = steerData.SteerSwitchActive;
        _state.WorkSwitchActive = steerData.WorkSwitchActive;

        _smartWas?.AddSample(steerData.ActualSteerAngle);

        if (switchChanged)
        {
            Debug.WriteLine(
                $"[AutoSteer] PGN 253 switch change: SteerSwitchActive={steerData.SteerSwitchActive} " +
                $"WorkSwitchActive={steerData.WorkSwitchActive} " +
                $"Remote={steerData.RemoteButtonPressed} Pwm={steerData.PwmDisplay}");

            // Fire the standard cycle event so UI subscribers (wizard gates,
            // config dialog indicators, chart series) see the new switch
            // state without waiting for the next GPS packet.
            NotifyStateUpdated();
        }
    }

    /// <summary>
    /// Process PGN 250 - Sensor Data from module.
    /// Contains pressure/current sensor readings for kickout.
    /// </summary>
    private void ProcessSensorData(byte[] data)
    {
        if (!PgnBuilder.TryParseSensorData(data, out var sensorData))
            return;

        _lastSensorData = sensorData;

        // Map sensor value (0-255) to percentage (0-100)
        _sensorPercent = sensorData.SensorValue / 255.0 * 100.0;

        // Get config for sensor settings
        var config = _configStore.AutoSteer;

        // Check pressure sensor trip point (hydraulic kickout)
        if (config.PressureSensorEnabled && config.PressureTripPoint > 0)
        {
            if (_sensorPercent >= config.PressureTripPoint)
            {
                Disengage();
                Debug.WriteLine($"[AutoSteer] Pressure kickout: {_sensorPercent:F1}% >= {config.PressureTripPoint}%");
            }
        }
        // Check current sensor trip point (motor overload protection)
        else if (config.CurrentSensorEnabled && config.CurrentTripPoint > 0)
        {
            if (_sensorPercent >= config.CurrentTripPoint)
            {
                Disengage();
                Debug.WriteLine($"[AutoSteer] Current kickout: {_sensorPercent:F1}% >= {config.CurrentTripPoint}%");
            }
        }
    }

    public void Engage()
    {
        if (_isEnabled)
        {
            _isEngaged = true;
            _state.IsAutoSteerEngaged = true;
        }
    }

    public void Disengage()
    {
        _isEngaged = false;
        _state.IsAutoSteerEngaged = false;
        // Don't leave the last commanded angle on the wire: PGN 254 keeps broadcasting
        // after disengage (status 0 — the board ignores the angle, but a stale value is
        // still a stale value if anything else reads it).
        _state.SteerAngle = 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Free Drive Mode
    // ═══════════════════════════════════════════════════════════════════════

    public void EnableFreeDrive()
    {
        _state.IsInFreeDriveMode = true;
        _state.FreeDriveSteerAngle = 0;
    }

    public void DisableFreeDrive()
    {
        _state.IsInFreeDriveMode = false;
        _state.FreeDriveSteerAngle = 0;
    }

    public void SetFreeDriveAngle(double angleDegrees)
    {
        // Clamp to safe range (-40 to +40 degrees)
        _state.FreeDriveSteerAngle = Math.Clamp(angleDegrees, -40, 40);
    }

    /// <summary>
    /// Set GPS drift compensation (offset fix). Applied to local coordinates
    /// before guidance and tool position calculations, so tractor + implement
    /// move together. Values in meters.
    /// </summary>
    public void SetDriftCompensation(double driftEasting, double driftNorthing)
    {
        _driftEasting = driftEasting;
        _driftNorthing = driftNorthing;
    }

    public void SendMachineConfig()
    {
        var config = _configStore.Machine;
        var pgn = PgnBuilder.BuildMachineConfigPgn(config);
        _udpService.SendToModules(pgn);
    }

    public void SendMachinePinConfig()
    {
        var config = _configStore.Machine;
        var pgn = PgnBuilder.BuildMachinePinsPgn(config);
        _udpService.SendToModules(pgn);
    }

    /// <summary>
    /// Push the full machine-module setup to the hardware: hydraulic/relay
    /// config (238), the 24-pin relay map (236) and the section dimensions
    /// (235). Mirrors what stock AgOpenGPS sends on config change — without
    /// this a machine board runs on whatever its EEPROM last held.
    /// </summary>
    public void SendMachineConfigAll()
    {
        SendMachineConfig();
        SendMachinePinConfig();
        _udpService.SendToModules(PgnBuilder.BuildSectionDimensionsPgn(_configStore.Tool, _configStore.NumSections));
    }

    public void SetMachineState(ulong sectionBits, bool isInUTurn, byte hydLiftState = 0)
    {
        _state.SectionStates = sectionBits;
        _state.IsInUTurn = isInUTurn;
        _state.HydLiftState = hydLiftState;
    }

    /// <summary>
    /// Set the current track for guidance.
    /// Called by MainViewModel when active track changes.
    /// </summary>
    public void SetCurrentTrack(TrackModel? track)
    {
        _currentTrack = track;
    }

    public void UpdateGuidanceResults(double steerAngle, double crossTrackError)
    {
        _state.SteerAngle = steerAngle;
        _state.CrossTrackError = crossTrackError;
    }

    /// <summary>
    /// Process incoming GPS buffer — entry point for the zero-copy pipeline.
    /// Called directly from the UDP receive callback.
    ///
    /// Phase B C4: this method does parse-and-return only. Coordinate
    /// conversion, guidance, PGN build, tram detection, notify, and latency
    /// recording all moved to the cycle worker, which calls
    /// <see cref="ProcessSimulatedPosition"/> once per tick with the
    /// cycle-computed coordinates and heading. Pre-C4, those steps ran here
    /// on the receive thread, violating the §0 invariant and emitting PGNs
    /// twice per packet (once here, once from the cycle).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void ProcessGpsBuffer(byte[] buffer, int length)
    {
        if (!_isEnabled) return;

        // PERF-05 #7 (autosteer-RX). Cycle = one GPS buffer parsed.
        // Marker .perf_autosteer shared with TX. Emits [AutoSteerRx-PERF].
        bool perf = AgOpenWeb.Models.Diagnostics.DiagFlags.PerfAutoSteer;
        long perfT0 = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        long perfA0 = perf ? GC.GetAllocatedBytesForCurrentThread() : 0;
        try
        {
            // Parse directly into VehicleState (zero-copy). ParseIntoState marks
            // its own parse-timing fields; no BeginNewCycle here because the
            // cycle owns timing now.
            ReadOnlySpan<byte> data = buffer.AsSpan(0, length);
            if (!NmeaParserServiceFast.ParseIntoState(data, ref _state, _configStore))
            {
                _parseFailures++;
                return;
            }

            // Real GPS fix parsed from inbound NMEA — mark the live-GPS gate so the
            // internal simulator stays mutually exclusive with a live source. The
            // sim feeds via GpsService.UpdateGpsData and never reaches this path.
            _gpsService.MarkRealGpsParsed();

            // Publish the parsed fix to GpsService. This is the sole event the
            // cycle worker listens to; everything else runs there.
            PublishGpsData();
        }
        finally
        {
            if (perf)
            {
                _perfRxTicks += System.Diagnostics.Stopwatch.GetTimestamp() - perfT0;
                _perfRxAllocs += GC.GetAllocatedBytesForCurrentThread() - perfA0;
                _perfRxCount++;
                EmitAutoSteerRxIfWindowElapsed();
            }
        }
    }

    /// <summary>
    /// Process simulated position data (bypass NMEA parsing).
    /// Used by simulator which already has parsed GPS data.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void ProcessSimulatedPosition(double latitude, double longitude, double altitude,
        double headingDegrees, double speedMps, int fixQuality, int satellites, double hdop,
        double easting, double northing)
    {
        if (!_isEnabled) return;

        // Mark cycle start
        _state.BeginNewCycle();

        // Set position directly (no parsing needed)
        _state.Latitude = latitude;
        _state.Longitude = longitude;
        _state.Altitude = altitude;
        _state.Heading = headingDegrees;
        _state.HeadingRadians = headingDegrees * Math.PI / 180.0;
        _state.Speed = speedMps;
        _state.FixQuality = fixQuality;
        _state.Satellites = satellites;
        _state.Hdop = hdop;
        _state.Easting = easting;
        _state.Northing = northing;
        _state.GpsValid = true;
        _state.MarkParseEnd(); // Mark parse complete (even though we skipped parsing)

        // Calculate guidance if we have an active track
        if (_currentTrack != null && _currentTrack.Points.Count >= 2)
        {
            CalculateGuidance();
        }

        // Detect tram line wheel positions for PGN 239
        UpdateTramState();

        // PGN sends now happen on the host control loop (#313 commit 4 of 11)
        // at 100 Hz, matching the firmware autosteer task cadence. State mutation
        // here still happens at GPS rate; the loop reads the latest state on
        // each tick.
        _state.MarkPgnSent();
        RecordLatency(_state.TotalLatencyMs);

        // Notify UI (creates snapshot copy)
        NotifyStateUpdated();

        _cycleCount++;
    }

    /// <summary>
    /// Mirror the parsed VehicleState fields onto a fresh GpsData and fire
    /// GpsService.UpdateGpsData — this is what kicks off the cycle worker.
    /// A fresh instance per packet is required: the event handler hands
    /// the reference to a background Task.Run (the cycle worker), and a
    /// reused instance would race with the next receive-thread mutation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PublishGpsData()
    {
        var gpsData = new GpsData
        {
            CurrentPosition = new Position
            {
                Latitude = _state.Latitude,
                Longitude = _state.Longitude,
                Altitude = _state.Altitude,
                Heading = _state.Heading,
                Speed = _state.Speed,
            },
            FixQuality = _state.FixQuality,
            SatellitesInUse = _state.Satellites,
            Hdop = _state.Hdop,
            DifferentialAge = _state.DifferentialAge,
            ImuRoll = _state.Roll,
            ImuPitch = _state.Pitch,
            ImuYawRate = _state.YawRate,
            ImuHeading = _state.ImuHeading,
            ImuValid = _state.ImuValid,
            Timestamp = DateTime.UtcNow,
        };
        _gpsService.UpdateGpsData(gpsData);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateTramState()
    {
        if (_tramLineService != null && _tramLineService.HasTramLines &&
            _configStore.Tram.DisplayMode != Models.Configuration.TramDisplayMode.Off)
        {
            // Use approximate tool position so detection matches implement indicators.
            // Rigid tools sit at Tool.HitchLength (working center); trailing/TBT project
            // from Vehicle.HitchLength (tractor hitch pin) plus the trailing arm.
            var config = _configStore;
            double hitchBase = (config.Tool.IsToolFrontFixed || config.Tool.IsToolRearFixed)
                ? config.Tool.HitchLength
                : config.Vehicle.HitchLength;
            double hitchLen = hitchBase + config.Tool.TrailingHitchLength;
            double toolE = _state.Easting + Math.Sin(_state.HeadingRadians) * hitchLen;
            double toolN = _state.Northing + Math.Cos(_state.HeadingRadians) * hitchLen;

            _state.TramState = _tramLineService.DetectTramWheels(
                new Models.Base.Vec3(toolE, toolN, _state.Heading),
                _state.HeadingRadians, 0.5);
        }
        else
        {
            _state.TramState = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CalculateGuidance()
    {
        // Build guidance input from current state
        _guidanceInput.Track = _currentTrack!;
        _guidanceInput.PivotPosition = new Vec3(_state.Easting, _state.Northing, _state.Heading);
        _guidanceInput.SteerPosition = new Vec3(_state.Easting, _state.Northing, _state.Heading);
        _guidanceInput.FixHeading = _state.HeadingRadians;

        // Calculate guidance
        var output = _guidanceService.CalculateGuidance(_guidanceInput);

        // Update state with guidance results
        _state.CrossTrackError = output.DistanceFromLinePivot;
        _state.SteerAngle = output.SteerAngle;
        _state.IsOnTrack = Math.Abs(output.DistanceFromLinePivot) < 1.0; // Within 1 meter
        _state.MarkGuidanceEnd();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SendPgns()
    {
        // Always send PGN 254 - the engage status is a bit in the status byte
        // This keeps the module informed of current position/speed even when not engaged
        var pgn = PgnBuilder.BuildAutoSteerPgn(ref _state);
        _udpService.SendToModules(pgn);

        // Send PGN 239 (Machine Data) - section control, speed, tramline state
        // Sent every GPS cycle so machine module has current section/speed info
        var machinePgn = PgnBuilder.BuildMachinePgn(ref _state,
            uturn: _state.IsInUTurn ? (byte)1 : (byte)0,
            hydLift: _state.HydLiftState,
            tram: _state.TramState,
            geoStop: _state.GeoStopState);
        _udpService.SendToModules(machinePgn);

        // Send PGN 229 (64-section on/off + L/R tool-tip speeds) alongside 239
        // unconditionally — stock AOG sends it every section frame, and on-wire
        // listeners (rate controllers) key on it regardless of section count.
        var sections64Pgn = PgnBuilder.BuildSection64Pgn(ref _state, _configStore.ActualToolWidth / 2.0);
        _udpService.SendToModules(sections64Pgn);

        // PGN 100 corrected position at ~10 Hz (every 10th control tick) for
        // ecosystem apps on the loopback plane — RateController paints its
        // application maps from it. Skipped without a fix.
        if (++_correctedPosDivider >= 10)
        {
            _correctedPosDivider = 0;
            if (_state.GpsValid && (_state.Latitude != 0 || _state.Longitude != 0))
                _udpService.SendToModules(PgnBuilder.BuildCorrectedPositionPgn(_state.Latitude, _state.Longitude));
        }
    }

    private int _correctedPosDivider;

    /// <summary>
    /// Build and send PGN 254 + PGN 239 from the current vehicle state.
    /// Called by the host control loop (#313) on every tick (100 Hz) so the
    /// firmware autosteer task — which also runs at 100 Hz — sees a fresh
    /// PGN every cycle. Reads <c>_state</c> without locking; concurrent
    /// updates from the GPS pipeline thread may produce a torn read on
    /// individual fields, which the firmware tolerates by acting on
    /// whatever bits it has at its next loop iteration.
    /// </summary>
    public void SendPgnsForControlTick()
    {
        if (!_isEnabled) return;
        // PERF-05 #7 (autosteer-TX). Cycle = one PGN-send tick (outbound
        // steering / config / hello). Emits [AutoSteerTx-PERF].
        bool perf = AgOpenWeb.Models.Diagnostics.DiagFlags.PerfAutoSteer;
        long perfT0 = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        long perfA0 = perf ? GC.GetAllocatedBytesForCurrentThread() : 0;
        try { SendPgns(); }
        finally
        {
            if (perf)
            {
                _perfTxTicks += System.Diagnostics.Stopwatch.GetTimestamp() - perfT0;
                _perfTxAllocs += GC.GetAllocatedBytesForCurrentThread() - perfA0;
                _perfTxCount++;
                EmitAutoSteerTxIfWindowElapsed();
            }
        }
    }

    // PERF-05 #7 accumulators (gated by DiagFlags.PerfAutoSteer).
    private long _perfRxTicks, _perfRxAllocs;
    private int _perfRxCount;
    private DateTime _perfRxWindowStart = DateTime.UtcNow;
    private long _perfTxTicks, _perfTxAllocs;
    private int _perfTxCount;
    private DateTime _perfTxWindowStart = DateTime.UtcNow;

    private void EmitAutoSteerRxIfWindowElapsed()
    {
        var elapsed = (DateTime.UtcNow - _perfRxWindowStart).TotalSeconds;
        if (elapsed < 1.0 || _perfRxCount == 0) return;
        double ticksPerUs = System.Diagnostics.Stopwatch.Frequency / 1_000_000.0;
        Console.WriteLine(
            $"[AutoSteerRx-PERF] cycles={_perfRxCount}"
            + $" us/cycle={(_perfRxTicks / ticksPerUs / _perfRxCount):F1}"
            + $" alloc/cycle={(_perfRxAllocs / _perfRxCount)}B"
            + $" total_us={(long)(_perfRxTicks / ticksPerUs)}"
            + $" total_alloc={_perfRxAllocs}B"
            + $" window={elapsed:F2}s");
        _perfRxTicks = 0; _perfRxAllocs = 0; _perfRxCount = 0;
        _perfRxWindowStart = DateTime.UtcNow;
    }

    private void EmitAutoSteerTxIfWindowElapsed()
    {
        var elapsed = (DateTime.UtcNow - _perfTxWindowStart).TotalSeconds;
        if (elapsed < 1.0 || _perfTxCount == 0) return;
        double ticksPerUs = System.Diagnostics.Stopwatch.Frequency / 1_000_000.0;
        Console.WriteLine(
            $"[AutoSteerTx-PERF] cycles={_perfTxCount}"
            + $" us/cycle={(_perfTxTicks / ticksPerUs / _perfTxCount):F1}"
            + $" alloc/cycle={(_perfTxAllocs / _perfTxCount)}B"
            + $" total_us={(long)(_perfTxTicks / ticksPerUs)}"
            + $" total_alloc={_perfTxAllocs}B"
            + $" window={elapsed:F2}s");
        _perfTxTicks = 0; _perfTxAllocs = 0; _perfTxCount = 0;
        _perfTxWindowStart = DateTime.UtcNow;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordLatency(double latencyMs)
    {
        _latencySamples[_latencySampleIndex] = latencyMs;
        _latencySampleIndex = (_latencySampleIndex + 1) % _latencySamples.Length;
    }

    // ---- Work switch resolution (polarity + momentary latch) ----------------
    // The raw bit is "pin pulled low". A maintained switch's state IS the level
    // (equal to ActiveLow = closed = working). A momentary button toggles a
    // latch on each press edge instead.
    private bool _workPressedPrev;
    private bool _workLatchOn;

    private bool ResolveWorkSwitchOn()
    {
        var tool = _configStore.Tool;
        bool pinLow = _state.MachineWorkSwitchPresent
            ? _state.MachineWorkSwitchActive : _state.WorkSwitchActive;
        bool pressed = pinLow == tool.IsWorkSwitchActiveLow;
        if (tool.IsWorkSwitchMomentary)
        {
            if (pressed && !_workPressedPrev) _workLatchOn = !_workLatchOn;
            _workPressedPrev = pressed;
            return _workLatchOn;
        }
        _workPressedPrev = pressed;
        _workLatchOn = pressed; // momentary mode starts from the live level
        return pressed;
    }

    private void NotifyStateUpdated()
    {
        var snapshot = CreateSnapshot();
        _latestSnapshot = snapshot;
        StateUpdated?.Invoke(this, snapshot);
    }

    private VehicleStateSnapshot CreateSnapshot()
    {
        return new VehicleStateSnapshot
        {
            Latitude = _state.Latitude,
            Longitude = _state.Longitude,
            Altitude = _state.Altitude,
            Speed = _state.Speed,
            Heading = _state.Heading,
            FixQuality = _state.FixQuality,
            Satellites = _state.Satellites,
            Hdop = _state.Hdop,
            DifferentialAge = _state.DifferentialAge,
            Roll = _state.Roll,
            Pitch = _state.Pitch,
            YawRate = _state.YawRate,
            Easting = _state.Easting,
            Northing = _state.Northing,
            CrossTrackError = _state.CrossTrackError,
            SteerAngle = _state.SteerAngle,
            DistanceToTurn = _state.DistanceToTurn,
            DistanceToEnd = _state.DistanceToEnd,
            IsOnTrack = _state.IsOnTrack,
            IsAutoSteerEngaged = _state.IsAutoSteerEngaged,
            SectionStates = _state.SectionStates,
            MasterSectionOn = _state.MasterSectionOn,
            TramState = _state.TramState,
            HydLiftState = _state.HydLiftState,
            GeoStopState = _state.GeoStopState,
            // The machine board's own work switch wins when that hardware
            // exists; otherwise the steer board's input (stock AOG wiring).
            WorkSwitchActive = _state.MachineWorkSwitchPresent
                ? _state.MachineWorkSwitchActive : _state.WorkSwitchActive,
            WorkSwitchOn = ResolveWorkSwitchOn(),
            TotalLatencyMs = _state.TotalLatencyMs,
            ParseLatencyMs = _state.ParseLatencyMs,
            GuidanceLatencyMs = _state.GuidanceLatencyMs,
            GpsValid = _state.GpsValid,
            GuidanceValid = _state.GuidanceValid,
            ImuValid = _state.ImuValid
        };
    }

    public AutoSteerLatencyMetrics GetLatencyMetrics()
    {
        double sum = 0;
        double max = 0;
        int count = 0;

        for (int i = 0; i < _latencySamples.Length; i++)
        {
            if (_latencySamples[i] > 0)
            {
                sum += _latencySamples[i];
                if (_latencySamples[i] > max) max = _latencySamples[i];
                count++;
            }
        }

        double avg = count > 0 ? sum / count : 0;
        int lastIndex = (_latencySampleIndex - 1 + _latencySamples.Length) % _latencySamples.Length;

        return new AutoSteerLatencyMetrics
        {
            LastTotalLatencyMs = _latencySamples[lastIndex],
            AvgTotalLatencyMs = avg,
            MaxTotalLatencyMs = max,
            LastParseLatencyMs = _state.ParseLatencyMs,
            LastGuidanceLatencyMs = _state.GuidanceLatencyMs,
            CycleCount = _cycleCount,
            ParseFailures = _parseFailures
        };
    }
}
