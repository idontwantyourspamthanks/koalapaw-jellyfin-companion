using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace KoalaPaw.ServerCompanion.WatchParty;

/// <summary>
/// Watch-together for same-server KoalaPaw TVs — the Jellyfin server is the rendezvous when the
/// TVs sit on different networks. Presence is per (user, device); parties are led by one device
/// and joined by others watching the same profile.
/// </summary>
[ApiController]
[Authorize] // any authenticated user, kid profiles included — deliberately NOT RequiresElevation
[Route("KoalaPaw/WatchParty")]
public class WatchPartyController : ControllerBase
{
    /// <summary>
    /// The identity claims Jellyfin's authentication handler stamps on every user token
    /// (Jellyfin.Api.Constants.InternalClaimTypes — not referenceable from a plugin). Identity is
    /// always (userId, deviceId) from the token, never from a request body.
    /// </summary>
    private const string UserIdClaimType = "Jellyfin-UserId";

    private const string DeviceIdClaimType = "Jellyfin-DeviceId";
    private const string DeviceNameClaimType = "Jellyfin-Device";

    private readonly WatchPartyStore _store;
    private readonly IUserManager _userManager;

    public WatchPartyController(WatchPartyStore store, IUserManager userManager)
    {
        _store = store;
        _userManager = userManager;
    }

