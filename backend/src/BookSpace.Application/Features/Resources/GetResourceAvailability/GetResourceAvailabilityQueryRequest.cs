using BookSpace.Application.Messaging;
using BookSpace.Domain.Availability;

namespace BookSpace.Application.Features.Resources.GetResourceAvailability;

// GET /resources/{id}/availability, on TenantMember. WP-3 Phase 5.
//
// The query has no FR of its own — it is a work-package task item. It serves the
// read side of FR-4.2 (a booking is rejected if it falls outside availability,
// inside a blackout, or exceeds capacity) and FR-6.3 (a resource's availability
// timezone is applied consistently), and the PRD's member flow is its shape:
// "selects a resource and date -> sees live availability -> picks a slot".
//
// **The range is resource-local dates, not UTC instants** (owner's call,
// 2026-09-03). Availability is expressed in the resource's timezone
// (docs/decisions/0003-availability-timezone.md) and the PRD flow says "selects a
// resource and date", so a local date is what a member actually picks. A pair of
// instants would only force the server to work out which local days they touch.
//
// Both dates are required, with no defaults. "Today" cannot be defaulted here
// without knowing the resource's zone, which is not known until the resource is
// loaded — and a client that wants a single day can send the same date twice,
// which is clearer than a default it has to guess the meaning of.
// Quantity is how many units the caller wants to hold at once, and it defaults
// to one. It exists because "how long can I book" has no single answer on a
// pooled resource: the longest run with one unit free is longer than the run
// with four. Without it the endpoint could only answer with the finest partition
// and leave the client to join the pieces — and the minimum-duration filter,
// measuring those pieces, then hid time a booking would have been accepted for.
//
// A quantity above the resource's Capacity is not an error: it is answered with
// an empty list, which is true. The validator cannot know the capacity without
// loading the resource, and "nothing that size fits" is a real answer rather
// than a malformed request.
public sealed record GetResourceAvailabilityQueryRequest(
    Guid ResourceId,
    DateOnly FromLocalDate,
    DateOnly ToLocalDate,
    int Quantity = AvailabilityCalculator.DefaultRequiredQuantity)
    : IRequest<GetResourceAvailabilityQueryResponse>;
