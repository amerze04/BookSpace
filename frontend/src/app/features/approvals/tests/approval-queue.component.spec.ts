import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { ApprovalQueueComponent } from '../components/approval-queue/approval-queue.component';
import { PagedResult } from '../../../core/http/paged-result';
import { BookingSummary } from '../../booking/models/booking.models';

const API = 'http://localhost:5270';

// Local components rather than "…Z" literals: this screen reads instants in the
// viewer's own zone, and CI runs in UTC while local development here is CET.
function localInstant(
  year: number,
  monthIndex: number,
  day: number,
  hours = 0,
  minutes = 0,
): string {
  return new Date(year, monthIndex, day, hours, minutes).toISOString();
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

function page(
  items: BookingSummary[],
  overrides: Partial<PagedResult<BookingSummary>> = {},
): PagedResult<BookingSummary> {
  return {
    items,
    page: 1,
    pageSize: 20,
    totalCount: items.length,
    totalPages: 1,
    hasPreviousPage: false,
    hasNextPage: false,
    ...overrides,
  };
}

describe('ApprovalQueueComponent', () => {
  let httpMock: HttpTestingController;
  let fixture: ComponentFixture<ApprovalQueueComponent>;

  function createFixture() {
    TestBed.configureTestingModule({
      imports: [ApprovalQueueComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ApprovalQueueComponent);
    fixture.detectChanges();
    return fixture;
  }

  function expectQueueRequest() {
    return httpMock.expectOne((r) => r.url === `${API}/bookings`);
  }

  function root(): HTMLElement {
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  function text(): string {
    return root().textContent ?? '';
  }

  function cards(): HTMLElement[] {
    return Array.from(root().querySelectorAll('.queue-card'));
  }

  afterEach(() => {
    httpMock.verify();
  });

  // The request is the design decision this screen rests on, so it is asserted
  // parameter by parameter rather than by URL alone.
  it('asks for the tenant-wide pending queue, oldest first', () => {
    createFixture();

    const req = expectQueueRequest();
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('scope')).toBe('tenant');
    expect(req.request.params.get('status')).toBe('Pending');
    expect(req.request.params.get('sort')).toBe('createdAtUtc');
    expect(req.request.params.get('page')).toBe('1');

    req.flush(page([]));
  });

  // An Approver and a TenantAdmin send the same request; the server narrows the
  // rows by ApprovalReach. A client-side role branch would be a second copy of
  // an authorization rule that cannot see what the server sees.
  it('sends no role-dependent parameter of any kind', () => {
    createFixture();

    const req = expectQueueRequest();
    expect(req.request.params.has('userId')).toBe(false);
    expect(req.request.params.has('resourceId')).toBe(false);

    req.flush(page([]));
  });

  it('renders a row per waiting request, with the facts a decision needs', () => {
    createFixture();

    expectQueueRequest().flush(
      page([
        booking({ id: 'b1', resourceName: 'Conference Room A', userName: 'Member Two', quantity: 3 }),
        booking({ id: 'b2', resourceName: '3D Printer', userName: 'Member One' }),
      ]),
    );

    expect(cards()).toHaveLength(2);

    const first = cards()[0].textContent ?? '';
    expect(first).toContain('Conference Room A');
    expect(first).toContain('Requested by Member Two');
    expect(first).toContain('3 units');
    expect(first).toContain('09:00');
  });

  // The recurrence marker is rendered, not merely computed — this is the kind of
  // assertion that would have caught this package's earlier owner-found bugs,
  // where the signal was right and the screen was not.
  it('shows the series badge only on an occurrence of a series', () => {
    createFixture();

    expectQueueRequest().flush(
      page([
        booking({ id: 'b1', recurrenceRuleId: 'rule-1' }),
        booking({ id: 'b2', recurrenceRuleId: null }),
      ]),
    );

    expect(cards()[0].querySelector('.recurring-badge')).not.toBeNull();
    expect(cards()[1].querySelector('.recurring-badge')).toBeNull();
  });

  it('links each row to the booking it is about', () => {
    createFixture();

    expectQueueRequest().flush(page([booking({ id: 'booking-42' })]));

    const link = cards()[0].querySelector('a.detail-link');
    expect(link?.getAttribute('href')).toBe('/bookings/booking-42');
  });

  // Empty is the ordinary case for this screen, so it gets real copy rather than
  // the shrug a list's empty state usually carries — asserted so a later edit
  // cannot quietly flatten it back.
  it('says nothing is waiting, in a sentence, when the queue is empty', () => {
    createFixture();

    expectQueueRequest().flush(page([]));

    expect(root().querySelector('.state-message--empty')).not.toBeNull();
    expect(text()).toContain('Nothing is waiting for you');
    expect(cards()).toHaveLength(0);
  });

  it('offers a retry when the queue cannot be loaded', () => {
    createFixture();

    expectQueueRequest().flush(
      { reasonCode: 'ServerError' },
      { status: 500, statusText: 'Server Error' },
    );

    expect(text()).toContain("Couldn't load the approval queue");

    const retry = root().querySelector<HTMLButtonElement>('.retry-button');
    expect(retry).not.toBeNull();

    retry!.click();
    expectQueueRequest().flush(page([booking()]));

    expect(cards()).toHaveLength(1);
  });

  describe('paging', () => {
    it('hides the pager when everything fits on one page', () => {
      createFixture();

      expectQueueRequest().flush(page([booking()]));

      expect(root().querySelector('.pagination')).toBeNull();
    });

    it('walks forward and back, asking for the right page each time', () => {
      createFixture();

      expectQueueRequest().flush(
        page([booking({ id: 'p1' })], { page: 1, totalCount: 25, totalPages: 2, hasNextPage: true }),
      );

      root().querySelectorAll<HTMLButtonElement>('.pagination-button')[1].click();

      const forward = expectQueueRequest();
      expect(forward.request.params.get('page')).toBe('2');
      forward.flush(
        page([booking({ id: 'p2' })], {
          page: 2,
          totalCount: 25,
          totalPages: 2,
          hasPreviousPage: true,
        }),
      );

      root().querySelectorAll<HTMLButtonElement>('.pagination-button')[0].click();

      const back = expectQueueRequest();
      expect(back.request.params.get('page')).toBe('1');
      back.flush(
        page([booking({ id: 'p1' })], { page: 1, totalCount: 25, totalPages: 2, hasNextPage: true }),
      );

      expect(cards()).toHaveLength(1);
    });

    it('disables Previous on the first page', () => {
      createFixture();

      expectQueueRequest().flush(
        page([booking()], { page: 1, totalCount: 25, totalPages: 2, hasNextPage: true }),
      );

      const buttons = root().querySelectorAll<HTMLButtonElement>('.pagination-button');
      expect(buttons[0].disabled).toBe(true);
      expect(buttons[1].disabled).toBe(false);
    });
  });

  // ---- Deciding from the queue (step 4) ----------------------------------
  describe('deciding', () => {
    function panelButton(card: HTMLElement, fragment: string): HTMLButtonElement {
      return (Array.from(card.querySelectorAll('app-decision-panel button')) as HTMLButtonElement[])
        .find((b) => b.textContent?.includes(fragment))!;
    }

    it('offers a decision on every waiting row', () => {
      createFixture();

      expectQueueRequest().flush(page([booking({ id: 'b1' }), booking({ id: 'b2' })]));

      expect(root().querySelectorAll('app-decision-panel')).toHaveLength(2);
    });

    // The decided row leaves from the response rather than from a refetch — a
    // list being worked down should not reshuffle under the approver.
    it('drops a decided row without refetching the page', () => {
      createFixture();

      expectQueueRequest().flush(
        page([booking({ id: 'b1' }), booking({ id: 'b2' })], { totalCount: 2 }),
      );

      panelButton(cards()[0], 'Approve').click();
      fixture.detectChanges();
      panelButton(cards()[0], 'Yes, approve it').click();

      httpMock.expectOne(`${API}/bookings/b1/approve`).flush({
        id: 'b1',
        status: 'Confirmed',
        decidedByUserId: 'approver-1',
        decidedAtUtc: '2026-09-21T11:00:00Z',
      });

      // No second list request: `httpMock.verify()` in afterEach would fail if
      // one had gone out, and the row is gone regardless.
      expect(cards()).toHaveLength(1);
    });

    // A refusal leaves the row where it is — nothing was decided, so nothing
    // should disappear.
    it('keeps the row when the decision is refused', () => {
      createFixture();

      expectQueueRequest().flush(page([booking({ id: 'b1' })]));

      panelButton(cards()[0], 'Approve').click();
      fixture.detectChanges();
      panelButton(cards()[0], 'Yes, approve it').click();

      httpMock.expectOne(`${API}/bookings/b1/approve`).flush(
        {
          title: 'The request was rejected by a rule.',
          status: 409,
          reasonCode: 'SlotUnavailable',
          correlationId: 'test-correlation-id',
        },
        { status: 409, statusText: 'Conflict' },
      );

      expect(cards()).toHaveLength(1);
      expect(text()).toContain('taken while the request was waiting');
    });
  });
});
