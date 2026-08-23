using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;

namespace AgOpenWeb.ViewModels.Tests;

[TestFixture]
public class RoutePlanClearTests
{
    private static Track Ab(string name, double e) => new()
    {
        Name = name,
        Points = new List<Vec3> { new(e, 0, 0), new(e, 100, 0) },
        Type = TrackType.ABLine,
    };

    // Clearing a route must drop the plan's registered "Route *" steer tracks.
    // RegisterRouteSteerTracks only prunes them on the NEXT plan, so a clear used
    // to leave "Route Headland"/"Route Main"/… behind — still drawn and selectable,
    // so the route looked like it never cleared.
    [Test]
    public void ClearRoutePlan_RemovesRegisteredRouteSteerTracks_KeepsUserTracks()
    {
        var vm = new MainViewModelBuilder().Build();
        var userAb = Ab("AB 1", 0);
        vm.SavedTracks.Add(userAb);
        vm.SavedTracks.Add(Ab("Route Headland", 1));
        vm.SavedTracks.Add(Ab("Route Main", 2));

        vm.ClearRoutePlan();

        Assert.That(vm.SavedTracks, Does.Contain(userAb), "user's own AB line must survive a clear");
        Assert.That(vm.SavedTracks.Any(t => t.Name.StartsWith("Route ")), Is.False,
            "clear must drop the plan's registered Route * steer tracks");
    }

    // If the operator was following one of the route steer tracks, clearing the
    // route must also deactivate guidance (setting SelectedTrack = null).
    [Test]
    public void ClearRoutePlan_DeactivatesFollowedRouteTrack()
    {
        var vm = new MainViewModelBuilder().Build();
        var routeMain = Ab("Route Main", 2);
        vm.SavedTracks.Add(routeMain);
        vm.SelectedTrack = routeMain;
        Assert.That(vm.HasActiveTrack, Is.True);

        vm.ClearRoutePlan();

        Assert.That(vm.SelectedTrack, Is.Null, "clear must deactivate a followed route track");
        Assert.That(vm.HasActiveTrack, Is.False);
    }

    // A hand-made track that happens to be selected (not a Route path) must NOT be
    // deactivated or removed by a route clear.
    [Test]
    public void ClearRoutePlan_LeavesSelectedUserTrackUntouched()
    {
        var vm = new MainViewModelBuilder().Build();
        var userAb = Ab("AB 1", 0);
        vm.SavedTracks.Add(userAb);
        vm.SavedTracks.Add(Ab("Route Main", 2));
        vm.SelectedTrack = userAb;

        vm.ClearRoutePlan();

        Assert.That(vm.SelectedTrack, Is.EqualTo(userAb), "a selected user track must survive a route clear");
        Assert.That(vm.SavedTracks, Does.Contain(userAb));
        Assert.That(vm.SavedTracks.Any(t => t.Name.StartsWith("Route ")), Is.False);
    }

    // The web's track.deleteAll used to map to DeleteAllTracksCommand, which opens a
    // host-side confirmation the browser can never answer: nothing was deleted and the
    // UI stayed parked on the Confirmation dialog. The web now calls the confirmed
    // action directly.
    [Test]
    public void DeleteAllTracksRemote_ClearsTracks_WithoutADialog()
    {
        var vm = new MainViewModelBuilder().Build();
        var ab = Ab("AB 1", 0);
        vm.SavedTracks.Add(ab);
        vm.SavedTracks.Add(Ab("AB 2", 1));
        vm.SelectedTrack = ab;

        vm.DeleteAllTracksRemote();

        Assert.That(vm.SavedTracks, Is.Empty, "every saved track is deleted");
        Assert.That(vm.SelectedTrack, Is.Null, "the active track is deactivated");
        Assert.That(vm.StatusMessage, Is.EqualTo("All tracks deleted"));
    }
}
