using System;
using System.Collections.Generic;
using System.Linq;

namespace KoalaPaw.ServerCompanion.Recommendations;

/// <summary>
/// Pure rail-selection logic for "Because you watched X": seed picking, strategy precedence, seed
/// exclusion, unplayed-first ordering and the 3–12 item window. No Jellyfin dependencies —
/// everything arrives as plain records so the rules can be unit-tested in isolation.
/// </summary>
public static class RecommendationEngine
{
    /// <summary>Rail seeds are capped at the three most recently played items.</summary>
    public const int MaxSeeds = 3;

    /// <summary>Rails with fewer than this many items are dropped.</summary>
    public const int MinRailItems = 3;

    /// <summary>Rails never show more than this many items.</summary>
    public const int MaxRailItems = 12;

    /// <summary>
    /// Merge the recently played movie and series candidates into the final seed list: most recent
    /// play first, de-duplicated by item id, capped at <paramref name="maxSeeds"/>.
    /// </summary>
    public static IReadOnlyList<RecommendationSeed> PickSeeds(
        IReadOnlyList<RecommendationSeed> recentMovies,
        IReadOnlyList<RecommendationSeed> recentSeries,
        int maxSeeds = MaxSeeds)
    {
        return recentMovies
            .Concat(recentSeries)
            .Where(seed => !seed.Id.Equals(Guid.Empty))
            .GroupBy(seed => seed.Id)
            .Select(group => group.OrderByDescending(seed => seed.LastPlayedDate).First())
            .OrderByDescending(seed => seed.LastPlayedDate)
            .ThenBy(seed => seed.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxSeeds)
            .ToList();
    }

    /// <summary>
    /// The featured person for a seed: the top-billed actor in credit order, falling back to the
    /// first director when the seed has no credited actors.
    /// </summary>
    public static Guid? PickFeaturedPerson(IReadOnlyList<PersonCredit> credits)
    {
        var actor = credits.FirstOrDefault(credit => string.Equals(credit.Kind, "Actor", StringComparison.OrdinalIgnoreCase));
        if (actor is not null)
        {
            return actor.Id;
        }

        return credits
            .FirstOrDefault(credit => string.Equals(credit.Kind, "Director", StringComparison.OrdinalIgnoreCase))
            ?.Id;
    }

    /// <summary>
    /// Build one rail for a seed. The first strategy that yields at least <paramref name="minItems"/>
    /// items wins — collection, then person, then genre. Returns null when no strategy produces a
    /// full rail. The seed itself (and, for series seeds, the seed series' own episodes) is always excluded.
    /// </summary>
    public static RecommendationRailDto? BuildRail(
        RecommendationSeed seed,
        IReadOnlyList<RecommendationCandidate> collectionCandidates,
        IReadOnlyList<RecommendationCandidate> personCandidates,
        IReadOnlyList<RecommendationCandidate> genreCandidates,
        int minItems = MinRailItems,
        int maxItems = MaxRailItems)
    {
        var collection = OrderCollection(ExcludeSeed(collectionCandidates, seed));
        if (collection.Count >= minItems)
        {
            return Build(seed, "collection", collection, maxItems);
        }

        var person = OrderPerson(ExcludeSeed(personCandidates, seed));
        if (person.Count >= minItems)
        {
            return Build(seed, "person", person, maxItems);
        }

        var genre = OrderPerson(ExcludeSeed(genreCandidates, seed)
            .Where(candidate => MatchesPrimaryGenre(candidate, seed.PrimaryGenre)));
        if (genre.Count >= minItems)
        {
            return Build(seed, "genre", genre, maxItems);
        }

        return null;
    }

    private static IEnumerable<RecommendationCandidate> ExcludeSeed(
        IEnumerable<RecommendationCandidate> candidates,
        RecommendationSeed seed)
    {
        var seedIsSeries = string.Equals(seed.Type, "Series", StringComparison.OrdinalIgnoreCase);
        return candidates
            .Where(candidate => !candidate.Id.Equals(seed.Id))
            .Where(candidate => !seedIsSeries || !candidate.SeriesId.Equals(seed.Id));
    }

    private static bool MatchesPrimaryGenre(RecommendationCandidate candidate, string? seedGenre)
        => !string.IsNullOrEmpty(seedGenre)
           && string.Equals(candidate.PrimaryGenre, seedGenre, StringComparison.OrdinalIgnoreCase);

    /// <summary>Box-set members: unplayed first, then chronological premiere date (Jellyfin's box-set order), undated last.</summary>
    private static IReadOnlyList<RecommendationCandidate> OrderCollection(IEnumerable<RecommendationCandidate> candidates)
        => candidates
            .OrderBy(candidate => candidate.Played)
            .ThenBy(candidate => candidate.PremiereDate.HasValue ? 0 : 1)
            .ThenBy(candidate => candidate.PremiereDate)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Person and genre picks: unplayed first, then community rating, then name.</summary>
    private static IReadOnlyList<RecommendationCandidate> OrderPerson(IEnumerable<RecommendationCandidate> candidates)
        => candidates
            .OrderBy(candidate => candidate.Played)
            .ThenByDescending(candidate => candidate.CommunityRating)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static RecommendationRailDto Build(
        RecommendationSeed seed,
        string reason,
        IEnumerable<RecommendationCandidate> ordered,
        int maxItems)
    {
        return new RecommendationRailDto
        {
            SeedItemId = seed.Id.ToString("N"),
            SeedTitle = seed.Name,
            Reason = reason,
            Items = ordered.Take(maxItems).Select(ToDto).ToList(),
        };
    }

    private static RecommendationItemDto ToDto(RecommendationCandidate candidate)
    {
        return new RecommendationItemDto
        {
            Id = candidate.Id.ToString("N"),
            Name = candidate.Name,
            Type = candidate.Type,
            SeriesName = candidate.SeriesName,
            ProductionYear = candidate.ProductionYear,
            HasPrimaryImage = candidate.HasPrimaryImage,
            Played = candidate.Played,
        };
    }
}
