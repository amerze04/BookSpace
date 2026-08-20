# 0005 — Capacity: concurrent bookings or seats within one booking

## Status
Decided — formalises what the schema already implemented

## Context
Added during schema design, not from PRD §13 directly. FR-4.2 and FR-4.3 can
be read either as "Capacity = number of simultaneous bookings a resource
allows" or "Capacity = seats available within a single exclusive booking."

## Decision
`Resources.Capacity` means **concurrent units** the resource supports at
any instant. `Bookings.Quantity` is the number of units a given booking
consumes, and `dbo.CreateBooking` / `dbo.ApproveBooking` sum `Quantity`
across all temporally-overlapping `Confirmed`/`Pending` bookings for a
resource and reject if the sum would exceed `Resources.Capacity`
(`docs/bookspace-schema-v2.sql`, `CK_Bookings_Quantity`, and the
tier-2 locking protocol in CLAUDE.md §6). This is not "seats within one
exclusive booking" — a single resource can host multiple simultaneous,
independent bookings as long as their summed `Quantity` stays within
`Capacity`.

## Consequences
- No schema change — this decision only documents what
  `Resources.Capacity` + `Bookings.Quantity` + `CK_Resources_Capacity` /
  `CK_Bookings_Quantity` already implement.
- Domain-level `Booking` and `Resource` entities should carry this
  interpretation in their doc comments so future readers don't re-litigate
  it from the property names alone.
