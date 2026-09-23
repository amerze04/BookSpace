import { Rejection, RejectionCopy, RejectionDialect, describeRejection } from '../../../core/http/rejection';

// Every way the blackout endpoints can refuse, in an administrator's
// vocabulary. The eighth and last dialect over `core/http/rejection.ts`.
//
// It covers create, edit **and delete**, which no other dialect in the app has
// had to do — the three share an audience and a vocabulary, and delete adds
// only `BlackoutPeriodNotFound` to what the other two can produce.
//
// The codes, read off `BlackoutPeriodsController` and the three handlers:
//   400 ValidationFailed · 404 ResourceNotFound · 404 BlackoutPeriodNotFound ·
//   422 ResourceArchived · 422 BlackoutPeriodElapsed ·
//   409 ConcurrencyConflict (global, and genuinely reachable here — the cascade
//   writes to Bookings, which carry a RowVersion).
//
// BlackoutPeriodElapsed is a **422**, not the 400 its "the value is wrong" feel
// suggests: ReasonCodes pairs it with ErrorKind.RuleViolation, because the
// request is well-formed and a rule refuses it. Confirmed against the running
// API rather than inferred from the shape of the message.

// Two real controls plus the reason box. Unlike the windows editor, a refusal
// here *can* be pointed at a field: the payload has exactly three, and the
// server names them.
export type BlackoutFieldName = 'startsAt' | 'endsAt' | 'reason';

export type BlackoutRejection = Rejection<BlackoutFieldName>;

const BLACKOUT_COPY: Record<string, RejectionCopy<BlackoutFieldName>> = {
  // **Decision `0019`, and the rule most likely to surprise.** A blackout whose
  // interval is entirely in the past is refused, because it blocks nothing and
  // could only reach backwards into bookings that have already happened. Note
  // it is about the *end*, not the start: a blackout that began this morning
  // and runs through tomorrow is perfectly legal, which is exactly what an
  // admin needs the moment a room floods.
  BlackoutPeriodElapsed: {
    field: 'endsAt',
    message:
      'This blackout has already finished, so it would block nothing. Its end has to be in the '
      + 'future — a blackout that already started is fine.',
  },

  // The blackout is gone. Either somebody else deleted it, or it never belonged
  // to this resource. A 404 rather than a 204 on delete is deliberate: the
  // endpoint cannot tell "already deleted" from "another tenant's" (AC-4).
  BlackoutPeriodNotFound: {
    message: 'This blackout no longer exists — it may already have been deleted. Reload the list.',
  },

  // FR-3.5, and the one rule the delete handler calls arguable in writing: an
  // archived resource accepts no writes at all, tidying included.
  ResourceArchived: {
    message:
      'This resource has been archived, so its blackouts can no longer be changed. Archiving '
      + 'cannot be undone.',
  },

  // Reachable in a way it is not on the other admin screens: the cascade writes
  // to `Bookings`, which carry their own RowVersion, so two admins blacking out
  // overlapping ranges at once means one of them loses.
  ConcurrencyConflict: {
    message:
      'A booking this blackout would have cancelled changed at the same moment. Nothing was '
      + 'saved — reload and try again.',
  },
};

// FluentValidation reports under the C# property name.
const BLACKOUT_BACKEND_FIELDS: Record<string, BlackoutFieldName> = {
  StartsAtUtc: 'startsAt',
  EndsAtUtc: 'endsAt',
  Reason: 'reason',
};

const BLACKOUT_DIALECT: RejectionDialect<BlackoutFieldName> = {
  copy: BLACKOUT_COPY,
  backendFields: BLACKOUT_BACKEND_FIELDS,

  unmappedField: {
    message: "The blackout couldn't be saved — one of the values sent wasn't valid.",
    recheckAvailability: false,
  },

  genericMessage: 'This blackout could not be saved.',

  // **No retry offered, and this is the one admin path where that matters
  // most.** A blackout write is not idempotent in any useful sense: repeating a
  // create makes a *second* blackout, and decision `0019` allows overlaps, so
  // nothing would refuse it. Worse, the first attempt may already have
  // cancelled other people's bookings — and the cascade is forwards-only, so
  // nothing undoes that. Reloading is the only safe instruction.
  unknownOutcomeMessage:
    'This blackout may or may not have been saved — we could not confirm it. Reload the list '
    + 'before trying again: saving twice would create a second blackout, and either one may '
    + 'already have cancelled bookings.',
};

export function describeBlackoutRejection(error: unknown): BlackoutRejection {
  return describeRejection(error, BLACKOUT_DIALECT);
}
