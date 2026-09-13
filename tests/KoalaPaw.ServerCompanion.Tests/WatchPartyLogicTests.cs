using System;
using System.Collections.Generic;
using System.Linq;
using KoalaPaw.ServerCompanion.WatchParty;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

/// <summary>
/// The watch-together decisions extracted from WatchPartyController/WatchPartyStore: presence
/// upsert/TTL, party roster and version, the member-only state-write truth table, and the
/// dissolution rules (leader leaves, leader presence expires, idle 10 min).
/// </summary>
public class WatchPartyLogicTests
{
    private static readonly Guid AliceId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid BobId = new("00000000-0000-0000-0000-000000000002");
    private static readonly Guid ItemId = new("00000000-0000-0000-0000-0000000000aa");

    private const string TvA = "tv-a";
    private const string TvB = "tv-b";

    // --- Presence ---

    [Fact]
    public void UpsertPresence_newDevice_storesCallerIdentityAndState()
    {
        var entry = WatchPartyLogic.UpsertPresence(
            null, AliceId, TvA, "Living Room TV", ItemId, "Pilot", 1234.5, isPlaying: true, partyId: null, nowMs: 1000);

        Assert.Equal(AliceId, entry.UserId);
        Assert.Equal(TvA, entry.DeviceId);
        Assert.Equal("Living Room TV", entry.DeviceName);
        Assert.Equal(ItemId, entry.ItemId);
        Assert.Equal("Pilot", entry.ItemName);
        Assert.Equal(1234.5, entry.PositionMs);
        Assert.True(entry.IsPlaying);
        Assert.Null(entry.PartyId);
        Assert.Equal(1000, entry.LastSeenMs);
    }

    [Fact]
    public void UpsertPresence_partyId_isAppliedOnEveryWrite()
    {
        var partyId = Guid.NewGuid();
        var joined = WatchPartyLogic.UpsertPresence(null, AliceId, TvA, "TV", null, null, null, true, partyId, 1000);

        var left = WatchPartyLogic.UpsertPresence(joined, AliceId, TvA, "TV", null, null, null, true, null, 2000);

        Assert.Equal(partyId, joined.PartyId);
        Assert.Null(left.PartyId);
    }

    [Fact]
    public void PartyOf_findsPartyByLeaderOrMemberDevice()
    {
        var party = WatchPartyLogic.Join(
            WatchPartyLogic.CreateParty(Guid.NewGuid(), AliceId, TvA, nowMs: 1000), BobId, TvB, 2000);

        Assert.Equal(party.PartyId, WatchPartyLogic.PartyOf([party], AliceId, TvA)!.PartyId);
        Assert.Equal(party.PartyId, WatchPartyLogic.PartyOf([party], BobId, TvB)!.PartyId);
        Assert.Null(WatchPartyLogic.PartyOf([party], AliceId, TvB));
    }

    [Fact]
    public void UpsertPresence_blankDeviceName_keepsStoredName()
    {
        var first = WatchPartyLogic.UpsertPresence(null, AliceId, TvA, "Living Room TV", null, null, null, false, null, 1000);

        var second = WatchPartyLogic.UpsertPresence(first, AliceId, TvA, null, null, null, null, false, null, 2000);

        Assert.Equal("Living Room TV", second.DeviceName);
    }

    [Fact]
    public void UpsertPresence_blankDeviceName_keepsStoredItemAndPosition()
    {
        var first = WatchPartyLogic.UpsertPresence(null, AliceId, TvA, "TV", ItemId, "Pilot", 1000.0, true, null, 1000);

        // A party state heartbeat carries no device name or item name — it must not wipe them.
        var second = WatchPartyLogic.UpsertPresence(first, AliceId, TvA, null, null, null, null, true, null, 2000);

        Assert.Equal(ItemId, second.ItemId);
        Assert.Equal("Pilot", second.ItemName);
        Assert.Equal(1000.0, second.PositionMs);
        Assert.Equal(2000, second.LastSeenMs);
    }

    [Fact]
    public void UpsertPresence_namedDevice_updatesStoredName()
    {
        var first = WatchPartyLogic.UpsertPresence(null, AliceId, TvA, "Living Room TV", null, null, null, false, null, 1000);

        var second = WatchPartyLogic.UpsertPresence(first, AliceId, TvA, "Bedroom TV", null, null, null, false, null, 2000);

        Assert.Equal("Bedroom TV", second.DeviceName);
    }

