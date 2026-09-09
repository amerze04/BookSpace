using BookSpace.Domain.Entities;

namespace BookSpace.Application.Abstractions;

// The write port for recurrence rules (WP-5 Phase 1b). Declared here,
// implemented in BookSpace.Infrastructure, like every other port.
//
// Unlike IBookingRepository, this one embodies no §4.1 constraint: a
// RecurrenceRule carries no capacity claim of its own — it is
// Bookings.RecurrenceRuleId that an occurrence's row points back at, and each
// occurrence's capacity guarantee still goes through dbo.CreateBooking exactly
// as a one-off booking's does. So this is a plain EF add, on the same footing
// as AddApprovalRequest / AddNotifications on IBookingRepository.
public interface IRecurrenceRuleRepository
{
    void Add(RecurrenceRule rule);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
