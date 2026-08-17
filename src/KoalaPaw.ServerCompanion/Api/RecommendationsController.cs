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
        if (caller is null)
        {
            return Unauthorized();
        }

        var target = _userManager.GetUserById(userId);
        if (target is null)
        {
            return NotFound();
        }

        // Non-admins may only request their own rails.
        if (!caller.HasPermission(PermissionKind.IsAdministrator) && !caller.Id.Equals(userId))
        {
            return Forbid();
        }

        return Ok(new RecommendationsResponse { Rails = _service.GetRecommendations(target) });
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
