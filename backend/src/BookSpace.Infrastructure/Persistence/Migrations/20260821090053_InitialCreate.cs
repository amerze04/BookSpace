using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovalRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: true),
                    Decision = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequests", x => x.Id);
                    table.UniqueConstraint("UQ_ApprovalRequests_Booking", x => x.BookingId);
                    table.CheckConstraint("CK_ApprovalRequests_Decision", "[Decision] IN ('Pending','Approved','Rejected','Expired')");
                    table.CheckConstraint("CK_ApprovalRequests_DecisionPaired", "([Decision] = 'Pending' AND [DecidedByUserId] IS NULL AND [DecidedAtUtc] IS NULL) OR ([Decision] <> 'Pending' AND [DecidedAtUtc] IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "AvailabilityWindows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Weekday = table.Column<byte>(type: "tinyint", nullable: false),
                    OpensAt = table.Column<TimeOnly>(type: "time(0)", precision: 0, nullable: false),
                    ClosesAt = table.Column<TimeOnly>(type: "time(0)", precision: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AvailabilityWindows", x => x.Id);
                    table.CheckConstraint("CK_AvailabilityWindows_Weekday", "[Weekday] BETWEEN 0 AND 6");
                    table.CheckConstraint("CK_AvailabilityWindows_Window", "[ClosesAt] > [OpensAt]");
                });

            migrationBuilder.CreateTable(
                name: "BlackoutPeriods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartsAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    EndsAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlackoutPeriods", x => x.Id);
                    table.CheckConstraint("CK_BlackoutPeriods_Interval", "[EndsAtUtc] > [StartsAtUtc]");
                });

            migrationBuilder.CreateTable(
                name: "Bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrgId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecurrenceRuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StartsAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    EndsAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CheckedInAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: true),
                    CancelledByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bookings", x => x.Id);
                    table.CheckConstraint("CK_Bookings_Interval", "[EndsAtUtc] > [StartsAtUtc]");
                    table.CheckConstraint("CK_Bookings_Quantity", "[Quantity] > 0");
                    table.CheckConstraint("CK_Bookings_Status", "[Status] IN ('Pending','Confirmed','Rejected','Cancelled','Completed','NoShow')");
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecurrenceRuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurrenceDate = table.Column<DateOnly>(type: "date", nullable: true),
                    RecipientUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SendAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.CheckConstraint("CK_Notifications_HasContext", "[BookingId] IS NOT NULL OR ([RecurrenceRuleId] IS NOT NULL AND [OccurrenceDate] IS NOT NULL)");
                    table.CheckConstraint("CK_Notifications_Kind", "[Kind] IN ('Confirmed','Rejected','Cancelled','Reminder','ApprovalRequested','NoShowReleased','RecurrenceOccurrenceSkipped')");
                    table.ForeignKey(
                        name: "FK_Notifications_Bookings",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Organizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ReminderLeadMinutes = table.Column<int>(type: "int", nullable: false),
                    NoShowGraceMinutes = table.Column<int>(type: "int", nullable: false),
                    ApprovalExpiryHours = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                    table.UniqueConstraint("UQ_Organizations_Slug", x => x.Slug);
                    table.CheckConstraint("CK_Organizations_Status", "[Status] IN ('Active','Suspended')");
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrgId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CalendarFeedToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.UniqueConstraint("UQ_Users_CalendarFeedToken", x => x.CalendarFeedToken);
                    table.ForeignKey(
                        name: "FK_Users_CreatedBy",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Users_Organizations",
                        column: x => x.OrgId,
                        principalTable: "Organizations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Users_UpdatedBy",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: true),
                    ReplacedByTokenId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                    table.UniqueConstraint("UQ_RefreshTokens_TokenHash", x => x.TokenHash);
                    table.ForeignKey(
                        name: "FK_RefreshTokens_Replacement",
                        column: x => x.ReplacedByTokenId,
                        principalTable: "RefreshTokens",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RefreshTokens_Users",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Resources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrgId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ResourceType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Capacity = table.Column<int>(type: "int", nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    RequiresApproval = table.Column<bool>(type: "bit", nullable: false),
                    MinDurationMinutes = table.Column<int>(type: "int", nullable: true),
                    MaxDurationMinutes = table.Column<int>(type: "int", nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Resources", x => x.Id);
                    table.UniqueConstraint("UQ_Resources_Org_Id", x => new { x.OrgId, x.Id });
                    table.CheckConstraint("CK_Resources_Capacity", "[Capacity] > 0");
                    table.ForeignKey(
                        name: "FK_Resources_CreatedBy",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Resources_Organizations",
                        column: x => x.OrgId,
                        principalTable: "Organizations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Resources_UpdatedBy",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                columns: table => new
                {
                    Role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => new { x.UserId, x.Role });
                    table.CheckConstraint("CK_UserRoles_Role", "[Role] IN ('SysAdmin','TenantAdmin','Approver','Member')");
                    table.ForeignKey(
                        name: "FK_UserRoles_Users",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecurrenceRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Frequency = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    IntervalValue = table.Column<int>(type: "int", nullable: false),
                    LocalStartTime = table.Column<TimeOnly>(type: "time(0)", precision: 0, nullable: false),
                    LocalEndTime = table.Column<TimeOnly>(type: "time(0)", precision: 0, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    OccurrenceCount = table.Column<int>(type: "int", nullable: true),
                    TimeZoneId = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurrenceRules", x => x.Id);
                    table.CheckConstraint("CK_RecurrenceRules_EndCondition", "([EndDate] IS NOT NULL AND [OccurrenceCount] IS NULL) OR ([EndDate] IS NULL AND [OccurrenceCount] IS NOT NULL)");
                    table.CheckConstraint("CK_RecurrenceRules_Frequency", "[Frequency] IN ('Daily','Weekly','Monthly')");
                    table.CheckConstraint("CK_RecurrenceRules_Interval", "[IntervalValue] > 0");
                    table.CheckConstraint("CK_RecurrenceRules_MaxSpan", "[EndDate] IS NULL OR [EndDate] <= DATEADD(YEAR, 2, [StartDate])");
                    table.CheckConstraint("CK_RecurrenceRules_Status", "[Status] IN ('Active','Cancelled')");
                    table.ForeignKey(
                        name: "FK_RecurrenceRules_CreatedBy",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RecurrenceRules_Resources",
                        column: x => x.ResourceId,
                        principalTable: "Resources",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RecurrenceRules_UpdatedBy",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RecurrenceRules_Users",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ResourceApprovers",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceApprovers", x => new { x.ResourceId, x.UserId });
                    table.ForeignKey(
                        name: "FK_ResourceApprovers_Resources",
                        column: x => x.ResourceId,
                        principalTable: "Resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResourceApprovers_Users",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_DecidedByUserId",
                table: "ApprovalRequests",
                column: "DecidedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_Pending",
                table: "ApprovalRequests",
                column: "ExpiresAtUtc",
                filter: "[Decision] = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_AvailabilityWindows_ResourceId",
                table: "AvailabilityWindows",
                column: "ResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_BlackoutPeriods_CreatedByUserId",
                table: "BlackoutPeriods",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BlackoutPeriods_Resource_Start",
                table: "BlackoutPeriods",
                columns: new[] { "ResourceId", "StartsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BlackoutPeriods_UpdatedByUserId",
                table: "BlackoutPeriods",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_CancelledByUserId",
                table: "Bookings",
                column: "CancelledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_CreatedByUserId",
                table: "Bookings",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_NoShowSweep",
                table: "Bookings",
                column: "StartsAtUtc",
                filter: "[Status] = 'Confirmed' AND [CheckedInAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_OrgId_ResourceId",
                table: "Bookings",
                columns: new[] { "OrgId", "ResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_Resource_Start",
                table: "Bookings",
                columns: new[] { "ResourceId", "StartsAtUtc" })
                .Annotation("SqlServer:Include", new[] { "EndsAtUtc", "Status", "Quantity" });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_Series",
                table: "Bookings",
                column: "RecurrenceRuleId",
                filter: "[RecurrenceRuleId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_UpdatedByUserId",
                table: "Bookings",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_User",
                table: "Bookings",
                columns: new[] { "OrgId", "UserId", "StartsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_UserId",
                table: "Bookings",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_CreatedByUserId",
                table: "Notifications",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_Due",
                table: "Notifications",
                column: "SendAtUtc",
                filter: "[SentAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RecipientUserId",
                table: "Notifications",
                column: "RecipientUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RecurrenceRuleId",
                table: "Notifications",
                column: "RecurrenceRuleId");

            migrationBuilder.CreateIndex(
                name: "UQ_Notifications_Once",
                table: "Notifications",
                columns: new[] { "BookingId", "RecurrenceRuleId", "OccurrenceDate", "RecipientUserId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_CreatedByUserId",
                table: "Organizations",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_UpdatedByUserId",
                table: "Organizations",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrenceRules_CreatedByUserId",
                table: "RecurrenceRules",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrenceRules_ResourceId",
                table: "RecurrenceRules",
                column: "ResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrenceRules_UpdatedByUserId",
                table: "RecurrenceRules",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrenceRules_UserId",
                table: "RecurrenceRules",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_Family",
                table: "RefreshTokens",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_ReplacedByTokenId",
                table: "RefreshTokens",
                column: "ReplacedByTokenId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_UserId",
                table: "RefreshTokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceApprovers_UserId",
                table: "ResourceApprovers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_CreatedByUserId",
                table: "Resources",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_UpdatedByUserId",
                table: "Resources",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_CreatedByUserId",
                table: "Users",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_UpdatedByUserId",
                table: "Users",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "UX_Users_Org_Email",
                table: "Users",
                columns: new[] { "OrgId", "Email" },
                unique: true,
                filter: "[OrgId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_ApprovalRequests_Bookings",
                table: "ApprovalRequests",
                column: "BookingId",
                principalTable: "Bookings",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ApprovalRequests_Users",
                table: "ApprovalRequests",
                column: "DecidedByUserId",
                principalTable: "Users",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_AvailabilityWindows_Resources",
                table: "AvailabilityWindows",
                column: "ResourceId",
                principalTable: "Resources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_BlackoutPeriods_CreatedBy",
                table: "BlackoutPeriods",
                column: "CreatedByUserId",
                principalTable: "Users",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_BlackoutPeriods_UpdatedBy",
                table: "BlackoutPeriods",
                column: "UpdatedByUserId",
                principalTable: "Users",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_BlackoutPeriods_Resources",
                table: "BlackoutPeriods",
                column: "ResourceId",
                principalTable: "Resources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_CancelledBy",
                table: "Bookings",
                column: "CancelledByUserId",
                principalTable: "Users",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_CreatedBy",
                table: "Bookings",
                column: "CreatedByUserId",
                principalTable: "Users",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_UpdatedBy",
                table: "Bookings",
                column: "UpdatedByUserId",
                principalTable: "Users",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_Users",
                table: "Bookings",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_RecurrenceRules",
                table: "Bookings",
                column: "RecurrenceRuleId",
                principalTable: "RecurrenceRules",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_Resources_SameOrg",
                table: "Bookings",
                columns: new[] { "OrgId", "ResourceId" },
                principalTable: "Resources",
                principalColumns: new[] { "OrgId", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_Notifications_CreatedBy",
                table: "Notifications",
                column: "CreatedByUserId",
                principalTable: "Users",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Notifications_Users",
                table: "Notifications",
                column: "RecipientUserId",
                principalTable: "Users",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Notifications_RecurrenceRules",
                table: "Notifications",
                column: "RecurrenceRuleId",
                principalTable: "RecurrenceRules",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Organizations_CreatedBy",
                table: "Organizations",
                column: "CreatedByUserId",
                principalTable: "Users",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Organizations_UpdatedBy",
                table: "Organizations",
                column: "UpdatedByUserId",
                principalTable: "Users",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Organizations_CreatedBy",
                table: "Organizations");

            migrationBuilder.DropForeignKey(
                name: "FK_Organizations_UpdatedBy",
                table: "Organizations");

            migrationBuilder.DropTable(
                name: "ApprovalRequests");

            migrationBuilder.DropTable(
                name: "AvailabilityWindows");

            migrationBuilder.DropTable(
                name: "BlackoutPeriods");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "RefreshTokens");

            migrationBuilder.DropTable(
                name: "ResourceApprovers");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropTable(
                name: "Bookings");

            migrationBuilder.DropTable(
                name: "RecurrenceRules");

            migrationBuilder.DropTable(
                name: "Resources");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Organizations");
        }
    }
}
