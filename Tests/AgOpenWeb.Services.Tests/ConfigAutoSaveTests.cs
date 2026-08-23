// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Diagnostics;
using System.Threading;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Every steer/vehicle/tool edit only calls Store.MarkChanged() and pushes to the module;
/// only the explicit "Send + Save" ever wrote the profile to disk — a native-panel habit the
/// web UI never surfaces — so any restart discarded every edit ("settings don't persist").
/// With auto-save enabled, a flagged change must persist itself after the debounce.
/// </summary>
[TestFixture]
public class ConfigAutoSaveTests
{
    private static (ConfigurationService svc, IVehicleProfileService vp, ConfigurationStore store) Build()
    {
        var store = new ConfigurationStore();
        ConfigurationStore.SetInstance(store);
        store.ActiveVehicleProfileName = "6480";
        store.ActiveToolProfileName = "Sam Spreader Fert";
        var vp = Substitute.For<IVehicleProfileService>();
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(new AppSettings());
        var svc = new ConfigurationService(vp, Substitute.For<IToolProfileService>(), settings, store);
        return (svc, vp, store);
    }

    private static bool WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (cond()) return true; Thread.Sleep(25); }
        return cond();
    }

    [Test]
    public void MarkChanged_PersistsProfile_WithoutExplicitSave()
    {
        var (svc, vp, store) = Build();
        svc.EnableAutoSave();

        store.MarkChanged();   // what every web edit / Zero WAS does — and nothing more

        // Wait for the END condition (dirty flag cleared by SaveProfiles), not merely the
        // first mock call — the save clears the flag last, so polling on the call races it.
        Assert.That(WaitUntil(() => !store.HasUnsavedChanges, 5000), Is.True,
            "a flagged change must be written to disk by the debounced auto-save (and clear the flag)");
        vp.Received().Save("6480", store);
    }

    [Test]
    public void WithoutEnableAutoSave_NothingIsWritten()
    {
        // Pins the old behaviour so the fix is visibly the opt-in EnableAutoSave().
        var (svc, vp, store) = Build();

        store.MarkChanged();
        Thread.Sleep(2600);

        vp.DidNotReceiveWithAnyArgs().Save(default!, default!);
        Assert.That(store.HasUnsavedChanges, Is.True);
    }
}
