using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using KoalaPaw.ServerCompanion.Recommendations;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

/// <summary>
/// The RecommendationService seams over plain data: DTO shaping from directly constructed Jellyfin
/// entities, the series-seed rollup, box-set member selection, the person-kind bridge and the
/// skip-null rail loop. The ILibraryManager queries themselves stay untested (no fakes here).
/// </summary>
public class RecommendationServiceTests
{
    private static readonly DateTime Day1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day3 = new(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day4 = new(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc);

    // --- Seed mapping ---

    [Fact]
    public void ToSeed_nullItemYieldsTheEmptySentinelSeed()
    {
        var seed = RecommendationService.ToSeed(null, NewUser(), Day3);

        Assert.Equal(Guid.Empty, seed.Id);
        Assert.Equal(string.Empty, seed.Name);
        Assert.Null(seed.LastPlayedDate); // the sentinel drops the play date too
        Assert.Equal(string.Empty, seed.Type);
        Assert.Null(seed.PrimaryGenre);
    }

    [Fact]
    public void ToSeed_mapsMovieFields()
    {
        var movie = new Movie { Id = Id(1), Name = "Heat", Genres = ["Crime", "Drama"] };

        var seed = RecommendationService.ToSeed(movie, NewUser(), Day2);

        Assert.Equal(Id(1), seed.Id);
        Assert.Equal("Heat", seed.Name);
        Assert.Equal(Day2, seed.LastPlayedDate);
        Assert.Equal("Movie", seed.Type);
        Assert.Equal("Crime", seed.PrimaryGenre); // primary genre only, never the second
    }

    [Fact]
    public void ToSeed_treatsMissingNameAndGenresAsEmpty()
    {
        var movie = new Movie { Id = Id(1), Genres = [] }; // name left unset, as library items can be

        var seed = RecommendationService.ToSeed(movie, NewUser(), Day1);

        Assert.Equal(string.Empty, seed.Name);
        Assert.Null(seed.PrimaryGenre);
    }

    // --- Candidate mapping ---

    [Fact]
    public void ToCandidate_mapsMovieFields()
    {
        var premiere = new DateTime(1995, 12, 15, 0, 0, 0, DateTimeKind.Utc);
        var movie = new Movie
        {
            Id = Id(1),
            Name = "Heat",
            ProductionYear = 1995,
            CommunityRating = 8.3f,
            Genres = ["Crime"],
            PremiereDate = premiere,
        };

        var candidate = RecommendationService.ToCandidate(movie, NewUser());

        Assert.Equal(Id(1), candidate.Id);
        Assert.Equal("Heat", candidate.Name);
        Assert.Equal("Movie", candidate.Type);
        Assert.Null(candidate.SeriesName);
        Assert.Equal(1995, candidate.ProductionYear);
        Assert.False(candidate.HasPrimaryImage); // no images attached
        Assert.False(candidate.Played); // no user data attached
        Assert.Equal(8.3f, candidate.CommunityRating);
        Assert.Equal("Crime", candidate.PrimaryGenre);
        Assert.Equal(premiere, candidate.PremiereDate);
        Assert.Equal(Guid.Empty, candidate.SeriesId); // movies never carry a series
    }

    [Fact]
    public void ToCandidate_mapsEpisodeFields()
    {
        var episode = NewEpisode(2, "Pilot", seriesId: 8, seriesName: "Better Call Saul");

        var candidate = RecommendationService.ToCandidate(episode, NewUser());

        Assert.Equal("Episode", candidate.Type);
        Assert.Equal("Better Call Saul", candidate.SeriesName);
        Assert.Equal(Id(8), candidate.SeriesId);
    }

    [Theory]
    [InlineData(ImageType.Primary, true)]
    [InlineData(ImageType.Backdrop, false)]
    public void ToCandidate_countsOnlyPrimaryImages(ImageType imageType, bool expectedHasPrimaryImage)
    {
        var movie = new Movie
        {
            Id = Id(1),
            Name = "Heat",
            ImageInfos = [new ItemImageInfo { Path = "poster.jpg", Type = imageType }],
        };

        Assert.Equal(expectedHasPrimaryImage, RecommendationService.ToCandidate(movie, NewUser()).HasPrimaryImage);
    }

    [Fact]
    public void ToCandidate_marksPlayedOnlyWhenTheRequesterWatchedIt()
    {
        var requester = NewUser();
        var someoneElse = new User("Other", "authProvider", "resetProvider") { Id = Id(91) };
        var movie = NewMovie(1, "Heat");
        movie.UserData = [UserDataFor(someoneElse, played: true, lastPlayed: Day1)];

        Assert.False(RecommendationService.ToCandidate(movie, requester).Played); // another user's watch is irrelevant

        movie.UserData.Add(UserDataFor(requester, played: true, lastPlayed: Day2));

        Assert.True(RecommendationService.ToCandidate(movie, requester).Played);
    }

    // --- Series seed rollup ---

    [Fact]
    public void RollupSeriesSeeds_emptyInputYieldsNoSeeds()
        => Assert.Empty(RecommendationService.RollupSeriesSeeds([]));

    [Fact]
    public void RollupSeriesSeeds_ignoresPlaysWithoutASeriesId()
    {
        var rows = RecommendationService.RollupSeriesSeeds([(Guid.Empty, Day3), (Id(4), Day1)]);

        var row = Assert.Single(rows);
        Assert.Equal(Id(4), row.SeriesId);
        Assert.Equal(Day1, row.LastPlayedDate);
    }

    [Fact]
    public void RollupSeriesSeeds_datesEachSeriesByItsNewestEpisode()
    {
        var rows = RecommendationService.RollupSeriesSeeds([(Id(4), Day1), (Id(4), Day3), (Id(4), Day2)]);

        var row = Assert.Single(rows);
        Assert.Equal(Id(4), row.SeriesId);
        Assert.Equal(Day3, row.LastPlayedDate);
    }

    [Fact]
    public void RollupSeriesSeeds_dropsSeriesWithoutAnyPlayedEpisode()
    {
        var rows = RecommendationService.RollupSeriesSeeds([(Id(4), null), (Id(5), null)]);

        Assert.Empty(rows);
    }

    [Fact]
    public void RollupSeriesSeeds_keepsSeriesWithAtLeastOnePlayedEpisode()
    {
        var rows = RecommendationService.RollupSeriesSeeds([(Id(4), null), (Id(4), Day2)]);

        var row = Assert.Single(rows);
        Assert.Equal(Day2, row.LastPlayedDate);
    }

    [Fact]
    public void RollupSeriesSeeds_ordersNewestSeriesFirst()
    {
        var rows = RecommendationService.RollupSeriesSeeds([(Id(4), Day1), (Id(5), Day3), (Id(6), Day2)]);

        Assert.Equal([Id(5), Id(6), Id(4)], rows.Select(row => row.SeriesId));
    }

    [Fact]
    public void RollupSeriesSeeds_capsAtThreeSeeds()
    {
        var rows = RecommendationService.RollupSeriesSeeds(
            [(Id(1), Day1), (Id(2), Day2), (Id(3), Day3), (Id(4), Day4)]);

        Assert.Equal(RecommendationEngine.MaxSeeds, rows.Count);
        Assert.Equal([Id(4), Id(3), Id(2)], rows.Select(row => row.SeriesId)); // the newest three survive
    }

    [Fact]
    public void RollupSeriesSeeds_respectsCustomMaxSeeds()
    {
        var rows = RecommendationService.RollupSeriesSeeds([(Id(1), Day1), (Id(2), Day2)], maxSeeds: 1);

        var row = Assert.Single(rows);
        Assert.Equal(Id(2), row.SeriesId);
    }

    // --- Box-set member selection ---

    [Fact]
    public void SelectCollectionCandidates_keepsOnlyPlayableKinds()
    {
        var children = new BaseItem[]
        {
            NewMovie(1, "Heat"),
            new BoxSet(), // a nested box set is not itself a rail item
            NewEpisode(2, "Pilot", seriesId: 8, seriesName: "Better Call Saul"),
            new Folder(), // plain folders never become rail items
            new Series { Id = Id(3), Name = "Better Call Saul" },
        };

        var candidates = RecommendationService.SelectCollectionCandidates(children, NewUser());

        Assert.Equal([Id(1), Id(2), Id(3)], candidates.Select(candidate => candidate.Id)); // box-set order preserved
    }

    [Fact]
    public void SelectCollectionCandidates_mapsKeptItemsForTheEngine()
    {
        var requester = NewUser();
        var episode = NewEpisode(2, "Pilot", seriesId: 8, seriesName: "Better Call Saul");
        episode.UserData = [UserDataFor(requester, played: true, lastPlayed: Day2)];

        var candidates = RecommendationService.SelectCollectionCandidates([NewMovie(1, "Heat"), episode], requester);

        Assert.Equal(2, candidates.Count);
        var mappedEpisode = candidates.Single(candidate => candidate.Id.Equals(Id(2)));
        Assert.Equal("Episode", mappedEpisode.Type);
        Assert.Equal("Better Call Saul", mappedEpisode.SeriesName);
        Assert.Equal(Id(8), mappedEpisode.SeriesId);
        Assert.True(mappedEpisode.Played);
        Assert.False(candidates.Single(candidate => candidate.Id.Equals(Id(1))).Played);
    }

    // --- Person-kind bridge ---

    [Theory]
    [InlineData(PersonKind.Actor, "Actor")]
    [InlineData(PersonKind.Director, "Director")]
    [InlineData(PersonKind.Writer, "Writer")]
    public void ToPersonCredits_mapsPersonKindToItsEngineName(PersonKind kind, string expectedKind)
    {
        var credits = RecommendationService.ToPersonCredits([new PersonInfo { Id = Id(5), Type = kind }]);

        var credit = Assert.Single(credits);
        Assert.Equal(Id(5), credit.Id);
        Assert.Equal(expectedKind, credit.Kind);
    }

    [Fact]
    public void ToPersonCredits_preservesCreditOrder()
    {
        var people = new[]
        {
            new PersonInfo { Id = Id(1), Type = PersonKind.Director },
            new PersonInfo { Id = Id(2), Type = PersonKind.Actor },
            new PersonInfo { Id = Id(3), Type = PersonKind.Writer },
        };

        Assert.Equal([Id(1), Id(2), Id(3)], RecommendationService.ToPersonCredits(people).Select(credit => credit.Id));
    }

    [Fact]
    public void ToPersonCredits_feedsTheEnginesFeaturedPersonPick()
    {
        // The director credited first still loses to the first actor: the kind-to-name bridge
        // must produce exactly the names PickFeaturedPerson matches on.
        var people = new[]
        {
            new PersonInfo { Id = Id(1), Type = PersonKind.Director },
            new PersonInfo { Id = Id(2), Type = PersonKind.Actor },
        };

        Assert.Equal(Id(2), RecommendationEngine.PickFeaturedPerson(RecommendationService.ToPersonCredits(people)));
    }

    // --- Rail loop ---

    [Fact]
    public void SelectRails_emptySeedListYieldsNoRails()
    {
        var rails = RecommendationService.SelectRails(
            [],
            _ => throw new InvalidOperationException("no seed should reach the rail builder"));

        Assert.Empty(rails);
    }

    [Fact]
    public void SelectRails_skipsSeedsThatYieldNoRail()
    {
        var seeds = new[]
        {
            Seed(1, "Heat", Day2),
            Seed(2, "Vanished", Day3), // deleted from the library: no rail
            Seed(3, "A New Hope", Day1),
        };

        var rails = RecommendationService.SelectRails(seeds, seed =>
            seed.Name == "Vanished"
                ? null
                : new RecommendationRailDto { SeedItemId = seed.Id.ToString("N"), SeedTitle = seed.Name });

        Assert.Equal(["Heat", "A New Hope"], rails.Select(rail => rail.SeedTitle)); // no gap where the seed vanished
        Assert.Equal([Id(1).ToString("N"), Id(3).ToString("N")], rails.Select(rail => rail.SeedItemId));
    }

    [Fact]
    public void SelectRails_preservesSeedOrder()
    {
        var seeds = new[] { Seed(2, "Older", Day2), Seed(1, "Newest", Day3), Seed(3, "Oldest", Day1) };

        var rails = RecommendationService.SelectRails(seeds, seed => new RecommendationRailDto { SeedTitle = seed.Name });

        Assert.Equal(["Older", "Newest", "Oldest"], rails.Select(rail => rail.SeedTitle));
    }

    // --- Helpers ---

    private static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static User NewUser() => new("Tester", "authProvider", "resetProvider") { Id = Id(90) };

    private static RecommendationSeed Seed(int id, string name, DateTime? lastPlayed)
        => new(Id(id), name, lastPlayed, "Movie", null);

    private static Movie NewMovie(int id, string name) => new() { Id = Id(id), Name = name };

    private static Episode NewEpisode(int id, string name, int seriesId, string seriesName)
        => new() { Id = Id(id), Name = name, SeriesId = Id(seriesId), SeriesName = seriesName };

    private static UserData UserDataFor(User user, bool played, DateTime? lastPlayed)
        => new()
        {
            CustomDataKey = "koalapaw-test",
            ItemId = Guid.NewGuid(),
            Item = null,
            UserId = user.Id,
            User = user,
            Played = played,
            LastPlayedDate = lastPlayed,
        };
}
