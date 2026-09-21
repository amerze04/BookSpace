import { BookingSummary } from '../../booking/models/booking.models';
import { toQueueRow, waitingLabel } from '../queue/approval-queue';

// Built from local components rather than from a "…Z" literal wherever the
// assertion is about what a viewer reads: CI runs in UTC and local development
// here is CET, so a literal would make the expectation environment-dependent.
// The waiting-label tests are the exception — they are pure elapsed-time
// arithmetic, which no zone affects.
function localInstant(
  year: number,
  monthIndex: number,
  day: number,
  hour: number,
  minute: number,
): string {
  return new Date(year, monthIndex, day, hour, minute).toISOString();
}

function booking(overrides: Partial<BookingSummary> = {}): BookingSummary {
  return {
    id: 'b1',
    resourceId: 'r1',
    resourceName: '3D Printer',
    userId: 'u1',
    userName: 'Member One',
    recurrenceRuleId: null,
    startsAtUtc: localInstant(2026, 10, 16, 9, 0),
    endsAtUtc: localInstant(2026, 10, 16, 10, 30),
    quantity: 1,
    title: null,
    status: 'Pending',
    createdAtUtc: localInstant(2026, 8, 20, 8, 0),
    ...overrides,
  };
}

const VIEWER_ZONE = Intl.DateTimeFormat().resolvedOptions().timeZone;

describe('waitingLabel', () => {
  const created = '2026-09-20T08:00:00Z';
  const createdMs = Date.parse(created);

  it('reads "Just now" under a minute', () => {
    expect(waitingLabel(created, createdMs + 59_000)).toBe('Just now');
  });

  it('counts whole minutes under an hour', () => {
    expect(waitingLabel(created, createdMs + 60_000)).toBe('Waiting 1 minute');
    expect(waitingLabel(created, createdMs + 45 * 60_000)).toBe('Waiting 45 minutes');
  });

  it('counts whole hours under a day', () => {
    expect(waitingLabel(created, createdMs + 60 * 60_000)).toBe('Waiting 1 hour');
    expect(waitingLabel(created, createdMs + 23 * 60 * 60_000)).toBe('Waiting 23 hours');
  });

  it('counts whole days beyond that', () => {
    expect(waitingLabel(created, createdMs + 24 * 60 * 60_000)).toBe('Waiting 1 day');
    expect(waitingLabel(created, createdMs + 9 * 24 * 60 * 60_000)).toBe('Waiting 9 days');
  });

  // The server's clock and the browser's are not the same clock, so a request
  // created seconds ago can arrive stamped slightly ahead of Date.now(). The
  // honest reading is "Just now"; "Waiting -1 minutes" is the kind of visible
  // nonsense that makes a reader distrust the rest of the row.
  it('never reports negative time when the stamp is ahead of the clock', () => {
    expect(waitingLabel(created, createdMs - 30_000)).toBe('Just now');
    expect(waitingLabel(created, createdMs - 5 * 60 * 60_000)).toBe('Just now');
  });

  it('does not throw on an unparseable stamp', () => {
    expect(waitingLabel('not-a-date', Date.now())).toBe('Just now');
  });
});

describe('toQueueRow', () => {
  it('carries the four facts an approver decides on', () => {
    const row = toQueueRow(
      booking({ resourceName: 'Conference Room A', userName: 'Member Two', quantity: 3 }),
      VIEWER_ZONE,
      Date.parse(localInstant(2026, 8, 21, 8, 0)),
    );

    expect(row.resourceName).toBe('Conference Room A');
    expect(row.requesterName).toBe('Member Two');
    expect(row.span.timeRange).toContain('09:00');
    expect(row.quantityLabel).toBe('3 units');
  });

  // Singular, because "1 units" on every exclusive resource's row is the kind
  // of small wrongness that is read hundreds of times.
  it('says "1 unit", not "1 units"', () => {
    expect(toQueueRow(booking({ quantity: 1 }), VIEWER_ZONE, Date.now()).quantityLabel).toBe('1 unit');
  });

  // FR-5.2 and decision 0007: a series is N independent booking rows sharing a
  // rule id, so the marker is the only thing telling an approver that five asks
  // for the same room are one member's series.
  it('marks an occurrence of a series, and leaves a one-off unmarked', () => {
    expect(toQueueRow(booking({ recurrenceRuleId: 'rule-1' }), VIEWER_ZONE, Date.now()).isRecurring).toBe(
      true,
    );
    expect(toQueueRow(booking({ recurrenceRuleId: null }), VIEWER_ZONE, Date.now()).isRecurring).toBe(
      false,
    );
  });

  // Both readings are rendered side by side: the relative one answers the
  // question at a glance and the absolute one is the one a reader can check.
  // Asserted together so neither can quietly disappear.
  it('gives the requested-at stamp both an absolute and a relative reading', () => {
    const createdAtUtc = localInstant(2026, 8, 18, 9, 30);
    const row = toQueueRow(
      booking({ createdAtUtc }),
      VIEWER_ZONE,
      Date.parse(createdAtUtc) + 3 * 24 * 60 * 60_000,
    );

    expect(row.waitingLabel).toBe('Waiting 3 days');
    expect(row.requestedAtLabel).toContain('Sep 18, 2026');
    expect(row.requestedAtLabel).toContain('09:30');
  });

  // The span reads in the viewer's zone rather than the resource's — an
  // approver is judging a request against their own day, not choosing a slot.
  // An overnight booking therefore has to carry the end date, which is
  // spanLabels' own rule; asserted here because the queue is now a second
  // caller relying on it.
  it('names the end date when a booking runs past midnight', () => {
    const row = toQueueRow(
      booking({
        startsAtUtc: localInstant(2026, 10, 16, 22, 0),
        endsAtUtc: localInstant(2026, 10, 17, 2, 0),
      }),
      VIEWER_ZONE,
      Date.now(),
    );

    expect(row.span.timeRange).toContain('22:00');
    expect(row.span.timeRange).toContain('Nov 17, 2026');
  });
});
