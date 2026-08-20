using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace BookSpace.Api.Controllers;

[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public HealthController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet]
    public IActionResult Get() => Ok(new { status = "healthy" });

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
