using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.BlackoutPeriods.CreateBlackoutPeriod;

// FR-3.4, TenantAdmin only. POST /resources/{id}/blackout-periods.
//
// A plain create, unlike Phase 3's two replace-the-set endpoints, and the
// difference is in the data rather than in taste. A weekly schedule and an
// approver list are *sets* an admin holds an opinion about as a whole; blackouts
// are individual events ("boiler service on the 14th") that accumulate
// independently and each carry their own audit columns. Replacing the set would
// mean an admin adding one blackout had to resend every other one, and decision
// 0001's cascade makes resending a blackout anything but free.
//
// No OrgId and no CreatedByUserId: both come from the token, via ICurrentTenant
// and ICurrentUser. ResourceId comes from the route.
//
// Overlapping blackouts on one resource are allowed (owner's call, 2026-09-02),
// deliberately unlike overlapping availability windows: the union of two
// blackouts is still blacked out, so there is nothing to disambiguate, whereas
// two overlapping windows genuinely contradict each other about when a resource
// opens.
public sealed record CreateBlackoutPeriodCommandRequest(
    Guid ResourceId,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    string? Reason)
    : IRequest<CreateBlackoutPeriodCommandResponse>, IBlackoutPeriodWriteCommand;