    [Fact]
    public void UpsertPresence_sameUserTwoDevices_tracksThemIndependently()
    {
        var tvA = WatchPartyLogic.UpsertPresence(null, AliceId, TvA, "TV A", ItemId, null, null, true, null, 1000);
        var tvB = WatchPartyLogic.UpsertPresence(null, AliceId, TvB, "TV B", null, null, null, false, null, 2000);

        var active = WatchPartyLogic.ActivePresence([tvA, tvB], nowMs: 2000);

        Assert.Equal(2, active.Count);
        Assert.True(WatchPartyLogic.IsPresent(active, AliceId, TvA, 2000));
        Assert.True(WatchPartyLogic.IsPresent(active, AliceId, TvB, 2000));
        Assert.False(WatchPartyLogic.IsPresent(active, AliceId, "tv-c", 2000));
    }

    [Fact]
    public void IsExpired_atTtlBoundary_isAlive()
    {
        var entry = WatchPartyLogic.UpsertPresence(null, AliceId, TvA, "TV", null, null, null, false, null, 1000);

        Assert.False(WatchPartyLogic.IsExpired(entry, 1000 + WatchPartyLogic.PresenceTtlMs));
    }

    [Fact]
    public void IsExpired_pastTtl_isGone()
    {
        var entry = WatchPartyLogic.UpsertPresence(null, AliceId, TvA, "TV", null, null, null, false, null, 1000);

        Assert.True(WatchPartyLogic.IsExpired(entry, 1001 + WatchPartyLogic.PresenceTtlMs));
    }

    [Fact]
    public void ActivePresence_dropsExpired_newestFirst()
    {
        var stale = WatchPartyLogic.UpsertPresence(null, AliceId, TvA, "TV A", null, null, null, false, null, 1000);
        var fresh = WatchPartyLogic.UpsertPresence(null, BobId, TvB, "TV B", null, null, null, false, null, 40_000);

        var active = WatchPartyLogic.ActivePresence([stale, fresh], nowMs: 46_001);

        Assert.Single(active);
        Assert.Equal(TvB, active[0].DeviceId);
    }

    // --- Party creation and membership ---

    [Fact]
    public void CreateParty_leaderDeviceStartsOnRosterAtVersionZero()
    {
        var party = WatchPartyLogic.CreateParty(Guid.NewGuid(), AliceId, TvA, nowMs: 1000);

        Assert.Equal(AliceId, party.LeaderUserId);
        Assert.Equal(TvA, party.LeaderDeviceId);
        Assert.Equal(0, party.Version);
        Assert.Equal(1000, party.LastActivityMs);
        Assert.Single(party.Members);
        Assert.True(WatchPartyLogic.IsLeader(party, AliceId, TvA));
        Assert.True(WatchPartyLogic.IsMember(party, AliceId, TvA));
    }

    [Fact]
    public void IsLeader_sameUserDifferentDevice_isFalse()
    {
        var party = WatchPartyLogic.CreateParty(Guid.NewGuid(), AliceId, TvA, nowMs: 1000);

        Assert.False(WatchPartyLogic.IsLeader(party, AliceId, TvB));
    }

    [Fact]
    public void Join_newDevice_addsMemberAndMarksActive()
    {
        var party = WatchPartyLogic.CreateParty(Guid.NewGuid(), AliceId, TvA, nowMs: 1000);

        var joined = WatchPartyLogic.Join(party, BobId, TvB, nowMs: 2000);

        Assert.Equal(2, joined.Members.Count);
        Assert.True(WatchPartyLogic.IsMember(joined, BobId, TvB));
        Assert.Equal(2000, joined.LastActivityMs);
        Assert.True(WatchPartyLogic.IsMember(joined, AliceId, TvA));
    }

    [Fact]
    public void Join_sameDeviceTwice_isIdempotent()
    {
        var party = WatchPartyLogic.CreateParty(Guid.NewGuid(), AliceId, TvA, nowMs: 1000);

        var joined = WatchPartyLogic.Join(WatchPartyLogic.Join(party, BobId, TvB, 2000), BobId, TvB, 3000);

        Assert.Equal(2, joined.Members.Count);
        Assert.Equal(3000, joined.LastActivityMs);
    }

