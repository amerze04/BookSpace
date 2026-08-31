using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResourceDurationLimitsCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_Resources_DurationLimits",
                table: "Resources",
                sql: "([MinDurationMinutes] IS NULL OR [MinDurationMinutes] > 0) AND ([MaxDurationMinutes] IS NULL OR [MaxDurationMinutes] > 0) AND ([MinDurationMinutes] IS NULL OR [MaxDurationMinutes] IS NULL OR [MaxDurationMinutes] >= [MinDurationMinutes])");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Resources_DurationLimits",
                table: "Resources");
        }
    }
}
