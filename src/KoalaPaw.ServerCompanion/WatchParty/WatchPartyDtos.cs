using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KoalaPaw.ServerCompanion.WatchParty;

/// <summary>Body of POST /KoalaPaw/WatchParty/Presence (also the party heartbeat).</summary>
public sealed class PresenceRequest
{
    /// <summary>
    /// Device the client believes it is. Optional; when supplied it must equal the authenticated
    /// device id — identity always comes from the token, never the body.
    /// </summary>
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("deviceName")]
    public string? DeviceName { get; set; }

    [JsonPropertyName("itemId")]
    public Guid? ItemId { get; set; }

    [JsonPropertyName("itemName")]
    public string? ItemName { get; set; }

    [JsonPropertyName("positionMs")]
    public double? PositionMs { get; set; }

    [JsonPropertyName("isPlaying")]
    public bool IsPlaying { get; set; }
}

/// <summary>One active viewer device as GET /KoalaPaw/WatchParty/Presence reports it.</summary>
public sealed class PresenceDto
{
    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    /// <summary>Resolved server-side from Jellyfin's user manager.</summary>
    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("itemId")]
    public Guid? ItemId { get; set; }

    [JsonPropertyName("itemName")]
    public string? ItemName { get; set; }

    [JsonPropertyName("positionMs")]
    public double? PositionMs { get; set; }

    [JsonPropertyName("isPlaying")]
    public bool IsPlaying { get; set; }

    /// <summary>
    /// The party this device is currently in (as leader or member), or null when it is in none.
    /// Lets a TV discover who leads what without a second call.
    /// </summary>
    [JsonPropertyName("partyId")]
    public Guid? PartyId { get; set; }

    /// <summary>Server stamp (Unix ms) of the last heartbeat.</summary>
    [JsonPropertyName("lastSeenMs")]
    public long LastSeenMs { get; set; }
}

/// <summary>Body of POST /KoalaPaw/WatchParty/Join.</summary>
public sealed class JoinPartyRequest
{
    [JsonPropertyName("partyId")]
    public Guid PartyId { get; set; }
}

/// <summary>Body of POST /KoalaPaw/WatchParty/State — a member's state write + heartbeat.</summary>
public sealed class PartyStateRequest
{
    [JsonPropertyName("partyId")]
    public Guid PartyId { get; set; }

    [JsonPropertyName("itemId")]
    public Guid? ItemId { get; set; }

    [JsonPropertyName("positionMs")]
    public double? PositionMs { get; set; }

    [JsonPropertyName("isPlaying")]
    public bool IsPlaying { get; set; }

    /// <summary>Opaque client-side sibling ids, echoed back verbatim.</summary>
    [JsonPropertyName("siblingIds")]
    public List<string>? SiblingIds { get; set; }
}

/// <summary>Body of POST /KoalaPaw/WatchParty/Leave.</summary>
public sealed class LeavePartyRequest
{
    [JsonPropertyName("partyId")]
    public Guid PartyId { get; set; }
}

/// <summary>Body of POST /KoalaPaw/WatchParty/JoinByDevice — join the party of a device by indication.</summary>
public sealed class JoinByDeviceRequest
{
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }
}

/// <summary>
/// Body of a 409 from POST /JoinByDevice: the party the client can Join directly (target is a
/// member) or must Leave first (caller is already in a party).
/// </summary>
public sealed class PartyConflictDto
{
    /// <summary>"callerInParty" or "targetIsMember".</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("partyId")]
    public Guid PartyId { get; set; }
}

/// <summary>Response of POST /KoalaPaw/WatchParty/Create.</summary>
public sealed class CreatePartyResponse
{
    [JsonPropertyName("partyId")]
    public Guid PartyId { get; set; }
}

/// <summary>The party state GET/Join/POST State return.</summary>
public sealed class WatchPartyStateDto
{
    [JsonPropertyName("partyId")]
    public Guid PartyId { get; set; }

    [JsonPropertyName("leaderUserId")]
    public Guid LeaderUserId { get; set; }

    [JsonPropertyName("leaderDeviceId")]
    public string LeaderDeviceId { get; set; } = string.Empty;

    [JsonPropertyName("itemId")]
    public Guid? ItemId { get; set; }

    [JsonPropertyName("positionMs")]
    public double? PositionMs { get; set; }

    [JsonPropertyName("isPlaying")]
    public bool IsPlaying { get; set; }

    [JsonPropertyName("siblingIds")]
    public IReadOnlyList<string> SiblingIds { get; set; } = [];

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("memberCount")]
    public int MemberCount { get; set; }

    /// <summary>False once the leader's presence has expired — clients then dissolve the party.</summary>
    [JsonPropertyName("leaderPresent")]
    public bool LeaderPresent { get; set; }
}
