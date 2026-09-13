using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace KoalaPaw.ServerCompanion.WatchParty;

/// <summary>
/// Watch-together persistence: parties survive a server restart (a restart must not silently
/// strand clients), presence is transient and in-memory only. Parties are written to
/// <c>watch-parties.json</c> in the plugin data folder on every mutation, mirroring
/// <see cref="Bookkeeping.AnalysisStore"/>'s load-guard and camelCase JSON options.
/// </summary>
public sealed class WatchPartyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<(Guid UserId, string DeviceId), WatchPartyLogic.PresenceEntry> _presence = new();
    private readonly Dictionary<Guid, WatchPartyLogic.PartyState> _parties = new();
    private readonly object _partiesLock = new();

    public WatchPartyStore(string dataFolderPath, ILogger<WatchPartyStore> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(dataFolderPath, "watch-parties.json");
        Load();
    }

    // --- Presence (in-memory, 45 s TTL) ---

    /// <summary>
    /// Upsert the caller device's presence. The stored entry's party id is always re-resolved from
    /// the live parties, so a device that left or whose party dissolved reports none again.
    /// </summary>
    internal WatchPartyLogic.PresenceEntry UpsertPresence(
        Guid userId,
        string deviceId,
        string? deviceName,
        Guid? itemId,
        string? itemName,
        double? positionMs,
        bool isPlaying,
        long nowMs)
    {
        var key = (userId, deviceId);
        var existing = _presence.TryGetValue(key, out var found) ? found : null;

        Guid? partyId;
        lock (_partiesLock)
        {
            partyId = WatchPartyLogic.PartyOf(_parties.Values, userId, deviceId)?.PartyId;
        }

        var entry = WatchPartyLogic.UpsertPresence(
            existing, userId, deviceId, deviceName, itemId, itemName, positionMs, isPlaying, partyId, nowMs);
        _presence[key] = entry;
        return entry;
    }

    /// <summary>Live presence, newest heartbeat first; expired entries are dropped.</summary>
    internal IReadOnlyList<WatchPartyLogic.PresenceEntry> GetActivePresence(long nowMs)
    {
        foreach (var expired in _presence.Values.Where(e => WatchPartyLogic.IsExpired(e, nowMs)))
        {
            _presence.TryRemove((expired.UserId, expired.DeviceId), out _);
        }

        return WatchPartyLogic.ActivePresence(_presence.Values, nowMs);
    }

    // --- Parties (persisted) ---

    /// <summary>
    /// Create a party led by the caller device. Any party that device already led is replaced —
    /// one active party per leader device.
    /// </summary>
    internal WatchPartyLogic.PartyState CreateParty(Guid userId, string deviceId, long nowMs)
    {
        lock (_partiesLock)
        {
            foreach (var old in _parties.Values.Where(p => WatchPartyLogic.IsLeader(p, userId, deviceId)).ToList())
            {
                _parties.Remove(old.PartyId);
                ClearPresencePartyLocked(old);
            }

            var party = WatchPartyLogic.CreateParty(Guid.NewGuid(), userId, deviceId, nowMs);
            _parties[party.PartyId] = party;
            StampPresencePartyLocked(userId, deviceId, party.PartyId);
            Save();
            return party;
        }
    }

    /// <summary>Add the caller device to a party's roster, or null when the party is unknown/idle.</summary>
    internal WatchPartyLogic.PartyStatus? Join(Guid partyId, Guid userId, string deviceId, long nowMs)
    {
        lock (_partiesLock)
        {
            if (PruneIdleLocked(nowMs))
            {
                Save();
            }

            if (!_parties.TryGetValue(partyId, out var party))
            {
                return null;
            }

            var joined = WatchPartyLogic.Join(party, userId, deviceId, nowMs);
            _parties[partyId] = joined;
            StampPresencePartyLocked(userId, deviceId, partyId);
            Save();
            return DescribeLocked(joined, nowMs);
        }
    }

    /// <summary>
    /// Join by indication: the caller taps a device that never created a party. The target must be
    /// actively present; when it leads a party the caller joins it, when it is in no party a party
    /// led by the target is created for the caller, and a target that is only a member (or a caller
    /// already in a party) is a conflict.
    /// </summary>
    internal JoinByDeviceResult JoinByDevice(
        Guid callerUserId,
        string callerDeviceId,
        string targetDeviceId,
        long nowMs)
    {
        lock (_partiesLock)
        {
            if (PruneIdleLocked(nowMs))
            {
                Save();
            }

            // Newest heartbeat wins when one device id appears under more than one user.
            var target = GetActivePresence(nowMs)
                .Where(e => string.Equals(e.DeviceId, targetDeviceId, StringComparison.Ordinal))
                .OrderByDescending(e => e.LastSeenMs)
                .FirstOrDefault();

            var callerParty = WatchPartyLogic.PartyOf(_parties.Values, callerUserId, callerDeviceId);
            var targetParty = target is null
                ? null
                : WatchPartyLogic.PartyOf(_parties.Values, target.UserId, target.DeviceId);

            var decision = WatchPartyLogic.DecideJoinByDevice(
                target is not null,
                callerParty,
                targetParty,
                target?.UserId ?? Guid.Empty,
                targetDeviceId);

            if (decision.Outcome is WatchPartyLogic.JoinByDeviceOutcome.UnknownTarget
                or WatchPartyLogic.JoinByDeviceOutcome.CallerAlreadyInParty
                or WatchPartyLogic.JoinByDeviceOutcome.TargetIsMember)
            {
                return new JoinByDeviceResult(decision.Outcome, null, decision.PartyId);
            }

            WatchPartyLogic.PartyState party;
            if (decision.Outcome == WatchPartyLogic.JoinByDeviceOutcome.CreatedNewParty)
            {
                party = WatchPartyLogic.CreateParty(Guid.NewGuid(), target!.UserId, target.DeviceId, nowMs);
                _parties[party.PartyId] = party;
            }
            else
            {
                party = targetParty!;
            }

            var joined = WatchPartyLogic.Join(party, callerUserId, callerDeviceId, nowMs);
            _parties[joined.PartyId] = joined;
            StampPresencePartyLocked(joined.LeaderUserId, joined.LeaderDeviceId, joined.PartyId);
            StampPresencePartyLocked(callerUserId, callerDeviceId, joined.PartyId);
            Save();
            return new JoinByDeviceResult(decision.Outcome, DescribeLocked(joined, nowMs), joined.PartyId);
        }
    }

    /// <summary>Current party state, or null when unknown/idle. Prunes idle parties (the idle sweep on read).</summary>
    internal WatchPartyLogic.PartyStatus? GetState(Guid partyId, long nowMs)
    {
        lock (_partiesLock)
        {
            if (PruneIdleLocked(nowMs))
            {
                Save();
            }

            if (!_parties.TryGetValue(partyId, out var party))
            {
                return null;
            }

            return DescribeLocked(party, nowMs);
        }
    }

    /// <summary>
    /// Apply a member's state write. Returns the access decision so the controller can map
    /// unknown → 404 and not-a-member → 403 without a second store round trip.
    /// </summary>
    internal PartyWriteResult WriteState(
        Guid partyId,
        Guid userId,
        string deviceId,
        Guid? itemId,
        double? positionMs,
        bool isPlaying,
        IReadOnlyList<string> siblingIds,
        long nowMs)
    {
        lock (_partiesLock)
        {
            if (PruneIdleLocked(nowMs))
            {
                Save();
            }

            var access = WatchPartyLogic.AccessFor(
                _parties.TryGetValue(partyId, out var party) ? party : null,
                userId,
                deviceId);

            if (access != WatchPartyLogic.PartyAccess.Member)
            {
                return new PartyWriteResult(access, null);
            }

            var updated = WatchPartyLogic.ApplyState(party!, itemId, positionMs, isPlaying, siblingIds, nowMs);
            _parties[partyId] = updated;
            Save();
            return new PartyWriteResult(access, DescribeLocked(updated, nowMs));
        }
    }

    /// <summary>
    /// Remove the caller device from a party. The party dissolves when the leader leaves; a leave
    /// by a non-member is a no-op. False means the party is unknown/idle (HTTP 404).
    /// </summary>
    internal bool Leave(Guid partyId, Guid userId, string deviceId, long nowMs)
    {
        lock (_partiesLock)
        {
            if (PruneIdleLocked(nowMs))
            {
                Save();
            }

            if (!_parties.TryGetValue(partyId, out var party))
            {
                return false;
            }

            if (WatchPartyLogic.IsLeader(party, userId, deviceId))
            {
                // Dissolving the party also clears every member's presence party id.
                _parties.Remove(partyId);
                ClearPresencePartyLocked(party);
            }
            else
            {
                _parties[partyId] = WatchPartyLogic.Leave(party, userId, deviceId, nowMs);
                StampPresencePartyLocked(userId, deviceId, null);
            }

            Save();
            return true;
        }
    }

    /// <summary>The access decision plus the party state a state write produced.</summary>
    internal sealed record PartyWriteResult(
        WatchPartyLogic.PartyAccess Access,
        WatchPartyLogic.PartyStatus? Status);

    /// <summary>A JoinByDevice outcome plus the state (200 paths) or conflicting party id (409 paths).</summary>
    internal sealed record JoinByDeviceResult(
        WatchPartyLogic.JoinByDeviceOutcome Outcome,
        WatchPartyLogic.PartyStatus? Status,
        Guid? PartyId);

    private WatchPartyLogic.PartyStatus DescribeLocked(WatchPartyLogic.PartyState party, long nowMs) =>
        WatchPartyLogic.Describe(party, _presence.Values, nowMs);

    /// <summary>Drop parties with no activity for the idle window. Caller holds the lock.</summary>
    private bool PruneIdleLocked(long nowMs)
    {
        var idle = _parties.Values.Where(p => WatchPartyLogic.IsIdle(p, nowMs)).ToList();
        foreach (var party in idle)
        {
            _parties.Remove(party.PartyId);
            ClearPresencePartyLocked(party);
        }

        return idle.Count > 0;
    }

    /// <summary>Point one live presence entry at a party (or at none). No-op when the device is not present.</summary>
    private void StampPresencePartyLocked(Guid userId, string deviceId, Guid? partyId)
    {
        var key = (userId, deviceId);
        if (_presence.TryGetValue(key, out var entry) && entry.PartyId != partyId)
        {
            _presence[key] = entry with { PartyId = partyId };
        }
    }

    /// <summary>Clear the party id on every present member of a party that no longer exists.</summary>
    private void ClearPresencePartyLocked(WatchPartyLogic.PartyState party)
    {
        foreach (var member in party.Members)
        {
            StampPresencePartyLocked(member.UserId, member.DeviceId, null);
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(_parties.Values.ToList(), JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WatchPartyStore: failed to flush to {Path}", _filePath);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = File.ReadAllText(_filePath);
            var parties = JsonSerializer.Deserialize<List<WatchPartyLogic.PartyState>>(json, JsonOptions);
            if (parties is null)
            {
                return;
            }

            foreach (var party in parties)
            {
                _parties[party.PartyId] = party;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WatchPartyStore: could not load {Path}, starting fresh", _filePath);
        }
    }
}
