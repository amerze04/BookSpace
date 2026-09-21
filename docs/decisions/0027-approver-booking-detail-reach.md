# 0027 — An approver's reach on a booking detail read

## Status
Decided by the owner and implemented 2026-09-21, while building WP-7 Phase 6.
Found by the owner clicking a queue row, not by review or by the test suite —
the sixth bug in this work package found that way and the first found in the
backend.

## Context
WP-5 Phase 3 widened `GET /bookings` so an **Approver**, not only a
TenantAdmin, may ask for `scope=tenant` and see the bookings on the resources
they are assigned to approve (decision `0018`). That widening lives in
`BookingReadRules.ResolveOwnerFilter`, which resolves a `BookingOwnerFilter`
the repository turns into a `WHERE` clause.

`GET /bookings/{id}` resolves its filter through a **different** method in the
same file, `ResolveDetailFilter`, and that one was never widened. Its whole
body was:

```csharp
isTenantAdmin ? BookingOwnerFilter.AnyOwner : BookingOwnerFilter.Owner(callerUserId);
```

An Approver is not a TenantAdmin, so the filter was `Owner(callerUserId)` and
another member's booking simply did not come back — a 404 `BookingNotFound`,
deliberately indistinguishable from "no such booking" (decision `0002`,
`BookingNotFoundException`).

Nothing exercised the inconsistency until WP-7 Phase 6 step 3 put an approval
queue on screen whose every row links to `/bookings/{id}`. The result was an
approver who could **see** a request in their queue, and was **allowed to
decide** on it, but could not **open** it:

| Endpoint | An Approver's reach before this decision |
|---|---|
| `GET /bookings?scope=tenant` | Bookings on resources they gate |
| `POST /bookings/{id}/approve` | Bookings on resources they gate (`ApprovalReach`) |
| `POST /bookings/{id}/reject` | Bookings on resources they gate (`ApprovalReach`) |
| `GET /bookings/{id}` | **Their own only** |

Three of the four agreed. The fourth is the one that answers "what am I being
asked to approve", which is the question an approver most needs answered
before deciding.

## Decision
**An Approver may read a booking by id when it is their own *or* when it is on
a resource they are assigned to approve.** `ResolveDetailFilter` takes the same
`approverResourceIds` the list handler already resolves, and
`GetBookingQueryRequestHandler` resolves it through the same
`FindApprovableResourceIdsAsync` call, gated the same way — only when the
caller is a non-admin holding `Approver`, since an admin already sees
everything and a plain member has no reach to resolve.

There is deliberately **no scope parameter** on the detail read. A read of one
booking has no "how wide" question for a client to answer and nothing for a
validator to refuse, which is why an Approver's reach applies unconditionally
here while the list requires `scope=tenant` to be asked for.

### The union, which is the part that is easy to get wrong
The reach is **their own OR on a gated resource**, never the intersection.

`BookingOwnerFilter` already carried both a `UserId` and a `ResourceIds`
restriction, and the list applies them with **AND** — `AnyOwnerRestrictedToResources`
drops the owner restriction entirely, so "any owner, on my resources" is
exactly the queue's question. Reusing that factory here looked right and was
wrong: it would have taken away an Approver's ability to read **their own**
bookings on resources they do **not** gate, which every plain member can do.

So the filter gained an explicit combinator, `Combination.All` / `Any`, and a
new factory `OwnerOrResources` that asks for the union by name. The two reads
now differ on purpose and say so:

- **List** — AND. An Approver asking `scope=tenant` is asking what is booked on
  the resources they gate; their own booking elsewhere is not part of that
  question, and `scope=own` (the default) already answers it.
- **Detail** — OR. One request, two reasons it might be visible.

An Approver assigned to nothing collapses to the plain member's filter rather
than carrying an empty set: "mine, or one of no resources" is exactly "mine".

### What did not widen
**The cancel.** Decision `0002` gives a cancel to the booking's owner and to a
TenantAdmin. An Approver's resource reach widens what they may *read*, never
what they may cancel or otherwise write. `FindForCancelAsync` therefore
deliberately does **not** go through the shared owner-filter helper, and says
so in a comment, because routing it through would silently hand an Approver the
ability to cancel any booking on a resource they gate.

## Consequences

### Accepted
An approver can now read **every** booking on a resource they gate — not only
the pending ones, but confirmed, completed, cancelled and past bookings of
other members, including each one's title and who made it. That is a real
widening of visibility and the owner accepted it on 2026-09-21.

The alternative considered was restricting the widened read to `Pending`
bookings only. It was rejected because the link would then break at the worst
moment: an approver approves a request, is sent to the booking, and gets a 404
because it is now `Confirmed`. A reach that changes under the approver's own
action is harder to explain than a slightly wider one, and "you may see what is
booked on the resources you are responsible for" is a defensible rule in its
own right rather than an accident of what a screen needed.

The reach is still by **resource**, never by role: an Approver assigned to
nothing sees exactly what a member sees, and a resource they do not gate stays
invisible. `Get_RefusesAnotherMembersBookingToAnApprover` pins that and still
passes unchanged.

### A latent fail-open closed on the way
`BookingRepository.FindDetailAsync` applied `owner.UserId` and **silently
ignored `owner.ResourceIds`**, which had been on the filter since WP-5 Phase 3.
Nothing had ever handed that method a resource-restricted filter, so nothing
leaked — but the first caller to do so would have got *any* booking in the
tenant back, which is precisely the fail-open shape `BookingOwnerFilter`'s own
header exists to rule out. This decision's implementation would have been that
first caller.

Both reads now go through one private `ApplyOwnerFilter` helper, so a future
field on the filter cannot be honoured by one read and dropped by the other.

### Cost
One extra query on `GET /bookings/{id}` for a caller who holds `Approver` and
is not a TenantAdmin — `FindApprovableResourceIdsAsync`, the same call the list
handler already makes, and not made at all for a member or an admin.

## Alternatives rejected

- **Pending-only reach.** See above — the link breaks the moment the approver
  acts.
- **Drop the link from the queue and inline everything an approver needs.** No
  backend change, but an approver would decide without access to the booking's
  full record, including the approval history and any note a previous decision
  left. It also leaves the two reads disagreeing, which would surface again the
  next time anything links to a booking.
- **Leave it, record it as a gap for a later backend package** (the treatment
  `POST /bookings`' missing idempotency key got). Rejected because that gap
  degrades a recovery path, while this one leaves a shipped screen's primary
  link answering 404.

## References
- `docs/decisions/0002-tenant-admin-cancellation.md` — the reach this narrows against
- `docs/decisions/0018-approver-eligibility.md` — who may approve, and the resource set this reuses
- `BookSpace.Application/Features/Bookings/BookingReadRules.cs`
- `BookSpace.Application/Features/Bookings/BookingOwnerFilter.cs`
- `BookSpace.Infrastructure/Persistence/Repositories/BookingRepository.cs` — `ApplyOwnerFilter`
- `docs/wp7-plan.md` — WP-7 Phase 6
