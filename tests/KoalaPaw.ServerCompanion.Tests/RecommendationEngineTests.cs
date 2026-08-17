using System;
using System.Collections.Generic;
using System.Linq;
using KoalaPaw.ServerCompanion.Recommendations;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

/// <summary>
/// The pure rail-selection rules: seed picking, strategy precedence, unplayed-first ordering,
/// seed exclusion and the 3–12 item window.
/// </summary>
public class RecommendationEngineTests
{
    private static readonly DateTime Day1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day3 = new(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);

    // --- Seed picking ---

    [Fact]
    public void PickSeeds_mergesMoviesAndSeriesMostRecentFirst()
    {
        var movies = new[] { Seed(Guid.NewGuid(), "A Movie", Day1), Seed(Guid.NewGuid(), "B Movie", Day2) };
        var series = new[] { Seed(Guid.NewGuid(), "C Series", Day3) };

        var seeds = RecommendationEngine.PickSeeds(movies, series);

        Assert.Equal(["C Series", "B Movie", "A Movie"], seeds.Select(s => s.Name));
    }

    [Fact]
    public void PickSeeds_capsAtThreeSeeds()
    {
        var movies = new[] { Seed(Id(1), "M1", Day1), Seed(Id(2), "M2", Day2), Seed(Id(3), "M3", Day3) };
        var series = new[] { Seed(Id(4), "S1", Day1), Seed(Id(5), "S2", Day2), Seed(Id(6), "S3", Day3) };

        var seeds = RecommendationEngine.PickSeeds(movies, series);

        Assert.Equal(3, seeds.Count);
        Assert.Equal(3, seeds.DistinctBy(s => s.Id).Count());
    }

    [Fact]
    public void PickSeeds_deduplicatesByIdKeepingNewestDate()
    {
        var movies = new[] { Seed(Id(1), "Dupe", Day1) };
        var series = new[] { Seed(Id(1), "Dupe", Day3) };

        var seeds = RecommendationEngine.PickSeeds(movies, series);

        var seed = Assert.Single(seeds);
        Assert.Equal(Id(1), seed.Id);
        Assert.Equal(Day3, seed.LastPlayedDate);
    }

    [Fact]
    public void PickSeeds_ignoresSeedsWithEmptyId()
    {
        var movies = new[] { Seed(Guid.Empty, "Ghost", Day3), Seed(Id(1), "Real", Day1) };

        var seeds = RecommendationEngine.PickSeeds(movies, []);

        var seed = Assert.Single(seeds);
        Assert.Equal(Id(1), seed.Id);
    }

    [Fact]
    public void PickSeeds_respectsCustomMaxSeeds()
    {
        var movies = new[] { Seed(Id(1), "M1", Day3), Seed(Id(2), "M2", Day2), Seed(Id(3), "M3", Day1) };

        var seeds = RecommendationEngine.PickSeeds(movies, [], maxSeeds: 1);

        var seed = Assert.Single(seeds);
        Assert.Equal(Id(1), seed.Id);
    }

    // --- Featured person ---

    [Fact]
    public void PickFeaturedPerson_prefersTopBilledActor()
    {
        var director = new PersonCredit(Id(1), "Director");
        var lead = new PersonCredit(Id(2), "Actor");
        var supporting = new PersonCredit(Id(3), "Actor");

        Assert.Equal(Id(2), RecommendationEngine.PickFeaturedPerson([director, lead, supporting]));
    }

    [Fact]
    public void PickFeaturedPerson_fallsBackToFirstDirectorWhenNoActor()
    {
        var writer = new PersonCredit(Id(1), "Writer");
        var director = new PersonCredit(Id(2), "Director");

        Assert.Equal(Id(2), RecommendationEngine.PickFeaturedPerson([writer, director]));
    }

    [Fact]
    public void PickFeaturedPerson_returnsNullWithoutActorOrDirector()
    {
        Assert.Null(RecommendationEngine.PickFeaturedPerson([new PersonCredit(Id(1), "Writer")]));
        Assert.Null(RecommendationEngine.PickFeaturedPerson([]));
    }

    // --- Strategy precedence ---

