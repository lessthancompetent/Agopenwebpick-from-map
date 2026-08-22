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
using System.Collections.Generic;
using System.Diagnostics;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Coverage;
using AgOpenWeb.Models.Headland;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Section;

/// <summary>
/// Manages automatic section on/off based on coverage, boundaries, headlands,
/// and look-ahead calculations.
///
/// Based on AgOpenGPS section control logic from Sections.Designer.cs and CSection.cs.
/// </summary>
public class SectionControlService : ISectionControlService
{
    private readonly IToolPositionService _toolPositionService;
    private readonly ICoverageMapService _coverageMapService;
    private readonly ApplicationState _state;
    private readonly ConfigurationStore _configStore;

    private readonly SectionControlState[] _sectionStates;
    private SectionMasterState _masterState = SectionMasterState.Off;

    // Timing in seconds. The state machine honors *only* what the user
    // configures via Tool.LookAheadOnSetting / LookAheadOffSetting — no
    // built-in floors. Per-tick math is rate-independent via TickHz.
    //
    // MAPPING_ON_DELAY_SECONDS = 0: the section's own LookAheadOn timing
    //   (configured by the user) already gates the IsOn flip; an extra
    //   mapping-side debounce would just leave an unsprayed gap.
    // MAPPING_OFF_DELAY_SECONDS: kept non-zero so a brief shouldBeOff /
    //   shouldBeOn flicker doesn't tear the strip — UpdateMapping continues
    //   painting through the debounce since IsMappingOn is still true.
    private const double MAPPING_ON_DELAY_SECONDS = 0.0;
    private const double MAPPING_OFF_DELAY_SECONDS = 0.2;

    /// <summary>
    /// Rate at which <see cref="Update"/> is being called. Defaults to
    /// 10 Hz (legacy GPS-cycle cadence). The host control loop (#313)
    /// sets this to 100 Hz for sub-frame section control. Used to convert
    /// seconds-based delays into integer tick thresholds.
    /// </summary>
    public double TickHz { get; set; } = 10.0;

    // Ticks of "just finished a turn" grace: the turn EARLY-COMPLETES ~4m
    // before the path end, so the plan disappears while the trailing tool is
    // still inside the headland - the turn-exit paint override needs to stay
    // armed briefly after the plan ends or it never fires.
    private int _planGraceTicks;

    /// <summary>When a planned path is known ahead (an armed/executing U-turn),
    /// look-ahead anticipation walks the PLAN instead of dead-reckoning from
    /// tool heading — the trailing tool crabs through a turn, so heading
    /// projection points the sample sideways and anticipation collapses to ~0
    /// exactly when the valve-open lead matters most. Returns (path, progress
    /// index) or null when no plan is active.</summary>
    public Func<(IReadOnlyList<Vec3> Path, int Index)?>? PlannedPathProvider { get; set; }

    // Section ON/OFF phase ticks are derived from turnOnPhaseSec /
    // turnOffPhaseSec (which already include the SECTION_ON_DELAY_SECONDS /
    // 0.1 s minimum floors for debounce); see UpdateSection. Mapping
    // delays are separate concerns.
    private int MappingOnDelayTicks => (int)Math.Round(MAPPING_ON_DELAY_SECONDS * TickHz);
    private int MappingOffDelayTicks => (int)Math.Round(MAPPING_OFF_DELAY_SECONDS * TickHz);

    // Default coverage overlap threshold (used if MinCoverage is 0)
    private const double DEFAULT_COVERAGE_THRESHOLD = 0.70; // 70%

    // OFF threshold: section stays on until its swath is essentially fully
    // covered, so a pass over a partly-painted strip fills the holes instead
    // of turning the section off as soon as it reads "mostly covered". A
    // section-wide threshold tied to MinCoverage caused #348 (gaps don't
    // fill on subsequent passes); decoupling the OFF decision from
    // MinCoverage and pinning it near full coverage is the principled fix.
    // 0.99 (with 0.01 tolerance for FP / pixel noise around 100 %) keeps the
    // section on through any meaningful gap. MarkCellCovered is already
    // idempotent so over-painting covered cells is a no-op.
    private const double COVERAGE_OFF_THRESHOLD = 0.99;

    // Floor on the forward distance from section center to the look-on /
    // look-off sample points. lookAheadDistance = speed × LookAheadSetting,
    // so at slow speed and/or LookAheadSetting=0 (the default) it goes to
    // zero — the look-on/off check then samples cells at the section center,
    // i.e. inside the section's own freshly-painted swath. shouldBeOff fires
    // off the section's own coverage, IsOn flips off, paint stops; the next
    // tick the section has crept past its own paint slightly, shouldBeOn
    // fires, IsOn flips back on. This is the slow-speed flicker in #345.
    // 0.3 m (3 detection cells) is enough to clear the painted swath
    // longitudinally without changing behavior at normal speeds, where
    // speed × time already exceeds it.
    private const double MIN_LOOKAHEAD_FORWARD_DISTANCE_METERS = 0.3;

    // Minimum distance (squared) between coverage points to reduce edge jaggedness
    // At 10Hz and 10 kph (2.78 m/s), vehicle moves ~0.28m per update
    // Using 0.12m threshold ensures we add points frequently enough for accuracy
    // but filter out GPS jitter that causes jagged edges
    private const double MIN_COVERAGE_POINT_DISTANCE_SQ = 0.12 * 0.12; // 0.0144 m²

    // Last coverage point position per zone (for minimum distance filtering)
    private readonly Dictionary<int, Vec2> _lastCoveragePosition = new();

    // Yaw rate tracking for curve-following coverage margin
    private double _previousHeading = double.NaN;
    private double _previousVehicleHeading = double.NaN;
    private double _yawRate = 0; // smoothed yaw rate in radians per update cycle (positive = turning right)
    private double _instantYawRate = 0; // instantaneous (unsmoothed) yaw rate for threshold checks
    private double _vehicleYawRate = 0; // vehicle (tractor) yaw rate
    private double _toolVehicleHeadingDiff = 0; // difference between tool and vehicle heading (for trailed implements)
    private const double YAW_RATE_SMOOTHING = 0.3; // Smoothing factor (0-1, lower = smoother)

    // Performance timing (exposed for consolidated logging)
    private static readonly Stopwatch _sectionSw = new();
    private static double _totalBoundaryMs;
    private static double _totalHeadlandMs;
    private static double _totalCoverageMs;
    private static int _sectionUpdateCounter;

    // Public accessors for timing (read from MainViewModel)
    public static double LastBoundaryMs => _totalBoundaryMs;
    public static double LastHeadlandMs => _totalHeadlandMs;
    public static double LastCoverageCheckMs => _totalCoverageMs;

    public IReadOnlyList<SectionControlState> SectionStates => _sectionStates;
    public SectionMasterState MasterState
    {
        get => _masterState;
        set
        {
            if (_masterState != value)
            {
                _masterState = value;
                if (value == SectionMasterState.Off)
                {
                    TurnAllOff();
                }
            }
        }
    }

    public bool IsAnySectionOn
    {
        get
        {
            for (int i = 0; i < NumSections; i++)
            {
                if (_sectionStates[i].IsOn) return true;
            }
            return false;
        }
    }

