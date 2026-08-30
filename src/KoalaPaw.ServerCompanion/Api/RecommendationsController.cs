using System;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using KoalaPaw.ServerCompanion.Recommendations;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace KoalaPaw.ServerCompanion.Api;

/// <summary>
/// "Because you watched X" rails for the KoalaPaw grown-up home. Any authenticated user may read
/// their own recommendations; admins may read any user's.
/// </summary>
[ApiController]
[Authorize] // any authenticated user — deliberately NOT RequiresElevation
[Route("KoalaPaw")]
public class RecommendationsController : ControllerBase
{
    /// <summary>
    /// The user-id claim Jellyfin's authentication handler stamps on every user token
    /// (Jellyfin.Api.Constants.InternalClaimTypes.UserId — not referenceable from a plugin).
    /// </summary>
    private const string UserIdClaimType = "Jellyfin-UserId";

    private readonly RecommendationService _service;
    private readonly IUserManager _userManager;

    public RecommendationsController(RecommendationService service, IUserManager userManager)
    {
        _service = service;
        _userManager = userManager;
    }

    /// <summary>Recommendation rails for one user, newest seed first. Rails is empty when the user has no watch history.</summary>
    [HttpGet("Recommendations/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<RecommendationsResponse> GetRecommendations(Guid userId)
    {
        var caller = GetCaller();
        var target = _userManager.GetUserById(userId);

        return AccessFor(caller, target) switch
        {
            RecommendationAccess.Unauthorized => Unauthorized(),
            RecommendationAccess.NotFound => NotFound(),
            RecommendationAccess.Forbidden => Forbid(),
            _ => Ok(new RecommendationsResponse { Rails = _service.GetRecommendations(target!) }),
        };
    }

    /// <summary>Status-code decision for reading one user's recommendation rails.</summary>
    public enum RecommendationAccess
    {
        /// <summary>No usable caller identity on the request — HTTP 401.</summary>
        Unauthorized,

        /// <summary>The requested user id does not exist — HTTP 404.</summary>
        NotFound,

        /// <summary>Authenticated non-admin asking for another user's rails — HTTP 403.</summary>
        Forbidden,

        /// <summary>Own rails, or an admin reading any user's — HTTP 200.</summary>
        Allowed,
    }

    /// <summary>
    /// The self-vs-admin access decision behind GetRecommendations, extracted as a pure static so
    /// the 401/403/404 truth table (including 404-before-403 precedence for unknown targets) is
    /// unit-testable without Jellyfin plumbing (the claims principal and IAuthorizationContext
    /// have no test fakes in this repo). Precedence: caller identity first, then target
    /// existence, then the self-vs-admin rule.
    /// </summary>
    internal static RecommendationAccess AccessFor(User? caller, User? target)
    {
        if (caller is null)
        {
            return RecommendationAccess.Unauthorized;
        }

        if (target is null)
        {
            return RecommendationAccess.NotFound;
        }

        // Non-admins may only request their own rails.
        if (!caller.HasPermission(PermissionKind.IsAdministrator) && !caller.Id.Equals(target.Id))
        {
            return RecommendationAccess.Forbidden;
        }

        return RecommendationAccess.Allowed;
    }

    private User? GetCaller()
    {
        var userIdClaim = User.FindFirst(UserIdClaimType)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
            return null;
        }

        return _userManager.GetUserById(userId);
    }
}
