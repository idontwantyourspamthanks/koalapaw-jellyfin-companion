using System;
using System.Collections.Generic;
using System.Linq;

namespace KoalaPaw.ServerCompanion.WatchParty;

/// <summary>
/// Pure decisions behind watch-together: presence upsert/TTL pruning and party membership,
/// version and dissolution rules. Nothing here reads a clock or touches I/O — every entry point
/// takes <c>nowMs</c>, so the 45 s presence TTL and 10 min idle sweep are unit-testable without
/// Jellyfin plumbing (same extracted-static style as SegmentScanTask/LoudnessScanTask).
/// </summary>
internal static class WatchPartyLogic
{
    /// <summary>Presence entries with no heartbeat for longer than this are gone (devices beat ~15 s).</summary>
    public const long PresenceTtlMs = 45_000;

    /// <summary>A party with no state write for longer than this is dissolved on the next read.</summary>
    public const long IdlePartyMs = 10 * 60 * 1000;

    /// <summary>
    /// One device's viewing state. Presence is transient and never persisted; <see cref="PartyId"/>
    /// is the party this device is currently in (leader or member), so a TV can discover who leads
    /// what without a second call.
    /// </summary>
    internal sealed record PresenceEntry(
        Guid UserId,
        string DeviceId,
        string DeviceName,
        Guid? ItemId,
        string? ItemName,
        double? PositionMs,
        bool IsPlaying,
        Guid? PartyId,
        long LastSeenMs);

    /// <summary>One device signed into a party — identity is the (userId, deviceId) pair.</summary>
    internal sealed record PartyMember(Guid UserId, string DeviceId);

    /// <summary>A watch party's persisted state: server-owned version, roster and last activity stamp.</summary>
    internal sealed record PartyState(
        Guid PartyId,
        Guid LeaderUserId,
        string LeaderDeviceId,
        Guid? ItemId,
        double? PositionMs,
        bool IsPlaying,
        IReadOnlyList<string> SiblingIds,
        int Version,
        long LastActivityMs,
        IReadOnlyList<PartyMember> Members);

    /// <summary>The read model GET /State returns, with the presence-derived leaderPresent flag.</summary>
    internal sealed record PartyStatus(
        Guid PartyId,
        Guid LeaderUserId,
        string LeaderDeviceId,
        Guid? ItemId,
        double? PositionMs,
        bool IsPlaying,
        IReadOnlyList<string> SiblingIds,
        int Version,
        int MemberCount,
        bool LeaderPresent);

    /// <summary>Who may write party state — the membership truth table behind POST /State.</summary>
    internal enum PartyAccess
    {
        /// <summary>No such party, or it idled out — HTTP 404.</summary>
        Unknown,

        /// <summary>The party exists but this device is not on the roster — HTTP 403.</summary>
        NotMember,

        /// <summary>This device is on the roster — the write applies.</summary>
        Member,
    }

    /// <summary>Outcome of POST /JoinByDevice — the join-by-indication flow's decision table.</summary>
    internal enum JoinByDeviceOutcome
    {
        /// <summary>The target device has no live presence — HTTP 404.</summary>
        UnknownTarget,

        /// <summary>The caller is already in a party and must Leave first — HTTP 409.</summary>
        CallerAlreadyInParty,

        /// <summary>The target is in a party but does not lead it; the client should Join it — HTTP 409.</summary>
        TargetIsMember,

        /// <summary>The target leads a party the caller was added to — HTTP 200.</summary>
        JoinedExistingParty,

        /// <summary>The target was in no party, so one was created led by the target — HTTP 200.</summary>
        CreatedNewParty,
    }

    /// <summary>The JoinByDevice decision plus the party id the 409 body hands back.</summary>
    internal sealed record JoinByDeviceDecision(JoinByDeviceOutcome Outcome, Guid? PartyId);

    /// <summary>
    /// Upsert one device's presence entry. Blank device name and null item/position mean
    /// "unchanged" so a party state heartbeat (which carries no device name) cannot wipe them;
    /// <paramref name="isPlaying"/> and <paramref name="partyId"/> always apply because they are
    /// resolved by the caller on every write.
    /// </summary>
    internal static PresenceEntry UpsertPresence(
        PresenceEntry? existing,
        Guid userId,
        string deviceId,
        string? deviceName,
        Guid? itemId,
        string? itemName,
        double? positionMs,
        bool isPlaying,
        Guid? partyId,
        long nowMs) =>
        new(
            userId,
            deviceId,
            string.IsNullOrWhiteSpace(deviceName) ? existing?.DeviceName ?? string.Empty : deviceName,
            itemId ?? existing?.ItemId,
            itemName ?? existing?.ItemName,
            positionMs ?? existing?.PositionMs,
            isPlaying,
            partyId,
            nowMs);

    /// <summary>True once a presence entry has missed its heartbeat window.</summary>
    internal static bool IsExpired(PresenceEntry entry, long nowMs) =>
        nowMs - entry.LastSeenMs > PresenceTtlMs;

    /// <summary>Live presence entries, newest heartbeat first.</summary>
    internal static IReadOnlyList<PresenceEntry> ActivePresence(IEnumerable<PresenceEntry> entries, long nowMs) =>
        entries
            .Where(e => !IsExpired(e, nowMs))
            .OrderByDescending(e => e.LastSeenMs)
            .ToList();