    public int NumSections => _configStore.NumSections;

    public event EventHandler<SectionStateChangedEventArgs>? SectionStateChanged;

    public SectionControlService(
        IToolPositionService toolPositionService,
        ICoverageMapService coverageMapService,
        ApplicationState state,
        ConfigurationStore configStore)
    {
        _toolPositionService = toolPositionService;
        _coverageMapService = coverageMapService;
        _state = state;
        _configStore = configStore;

        // Initialize section states
        _sectionStates = new SectionControlState[ToolConfig.MaxSections];
        for (int i = 0; i < _sectionStates.Length; i++)
        {
            _sectionStates[i] = new SectionControlState { Index = i };
        }

        // Calculate initial section positions
        RecalculateSectionPositions();

        // Listen for configuration changes to recalculate section positions
        _configStore.PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName == nameof(ConfigurationStore.NumSections) ||
                e.PropertyName == nameof(ConfigurationStore.Tool))
            {
                RecalculateSectionPositions();
            }
        };

        // Also listen for ToolConfig changes (section width changes don't trigger store-level event)
        _configStore.Tool.PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName == nameof(ToolConfig.SectionWidths) ||
                e.PropertyName == nameof(ToolConfig.TotalSectionWidth))
            {
                RecalculateSectionPositions();
            }
        };
    }

    public void Update(Vec3 toolPosition, double toolHeading, double vehicleHeading, double speed)
    {
        var tool = _configStore.Tool;
        int numSections = NumSections;

        // Calculate tool vs vehicle heading difference (for trailed implements)
        // When tool is "catching up" after a turn, this difference is large
        double headingDiff = toolHeading - vehicleHeading;
        // Handle wrap-around at ±π
        if (headingDiff > Math.PI)
            headingDiff -= 2 * Math.PI;
        else if (headingDiff < -Math.PI)
            headingDiff += 2 * Math.PI;
        _toolVehicleHeadingDiff = headingDiff;

        // Calculate vehicle yaw rate (to distinguish turns from catch-up)
        if (!double.IsNaN(_previousVehicleHeading))
        {
            double vehicleHeadingDelta = vehicleHeading - _previousVehicleHeading;
            if (vehicleHeadingDelta > Math.PI)
                vehicleHeadingDelta -= 2 * Math.PI;
            else if (vehicleHeadingDelta < -Math.PI)
                vehicleHeadingDelta += 2 * Math.PI;
            _vehicleYawRate = vehicleHeadingDelta;
        }
        _previousVehicleHeading = vehicleHeading;

        // Calculate yaw rate (both instantaneous and smoothed)
        // Instantaneous is used for threshold checks (especially for trailed implements)
        // Smoothed is used for curve-following adjustments
        if (!double.IsNaN(_previousHeading))
        {
            double headingDelta = toolHeading - _previousHeading;
            // Handle wrap-around at ±π
            if (headingDelta > Math.PI)
                headingDelta -= 2 * Math.PI;
            else if (headingDelta < -Math.PI)
                headingDelta += 2 * Math.PI;

            // Store instantaneous rate for threshold checks
            _instantYawRate = headingDelta;

            // Exponential smoothing for curve-following: new = old * (1-α) + measured * α
            _yawRate = _yawRate * (1 - YAW_RATE_SMOOTHING) + headingDelta * YAW_RATE_SMOOTHING;
        }
        _previousHeading = toolHeading;

        // Check if speed is below cutoff - turn off AUTO sections but keep
        // MANUAL ON sections active so coverage doesn't gap on stop/restart.
        // SlowSpeedCutoff is stored in km/h (matching the UI); speed is m/s.
        double slowSpeedCutoffMps = tool.SlowSpeedCutoff / 3.6;
        bool isSlowSpeedCutoff = speed < slowSpeedCutoffMps;
        if (isSlowSpeedCutoff)
        {
            for (int i = 0; i < numSections; i++)
            {
                if (_sectionStates[i].ButtonState != SectionButtonState.On)
                    UpdateSectionOff(i);
            }
            _yawRate = 0; // Reset when stopped
            _instantYawRate = 0;
            _vehicleYawRate = 0;
            // Don't return early - manual sections still need UpdateSectionOn
        }

        // Reset timing accumulators
        _totalBoundaryMs = 0;
        _totalHeadlandMs = 0;
        _totalCoverageMs = 0;

        // Update each section. During slow-speed cutoff, Auto sections were
        // already cleared above; skip them here so UpdateSection's look-ahead
        // doesn't re-arm SectionOnRequest at speed=0 (lookOnDist collapses to
        // the section center, which evaluates as shouldBeOn inside the
        // boundary, leaving the section pinned in TURNING_ON forever).
        for (int i = 0; i < numSections; i++)
        {
            if (isSlowSpeedCutoff && _sectionStates[i].ButtonState == SectionButtonState.Auto)
                continue;
            UpdateSection(i, toolPosition, toolHeading, speed);
        }

        // Flush coverage updates after all sections processed (fires event once, not 16 times)
        _coverageMapService.FlushCoverageUpdate();

        // Timing logged from MainViewModel
        _sectionUpdateCounter++;

        // Fire state changed event
        SectionStateChanged?.Invoke(this, new SectionStateChangedEventArgs
        {
            SectionIndex = -1, // All sections
            IsOn = IsAnySectionOn,
            IsMappingOn = IsAnyMappingOn(),
            SectionBits = GetSectionBits()
        });
    }

    /// <summary>
    /// Update a single section's state
    /// </summary>
    private void UpdateSection(int index, Vec3 toolPosition, double toolHeading, double speed)
    {
        var section = _sectionStates[index];
        var tool = _configStore.Tool;

        // Get section world position
        var (leftEdge, rightEdge) = GetSectionWorldPosition(index, toolPosition, toolHeading);
        var sectionCenter = new Vec2(
            (leftEdge.Easting + rightEdge.Easting) / 2,
            (leftEdge.Northing + rightEdge.Northing) / 2
        );

        // Check manual override states
        if (section.ButtonState == SectionButtonState.Off)
        {
            UpdateSectionOff(index);
            return;
        }

        if (section.ButtonState == SectionButtonState.On)
        {
            UpdateSectionOn(index, leftEdge, rightEdge, toolHeading);
            return;
        }

        // Auto mode - check boundary/overlap conditions.
        // Look-ahead distances and TURNING_ON / TURNING_OFF phase durations
        // come straight from user config. The phase delay exactly cancels the
        // projection, so the physical IsOn flip lands on the boundary edge.
        // With both settings at 0, no anticipation and no wait — section
        // flips on the first tick that shouldBe(On|Off) becomes true.
        // Clamp: a NEGATIVE look-ahead makes the "ahead" sample point land BEHIND
        // the section — the behind-point and the here-point then disagree at any
        // zone edge (headland/boundary/coverage), shouldBeOn and shouldBeOff go
        // true simultaneously, and with 1-tick debounces the section chatters
        // on/off every tick (real relay/work-signal chatter, seen live with a
        // stored -3 s setting). Look-ahead is forward-only by definition.
        double turnOnPhaseSec = Math.Max(0, tool.LookAheadOnSetting);
        double turnOffPhaseSec = Math.Max(0, tool.LookAheadOffSetting);
        // Floor the FORWARD distance only (the time-based phase debounce above
        // stays at user config). This keeps the sample point past the section's
        // own swath at slow speed; at normal speeds speed × time exceeds the
        // floor and the user's anticipation is preserved exactly. See the
        // MIN_LOOKAHEAD_FORWARD_DISTANCE_METERS comment for the full reason.
        double lookAheadOnDist = Math.Max(speed * turnOnPhaseSec, MIN_LOOKAHEAD_FORWARD_DISTANCE_METERS);
        double lookAheadOffDist = Math.Max(speed * turnOffPhaseSec, MIN_LOOKAHEAD_FORWARD_DISTANCE_METERS);

        // Calculate section half-width for segment-based checks
        double halfWidth = (section.PositionRight - section.PositionLeft) / 2.0;

        // Include coverage margin in boundary check - coverage is recorded at expanded positions,
        // so we must check the ACTUAL coverage area, not just the section width
        double coverageMargin = tool.CoverageMarginMeters > 0 ? tool.CoverageMarginMeters : 0;
        double halfWidthWithMargin = halfWidth + coverageMargin;

        double sectionLateral = (section.PositionLeft + section.PositionRight) / 2.0;

        // Planned turn path (armed only while a turn is executing). Invoked once
        // per section eval; remaining arc length gates the ON arming below and
        // pushes the ON ground-samples past the exit so they don't slide across
        // the headland edge with speed changes mid-arc.
        var plan = PlannedPathProvider?.Invoke();
        double? planRemaining = null;
        if (plan is { Path.Count: >= 2 } p2)
        {
            planRemaining = RemainingPlannedPath(p2.Path, p2.Index);
            _planGraceTicks = (int)(5.0 * TickHz);
        }
        else if (_planGraceTicks > 0)
        {
            _planGraceTicks--;
        }
        double onSampleDist = planRemaining is { } r
            ? Math.Max(lookAheadOnDist, r + 2.0)
            : lookAheadOnDist;

        // Look-ahead samples: along the planned turn path when one is active
        // (see LookAheadPoint), else curved projection from tool heading.
        var onCheckPoint = LookAheadPoint(sectionCenter, toolHeading, onSampleDist, speed, sectionLateral);
        var offCheckPoint = LookAheadPoint(sectionCenter, toolHeading, lookAheadOffDist, speed, sectionLateral);

        // Check boundary conditions using segment-based detection
        // Use halfWidthWithMargin for current position to prevent coverage outside boundary
        _sectionSw.Restart();
        var currentBoundaryResult = GetSegmentBoundaryStatus(sectionCenter, toolHeading, halfWidthWithMargin);
        var lookOnBoundaryResult = GetSegmentBoundaryStatus(onCheckPoint, toolHeading, halfWidth);
        var lookOffBoundaryResult = GetSegmentBoundaryStatus(offCheckPoint, toolHeading, halfWidth);
        _totalBoundaryMs += _sectionSw.Elapsed.TotalMilliseconds;

        // Use strict threshold for current position - section must be fully inside to spray
        // This prevents spraying outside boundary when implement swings during turns
        const double BOUNDARY_THRESHOLD_STRICT = 0.95; // 95% inside required to be "in boundary"
        const double BOUNDARY_THRESHOLD_LOOKAHEAD = 0.50; // 50% for look-ahead anticipation
        bool isInBoundary = currentBoundaryResult.InsidePercent >= BOUNDARY_THRESHOLD_STRICT;
        bool lookOnInBoundary = lookOnBoundaryResult.InsidePercent >= BOUNDARY_THRESHOLD_LOOKAHEAD;
        bool lookOffInBoundary = lookOffBoundaryResult.InsidePercent >= BOUNDARY_THRESHOLD_LOOKAHEAD;

        // Check headland conditions
        // Use speed-dependent look-ahead so coverage edges land exactly on
        // the headland line. The look-ahead distance must cancel the wait
        // time between shouldBeOn/Off and the actual IsOn flip:
        //   ON:  lookahead = speed * turnOnPhaseSec  (cancels TURNING_ON wait)
        //   OFF: lookahead = speed * turnOffPhaseSec (cancels TURNING_OFF wait)
        // With MAPPING_ON_DELAY = 0, the TURNING phases are the only wait,
        // so this gives strip start/end at the line with no gap and no
        // overspray when all timings are 0.
        double headlandOnLookAhead = planRemaining is { } r2
            ? Math.Max(speed * turnOnPhaseSec, r2 + 2.0)
            : speed * turnOnPhaseSec;
        double headlandOffLookAhead = speed * turnOffPhaseSec;
        var headlandOnCheckPoint = LookAheadPoint(sectionCenter, toolHeading, headlandOnLookAhead, speed, sectionLateral);
        var headlandOffCheckPoint = LookAheadPoint(sectionCenter, toolHeading, headlandOffLookAhead, speed, sectionLateral);

        _sectionSw.Restart();
        // Edge-aware headland test (bug A): a section counts as "in headland" only when
        // its WHOLE swath is in the band, so a section straddling the headland line
        // stays ON and paints its field half right up to the line — matching the
        // swath-based boundary check instead of the crude single-centre-point test that
        // cut the section ~half its width short of the line on a side-headland pass.
        // Uses the section's own edge offset (from GetSectionWorldPosition) applied to
        // the current position and each look-ahead sample point. For a pass PERPENDICULAR
        // to the headland the swath is parallel to the line and both edges cross together,
        // so this is identical to the old centre test — only angled/side passes change.
        // Headland as VIRTUAL COVERAGE (user model / bug B): when "Off in headland"
        // is on, the band counts toward each section's coverage — the section turns
        // off on its OWN combined paint+headland coverage, never a whole-tool shutoff,
        // and a section that hasn't reached the line keeps painting. HeadlandFraction
        // is the share of the section's swath sitting in the band, sampled across the
        // section's own width (its edge offset from GetSectionWorldPosition). Returns 0
        // when headland section-control / the headland toggle is off (IsPointInHeadland
        // honours both), so with the headland off behaviour is pure coverage/boundary.
        double edgeOffE = rightEdge.Easting - sectionCenter.Easting;
        double edgeOffN = rightEdge.Northing - sectionCenter.Northing;
        double HeadlandFraction(Vec2 c)
        {
            const int SAMPLES = 6;
            int inBand = 0;
            for (int i = 0; i <= SAMPLES; i++)
            {
                double t = (double)i / SAMPLES * 2.0 - 1.0; // -1 .. +1 across the swath
                if (IsPointInHeadland(new Vec2(c.Easting + edgeOffE * t, c.Northing + edgeOffN * t)))
                    inBand++;
            }
            return (double)inBand / (SAMPLES + 1);
        }
        double hfCurrent = HeadlandFraction(sectionCenter);
        double hfLookOn = HeadlandFraction(headlandOnCheckPoint);
        double hfLookOff = HeadlandFraction(headlandOffCheckPoint);
        bool isInHeadland = hfCurrent >= 0.5;      // display state (majority of swath in band)
        bool lookOnInHeadland = hfLookOn >= 0.5;   // display state
        _totalHeadlandMs += _sectionSw.Elapsed.TotalMilliseconds;

        // Bitmap-based coverage check is O(width / cellSize) bit reads per section
        // (~80 reads for an 8 m boom at 10 cm cells). Cheap enough to run every tick;
        // the prior 150 ms throttle was a holdover from polygon-based coverage and
        // produced 15 ticks of stale-cache lag at 100 Hz, leaving a visible gap when
        // exiting previously-covered area (section stays cached-OFF past the edge).
        _sectionSw.Restart();
        var (currentCoverage, lookOnCoverage, lookOffCoverage) = _coverageMapService.GetSegmentCoverageMulti(
            sectionCenter,
            toolHeading,
            halfWidth,
            lookAheadOnDist,
            lookAheadOffDist);
        _totalCoverageMs += _sectionSw.Elapsed.TotalMilliseconds;

        // The ON and OFF decisions use different thresholds — by design, not
        // for noise hysteresis (the #345 explicit margin is gone, replaced
        // by the natural separation below).
        //   ON threshold = MinCoverage. User's "I want X % overlap" setting
        //     gates turning the section on: don't bother firing if the look-
        //     ahead area is already at or above the user's accepted overlap.
        //   OFF threshold = COVERAGE_OFF_THRESHOLD (≈ 1.0). Section stays on
        //     while ANY meaningful cell in its swath is uncovered, so a pass
        //     over a partly-painted strip fills the gaps (#348). Per-cell
        //     idempotency in MarkCellCovered handles the already-covered
        //     cells without over-paint. The natural separation between this
        //     and the ON threshold (≈ 30 points at default MinCoverage) is
        //     the hysteresis that prevents slow-speed flicker (#345).
        double coverageOnThreshold = tool.MinCoverage > 0
            ? tool.MinCoverage / 100.0
            : DEFAULT_COVERAGE_THRESHOLD;
        double coverageOffThreshold = COVERAGE_OFF_THRESHOLD;
        // Fold the headland-band share into each look-ahead coverage: the band counts
        // exactly like already-painted ground, so the section decides on its OWN
        // combined coverage (per-section, independent) with no separate headland
        // shut-off term. With "Off in headland" off the fraction is 0, so a section
        // over unpainted ground stays active even inside the headland.
        bool lookOnCovered = Math.Min(1.0, lookOnCoverage.CoveragePercent + hfLookOn) >= coverageOnThreshold;
        bool lookOffCovered = Math.Min(1.0, lookOffCoverage.CoveragePercent + hfLookOff) >= coverageOffThreshold;
        // Current-position headland backstop: during a U-turn the planned-path
        // look-ahead samples BEYOND the headland, so without a here-and-now check a
        // section would paint straight through the band it is physically crossing.
        bool currentInHeadlandFull = hfCurrent >= coverageOffThreshold;

        // Store coverage percentage for potential UI display
        section.CoveragePercent = currentCoverage.CoveragePercent;

        // Update section state tracking
        section.IsInBoundary = isInBoundary;
        section.IsInHeadland = isInHeadland;
        section.IsLookOnInHeadland = lookOnInHeadland;

        // CRITICAL: If current position is outside boundary, section must be OFF immediately.
        // This prevents spraying outside the field when trailing implements swing out during turns.
        // Look-ahead is for anticipating boundaries, not overriding physical position.
        if (!isInBoundary)
        {
            UpdateSectionOff(index);
            return;
        }

        // NOTE: there was a second, BINARY check here — "both expanded edge points
        // must be inside the boundary" — that killed every fence-adjacent pass:
        // a correct headland lap runs its swath edge exactly ON the fence, so the
        // margin-expanded edge point sits a few cm outside and the section was
        // forced instantly off EVERY tick of the whole lap. The headland lap
        // could never paint in auto, and delay/look-ahead settings appeared dead
        // along the fence (this path bypasses the state machine entirely). The
        // strict segment check above already evaluates the margin-expanded swath
        // proportionally (95% inside required) — a genuinely overhanging swath
        // still cuts off; a centimetre sliver no longer blanks the lap.

        // Determine if section should be on.
        // The actuator-delay compensation built into lookOnDist means the valve receives
        // the OPEN command exactly the actuator's open-time before reaching clear ground.
        // The SECTION_ON_DELAY debounce inside the state machine protects against brief
        // false positives — by the time it expires, the section has moved enough that
        // a transient blip cannot reach IsOn=true unless lookOnCovered stays false.
        bool shouldBeOn = !lookOnCovered      // Not already covered (paint+headland) at look-ON
                       && lookOnInBoundary;   // Inside boundary at look-ahead

        // While a U-turn is executing the arming must be monotonic: gate the ON
        // request on the REMAINING planned-path length (relay leads the exit by
        // exactly the configured look-ahead time), with the ground check taken
        // at a stable point past the exit. The raw walked sample slides with
        // speed changes and flickered across the headland edge, restarting the
        // phase and landing the paint seconds late.
        if (planRemaining is { } rem)
            shouldBeOn = shouldBeOn && rem <= lookAheadOnDist;
        // Belt: once armed during a turn, hold the request through residual
        // flickers - the phase timer still times the landing on the exit edge.
        if (!shouldBeOn && planRemaining.HasValue && section.SectionOnRequest && !section.IsOn)
            shouldBeOn = true;

        // Determine if section should be off
        bool shouldBeOff = lookOffCovered        // Already covered (paint + headland) at look-ahead
                        || !lookOffInBoundary    // Outside boundary at look-ahead
                        || currentInHeadlandFull; // physically fully in the headland now (U-turn backstop)

        // Apply state transitions with timing
        if (shouldBeOn && !section.IsOn)
        {
            section.SectionOnRequest = true;
            section.SectionOffRequest = false;
            section.SectionOnTimer++;
            section.SectionOffTimer = 0;

            // TURNING_ON phase: duration must match the look-ahead anticipation
            // (turnOnPhaseSec, in seconds, computed above) so the projection
            // exactly cancels the phase delay and the physical IsOn flip lands
            // on the boundary edge. Derive ticks from the same seconds value
            // — *not* from LookAheadOnSetting alone — otherwise the floor
            // (SECTION_ON_DELAY_SECONDS) doesn't carry into the wait time.
            // Use >= so the flip happens on the tick that completes the
            // debounce; > would add one extra tick of wait (visible as a
            // tick-period of late spray at any tick rate).
            int turnOnPhaseTicks = Math.Max(1, (int)Math.Round(turnOnPhaseSec * TickHz));

            // Turn-exit override: the phase timer times the landing from the
            // arming moment, but arming converts the time-lead to distance at
            // the TURN-SLOWED speed - accelerating out of the arc leaves the
            // lead short and the paint started seconds past the line. During a
            // turn, the physical exit is authoritative: the section has left
            // the headland onto uncovered ground, so paint NOW. Normal driving
            // (no active plan) keeps the pure timer semantics.
            bool turnExitArrived = (planRemaining.HasValue || _planGraceTicks > 0)
                && !isInHeadland
                && currentCoverage.CoveragePercent < coverageOnThreshold;

            if (section.SectionOnTimer >= turnOnPhaseTicks || turnExitArrived)
            {
                section.IsOn = true;
                section.SectionOnRequest = false;
                StartMapping(index, leftEdge, rightEdge, toolHeading);
            }
        }
        else if (shouldBeOff && section.IsOn)
        {
            section.SectionOffRequest = true;
            section.SectionOnRequest = false;
            section.SectionOffTimer++;
            section.SectionOnTimer = 0;

            // TURNING_OFF phase models the valve close time. Section is still
            // physically applying spray during this transition, so keep updating
            // coverage. The phase duration matches LookAheadOffSetting (the
            // user-configured actuator close time) so the projection's
            // anticipation is exactly cancelled by the phase delay — physical
            // spray stops at the intended position.
            UpdateMapping(index, leftEdge, rightEdge, toolHeading);

            // Same as ON: derive ticks from turnOffPhaseSec and use >= so the
            // OFF flip lands at the intended position instead of one tick past.
            // Turn-off DELAY is added on top: the projection only cancels
            // turnOffPhaseSec, so the extra delay pushes the actual off PAST
            // the trigger line — the section keeps applying (and painting)
            // that many seconds longer. Spinner spreaders keep throwing after
            // the gate closes; this records it. Was stored but never read.
            double offDelaySec = Math.Max(0, tool.TurnOffDelay);
            int turnOffPhaseTicks = Math.Max(1, (int)Math.Round((turnOffPhaseSec + offDelaySec) * TickHz));

            if (section.SectionOffTimer >= turnOffPhaseTicks)
            {
                section.IsOn = false;
                section.SectionOffRequest = false;
                StopMapping(index);
            }
        }
        else if (section.IsOn)
        {
            // Section is on and should stay on - update mapping.
            // Clear any stale request flags from a prior tick so the section
            // doesn't render as "Turning OFF" (cyan, code 3) after a transient
            // shouldBeOff flicker that didn't accumulate enough timer ticks
            // to actually flip IsOn. Without this, SectionOffRequest sticks
            // true forever and the UI shows code 3 indefinitely.
            section.SectionOnTimer = 0;
            section.SectionOffTimer = 0;
            section.SectionOnRequest = false;
            section.SectionOffRequest = false;
            UpdateMapping(index, leftEdge, rightEdge, toolHeading);
        }
        else
        {
            // Drive the mapping-off debounce to completion. The phase machine
            // calls StopMapping ONCE at the IsOn drop, but the 0.2s mapping-off
            // debounce needs repeated ticks - the strip never actually closed,
            // its stale last-edge pair survived the whole U-turn, and the first
            // paint after re-green drew a straight band from the pre-turn edge
            // to the resume point (the "shortcut" chord across the turn).
            if (section.IsMappingOn) StopMapping(index);

            // Section is off and should stay off. Same reasoning as the
            // steady-on branch: clear any stale SectionOnRequest from a
            // transient shouldBeOn flicker so the section doesn't render
            // as "Turning ON" (orange, code 4) indefinitely.
            // (A timer-drain variant that tolerated !shouldBeOn blips was
            // trialed and reverted: retained charge let a lone spurious
            // shouldBeOn tick flip the section on inside the headland band,
            // and the faster-drain compromise still misbehaved. The hard
            // reset is the verified-safe behavior.)
            section.SectionOnTimer = 0;
            section.SectionOffTimer = 0;
            section.SectionOnRequest = false;
            section.SectionOffRequest = false;

            // Keep ticking the StopMapping debounce while mapping is still
            // active — same reasoning as UpdateSectionOff. The shouldBeOff
            // branch above only calls StopMapping the cycle IsOn flips;
            // without this, IsMappingOn stays true forever once the state
            // settles into "off, stay off".
            if (section.IsMappingOn)
            {
                StopMapping(index);
            }
        }
    }

    /// <summary>
    /// Turn a section off. IsOn flips immediately; mapping tear-down has its
    /// own debounce (MAPPING_OFF_DELAY = 2 cycles) inside StopMapping, so we
    /// must keep calling StopMapping every cycle while IsMappingOn is still
    /// true — even after IsOn has already flipped — so the debounce timer
    /// accumulates and the coverage paint actually stops.
    /// </summary>
    private void UpdateSectionOff(int index)
    {
        var section = _sectionStates[index];
        if (section.IsOn)
        {
            section.IsOn = false;
        }
        if (section.IsMappingOn)
        {
            StopMapping(index);
        }
        section.SectionOnTimer = 0;
        section.SectionOffTimer = 0;
        section.SectionOnRequest = false;
        section.SectionOffRequest = false;
    }

    /// <summary>
    /// Force a section on (manual override)
    /// </summary>
    private void UpdateSectionOn(int index, Vec2 leftEdge, Vec2 rightEdge, double toolHeading)
    {
        var section = _sectionStates[index];
        if (!section.IsOn)
        {
            section.IsOn = true;
            StartMapping(index, leftEdge, rightEdge, toolHeading);
        }
        else
        {
            UpdateMapping(index, leftEdge, rightEdge, toolHeading);
        }
        // Clear timers AND request flags. Without clearing the requests,
        // a section that switches Auto -> Manual On while a SectionOffRequest
        // was pending would carry that flag forever; the ButtonState=On
        // path of GetSectionColorCode masks it as code 1 (yellow), but the
        // dirty state shows up the moment the user flips back to Auto.
        section.SectionOnTimer = 0;
        section.SectionOffTimer = 0;
        section.SectionOnRequest = false;
        section.SectionOffRequest = false;
    }

    /// <summary>
    /// Start coverage mapping for a section
    /// </summary>
    private void StartMapping(int index, Vec2 leftEdge, Vec2 rightEdge, double toolHeading)
    {
        var section = _sectionStates[index];
        section.MappingOnTimer++;

        if (section.MappingOnTimer > MappingOnDelayTicks && !section.IsMappingOn)
        {
            section.IsMappingOn = true;
            section.MappingOnTimer = 0;

            // Reset yaw rates when starting a new patch to prevent turn influence
            _yawRate = 0;
            _instantYawRate = 0;

            // For the FIRST point of a new patch, use straight perpendicular (no yaw adjustment).
            var (expandedLeft, expandedRight) = ApplyCoverageMarginStraight(leftEdge, rightEdge, toolHeading);

            // Get zone index (for multi-colored sections or zones)
            int zoneIndex = GetZoneIndex(index);
            _coverageMapService.StartMapping(zoneIndex, expandedLeft, expandedRight);

            // Record initial position for minimum distance filtering
            var center = new Vec2(
                (leftEdge.Easting + rightEdge.Easting) / 2,
                (leftEdge.Northing + rightEdge.Northing) / 2);
            _lastCoveragePosition[zoneIndex] = center;
        }
    }

    /// <summary>
    /// Update coverage mapping point. Paints unconditionally each cycle so
    /// adjacent triangle pairs in the strip are guaranteed continuous —
    /// any per-cycle skip leaves a visible hole because this is the sole
    /// writer of coverage points. The legacy yaw-rate and min-distance
    /// filters were removed for that reason; the resulting renderer-side
    /// near-degenerate triangles in fast turns or near-stationary motion
    /// are visually preferable to gaps.
    /// </summary>
    private void UpdateMapping(int index, Vec2 leftEdge, Vec2 rightEdge, double toolHeading)
    {
        var section = _sectionStates[index];

        if (!section.IsMappingOn)
        {
            // Mapping hasn't started yet - continue the startup timer
            StartMapping(index, leftEdge, rightEdge, toolHeading);
            return;
        }

        int zoneIndex = GetZoneIndex(index);

        // Apply coverage margin (always — the curve-skip in ApplyCoverageMargin
        // was the cause of the dotted seam pattern when coverage overlapped
        // itself in turns).
        var (expandedLeft, expandedRight) = ApplyCoverageMargin(leftEdge, rightEdge, toolHeading);

        _coverageMapService.AddCoveragePoint(zoneIndex, expandedLeft, expandedRight);

        // Update last position for this zone (kept for any downstream readers
        // even though it's no longer used as a skip threshold).
        _lastCoveragePosition[zoneIndex] = new Vec2(
            (leftEdge.Easting + rightEdge.Easting) / 2,
            (leftEdge.Northing + rightEdge.Northing) / 2);
    }

    /// <summary>
    /// Apply coverage margin with straight perpendicular (no yaw adjustment).
    /// Used for the first point of a new coverage patch.
    /// </summary>
    private (Vec2 left, Vec2 right) ApplyCoverageMarginStraight(Vec2 leftEdge, Vec2 rightEdge, double toolHeading)
    {
        var tool = _configStore.Tool;
        double margin = tool.CoverageMarginMeters;

        if (margin <= 0)
            return (leftEdge, rightEdge);

        // Straight perpendicular direction (no yaw adjustment)
        double perpHeading = toolHeading + Math.PI / 2.0;
        double perpSin = Math.Sin(perpHeading);
        double perpCos = Math.Cos(perpHeading);

        // Expand left edge outward (negative direction)
        var expandedLeft = new Vec2(
            leftEdge.Easting - perpSin * margin,
            leftEdge.Northing - perpCos * margin);

        // Expand right edge outward (positive direction)
        var expandedRight = new Vec2(
            rightEdge.Easting + perpSin * margin,
            rightEdge.Northing + perpCos * margin);

        return (expandedLeft, expandedRight);
    }

    /// <summary>
    /// Apply coverage margin to expand section edges outward. Always applies
    /// — the legacy "skip during yaw / heading mismatch" gating was the cause
    /// of the dotted seam pattern when coverage overlapped itself, because
    /// the strip would alternate between expanded and raw widths cycle to
    /// cycle. Consistent margin = consistent strip width = clean overlap.
    /// </summary>
    private (Vec2 left, Vec2 right) ApplyCoverageMargin(Vec2 leftEdge, Vec2 rightEdge, double toolHeading)
    {
        var tool = _configStore.Tool;
        double margin = tool.CoverageMarginMeters;

        if (margin <= 0)
            return (leftEdge, rightEdge);

        // For smoother alignment on curves, bias the perpendicular by half
        // the smoothed yaw rate.
        double curveAdjustedHeading = toolHeading + _yawRate * 0.5;

        // Perpendicular direction (rotated 90° from adjusted heading)
        double perpHeading = curveAdjustedHeading + Math.PI / 2.0;
        double perpSin = Math.Sin(perpHeading);
        double perpCos = Math.Cos(perpHeading);

        // Expand left edge outward (negative direction)
        var expandedLeft = new Vec2(
            leftEdge.Easting - perpSin * margin,
            leftEdge.Northing - perpCos * margin);

        // Expand right edge outward (positive direction)
        var expandedRight = new Vec2(
            rightEdge.Easting + perpSin * margin,
            rightEdge.Northing + perpCos * margin);

        return (expandedLeft, expandedRight);
    }

    /// <summary>
    /// Stop coverage mapping for a section
    /// </summary>
    private void StopMapping(int index)
    {
        var section = _sectionStates[index];
        section.MappingOffTimer++;

        if (section.MappingOffTimer > MappingOffDelayTicks && section.IsMappingOn)
        {
            section.IsMappingOn = false;
            section.MappingOffTimer = 0;

            int zoneIndex = GetZoneIndex(index);
            _coverageMapService.StopMapping(zoneIndex);

            // Clear last position so next patch starts fresh
            _lastCoveragePosition.Remove(zoneIndex);
        }
    }

    /// <summary>
    /// Get zone index for a section (handles zones vs individual sections)
    /// </summary>
    private int GetZoneIndex(int sectionIndex)
    {
        var tool = _configStore.Tool;
        if (tool.IsSectionsNotZones)
        {
            return sectionIndex;
        }

        // Find which zone this section belongs to
        for (int z = 1; z <= tool.Zones; z++)
        {
            if (sectionIndex < tool.GetZoneEndSection(z))
            {
                return z - 1;
            }
        }
        return 0;
    }

    /// <summary>
    /// Project a point forward along a heading (straight line)
    /// </summary>
    private Vec2 ProjectForward(Vec2 point, double heading, double distance)
    {
        return new Vec2(
            point.Easting + Math.Sin(heading) * distance,
            point.Northing + Math.Cos(heading) * distance
        );
    }

    /// <summary>
    /// Project a point forward along a curved path using yaw rate.
    /// This "looks around the corner" by predicting where we'll actually be
    /// based on our current turn rate, rather than projecting straight ahead.
    /// </summary>
    /// <param name="point">Starting point</param>
    /// <param name="heading">Current heading in radians</param>
    /// <param name="distance">Distance to project forward</param>
    /// <param name="speed">Current speed in m/s</param>
    /// <returns>Projected point along the curved path</returns>
    /// <summary>Look-ahead sample point: along the planned path when one is
    /// active, else dead-reckoned projection. lateralOffset is the section
    /// centre's offset from the tool centreline (right positive), applied
    /// perpendicular to the local path heading.</summary>
    private Vec2 LookAheadPoint(Vec2 sectionCenter, double toolHeading, double distance,
        double speed, double lateralOffset)
    {
        if (PlannedPathProvider?.Invoke() is { } plan && plan.Path is { Count: >= 2 })
        {
            var pt = WalkPlannedPath(plan.Path, plan.Index, distance, lateralOffset);
            if (pt.HasValue) return pt.Value;
        }
        return ProjectForwardCurved(sectionCenter, toolHeading, distance, speed);
    }

    /// <summary>Walk the planned path from the progress index by arc length,
    /// extend past its end along the final segment, then offset laterally.</summary>
    private static Vec2? WalkPlannedPath(IReadOnlyList<Vec3> path, int index, double distance,
        double lateralOffset)
    {
        int i = Math.Clamp(index, 0, path.Count - 2);
        double remaining = distance;
        for (; i < path.Count - 1; i++)
        {
            double dx = path[i + 1].Easting - path[i].Easting;
            double dy = path[i + 1].Northing - path[i].Northing;
            double seg = Math.Sqrt(dx * dx + dy * dy);
            if (seg < 1e-6) continue;
            if (remaining <= seg)
            {
                double t = remaining / seg;
                double e = path[i].Easting + dx * t;
                double n = path[i].Northing + dy * t;
                double h = Math.Atan2(dx, dy);
                return Offset(e, n, h, lateralOffset);
            }
            remaining -= seg;
        }
        // Past the end: extend along the last segment's heading.
        var a = path[path.Count - 2];
        var b = path[path.Count - 1];
        double ldx = b.Easting - a.Easting, ldy = b.Northing - a.Northing;
        double len = Math.Sqrt(ldx * ldx + ldy * ldy);
        if (len < 1e-6) return null;
        double lh = Math.Atan2(ldx, ldy);
        double ee = b.Easting + Math.Sin(lh) * remaining;
        double nn = b.Northing + Math.Cos(lh) * remaining;
        return Offset(ee, nn, lh, lateralOffset);

        static Vec2 Offset(double e, double n, double h, double lat) =>
            new(e + Math.Sin(h + Math.PI / 2) * lat, n + Math.Cos(h + Math.PI / 2) * lat);
    }

    /// <summary>Arc length of the planned path from the progress index to its end.</summary>
    private static double RemainingPlannedPath(IReadOnlyList<Vec3> path, int index)
    {
        int i = Math.Clamp(index, 0, path.Count - 1);
        double total = 0;
        for (; i < path.Count - 1; i++)
        {
            double dx = path[i + 1].Easting - path[i].Easting;
            double dy = path[i + 1].Northing - path[i].Northing;
            total += Math.Sqrt(dx * dx + dy * dy);
        }
        return total;
    }

    private Vec2 ProjectForwardCurved(Vec2 point, double heading, double distance, double speed)
    {
        // For very slow speeds or no turn, use straight projection
        if (speed < 0.1 || Math.Abs(_yawRate) < 0.001)
        {
            return ProjectForward(point, heading, distance);
        }

        // Calculate how much heading will change over the lookahead distance
        // yawRate is rad/update, at 10Hz that's rad per 0.1 seconds
        // Time to travel distance: t = distance / speed
        // Number of updates in that time: n = t * 10 = 10 * distance / speed
        // Total heading change: Δθ = yawRate * n
        double updateRate = 10.0; // Hz
        double timeToTravel = distance / speed;
        double numUpdates = timeToTravel * updateRate;
        double headingChange = _yawRate * numUpdates;

        // For small heading changes, project along the average heading
        // This is a good approximation for typical lookahead distances
        if (Math.Abs(headingChange) < 0.5) // Less than ~30 degrees
        {
            double avgHeading = heading + headingChange * 0.5;
            return new Vec2(
                point.Easting + Math.Sin(avgHeading) * distance,
                point.Northing + Math.Cos(avgHeading) * distance
            );
        }

        // For larger turns, use proper arc math
        // Turn radius: R = speed / (yawRate * updateRate) = speed / angular_velocity
        double angularVelocity = _yawRate * updateRate; // rad/s
        double turnRadius = speed / Math.Abs(angularVelocity);

        // Arc endpoint calculation
        // The center of the turn circle is perpendicular to heading at distance R
        double turnSign = Math.Sign(_yawRate);
        double centerHeading = heading + turnSign * Math.PI / 2.0;
        double centerEasting = point.Easting + Math.Sin(centerHeading) * turnRadius;
        double centerNorthing = point.Northing + Math.Cos(centerHeading) * turnRadius;

        // Final heading after traveling the arc
        double finalHeading = heading + headingChange;

        // Position on arc at final heading (opposite side from center)
        double finalCenterHeading = finalHeading + turnSign * Math.PI / 2.0;
        return new Vec2(
            centerEasting - Math.Sin(finalCenterHeading) * turnRadius,
            centerNorthing - Math.Cos(finalCenterHeading) * turnRadius
        );
    }

    /// <summary>
    /// Check if a point is inside the field boundary
    /// </summary>
    private bool IsPointInBoundary(Vec2 point)
    {
        // Tool config "Off outside boundary" toggle: with it OFF the boundary
        // never gates sections (coverage/headland remain the only authorities).
        // This flag was stored but unread — the config button did nothing.
        if (!_configStore.Tool.IsSectionOffWhenOut)
            return true;

        var boundary = _state.Field.CurrentBoundary;
        if (boundary == null || !boundary.IsValid)
            return true; // No boundary = always in

        return boundary.IsPointInside(point.Easting, point.Northing);
    }

    /// <summary>
    /// Get segment-based boundary status for a section
    /// </summary>
    private BoundaryResult GetSegmentBoundaryStatus(Vec2 sectionCenter, double heading, double halfWidth)
    {
        // "Off outside boundary" disabled → boundary never gates sections. The
        // no-field guard stays: spraying with no field open is never intended.
        if (!_configStore.Tool.IsSectionOffWhenOut)
            return _state.Field.HasActiveField
                ? BoundaryResult.FullyInside
                : BoundaryResult.FullyOutside;

        var boundary = _state.Field.CurrentBoundary;
        if (boundary == null || !boundary.IsValid)
        {
            // No usable boundary. Two cases:
            //  • A field IS open but has no boundary yet (#419): Auto must
            //    still work — coverage drives on/off so sections turn off
            //    when crossing previously-applied area while free driving.
            //    Treat every section as fully inside so the boundary gate is
            //    a no-op and the coverage check is the only authority.
            //  • No field open at all (#347): spraying with no field defined
            //    is never intended (AiO firmware enforces the same), so treat
            //    every section as fully outside, which gates Auto off and
            //    forces IsOn=false via the strict isInBoundary check.
            return _state.Field.HasActiveField
                ? BoundaryResult.FullyInside
                : BoundaryResult.FullyOutside;
        }

        return boundary.GetSegmentBoundaryStatus(sectionCenter, heading, halfWidth);
    }

    /// <summary>
    /// Check if a point is in the headland area
    /// </summary>
    private bool IsPointInHeadland(Vec2 point)
    {
        var tool = _configStore.Tool;

        // Check if headland section control is enabled
        if (!tool.IsHeadlandSectionControl)
            return false; // Headland control disabled

        // The on-screen headland toggle turns the whole headland OFF — with it off,
        // sections must run right to the boundary and only coverage/boundary stop
        // them. Without this gate the toggle looked dead: sections kept cutting at
        // the band no matter what the operator set.
        if (!_state.FieldTools.IsHeadlandOn)
            return false;

        var headlandLine = _state.Field.HeadlandLine;
        if (headlandLine == null || headlandLine.Count < 3)
            return false; // No headland = never in headland

        // Point is in headland if it's inside boundary but outside headland line
        var boundary = _state.Field.CurrentBoundary;
        if (boundary == null || !boundary.IsValid)
            return false;

        bool inBoundary = boundary.IsPointInside(point.Easting, point.Northing);
        bool insideHeadlandLine = GeometryMath.IsPointInPolygon(headlandLine, point);

        // Headland zone is BETWEEN outer boundary and headland line
        // If inside boundary but outside headland line = in headland zone
        return inBoundary && !insideHeadlandLine;
    }

    public (Vec2 left, Vec2 right) GetSectionWorldPosition(int sectionIndex, Vec3 toolPosition, double toolHeading)
    {
        if (sectionIndex < 0 || sectionIndex >= _sectionStates.Length)
            return (new Vec2(0, 0), new Vec2(0, 0));

        var section = _sectionStates[sectionIndex];

        // Perpendicular to tool heading (right is positive)
        double perpHeading = toolHeading + Math.PI / 2.0;

        var left = new Vec2(
            toolPosition.Easting + Math.Sin(perpHeading) * section.PositionLeft,
            toolPosition.Northing + Math.Cos(perpHeading) * section.PositionLeft
        );

        var right = new Vec2(
            toolPosition.Easting + Math.Sin(perpHeading) * section.PositionRight,
            toolPosition.Northing + Math.Cos(perpHeading) * section.PositionRight
        );

        return (left, right);
    }

    public void SetSectionState(int sectionIndex, SectionButtonState state)
    {
        if (sectionIndex < 0 || sectionIndex >= _sectionStates.Length) return;

        _sectionStates[sectionIndex].ButtonState = state;

        // For immediate UI feedback, sync IsOn / IsMappingOn for manual states.
        // Off must route through UpdateSectionOff so coverage mapping is torn
        // down (StopMapping clears IsMappingOn and notifies the coverage map
        // service) — otherwise the next Update() tick sees IsOn already false
        // and skips the StopMapping call, leaving coverage painting forever.
        // Auto is left for Update() to determine based on boundaries/coverage.
        if (state == SectionButtonState.On)
            _sectionStates[sectionIndex].IsOn = true;
        else if (state == SectionButtonState.Off)
            UpdateSectionOff(sectionIndex);

        SectionStateChanged?.Invoke(this, new SectionStateChangedEventArgs
        {
            SectionIndex = sectionIndex,
            IsOn = _sectionStates[sectionIndex].IsOn,
            IsMappingOn = _sectionStates[sectionIndex].IsMappingOn,
            SectionBits = GetSectionBits()
        });
    }

    public void SetAllSections(SectionButtonState state)
    {
        for (int i = 0; i < _sectionStates.Length; i++)
        {
            _sectionStates[i].ButtonState = state;

            // Mirror SetSectionState: sync IsOn / IsMappingOn for manual
            // states. Off must route through UpdateSectionOff so coverage
            // mapping is torn down. See SetSectionState for the why.
            if (state == SectionButtonState.On)
                _sectionStates[i].IsOn = true;
            else if (state == SectionButtonState.Off)
                UpdateSectionOff(i);
        }

        SectionStateChanged?.Invoke(this, new SectionStateChangedEventArgs
        {
            SectionIndex = -1,
            IsOn = IsAnySectionOn,
            IsMappingOn = IsAnyMappingOn(),
            SectionBits = GetSectionBits()
        });
    }

    public void TurnAllOff()
    {
        for (int i = 0; i < _sectionStates.Length; i++)
        {
            UpdateSectionOff(i);
        }

        // Clear all last coverage positions
        _lastCoveragePosition.Clear();

        SectionStateChanged?.Invoke(this, new SectionStateChangedEventArgs
        {
            SectionIndex = -1,
            IsOn = false,
            IsMappingOn = false,
            SectionBits = 0
        });
    }

    public void SetAllAuto()
    {
        SetAllSections(SectionButtonState.Auto);
        _masterState = SectionMasterState.Auto;
    }

    /// <summary>
    /// No-op since the per-tick cache was removed; coverage is now queried
    /// every Update directly. Kept for backwards compatibility with tests
    /// that explicitly invalidated the old cache between frames.
    /// </summary>
    public void InvalidateCoverageCache() { }

    public void RecalculateSectionPositions()
    {
        var tool = _configStore.Tool;
        int numSections = NumSections;

        // Calculate total width from section widths
        double totalWidth = 0;
        for (int i = 0; i < numSections; i++)
        {
            totalWidth += tool.GetSectionWidth(i) / 100.0; // Convert cm to meters
        }

        // Position sections from left to right, SYMMETRIC about the tool
        // center. Tool.Offset must NOT appear here: the ToolPosition this
        // service is fed already carries the lateral offset
        // (ToolPositionService.ApplyLateralOffset), so baking it into the
        // spans painted coverage and ran section queries at 2× the
        // configured offset — the pre-existing double-application bug the
        // 2026-08 tool-offset survey uncovered.
        double currentPos = -totalWidth / 2.0;

        for (int i = 0; i < numSections; i++)
        {
            double sectionWidth = tool.GetSectionWidth(i) / 100.0;
            _sectionStates[i].PositionLeft = currentPos;
            _sectionStates[i].PositionRight = currentPos + sectionWidth;
            currentPos += sectionWidth;
        }

        // Clear positions for unused sections
        for (int i = numSections; i < _sectionStates.Length; i++)
        {
            _sectionStates[i].PositionLeft = 0;
            _sectionStates[i].PositionRight = 0;
        }
    }

    // The WORK SIGNAL (PGN bits to the drill/sprayer) leads the coverage: a section
    // in TURNING_ON has already seen clear ground at the look-ahead point, so the
    // hardware gets its bit NOW — Look-Ahead-On seconds before the line — to prime
    // the implement (drill metering spin-up, boom pressure). Coverage mapping still
    // starts when IsOn flips at the line, so the painted record shows where product
    // actually landed, not when the valve was told to open.
    private bool SectionOutputOn(int i)
    {
        var s = _sectionStates[i];
        if (s.SectionOnRequest) return true;
        if (!s.IsOn) return false;
        // Look-Ahead-Off drops the WORK SIGNAL early (valve/metering shut-off
        // travel time) while coverage keeps painting to the line. A Turn-Off
        // DELAY is the opposite intent (keep applying past the trigger), so
        // when a delay is set the signal follows IsOn through the whole
        // extended phase instead.
        var tool = _configStore.Tool;
        if (s.SectionOffRequest && tool.TurnOffDelay <= 0 && tool.LookAheadOffSetting > 0)
            return false;
        return true;
    }

    public ushort GetSectionBits()
    {
        ushort bits = 0;
        for (int i = 0; i < 16; i++)
        {
            if (SectionOutputOn(i))
            {
                bits |= (ushort)(1 << i);
            }
        }
        return bits;
    }

    /// <summary>
    /// Section on/off as a 64-bit mask (sections 1–64 in bits 0–63). The low
    /// 16 bits match <see cref="GetSectionBits"/>. Used to build PGN 229 when
    /// more than 16 sections are configured.
    /// </summary>
    public ulong GetSectionBits64()
    {
        ulong bits = 0;
        int count = Math.Min(_sectionStates.Length, ToolConfig.MaxSections);
        for (int i = 0; i < count; i++)
        {
            if (SectionOutputOn(i))
            {
                bits |= 1UL << i;
            }
        }
        return bits;
    }

    private bool IsAnyMappingOn()
    {
        for (int i = 0; i < NumSections; i++)
        {
            if (_sectionStates[i].IsMappingOn) return true;
        }
        return false;
    }
}
