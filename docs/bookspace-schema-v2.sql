/* ============================================================
   BookSpace — SQL Server schema v2.0
   Aligned to Product Requirements Document v1.0

   12 tables. Every table traces to a Must requirement;
   see the FR tags in the comments below.

   Import to Visual Paradigm:
     Tools > DB > Reverse DDL > Database: MS SQL
   ============================================================ */

/* --- Audit trail convention ------------------------------
   Engineering convention (not a PRD requirement): every table below except
   UserRoles, ResourceApprovers, AvailabilityWindows (pure join / composite-PK,
   never individually updated), RefreshTokens and ApprovalRequests (already
   have more precise lifecycle columns — IssuedAtUtc/RevokedAtUtc and
   RequestedAtUtc/DecidedAtUtc/DecidedByUserId respectively) carries:
     CreatedAtUtc     DATETIME2(0)     NOT NULL
     CreatedByUserId  UNIQUEIDENTIFIER NOT NULL  -- who/what created the row
     UpdatedAtUtc     DATETIME2(0)     NOT NULL  -- = CreatedAtUtc at insert
     UpdatedByUserId  UNIQUEIDENTIFIER NULL      -- NULL = last change was a
                                                  -- background job, not a person
   Notifications is a partial exception — see its definition below.
   CreatedByUserId is NOT NULL because every row is created as the result of
   a human-initiated action (even Bookings, inserted by dbo.CreateBooking, are
   always on behalf of a specific Member). Users.CreatedByUserId is
   self-referencing and NOT NULL: no user self-registers, every account is
   provisioned by a SysAdmin or TenantAdmin; the first-ever SysAdmin row is
   seeded by migration, self-referencing its own Id.
   ============================================================ */

/* --- Tenancy -------------------------------------------- */

-- FR-1.1 root of ownership; FR-1.3 suspend/reactivate;
-- FR-7.4 / FR-8.3 / FR-9.1 configurable windows live here
CREATE TABLE Organizations (
    Id                   UNIQUEIDENTIFIER NOT NULL,
    Name                 NVARCHAR(200)    NOT NULL,
    Slug                 NVARCHAR(60)     NOT NULL,
    TimeZoneId           NVARCHAR(60)     NOT NULL,
    Status               NVARCHAR(20)     NOT NULL,
    ReminderLeadMinutes  INT              NOT NULL,
    NoShowGraceMinutes   INT              NOT NULL,
    ApprovalExpiryHours  INT              NULL,
    CreatedAtUtc         DATETIME2(0)     NOT NULL,
    CreatedByUserId      UNIQUEIDENTIFIER NOT NULL,
    UpdatedAtUtc         DATETIME2(0)     NOT NULL,
    UpdatedByUserId      UNIQUEIDENTIFIER NULL,
    CONSTRAINT PK_Organizations PRIMARY KEY (Id),
    CONSTRAINT UQ_Organizations_Slug UNIQUE (Slug),
    CONSTRAINT CK_Organizations_Status CHECK (Status IN ('Active','Suspended'))
    -- CreatedBy/UpdatedBy FKs to Users are added below, after CREATE TABLE
    -- Users, because Users.OrgId references Organizations right back —
    -- SQL Server can't resolve that cycle inline in either CREATE TABLE.
);

-- FR-1.5 one tenant per user; OrgId NULL = SysAdmin (above tenants)
-- FR-8.2 CalendarFeedToken is the ICS subscription secret
CREATE TABLE Users (
    Id                UNIQUEIDENTIFIER NOT NULL,
    OrgId             UNIQUEIDENTIFIER NULL,
    Email             NVARCHAR(320)    NOT NULL,
    PasswordHash      NVARCHAR(255)    NOT NULL,
    FullName          NVARCHAR(200)    NOT NULL,
    IsActive          BIT              NOT NULL,
    CalendarFeedToken UNIQUEIDENTIFIER NOT NULL,
    CreatedAtUtc      DATETIME2(0)     NOT NULL,
    CreatedByUserId   UNIQUEIDENTIFIER NOT NULL,
    UpdatedAtUtc      DATETIME2(0)     NOT NULL,
    UpdatedByUserId   UNIQUEIDENTIFIER NULL,
    CONSTRAINT PK_Users PRIMARY KEY (Id),
    CONSTRAINT UQ_Users_CalendarFeedToken UNIQUE (CalendarFeedToken),
    CONSTRAINT FK_Users_Organizations FOREIGN KEY (OrgId)
        REFERENCES Organizations (Id),
    -- self-referencing: no self-registration, every user is provisioned by a
    -- SysAdmin or TenantAdmin; bootstrap SysAdmin row self-references its own Id
    CONSTRAINT FK_Users_CreatedBy FOREIGN KEY (CreatedByUserId)
        REFERENCES Users (Id),
    CONSTRAINT FK_Users_UpdatedBy FOREIGN KEY (UpdatedByUserId)
        REFERENCES Users (Id)
);

