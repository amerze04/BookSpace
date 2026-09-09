using BookSpace.Application.Features.Bookings;
using BookSpace.Application.Features.Bookings.GetBooking;
using BookSpace.Application.Features.Bookings.ListBookings;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Resources;

namespace BookSpace.UnitTests.Bookings;

// WP-4 Phase 2a. The shape rules on the two read queries, and in particular the
// two admin-only parameters — the settled answer is ValidationFailed 400 with a
// per-field error (owner's call, 2026-09-08), so these assert the field name as
// well as the failure.
public class BookingReadValidatorTests
{
    private static readonly Guid Caller = Guid.NewGuid();

    private static ListBookingsQueryRequestValidator ListValidator(params Role[] roles) =>
        new(new FixedCurrentUser(Caller, roles));

    private static DateTime Utc(int day) => new(2027, 3, day, 0, 0, 0, DateTimeKind.Utc);

    // ---- The default query is valid for everyone ---------------------------

    [Theory]
    [InlineData(Role.Member)]
    [InlineData(Role.Approver)]
    [InlineData(Role.TenantAdmin)]
    public void AnUnfilteredListIsValidForAnyRole(Role role)
    {
        var result = ListValidator(role).Validate(new ListBookingsQueryRequest());

        Assert.True(result.IsValid);
    }

    // ---- The two admin-only parameters -------------------------------------

