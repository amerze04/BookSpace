import { Component, computed, inject, input, output, signal } from '@angular/core';
import { BookingsService } from '../../../booking/services/bookings.service';
import {
  ApproveBookingResponse,
  MAX_DECISION_NOTE_LENGTH,
  RejectBookingResponse,
} from '../../../booking/models/booking.models';
import { BookingRejection } from '../../../booking/rejection/booking-rejection';
import { describeDecisionRejection } from '../../../booking/rejection/approval-rejection';

export type Decision = 'approve' | 'reject';

// What a decision produced, for whichever screen is hosting the panel: the
// queue drops the row, the detail screen re-reads the booking. The panel does
// neither itself — it has no idea what it is embedded in, and a component that
// navigated or refetched on its owner's behalf could not be used by two screens
// that want different things.
export interface DecisionMade {
  bookingId: string;
  decision: Decision;
  status: ApproveBookingResponse['status'];
}

// WP-7 Phase 6 step 4 — approve and reject, FR-7.1–FR-7.5 and AC-5.
//
// **One component, two hosts** (owner's call, 2026-09-21): the queue row for
// the quick decision, and the booking detail screen for the one an approver
// wants to read properly first. Building it twice would mean two copies of the
// AC-5 wording, the in-flight rules and the no-retry rule, and the first of
// those to drift would be the one nobody was looking at.
//
// **It lives under `approvals/` though `booking/` owns the endpoint**, matching
// how the calendar reads through `booking/`'s service: the *contract* belongs
// with the aggregate, the *screen* belongs with the job it does. Deciding is an
// approver's job wherever it is rendered.
//
// Three rules inherited from the 2026-09-17 hardening pass and not
// re-litigated here: everything feeding a submit is disabled while it is in
// flight, the outcome renders from a snapshot of what was submitted rather than
// from live state, and a server field message outranks a client one until its
// control is edited.
//
// **Nothing on this path ever offers a retry.** A decision is deliberately not
// idempotent — a second call answers `422 BookingNotPending` — and on the
// approve side a repeat could lose the capacity race a second time. An unknown
// outcome is reported as unknown and the approver is sent to look.
@Component({
  selector: 'app-decision-panel',
  imports: [],
  templateUrl: './decision-panel.component.html',
  styleUrl: './decision-panel.component.scss',
})
export class DecisionPanelComponent {
  private readonly bookingsService = inject(BookingsService);

  readonly bookingId = input.required<string>();

  // Host-supplied wording for the confirm step, so a queue row can name the
  // resource ("Approve the 3D Printer request?") without this component having
  // to fetch anything it was not given.
  readonly subject = input<string>('this request');

  readonly decided = output<DecisionMade>();

  protected readonly maxNoteLength = MAX_DECISION_NOTE_LENGTH;

  // Which decision is being confirmed, if any. A mode rather than a boolean
  // because approve and reject are different acts with different consequences —
  // the same reasoning the cancel screen's `confirmMode` uses — and because the
  // note box belongs to whichever one is open.
  protected readonly confirmMode = signal<Decision | null>(null);
  protected readonly note = signal('');
  protected readonly submitting = signal(false);

  // The outcome, rendered from what came back rather than from form state.
  protected readonly outcome = signal<DecisionMade | null>(null);
  protected readonly rejection = signal<BookingRejection | null>(null);

  // Cleared when the note is edited, so a server message about the note does
  // not sit under a box whose contents have since changed — the hardening
  // pass's third rule.
  protected readonly noteError = computed(() => this.rejection()?.fieldMessages.reason ?? null);

  protected readonly confirmHeading = computed(() =>
    this.confirmMode() === 'reject'
      ? `Reject ${this.subject()}?`
      : `Approve ${this.subject()}?`,
  );

  protected readonly confirmActionLabel = computed(() =>
    this.confirmMode() === 'reject' ? 'Yes, reject it' : 'Yes, approve it',
  );

  // The note is optional on both endpoints. It is worth asking for on a
  // rejection and merely allowed on an approval, which is a difference in
  // prompting rather than in validation — the server caps both at 500 and
  // requires neither.
  protected readonly noteLabel = computed(() =>
    this.confirmMode() === 'reject'
      ? 'Why is this being rejected? (optional, but the requester will see it)'
      : 'Add a note (optional — the requester will see it)',
  );

  protected startConfirming(decision: Decision): void {
    this.confirmMode.set(decision);
    this.note.set('');
    this.rejection.set(null);
  }

  protected cancelConfirming(): void {
    this.confirmMode.set(null);
    this.note.set('');
    this.rejection.set(null);
  }

  protected onNoteInput(event: Event): void {
    this.note.set((event.target as HTMLTextAreaElement).value);

    // Drop a stale server message about a box that has just changed.
    if (this.rejection()?.fieldMessages.reason) {
      this.rejection.set(null);
    }
  }

  protected submit(): void {
    const decision = this.confirmMode();
    if (decision === null || this.submitting()) {
      return;
    }

    this.submitting.set(true);
    this.rejection.set(null);

    // Trimmed to null rather than sent as whitespace: "no note" is a real
    // answer and a box someone tabbed through should not become one.
    const note = this.note().trim() || null;
    const bookingId = this.bookingId();

    const request$ =
      decision === 'approve'
        ? this.bookingsService.approve(bookingId, { note })
        : this.bookingsService.reject(bookingId, { note });

    request$.subscribe({
      next: (response: ApproveBookingResponse | RejectBookingResponse) => {
        const made: DecisionMade = { bookingId, decision, status: response.status };

        this.submitting.set(false);
        this.confirmMode.set(null);
        this.outcome.set(made);
        this.decided.emit(made);
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        this.rejection.set(describeDecisionRejection(error));
      },
    });
  }
}
