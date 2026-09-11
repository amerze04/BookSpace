using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // Hardening pass, P1. CreateBookingCommandRequestHandler decides Pending vs
    // Confirmed by reading Resource.RequiresApproval **before** the unit of
    // work opens (CreateBookingCommandRequestHandler.cs), then passes that
    // decision to dbo.CreateBooking as @Status. Until this migration, the
    // procedure trusted it outright — so an admin flipping RequiresApproval
    // false→true while a request was in flight could commit a Confirmed
    // booking on a now-approval-gated resource, silently bypassing FR-7.1.
    //
    // The fix is a downgrade, not a re-derivation: the procedure already reads
    // Resources under no special lock before it decides anything (decision
    // 0023), so this widens that same read to include RequiresApproval and,
    // only when it is true and the caller asked for Confirmed, stores Pending
    // instead. It never *upgrades* a caller's Pending to Confirmed — a
    // resource that stopped requiring approval mid-request producing an
    // unnecessarily-Pending booking is conservative, not a hazard, and
    // CreateBookingProcedureTests deliberately forces Pending directly on a
    // non-approval-gated resource to prove Pending still counts against
    // capacity (decision 0005); a full re-derivation would break that test by
    // design, not by accident.
    //
    // The procedure now returns the status it actually stored, as a third
    // result column, so BookingCreationOutcome.ActualStatus lets the caller
    // react — staging the ApprovalRequest and the approval-requested
    // notifications it would otherwise have skipped, rather than leaving a
    // Pending booking with no decision record (the same FR-7.1 invariant this
    // codebase already guards after WP-4 Phase 3). See
    // CreateBookingCommandRequestHandler and
    // CreateRecurrenceSeriesCommandRequestHandler.CreateOccurrenceAsync for
    // both callers reacting to it.
    public partial class AlterCreateBookingProcedureRequiresApprovalRecheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER PROCEDURE dbo.CreateBooking
                    @BookingId        UNIQUEIDENTIFIER,
                    @ResourceId       UNIQUEIDENTIFIER,
                    @UserId           UNIQUEIDENTIFIER,
                    @StartsAtUtc      DATETIME2(0),
                    @EndsAtUtc        DATETIME2(0),
                    @Quantity         INT,
                    @Status           NVARCHAR(20),
                    @CreatedByUserId  UNIQUEIDENTIFIER,
                    @NowUtc           DATETIME2(0),
                    @RecurrenceRuleId UNIQUEIDENTIFIER = NULL,
                    @Title            NVARCHAR(200)    = NULL
                AS
                BEGIN
                    SET NOCOUNT ON;
                    SET XACT_ABORT ON;

                    IF @Status NOT IN ('Pending', 'Confirmed')
                    BEGIN
                        THROW 50000, 'dbo.CreateBooking accepts only Pending or Confirmed.', 1;
                    END

                    DECLARE @ownTransaction BIT = 0;

                    IF @@TRANCOUNT = 0
                    BEGIN
                        SET @ownTransaction = 1;
                        BEGIN TRANSACTION;
                    END

                    DECLARE @orgId            UNIQUEIDENTIFIER;
                    DECLARE @capacity         INT;
                    DECLARE @isArchived       BIT;
                    DECLARE @requiresApproval BIT;

                    SELECT
                        @orgId            = r.OrgId,
                        @capacity         = r.Capacity,
                        @isArchived       = r.IsArchived,
                        @requiresApproval = r.RequiresApproval
                    FROM dbo.Resources AS r
                    WHERE r.Id = @ResourceId;

                    IF @orgId IS NULL
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'ResourceNotFound' AS ResultCode, NULL AS RemainingCapacity, NULL AS ActualStatus;
                        RETURN;
                    END

                    IF @isArchived = 1
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'ResourceArchived' AS ResultCode, NULL AS RemainingCapacity, NULL AS ActualStatus;
                        RETURN;
                    END

                    IF EXISTS (
                        SELECT 1
                        FROM dbo.BlackoutPeriods AS bp WITH (HOLDLOCK)
                        WHERE bp.ResourceId = @ResourceId
                          AND bp.StartsAtUtc < @EndsAtUtc
                          AND bp.EndsAtUtc   > @StartsAtUtc)
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'BlackoutPeriod' AS ResultCode, NULL AS RemainingCapacity, NULL AS ActualStatus;
                        RETURN;
                    END

                    DECLARE @overlapping TABLE (
                        StartsAtUtc DATETIME2(0) NOT NULL,
                        EndsAtUtc   DATETIME2(0) NOT NULL,
                        Quantity    INT          NOT NULL);

                    INSERT INTO @overlapping (StartsAtUtc, EndsAtUtc, Quantity)
                    SELECT b.StartsAtUtc, b.EndsAtUtc, b.Quantity
                    FROM dbo.Bookings AS b WITH (UPDLOCK, HOLDLOCK)
                    WHERE b.ResourceId = @ResourceId
                      AND b.Status IN ('Pending', 'Confirmed')
                      AND b.StartsAtUtc < @EndsAtUtc
                      AND b.EndsAtUtc   > @StartsAtUtc;

                    DECLARE @peak INT;

                    SELECT @peak = ISNULL(MAX(u.Used), 0)
                    FROM (
                        SELECT @StartsAtUtc AS T
                        UNION
                        SELECT o.StartsAtUtc FROM @overlapping AS o WHERE o.StartsAtUtc > @StartsAtUtc
                    ) AS boundary
                    CROSS APPLY (
                        SELECT SUM(o.Quantity) AS Used
                        FROM @overlapping AS o
                        WHERE o.StartsAtUtc <= boundary.T
                          AND o.EndsAtUtc   >  boundary.T
                    ) AS u;

                    DECLARE @remaining INT = @capacity - @peak;

                    IF @remaining < @Quantity
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;

                        SELECT
                            CASE WHEN @remaining <= 0 THEN 'SlotUnavailable' ELSE 'CapacityExceeded' END
                                AS ResultCode,
                            @remaining AS RemainingCapacity,
                            NULL AS ActualStatus;
                        RETURN;
                    END

                    -- P1: downgrade only, never upgrade. @requiresApproval is
                    -- read under this same procedure call, moments before the
                    -- insert, which is as close to "at commit time" as this
                    -- system gets — closing the gap where the caller's
                    -- Confirmed decision was made from a snapshot taken before
                    -- the unit of work even opened.
                    DECLARE @finalStatus NVARCHAR(20) =
                        CASE WHEN @requiresApproval = 1 AND @Status = 'Confirmed'
                             THEN 'Pending'
                             ELSE @Status
                        END;

                    INSERT INTO dbo.Bookings
                        (Id, OrgId, ResourceId, UserId, RecurrenceRuleId,
                         StartsAtUtc, EndsAtUtc, Quantity, Title, Status,
                         CreatedAtUtc, CreatedByUserId, UpdatedAtUtc, UpdatedByUserId)
                    VALUES
                        (@BookingId, @orgId, @ResourceId, @UserId, @RecurrenceRuleId,
                         @StartsAtUtc, @EndsAtUtc, @Quantity, @Title, @finalStatus,
                         @NowUtc, @CreatedByUserId, @NowUtc, @CreatedByUserId);

                    IF @ownTransaction = 1 COMMIT TRANSACTION;

                    SELECT 'Created' AS ResultCode, @remaining - @Quantity AS RemainingCapacity,
                        @finalStatus AS ActualStatus;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER PROCEDURE dbo.CreateBooking
                    @BookingId        UNIQUEIDENTIFIER,
                    @ResourceId       UNIQUEIDENTIFIER,
                    @UserId           UNIQUEIDENTIFIER,
                    @StartsAtUtc      DATETIME2(0),
                    @EndsAtUtc        DATETIME2(0),
                    @Quantity         INT,
                    @Status           NVARCHAR(20),
                    @CreatedByUserId  UNIQUEIDENTIFIER,
                    @NowUtc           DATETIME2(0),
                    @RecurrenceRuleId UNIQUEIDENTIFIER = NULL,
                    @Title            NVARCHAR(200)    = NULL
                AS
                BEGIN
                    SET NOCOUNT ON;
                    SET XACT_ABORT ON;

                    IF @Status NOT IN ('Pending', 'Confirmed')
                    BEGIN
                        THROW 50000, 'dbo.CreateBooking accepts only Pending or Confirmed.', 1;
                    END

                    DECLARE @ownTransaction BIT = 0;

                    IF @@TRANCOUNT = 0
                    BEGIN
                        SET @ownTransaction = 1;
                        BEGIN TRANSACTION;
                    END

                    DECLARE @orgId      UNIQUEIDENTIFIER;
                    DECLARE @capacity   INT;
                    DECLARE @isArchived BIT;

                    SELECT
                        @orgId      = r.OrgId,
                        @capacity   = r.Capacity,
                        @isArchived = r.IsArchived
                    FROM dbo.Resources AS r
                    WHERE r.Id = @ResourceId;

                    IF @orgId IS NULL
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'ResourceNotFound' AS ResultCode, NULL AS RemainingCapacity;
                        RETURN;
                    END

                    IF @isArchived = 1
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'ResourceArchived' AS ResultCode, NULL AS RemainingCapacity;
                        RETURN;
                    END

                    IF EXISTS (
                        SELECT 1
                        FROM dbo.BlackoutPeriods AS bp WITH (HOLDLOCK)
                        WHERE bp.ResourceId = @ResourceId
                          AND bp.StartsAtUtc < @EndsAtUtc
                          AND bp.EndsAtUtc   > @StartsAtUtc)
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'BlackoutPeriod' AS ResultCode, NULL AS RemainingCapacity;
                        RETURN;
                    END

                    DECLARE @overlapping TABLE (
                        StartsAtUtc DATETIME2(0) NOT NULL,
                        EndsAtUtc   DATETIME2(0) NOT NULL,
                        Quantity    INT          NOT NULL);

                    INSERT INTO @overlapping (StartsAtUtc, EndsAtUtc, Quantity)
                    SELECT b.StartsAtUtc, b.EndsAtUtc, b.Quantity
                    FROM dbo.Bookings AS b WITH (UPDLOCK, HOLDLOCK)
                    WHERE b.ResourceId = @ResourceId
                      AND b.Status IN ('Pending', 'Confirmed')
                      AND b.StartsAtUtc < @EndsAtUtc
                      AND b.EndsAtUtc   > @StartsAtUtc;

                    DECLARE @peak INT;

                    SELECT @peak = ISNULL(MAX(u.Used), 0)
                    FROM (
                        SELECT @StartsAtUtc AS T
                        UNION
                        SELECT o.StartsAtUtc FROM @overlapping AS o WHERE o.StartsAtUtc > @StartsAtUtc
                    ) AS boundary
                    CROSS APPLY (
                        SELECT SUM(o.Quantity) AS Used
                        FROM @overlapping AS o
                        WHERE o.StartsAtUtc <= boundary.T
                          AND o.EndsAtUtc   >  boundary.T
                    ) AS u;

                    DECLARE @remaining INT = @capacity - @peak;

                    IF @remaining < @Quantity
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;

                        SELECT
                            CASE WHEN @remaining <= 0 THEN 'SlotUnavailable' ELSE 'CapacityExceeded' END
                                AS ResultCode,
                            @remaining AS RemainingCapacity;
                        RETURN;
                    END

                    INSERT INTO dbo.Bookings
                        (Id, OrgId, ResourceId, UserId, RecurrenceRuleId,
                         StartsAtUtc, EndsAtUtc, Quantity, Title, Status,
                         CreatedAtUtc, CreatedByUserId, UpdatedAtUtc, UpdatedByUserId)
                    VALUES
                        (@BookingId, @orgId, @ResourceId, @UserId, @RecurrenceRuleId,
                         @StartsAtUtc, @EndsAtUtc, @Quantity, @Title, @Status,
                         @NowUtc, @CreatedByUserId, @NowUtc, @CreatedByUserId);

                    IF @ownTransaction = 1 COMMIT TRANSACTION;

                    SELECT 'Created' AS ResultCode, @remaining - @Quantity AS RemainingCapacity;
                END
                """);
        }
    }
}
