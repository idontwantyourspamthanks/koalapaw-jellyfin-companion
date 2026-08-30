using System;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using KoalaPaw.ServerCompanion.Api;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

/// <summary>
/// Who may read whose recommendation rails: the 401/403/404 decision table behind
/// RecommendationsController, over caller identity, target existence and the self-vs-admin rule.
/// </summary>
public class RecommendationsControllerTests
{
    private static readonly Guid AliceId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid BobId = new("00000000-0000-0000-0000-000000000002");

    [Theory]
    // No usable caller identity → 401, whatever was asked for.
    [InlineData(false, false, false, false, RecommendationsController.RecommendationAccess.Unauthorized)]
    [InlineData(false, false, true,  false, RecommendationsController.RecommendationAccess.Unauthorized)]
    [InlineData(false, true,  false, false, RecommendationsController.RecommendationAccess.Unauthorized)]
    [InlineData(false, true,  true,  true,  RecommendationsController.RecommendationAccess.Unauthorized)]
    // Unknown target → 404, even when a non-admin asked for someone else's rails (404 before 403).
    [InlineData(true, false, false, false, RecommendationsController.RecommendationAccess.NotFound)]
    [InlineData(true, true,  false, false, RecommendationsController.RecommendationAccess.NotFound)]
    // Non-admin asking for another user's rails → 403.
    [InlineData(true, false, true, false, RecommendationsController.RecommendationAccess.Forbidden)]
    // Own rails → allowed; admins may read anyone's.
    [InlineData(true, false, true, true,  RecommendationsController.RecommendationAccess.Allowed)]
    [InlineData(true, true,  true, false, RecommendationsController.RecommendationAccess.Allowed)]
    [InlineData(true, true,  true, true,  RecommendationsController.RecommendationAccess.Allowed)]
    public void AccessFor_coversFullTruthTable(
        bool hasCaller,
        bool callerIsAdmin,
        bool targetExists,
        bool callerAsksForOwnRails,
        RecommendationsController.RecommendationAccess expected)
    {
        var caller = hasCaller ? MakeUser("Alice", AliceId, admin: callerIsAdmin) : null;
        var target = !targetExists ? null
            : callerAsksForOwnRails ? caller
            : MakeUser("Bob", BobId, admin: false);

        Assert.Equal(expected, RecommendationsController.AccessFor(caller, target));
    }

    private static User MakeUser(string name, Guid id, bool admin)
    {
        var user = new User(name, "authProvider", "resetProvider") { Id = id };
        if (admin)
        {
            user.SetPermission(PermissionKind.IsAdministrator, true);
        }

        return user;
    }
}
