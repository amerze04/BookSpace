# 0029 — User provisioning and invitation delivery

## Status
Decided and implemented 2026-09-23/24, user management phases 1–3.

## Context

`User` has had `AddRole`, `Deactivate` and `Reactivate` since WP-1 and nothing
outside `SeedData` has ever called them: a tenant's population was whatever the
seed created. Closing that means an administrator has to be able to add a
colleague — and the colleague needs a credential the administrator never sees,
since `User`'s own header records that there is no self-registration and
`PasswordHash` had no setter at all.

That forces two questions this codebase had no answer for.

**How does an invitation reach somebody with no account?** PRD §11 assumes a
transactional email provider; none was ever wired. Grepped 2026-09-23 for
`IEmailSender`, `SendEmail`, `SmtpClient` and `SendGrid` across `backend/src` —
zero hits. The owner's question when choosing this package was whether the email
path could be built without the three background jobs (CLAUDE.md §7), which are
unbuilt and belong to a later work package.

**What does the recipient act on?** An email with nothing actionable in it
creates somebody who can never sign in, which is not a smaller version of the
feature but a broken one.

## Decision

### 1. An invitation is sent synchronously and does not use `Notifications`

`IEmailSender` is called directly by the create-user handler, inside the request
the administrator is waiting on.

`Notifications` is a **scheduler, not a mailbox**. Read off `Notification.cs`: a
row carries `SendAtUtc`, `SentAtUtc`, `Attempts` and `LastError`, and is anchored
by `BookingId` or `RecurrenceRuleId` (+ `OccurrenceDate`). It carries no address,
no subject and no body — content is composed at dispatch time by a job that does
not exist. All three factory methods are booking- or series-shaped.

