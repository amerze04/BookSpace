import { Rejection, RejectionCopy, RejectionDialect, describeRejection } from '../../../core/http/rejection';

// Every way `POST /users` and the three user-detail writes can refuse, in an
// administrator's vocabulary. The ninth dialect over `core/http/rejection.ts`'s
// machinery — one binding its own field union, not a widening of
// `ResourceFieldName`, which is the rule admin console phase 2 set when it made
// the machinery generic.
//
// **Two dialect objects in this one file, not one**, matching
// `cancel-rejection.ts`'s own split between a booking cancel and a series
// cancel: creation and the detail-screen writes share `UserFieldName`, but
// their generic/unmapped/unknown-outcome wording has to differ — "the account
// could not be created" would be actively misleading on a screen that is
// deactivating somebody, not making them.
//
// The codes, read off `UsersController` and its handlers/validators rather
// than inferred:
//   `POST /users`:            400 ValidationFailed · 409 EmailAlreadyInUse.
//   deactivate / reactivate / replace roles:
//                              400 ValidationFailed (roles) · 404 UserNotFound
//                              · 422 LastTenantAdmin (deactivate and replace
//                              roles only — reactivate can only grow the
//                              active-admin set, but shares the dialect anyway:
//                              two near-identical maps kept in step would be
//                              the more likely source of a wrong message than
//                              one map with an unreachable entry, the same call
//                              approval-rejection.ts makes for approve/reject).
//
// `ResourceNotFound` is handled by the shared machinery and cannot arrive on
// any of these endpoints, so `resourceNotFound` is always false here. It stays
// on the shared shape for the reason `core/http/rejection.ts` gives; this
// feature simply never reads it. `UserNotFound` is a **different** code and is
// not special-cased the same way — it goes through the ordinary copy lookup
// below, matching how `BlackoutPeriodNotFound` and `BookingNotFound` are
// handled in their own dialects. The user detail screen's *initial* load uses
// a plain `error.status === 404` check instead of this dialect at all, mirroring
// `BookingDetailComponent`'s own load — a dialect describes a refused *write*,
// and loading the screen is not one.

// Keyed by every control across both forms. `roles` is phase 7's: FluentValidation
// reports `PUT /users/{id}/roles`' rule failures (empty set, duplicate,
// non-enum) under the C# property `Roles`.
export type UserFieldName = 'email' | 'fullName' | 'roles';

export type UserRejection = Rejection<UserFieldName>;

// ---------------------------------------------------------------------------
// POST /users — create and invite (phase 3)
// ---------------------------------------------------------------------------

const CREATE_COPY: Record<string, RejectionCopy<UserFieldName>> = {
  // **The one refusal an administrator will actually meet, and the one whose
  // wording is a security property rather than a matter of tone.**
  //
  // Decision `0010` makes an email address identify exactly one account across
  // the whole platform, so a collision may be with somebody in an organization
  // this administrator cannot see — and decision `0030` settles that the server
  // answers identically either way, precisely so `POST /users` cannot be used
  // to discover which. The copy has to hold that line: it says the address is
  // taken, and does not say by whom, where, or whether they are "in your
  // organization", because none of those is something the client is told and
  // guessing would be worse than silence.
  //
  // Pointed at the email control rather than the form, because that is the one
  // thing the administrator can change.
  EmailAlreadyInUse: {
    field: 'email',
    message: 'That email address already has an account. Check the directory, or use a different address.',
  },
};

// FluentValidation reports under the C# property name (`problem-details.ts`
// records the PascalCase shape). One translation, here, rather than one at
// every reader. Only the two `CreateUserRequest` properties are mapped —
// `Roles` never arrives on this endpoint, so it is left for the detail
// dialect below.
const CREATE_BACKEND_FIELDS: Record<string, UserFieldName> = {
  Email: 'email',
  FullName: 'fullName',
};

const CREATE_DIALECT: RejectionDialect<UserFieldName> = {
  copy: CREATE_COPY,
  backendFields: CREATE_BACKEND_FIELDS,

  unmappedField: {
    message: 'The account could not be created — one of the values sent was not valid.',
    recheckAvailability: false,
  },

  genericMessage: 'This account could not be created.',

  // **No retry offered, and this is the strictest case in the console.**
  //
  // `POST /users` has no idempotency key, so repeating it after an unknown
  // outcome either creates a second account or — more likely, since the email
  // is unique platform-wide — comes back 409 and tells the administrator the
  // address is taken *by the account they just created*, which reads as a
  // different failure entirely.
  //
  // Worse, the first attempt may already have sent the invitation. There is no
  // way to re-issue one and no way to withdraw one, so a duplicate attempt can
  // leave two accounts, one of which nobody will ever activate.
  //
  // So the instruction is to go and look, and the directory is where looking
  // works.
  unknownOutcomeMessage:
    'We could not confirm whether the account was created. Check the user list before trying '
    + 'again — if it is there, the invitation has already gone out.',
};

export function describeUserRejection(error: unknown): UserRejection {
  return describeRejection(error, CREATE_DIALECT);
}

// ---------------------------------------------------------------------------
// The user detail screen (phase 7): deactivate, reactivate, replace roles
// ---------------------------------------------------------------------------

const DETAIL_COPY: Record<string, RejectionCopy<UserFieldName>> = {
  // Mid-edit 404: this account left this administrator's reach between loading
  // the screen and saving. Users are never deleted (CLAUDE.md §4.5), so in
  // practice this means a stale link or the AC-4 answer for an id that was
  // never this tenant's — either way there is nothing left here to save.
  UserNotFound: {
    message: 'This account could not be found. Go back to the directory and try again.',
  },

  // Decision `0031`. Covers both doors — deactivating the account and taking
  // the TenantAdmin role off it — with one message rather than two, because
  // the fix is the same either way and the wording has to work regardless of
  // which control triggered it. **Rendered as something to act on, not a
  // wall**: the way out is named, not just the refusal.
  LastTenantAdmin: {
    message:
      'This is the only active administrator your organization has left, so this change is '
      + 'refused. Give someone else the administrator role first, then try again.',
  },
};

// `Roles` is the only body field any of these three endpoints can name —
// deactivate and reactivate take no body at all.
const DETAIL_BACKEND_FIELDS: Record<string, UserFieldName> = {
  Roles: 'roles',
};

const DETAIL_DIALECT: RejectionDialect<UserFieldName> = {
  copy: DETAIL_COPY,
  backendFields: DETAIL_BACKEND_FIELDS,

  unmappedField: {
    message: "This change couldn't be saved — the request wasn't valid.",
    recheckAvailability: false,
  },

  genericMessage: 'This change could not be saved.',

  // Deactivate and reactivate are idempotent, and replacing the whole role set
  // with the same set twice is harmless — but the dialect has no way to know
  // which of the three actions produced an unknown outcome, so it gives the
  // conservative answer rather than a wrong one: check first, then decide
  // whether to repeat it.
  unknownOutcomeMessage:
    'We could not confirm whether that change was saved. Reload this person before trying again.',
};

export function describeUserDetailRejection(error: unknown): UserRejection {
  return describeRejection(error, DETAIL_DIALECT);
}
