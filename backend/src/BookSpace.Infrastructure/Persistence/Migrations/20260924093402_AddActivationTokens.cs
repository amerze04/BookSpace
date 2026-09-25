using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // User management phase 2 (docs/user-management-plan.md). What a
    // provisioned user redeems to set their first password.
    //
    // **No OrgId, no filter predicate on Security.TenantAccessPolicy, and that
    // is deliberate** — the same shape RefreshTokens has, for the same reason.
    // Activation runs before the user has ever signed in, so there is no tenant
    // context for CLAUDE.md §4.2's three mechanisms to act on; adding an OrgId
    // here would create a column nothing could ever filter by at the moment it
    // matters. The token's own secrecy is the access control: 256 bits from a
    // CSPRNG, stored only as its SHA-256, single-use (see ActivationToken and
    // docs/decisions/0011-refresh-token-hashing-and-rotation.md).
    //
    // Cascade on the Users FK, unlike most of this schema's NoAction default
    // (CLAUDE.md §5) — single-path and obviously safe, matching RefreshTokens.
    // Nothing deletes a user (§4.5), so in practice it never fires.
    //
    // ConsumedAtUtc is EF concurrency-token metadata, not a column property, so
    // it produces no DDL here — see ActivationTokenConfiguration for why
    // single-use depends on it.
    public partial class AddActivationTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivationTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivationTokens", x => x.Id);
                    table.UniqueConstraint("UQ_ActivationTokens_TokenHash", x => x.TokenHash);
                    table.ForeignKey(
                        name: "FK_ActivationTokens_Users",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivationTokens_User",
                table: "ActivationTokens",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivationTokens");
        }
    }
}
