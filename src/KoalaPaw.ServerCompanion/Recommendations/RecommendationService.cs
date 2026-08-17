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

        var rails = new List<RecommendationRailDto>(seeds.Count);
        foreach (var seed in seeds)
        {
            var rail = BuildRail(user, seed);
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

        return playedEpisodes
            .GroupBy(episode => episode.SeriesId)
            .Where(group => !group.Key.Equals(Guid.Empty))
            .Select(group => new
            {
                SeriesId = group.Key,
                LastPlayedDate = group.Max(episode => GetUserData(episode, user)?.LastPlayedDate),
            })
            .Where(result => result.LastPlayedDate.HasValue)
            .OrderByDescending(result => result.LastPlayedDate)
            .Take(RecommendationEngine.MaxSeeds)
            .Select(result => ToSeed(_libraryManager.GetItemById(result.SeriesId), user, result.LastPlayedDate))
            .Where(seed => !seed.Id.Equals(Guid.Empty))
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

        return boxSet.GetLinkedChildren(user)
            .Where(item => item is Movie or Series or Episode)
            .Select(item => ToCandidate(item, user))
            .ToList();
    }

    /// <summary>The featured person's other movies and episodes, or nothing when the seed has no credited actors or directors.</summary>
    private IReadOnlyList<RecommendationCandidate> GetPersonCandidates(User user, BaseItem seedItem)
    {
        var personId = RecommendationEngine.PickFeaturedPerson(
            _libraryManager.GetPeople(seedItem)
                .Select(person => new PersonCredit(person.Id, person.Type.ToString()))
                .ToList());
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

    private static RecommendationSeed ToSeed(BaseItem? item, User user, DateTime? lastPlayedDate)
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

    private static RecommendationCandidate ToCandidate(BaseItem item, User user)
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