-- Deferred from CREATE TABLE Organizations (see comment there): Users must
-- exist first because of the Organizations <-> Users circular reference.
ALTER TABLE Organizations
    ADD CONSTRAINT FK_Organizations_CreatedBy FOREIGN KEY (CreatedByUserId)
        REFERENCES Users (Id);
ALTER TABLE Organizations
    ADD CONSTRAINT FK_Organizations_UpdatedBy FOREIGN KEY (UpdatedByUserId)
        REFERENCES Users (Id);

-- FR-1.4 / FR-1.5 roles are additive within a tenant
CREATE TABLE UserRoles (
    UserId UNIQUEIDENTIFIER NOT NULL,
    Role   NVARCHAR(20)     NOT NULL,
    CONSTRAINT PK_UserRoles PRIMARY KEY (UserId, Role),
    CONSTRAINT FK_UserRoles_Users FOREIGN KEY (UserId)
        REFERENCES Users (Id) ON DELETE CASCADE,
    CONSTRAINT CK_UserRoles_Role CHECK
        (Role IN ('SysAdmin','TenantAdmin','Approver','Member'))
);

-- FR-2.1 rotating refresh tokens; FR-2.2 reuse kills the family;
-- FR-2.3 hash only, never the token itself
CREATE TABLE RefreshTokens (
    Id                UNIQUEIDENTIFIER NOT NULL,
    UserId            UNIQUEIDENTIFIER NOT NULL,
    TokenHash         NVARCHAR(255)    NOT NULL,
    FamilyId          UNIQUEIDENTIFIER NOT NULL,
    IssuedAtUtc       DATETIME2(0)     NOT NULL,
    ExpiresAtUtc      DATETIME2(0)     NOT NULL,
    RevokedAtUtc      DATETIME2(0)     NULL,
    ReplacedByTokenId UNIQUEIDENTIFIER NULL,
    CONSTRAINT PK_RefreshTokens PRIMARY KEY (Id),
    CONSTRAINT UQ_RefreshTokens_TokenHash UNIQUE (TokenHash),
    CONSTRAINT FK_RefreshTokens_Users FOREIGN KEY (UserId)
        REFERENCES Users (Id) ON DELETE CASCADE,
    CONSTRAINT FK_RefreshTokens_Replacement FOREIGN KEY (ReplacedByTokenId)
        REFERENCES RefreshTokens (Id)
);

/* --- Resources ------------------------------------------ */

-- FR-3.1 type/capacity/timezone; FR-3.5 archive preserves history
-- Capacity = concurrent units the resource supports (see FR-4.2)
CREATE TABLE Resources (
    Id                 UNIQUEIDENTIFIER NOT NULL,
    OrgId              UNIQUEIDENTIFIER NOT NULL,
    Name               NVARCHAR(200)    NOT NULL,
    Description        NVARCHAR(1000)   NULL,
    ResourceType       NVARCHAR(50)     NOT NULL,
    Capacity           INT              NOT NULL,
    TimeZoneId         NVARCHAR(60)     NOT NULL,
    RequiresApproval   BIT              NOT NULL,
    MinDurationMinutes INT              NULL,
    MaxDurationMinutes INT              NULL,
    IsArchived         BIT              NOT NULL,
    CreatedAtUtc       DATETIME2(0)     NOT NULL,
    CreatedByUserId    UNIQUEIDENTIFIER NOT NULL,
    UpdatedAtUtc       DATETIME2(0)     NOT NULL,
    UpdatedByUserId    UNIQUEIDENTIFIER NULL,
    CONSTRAINT PK_Resources PRIMARY KEY (Id),
    -- alternate key: target of the composite tenant FK on Bookings
    CONSTRAINT UQ_Resources_Org_Id UNIQUE (OrgId, Id),
    CONSTRAINT FK_Resources_Organizations FOREIGN KEY (OrgId)
        REFERENCES Organizations (Id),
    CONSTRAINT FK_Resources_CreatedBy FOREIGN KEY (CreatedByUserId)
        REFERENCES Users (Id),
    CONSTRAINT FK_Resources_UpdatedBy FOREIGN KEY (UpdatedByUserId)
        REFERENCES Users (Id),
    CONSTRAINT CK_Resources_Capacity CHECK (Capacity > 0)
);

