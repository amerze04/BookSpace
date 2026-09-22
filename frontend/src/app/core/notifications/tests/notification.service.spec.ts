import { TestBed } from '@angular/core/testing';
import { NotificationService } from '../notification.service';

// WP-7 Phase 7 step 2. The only service in the app with no spec of its own —
// its *component* had one, which proves the list renders what the service holds
// but says nothing about what the service decides to hold.
//
// Small, but not trivial: it owns an auto-dismiss timer and an id sequence, and
// both have a failure mode worth pinning. A dismiss that matched by index
// instead of id would remove the wrong toast whenever two were open; a timer
// that fired after a manual dismiss would remove a *later* toast that happened
// to reuse the id.
describe('NotificationService', () => {
  let service: NotificationService;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({});
    service = TestBed.inject(NotificationService);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('starts with nothing to show', () => {
    expect(service.notifications()).toEqual([]);
  });

  // Error is the default because this service exists for the failures a screen
  // did *not* give local feedback for (CLAUDE.md §6's "a rejection is never
  // silent", client side). An info toast has to be asked for.
  it('defaults to an error, and takes info when asked', () => {
    service.show('Something went wrong');
    service.show('Saved', 'info');

    expect(service.notifications().map((n) => n.kind)).toEqual(['error', 'info']);
  });

  it('keeps several at once, in the order they arrived', () => {
    service.show('first');
    service.show('second');

    expect(service.notifications().map((n) => n.message)).toEqual(['first', 'second']);
  });

  // The failure this rules out: dismissing by position rather than by id would
  // take the wrong toast off the screen the moment two are open.
  it('dismisses the one named, not the one in that position', () => {
    service.show('first');
    service.show('second');
    service.show('third');

    const second = service.notifications()[1];
    service.dismiss(second.id);

    expect(service.notifications().map((n) => n.message)).toEqual(['first', 'third']);
  });

  it('auto-dismisses after its own timeout, and not before', () => {
    service.show('goes away on its own');

    vi.advanceTimersByTime(5999);
    expect(service.notifications()).toHaveLength(1);

    vi.advanceTimersByTime(1);
    expect(service.notifications()).toHaveLength(0);
  });

  // Ids are a running sequence, never reused. This is what makes the timer safe:
  // a pending auto-dismiss for a toast the user already closed fires against an
  // id nothing holds any more, so it removes nothing rather than removing
  // whatever arrived next.
  // **The two have to be staggered for this to test anything.** Written first
  // with both shown at t=0, it failed — and rightly: their timers were then due
  // at the same instant, so advancing to the first one's deadline also reached
  // the second's, and the empty list was correct rather than a bug. The point
  // here is that the *first* toast's expiry passes harmlessly while a later one
  // is still within its own lifetime.
  it('does not let a stale timer take a later notification down with it', () => {
    service.show('closed by hand');
    const first = service.notifications()[0];
    service.dismiss(first.id);

    vi.advanceTimersByTime(3000);
    service.show('arrived afterwards');
    expect(service.notifications()).toHaveLength(1);

    // t = 6000: the first toast's timer comes due. It holds an id nothing is
    // showing any more, so it must remove nothing — the second toast has until
    // t = 9000.
    vi.advanceTimersByTime(3000);

    expect(service.notifications().map((n) => n.message)).toEqual(['arrived afterwards']);
  });

  it('ignores a dismiss for something that is not showing', () => {
    service.show('still here');

    service.dismiss(9999);

    expect(service.notifications()).toHaveLength(1);
  });

  // The signal is exposed read-only, so a consumer cannot reach in and mutate
  // the list behind the service's back.
  it('exposes its notifications without a setter', () => {
    expect('set' in service.notifications).toBe(false);
  });
});