    /// <summary>Heartbeat upsert of the caller device's presence; also the party heartbeat.</summary>
    [HttpPost("Presence")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<PresenceDto> PostPresence(PresenceRequest request)
    {
        if (!TryGetCaller(out var userId, out var deviceId, out var deviceName))
        {
            return Unauthorized();
        }

        if (!string.IsNullOrWhiteSpace(request.DeviceId)
            && !string.Equals(request.DeviceId, deviceId, StringComparison.Ordinal))
        {
            return BadRequest();
        }

        var entry = _store.UpsertPresence(
            userId,
            deviceId,
            request.DeviceName ?? deviceName,
            request.ItemId,
            request.ItemName,
            request.PositionMs,
            request.IsPlaying,
            NowMs());

        return Ok(ToDto(entry));
    }

    /// <summary>Every device still heartbeating in the last 45 s, newest heartbeat first.</summary>
    [HttpGet("Presence")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<PresenceDto>> GetPresence()
    {
        var entries = _store.GetActivePresence(NowMs());
        var names = new Dictionary<Guid, string>();
        var dtos = new List<PresenceDto>(entries.Count);

        foreach (var entry in entries)
        {
            if (!names.TryGetValue(entry.UserId, out var userName))
            {
                userName = _userManager.GetUserById(entry.UserId)?.Username ?? string.Empty;
                names[entry.UserId] = userName;
            }

            dtos.Add(ToDto(entry, userName));
        }

        return Ok(dtos);
    }

    /// <summary>Start a party led by the caller device; replaces any party that device already led.</summary>
    [HttpPost("Create")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<CreatePartyResponse> Create()
    {
        if (!TryGetCaller(out var userId, out var deviceId, out _))
        {
            return Unauthorized();
        }

        var party = _store.CreateParty(userId, deviceId, NowMs());
        return Ok(new CreatePartyResponse { PartyId = party.PartyId });
    }

    /// <summary>Join the caller device to a party.</summary>
    [HttpPost("Join")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<WatchPartyStateDto> Join(JoinPartyRequest request)
    {
        if (!TryGetCaller(out var userId, out var deviceId, out _))
        {
            return Unauthorized();
        }

        var status = _store.Join(request.PartyId, userId, deviceId, NowMs());
        return status is null ? NotFound() : Ok(ToDto(status));
    }

    /// <summary>
    /// Join by indication: the caller tapped another active device that may never have created a
    /// party. The target leads a party → join it; the target is in no party → create one led by the
    /// target; the target is only a member, or the caller is already in a party → 409 with that
    /// party's id so the client can Join it directly or Leave first.
    /// </summary>
    [HttpPost("JoinByDevice")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<WatchPartyStateDto> JoinByDevice(JoinByDeviceRequest request)
    {
        if (!TryGetCaller(out var userId, out var deviceId, out _))
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.DeviceId))
        {
            return BadRequest();
        }

        var result = _store.JoinByDevice(userId, deviceId, request.DeviceId, NowMs());

        return result.Outcome switch
        {
            WatchPartyLogic.JoinByDeviceOutcome.UnknownTarget => NotFound(),
            WatchPartyLogic.JoinByDeviceOutcome.CallerAlreadyInParty => ConflictFor(result, "callerInParty"),
            WatchPartyLogic.JoinByDeviceOutcome.TargetIsMember => ConflictFor(result, "targetIsMember"),
            _ => Ok(ToDto(result.Status!)),
        };
    }

    /// <summary>Member state write — bumps the party version and refreshes the writer's presence.</summary>
    [HttpPost("State")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<WatchPartyStateDto> PostState(PartyStateRequest request)
    {
        if (!TryGetCaller(out var userId, out var deviceId, out var deviceName))
        {
            return Unauthorized();
        }

        var nowMs = NowMs();

        // Doubles as the heartbeat: keep this device's presence (and the leaderPresent flag) fresh.
        _store.UpsertPresence(
            userId, deviceId, deviceName, request.ItemId, null, request.PositionMs, request.IsPlaying, nowMs);

        var result = _store.WriteState(
            request.PartyId,
            userId,
            deviceId,
            request.ItemId,
            request.PositionMs,
            request.IsPlaying,
            request.SiblingIds ?? [],
            nowMs);

        return result.Access switch
        {
            WatchPartyLogic.PartyAccess.Unknown => NotFound(),
            WatchPartyLogic.PartyAccess.NotMember => Forbid(),
            _ => Ok(ToDto(result.Status!)),
        };
    }

    /// <summary>
    /// Current party state. leaderPresent=false once the leader's presence expired — clients then
    /// dissolve; idle parties (no state write for 10 min) are gone and answer 404.
    /// </summary>
    [HttpGet("State")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<WatchPartyStateDto> GetState(Guid partyId)
    {
        var status = _store.GetState(partyId, NowMs());
        return status is null ? NotFound() : Ok(ToDto(status));
    }

    /// <summary>Leave the party. The party dissolves when the leader leaves.</summary>
    [HttpPost("Leave")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Leave(LeavePartyRequest request)
    {
        if (!TryGetCaller(out var userId, out var deviceId, out _))
        {
            return Unauthorized();
        }

        return _store.Leave(request.PartyId, userId, deviceId, NowMs()) ? NoContent() : NotFound();
    }

    /// <summary>Server clock in Unix ms — the single time source for presence TTL and idle sweeps.</summary>
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static ConflictObjectResult ConflictFor(WatchPartyStore.JoinByDeviceResult result, string reason) =>
        new(new PartyConflictDto { Reason = reason, PartyId = result.PartyId ?? Guid.Empty });

    /// <summary>Caller identity from the token: (userId, deviceId) plus the device's display name.</summary>
    private bool TryGetCaller(out Guid userId, out string deviceId, out string deviceName)
    {
        userId = Guid.Empty;
        deviceId = string.Empty;
        deviceName = string.Empty;

        var userIdClaim = User.FindFirst(UserIdClaimType)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out userId))
        {
            return false;
        }

        var deviceIdClaim = User.FindFirst(DeviceIdClaimType)?.Value;
        if (string.IsNullOrWhiteSpace(deviceIdClaim))
        {
            return false;
        }

        deviceId = deviceIdClaim;
        deviceName = User.FindFirst(DeviceNameClaimType)?.Value ?? string.Empty;
        return true;
    }

    private PresenceDto ToDto(WatchPartyLogic.PresenceEntry entry)
        => ToDto(entry, _userManager.GetUserById(entry.UserId)?.Username ?? string.Empty);

    private static PresenceDto ToDto(WatchPartyLogic.PresenceEntry entry, string userName) =>
        new()
        {
            UserId = entry.UserId,
            UserName = userName,
            DeviceId = entry.DeviceId,
            DeviceName = entry.DeviceName,
            ItemId = entry.ItemId,
            ItemName = entry.ItemName,
            PositionMs = entry.PositionMs,
            IsPlaying = entry.IsPlaying,
            PartyId = entry.PartyId,
            LastSeenMs = entry.LastSeenMs,
        };

    private static WatchPartyStateDto ToDto(WatchPartyLogic.PartyStatus status) =>
        new()
        {
            PartyId = status.PartyId,
            LeaderUserId = status.LeaderUserId,
            LeaderDeviceId = status.LeaderDeviceId,
            ItemId = status.ItemId,
            PositionMs = status.PositionMs,
            IsPlaying = status.IsPlaying,
            SiblingIds = status.SiblingIds,
            Version = status.Version,
            MemberCount = status.MemberCount,
            LeaderPresent = status.LeaderPresent,
        };
}
