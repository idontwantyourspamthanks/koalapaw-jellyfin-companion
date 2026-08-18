using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using KoalaPaw.ServerCompanion;
using KoalaPaw.ServerCompanion.Api;
using KoalaPaw.ServerCompanion.Configuration;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

/// <summary>
/// The password-free TV directory: which configured users get listed (with kid flag and avatar),
/// and the anonymous Info ping shape.
/// </summary>
public class DirectoryControllerTests
{
    private static readonly Guid AliceId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid BobId = new("00000000-0000-0000-0000-000000000002");
    private static readonly Guid CarolId = new("00000000-0000-0000-0000-000000000003");
    private static readonly Guid StaleId = new("00000000-0000-0000-0000-000000000099");

    private const string DefaultAvatar = "🐨";

    // --- Listed user selection ---

    [Fact]
    public void SelectListedUsers_returnsOnlyConfiguredPasswordFreeUsers()
    {
        var users = Users(Alice(Password: null), Bob(Password: "hashed"));

        var result = DirectoryController.SelectListedUsers([Entry(AliceId), Entry(BobId)], users.GetValueOrDefault);

        var dto = Assert.Single(result);
        Assert.Equal("Alice", dto.Name);
        Assert.Equal(AliceId.ToString("D"), dto.Id);
    }

    [Fact]
    public void SelectListedUsers_carriesKidFlagAndAvatarThrough()
    {
        var users = Users(Alice(Password: null), Bob(Password: null));

        var result = DirectoryController.SelectListedUsers(
            [Entry(AliceId, kid: true, avatar: "🐯"), Entry(BobId, kid: false, avatar: "🐙")],
            users.GetValueOrDefault);

        Assert.Collection(
            result,
            dto => { Assert.True(dto.Kid); Assert.Equal("🐯", dto.Avatar); },
            dto => { Assert.False(dto.Kid); Assert.Equal("🐙", dto.Avatar); });
    }

    [Fact]
    public void SelectListedUsers_fallsBackToDefaultAvatarForUnrecognisedStoredAvatar()
    {
        var users = Users(Alice(Password: null));

        var result = DirectoryController.SelectListedUsers([Entry(AliceId, avatar: "🦄")], users.GetValueOrDefault);

        var dto = Assert.Single(result);
        Assert.Equal(DefaultAvatar, dto.Avatar);
    }

    [Fact]
    public void SelectListedUsers_skipsUnknownConfiguredIds()
    {
        var users = Users(Alice(Password: null));

        var result = DirectoryController.SelectListedUsers([Entry(AliceId), Entry(StaleId)], users.GetValueOrDefault);

        var dto = Assert.Single(result);
        Assert.Equal("Alice", dto.Name);
    }

    [Fact]
    public void SelectListedUsers_treatsEmptyPasswordAsPasswordFree()
    {
        // Mirrors Jellyfin's own HasPassword check: empty and null passwords are the
        // password-free cases where an empty-password sign-in succeeds.
        var users = Users(Alice(Password: ""));

        var result = DirectoryController.SelectListedUsers([Entry(AliceId)], users.GetValueOrDefault);

        var dto = Assert.Single(result);
        Assert.Equal("Alice", dto.Name);
    }

    [Fact]
    public void SelectListedUsers_skipsAllUsersWhenEveryConfiguredUserHasAPassword()
    {
        var users = Users(Alice(Password: "hashed"), Bob(Password: "also-hashed"));

        var result = DirectoryController.SelectListedUsers([Entry(AliceId), Entry(BobId)], users.GetValueOrDefault);

        Assert.Empty(result);
    }

    [Fact]
    public void SelectListedUsers_deduplicatesConfiguredIds()
    {
        var users = Users(Alice(Password: null));

        var result = DirectoryController.SelectListedUsers([Entry(AliceId), Entry(AliceId)], users.GetValueOrDefault);

        Assert.Single(result);
    }

    [Fact]
    public void SelectListedUsers_preservesConfiguredOrder()
    {
        var users = Users(Alice(Password: null), Bob(Password: null), Carol(Password: null));

        var result = DirectoryController.SelectListedUsers(
            [Entry(CarolId), Entry(AliceId), Entry(BobId)],
            users.GetValueOrDefault);

        Assert.Equal(["Carol", "Alice", "Bob"], result.Select(u => u.Name));
    }

    [Fact]
    public void SelectListedUsers_returnsEmptyWhenNothingConfigured()
    {
        var users = Users(Alice(Password: null));

        var result = DirectoryController.SelectListedUsers([], users.GetValueOrDefault);

        Assert.Empty(result);
    }

    // --- Info ping ---

    [Fact]
    public void GetInfo_returnsPluginNameAndAssemblyVersion()
    {
        // No Plugin instance is constructed in the test host, so the fallback path —
        // the plugin assembly's own version — is what runs, exactly as in production.
        var controller = new DirectoryController(userManager: null!);

        var result = controller.GetInfo().Result as OkObjectResult;

        var info = Assert.IsType<DirectoryController.InfoResponse>(result!.Value);
        Assert.Equal("KoalaPaw Server Companion", info.Name);
        Assert.Equal(typeof(Plugin).Assembly.GetName().Version!.ToString(), info.Version);
    }

    [Fact]
    public void GetListedUsers_returnsEmptyUsersWhenNoPluginInstance()
    {
        var controller = new DirectoryController(userManager: null!);

        var result = controller.GetListedUsers().Result as OkObjectResult;

        var response = Assert.IsType<DirectoryController.ListedUsersResponse>(result!.Value);
        Assert.Empty(response.Users);
    }

    private static PluginConfiguration.ListedUserEntry Entry(Guid userId, bool kid = false, string avatar = DefaultAvatar) =>
        new() { UserId = userId, Kid = kid, Avatar = avatar };

    private static Dictionary<Guid, User> Users(params User[] users) =>
        users.ToDictionary(user => user.Id);

    private static User Alice(string? Password) => new("Alice", "authProvider", "resetProvider") { Password = Password, Id = AliceId };

    private static User Bob(string? Password) => new("Bob", "authProvider", "resetProvider") { Password = Password, Id = BobId };

    private static User Carol(string? Password) => new("Carol", "authProvider", "resetProvider") { Password = Password, Id = CarolId };
}