    [Fact]
    public void Leave_memberDevice_removesOnlyThatDevice()
    {
        var party = WatchPartyLogic.Join(
            WatchPartyLogic.CreateParty(Guid.NewGuid(), AliceId, TvA, nowMs: 1000), BobId, TvB, 2000);

        var left = WatchPartyLogic.Leave(party, BobId, TvB, nowMs: 3000);

        Assert.Single(left.Members);
        Assert.True(WatchPartyLogic.IsMember(left, AliceId, TvA));
        Assert.False(WatchPartyLogic.IsMember(left, BobId, TvB));
    }

    // --- State writes ---

    [Fact]
    public void ApplyState_bumpsVersionMonotonically()
    {
        var party = WatchPartyLogic.CreateParty(Guid.NewGuid(), AliceId, TvA, nowMs: 1000);

        var first = WatchPartyLogic.ApplyState(party, ItemId, 100.0, true, ["sib-1"], nowMs: 2000);
        var second = WatchPartyLogic.ApplyState(first, ItemId, 200.0, true, ["sib-1"], nowMs: 3000);

        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.True(second.Version > first.Version);
        Assert.Equal(200.0, second.PositionMs);
        Assert.Equal(new[] { "sib-1" }, second.SiblingIds);
        Assert.Equal(3000, second.LastActivityMs);
    }

    [Fact]
    public void AccessFor_memberDevice_isMember()
    {
        var party = WatchPartyLogic.CreateParty(Guid.NewGuid(), AliceId, TvA, nowMs: 1000);

        Assert.Equal(WatchPartyLogic.PartyAccess.Member, WatchPartyLogic.AccessFor(party, AliceId, TvA));
    }

    [Fact]
    public void AccessFor_nonMemberDevice_isNotMember()
    {
        var party = WatchPartyLogic.CreateParty(Guid.NewGuid(), AliceId, TvA, nowMs: 1000);

        Assert.Equal(WatchPartyLogic.PartyAccess.NotMember, WatchPartyLogic.AccessFor(party, BobId, TvB));
    }

    [Fact]
    public void AccessFor_unknownParty_isUnknown()
    {
        Assert.Equal(WatchPartyLogic.PartyAccess.Unknown, WatchPartyLogic.AccessFor(null, AliceId, TvA));
    }

    [Fact]
    public void AccessFor_sameUserDifferentDevice_isNotMember()
    {
        var party = WatchPartyLogic.CreateParty(Guid.NewGuid(), AliceId, TvA, nowMs: 1000);

        Assert.Equal(WatchPartyLogic.PartyAccess.NotMember, WatchPartyLogic.AccessFor(party, AliceId, TvB));
    }

    // --- Dissolution ---

    [Fact]
    public void IsIdle_atIdleBoundary_isAlive()
    {
        var party = WatchPartyLogic.CreateParty(Guid.NewGuid(), AliceId, TvA, nowMs: 1000);

        Assert.False(WatchPartyLogic.IsIdle(party, 1000 + WatchPartyLogic.IdlePartyMs));
    }

    [Fact]
    public void IsIdle_pastIdleWindow_isGone()
    {
        var party = WatchPartyLogic.CreateParty(Guid.NewGuid(), AliceId, TvA, nowMs: 1000);

        Assert.True(WatchPartyLogic.IsIdle(party, 1001 + WatchPartyLogic.IdlePartyMs));
    }

    [Fact]
    public void IsIdle_afterStateWrite_restartsTheWindow()
    {
        var party = WatchPartyLogic.CreateParty(Guid.NewGuid(), AliceId, TvA, nowMs: 1000);
        var written = WatchPartyLogic.ApplyState(party, ItemId, 1.0, true, [], nowMs: 500_000);

        Assert.False(WatchPartyLogic.IsIdle(written, 500_000 + WatchPartyLogic.IdlePartyMs));
    }

    [Fact]
    public void Describe_leaderPresent_whileLeaderHeartbeats()
    {
        var party = WatchPartyLogic.CreateParty(Guid.NewGuid(), AliceId, TvA, nowMs: 1000);
        var presence = new List<WatchPartyLogic.PresenceEntry>
        {
            WatchPartyLogic.UpsertPresence(null, AliceId, TvA, "TV A", null, null, null, true, null, 1000),
            WatchPartyLogic.UpsertPresence(null, BobId, TvB, "TV B", null, null, null, true, null, 1000),
        };

        var status = WatchPartyLogic.Describe(party, presence, nowMs: 2000);

        Assert.True(status.LeaderPresent);

        // Bob is heartbeating but never joined — the roster, not presence, drives memberCount.
        Assert.Equal(1, status.MemberCount);
    }

