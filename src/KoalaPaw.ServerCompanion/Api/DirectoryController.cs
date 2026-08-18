using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Database.Implementations.Entities;
using KoalaPaw.ServerCompanion.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace KoalaPaw.ServerCompanion.Api;

/// <summary>
/// First-run directory for the KoalaPaw Android TV app. Both routes are deliberately anonymous:
/// the TV has no Jellyfin credentials yet, and these calls happen before any login (app-store
/// reviewers are given no account at all). The exposure is safe because an admin must opt in per
/// user on the dashboard config page, only name + id + a kid flag + an avatar emoji are returned,
/// and only users with no configured password can ever be listed — those are exactly the users an
/// empty-password sign-in from the TV can authenticate as.
/// </summary>
[ApiController]
[Route("KoalaPaw")]
public class DirectoryController : ControllerBase
{
    /// <summary>
    /// The avatar glyphs the TV can display. Mirrors AvatarPresets in koalapaw-androidtv
    /// (app/src/main/kotlin/com/koalapaw/tv/ui/IdentityTile.kt) — Unicode 6.0 emoji only, so they
    /// render on every TV box; anything else the TV would reject.
    /// </summary>
    private static readonly string[] AvatarPresets =
        ["🐨", "🐼", "🐯", "🐸", "🐙", "🐬", "🐢", "🐝", "🐊", "🐵", "🐰", "🐻"];

    private const string DefaultAvatar = "🐨";

    private readonly IUserManager _userManager;

    public DirectoryController(IUserManager userManager)
    {
        _userManager = userManager;
    }

    /// <summary>Existence ping for the TV: plugin name and version, no authentication required.</summary>
    [HttpGet("Info")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<InfoResponse> GetInfo()
    {
        return Ok(new InfoResponse
        {
            Name = "KoalaPaw Server Companion",
            Version = (Plugin.Instance?.Version ?? typeof(Plugin).Assembly.GetName().Version)?.ToString() ?? "0.0.0",
        });
    }

    /// <summary>
    /// Users an admin opted in to the one-tap TV sign-in picker: configured entry → live server user →
    /// no configured password. Unknown or stale configured ids are silently skipped, and an avatar
    /// the TV wouldn't recognise falls back to the default. No authentication required.
    /// </summary>
    [HttpGet("ListedUsers")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ListedUsersResponse> GetListedUsers()
    {
        var configuredEntries = Plugin.Instance?.Configuration.ListedUsers ?? [];
        var users = SelectListedUsers(configuredEntries, id => _userManager.GetUserById(id));
        return Ok(new ListedUsersResponse { Users = users });
    }

    /// <summary>
    /// Maps configured entries to password-free live users, in configured order, deduplicated by
    /// user id. Mirrors Jellyfin's own password check (<see cref="MediaBrowser.Controller.Authentication.IAuthenticationProvider.HasPassword"/>):
    /// a null/empty <see cref="User.Password"/> is exactly the case where an empty-password sign-in succeeds.
    /// </summary>
    internal static IReadOnlyList<ListedUserDto> SelectListedUsers(
        IEnumerable<PluginConfiguration.ListedUserEntry> configuredEntries,
        Func<Guid, User?> getUserById)
    {
        return configuredEntries
            .DistinctBy(entry => entry.UserId)
            .Select(entry => (Entry: entry, User: getUserById(entry.UserId)))
            .Where(pair => pair.User is not null && string.IsNullOrEmpty(pair.User.Password))
            .Select(pair => new ListedUserDto
            {
                Id = pair.User!.Id.ToString("D"),
                Name = pair.User.Username,
                Kid = pair.Entry.Kid,
                Avatar = AvatarPresets.Contains(pair.Entry.Avatar) ? pair.Entry.Avatar : DefaultAvatar,
            })
            .ToList();
    }

    public sealed class InfoResponse
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;
    }

    public sealed class ListedUsersResponse
    {
        [JsonPropertyName("users")]
        public IReadOnlyList<ListedUserDto> Users { get; set; } = [];
    }

    public sealed class ListedUserDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("kid")]
        public bool Kid { get; set; }

        [JsonPropertyName("avatar")]
        public string Avatar { get; set; } = DefaultAvatar;
    }
}
