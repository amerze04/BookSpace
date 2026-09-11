using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace BookSpace.Api.Controllers;

[ApiController]
[Route("health")]
// Reachable without a token: the fallback policy in AuthorizationPolicies
// protects everything by default, and a health check that needs credentials is
// useless to a load balancer or uptime monitor.
[AllowAnonymous]
public class HealthController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<HealthController> _logger;

    public HealthController(IConfiguration configuration, ILogger<HealthController> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Get()
    {
        _logger.LogInformation("Health check requested");
        return Ok(new { status = "healthy" });
    }

    // Hardening pass, P2 security. Anonymous and reachable by any caller —
    // deliberately, this endpoint exists for a load balancer or uptime
    // monitor with no credentials — so it must say nothing beyond "can this
    // API reach its database right now". It used to also return the database
    // name, the SQL Server host, and (on failure) the raw SqlException.Message,
    // any of which can carry infrastructure detail an anonymous caller has no
    // business learning. The real exception still goes to the log, with the
    // correlation id already ambient in Serilog's LogContext
    // (CorrelationIdMiddleware), which is where a genuine failure should be
    // diagnosed from — not the response body.
    [HttpGet("db")]
    public async Task<IActionResult> GetDatabase(CancellationToken cancellationToken)
    {
        var connectionString = _configuration.GetConnectionString("BookSpaceDb");

        await using var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return Ok(new { status = "healthy" });
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "Health check failed: could not open a connection to the database");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "unhealthy" });
        }
    }
}