-- FR-3.3 one or more assigned approvers per resource
CREATE TABLE ResourceApprovers (
    ResourceId UNIQUEIDENTIFIER NOT NULL,
    UserId     UNIQUEIDENTIFIER NOT NULL,
    CONSTRAINT PK_ResourceApprovers PRIMARY KEY (ResourceId, UserId),
    CONSTRAINT FK_ResourceApprovers_Resources FOREIGN KEY (ResourceId)
        REFERENCES Resources (Id),
    CONSTRAINT FK_ResourceApprovers_Users FOREIGN KEY (UserId)
        REFERENCES Users (Id)
);

-- FR-3.2 recurring open hours, resource-local wall clock (FR-6.3)
CREATE TABLE AvailabilityWindows (
    Id         UNIQUEIDENTIFIER NOT NULL,
    ResourceId UNIQUEIDENTIFIER NOT NULL,
    Weekday    TINYINT          NOT NULL,
    OpensAt    TIME(0)          NOT NULL,
    ClosesAt   TIME(0)          NOT NULL,
    CONSTRAINT PK_AvailabilityWindows PRIMARY KEY (Id),
    CONSTRAINT FK_AvailabilityWindows_Resources FOREIGN KEY (ResourceId)
        REFERENCES Resources (Id) ON DELETE CASCADE,
    CONSTRAINT CK_AvailabilityWindows_Weekday CHECK (Weekday BETWEEN 0 AND 6),
    CONSTRAINT CK_AvailabilityWindows_Window CHECK (ClosesAt > OpensAt)
);

-- FR-3.4 blackouts override availability; absolute instants
-- Decision #1 (docs/decisions/0001-blackout-vs-recurring-series.md): blackouts
-- have absolute priority — occurrences they overlap get cancelled, booker notified
CREATE TABLE BlackoutPeriods (
    Id              UNIQUEIDENTIFIER NOT NULL,
    ResourceId      UNIQUEIDENTIFIER NOT NULL,
    StartsAtUtc     DATETIME2(0)     NOT NULL,
    EndsAtUtc       DATETIME2(0)     NOT NULL,
    Reason          NVARCHAR(300)    NULL,
    CreatedAtUtc    DATETIME2(0)     NOT NULL,
    CreatedByUserId UNIQUEIDENTIFIER NOT NULL,
    UpdatedAtUtc    DATETIME2(0)     NOT NULL,
    UpdatedByUserId UNIQUEIDENTIFIER NULL,
    CONSTRAINT PK_BlackoutPeriods PRIMARY KEY (Id),
    CONSTRAINT FK_BlackoutPeriods_Resources FOREIGN KEY (ResourceId)
        REFERENCES Resources (Id) ON DELETE CASCADE,
    CONSTRAINT FK_BlackoutPeriods_CreatedBy FOREIGN KEY (CreatedByUserId)
        REFERENCES Users (Id),
    CONSTRAINT FK_BlackoutPeriods_UpdatedBy FOREIGN KEY (UpdatedByUserId)
        REFERENCES Users (Id),
    CONSTRAINT CK_BlackoutPeriods_Interval CHECK (EndsAtUtc > StartsAtUtc)
);

/* --- Bookings ------------------------------------------- */