    [Fact]
    public void BuildRail_collectionWinsOverPersonAndGenre()
    {
        var seed = Seed(Id(9), "Seed", Day3);
        var collection = Many(1, 3, "Collection");
        var person = Many(10, 4, "Person");
        var genre = Many(20, 4, "Genre");

        var rail = RecommendationEngine.BuildRail(seed, collection, person, genre);

        Assert.NotNull(rail);
        Assert.Equal("collection", rail.Reason);
        Assert.StartsWith("Collection", rail.Items[0].Name);
    }

    [Fact]
    public void BuildRail_personWinsWhenCollectionHasTooFewItems()
    {
        var seed = Seed(Id(9), "Seed", Day3);
        var collection = Many(1, 2, "Collection");
        var person = Many(10, 4, "Person");

        var rail = RecommendationEngine.BuildRail(seed, collection, person, []);

        Assert.NotNull(rail);
        Assert.Equal("person", rail.Reason);
    }

    [Fact]
    public void BuildRail_genreWinsWhenCollectionAndPersonHaveTooFewItems()
    {
        var seed = Seed(Id(9), "Seed", Day3, "Movie", "Horror");
        var collection = Many(1, 2, "Collection");
        var person = Many(10, 1, "Person");
        var genre = Many(20, 5, "Genre", genre: "Horror");

        var rail = RecommendationEngine.BuildRail(seed, collection, person, genre);

        Assert.NotNull(rail);
        Assert.Equal("genre", rail.Reason);
    }

    [Fact]
    public void BuildRail_returnsNullWhenNoStrategyHasMinItems()
    {
        var seed = Seed(Id(9), "Seed", Day3, "Movie", "Horror");
        var collection = Many(1, 2, "Collection");
        var person = Many(10, 2, "Person");
        var genre = Many(20, 2, "Genre", genre: "Horror");

        Assert.Null(RecommendationEngine.BuildRail(seed, collection, person, genre));
    }

    [Fact]
    public void BuildRail_respectsCustomMinItems()
    {
        var seed = Seed(Id(9), "Seed", Day3);
        var collection = Many(1, 3, "Collection");

        Assert.Null(RecommendationEngine.BuildRail(seed, collection, [], [], minItems: 4));
    }

    // --- Ordering ---

    [Fact]
    public void BuildRail_ordersUnplayedFirst()
    {
        var seed = Seed(Id(9), "Seed", Day3);
        var collection = new[]
        {
            Candidate(Id(1), "Watched", played: true),
            Candidate(Id(2), "Fresh"),
            Candidate(Id(3), "AlsoFresh"),
        };

        var rail = RecommendationEngine.BuildRail(seed, collection, [], [])!;

        Assert.Equal([false, false, true], rail.Items.Select(i => i.Played));
        Assert.Equal("AlsoFresh", rail.Items[0].Name);
    }

    [Fact]
    public void BuildRail_collectionOrdersByPremiereDateAfterUnplayed()
    {
        var seed = Seed(Id(9), "Seed", Day3);
        var collection = new[]
        {
            Candidate(Id(1), "Newer", premiere: Day3),
            Candidate(Id(2), "Older", premiere: Day1),
            Candidate(Id(3), "NoDate", premiere: null),
        };

        var rail = RecommendationEngine.BuildRail(seed, collection, [], [])!;

        // All unplayed: chronological premiere order, undated last.
        Assert.Equal(["Older", "Newer", "NoDate"], rail.Items.Select(i => i.Name));
    }

    [Fact]
    public void BuildRail_personOrdersByCommunityRatingAfterUnplayed()
    {
        var seed = Seed(Id(9), "Seed", Day3);
        var person = new[]
        {
            Candidate(Id(1), "Meh", rating: 5f),
            Candidate(Id(2), "Great", rating: 9f),
            Candidate(Id(3), "Unrated", rating: null),
        };

        var rail = RecommendationEngine.BuildRail(seed, [], person, [])!;

        Assert.Equal(["Great", "Meh", "Unrated"], rail.Items.Select(i => i.Name));
    }

    // --- Exclusion ---

    [Fact]
    public void BuildRail_excludesTheSeedItself()
    {
        var seedId = Id(9);
        var seed = Seed(seedId, "Seed", Day3);
        var collection = new[]
        {
            Candidate(seedId, "Seed"), // must never appear
            Candidate(Id(1), "Member A"),
            Candidate(Id(2), "Member B"),
            Candidate(Id(3), "Member C"),
        };

        var rail = RecommendationEngine.BuildRail(seed, collection, [], [])!;

        Assert.DoesNotContain(rail.Items, i => i.Id == seedId.ToString("N"));
        Assert.Equal(3, rail.Items.Count);
    }

