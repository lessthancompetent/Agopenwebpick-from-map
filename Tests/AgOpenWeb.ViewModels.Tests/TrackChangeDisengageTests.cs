using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// SAFETY: swapping or removing the guidance line while AutoSteer is engaged must
/// disengage it (AgOpenGPS does). The SelectedTrack setter never did, so a new
/// Bnd. Curve, a Tracks-manager tap, a cycle or a delete silently retargeted the
/// steering while the wheel stayed engaged.
/// </summary>
[TestFixture]
public class TrackChangeDisengageTests
{
    private static Track Ab(string name, double e) => new()
    {
        Name = name,
        Points = new List<Vec3> { new(e, 0, 0), new(e, 100, 0) },
        Type = TrackType.ABLine,
    };

    [Test]
    public void ChangingTrack_WhileEngaged_Disengages()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        var a = Ab("AB 1", 0); var c = Ab("AB 2", 10);
        vm.SavedTracks.Add(a); vm.SavedTracks.Add(c);
        vm.SelectedTrack = a;
        vm.IsAutoSteerEngaged = true;
        b.AutoSteerService.ClearReceivedCalls();

        vm.SelectedTrack = c;

        Assert.That(vm.IsAutoSteerEngaged, Is.False, "engaged flag must drop on a track change");
        b.AutoSteerService.Received(1).Disengage();
        Assert.That(vm.StatusMessage, Does.Contain("Guidance stopped"));
    }

    [Test]
    public void RemovingTrack_WhileEngaged_Disengages()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        var a = Ab("AB 1", 0);
        vm.SavedTracks.Add(a);
        vm.SelectedTrack = a;
        vm.IsAutoSteerEngaged = true;
        b.AutoSteerService.ClearReceivedCalls();

        vm.SelectedTrack = null;

        Assert.That(vm.IsAutoSteerEngaged, Is.False);
        b.AutoSteerService.Received(1).Disengage();
    }

    [Test]
    public void ReselectingSameTrack_DoesNotDisengage()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        var a = Ab("AB 1", 0);
        vm.SavedTracks.Add(a);
        vm.SelectedTrack = a;
        vm.IsAutoSteerEngaged = true;
        b.AutoSteerService.ClearReceivedCalls();

        vm.SelectedTrack = a;   // no change

        Assert.That(vm.IsAutoSteerEngaged, Is.True, "same track is not a change");
        b.AutoSteerService.DidNotReceive().Disengage();
    }
}