    [Fact]
    public void AMemberCannotFilterByUserId()
    {
        var result = ListValidator(Role.Member).Validate(
            new ListBookingsQueryRequest(UserId: Guid.NewGuid()));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ListBookingsQueryRequest.UserId));
    }

    // Approver sits between Member and TenantAdmin, so "non-admin" has to mean
    // every non-admin — the same reasoning WP-3's non-admin tests use.
    [Fact]
    public void AnApproverCannotFilterByUserId()
    {
        var result = ListValidator(Role.Approver, Role.Member).Validate(
            new ListBookingsQueryRequest(UserId: Guid.NewGuid()));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void AMemberCannotRequestTheTenantScope()
    {
        var result = ListValidator(Role.Member).Validate(
            new ListBookingsQueryRequest(Scope: BookingScope.Tenant));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ListBookingsQueryRequest.Scope));
    }

    // WP-5 Phase 3, decision 0018's approver queue: an Approver may also
    // request scope=tenant, resource-restricted by the handler rather than
    // ignored by the validator.
    [Fact]
    public void AnApproverCanRequestTheTenantScope()
    {
        var result = ListValidator(Role.Approver).Validate(
            new ListBookingsQueryRequest(Scope: BookingScope.Tenant));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void AnAdminCanFilterByUserId()
    {
        var result = ListValidator(Role.TenantAdmin).Validate(
            new ListBookingsQueryRequest(UserId: Guid.NewGuid()));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void AnAdminCanRequestTheTenantScope()
    {
        var result = ListValidator(Role.TenantAdmin).Validate(
            new ListBookingsQueryRequest(Scope: BookingScope.Tenant));

        Assert.True(result.IsValid);
    }

    // Refused rather than given a precedence rule: together, one of the two has
    // to be ignored, and an accepted-then-ignored parameter is exactly what the
    // 400 exists to avoid. Refused even for an admin, who is the only caller who
    // could send both.
    [Fact]
    public void UserIdAndTheTenantScopeCannotBeCombined()
    {
        var result = ListValidator(Role.TenantAdmin).Validate(
            new ListBookingsQueryRequest(UserId: Guid.NewGuid(), Scope: BookingScope.Tenant));

        Assert.False(result.IsValid);
    }

    // Scope.Own with a userId is not a combination — Own is the absence of a
    // widening, so it composes with the narrowing rather than contradicting it.
    [Fact]
    public void UserIdWithTheDefaultScopeIsFine()
    {
        var result = ListValidator(Role.TenantAdmin).Validate(
            new ListBookingsQueryRequest(UserId: Guid.NewGuid(), Scope: BookingScope.Own));

        Assert.True(result.IsValid);
    }

    // ---- The range filter --------------------------------------------------

    [Fact]
    public void AnInvertedRangeIsRefused()
    {
        var result = ListValidator(Role.Member).Validate(
            new ListBookingsQueryRequest(From: Utc(8), To: Utc(1)));

        Assert.False(result.IsValid);
    }

    // Zero-width overlaps nothing, so it is never what the caller meant.
    [Fact]
    public void AZeroWidthRangeIsRefused()
    {
        var result = ListValidator(Role.Member).Validate(
            new ListBookingsQueryRequest(From: Utc(1), To: Utc(1)));

        Assert.False(result.IsValid);
    }

    // Either bound alone is an open-ended range and cannot be inverted.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OneOpenEndedBoundIsFine(bool hasFrom, bool hasTo)
    {
        var result = ListValidator(Role.Member).Validate(
            new ListBookingsQueryRequest(
                From: hasFrom ? Utc(1) : null,
                To: hasTo ? Utc(8) : null));

        Assert.True(result.IsValid);
    }

    // An instant with no zone can only be read by guessing one, and a silently
    // shifted filter window is a wrong answer with no error — the same rule the
    // blackout list filter applies.
    [Fact]
    public void AZonelessFromIsRefused()
    {
        var result = ListValidator(Role.Member).Validate(
            new ListBookingsQueryRequest(From: new DateTime(2027, 3, 1, 0, 0, 0)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ListBookingsQueryRequest.From));
    }

    [Fact]
    public void AZonelessToIsRefused()
    {
        var result = ListValidator(Role.Member).Validate(
            new ListBookingsQueryRequest(To: new DateTime(2027, 3, 8, 0, 0, 0)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ListBookingsQueryRequest.To));
    }

    // ---- Status and scope have to be defined members -----------------------

    // Enum.TryParse accepts any integer, so `?status=99` binds to an undefined
    // BookingStatus that would match no row and read as "you have no bookings".
    [Fact]
    public void AnUndefinedStatusIsRefused()
    {
        var result = ListValidator(Role.Member).Validate(
            new ListBookingsQueryRequest(Status: (BookingStatus)99));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void AnUndefinedScopeIsRefused()
    {
        var result = ListValidator(Role.TenantAdmin).Validate(
            new ListBookingsQueryRequest(Scope: (BookingScope)99));

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.NoShow)]
    public void EveryRealStatusIsAValidFilter(BookingStatus status)
    {
        var result = ListValidator(Role.Member).Validate(
            new ListBookingsQueryRequest(Status: status));

        Assert.True(result.IsValid);
    }

    // ---- Paging and sorting come from the shared rules ---------------------

    [Theory]
    [InlineData("startsAtUtc")]
    [InlineData("-startsAtUtc")]
    [InlineData("createdAtUtc")]
    [InlineData("status")]
    public void EveryWhitelistedSortIsAccepted(string sort)
    {
        Assert.True(ListValidator(Role.Member).Validate(
            new ListBookingsQueryRequest(Sort: sort)).IsValid);
    }

    // Guards the whitelist against quietly growing: userId is a column, is not
    // sortable, and sorting by an opaque id orders nothing a reader can see.
    [Theory]
    [InlineData("userId")]
    [InlineData("quantity")]
    [InlineData("title")]
    [InlineData("resourceName")]
    public void ASortOutsideTheWhitelistIsRefused(string sort)
    {
        Assert.False(ListValidator(Role.Member).Validate(
            new ListBookingsQueryRequest(Sort: sort)).IsValid);
    }

    // Rejected rather than clamped, per decision 0015 — the same treatment an
    // oversized pageSize gets on every other list endpoint.
    [Fact]
    public void AnOversizedPageSizeIsRefused()
    {
        Assert.False(ListValidator(Role.Member).Validate(
            new ListBookingsQueryRequest(PageSize: 101)).IsValid);
    }

    [Fact]
    public void APageBelowOneIsRefused()
    {
        Assert.False(ListValidator(Role.Member).Validate(
            new ListBookingsQueryRequest(Page: 0)).IsValid);
    }

    // ---- GET /bookings/{id} ------------------------------------------------

    // The route constraint catches a malformed id, but all-zeros parses fine and
    // would otherwise reach the database as a lookup that can only miss.
    [Fact]
    public void AnEmptyBookingIdIsRefused()
    {
        var result = new GetBookingQueryRequestValidator().Validate(
            new GetBookingQueryRequest(Guid.Empty));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ARealBookingIdIsAccepted()
    {
        var result = new GetBookingQueryRequestValidator().Validate(
            new GetBookingQueryRequest(Guid.NewGuid()));

        Assert.True(result.IsValid);
    }
}