Routing an invitation through it would need **all** of: a new `NotificationKind`;
a second widening of `CK_Notifications_HasContext` (decision `0026` widened it
once, for a row anchored to a series — a row anchored to a *user* shares none of
the table's semantics); a meaningless idempotency key, since
`UQ_Notifications_Once` is `(BookingId, RecurrenceRuleId, OccurrenceDate,
RecipientUserId, Kind)` and an invitation has neither of the first two; and the
reminder-dispatch job itself.

Against which: an invitation has nothing to schedule. It is sent *now*, as a
direct result of a request. So the three jobs stay entirely out of this package —
and this package builds the `IEmailSender` they will later need.

### 2. A delivery failure is a return value, not an exception

`IEmailSender.SendAsync` answers `EmailSendResult { Delivered, FailureDetail }`,
and implementations never throw for a transport failure — only the caller's own
cancellation propagates.

Creating a colleague must not fail because a third party is down. Had the
failure been an exception, the handling would be a `try/catch` a future caller
can forget; as a result type it is in the signature and has to be discarded on
purpose. `POST /users` therefore returns **201 with the activation link even
when the send failed**, plus `invitationEmailSent: false`. The administrator is
standing there and can pass the link on by chat or in person — which is the only
recovery path there is, because re-issuing an invitation is out of scope.

### 3. Two senders, chosen by one configuration switch

SMTP over any provider's relay (MailKit), and a development sink that writes each
message to disk as an `.eml`. The sink is **not a test double** — it is how this
runs on a machine with no provider account, which is every machine today. Both
go through one `MimeMessageFactory`, so what lands on disk is what would have
gone on the wire.

SMTP rather than a vendor SDK because there is no provider account yet and every
transactional provider offers a relay taking the API key as the password; picking
a vendor now would be a dependency taken before the decision it depends on.

`EmailOptions.DeliveryMode` defaults to **`Smtp`**, so an omitted or misspelled
section fails the boot. A `DevelopmentSink` default would have written production
invitations to a directory nobody reads.

### 4. The activation token is shaped like a refresh token

A single-use, expiring, hashed token in its own `ActivationTokens` table: SHA-256
of a 256-bit CSPRNG value, stored only as the hash, plaintext only in the email
and in the create response. That is decision `0011`'s shape reused rather than
reinvented, and `SecureToken` now holds the rule once for both token types —
two copies of a crypto rule is where drift is most expensive.

Single use is a **concurrency token**, not an `if`: `ConsumedAtUtc` is
`IsConcurrencyToken`, as `RefreshTokens.RevokedAtUtc` already is, so two
simultaneous redemptions cannot both set a password.

`ActivationTokens` has **no `OrgId`, no query filter and no RLS predicate**,
exactly like `RefreshTokens`. Activation runs before the user has ever signed in,
so CLAUDE.md §4.2's three mechanisms have nothing to act on; the token's own
secrecy is the access control.

### 5. `POST /auth/activate` is anonymous, rate-limited, and tells you nothing

Expired, already redeemed, never existed, user deactivated and organization
suspended all answer `401 InvalidActivationToken`, byte for byte. A
distinguishable response would make the endpoint an oracle for which invitations
are outstanding — the concern decision `0018` collapsed three approver reasons
into one code to avoid (AC-4). The password policy is validated *before* the
token is looked up, so a 400 cannot be used to probe whether a token is live.

It returns **204, not a session**. Minting one would mean re-implementing
login's FR-2.4 account-state gate in a second place, or signing somebody into a
suspended organization. The client holds the password it just set and can call
`POST /auth/login`.

## Consequences

- **The request now depends on a provider's latency.** Bounded by
  `Email:Smtp:TimeoutSeconds`, default 30s against MailKit's own two minutes.
- **Two of §4.2's three mechanisms had to be given an explicit write exemption.**
  Activation is the first operation that writes to a `Users` row from an
  unauthenticated request. RLS's *filter* predicate hides the row, so the UPDATE
  matches nothing — hence
  `IAuthenticationUserRepository.SaveChangesUnfilteredAsync`. And
  `ValidateTenantOwnership` threw when a browser attached an existing session's
  bearer token to the anonymous call, so it now stands down inside a
  `TenantBypassScope` — the same signal RLS already honours. Nothing widens: the
  scope is internal and only explicitly-named repository methods may enter it.
  Both were proven load-bearing by removal (8 of 16 integration tests fail
  without the first; exactly 1 without the second).
- **A password policy had to be invented.** `PasswordPolicy` is 12–128
  characters with no composition rules, following NIST SP 800-63B. Nothing in
  the PRD specifies one and FR-2.3 only says "hashed"; the maximum bounds PBKDF2
  work on an anonymous endpoint. Recorded as a judgement call, not a requirement.
- **`Activation:ActivationUrl` is required with no default**, like
  `Cors:AllowedOrigins`. The API cannot guess its frontend's origin and a guess
  would email links that lead nowhere.
- **The development sink's files are live credentials.**
  `backend/src/BookSpace.Api/sent-emails/` is gitignored, and the log line
  carries the subject and the path and nothing else — not the body, which holds
  a token, and not the recipient, which is a personal record. The file name
  deliberately carries nothing from the message: it held the recipient first,
  and since the log carries the path, that made the rule false as written.
- **Two MimeKit behaviours are relied on being wrong for us**, measured against
  4.18 rather than assumed: `MailboxAddress.TryParse("not-an-address")` returns
  true, and `new MailboxAddress(name, "")` succeeds with an empty address.
  `EmailAddressRules` is the one usable-address rule both the startup validator
  and `MimeMessageFactory` apply.
- **Nothing re-issues an invitation.** If the provider is down and the
  administrator closes the page, that person has no route into the system at
  all. The always-return-the-link rule mitigates it but depends on the admin
  acting in the moment. `POST /users/{id}/invitation` is the cheap close and is
  recorded as the owner's call, not assumed — see
  [`docs/user-management-plan.md`](../user-management-plan.md) §6.
- **One new package dependency, MailKit**, and one new reason code,
  `EmailAlreadyInUse` — see decision `0030` for what that code may and may not
  say.
