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

    [HttpGet("db")]
    public async Task<IActionResult> GetDatabase(CancellationToken cancellationToken)
    {
        var connectionString = _configuration.GetConnectionString("BookSpaceDb");

        await using var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return Ok(new { status = "healthy", database = connection.Database, server = connection.DataSource });
        }
        catch (SqlException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "unhealthy", error = ex.Message });
        }
    }
}