-- FR-5.1 daily/weekly/monthly + interval + end condition
-- CK_EndCondition enforces exactly one of EndDate / OccurrenceCount
-- FR-6.2 TimeZoneId is the zone the rule expands in (DST policy)
CREATE TABLE RecurrenceRules (
    Id              UNIQUEIDENTIFIER NOT NULL,
    ResourceId      UNIQUEIDENTIFIER NOT NULL,
    UserId          UNIQUEIDENTIFIER NOT NULL,
    Frequency       NVARCHAR(10)     NOT NULL,
    IntervalValue   INT              NOT NULL,
    LocalStartTime  TIME(0)          NOT NULL,
    LocalEndTime    TIME(0)          NOT NULL,
    StartDate       DATE             NOT NULL,
    EndDate         DATE             NULL,
    OccurrenceCount INT              NULL,
    TimeZoneId      NVARCHAR(60)     NOT NULL,
    Status          NVARCHAR(20)     NOT NULL,
    CreatedAtUtc    DATETIME2(0)     NOT NULL,
    CreatedByUserId UNIQUEIDENTIFIER NOT NULL,
    UpdatedAtUtc    DATETIME2(0)     NOT NULL,
    UpdatedByUserId UNIQUEIDENTIFIER NULL,
    CONSTRAINT PK_RecurrenceRules PRIMARY KEY (Id),
    CONSTRAINT FK_RecurrenceRules_Resources FOREIGN KEY (ResourceId)
        REFERENCES Resources (Id),
    CONSTRAINT FK_RecurrenceRules_Users FOREIGN KEY (UserId)
        REFERENCES Users (Id),
    CONSTRAINT FK_RecurrenceRules_CreatedBy FOREIGN KEY (CreatedByUserId)
        REFERENCES Users (Id),
    CONSTRAINT FK_RecurrenceRules_UpdatedBy FOREIGN KEY (UpdatedByUserId)
        REFERENCES Users (Id),
    CONSTRAINT CK_RecurrenceRules_Frequency CHECK
        (Frequency IN ('Daily','Weekly','Monthly')),
    CONSTRAINT CK_RecurrenceRules_Interval CHECK (IntervalValue > 0),
    CONSTRAINT CK_RecurrenceRules_Status CHECK (Status IN ('Active','Cancelled')),
    CONSTRAINT CK_RecurrenceRules_EndCondition CHECK
        ((EndDate IS NOT NULL AND OccurrenceCount IS NULL)
         OR (EndDate IS NULL AND OccurrenceCount IS NOT NULL)),
    -- Decision #7 (docs/decisions/0007): a series runs at most two calendar
    -- years past its own StartDate. This only covers the EndDate case exactly;
    -- the OccurrenceCount case gets the equivalent check in the Domain layer
    -- (RecurrenceRule constructor) instead, since expressing "implied span"
    -- for Monthly recurrence isn't a clean single SQL expression across all
    -- three frequencies.
    CONSTRAINT CK_RecurrenceRules_MaxSpan CHECK
        (EndDate IS NULL OR EndDate <= DATEADD(YEAR, 2, StartDate))
);

