using Microsoft.AspNetCore.Mvc;
using ProductOrderApi.Services;

namespace ProductOrderApi.Controllers;

[ApiController]
[Route("health")]
public sealed class HealthController(
    DatabaseHealthService healthService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<object>> Get(CancellationToken cancellationToken)
    {
        await healthService.EnsureHealthyAsync(cancellationToken);
        return Ok(new { status = "Healthy" });
    }
}
