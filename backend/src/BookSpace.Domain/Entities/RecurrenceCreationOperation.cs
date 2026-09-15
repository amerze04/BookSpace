using BookSpace.Domain.Common;
using BookSpace.Domain.Enums;

namespace BookSpace.Domain.Entities;

// Hardening pass, item 11: FR-5.1's series creation was not
// request-idempotent or crash-resumable — a process crash after some
// occurrences committed left a partial series, and retrying the identical
// POST minted a brand-new RecurrenceRule and re-attempted every occurrence
// from scratch, which on a pooled resource could double-book an occurrence
// that had already succeeded (a fresh random booking id never collides with
// the one already committed, so BookingRepository's existing PK-violation
// read-back — the mechanism that already makes a single booking's own retry
// safe — never had a reason to fire).
//
// This row is the "lightweight operation lifecycle" that closes that gap,
// keyed by a client-supplied Idempotency-Key header (decisions/0007's
// "best-effort per occurrence" is unchanged — this does not make the series
// atomic, it makes *retrying* it safe). One row per (OrgId, UserId,
// IdempotencyKey): the same client, the same logical request, retried any
// number of times, always resolves to the same RecurrenceRule.
//
// **The other half of the mechanism is CreateRecurrenceSeriesCommandRequestHandler
// deriving each occurrence's booking id deterministically from
// (RecurrenceRuleId, OccurrenceDate)**, instead of a fresh random Guid. That
// single change is what makes "resume" and "replay" need no special-case
// logic in the occurrence loop at all: retrying against the *same* rule id
// re-computes the *same* booking id for an occurrence already committed, so
// dbo.CreateBooking's own PK violation — already caught and read back,
// unchanged — reports it Created again rather than inserting a duplicate.
// This entity's only job is remembering *which* rule id a retry should reuse.
//
// A lighter design than a full response-snapshot cache on purpose (the
// review's own "lightweight" framing): a replay of an Active operation
// re-runs the whole handler rather than returning a stored payload, so it
// re-validates today's preconditions (resource not archived, still within
// its duration limits) rather than guaranteeing a byte-identical response no
// matter what changed in between. Accepted: achieving that guarantee needs
// the heavier design, and nothing about this system's other idempotency
// points (decisions/0011's refresh rotation, the Notifications uniqueness
// constraint) promises it either.
public class RecurrenceCreationOperation : IAuditable, ITenantOwned
{
    public Guid Id { get; private set; }
    public Guid OrgId { get; private set; }
    public Guid UserId { get; private set; }
    public string IdempotencyKey { get; private set; }
    public RecurrenceCreationOperationStatus Status { get; private set; }
    public Guid? RecurrenceRuleId { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public Guid? UpdatedByUserId { get; private set; }

    // Explicit, same technique Resource/Booking use: OrgId is never actually
    // null for this entity, but ITenantOwned's contract has to admit a
    // SysAdmin's null Users.OrgId, so the interface's own shape is Guid?.
    Guid? ITenantOwned.OrgId => OrgId;

    // EF Core materialization only.
    private RecurrenceCreationOperation()
    {
        IdempotencyKey = string.Empty;
    }

    public RecurrenceCreationOperation(
        Guid id, Guid orgId, Guid userId, string idempotencyKey, Guid recurrenceRuleId, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        Id = id;
        OrgId = orgId;
        UserId = userId;
        IdempotencyKey = idempotencyKey;
        RecurrenceRuleId = recurrenceRuleId;
        Status = RecurrenceCreationOperationStatus.Creating;
        CreatedAtUtc = nowUtc;
        CreatedByUserId = userId;
        UpdatedAtUtc = nowUtc;
        UpdatedByUserId = userId;
    }

    // At least one occurrence was created — the series is real. Idempotent
    // to call again on a replay that (re-)confirms the same thing.
    public void MarkActive(DateTime nowUtc)
    {
        Status = RecurrenceCreationOperationStatus.Active;
        UpdatedAtUtc = nowUtc;
    }

    // Nothing was reserved and the caller has already compensating-deleted
    // the RecurrenceRule this row named — so this stops naming it, since the
    // row no longer exists to resume into.
    public void MarkFailed(DateTime nowUtc)
    {
        Status = RecurrenceCreationOperationStatus.Failed;
        RecurrenceRuleId = null;
        UpdatedAtUtc = nowUtc;
    }

    // Points this key at a different rule than the one it currently names,
    // and resets to Creating. Two callers: a retry of a Failed operation
    // (nothing survived the last attempt, so this starts over exactly as a
    // first attempt would) and a key reused with a different ResourceId than
    // the rule it already names (bug fix — a client or caller error, not a
    // resume; the old rule is left exactly as it was, real bookings and all,
    // just no longer reachable through *this* key).
    public void RestartWith(Guid recurrenceRuleId, DateTime nowUtc)
    {
        RecurrenceRuleId = recurrenceRuleId;
        Status = RecurrenceCreationOperationStatus.Creating;
        UpdatedAtUtc = nowUtc;
    }
}