-- The only table owning a time interval. Occurrences of a series
-- are materialised here (FR-5.2 independently cancellable).
-- Quantity = units consumed; SUM over overlaps <= Resources.Capacity (FR-4.2)
-- CheckedInAtUtc feeds the no-show job (FR-9.1, section 6.4)
CREATE TABLE Bookings (
    Id                 UNIQUEIDENTIFIER NOT NULL,
    OrgId              UNIQUEIDENTIFIER NOT NULL,
    ResourceId         UNIQUEIDENTIFIER NOT NULL,
    UserId             UNIQUEIDENTIFIER NOT NULL,
    RecurrenceRuleId   UNIQUEIDENTIFIER NULL,
    StartsAtUtc        DATETIME2(0)     NOT NULL,
    EndsAtUtc          DATETIME2(0)     NOT NULL,
    Quantity           INT              NOT NULL,
    Title              NVARCHAR(200)    NULL,
    Status             NVARCHAR(20)     NOT NULL,
    CheckedInAtUtc     DATETIME2(0)     NULL,
    CancelledByUserId  UNIQUEIDENTIFIER NULL,
    CancelledAtUtc     DATETIME2(0)     NULL,
    CancellationReason NVARCHAR(300)    NULL,
    CreatedAtUtc       DATETIME2(0)     NOT NULL,
    CreatedByUserId    UNIQUEIDENTIFIER NOT NULL,
    UpdatedAtUtc       DATETIME2(0)     NOT NULL,
    UpdatedByUserId    UNIQUEIDENTIFIER NULL,
    RowVersion         ROWVERSION       NOT NULL,
    CONSTRAINT PK_Bookings PRIMARY KEY (Id),
    -- composite FK: a booking cannot reference another tenant's resource
    CONSTRAINT FK_Bookings_Resources_SameOrg FOREIGN KEY (OrgId, ResourceId)
        REFERENCES Resources (OrgId, Id),
    CONSTRAINT FK_Bookings_Users FOREIGN KEY (UserId)
        REFERENCES Users (Id),
    -- Decision #2 (docs/decisions/0002-tenant-admin-cancellation.md): a
    -- TenantAdmin may cancel another user's booking; CancelledByUserId already
    -- distinguishes "who cancelled" from UpdatedByUserId's generic audit role
    CONSTRAINT FK_Bookings_CancelledBy FOREIGN KEY (CancelledByUserId)
        REFERENCES Users (Id),
    CONSTRAINT FK_Bookings_RecurrenceRules FOREIGN KEY (RecurrenceRuleId)
        REFERENCES RecurrenceRules (Id),
    CONSTRAINT FK_Bookings_CreatedBy FOREIGN KEY (CreatedByUserId)
        REFERENCES Users (Id),
    -- NULL here specifically covers the no-show release job flipping Status
    -- to 'NoShow' with no human actor (Decision #4)
    CONSTRAINT FK_Bookings_UpdatedBy FOREIGN KEY (UpdatedByUserId)
        REFERENCES Users (Id),
    CONSTRAINT CK_Bookings_Interval CHECK (EndsAtUtc > StartsAtUtc),
    CONSTRAINT CK_Bookings_Quantity CHECK (Quantity > 0),
    CONSTRAINT CK_Bookings_Status CHECK
        (Status IN ('Pending','Confirmed','Rejected','Cancelled','Completed','NoShow'))
);

-- FR-7.2 decision record with optional note
-- FR-7.4 ExpiresAtUtc drives the stale-approval job (FR-9.3)
CREATE TABLE ApprovalRequests (
    Id              UNIQUEIDENTIFIER NOT NULL,
    BookingId       UNIQUEIDENTIFIER NOT NULL,
    RequestedAtUtc  DATETIME2(0)     NOT NULL,
    ExpiresAtUtc    DATETIME2(0)     NULL,
    Decision        NVARCHAR(20)     NOT NULL,
    DecidedByUserId UNIQUEIDENTIFIER NULL,
    DecidedAtUtc    DATETIME2(0)     NULL,
    Note            NVARCHAR(500)    NULL,
    CONSTRAINT PK_ApprovalRequests PRIMARY KEY (Id),
    CONSTRAINT UQ_ApprovalRequests_Booking UNIQUE (BookingId),
    CONSTRAINT FK_ApprovalRequests_Bookings FOREIGN KEY (BookingId)
        REFERENCES Bookings (Id),
    CONSTRAINT FK_ApprovalRequests_Users FOREIGN KEY (DecidedByUserId)
        REFERENCES Users (Id),
    CONSTRAINT CK_ApprovalRequests_Decision CHECK
        (Decision IN ('Pending','Approved','Rejected','Expired')),
    CONSTRAINT CK_ApprovalRequests_DecisionPaired CHECK
        ((Decision = 'Pending' AND DecidedByUserId IS NULL AND DecidedAtUtc IS NULL)
         OR (Decision <> 'Pending' AND DecidedAtUtc IS NOT NULL))
);

-- FR-8.1 transactional email; FR-8.4 Attempts/LastError support retry
-- FR-9.4 / AC-6 idempotence: unique (BookingId, RecurrenceRuleId,
-- OccurrenceDate, RecipientUserId, Kind)
-- All notifications are Kind-agnostic email sends (see Decision #2 note).
-- Decision #8 (docs/decisions/0008): BookingId is nullable because a
-- RecurrenceOccurrenceSkipped notification (DST spring-forward gap) has no
-- Booking to reference — that's the whole point, the occurrence was never
-- created. It's anchored to RecurrenceRuleId + OccurrenceDate instead.
-- CK_Notifications_HasContext guarantees every row points at one anchor or
-- the other, never neither.
-- Partial audit trail: CreatedByUserId is nullable because the no-show
-- release job creates 'NoShowReleased' rows with no human actor (the only
-- table where row *creation*, not just update, can be system-initiated).
-- No UpdatedByUserId — every update to this row (Attempts/SentAtUtc/LastError)
-- comes from the dispatch job, never a person, so the column would always be
-- null and add nothing.
CREATE TABLE Notifications (
    Id               UNIQUEIDENTIFIER NOT NULL,
    BookingId        UNIQUEIDENTIFIER NULL,
    RecurrenceRuleId UNIQUEIDENTIFIER NULL,
    OccurrenceDate   DATE             NULL,
    RecipientUserId  UNIQUEIDENTIFIER NOT NULL,
    Kind             NVARCHAR(30)     NOT NULL,
    SendAtUtc        DATETIME2(0)     NOT NULL,
    SentAtUtc        DATETIME2(0)     NULL,
    Attempts         INT              NOT NULL,
    LastError        NVARCHAR(500)    NULL,
    CreatedAtUtc     DATETIME2(0)     NOT NULL,
    CreatedByUserId  UNIQUEIDENTIFIER NULL,
    UpdatedAtUtc     DATETIME2(0)     NOT NULL,
    CONSTRAINT PK_Notifications PRIMARY KEY (Id),
    CONSTRAINT UQ_Notifications_Once UNIQUE
        (BookingId, RecurrenceRuleId, OccurrenceDate, RecipientUserId, Kind),
    CONSTRAINT FK_Notifications_Bookings FOREIGN KEY (BookingId)
        REFERENCES Bookings (Id) ON DELETE CASCADE,
    CONSTRAINT FK_Notifications_RecurrenceRules FOREIGN KEY (RecurrenceRuleId)
        REFERENCES RecurrenceRules (Id),
    CONSTRAINT FK_Notifications_Users FOREIGN KEY (RecipientUserId)
        REFERENCES Users (Id),
    CONSTRAINT FK_Notifications_CreatedBy FOREIGN KEY (CreatedByUserId)
        REFERENCES Users (Id),
    CONSTRAINT CK_Notifications_HasContext CHECK
        (BookingId IS NOT NULL
         OR (RecurrenceRuleId IS NOT NULL AND OccurrenceDate IS NOT NULL)),
    CONSTRAINT CK_Notifications_Kind CHECK
        (Kind IN ('Confirmed','Rejected','Cancelled','Reminder',
                  'ApprovalRequested','NoShowReleased','RecurrenceOccurrenceSkipped'))
);

/* --- Indexes -------------------------------------------- */

-- Load-bearing: gives HOLDLOCK a narrow key range instead of a table lock.
-- Without this the capacity check in dbo.CreateBooking serialises the table.
CREATE INDEX IX_Bookings_Resource_Start
    ON Bookings (ResourceId, StartsAtUtc)
    INCLUDE (EndsAtUtc, Status, Quantity);

CREATE INDEX IX_Bookings_User
    ON Bookings (OrgId, UserId, StartsAtUtc);

CREATE INDEX IX_Bookings_Series
    ON Bookings (RecurrenceRuleId)
    WHERE RecurrenceRuleId IS NOT NULL;

-- FR-9.1 no-show sweep: confirmed bookings past grace with no check-in
CREATE INDEX IX_Bookings_NoShowSweep
    ON Bookings (StartsAtUtc)
    WHERE Status = 'Confirmed' AND CheckedInAtUtc IS NULL;

CREATE UNIQUE INDEX UX_Users_Org_Email
    ON Users (OrgId, Email)
    WHERE OrgId IS NOT NULL;

CREATE INDEX IX_RefreshTokens_Family
    ON RefreshTokens (FamilyId);

CREATE INDEX IX_BlackoutPeriods_Resource_Start
    ON BlackoutPeriods (ResourceId, StartsAtUtc);

-- FR-7.2 approver queue
CREATE INDEX IX_ApprovalRequests_Pending
    ON ApprovalRequests (ExpiresAtUtc)
    WHERE Decision = 'Pending';

-- FR-9.2 worker poll
CREATE INDEX IX_Notifications_Due
    ON Notifications (SendAtUtc)
    WHERE SentAtUtc IS NULL;
