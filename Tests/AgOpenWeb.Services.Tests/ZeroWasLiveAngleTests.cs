// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.ViewModels;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Zero WAS must use the LIVE module angle. The panel's smoothed copy is only fed by a
/// UDP subscription gated on IsPanelVisible — a native-panel hook the web UI never sets —
/// so on the web UI it stayed 0 and Zero WAS silently added 0 to the offset
/// ("can't zero WAS"; the wheel read −5.5° after pressing it).
/// </summary>
[TestFixture]
public class ZeroWasLiveAngleTests
{
    private static (AutoSteerConfigViewModel vm, ConfigurationStore store, IAutoSteerService steer) Build(
        double liveAngle, TimeSpan age)
    {
        var store = new ConfigurationStore();
        ConfigurationStore.SetInstance(store);
        var cfg = Substitute.For<IConfigurationService>();
        cfg.Store.Returns(store);
        var steer = Substitute.For<IAutoSteerService>();
        steer.LastSteerData.Returns(new SteerModuleData(liveAngle, 0, 0, false, false, false, false, 0));
        steer.LastSteerDataAge.Returns(age);
        var vm = new AutoSteerConfigViewModel(cfg, udpService: null, autoSteerService: steer);
        return (vm, store, steer);
    }

    [Test]
    public void ZeroWas_UsesLiveModuleAngle_EvenWhenPanelNeverOpened()
    {
        // Web UI case: IsPanelVisible never set → no UDP subscription → smoothed angle = 0.
        var (vm, store, _) = Build(liveAngle: -5.47, age: TimeSpan.FromMilliseconds(50));
        store.AutoSteer.CountsPerDegree = 109;
        store.AutoSteer.WasOffset = 0;

        vm.ZeroWasCommand.Execute(null);

        // newOffset = old + round(angle × CPD) = 0 + round(−5.47 × 109) = −596
        Assert.That(store.AutoSteer.WasOffset, Is.EqualTo(-596),
            "zeroing must apply the live module angle, not the never-fed smoothed copy (0)");
    }

    [Test]
    public void ZeroWas_IgnoresStaleOrNeverReceivedModuleData()
    {
        // Module has never reported (age = MaxValue): LastSteerData is the zero default.
        // Must NOT treat that 0 as a real reading — fall back (smoothed is also 0 here),
        // so the offset is left unchanged rather than "zeroed" against nothing.
        var (vm, store, _) = Build(liveAngle: 0, age: TimeSpan.MaxValue);
        store.AutoSteer.CountsPerDegree = 109;
        store.AutoSteer.WasOffset = 613;

        vm.ZeroWasCommand.Execute(null);

        Assert.That(store.AutoSteer.WasOffset, Is.EqualTo(613),
            "with no live module data the offset must not be corrupted");
    }
}
