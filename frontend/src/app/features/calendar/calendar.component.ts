import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Observable, Subject, catchError, expand, map, merge, of, reduce, switchMap } from 'rxjs';
import { PagedResult } from '../../core/http/paged-result';
import { formatLocalDate, formatMinutesOfDay } from '../availability/local-date';
import { LocalDateString } from '../availability/availability.models';
import { BookingsService } from '../booking/bookings.service';
import { BookingSummary } from '../booking/booking.models';
import {
  CalendarRange,
  CalendarUrlState,
  CalendarView,
  DayCell,
  DayChips,
  DayEntry,
  HourRange,
  PositionedEntry,
  buildCalendarQueryParams,
  buildDayCells,
  chipsForCell,
  fetchWindowUtc,
  hourOffsetPercent,
  hourSpanStylePercent,
  isCompactChip,
  layOutDay,
  parseCalendarUrlState,
  stepRange,
  viewerToday,
  visibleRange,
  weekHourRange,
} from './calendar-range';

// WP-7 Phase 4 steps 2-3 — the calendar shell, the bounded fetch, and how a
// booking reads in a cell.
//
// **This is the landing screen** (`/calendar`, with `/home` redirecting to it
// and `''` pointing there), so its loading and empty states are the first thing
// anyone sees after signing in rather than incidental surface.
//
// **The fetch strategy is the point of this component.** `GET /bookings` is
// asked only for the visible date window and re-asked on navigation; nothing
// ever fetches "all bookings" and filters in the browser. That is what makes
// decision `0007`'s fully-materialized series tractable — every occurrence is
// already its own `Booking` row, so **there is no client-side recurrence
// expansion anywhere in this feature, and there must never be**: a series is
// just rows that happen to share a `recurrenceRuleId`.
//
// **Two things about the week view that are easy to undo by accident.**
// Overlapping bookings are laid into side-by-side columns (`layOutDay`) rather
// than every chip spanning the full width — a member with two bookings at once
// is ordinary, and full-width chips simply painted over each other. And a
// booking too short to stack a time line above a label line gets a one-line
// chip instead (`isCompactChip`); the chip is `overflow: hidden`, so the
// two-line shape silently swallowed the name of anything under about three
// quarters of an hour.
//
// Deliberately **not** here, and owned by step 4: making a chip clickable.
// There is nowhere to send anyone until `/bookings/:id` exists.
@Component({
  selector: 'app-calendar',
  imports: [RouterLink],
  templateUrl: './calendar.component.html',
  styleUrl: './calendar.component.scss',
})
export class CalendarComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly bookings = inject(BookingsService);

  protected readonly loading = signal(true);
  protected readonly loadFailed = signal(false);

  private readonly bookingRows = signal<readonly BookingSummary[]>([]);

  // Today is read once per component rather than per render: a `computed` over
  // `new Date()` would be a lie (it never re-evaluates on its own), and a timer
  // ticking a signal at midnight is more machinery than a highlight ring needs.
  // A session left open across midnight shows yesterday's ring until the next
  // navigation, which is the same thing every calendar app does.
  private readonly today = viewerToday();

  // The URL is the state, not a mirror of it — the same rule Phase 2's date
  // navigator and Phase 3's selected slot follow. Reading it as a signal means
  // back/forward, a reload and a pasted link all arrive through one path.
  private readonly urlState = signal<CalendarUrlState>({ view: 'month', date: this.today });

  private readonly retry$ = new Subject<void>();

  protected readonly view = computed(() => this.urlState().view);
  protected readonly anchor = computed(() => this.urlState().date);

  protected readonly range = computed<CalendarRange>(() =>
    visibleRange(this.view(), this.anchor()),
  );

  protected readonly cells = computed<DayCell[]>(() =>
    buildDayCells(this.bookingRows(), this.range(), this.anchor(), this.today),
  );

  protected readonly weekHours = computed<HourRange>(() => weekHourRange(this.cells()));

  // **Labels and rows are different counts, deliberately.** An 08:00-18:00
  // window has eleven labels (both ends are real times a person reads) but ten
  // hour-long rows. Drawing one row per label made the column an hour taller
  // than the window the chip percentages were computed against, which is what
  // made every chip sit slightly low — see minuteOffsetPercent's own comment.
  protected readonly hourTicks = computed<number[]>(() => {
    const { startHour, endHour } = this.weekHours();
    return Array.from({ length: endHour - startHour + 1 }, (_, i) => startHour + i);
  });

  // One per hour *span*: these are the grid rows, and their count times the row
  // height is exactly the window the chip offsets are percentages of.
  protected readonly hourRows = computed<number[]>(() => {
    const { startHour, endHour } = this.weekHours();
    return Array.from({ length: endHour - startHour }, (_, i) => startHour + i);
  });

  protected readonly isEmpty = computed(
    () => !this.loading() && !this.loadFailed() && this.cells().every((c) => c.entries.length === 0),
  );

  // "September 2026" for a month; "Sep 21 – Sep 27, 2026" for a week, matching
  // each design's own heading.
  protected readonly periodLabel = computed(() => {
    const range = this.range();
    if (this.view() === 'month') {
      return new Intl.DateTimeFormat('en-US', {
        month: 'long',
        year: 'numeric',
        timeZone: 'UTC',
      }).format(new Date(`${this.anchor()}T00:00:00Z`));
    }

    const start = formatLocalDate(range.start);
    const end = formatLocalDate(range.end);
    // The year is stated once, on the end, unless the week straddles two.
    return start.slice(-4) === end.slice(-4)
      ? `${start.slice(0, -6)} – ${end}`
      : `${start} – ${end}`;
  });

  constructor() {
    // Every trigger — first load, a query-param change from any of the
    // navigation controls, back/forward, and retry() — funnels through one
    // switchMap, so a slower earlier response can never land after a newer one.
    // switchMap cancels the in-flight request outright rather than merely
    // ignoring its result, the same guarantee the 2026-09-16 hardening pass
    // gave ResourceDetailComponent and AvailabilityComponent.
    const urlChanges$ = this.route.queryParamMap.pipe(
      map((params) => parseCalendarUrlState(params, this.today)),
    );

    merge(urlChanges$, this.retry$.pipe(map(() => this.urlState())))
      .pipe(
        map((state) => {
          this.urlState.set(state);
          this.loading.set(true);
          this.loadFailed.set(false);
          // The cells an expansion referred to are gone; carrying the set over
          // would silently expand whichever days of the *new* window happened
          // to share a date string.
          this.expandedDays.set(new Set());
          return visibleRange(state.view, state.date);
        }),
        switchMap((range) => this.fetchWindow$(range)),
        takeUntilDestroyed(),
      )
      .subscribe((rows) => {
        this.loading.set(false);
        if (rows === null) {
          this.loadFailed.set(true);
          this.bookingRows.set([]);
          return;
        }
        this.bookingRows.set(rows);
      });
  }

  protected retry(): void {
    this.retry$.next();
  }

  protected setView(view: CalendarView): void {
    this.navigate({ view, date: this.anchor() });
  }

  protected step(direction: -1 | 1): void {
    this.navigate({ view: this.view(), date: stepRange(this.view(), this.anchor(), direction) });
  }

  protected goToToday(): void {
    this.navigate({ view: this.view(), date: this.today });
  }

  // Which days the member has opened past the chip cap. Transient by design and
  // deliberately *not* in the URL: it is a disclosure within one cell, not
  // cross-screen state, and a link that reopened somebody else's expanded cells
  // would be noise. Cleared whenever the window changes, since the cells it
  // refers to no longer exist.
  private readonly expandedDays = signal<ReadonlySet<string>>(new Set());

  protected chipsFor(cell: DayCell): DayChips {
    return chipsForCell(cell.entries, this.expandedDays().has(cell.date));
  }

  protected toggleDay(date: LocalDateString): void {
    const next = new Set(this.expandedDays());
    if (!next.delete(date)) {
      next.add(date);
    }
    this.expandedDays.set(next);
  }

  protected isExpanded(date: LocalDateString): boolean {
    return this.expandedDays().has(date);
  }

  // A booking that has not been approved yet is not holding the slot (FR-7.1),
  // which is the one status distinction a member acts on — so it is carried by
  // a dashed outline *and* by the label, never by colour alone.
  protected isPending(entry: DayEntry): boolean {
    return entry.booking.status === 'Pending';
  }

  // Completed and NoShow are facts about a slot that has already passed. Muted
  // rather than hidden: they are honest history, and a calendar that forgot
  // them would read as though nothing had happened.
  protected isPast(entry: DayEntry): boolean {
    return entry.booking.status === 'Completed' || entry.booking.status === 'NoShow';
  }

  // Appended to the label rather than shown as a coloured pill, so the
  // distinction survives greyscale, a screen reader and a colour-blind reader
  // alike. `Completed` gets none: it is the unremarkable past.
  protected statusSuffix(entry: DayEntry): string {
    switch (entry.booking.status) {
      case 'Pending':
        return ' (Pending)';
      case 'NoShow':
        return ' (No-show)';
      default:
        return '';
    }
  }

  protected isRecurring(entry: DayEntry): boolean {
    return entry.booking.recurrenceRuleId !== null;
  }

  // The whole chip in one sentence, for assistive technology — the visual
  // version splits it across a time, a label, a suffix and an icon, none of
  // which reads well announced separately.
  protected entryAriaLabel(entry: DayEntry): string {
    const parts = [this.entryTimeRange(entry), this.entryLabel(entry) + this.statusSuffix(entry)];
    if (this.isRecurring(entry)) {
      parts.push('part of a repeating series');
    }
    if (entry.continuesFromPreviousDay) {
      parts.push('continues from the previous day');
    }
    if (entry.continuesIntoNextDay) {
      parts.push('continues into the next day');
    }
    return parts.join(', ');
  }

  protected entryLabel(entry: DayEntry): string {
    // The title when the member gave one, the resource otherwise — which is
    // what both designs show (a mix of "Weekly planning" and "Conference Room
    // A"). The detail screen (step 4) carries both, so nothing is lost here.
    return entry.booking.title?.trim() || entry.booking.resourceName;
  }

  protected entryTime(entry: DayEntry): string {
    return formatMinutesOfDay(entry.startMinutes);
  }

  protected entryTimeRange(entry: DayEntry): string {
    return `${formatMinutesOfDay(entry.startMinutes)} – ${formatMinutesOfDay(entry.endMinutes)}`;
  }

  // Week view only: overlaps resolved into side-by-side columns before
  // anything is positioned.
  protected positionedEntries(cell: DayCell): PositionedEntry[] {
    return layOutDay(cell.entries);
  }

  protected entryStyle(positioned: PositionedEntry): Record<string, string> {
    const { top, height } = hourSpanStylePercent(positioned.entry, this.weekHours());
    const width = 100 / positioned.columnCount;

    return {
      top,
      height,
      // The 2px inset keeps neighbouring chips from touching; taking it out of
      // the width rather than off the left edge keeps the last column flush
      // with the day's own right-hand rule.
      left: `calc(${positioned.column * width}% + 2px)`,
      width: `calc(${width}% - 4px)`,
    };
  }

  // A booking too short to stack two lines lays them out on one instead. It
  // used to keep the two-line shape and clip the label away entirely.
  protected isCompact(entry: DayEntry): boolean {
    return isCompactChip(entry);
  }

  protected weekdayLabel(date: LocalDateString): string {
    return new Intl.DateTimeFormat('en-US', { weekday: 'short', timeZone: 'UTC' }).format(
      new Date(`${date}T00:00:00Z`),
    );
  }

  protected dayHeaderLabel(date: LocalDateString): string {
    return new Intl.DateTimeFormat('en-US', {
      month: 'short',
      day: 'numeric',
      timeZone: 'UTC',
    }).format(new Date(`${date}T00:00:00Z`));
  }

  protected hourLabel(hour: number): string {
    return formatMinutesOfDay(hour * 60);
  }

  // Positioned through the same function the chips use, rather than by a grid
  // row of its own — so a label and a chip starting at that hour land on
  // exactly the same line by construction, not by two calculations agreeing.
  protected hourLabelTop(hour: number): string {
    return `${hourOffsetPercent(hour, this.weekHours())}%`;
  }

  private navigate(state: CalendarUrlState): void {
    // A real navigation rather than a local signal write, so the URL stays the
    // single source of truth and back/forward step through months the way a
    // person expects. The fetch is triggered by the resulting queryParamMap
    // emission, not from here.
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: buildCalendarQueryParams(state),
    });
  }

  // Emits the whole window's rows, or `null` if the fetch failed.
  //
  // **Paged, because the window can legitimately exceed one page.**
  // `PagingDefaults.MaxPageSize` is 100 and is a ceiling rather than a
  // suggestion — an oversized `pageSize` is a 400, not a clamp — so a month
  // holding more than 100 of the member's own bookings has to be walked. That
  // is still "bounded fetch": the bound is the visible window, and following
  // its pages is completing one question rather than asking a broader one.
  private fetchWindow$(range: CalendarRange): Observable<readonly BookingSummary[] | null> {
    const window = fetchWindowUtc(range);

    const page$ = (page: number) =>
      this.bookings.list({
        from: window.from,
        to: window.to,
        page,
        pageSize: PAGE_SIZE,
        sort: 'startsAtUtc',
      });

    return page$(1).pipe(
      expand((result) =>
        result.hasNextPage && result.page < MAX_PAGES ? page$(result.page + 1) : of(),
      ),
      reduce(
        (all: BookingSummary[], result: PagedResult<BookingSummary>) => all.concat(result.items),
        [],
      ),
      catchError(() => of(null)),
    );
  }
}

// The API's own maximum (PagingDefaults.MaxPageSize), so the common case is one
// request. Asking for more is a 400 rather than a clamp, which is why this
// mirrors the server's number instead of picking a larger one.
const PAGE_SIZE = 100;

// A stop on the page walk, not a product rule: nothing a member can do should
// produce 10,000 of their own bookings inside one month, so reaching this means
// something is wrong server-side and looping forever would be the worse
// failure. The grid renders what it got.
const MAX_PAGES = 100;
