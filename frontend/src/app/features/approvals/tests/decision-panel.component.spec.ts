import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import {
  DecisionMade,
  DecisionPanelComponent,
} from '../components/decision-panel/decision-panel.component';

const API = 'http://localhost:5270';

describe('DecisionPanelComponent', () => {
  let httpMock: HttpTestingController;
  let fixture: ComponentFixture<DecisionPanelComponent>;
  let decided: DecisionMade[];

  function createFixture(bookingId = 'b1') {
    TestBed.configureTestingModule({
      imports: [DecisionPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(DecisionPanelComponent);
    fixture.componentRef.setInput('bookingId', bookingId);

    decided = [];
    fixture.componentInstance.decided.subscribe((d) => decided.push(d));

    fixture.detectChanges();
    return fixture;
  }

  function root(): HTMLElement {
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  function text(): string {
    return (root().textContent ?? '').replace(/\s+/g, ' ').trim();
  }

  function click(fragment: string): void {
    (Array.from(root().querySelectorAll('button')) as HTMLButtonElement[])
      .find((b) => b.textContent?.includes(fragment))!
      .click();
    fixture.detectChanges();
  }

  function noteBox(): HTMLTextAreaElement | null {
    return root().querySelector('textarea');
  }

  // A real ProblemDetails, not just a reason code: `isProblemDetails` requires
  // `title` too, and a fixture missing it falls through to the generic message —
  // which is how these tests first passed the wrong assertion rather than the
  // right one.
  function problem(reasonCode: string, errors?: Record<string, string[]>) {
    return {
      title: 'The request was rejected by a rule.',
      status: 0,
      reasonCode,
      correlationId: 'test-correlation-id',
      ...(errors ? { errors } : {}),
    };
  }

  function decisionResponse(status: 'Confirmed' | 'Rejected') {
    return {
      id: 'b1',
      status,
      decidedByUserId: 'approver-1',
      decidedAtUtc: '2026-09-21T11:00:00Z',
    };
  }

  afterEach(() => {
    httpMock.verify();
  });

  // Neither decision fires straight off its button. Approving is not reversible
  // by the approver — it confirms a booking and tells the requester — and the
  // confirm step is also what carries the note box.
  it('confirms before deciding, rather than acting on the first click', () => {
    createFixture();

    click('Approve');

    httpMock.expectNone(`${API}/bookings/b1/approve`);
    expect(text()).toContain('Approve');
    expect(noteBox()).not.toBeNull();
  });

  it('approves through the approve endpoint, with the note it was given', () => {
    createFixture();
    click('Approve');

    noteBox()!.value = 'Fine by me.';
    noteBox()!.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    click('Yes, approve it');

    const req = httpMock.expectOne(`${API}/bookings/b1/approve`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ note: 'Fine by me.' });

    req.flush(decisionResponse('Confirmed'));

    expect(text()).toContain('Approved');
    expect(decided).toEqual([{ bookingId: 'b1', decision: 'approve', status: 'Confirmed' }]);
  });

  it('rejects through its own endpoint, never approve with a flag', () => {
    createFixture();
    click('Reject');
    click('Yes, reject it');

    httpMock.expectNone(`${API}/bookings/b1/approve`);
    httpMock.expectOne(`${API}/bookings/b1/reject`).flush(decisionResponse('Rejected'));

    expect(text()).toContain('Rejected');
    expect(decided).toEqual([{ bookingId: 'b1', decision: 'reject', status: 'Rejected' }]);
  });

  // "No note" is a real answer; a box someone tabbed through should not become
  // one made of spaces.
  it('sends a null note rather than whitespace', () => {
    createFixture();
    click('Approve');

    noteBox()!.value = '   ';
    noteBox()!.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    click('Yes, approve it');

    const req = httpMock.expectOne(`${API}/bookings/b1/approve`);
    expect(req.request.body).toEqual({ note: null });
    req.flush(decisionResponse('Confirmed'));
  });

  // The hardening pass's rule, inherited: everything feeding a submit is
  // disabled while it is in flight, so a second click cannot produce a second
  // decision on a path where a repeat is never safe.
  it('disables its controls while a decision is in flight', () => {
    createFixture();
    click('Approve');
    click('Yes, approve it');

    const buttons = Array.from(root().querySelectorAll('button')) as HTMLButtonElement[];
    expect(buttons.every((b) => b.disabled)).toBe(true);
    expect(noteBox()!.disabled).toBe(true);
    expect(text()).toContain('Saving');

    httpMock.expectOne(`${API}/bookings/b1/approve`).flush(decisionResponse('Confirmed'));
  });

  describe('when the decision is refused', () => {
    // **AC-5.** The slot went while the request sat in the queue —
    // dbo.ApproveBooking re-runs the capacity check under its lock. This must
    // read as its own event: an approver shown a generic failure would
    // reasonably conclude the system was broken.
    it('explains a since-taken slot as its own outcome, not a generic failure', () => {
      createFixture();
      click('Approve');
      click('Yes, approve it');

      httpMock
        .expectOne(`${API}/bookings/b1/approve`)
        .flush(problem('SlotUnavailable'), { status: 409, statusText: 'Conflict' });

      expect(text()).toContain('taken while the request was waiting');
      expect(text()).toContain('still pending');
      expect(decided).toEqual([]);
    });

    it('reads CapacityExceeded the same way — the slot is what is gone', () => {
      createFixture();
      click('Approve');
      click('Yes, approve it');

      httpMock
        .expectOne(`${API}/bookings/b1/approve`)
        .flush(problem('CapacityExceeded'), { status: 409, statusText: 'Conflict' });

      expect(text()).toContain('taken while the request was waiting');
    });

    // The concurrent-decision case the Phase 6 demo forces: two approvers, one
    // loses. It must be a clear "already decided", not a silent failure.
    it('says a request has already been decided', () => {
      createFixture();
      click('Approve');
      click('Yes, approve it');

      httpMock
        .expectOne(`${API}/bookings/b1/approve`)
        .flush(
          problem('BookingNotPending'),
          { status: 422, statusText: 'Unprocessable Entity' },
        );

      expect(text()).toContain('already been decided');
      expect(decided).toEqual([]);
    });

    it('explains a blackout that appeared since the request was made', () => {
      createFixture();
      click('Approve');
      click('Yes, approve it');

      httpMock
        .expectOne(`${API}/bookings/b1/approve`)
        .flush(
          problem('BlackoutPeriod'),
          { status: 422, statusText: 'Unprocessable Entity' },
        );

      expect(text()).toContain('blocked out over this time');
    });

    // A decision is deliberately not idempotent, and approve can also lose the
    // capacity race again — so an unknown outcome is reported as unknown and
    // never as something to try again.
    it('never offers a retry when the outcome is unknown', () => {
      createFixture();
      click('Approve');
      click('Yes, approve it');

      httpMock
        .expectOne(`${API}/bookings/b1/approve`)
        .flush(null, { status: 500, statusText: 'Server Error' });

      expect(text()).toContain('may or may not have been recorded');

      const labels = (Array.from(root().querySelectorAll('button')) as HTMLButtonElement[]).map(
        (b) => b.textContent?.trim() ?? '',
      );
      expect(labels.some((l) => /try again|retry/i.test(l))).toBe(false);
    });

    // A field message belongs under its control, and must not outlive an edit
    // to that control — the hardening pass's third rule.
    it('puts an over-long note message on the note box, and clears it on edit', () => {
      createFixture();
      click('Approve');
      click('Yes, approve it');

      httpMock.expectOne(`${API}/bookings/b1/approve`).flush(
        problem('ValidationFailed', { Note: ['The length of Note must be 500 characters or fewer.'] }),
        { status: 400, statusText: 'Bad Request' },
      );

      expect(root().querySelector('.field-error')).not.toBeNull();
      expect(noteBox()!.getAttribute('aria-invalid')).toBe('true');

      noteBox()!.value = 'shorter';
      noteBox()!.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      expect(root().querySelector('.field-error')).toBeNull();
    });

    // The panel stays usable after a refusal — the approver may well want to
    // reject what they could not approve, which the AC-5 copy explicitly says
    // is still possible.
    it('leaves the panel usable so the approver can still reject', () => {
      createFixture();
      click('Approve');
      click('Yes, approve it');

      httpMock
        .expectOne(`${API}/bookings/b1/approve`)
        .flush(problem('SlotUnavailable'), { status: 409, statusText: 'Conflict' });

      click('Back');
      click('Reject');
      click('Yes, reject it');

      httpMock.expectOne(`${API}/bookings/b1/reject`).flush(decisionResponse('Rejected'));

      expect(decided).toEqual([{ bookingId: 'b1', decision: 'reject', status: 'Rejected' }]);
    });
  });
});