    /// <summary>True when this exact device is still heartbeating (the leaderPresent rule).</summary>
    internal static bool IsPresent(IEnumerable<PresenceEntry> entries, Guid userId, string deviceId, long nowMs) =>
        entries.Any(e =>
            e.UserId == userId
            && string.Equals(e.DeviceId, deviceId, StringComparison.Ordinal)
            && !IsExpired(e, nowMs));

    /// <summary>True when this device leads the party.</summary>
    internal static bool IsLeader(PartyState party, Guid userId, string deviceId) =>
        party.LeaderUserId == userId
        && string.Equals(party.LeaderDeviceId, deviceId, StringComparison.Ordinal);

    /// <summary>True when this device is on the party roster.</summary>
    internal static bool IsMember(PartyState party, Guid userId, string deviceId) =>
        party.Members.Any(m => m.UserId == userId && string.Equals(m.DeviceId, deviceId, StringComparison.Ordinal));

    /// <summary>The party this device is in, as leader or member; null when it is in none.</summary>
    internal static PartyState? PartyOf(IEnumerable<PartyState> parties, Guid userId, string deviceId) =>
        parties.FirstOrDefault(p => IsLeader(p, userId, deviceId) || IsMember(p, userId, deviceId));

    /// <summary>
    /// The POST /State access decision, extracted as a pure static so the 404-before-403
    /// precedence (unknown party beats not-a-member) is testable without Jellyfin plumbing.
    /// </summary>
    internal static PartyAccess AccessFor(PartyState? party, Guid userId, string deviceId) =>
        party is null
            ? PartyAccess.Unknown
            : IsMember(party, userId, deviceId) ? PartyAccess.Member : PartyAccess.NotMember;

    /// <summary>
    /// The POST /JoinByDevice decision table, extracted as a pure static. Precedence: target
    /// liveness first (404 beats 409), then the caller's own blocking state, then the target's.
    /// </summary>
    internal static JoinByDeviceDecision DecideJoinByDevice(
        bool targetPresent,
        PartyState? callerParty,
        PartyState? targetParty,
        Guid targetUserId,
        string targetDeviceId)
    {
        if (!targetPresent)
        {
            return new JoinByDeviceDecision(JoinByDeviceOutcome.UnknownTarget, null);
        }

        if (callerParty is not null)
        {
            return new JoinByDeviceDecision(JoinByDeviceOutcome.CallerAlreadyInParty, callerParty.PartyId);
        }

        if (targetParty is null)
        {
            return new JoinByDeviceDecision(JoinByDeviceOutcome.CreatedNewParty, null);
        }

        return IsLeader(targetParty, targetUserId, targetDeviceId)
            ? new JoinByDeviceDecision(JoinByDeviceOutcome.JoinedExistingParty, targetParty.PartyId)
            : new JoinByDeviceDecision(JoinByDeviceOutcome.TargetIsMember, targetParty.PartyId);
    }

    /// <summary>A fresh party at version 0 with the leader device already on the roster.</summary>
    internal static PartyState CreateParty(Guid partyId, Guid userId, string deviceId, long nowMs) =>
        new(partyId, userId, deviceId, null, null, false, [], 0, nowMs, [new PartyMember(userId, deviceId)]);

    /// <summary>Add the caller device to the roster (idempotent) and mark the party active.</summary>
    internal static PartyState Join(PartyState party, Guid userId, string deviceId, long nowMs) =>
        IsMember(party, userId, deviceId)
            ? party with { LastActivityMs = nowMs }
            : party with { Members = [.. party.Members, new PartyMember(userId, deviceId)], LastActivityMs = nowMs };

    /// <summary>Remove the caller device from the roster and mark the party active.</summary>
    internal static PartyState Leave(PartyState party, Guid userId, string deviceId, long nowMs) =>
        party with
        {
            Members = party.Members
                .Where(m => !(m.UserId == userId && string.Equals(m.DeviceId, deviceId, StringComparison.Ordinal)))
                .ToList(),
            LastActivityMs = nowMs,
        };

    /// <summary>Apply a member's state write, bumping the server-owned version.</summary>
    internal static PartyState ApplyState(
        PartyState party,
        Guid? itemId,
        double? positionMs,
        bool isPlaying,
        IReadOnlyList<string> siblingIds,
        long nowMs) =>
        party with
        {
            ItemId = itemId,
            PositionMs = positionMs,
            IsPlaying = isPlaying,
            SiblingIds = siblingIds,
            Version = party.Version + 1,
            LastActivityMs = nowMs,
        };

    /// <summary>True when a party has had no activity for the idle window and must dissolve.</summary>
    internal static bool IsIdle(PartyState party, long nowMs) =>
        nowMs - party.LastActivityMs > IdlePartyMs;

    /// <summary>
    /// Read model for GET /State: leaderPresent goes false once the leader's presence expires,
    /// which is the signal clients use to dissolve the party on their side.
    /// </summary>
    internal static PartyStatus Describe(PartyState party, IEnumerable<PresenceEntry> presence, long nowMs) =>
        new(
            party.PartyId,
            party.LeaderUserId,
            party.LeaderDeviceId,
            party.ItemId,
            party.PositionMs,
            party.IsPlaying,
            party.SiblingIds,
            party.Version,
            party.Members.Count,
            IsPresent(presence, party.LeaderUserId, party.LeaderDeviceId, nowMs));
}
