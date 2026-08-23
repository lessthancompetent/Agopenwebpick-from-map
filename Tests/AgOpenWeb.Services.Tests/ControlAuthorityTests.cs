// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Reflection;
using AgOpenWeb.RemoteServer;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The control seat gates every Tier-2 (live actuation) command. Acquire used to refuse
/// a new claimant whenever ANY other connection held the seat — even one whose presence
/// had lapsed the deadman. After a deploy restart / link drop the reconnecting kiosk was
/// refused, stuck as Observer, and every AUTO press was silently dropped (PGN 254 status
/// stayed 0, nothing in the log). A stale hold must not block a fresh claimant.
/// </summary>
[TestFixture]
public class ControlAuthorityTests
{
    // Age the current hold past the deadman without waiting 5 s.
    private static void AgeHold(ControlAuthority a, long ms)
    {
        var f = typeof(ControlAuthority).GetField("_lastPresenceTicks", BindingFlags.NonPublic | BindingFlags.Instance)!;
        f.SetValue(a, (long)f.GetValue(a)! - ms);
    }

    [Test]
    public void FreshHolder_BlocksOtherClaimants()
    {
        var a = new ControlAuthority();
        var kiosk = Guid.NewGuid(); var other = Guid.NewGuid();
        Assert.That(a.Acquire(kiosk, "kiosk"), Is.True);
        Assert.That(a.Acquire(other, "other"), Is.False, "a FRESH holder keeps the seat (single authority)");
        Assert.That(a.HoldsFresh(kiosk), Is.True);
    }

    [Test]
    public void StaleHolder_DoesNotBlockAFreshClaimant()
    {
        var a = new ControlAuthority();
        var dead = Guid.NewGuid(); var kiosk = Guid.NewGuid();
        Assert.That(a.Acquire(dead, "old tab"), Is.True);
        AgeHold(a, 6000);                               // dead tab stopped heart-beating
        Assert.That(a.HoldsFresh(dead), Is.False, "the old hold is stale");

        Assert.That(a.Acquire(kiosk, "kiosk"), Is.True,
            "a reconnecting kiosk must be able to take a seat whose holder is past the deadman");
        Assert.That(a.HoldsFresh(kiosk), Is.True, "and its Tier-2 commands (AUTO) are honoured");
        Assert.That(a.HoldsFresh(dead), Is.False);
    }

    [Test]
    public void StaleHandover_DoesNotFireTheFailsafe()
    {
        // Revoked runs the host failsafe (disengages steering). Superseding a dead holder
        // must not disengage the steering the NEW operator just took (cf. Takeover).
        var a = new ControlAuthority();
        int revoked = 0; a.Revoked += _ => revoked++;
        a.Acquire(Guid.NewGuid(), "old tab");
        AgeHold(a, 6000);
        a.Acquire(Guid.NewGuid(), "kiosk");
        Assert.That(revoked, Is.Zero, "no failsafe on a stale-seat handover");
    }
}
