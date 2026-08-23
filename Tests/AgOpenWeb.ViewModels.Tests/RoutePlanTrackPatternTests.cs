namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Planning ALONG a reference track used to hard-code pattern 0 (Auto) on both the
/// AB-line and curve branches of PlanRouteAlongSelectedTrack, silently discarding the
/// panel's Skip/Block choice — "the planner lost its other path planners, everything
/// defaults to auto". The track fixes the HEADING; the row-ORDER patterns still apply.
/// </summary>
[TestFixture]
public class RoutePlanTrackPatternTests
{
    [TestCase(1, 1, TestName = "Skip is honoured along a track")]
    [TestCase(4, 4, TestName = "Block is honoured along a track")]
    public void RowOrderPatterns_AreHonouredAlongAReferenceTrack(int chosen, int expected)
        => Assert.That(MainViewModel.TrackPattern(chosen), Is.EqualTo(expected));

    // Spiral has no heading to follow and Cross is two headings — neither is "along a
    // line", so they fall back to plain Auto rather than producing a nonsense plan.
    [TestCase(0, TestName = "Auto stays Auto")]
    [TestCase(2, TestName = "Cross falls back to Auto along a track")]
    [TestCase(3, TestName = "Spiral falls back to Auto along a track")]
    public void HeadinglessPatterns_FallBackToAuto(int chosen)
        => Assert.That(MainViewModel.TrackPattern(chosen), Is.EqualTo(0));
}