    [Fact]
    public void BuildRail_excludesEpisodesOfTheSeedSeries()
    {
        var seriesId = Id(7);
        var seed = Seed(seriesId, "Seed Series", Day3, "Series", "Drama");
        var genre = new[]
        {
            Candidate(Id(1), "Ep of seed", type: "Episode", seriesId: seriesId, genre: "Drama"),
            Candidate(Id(2), "Ep of seed too", type: "Episode", seriesId: seriesId, genre: "Drama"),
            Candidate(Id(3), "Other movie", type: "Movie", genre: "Drama"),
            Candidate(Id(4), "Other series", type: "Series", genre: "Drama"),
            Candidate(Id(5), "Another movie", type: "Movie", genre: "Drama"),
        };

        var rail = RecommendationEngine.BuildRail(seed, [], [], genre)!;

        Assert.Equal(["Another movie", "Other movie", "Other series"], rail.Items.Select(i => i.Name));
    }

    // --- Cap and shape ---

    [Fact]
    public void BuildRail_capsAtTwelveItems()
    {
        var seed = Seed(Id(9), "Seed", Day3);
        var collection = Many(1, 20, "Member");

        var rail = RecommendationEngine.BuildRail(seed, collection, [], [])!;

        Assert.Equal(RecommendationEngine.MaxRailItems, rail.Items.Count);
    }

    [Fact]
    public void BuildRail_genreOnlyMatchesSamePrimaryGenre()
    {
        var seed = Seed(Id(9), "Seed", Day3, "Movie", "Horror");
        var genre = new[]
        {
            Candidate(Id(1), "Horror film", genre: "Horror"),
            Candidate(Id(2), "Sci-Fi film", genre: "Sci-Fi"), // secondary genre match must not qualify
            Candidate(Id(3), "Horror sequel", genre: "Horror"),
            Candidate(Id(4), "Thriller", genre: "Thriller"),
            Candidate(Id(5), "Horror anthology", genre: "Horror"),
        };

        var rail = RecommendationEngine.BuildRail(seed, [], [], genre);

        Assert.NotNull(rail);
        Assert.Equal(["Horror anthology", "Horror film", "Horror sequel"], rail.Items.Select(i => i.Name));
    }

    [Fact]
    public void BuildRail_skipsGenreEntirelyWhenSeedHasNoPrimaryGenre()
    {
        var seed = Seed(Id(9), "Seed", Day3, "Movie", genre: null);
        var genre = Many(20, 5, "Genre", genre: "Horror");

        Assert.Null(RecommendationEngine.BuildRail(seed, [], [], genre));
    }

    [Fact]
    public void BuildRail_shapesWireDto()
    {
        var seedId = Guid.Parse("c8c3b0f0-9f2a-4f3c-9c6e-8c0d1e2f3a4b");
        var seed = Seed(seedId, "Star Wars", Day3, "Movie", "Sci-Fi");
        var collection = new[]
        {
            Candidate(Id(1), "The Empire Strikes Back", type: "Movie", year: 1980, premiere: new DateTime(1980, 5, 21, 0, 0, 0, DateTimeKind.Utc)),
            Candidate(Id(2), "Return of the Jedi", type: "Movie", year: 1983, premiere: new DateTime(1983, 5, 25, 0, 0, 0, DateTimeKind.Utc)),
            Candidate(Id(3), "Solo", type: "Movie", year: 2018, premiere: new DateTime(2018, 5, 25, 0, 0, 0, DateTimeKind.Utc)),
        };

        var rail = RecommendationEngine.BuildRail(seed, collection, [], [])!;

        Assert.Equal(seedId.ToString("N"), rail.SeedItemId);
        Assert.Equal("Star Wars", rail.SeedTitle);
        Assert.Equal("collection", rail.Reason);

        var first = rail.Items[0];
        Assert.Equal(Id(1).ToString("N"), first.Id);
        Assert.Equal("The Empire Strikes Back", first.Name);
        Assert.Equal("Movie", first.Type);
        Assert.Null(first.SeriesName); // episodes carry seriesName, movies don't
        Assert.Equal(1980, first.ProductionYear);
        Assert.True(first.HasPrimaryImage);
        Assert.False(first.Played);
    }

