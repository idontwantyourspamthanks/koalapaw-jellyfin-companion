using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KoalaPaw.ServerCompanion.Recommendations;

/// <summary>
/// One rail seed: a recently played movie or series the rail grows from.
/// </summary>
public sealed record RecommendationSeed(
    Guid Id,
    string Name,
    DateTime? LastPlayedDate,
    string Type,
    string? PrimaryGenre);

/// <summary>
/// One candidate rail item, carrying everything the rail logic needs to filter, order and shape it.
/// </summary>
public sealed record RecommendationCandidate(
    Guid Id,
    string Name,
    string Type,
    string? SeriesName,
    int? ProductionYear,
    bool HasPrimaryImage,
    bool Played,
    float? CommunityRating,
    string? PrimaryGenre,
    DateTime? PremiereDate,
    Guid SeriesId);

/// <summary>One credited person, in credit order. Kind is the Jellyfin person kind ("Actor", "Director", ...).</summary>
public sealed record PersonCredit(Guid Id, string Kind);

/// <summary>Response body for GET /KoalaPaw/Recommendations/{userId}.</summary>
public sealed class RecommendationsResponse
{
    [JsonPropertyName("rails")]
    public IReadOnlyList<RecommendationRailDto> Rails { get; set; } = [];
}

/// <summary>One "Because you watched X" rail.</summary>
public sealed class RecommendationRailDto
{
    [JsonPropertyName("seedItemId")]
    public string SeedItemId { get; set; } = string.Empty;

    [JsonPropertyName("seedTitle")]
    public string SeedTitle { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("items")]
    public IReadOnlyList<RecommendationItemDto> Items { get; set; } = [];
}

/// <summary>One rail item, shaped exactly for the KoalaPaw home rails.</summary>
public sealed class RecommendationItemDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("seriesName")]
    public string? SeriesName { get; set; }

    [JsonPropertyName("productionYear")]
    public int? ProductionYear { get; set; }

    [JsonPropertyName("hasPrimaryImage")]
    public bool HasPrimaryImage { get; set; }

    [JsonPropertyName("played")]
    public bool Played { get; set; }
}
