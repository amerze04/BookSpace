using BookSpace.Application.Abstractions;
using BookSpace.Application.Features.Bookings;
using BookSpace.Domain.Entities;
using Microsoft.Data.SqlClient;
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

    // ---- Idempotent creation (hardening pass, item 11) ---------------------

    public Task<RecurrenceCreationOperation?> FindOperationAsync(
        Guid orgId, Guid userId, string idempotencyKey, CancellationToken cancellationToken) =>
        _context.RecurrenceCreationOperations.FirstOrDefaultAsync(
            o => o.OrgId == orgId && o.UserId == userId && o.IdempotencyKey == idempotencyKey,
            cancellationToken);

    public void AddOperation(RecurrenceCreationOperation operation) =>
        _context.RecurrenceCreationOperations.Add(operation);

    // Bug fix, item 11's own found gap: an Added entity that never made it to
    // the database — SaveChanges hasn't been called on it yet, or (below)
    // just failed on it — so Remove() only detaches it from the change
    // tracker rather than issuing a DELETE, the same behaviour Remove(rule)
    // already relies on elsewhere in this pass.
    public void RemoveOperation(RecurrenceCreationOperation operation) =>
        _context.RecurrenceCreationOperations.Remove(operation);

    public Task<RecurrenceRule?> FindByIdAsync(Guid recurrenceRuleId, CancellationToken cancellationToken) =>
        _context.RecurrenceRules.FirstOrDefaultAsync(r => r.Id == recurrenceRuleId, cancellationToken);

    // Bug fix, item 11's own found gap: two literally-simultaneous first-time
    // requests for the same (OrgId, UserId, IdempotencyKey) both pass
    // FindOperationAsync's "not seen before" check and both try to insert —
    // UQ_RecurrenceCreationOperations_Org_User_Key lets exactly one through.
    // False for the loser rather than letting the SqlException surface: the
    // caller's response to losing (detach its own attempt, re-read the
    // winner's) is an ordinary idempotency resume, not an error condition.
    // The SqlException stays in Infrastructure rather than crossing into the
    // handler, on the same reasoning IBookingRepository's own header gives
    // for keeping ADO specifics out of Application.
    //
    // Error 2601 is a duplicate key on a unique *index* (what
    // migrationBuilder.CreateIndex(..., unique: true) creates); 2627 is the
    // equivalent for a PRIMARY KEY or a named UNIQUE CONSTRAINT. This table's
    // constraint is the index form, so 2601 is what actually fires — 2627 is
    // included because nothing prevents a future migration from changing the
    // enforcement mechanism without anyone thinking to update this check.
    public async Task<bool> TrySaveNewOperationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            return false;
        }
    }

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
