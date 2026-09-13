using System;
using System.IO;
using System.Linq;
using KoalaPaw.ServerCompanion.WatchParty;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

/// <summary>
/// WatchPartyStore persistence and TTL behaviour: parties survive a restart, presence does not,
/// a corrupt file degrades to empty instead of throwing, and the idle/leader-leave dissolution
/// rules apply at the store boundary.
/// </summary>
public class WatchPartyStoreTests : IDisposable
{
    private static readonly Guid AliceId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid BobId = new("00000000-0000-0000-0000-000000000002");
    private static readonly Guid ItemId = new("00000000-0000-0000-0000-0000000000aa");

    private const string TvA = "tv-a";
    private const string TvB = "tv-b";

    private readonly string _tempDir;

    public WatchPartyStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "koalapaw-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private WatchPartyStore CreateStore() =>
        new(_tempDir, NullLogger<WatchPartyStore>.Instance);

    private string StoreFilePath => Path.Combine(_tempDir, "watch-parties.json");

    [Fact]
    public void Persistence_partySurvivesNewInstance()
    {
        var party = CreateStore().CreateParty(AliceId, TvA, nowMs: 1000);

        var status = CreateStore().GetState(party.PartyId, nowMs: 2000);

        Assert.NotNull(status);
        Assert.Equal(party.PartyId, status!.PartyId);
        Assert.Equal(AliceId, status.LeaderUserId);
        Assert.Equal(TvA, status.LeaderDeviceId);
        Assert.Equal(1, status.MemberCount);
    }

    [Fact]
    public void Persistence_presenceIsNotPersisted()
    {
        var store = CreateStore();
        store.UpsertPresence(AliceId, TvA, "TV A", ItemId, "Pilot", 10.0, true, nowMs: 1000);

        Assert.Empty(CreateStore().GetActivePresence(nowMs: 2000));
    }

    [Fact]
    public void Persistence_corruptFile_startsFreshAndKeepsWorking()
    {
        File.WriteAllText(StoreFilePath, "}{ not json");

        var store = CreateStore();
        Assert.Null(store.GetState(Guid.NewGuid(), nowMs: 1000));

        var party = store.CreateParty(AliceId, TvA, nowMs: 2000);
        Assert.NotNull(CreateStore().GetState(party.PartyId, nowMs: 3000));
    }

    [Fact]
    public void CreateParty_sameLeaderDevice_replacesPreviousParty()
    {
        var store = CreateStore();
        var first = store.CreateParty(AliceId, TvA, nowMs: 1000);
        var second = store.CreateParty(AliceId, TvA, nowMs: 2000);

        Assert.NotEqual(first.PartyId, second.PartyId);
        Assert.Null(store.GetState(first.PartyId, nowMs: 3000));
        Assert.NotNull(store.GetState(second.PartyId, nowMs: 3000));
    }

    [Fact]
    public void CreateParty_differentDevice_keepsBothParties()
    {
        var store = CreateStore();
        var first = store.CreateParty(AliceId, TvA, nowMs: 1000);
        var second = store.CreateParty(AliceId, TvB, nowMs: 2000);

        Assert.NotNull(store.GetState(first.PartyId, nowMs: 3000));
        Assert.NotNull(store.GetState(second.PartyId, nowMs: 3000));
    }

    [Fact]
    public void GetState_idleParty_isGone()
    {
        var store = CreateStore();
        var party = store.CreateParty(AliceId, TvA, nowMs: 1000);

        Assert.Null(store.GetState(party.PartyId, nowMs: 1001 + WatchPartyLogic.IdlePartyMs));
    }

    [Fact]
    public void Leave_leader_dissolvesParty()
    {
        var store = CreateStore();
        var party = store.CreateParty(AliceId, TvA, nowMs: 1000);

        Assert.True(store.Leave(party.PartyId, AliceId, TvA, nowMs: 2000));
        Assert.Null(store.GetState(party.PartyId, nowMs: 3000));
    }

    [Fact]
    public void Leave_member_keepsLeaderParty()
    {
        var store = CreateStore();
        var party = store.CreateParty(AliceId, TvA, nowMs: 1000);
        store.Join(party.PartyId, BobId, TvB, nowMs: 2000);

        Assert.True(store.Leave(party.PartyId, BobId, TvB, nowMs: 3000));

        var status = store.GetState(party.PartyId, nowMs: 4000);
        Assert.NotNull(status);
        Assert.Equal(1, status!.MemberCount);
    }

    [Fact]
    public void Leave_unknownParty_isFalse()
    {
        Assert.False(CreateStore().Leave(Guid.NewGuid(), AliceId, TvA, nowMs: 1000));
    }

