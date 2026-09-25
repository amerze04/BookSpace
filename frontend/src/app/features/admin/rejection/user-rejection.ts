import { Rejection, RejectionCopy, RejectionDialect, describeRejection } from '../../../core/http/rejection';

// Every way `POST /users` can refuse, in an administrator's vocabulary. The
// ninth dialect over `core/http/rejection.ts`'s machinery — a new one binding
// its own field union, not a widening of `ResourceFieldName`, which is the rule
// admin console phase 2 set when it made the machinery generic.
//
// The codes, read off `UsersController`, `CreateUserCommandRequestValidator`
// and `UserRepository.SaveChangesAsync` rather than inferred:
//   400 ValidationFailed · 409 EmailAlreadyInUse.
//
// Deliberately absent: `UserNotFound` and `LastTenantAdmin` belong to the three
// writes on the user detail screen (phase 7), which will extend this dialect
// when it has controls for them. A code this form cannot receive would be as
// wrong here as a missing one — the same reason `resource-rejection.ts` leaves
// out the approvers and availability codes.
//
// `ResourceNotFound` is handled by the shared machinery and cannot arrive on
// this endpoint, so `resourceNotFound` is always false here. It stays on the
// shared shape for the reason `core/http/rejection.ts` gives; this screen
// simply never reads it.

// Keyed by this form's own control names.
export type UserFieldName = 'email' | 'fullName';

export type UserRejection = Rejection<UserFieldName>;

const USER_COPY: Record<string, RejectionCopy<UserFieldName>> = {
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
// every reader. Both properties of CreateUserRequest are mapped, because this
// form renders a control for both.
const USER_BACKEND_FIELDS: Record<string, UserFieldName> = {
  Email: 'email',
  FullName: 'fullName',
};

const USER_DIALECT: RejectionDialect<UserFieldName> = {
  copy: USER_COPY,
  backendFields: USER_BACKEND_FIELDS,

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
  return describeRejection(error, USER_DIALECT);
}
