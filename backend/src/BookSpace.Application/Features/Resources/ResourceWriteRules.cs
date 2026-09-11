using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Domain.Entities;

namespace BookSpace.Application.Features.Resources;

// The tier-4 rules (CLAUDE.md §6) that both write handlers enforce, in one
// place so each reason code has exactly one thrower. The corresponding
// invariants that belong to the entity — capacity > 0, non-blank name, coherent
// duration bounds — are in Resource itself; these are the ones it cannot
// judge, because they need the host's timezone database, another aggregate, or
// state the entity is allowed to be in temporarily.
internal static class ResourceWriteRules
{
    // FR-3.1. Validation rather than RuleViolation: the value is simply wrong,
    // and the shape validator could not have known (see ITimeZoneCatalog for
    // why a Windows id is rejected too, even though TimeZoneInfo resolves it).
    public static void EnsureKnownTimeZone(ITimeZoneCatalog timeZones, string timeZoneId)
    {
        if (!timeZones.IsKnownIanaId(timeZoneId))
        {
            throw new InvalidTimeZoneIdException(timeZoneId);
        }
    }

    // FR-3.3: "marked RequiresApproval, with one or more assigned approvers".
    // Enforced here rather than in Resource because the entity would have to
    // throw an ArgumentException, which reaches the client as a 500 with no
    // reason code (see the note above Resource's edit methods).
    //
    // Applied on create as well as edit, so the empty state is never a resting
    // state. That has a temporary consequence worth knowing about: approver
    // assignment is Phase 3 (FR-3.3), so until it lands the only resources that
    // can carry RequiresApproval = true are ones that already have an approver.
    public static void EnsureApproversWhenRequired(bool requiresApproval, int approverCount)
    {
        if (requiresApproval && approverCount == 0)
        {
            throw new ApproversRequiredException();
        }
    }

    // FR-3.3. eligibleUserIds is what IUserRepository.FindEligibleApproverIdsAsync
    // returned for the requested set; anything missing from it failed one of the
    // three eligibility conditions, and which one is deliberately not reported —
    // see ApproverNotEligibleException.
    //
    // A set difference rather than a per-id loop, so one round trip decides the
    // whole payload and the message names every offending id at once. An admin
    // fixing an approver list should not have to discover the ineligible members
    // one request at a time.
    public static void EnsureEveryApproverIsEligible(
        IReadOnlyCollection<Guid> requestedUserIds,
        IReadOnlyCollection<Guid> eligibleUserIds)
    {
        var ineligible = requestedUserIds.Except(eligibleUserIds).ToList();

        if (ineligible.Count > 0)
        {
            throw new ApproverNotEligibleException(ineligible);
        }
    }

    // FR-3.5: archiving preserves the resource and its history. It stays
    // readable (see GetResourceQueryRequestHandler) but accepts no further edits —
    // otherwise "archived" would mean nothing beyond a flag.
    public static void EnsureNotArchived(Resource resource)
    {
        if (resource.IsArchived)
        {
            throw new ResourceArchivedException(resource.Id);
        }
    }

    // A capacity decrease that would leave bookings already on the books over
    // the new limit (wp3-plan's "smaller calls"). peakConcurrentQuantity is the
    // largest number of units committed at any single instant still in the
    // future — see IResourceRepository for how it is computed and why only the
    // future counts.
    //
    // Best-effort by nature, and deliberately so: a booking could be created
    // between this check and the save. CLAUDE.md §4.1 puts the real capacity
    // guarantee in dbo.CreateBooking under a range lock, which is where a
    // guarantee can actually be made; this check exists to stop an admin
    // silently invalidating bookings that already exist, not to be that
    // guarantee. Confirmed and documented explicitly, not merely implied, in
    // docs/decisions/0005-capacity-semantics.md's 2026-09-11 amendment —
    // including why upgrading this to a hard lock was rejected and what a
    // resource left briefly under its own peak actually does downstream
    // (nothing unsafe: every capacity check re-reads Resources.Capacity
    // fresh, so it only ever stops admitting further demand past the new
    // number).
    public static void EnsureCapacityCoversExistingBookings(int newCapacity, int peakConcurrentQuantity)
    {
        if (newCapacity < peakConcurrentQuantity)
        {
            throw new CapacityBelowExistingBookingsException(newCapacity, peakConcurrentQuantity);
        }
    }
}
