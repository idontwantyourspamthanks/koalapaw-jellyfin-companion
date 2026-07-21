using KoalaPaw.ServerCompanion.Bookkeeping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace KoalaPaw.ServerCompanion.Api;

/// <summary>Minimal API for the dashboard config page: failure count + clear.</summary>
[ApiController]
[Authorize]
[Route("KoalaPaw")]
public class AnalysisController : ControllerBase
{
    private readonly AnalysisStore _store;

    public AnalysisController(AnalysisStore store)
    {
        _store = store;
    }

    [HttpGet("Failures")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<FailureSummary> GetFailures()
    {
        return Ok(new FailureSummary { Count = _store.GetFailureCount() });
    }

    [HttpPost("Failures/Clear")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearFailures()
    {
        _store.ClearFailures();
        return NoContent();
    }

    public sealed class FailureSummary
    {
        public int Count { get; set; }
    }
}