    [Fact]
    public void WriteState_nonMember_isRejectedWithoutBumpingVersion()
    {
        var store = CreateStore();
        var party = store.CreateParty(AliceId, TvA, nowMs: 1000);

        var result = store.WriteState(party.PartyId, BobId, TvB, ItemId, 100.0, true, [], nowMs: 2000);

        Assert.Equal(WatchPartyLogic.PartyAccess.NotMember, result.Access);
        Assert.Null(result.Status);
        Assert.Equal(0, store.GetState(party.PartyId, nowMs: 3000)!.Version);
    }

    [Fact]
    public void WriteState_unknownParty_isUnknown()
    {
        var result = CreateStore().WriteState(Guid.NewGuid(), AliceId, TvA, ItemId, 0.0, true, [], nowMs: 1000);

        Assert.Equal(WatchPartyLogic.PartyAccess.Unknown, result.Access);
        Assert.Null(result.Status);
    }

    [Fact]
    public void WriteState_member_bumpsVersionAndReportsLeaderPresence()
    {
        var store = CreateStore();
        var party = store.CreateParty(AliceId, TvA, nowMs: 1000);
        store.UpsertPresence(AliceId, TvA, "TV A", null, null, null, true, nowMs: 2000);

        var result = store.WriteState(party.PartyId, AliceId, TvA, ItemId, 500.0, true, ["sib-1"], nowMs: 2000);

        Assert.Equal(WatchPartyLogic.PartyAccess.Member, result.Access);
        Assert.NotNull(result.Status);
        Assert.Equal(1, result.Status!.Version);
        Assert.True(result.Status.LeaderPresent);
        Assert.Equal(ItemId, result.Status.ItemId);
        Assert.Equal(new[] { "sib-1" }, result.Status.SiblingIds);
    }

    [Fact]
    public void GetActivePresence_dropsExpiredEntries()
    {
        var store = CreateStore();
        store.UpsertPresence(AliceId, TvA, "TV A", null, null, null, true, nowMs: 1000);
        store.UpsertPresence(BobId, TvB, "TV B", null, null, null, true, nowMs: 40_000);

        var active = store.GetActivePresence(nowMs: 46_001);

        Assert.Single(active);
        Assert.Equal(TvB, active.Single().DeviceId);
    }

    // --- Presence party membership ---

    [Fact]
    public void Presence_reportsPartyIdForLeaderAndMember()
    {
        var store = CreateStore();
        store.UpsertPresence(AliceId, TvA, "TV A", null, null, null, true, nowMs: 1000);
        store.UpsertPresence(BobId, TvB, "TV B", null, null, null, true, nowMs: 1000);
        var party = store.CreateParty(AliceId, TvA, nowMs: 2000);
        store.Join(party.PartyId, BobId, TvB, nowMs: 2000);

        var presence = store.GetActivePresence(nowMs: 2000);

        Assert.Equal(2, presence.Count);
        Assert.All(presence, p => Assert.Equal(party.PartyId, p.PartyId));
    }

    [Fact]
    public void Presence_partyIdClearedWhenLeaderLeaves()
    {
        var store = CreateStore();
        store.UpsertPresence(AliceId, TvA, "TV A", null, null, null, true, nowMs: 1000);
        store.UpsertPresence(BobId, TvB, "TV B", null, null, null, true, nowMs: 1000);
        var party = store.CreateParty(AliceId, TvA, nowMs: 2000);
        store.Join(party.PartyId, BobId, TvB, nowMs: 2000);

        store.Leave(party.PartyId, AliceId, TvA, nowMs: 3000);

        Assert.All(store.GetActivePresence(nowMs: 3000), p => Assert.Null(p.PartyId));
    }

    [Fact]
    public void Presence_partyIdClearedOnMemberLeave_onlyForThatDevice()
    {
        var store = CreateStore();
        store.UpsertPresence(AliceId, TvA, "TV A", null, null, null, true, nowMs: 1000);
        store.UpsertPresence(BobId, TvB, "TV B", null, null, null, true, nowMs: 1000);
        var party = store.CreateParty(AliceId, TvA, nowMs: 2000);
        store.Join(party.PartyId, BobId, TvB, nowMs: 2000);

        store.Leave(party.PartyId, BobId, TvB, nowMs: 3000);

        var presence = store.GetActivePresence(nowMs: 3000);
        Assert.Null(presence.Single(p => p.DeviceId == TvB).PartyId);
        Assert.Equal(party.PartyId, presence.Single(p => p.DeviceId == TvA).PartyId);
    }

    [Fact]
    public void Presence_partyIdClearedWhenThePartyIsReplaced()
    {
        var store = CreateStore();
        store.UpsertPresence(AliceId, TvA, "TV A", null, null, null, true, nowMs: 1000);
        store.UpsertPresence(BobId, TvB, "TV B", null, null, null, true, nowMs: 1000);
        var first = store.CreateParty(AliceId, TvA, nowMs: 2000);
        store.Join(first.PartyId, BobId, TvB, nowMs: 2000);

        var second = store.CreateParty(AliceId, TvA, nowMs: 3000);

        var presence = store.GetActivePresence(nowMs: 3000);
        Assert.Equal(second.PartyId, presence.Single(p => p.DeviceId == TvA).PartyId);
        Assert.Null(presence.Single(p => p.DeviceId == TvB).PartyId);
    }

