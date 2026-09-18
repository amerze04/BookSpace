import {
  BookingFieldName,
  BookingRejection,
  RejectionCopy,
  RejectionDialect,
  describeRejection,
} from './booking-rejection';

// Every way `POST /recurrence-rules` can refuse, in the recurring form's own
// vocabulary. The machinery is `booking-rejection.ts`'s — the status-0/5xx
// unknown-outcome rules, ResourceNotFound, the validation walk — and only the
// words and the field map are here.
//
// **Why this is not just the one-off map reused.** Before this pass a series
// went through `describeBookingRejection` whole, which knows three controls
// (title, quantity, duration) and treats every other named field as "the
// selected time is not valid — go back to availability and pick a slot again".
// A series has no selected slot at all: it names its own schedule. So a 400 on
// `OccurrenceCount` or `IntervalValue` — both of which this endpoint really
// does return — landed as a top-of-form message pointing at a screen that
// cannot change either value, while the control that holds it said nothing.
//
// The codes this endpoint can actually return, from the handler and validator
// (`CreateRecurrenceSeriesCommandRequestHandler`,
// `CreateRecurrenceSeriesCommandRequestValidator`):
//   ResourceNotFound (404); ResourceArchived, BookingDurationOutOfRange,
//   NoOccurrencesCreated (422); ValidationFailed (400). Plus
//   ConcurrencyConflict, which the stack can produce without the handler
//   naming it.
//
// **Codes deliberately absent, and this is the substance of the fix rather
// than an omission**: SlotUnavailable, CapacityExceeded, BlackoutPeriod,
// OutsideAvailability and BookingInThePast can never be the *series'* reason
// code. They are per-occurrence outcomes, carried in the FR-5.4 breakdown that
// `recurrence-outcome.ts` renders — and when every occurrence hits one, the
// refusal arrives as NoOccurrencesCreated with that same breakdown attached,
// which `parseSeriesRefusal` takes before this file is ever reached.
const RECURRENCE_COPY: Record<string, RejectionCopy> = {
  ResourceArchived: {
    message: 'This resource has been archived, so no new series can be booked against it.',
  },

  // The one 422 with controls to blame. Every occurrence shares the series'
  // nominal length, so the handler asks `Resource.AllowsBookingDuration` once
  // — and the two controls that decide it are From and To.
  BookingDurationOutOfRange: {
    field: 'duration',
    message: "This series' length is outside what the resource allows.",
  },

  // Re-checking availability is not the way out: the series names its own
  // schedule, so there is no picked slot for another screen to re-pick.
  ConcurrencyConflict: {
    message: 'This resource changed while the series was being created. Try again.',
  },
};

// FluentValidation reports under the C# property name, so this is
// `CreateRecurrenceSeriesCommandRequest`'s own property list mapped onto the
// controls that hold each one.
//
// Both times map to `times`, which is where the form already puts "the end
// time must be after the start time" — the server's own
// `LocalEndTime > LocalStartTime` rule is the same sentence, and it belongs
// against the pair rather than against one half of it.
//
// ResourceId and IdempotencyKey are deliberately absent: neither is a control
// a member can see or edit, so a failure on one is a top-of-form message.
const RECURRENCE_BACKEND_FIELDS: Record<string, BookingFieldName> = {
  Title: 'title',
  Quantity: 'quantity',
  StartDate: 'startDate',
  IntervalValue: 'interval',
  LocalStartTime: 'times',
  LocalEndTime: 'times',
  OccurrenceCount: 'occurrenceCount',
  EndDate: 'endDate',
};

const RECURRENCE_DIALECT: RejectionDialect = {
  copy: RECURRENCE_COPY,
  backendFields: RECURRENCE_BACKEND_FIELDS,
  unmappedField: {
    // Not "go back to availability": nothing on that screen feeds this
    // request. The fields are all on this form, so the recovery is here.
    message: 'This series could not be created. Check the details above and try again.',
    recheckAvailability: false,
  },
  genericMessage: 'This series could not be created.',

  // The recurring endpoint *does* carry an idempotency key, so a same-page
  // retry of this exact attempt is safe — but only that, and only while this
  // page is still holding the key (the component owns that; see
  // `BookingComponent.canRetrySeries`). Reload the page or change the form and
  // the honest answer is the same one the one-off path gives.
  unknownOutcomeMessage:
    'Your series may have been created — we could not confirm it. Check your calendar before '
    + 'trying again, so you do not create the same series twice.',
};

export function describeRecurrenceRejection(error: unknown): BookingRejection {
  return describeRejection(error, RECURRENCE_DIALECT);
}

// The controls the recurring half owns. Used by the component to decide when a
// server message has been superseded by an edit, and to gate the submit on
// server feedback the client's own guards would not have produced.
export const RECURRENCE_FIELD_NAMES: readonly BookingFieldName[] = [
  'startDate',
  'interval',
  'times',
  'duration',
  'occurrenceCount',
  'endDate',
];