    [Fact]
    public void BuildRail_episodesCarrySeriesName()
    {
        var seed = Seed(Id(9), "Seed", Day3);
        var person = new[]
        {
            Candidate(Id(1), "Pilot", type: "Episode", seriesName: "Better Call Saul", year: 2015),
            Candidate(Id(2), "Heat", type: "Movie", year: 1995),
            Candidate(Id(3), "S1E2", type: "Episode", seriesName: "Better Call Saul", year: 2015),
        };

        var rail = RecommendationEngine.BuildRail(seed, [], person, [])!;

        Assert.Equal("Better Call Saul", rail.Items.Single(i => i.Name == "Pilot").SeriesName);
        Assert.Null(rail.Items.Single(i => i.Name == "Heat").SeriesName);
    }

    [Fact]
    public void RecommendationsResponse_serializesToTheWireContract()
    {
        var seedId = Guid.Parse("5c0d3e1f-9b2a-4d5c-8e6f-1a2b3c4d5e6f");
        var seed = Seed(seedId, "Star Wars", Day3, "Movie", "Sci-Fi");
        var rail = RecommendationEngine.BuildRail(
            seed,
            [
                Candidate(Id(1), "The Empire Strikes Back", type: "Movie", year: 1980, premiere: new DateTime(1980, 5, 21, 0, 0, 0, DateTimeKind.Utc)),
                Candidate(Id(2), "Return of the Jedi", type: "Movie", year: 1983, premiere: new DateTime(1983, 5, 25, 0, 0, 0, DateTimeKind.Utc)),
                Candidate(Id(3), "A New Hope", type: "Movie", year: 1977, premiere: new DateTime(1977, 5, 25, 0, 0, 0, DateTimeKind.Utc)),
            ],
            [],
            [])!;
        var response = new RecommendationsResponse { Rails = [rail] };

        var json = System.Text.Json.JsonSerializer.Serialize(response, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var railNode = document.RootElement.GetProperty("rails")[0];

        Assert.Equal(["seedItemId", "seedTitle", "reason", "items"], railNode.EnumerateObject().Select(p => p.Name));
        Assert.Equal(seedId.ToString("N"), railNode.GetProperty("seedItemId").GetString());
        Assert.Equal("Star Wars", railNode.GetProperty("seedTitle").GetString());
        Assert.Equal("collection", railNode.GetProperty("reason").GetString());

        // Collection rail orders chronologically, so A New Hope (1977) leads.
        var item = railNode.GetProperty("items")[0];
        Assert.Equal(["id", "name", "type", "seriesName", "productionYear", "hasPrimaryImage", "played"], item.EnumerateObject().Select(p => p.Name));
        Assert.Equal(Id(3).ToString("N"), item.GetProperty("id").GetString());
        Assert.Equal("A New Hope", item.GetProperty("name").GetString());
        Assert.Equal("Movie", item.GetProperty("type").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, item.GetProperty("seriesName").ValueKind);
        Assert.Equal(1977, item.GetProperty("productionYear").GetInt32());
        Assert.True(item.GetProperty("hasPrimaryImage").GetBoolean());
        Assert.False(item.GetProperty("played").GetBoolean());
    }

    // --- Helpers ---

    private static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static RecommendationSeed Seed(Guid id, string name, DateTime? lastPlayed, string type = "Movie", string? genre = null)
        => new(id, name, lastPlayed, type, genre);

    private static RecommendationCandidate Candidate(
        Guid id,
        string name,
        bool played = false,
        string type = "Movie",
        string? genre = null,
        float? rating = null,
        DateTime? premiere = null,
        string? seriesName = null,
        Guid seriesId = default,
        int? year = null)
        => new(id, name, type, seriesName, year, HasPrimaryImage: true, played, rating, genre, premiere, seriesId);

    private static IReadOnlyList<RecommendationCandidate> Many(int firstId, int count, string namePrefix, string? genre = null)
        => Enumerable.Range(firstId, count)
            .Select(n => Candidate(Id(n), $"{namePrefix} {n}", genre: genre))
            .ToList();
}