    [Fact]
    public void Presence_stalePartyId_isReResolvedOnUpsert()
    {
        var store = CreateStore();
        store.UpsertPresence(AliceId, TvA, "TV A", null, null, null, true, nowMs: 1000);
        var party = store.CreateParty(AliceId, TvA, nowMs: 2000);
        store.Leave(party.PartyId, AliceId, TvA, nowMs: 3000);

        var entry = store.UpsertPresence(AliceId, TvA, "TV A", null, null, null, true, nowMs: 4000);

        Assert.Null(entry.PartyId);
    }

    // --- Join by device ---

    [Fact]
    public void JoinByDevice_targetInNoParty_createsPartyLedByTarget()
    {
        var store = CreateStore();
        store.UpsertPresence(BobId, TvB, "Mum's TV", null, null, null, true, nowMs: 1000);

        var result = store.JoinByDevice(AliceId, TvA, TvB, nowMs: 2000);

        Assert.Equal(WatchPartyLogic.JoinByDeviceOutcome.CreatedNewParty, result.Outcome);
        Assert.NotNull(result.Status);
        Assert.Equal(result.PartyId, result.Status!.PartyId);
        Assert.Equal(BobId, result.Status.LeaderUserId);
        Assert.Equal(TvB, result.Status.LeaderDeviceId);
        Assert.Equal(0, result.Status.Version);
        Assert.Null(result.Status.ItemId);
        Assert.Null(result.Status.PositionMs);
        Assert.Equal(2, result.Status.MemberCount);
        Assert.True(result.Status.LeaderPresent);

        var targetPresence = store.GetActivePresence(nowMs: 2000).Single(p => p.DeviceId == TvB);
        Assert.Equal(result.PartyId, targetPresence.PartyId);
    }

    [Fact]
    public void JoinByDevice_targetLeads_joinsItsParty()
    {
        var store = CreateStore();
        store.UpsertPresence(BobId, TvB, "Mum's TV", null, null, null, true, nowMs: 1000);
        var party = store.CreateParty(BobId, TvB, nowMs: 1000);

        var result = store.JoinByDevice(AliceId, TvA, TvB, nowMs: 2000);

        Assert.Equal(WatchPartyLogic.JoinByDeviceOutcome.JoinedExistingParty, result.Outcome);
        Assert.Equal(party.PartyId, result.PartyId);
        Assert.Equal(BobId, result.Status!.LeaderUserId);
        Assert.Equal(2, result.Status.MemberCount);
    }

    [Fact]
    public void JoinByDevice_targetIsMemberNotLeader_conflictsWithTargetParty()
    {
        var store = CreateStore();
        store.UpsertPresence(AliceId, TvA, "TV A", null, null, null, true, nowMs: 1000);
        store.UpsertPresence(BobId, TvB, "TV B", null, null, null, true, nowMs: 1000);
        var party = store.CreateParty(AliceId, TvA, nowMs: 1000);
        store.Join(party.PartyId, BobId, TvB, nowMs: 1000);

        // A second device of the same user as the leader: the caller itself is in no party.
        var result = store.JoinByDevice(AliceId, "tv-c", TvB, nowMs: 2000);

        Assert.Equal(WatchPartyLogic.JoinByDeviceOutcome.TargetIsMember, result.Outcome);
        Assert.Equal(party.PartyId, result.PartyId);
        Assert.Null(result.Status);
    }

    [Fact]
    public void JoinByDevice_staleTarget_isUnknown()
    {
        var store = CreateStore();
        store.UpsertPresence(BobId, TvB, "Mum's TV", null, null, null, true, nowMs: 1000);

        var result = store.JoinByDevice(AliceId, TvA, TvB, nowMs: 1001 + WatchPartyLogic.PresenceTtlMs);

        Assert.Equal(WatchPartyLogic.JoinByDeviceOutcome.UnknownTarget, result.Outcome);
        Assert.Null(result.Status);
        Assert.Null(result.PartyId);
    }

    [Fact]
    public void JoinByDevice_callerAlreadyInParty_conflictsWithCallerParty()
    {
        var store = CreateStore();
        store.UpsertPresence(BobId, TvB, "TV B", null, null, null, true, nowMs: 1000);
        var callerParty = store.CreateParty(AliceId, TvA, nowMs: 1000);

        var result = store.JoinByDevice(AliceId, TvA, TvB, nowMs: 2000);

        Assert.Equal(WatchPartyLogic.JoinByDeviceOutcome.CallerAlreadyInParty, result.Outcome);
        Assert.Equal(callerParty.PartyId, result.PartyId);
        Assert.Null(result.Status);
    }
}
