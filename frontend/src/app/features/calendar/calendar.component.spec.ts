import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { CalendarComponent } from './calendar.component';
import { PagedResult } from '../../core/http/paged-result';
import { BookingSummary } from '../booking/booking.models';

const API = 'http://localhost:5270';

// Instants are built from local components rather than UTC literals, for the
// same reason calendar-range.spec.ts does: this screen reads them in the
// viewer's own zone, so a "...Z" literal would make these assertions quietly
// depend on where the suite runs (UTC in CI, CET locally).
function localInstant(year: number, monthIndex: number, day: number, hours = 0, minutes = 0): string {
  return new Date(year, monthIndex, day, hours, minutes).toISOString();
}

function localDateString(year: number, monthIndex: number, day: number): string {
  return `${year}-${String(monthIndex + 1).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
}

function booking(overrides: Partial<BookingSummary> = {}): BookingSummary {
  return {
    id: 'b1',
    resourceId: 'r1',
    resourceName: 'Conference Room A',
    userId: 'u1',
    userName: 'Member One',
    recurrenceRuleId: null,
    startsAtUtc: localInstant(2026, 8, 24, 9, 0),
    endsAtUtc: localInstant(2026, 8, 24, 10, 0),
    quantity: 1,
    title: null,
    status: 'Confirmed',
    ...overrides,
  };
}

function page(items: BookingSummary[], overrides: Partial<PagedResult<BookingSummary>> = {}): PagedResult<BookingSummary> {
  return {
    items,
    page: 1,
    pageSize: 100,
    totalCount: items.length,
    totalPages: 1,
    hasPreviousPage: false,
    hasNextPage: false,
    ...overrides,
  };
}

describe('CalendarComponent', () => {
  let httpMock: HttpTestingController;
  let queryParamMap$: BehaviorSubject<ReturnType<typeof convertToParamMap>>;
  let fixture: ComponentFixture<CalendarComponent>;

  // A BehaviorSubject rather than a bare `of(...)`: every navigation control on
  // this screen works by changing the URL, so the tests have to be able to
  // emit a new query-param map the way the router would.
  function createFixture(queryParams: Record<string, string> = {}) {
    queryParamMap$ = new BehaviorSubject(convertToParamMap(queryParams));

    TestBed.configureTestingModule({
      imports: [CalendarComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap(queryParams) }, queryParamMap: queryParamMap$ },
        },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(CalendarComponent);
    fixture.detectChanges();
    return fixture;
  }

  function expectWindowRequest() {
    return httpMock.expectOne((r) => r.url === `${API}/bookings`);
  }

  function root(): HTMLElement {
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  // Read as the two elements the chip actually is, rather than as its flattened
  // textContent — adjacent spans carry no whitespace between them, so joining
  // here is what a reader sees rather than what the DOM string happens to be.
  function chipTexts(): string[] {
    return Array.from(root().querySelectorAll('.chip, .week-chip')).map((c) =>
      [c.querySelector('.chip-time'), c.querySelector('.chip-label')]
        .map((el) => (el?.textContent ?? '').trim())
        .filter(Boolean)
        .join(' '),
    );
  }

  afterEach(() => {
    httpMock.verify();
  });

  describe('the bounded fetch', () => {
    it('asks only for the visible window, zone-designated, sorted chronologically', () => {
      createFixture({ view: 'month', date: '2026-09-18' });

      const req = expectWindowRequest();
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('sort')).toBe('startsAtUtc');
      expect(req.request.params.get('pageSize')).toBe('100');
      expect(req.request.params.get('from')).toMatch(/Z$/);
      expect(req.request.params.get('to')).toMatch(/Z$/);

      // September 2026 renders Aug 31 – Oct 4, so the window opens on Aug 31
      // and closes at midnight after Oct 4, not on the 1st and 30th.
      const from = new Date(req.request.params.get('from') as string);
      const to = new Date(req.request.params.get('to') as string);
      expect(localDateString(from.getFullYear(), from.getMonth(), from.getDate())).toBe('2026-08-31');
      expect(localDateString(to.getFullYear(), to.getMonth(), to.getDate())).toBe('2026-10-05');

      req.flush(page([]));
    });

    // scope=Own is the endpoint's default and this client never sends the
    // parameter — a plain member's token is 400'd for `scope=tenant`, so
    // sending it would break the screen rather than sit unused. Phase 6 owns
    // the widening.
    it('never asks for a scope or a userId', () => {
      createFixture();

      const req = expectWindowRequest();
      expect(req.request.params.has('scope')).toBe(false);
      expect(req.request.params.has('userId')).toBe(false);
      expect(req.request.params.has('status')).toBe(false);

      req.flush(page([]));
    });

    // The bound is the visible window, not one page of it: PagingDefaults
    // caps pageSize at 100 and rejects anything larger rather than clamping,
    // so a busy window has to be walked.
    it('follows the pages of one window before rendering', () => {
      createFixture({ view: 'month', date: '2026-09-18' });

      expectWindowRequest().flush(
        page([booking({ id: 'p1' })], { page: 1, hasNextPage: true, totalPages: 2, totalCount: 2 }),
      );

      const second = expectWindowRequest();
      expect(second.request.params.get('page')).toBe('2');
      second.flush(
        page([booking({ id: 'p2', title: 'From page two' })], { page: 2, hasPreviousPage: true, totalPages: 2 }),
      );

      expect(chipTexts().join(' ')).toContain('From page two');
    });

    it('re-asks for the new window when the URL changes, and cancels the one in flight', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      const first = expectWindowRequest();

      queryParamMap$.next(convertToParamMap({ view: 'month', date: '2026-10-18' }));

      // switchMap unsubscribes the previous inner observable, which aborts the
      // request outright rather than merely ignoring what it returns — so a
      // slower earlier response can never land after a newer one.
      expect(first.cancelled).toBe(true);
      expectWindowRequest().flush(page([]));
    });
  });

  describe('rendering', () => {
    it('lays the month out as whole weeks with the day numbers in place', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(page([]));

      const cells = root().querySelectorAll('.day-cell');
      expect(cells).toHaveLength(35);
      expect(cells[0].querySelector('.day-number')?.textContent?.trim()).toBe('31');
      expect(cells[0].classList.contains('day-cell--outside')).toBe(true);
      expect(root().querySelectorAll('.weekday-name')[0].textContent?.trim()).toBe('Mon');
      expect(root().querySelector('.period-label')?.textContent?.trim()).toBe('September 2026');
    });

    it('draws a booking in its own day cell, with its time and label', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(
        page([booking({ startsAtUtc: localInstant(2026, 8, 24, 9, 0), title: 'Weekly planning' })]),
      );

      expect(chipTexts()).toEqual(['09:00 Weekly planning']);
    });

    // Both designs show a mix of titles and resource names; an untitled
    // booking is legal (the column is nullable), so the resource stands in.
    it('falls back to the resource name when a booking has no title', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(page([booking({ title: null })]));

      expect(chipTexts()).toEqual(['09:00 Conference Room A']);
    });

    it('treats a whitespace-only title as no title', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(page([booking({ title: '   ' })]));

      expect(chipTexts()).toEqual(['09:00 Conference Room A']);
    });

    // The status rule, asserted where a member would actually see it rather
    // than at the signal level.
    it('draws nothing for a cancelled or rejected booking', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(
        page([
          booking({ id: 'c', status: 'Cancelled', title: 'Called off' }),
          booking({ id: 'r', status: 'Rejected', title: 'Turned down' }),
          booking({ id: 'p', status: 'Pending', title: 'Awaiting approval' }),
        ]),
      );

      // The surviving chip carries its "(Pending)" suffix from step 3 — see the
      // status-treatment tests below for why that is in the label rather than
      // only in the styling.
      expect(chipTexts()).toEqual(['09:00 Awaiting approval (Pending)']);
    });

    it('renders the week view as seven day columns with an hour axis', () => {
      createFixture({ view: 'week', date: '2026-09-23' });
      expectWindowRequest().flush(
        page([booking({ startsAtUtc: localInstant(2026, 8, 23, 10, 0), endsAtUtc: localInstant(2026, 8, 23, 11, 0), title: 'Product Review' })]),
      );

      expect(root().querySelectorAll('.week-column')).toHaveLength(7);
      expect(root().querySelector('.period-label')?.textContent?.trim()).toBe('Sep 21 – Sep 27, 2026');
      expect(chipTexts()).toEqual(['10:00 – 11:00 Product Review']);
      // 10:00 is two hours into the default 08:00–18:00 window.
      expect((root().querySelector('.week-chip') as HTMLElement).style.top).toBe('20%');
    });

    // Regression, 2026-09-18: chips sat progressively lower than their own
    // stated times. Two independent one-row errors — the grid drew a row per
    // *label* (eleven for 08:00–18:00) while the chip offsets were percentages
    // of the ten-hour *span*, and the hour lines were border-bottoms sitting an
    // hour below their own labels.
    //
    // **Nothing that asserts the percentage string can catch this**: `top: 20%`
    // is what both the correct and the broken version produce, and jsdom does
    // no layout, so there are no pixels to measure. What is checkable is the
    // basis itself — that a row is an hour *span*, and that a label and a chip
    // starting at that hour resolve to the same offset.
    it('draws one row per hour span, not one per label', () => {
      createFixture({ view: 'week', date: '2026-09-23' });
      expectWindowRequest().flush(page([]));

      // The default window is 08:00–18:00: eleven labels, ten hours.
      expect(root().querySelectorAll('.hour-tick')).toHaveLength(11);
      expect(root().querySelectorAll('.week-column')[0].querySelectorAll('.hour-line')).toHaveLength(10);
    });

    it('puts an hour label on exactly the line a chip starting at that hour begins on', () => {
      createFixture({ view: 'week', date: '2026-09-23' });
      expectWindowRequest().flush(
        page([
          booking({
            startsAtUtc: localInstant(2026, 8, 23, 10, 0),
            endsAtUtc: localInstant(2026, 8, 23, 11, 0),
          }),
        ]),
      );

      const labels = Array.from(root().querySelectorAll('.hour-tick')) as HTMLElement[];
      expect(labels[0].style.top).toBe('0%');
      expect(labels[labels.length - 1].style.top).toBe('100%');

      // The 10:00 label and the 10:00 chip have to agree, or the grid lies.
      const tenOClock = labels.find((l) => l.textContent?.trim() === '10:00');
      expect(tenOClock?.style.top).toBe((root().querySelector('.week-chip') as HTMLElement).style.top);
    });

    it('marks today in both views', () => {
      const today = new Date();
      const todayDate = localDateString(today.getFullYear(), today.getMonth(), today.getDate());

      createFixture({ view: 'month', date: todayDate });
      expectWindowRequest().flush(page([]));
      expect(root().querySelectorAll('.day-number--today')).toHaveLength(1);

      queryParamMap$.next(convertToParamMap({ view: 'week', date: todayDate }));
      expectWindowRequest().flush(page([]));
      expect(root().querySelectorAll('.week-day-name--today')).toHaveLength(1);
    });
  });

  // Regression, 2026-09-18 (second report). The week grid's columns drifted
  // right of their own day headers, and 08:00 could not be brought into view at
  // any scroll position.
  //
  // Both came from the same mistake — the header and the body were two grids in
  // two different boxes, only one of which scrolled. The scrollbar is laid out
  // *inside* the scrolling box, so the body's seven columns each came out a
  // couple of pixels narrower than the header's and the drift accumulated
  // (~17px by Sunday); and the opening label, centred on the body's own top
  // edge, was half outside it and clipped by that same overflow.
  //
  // jsdom performs no layout, so neither symptom is measurable here — but the
  // *mechanism* is, because it resolves the component's stylesheet. These
  // assert the structural property the fix rests on: one scroll box, a sticky
  // header inside it, and room for the labels that straddle its edges.
  describe('scrolling and alignment', () => {
    it('scrolls one box containing both the day header and the columns', () => {
      createFixture({ view: 'week', date: '2026-09-23' });
      expectWindowRequest().flush(page([]));

      const grid = root().querySelector('.grid--week') as HTMLElement;
      const body = root().querySelector('.week-body') as HTMLElement;

      expect(getComputedStyle(grid).overflow).toBe('auto');
      // The body must NOT scroll on its own — that is what made its columns
      // narrower than the header's.
      expect(getComputedStyle(body).overflowY).toBe('');
    });

    it('pins the day header by making it sticky, not by giving it its own box', () => {
      createFixture({ view: 'week', date: '2026-09-23' });
      expectWindowRequest().flush(page([]));

      expect(getComputedStyle(root().querySelector('.week-header') as HTMLElement).position).toBe('sticky');
    });

    // The opening and closing hour labels are centred on the grid's own edges,
    // so half of each sits outside it. Without this room, 08:00 was cut in two.
    it('leaves room for the labels that straddle the top and bottom edges', () => {
      createFixture({ view: 'week', date: '2026-09-23' });
      expectWindowRequest().flush(page([]));

      const body = getComputedStyle(root().querySelector('.week-body') as HTMLElement);
      expect(body.paddingTop).toBe('10px');
      expect(body.paddingBottom).toBe('10px');
    });

    it('scrolls the month view the same single-box way', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(page([]));

      expect(getComputedStyle(root().querySelector('.grid--month') as HTMLElement).overflow).toBe('auto');
      expect(getComputedStyle(root().querySelector('.month-cells') as HTMLElement).overflowY).toBe('');
      expect(getComputedStyle(root().querySelector('.weekday-header') as HTMLElement).position).toBe('sticky');
    });

    // A flex item shrinks to fit by default, which would compress the grids
    // into the visible height and leave `.grid` with nothing to scroll.
    it('lets the grids keep their natural height instead of shrinking to fit', () => {
      createFixture({ view: 'week', date: '2026-09-23' });
      expectWindowRequest().flush(page([]));

      expect(getComputedStyle(root().querySelector('.week-body') as HTMLElement).flexShrink).toBe('0');
      expect(getComputedStyle(root().querySelector('.week-header') as HTMLElement).flexShrink).toBe('0');
    });
  });

  // Step 3. Every assertion here is against the rendered DOM rather than a
  // signal — the standing lesson of this package, three bugs in.
  describe('how a booking reads in a cell', () => {
    function chipAt(index = 0): HTMLElement {
      return root().querySelectorAll('.chip, .week-chip')[index] as HTMLElement;
    }

    // **Colour is never the only carrier.** A member acts on Pending — the slot
    // is not held yet (FR-7.1) — so it is in the label, where greyscale, a
    // colour-blind reader and a screen reader all reach it.
    it('says Pending in the label, not only in the styling', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(page([booking({ status: 'Pending', title: 'Lab 2' })]));

      expect(chipTexts()).toEqual(['09:00 Lab 2 (Pending)']);
      expect(chipAt().classList.contains('chip--pending')).toBe(true);
    });

    it('marks a no-show in the label too, and mutes it as past', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(page([booking({ status: 'NoShow', title: 'Pool Cars' })]));

      expect(chipTexts()).toEqual(['09:00 Pool Cars (No-show)']);
      expect(chipAt().classList.contains('chip--past')).toBe(true);
    });

    // Completed is the unremarkable past: receded, but not annotated — there is
    // nothing for the member to do about it.
    it('mutes a completed booking without labelling it', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(page([booking({ status: 'Completed', title: 'Design Studio' })]));

      expect(chipTexts()).toEqual(['09:00 Design Studio']);
      expect(chipAt().classList.contains('chip--past')).toBe(true);
    });

    it('leaves a confirmed booking plain', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(page([booking({ status: 'Confirmed', title: 'Printer' })]));

      expect(chipTexts()).toEqual(['09:00 Printer']);
      expect(chipAt().classList.contains('chip--pending')).toBe(false);
      expect(chipAt().classList.contains('chip--past')).toBe(false);
    });

    // Decision 0007 materializes every occurrence as its own row, so a series is
    // just rows sharing a recurrenceRuleId — the marker is the only thing that
    // says so, since nothing else on the chip differs.
    it('marks a series occurrence, and says so to a screen reader', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(
        page([booking({ recurrenceRuleId: 'rr1', title: 'Weekly planning' })]),
      );

      expect(chipAt().querySelector('.chip-recurring')).not.toBeNull();
      expect(chipAt().getAttribute('aria-label')).toContain('part of a repeating series');
    });

    it('leaves a one-off booking unmarked', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(page([booking({ recurrenceRuleId: null })]));

      expect(chipAt().querySelector('.chip-recurring')).toBeNull();
      expect(chipAt().getAttribute('aria-label')).not.toContain('repeating');
    });

    // The visual chip splits into a time, a label, a suffix and an icon, none of
    // which reads well announced on its own.
    it('announces the whole chip as one sentence', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(
        page([booking({ status: 'Pending', title: 'Lab 2', recurrenceRuleId: 'rr1' })]),
      );

      expect(chipAt().getAttribute('aria-label')).toBe(
        '09:00 – 10:00, Lab 2 (Pending), part of a repeating series',
      );
    });

    it('says when a chip is a clipped piece of a longer booking', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(
        page([
          booking({
            title: 'Overnight run',
            startsAtUtc: localInstant(2026, 8, 24, 22, 0),
            endsAtUtc: localInstant(2026, 8, 25, 2, 0),
          }),
        ]),
      );

      const labels = Array.from(root().querySelectorAll('.chip')).map((c) => c.getAttribute('aria-label'));
      expect(labels[0]).toContain('continues into the next day');
      expect(labels[1]).toContain('continues from the previous day');
    });

    // Regression, 2026-09-18 (third report). Two problems in the same column,
    // both visible in the owner's screenshot.
    //
    // 1. Every chip spanned the full column width, so overlapping bookings were
    //    painted on top of one another and whichever came last in the DOM hid
    //    the rest.
    // 2. A chip's height comes from its duration (56px per hour), but its
    //    content is a time line above a label line — so anything under about
    //    three quarters of an hour was shorter than its own content and
    //    `overflow: hidden` swallowed the booking's name.
    it('shares the column between two overlapping bookings instead of stacking them', () => {
      createFixture({ view: 'week', date: '2026-09-23' });
      expectWindowRequest().flush(
        page([
          booking({
            id: 'a',
            title: 'First',
            startsAtUtc: localInstant(2026, 8, 24, 15, 0),
            endsAtUtc: localInstant(2026, 8, 24, 18, 0),
          }),
          booking({
            id: 'b',
            title: 'Second',
            startsAtUtc: localInstant(2026, 8, 24, 16, 15),
            endsAtUtc: localInstant(2026, 8, 24, 19, 15),
          }),
        ]),
      );

      const chips = Array.from(root().querySelectorAll('.week-chip')) as HTMLElement[];
      expect(chips).toHaveLength(2);
      // Half the column each, side by side — not one on top of the other.
      expect(chips.map((c) => c.style.width)).toEqual(['calc(50% - 4px)', 'calc(50% - 4px)']);
      expect(chips[0].style.left).toBe('calc(0% + 2px)');
      expect(chips[1].style.left).toBe('calc(50% + 2px)');
    });

    it('gives a booking with nothing beside it the whole column', () => {
      createFixture({ view: 'week', date: '2026-09-23' });
      expectWindowRequest().flush(
        page([
          booking({
            startsAtUtc: localInstant(2026, 8, 24, 10, 0),
            endsAtUtc: localInstant(2026, 8, 24, 11, 0),
          }),
        ]),
      );

      const chip = root().querySelector('.week-chip') as HTMLElement;
      expect(chip.style.width).toBe('calc(100% - 4px)');
      expect(chip.style.left).toBe('calc(0% + 2px)');
    });

    it('lays a short booking out on one line so its name is not clipped away', () => {
      createFixture({ view: 'week', date: '2026-09-23' });
      expectWindowRequest().flush(
        page([
          booking({
            title: 'Another test',
            startsAtUtc: localInstant(2026, 8, 24, 21, 0),
            endsAtUtc: localInstant(2026, 8, 24, 21, 30),
          }),
        ]),
      );

      const chip = root().querySelector('.week-chip') as HTMLElement;
      expect(chip.classList.contains('week-chip--compact')).toBe(true);
      // The name still has to be *there* — clipping it was the bug.
      expect(chip.querySelector('.chip-label')?.textContent).toContain('Another test');
      expect(getComputedStyle(chip).flexDirection).toBe('row');
    });

    it('keeps the two-line shape for a booking with room for it', () => {
      createFixture({ view: 'week', date: '2026-09-23' });
      expectWindowRequest().flush(
        page([
          booking({
            startsAtUtc: localInstant(2026, 8, 24, 10, 0),
            endsAtUtc: localInstant(2026, 8, 24, 11, 0),
          }),
        ]),
      );

      const chip = root().querySelector('.week-chip') as HTMLElement;
      expect(chip.classList.contains('week-chip--compact')).toBe(false);
      expect(getComputedStyle(chip).flexDirection).toBe('column');
    });

    // Step 4 gave chips somewhere to go. A real anchor rather than a click
    // handler, so middle-click, copy-link and open-in-new-tab all work — the
    // same reasoning that made the resource card's title a real `<a>` in the
    // 2026-09-16 accessibility pass.
    it('opens the booking it represents, in both views', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(page([booking({ id: 'bk-7' })]));

      expect(root().querySelector('.chip')?.tagName).toBe('A');
      expect(root().querySelector('.chip')?.getAttribute('href')).toBe('/bookings/bk-7');

      queryParamMap$.next(convertToParamMap({ view: 'week', date: '2026-09-23' }));
      expectWindowRequest().flush(
        page([
          booking({
            id: 'bk-9',
            startsAtUtc: localInstant(2026, 8, 24, 10, 0),
            endsAtUtc: localInstant(2026, 8, 24, 11, 0),
          }),
        ]),
      );

      expect(root().querySelector('.week-chip')?.getAttribute('href')).toBe('/bookings/bk-9');
    });

    it('carries the same treatments into the week view', () => {
      createFixture({ view: 'week', date: '2026-09-23' });
      expectWindowRequest().flush(
        page([
          booking({
            status: 'Pending',
            title: 'Lab 1',
            recurrenceRuleId: 'rr1',
            startsAtUtc: localInstant(2026, 8, 24, 13, 0),
            endsAtUtc: localInstant(2026, 8, 24, 14, 0),
          }),
        ]),
      );

      expect(chipTexts()).toEqual(['13:00 – 14:00 Lab 1 (Pending)']);
      expect(chipAt().classList.contains('chip--pending')).toBe(true);
      expect(chipAt().querySelector('.chip-recurring')).not.toBeNull();
    });
  });

  // The responsiveness acceptance criterion, asserted as the property that
  // actually delivers it: **the DOM a busy day costs is bounded by the cap, not
  // by the data**. A timing assertion would be flaky in CI and would not say
  // why; this says why.
  describe('volume', () => {
    function manyOn(date: [number, number, number], count: number): BookingSummary[] {
      return Array.from({ length: count }, (_, i) =>
        booking({
          id: `b${i}`,
          title: `Booking ${i}`,
          startsAtUtc: localInstant(date[0], date[1], date[2], 8 + (i % 10), 0),
          endsAtUtc: localInstant(date[0], date[1], date[2], 9 + (i % 10), 0),
        }),
      );
    }

    it('caps the chips a single busy day renders, however many it holds', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(page(manyOn([2026, 8, 24], 40)));

      const cell = Array.from(root().querySelectorAll('.day-cell')).find((c) =>
        c.querySelector('.more-button'),
      ) as HTMLElement;

      // Two chips plus the summary row, never forty.
      expect(cell.querySelectorAll('.chip')).toHaveLength(2);
      expect(cell.querySelector('.more-button')?.textContent?.trim()).toBe('+38 more');
    });

    it('keeps the whole month’s DOM bounded under a realistic volume', () => {
      createFixture({ view: 'month', date: '2026-09-18' });

      // 350 bookings spread across the visible window — comfortably past the
      // 260-booking benchmark wp7-plan.md names, and far past anything one
      // member accumulates in a month.
      const spread = Array.from({ length: 350 }, (_, i) =>
        booking({
          id: `b${i}`,
          title: `Booking ${i}`,
          startsAtUtc: localInstant(2026, 8, 1 + (i % 28), 8 + (i % 8), 0),
          endsAtUtc: localInstant(2026, 8, 1 + (i % 28), 9 + (i % 8), 0),
        }),
      );
      expectWindowRequest().flush(page(spread));

      const cells = root().querySelectorAll('.day-cell');
      const chips = root().querySelectorAll('.chip');

      expect(cells).toHaveLength(35);
      // The ceiling is structural: 35 cells x at most 3 chips each.
      expect(chips.length).toBeLessThanOrEqual(35 * 3);
      // And every hidden one is still accounted for on screen.
      expect(root().querySelectorAll('.more-button').length).toBeGreaterThan(0);
    });

    it('opens a capped day in place, and closes it again', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(page(manyOn([2026, 8, 24], 10)));

      const cellOf = () =>
        Array.from(root().querySelectorAll('.day-cell')).find((c) =>
          c.querySelector('.more-button'),
        ) as HTMLElement;

      (cellOf().querySelector('.more-button') as HTMLButtonElement).click();

      // Everything reachable — there is no day view to send anyone to, which is
      // why the affordance expands rather than navigates.
      expect(cellOf().querySelectorAll('.chip')).toHaveLength(10);
      expect(cellOf().querySelector('.more-button')?.textContent?.trim()).toBe('Show less');

      (cellOf().querySelector('.more-button') as HTMLButtonElement).click();
      expect(cellOf().querySelectorAll('.chip')).toHaveLength(2);
    });

    it('collapses expanded days when the window changes', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(page(manyOn([2026, 8, 24], 10)));

      (root().querySelector('.more-button') as HTMLButtonElement).click();
      expect(root().querySelectorAll('.chip')).toHaveLength(10);

      queryParamMap$.next(convertToParamMap({ view: 'month', date: '2026-10-18' }));
      expectWindowRequest().flush(page(manyOn([2026, 9, 24], 10)));

      // The expansion referred to cells that no longer exist; carrying it over
      // would expand whichever day of the new window shared the date string.
      expect(root().querySelectorAll('.chip')).toHaveLength(2);
    });
  });

  describe('states', () => {
    it('shows a loading message before the window arrives', () => {
      createFixture();

      expect(root().querySelector('.state-message')?.textContent).toContain('Loading your bookings');

      expectWindowRequest().flush(page([]));
    });

    // Deliberately "this month", never "you have no bookings": the fetch is
    // bounded to the visible window, so the screen genuinely does not know
    // whether there are bookings elsewhere.
    it('says nothing is booked in this period, not that there are no bookings', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(page([]));

      const message = root().querySelector('.state-message')?.textContent ?? '';
      expect(message).toContain('Nothing booked in this month');
      expect(message).not.toContain('no bookings');
      // The grid is still drawn behind the notice — an empty September is
      // still September.
      expect(root().querySelectorAll('.day-cell').length).toBeGreaterThan(0);
    });

    it('offers a retry when the window fails, and re-asks on click', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().error(new ProgressEvent('error'));

      const error = root().querySelector('.state-message--error');
      expect(error?.textContent).toContain("couldn't load your bookings");
      expect(root().querySelectorAll('.day-cell')).toHaveLength(0);

      (error?.querySelector('button') as HTMLButtonElement).click();

      expectWindowRequest().flush(page([booking({ title: 'Recovered' })]));
      expect(chipTexts()).toEqual(['09:00 Recovered']);
    });
  });

  describe('navigation', () => {
    let router: Router;

    function navigateSpy() {
      router = TestBed.inject(Router);
      return vi.spyOn(router, 'navigate').mockResolvedValue(true);
    }

    it('steps a month at a time and writes it to the URL', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(page([]));
      const navigate = navigateSpy();

      (root().querySelectorAll('.step-button')[1] as HTMLButtonElement).click();

      expect(navigate).toHaveBeenCalledWith(
        [],
        expect.objectContaining({ queryParams: { view: 'month', date: '2026-10-01' } }),
      );
    });

    it('steps a week at a time in the week view', () => {
      createFixture({ view: 'week', date: '2026-09-23' });
      expectWindowRequest().flush(page([]));
      const navigate = navigateSpy();

      (root().querySelectorAll('.step-button')[0] as HTMLButtonElement).click();

      expect(navigate).toHaveBeenCalledWith(
        [],
        expect.objectContaining({ queryParams: { view: 'week', date: '2026-09-16' } }),
      );
    });

    it('keeps the anchored date when switching view', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(page([]));
      const navigate = navigateSpy();

      const weekOption = Array.from(root().querySelectorAll('.view-option')).find(
        (b) => b.textContent?.trim() === 'Week',
      ) as HTMLButtonElement;
      weekOption.click();

      expect(navigate).toHaveBeenCalledWith(
        [],
        expect.objectContaining({ queryParams: { view: 'week', date: '2026-09-18' } }),
      );
    });

    it('returns to today without changing the view', () => {
      createFixture({ view: 'week', date: '2020-01-01' });
      expectWindowRequest().flush(page([]));
      const navigate = navigateSpy();

      (root().querySelector('.today-button') as HTMLButtonElement).click();

      const today = new Date();
      expect(navigate).toHaveBeenCalledWith(
        [],
        expect.objectContaining({
          queryParams: {
            view: 'week',
            date: localDateString(today.getFullYear(), today.getMonth(), today.getDate()),
          },
        }),
      );
    });

    // The toggle is a radiogroup, not a pair of pressed buttons: these two
    // switch what the same region shows and exactly one is always chosen.
    it('marks the active view for assistive technology, not only by colour', () => {
      createFixture({ view: 'week', date: '2026-09-23' });
      expectWindowRequest().flush(page([]));

      const options = Array.from(root().querySelectorAll('.view-option'));
      expect(options.map((o) => o.getAttribute('aria-checked'))).toEqual(['false', 'true']);
      expect(root().querySelector('[role="radiogroup"]')).not.toBeNull();
    });

    it('names which direction each step button goes', () => {
      createFixture({ view: 'month', date: '2026-09-18' });
      expectWindowRequest().flush(page([]));

      expect(
        Array.from(root().querySelectorAll('.step-button')).map((b) => b.getAttribute('aria-label')),
      ).toEqual(['Previous month', 'Next month']);
    });
  });
});
