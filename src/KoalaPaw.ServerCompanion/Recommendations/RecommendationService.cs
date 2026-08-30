using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace KoalaPaw.ServerCompanion.Recommendations;

/// <summary>
/// Builds the "Because you watched X" rails for one user: recently played movies and series become
/// seeds, and each seed grows into a rail via the first matching strategy (box set → person → genre).
/// All queries are synchronous ILibraryManager calls — no external services.
/// </summary>
public sealed class RecommendationService
{
    /// <summary>How many recently played episodes to scan for series seeds (enough for 3 distinct series).</summary>
    private const int RecentEpisodeScan = 15;

    /// <summary>Upper bound on candidates fetched per strategy before the engine narrows and caps them.</summary>
    private const int CandidateScanLimit = 40;

    private readonly ILibraryManager _libraryManager;

    public RecommendationService(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>The recommendation rails for <paramref name="user"/>, newest seed first.</summary>
    public IReadOnlyList<RecommendationRailDto> GetRecommendations(User user)
    {
        var seeds = RecommendationEngine.PickSeeds(GetRecentMovieSeeds(user), GetRecentSeriesSeeds(user));

        return SelectRails(seeds, seed => BuildRail(user, seed));
    }

    /// <summary>
    /// Builds one rail per picked seed, in seed order, dropping seeds that produced none (e.g.
    /// their item vanished from the library). Extracted as a delegate-taking static so the
    /// skip-null rule is unit-testable without Jellyfin plumbing (ILibraryManager has no test
    /// fakes in this repo).
    /// </summary>
    internal static IReadOnlyList<RecommendationRailDto> SelectRails(
        IReadOnlyList<RecommendationSeed> seeds,
        Func<RecommendationSeed, RecommendationRailDto?> buildRail)
    {
        var rails = new List<RecommendationRailDto>(seeds.Count);
        foreach (var seed in seeds)
        {
            var rail = buildRail(seed);
            if (rail is not null)
            {
                rails.Add(rail);
            }
        }

        return rails;
    }

    private IReadOnlyList<RecommendationSeed> GetRecentMovieSeeds(User user)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.Movie],
                Recursive = true,
                IsPlayed = true,
                OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
                Limit = RecommendationEngine.MaxSeeds,
            })
            .Select(item => ToSeed(item, user, GetUserData(item, user)?.LastPlayedDate))
            .ToList();
    }

    /// <summary>Series seeds come from recently played episodes, grouped by series and dated by the newest episode play.</summary>
    private IReadOnlyList<RecommendationSeed> GetRecentSeriesSeeds(User user)
    {
        var playedEpisodes = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false,
                IsPlayed = true,
                OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
                Limit = RecentEpisodeScan,
            })
            .OfType<Episode>()
            .ToList();

        var seriesPlays = RollupSeriesSeeds(playedEpisodes
            .Select(episode => (SeriesId: episode.SeriesId, LastPlayedDate: GetUserData(episode, user)?.LastPlayedDate)));

        return seriesPlays
            .Select(result => ToSeed(_libraryManager.GetItemById(result.SeriesId), user, result.LastPlayedDate))
            .Where(seed => !seed.Id.Equals(Guid.Empty))
            .ToList();
    }

    /// <summary>
    /// Collapses per-episode play rows into one row per series: rows without a series id and
    /// series without any played episode are dropped, each series is dated by its newest episode
    /// play, newest first, capped at <paramref name="maxSeeds"/>. Extracted as a static so the
    /// rollup is unit-testable without Jellyfin plumbing (ILibraryManager has no test fakes in
    /// this repo).
    /// </summary>
    internal static IReadOnlyList<(Guid SeriesId, DateTime? LastPlayedDate)> RollupSeriesSeeds(
        IEnumerable<(Guid SeriesId, DateTime? LastPlayedDate)> episodePlays,
        int maxSeeds = RecommendationEngine.MaxSeeds)
    {
        return episodePlays
            .GroupBy(play => play.SeriesId)
            .Where(group => !group.Key.Equals(Guid.Empty))
            .Select(group => (SeriesId: group.Key, LastPlayedDate: group.Max(play => play.LastPlayedDate)))
            .Where(play => play.LastPlayedDate.HasValue)
            .OrderByDescending(play => play.LastPlayedDate)
            .Take(maxSeeds)
            .ToList();
    }

    private RecommendationRailDto? BuildRail(User user, RecommendationSeed seed)
    {
        var seedItem = _libraryManager.GetItemById(seed.Id);
        if (seedItem is null)
        {
            return null;
        }

        var collectionCandidates = GetCollectionCandidates(user, seedItem);
        var personCandidates = GetPersonCandidates(user, seedItem);
        var genreCandidates = string.IsNullOrEmpty(seed.PrimaryGenre)
            ? []
            : GetGenreCandidates(user, seed.PrimaryGenre);

        return RecommendationEngine.BuildRail(seed, collectionCandidates, personCandidates, genreCandidates);
    }

    /// <summary>The other members of the first box set that contains the seed.</summary>
    private IReadOnlyList<RecommendationCandidate> GetCollectionCandidates(User user, BaseItem seedItem)
    {
        var boxSets = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            Recursive = true,
        });

        var boxSet = boxSets.OfType<BoxSet>().FirstOrDefault(b => b.ContainsLinkedChildByItemId(seedItem.Id));
        if (boxSet is null)
        {
            return [];
        }

        return SelectCollectionCandidates(boxSet.GetLinkedChildren(user), user);
    }

    /// <summary>
    /// Shapes a box set's linked children into rail candidates: only playable kinds (movies,
    /// series, episodes) qualify, kept in the box set's own order. Extracted as a static so the
    /// kind filter and mapping are unit-testable with directly constructed Jellyfin entities.
    /// </summary>
    internal static IReadOnlyList<RecommendationCandidate> SelectCollectionCandidates(
        IEnumerable<BaseItem> linkedChildren,
        User user)
        => linkedChildren
            .Where(item => item is Movie or Series or Episode)
            .Select(item => ToCandidate(item, user))
            .ToList();

    /// <summary>The featured person's other movies and episodes, or nothing when the seed has no credited actors or directors.</summary>
    private IReadOnlyList<RecommendationCandidate> GetPersonCandidates(User user, BaseItem seedItem)
    {
        var personId = RecommendationEngine.PickFeaturedPerson(ToPersonCredits(_libraryManager.GetPeople(seedItem)));
        if (!personId.HasValue)
        {
            return [];
        }

        return _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false,
                PersonIds = [personId.Value],
                Limit = CandidateScanLimit,
            })
            .Select(item => ToCandidate(item, user))
            .ToList();
    }

    /// <summary>
    /// Bridges Jellyfin's credited people onto the plain credit rows the engine matches on:
    /// PersonKind becomes its name ("Actor"/"Director"/...), credit order preserved. Extracted as
    /// a static so the kind-to-name mapping is unit-testable with directly constructed entities.
    /// </summary>
    internal static IReadOnlyList<PersonCredit> ToPersonCredits(IEnumerable<PersonInfo> people)
        => people
            .Select(person => new PersonCredit(person.Id, person.Type.ToString()))
            .ToList();

    /// <summary>Library items sharing the seed's primary genre (the engine enforces primary-genre equality).</summary>
    private IReadOnlyList<RecommendationCandidate> GetGenreCandidates(User user, string primaryGenre)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false,
                Genres = [primaryGenre],
                Limit = CandidateScanLimit,
            })
            .Select(item => ToCandidate(item, user))
            .ToList();
    }

    /// <summary>
    /// Shapes a library item into a seed record; items deleted from the library collapse to the
    /// empty sentinel seed (Guid.Empty id) that callers filter out. Internal static so the
    /// mapping is unit-testable with directly constructed Jellyfin entities.
    /// </summary>
    internal static RecommendationSeed ToSeed(BaseItem? item, User user, DateTime? lastPlayedDate)
    {
        if (item is null)
        {
            return new RecommendationSeed(Guid.Empty, string.Empty, null, string.Empty, null);
        }

        return new RecommendationSeed(
            item.Id,
            item.Name ?? string.Empty,
            lastPlayedDate,
            item.GetClientTypeName(),
            item.Genres.FirstOrDefault());
    }

    /// <summary>
    /// Shapes a library item into a candidate record, reading play state from the requesting
    /// user's cached user-data row. Internal static so the mapping is unit-testable with directly
    /// constructed Jellyfin entities.
    /// </summary>
    internal static RecommendationCandidate ToCandidate(BaseItem item, User user)
    {
        var userData = GetUserData(item, user);
        return new RecommendationCandidate(
            item.Id,
            item.Name ?? string.Empty,
            item.GetClientTypeName(),
            item is Episode episode ? episode.SeriesName : null,
            item.ProductionYear,
            item.HasImage(ImageType.Primary, 0),
            userData?.Played ?? false,
            item.CommunityRating,
            item.Genres.FirstOrDefault(),
            item.PremiereDate,
            item is Episode ep ? ep.SeriesId : Guid.Empty);
    }

    private static UserData? GetUserData(BaseItem item, User user)
        => item.UserData?.FirstOrDefault(data => data.UserId.Equals(user.Id));
}