    [Fact]
    public void Describe_leaderPresenceExpired_leaderPresentFalse()
    {
        var party = WatchPartyLogic.CreateParty(Guid.NewGuid(), AliceId, TvA, nowMs: 1000);
        var presence = new List<WatchPartyLogic.PresenceEntry>
        {
            WatchPartyLogic.UpsertPresence(null, AliceId, TvA, "TV A", null, null, null, true, null, 1000),
        };

        var status = WatchPartyLogic.Describe(party, presence, nowMs: 1001 + WatchPartyLogic.PresenceTtlMs);

        Assert.False(status.LeaderPresent);
    }

    [Fact]
    public void Describe_memberPresenceAlone_doesNotCountAsLeaderPresent()
    {
        var party = WatchPartyLogic.Join(
            WatchPartyLogic.CreateParty(Guid.NewGuid(), AliceId, TvA, nowMs: 1000), BobId, TvB, 2000);
        var presence = new List<WatchPartyLogic.PresenceEntry>
        {
            WatchPartyLogic.UpsertPresence(null, BobId, TvB, "TV B", null, null, null, true, null, 3000),
        };

        var status = WatchPartyLogic.Describe(party, presence, nowMs: 3000);

        Assert.False(status.LeaderPresent);
        Assert.Equal(2, status.MemberCount);
    }

    // --- Join by device (join-by-indication) ---

    [Fact]
    public void DecideJoinByDevice_staleTarget_isUnknownTarget()
    {
        var decision = WatchPartyLogic.DecideJoinByDevice(false, null, null, BobId, TvB);

        Assert.Equal(WatchPartyLogic.JoinByDeviceOutcome.UnknownTarget, decision.Outcome);
        Assert.Null(decision.PartyId);
    }

    [Fact]
    public void DecideJoinByDevice_staleTargetBeatsCallerParty_404Before409()
    {
        var callerParty = WatchPartyLogic.CreateParty(Guid.NewGuid(), AliceId, TvA, nowMs: 1000);

        var decision = WatchPartyLogic.DecideJoinByDevice(false, callerParty, null, BobId, TvB);

        Assert.Equal(WatchPartyLogic.JoinByDeviceOutcome.UnknownTarget, decision.Outcome);
    }

    [Fact]
    public void DecideJoinByDevice_targetInNoParty_createsOne()
    {
        var decision = WatchPartyLogic.DecideJoinByDevice(true, null, null, BobId, TvB);

        Assert.Equal(WatchPartyLogic.JoinByDeviceOutcome.CreatedNewParty, decision.Outcome);
        Assert.Null(decision.PartyId);
    }

    [Fact]
    public void DecideJoinByDevice_targetLeads_joinsItsParty()
    {
        var party = WatchPartyLogic.CreateParty(Guid.NewGuid(), BobId, TvB, nowMs: 1000);

        var decision = WatchPartyLogic.DecideJoinByDevice(true, null, party, BobId, TvB);

        Assert.Equal(WatchPartyLogic.JoinByDeviceOutcome.JoinedExistingParty, decision.Outcome);
        Assert.Equal(party.PartyId, decision.PartyId);
    }

    [Fact]
    public void DecideJoinByDevice_targetIsMemberNotLeader_conflictsWithThatParty()
    {
        var party = WatchPartyLogic.Join(
            WatchPartyLogic.CreateParty(Guid.NewGuid(), AliceId, TvA, nowMs: 1000), BobId, TvB, 2000);

        var decision = WatchPartyLogic.DecideJoinByDevice(true, null, party, BobId, TvB);

        Assert.Equal(WatchPartyLogic.JoinByDeviceOutcome.TargetIsMember, decision.Outcome);
        Assert.Equal(party.PartyId, decision.PartyId);
    }

    [Fact]
    public void DecideJoinByDevice_callerAlreadyInParty_conflictsWithCallerParty()
    {
        var callerParty = WatchPartyLogic.CreateParty(Guid.NewGuid(), AliceId, TvA, nowMs: 1000);
        var targetParty = WatchPartyLogic.CreateParty(Guid.NewGuid(), BobId, TvB, nowMs: 1000);

        var decision = WatchPartyLogic.DecideJoinByDevice(true, callerParty, targetParty, BobId, TvB);

        Assert.Equal(WatchPartyLogic.JoinByDeviceOutcome.CallerAlreadyInParty, decision.Outcome);
        Assert.Equal(callerParty.PartyId, decision.PartyId);
    }
}
