using BookSpace.Application.Abstractions;
using BookSpace.Domain.Entities;

namespace BookSpace.Infrastructure.Persistence.Repositories;

// WP-5 Phase 1b. A plain EF add — see IRecurrenceRuleRepository for why this
// carries none of BookingRepository's §4.1 machinery.
internal sealed class RecurrenceRuleRepository : IRecurrenceRuleRepository
{
    private readonly BookSpaceDbContext _context;

    public RecurrenceRuleRepository(BookSpaceDbContext context)
    {
        _context = context;
    }

    public void Add(RecurrenceRule rule) => _context.RecurrenceRules.Add(rule);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _context.SaveChangesAsync(cancellationToken);
}
