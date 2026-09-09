using BookSpace.Application.Abstractions;
using BookSpace.Application.Features.Bookings;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence.Repositories;

// WP-5 Phase 1b/2. A plain EF add and a tracked read — see
// IRecurrenceRuleRepository for why this carries none of BookingRepository's
// §4.1 machinery.
internal sealed class RecurrenceRuleRepository : IRecurrenceRuleRepository
{
    private readonly BookSpaceDbContext _context;

    public RecurrenceRuleRepository(BookSpaceDbContext context)
    {
        _context = context;
    }

    public void Add(RecurrenceRule rule) => _context.RecurrenceRules.Add(rule);

    public void Remove(RecurrenceRule rule) => _context.RecurrenceRules.Remove(rule);

    // Tracked, not AsNoTracking: the caller mutates it through
    // RecurrenceRule.Cancel and the change has to be saved. FirstOrDefaultAsync,
    // never Find() — Find can answer from the change tracker without querying,
    // which would skip both the tenant query filter (decision 0025) and the
    // owner filter here.
    public Task<RecurrenceRule?> FindForCancellationAsync(
        Guid recurrenceRuleId,
        BookingOwnerFilter owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var rules = _context.RecurrenceRules.Where(r => r.Id == recurrenceRuleId);

        if (owner.UserId is { } userId)
        {
            rules = rules.Where(r => r.UserId == userId);
        }

        return rules.FirstOrDefaultAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _context.SaveChangesAsync(cancellationToken);
}
