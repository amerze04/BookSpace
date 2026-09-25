# User management — provisioning, roles and account status

**Owner-initiated, not a mentor work package.** Planned 2026-09-23, immediately
after the admin console closed, because it is the largest remaining gap in the
application and WP-8 has not been issued. Same standing as the admin console and
the 2026-09-15 hardening pass: its own section in CLAUDE.md §12, not a WP
number, because §12's rule is that the roadmap mirrors the packages the mentor
issues rather than an invented build order.

---

## Status

**In progress.** Eight phases, each built in one go rather than split
into separately reviewable steps — the shape the owner settled for the admin
console on 2026-09-22 and which held up across all seven of its phases.

| Phase | State |
|---|---|
| 1 — `IEmailSender`, configuration, development sink | **Done 2026-09-23** |
| 2 — Activation tokens, `User.SetPassword`, `POST /auth/activate` | **Done 2026-09-24** |
| 3 — `POST /users` — create and invite | **Done 2026-09-24** |
| 4 — The directory read — widening `GET /users` | **Done 2026-09-24** |
| 5 — Deactivate, reactivate, roles, and the last-admin guard | **Done 2026-09-24** |
| 6 — Frontend: the user directory and the create form | **Done 2026-09-25** |
| **6b — Frontend: the activation screen** | **Done 2026-09-25** |
| 7 — Frontend: the user detail screen | **Done 2026-09-25** |
| 8 — Wiring, click-through, close | **Built 2026-09-25 — awaiting the owner's walk** |

---

## 1. Why this exists, and what it is not

**The gap has been recorded since 2026-09-23.** `STATE-OF-THE-APP.md` §6 has it
as the head of the backlog; `admin-plan.md` §3 put it explicitly out of scope
for the console ("inviting, deactivating and role assignment have no backend at
all"); CLAUDE.md §12 warns that `UsersController` "is not the start of a user
directory, and nothing should treat it as one."

What exists today is the domain and nothing above it. `User` has `AddRole`,
`RemoveRole`, `Deactivate` and `Reactivate`, all written in WP-1 and **never
called by anything outside the seeder**. There is no application layer, no
endpoint, and no screen. A tenant's population is whatever `SeedData` created.

### The PRD gap, stated rather than papered over

**There is no functional requirement for user management.** Checked against
`BookSpace_PRD_v1.docx` on 2026-09-23, not assumed:

- **§2, the persona table**, gives the Tenant Administrator the core need
  "Manage resources, rules, **members, roles**; view utilization." That is the
  authority this package is built on.
- **FR-1.5** — "A user belongs to one tenant and may hold multiple roles within
  it" — is a model constraint, not a management requirement.
- **FR-2.4** — "Sessions can be revoked; a suspended user loses access
  immediately on next token refresh" (**Should**, not Must) — is the closest
  thing to a requirement here, and it is about the *consequence* of suspension
  rather than the act.
- **FR-1.3** covers a Platform Operator creating, suspending and reactivating
  **tenants**, which is a different audience and is out of scope (§3).
- **PRD §13** raises four open decisions and none of them is this.

So this package is built on a persona line plus FR-2.4, and that is worth saying
out loud given CLAUDE.md §11's rule about not inventing requirements. It is not
an invention — an application whose administrator cannot add a colleague is
obviously incomplete, and the persona table says the role exists to do exactly
that — but the FR identifiers that every other package cites are not available
here, and no code comment should pretend otherwise. **Cite the persona line and
FR-2.4; do not invent an FR number.**

---

## 2. The contract it builds on — audited 2026-09-23

Read off the code, not assumed. This is the audit `admin-plan.md` §2 did and
WP-7 Phase 6 did not.

### What is already there

| Thing | State |
|---|---|
| `User.AddRole` / `RemoveRole` / `Deactivate` / `Reactivate` | Exist, WP-1. Called only by `SeedData`. |
| `User` constructor | Requires `createdByUserId` — "no self-registration" is written into its header comment. |
| `IPasswordHasher.Hash(string)` | Exists. |
| `UsersController` | **One** action: `GET /users`, the decision `0018` eligible-approver set. `TenantMember` + `TenantAdmin` stacked. |
| `IUserRepository` | Four methods, every one approver-shaped. |
| `AuthController` | `POST /auth/login`, `/auth/refresh`, `/auth/logout`. Nothing else. |
| `Organization` | Has `OrganizationStatus`, including `Suspended`. No controller anywhere. |

### What is missing, and it is more than it looks

- **`User` has no way to set a password after construction.** `PasswordHash` is
  `private set` and there is no method. A provisioned user therefore has no
  route to a credential at all.
- **`User` has no `RowVersion`**, unlike `Resources` (decision `0023`'s
  amendment). See §4.5.
- **There is no `IEmailSender`, and no email of any kind.** Grepped for
  `IEmailSender`, `SendEmail`, `SmtpClient` and `SendGrid` across
  `backend/src` — **zero hits**. PRD §11 assumes a provider is available; none
  is wired.
- **No user-related reason code exists.** `ReasonCodes` has no `UserNotFound`,
  no `EmailAlreadyInUse`, no `LastTenantAdmin`.

### FR-2.4 is already satisfied on the read side — which is the good news

This was the surprise of the audit, and it removes a whole phase:

- `LoginCommandRequestHandler` checks `found.User.IsActive`.
- `RefreshTokenCommandRequestHandler` checks `!found.User.IsActive` **and**
  `OrganizationStatus.Suspended`, and on either **revokes the whole token
  family**, with the comment already citing FR-2.4.

So deactivation needs **no change to the auth stack whatsoever**. The only thing
missing is something that writes `IsActive = false`. A deactivated user's
existing access token stays valid until it expires — at most 15 minutes
(decision `0009`) — and that is not a gap to close but precisely what FR-2.4
asks for: *"loses access immediately on next token refresh."* See §4.4.

---

## 3. Settled before planning (owner, 2026-09-23)

Four questions were put to the owner before any of this was written, because
each one changes the shape of the work and none was answerable from the PRD or
the decisions log.

### 3.1 Invitations are emailed, and the email path is built here

**Answered: build the email path now.** The owner asked the right follow-up —
*"is there a way to build this without the background jobs, they're in the next
WP?"* — and the answer is yes, decisively. §4.1 is that argument in full; the
short version is that **an invitation is sent synchronously, inside the request,
and does not go through the `Notifications` outbox at all.** No job, no
scheduler, no schema change to `Notifications`.

**This answer carries one thing with it that was not on the scope list, and it
is not scope creep**: an emailed invitation has to contain something the
recipient can act on, and acting on it has to set a password. So **activation
tokens and `POST /auth/activate` are in** (phase 2). Without them, `POST /users`
creates somebody who can never sign in, which is not a smaller version of the
feature — it is a broken one.

### 3.2 An email collision gives one generic refusal

**Answered: the same refusal either way.** `EmailAlreadyInUse`, whether the
address belongs to this tenant or another, with copy that does not say which.

Decision `0010` made email unique platform-wide, but it settled that for
*login* and never covered what a creation refusal may reveal. Saying "already
taken in another organization" — or merely answering differently in the two
cases — turns `POST /users` into a cross-tenant existence oracle, which is the
concern decision `0018` took seriously enough to collapse three distinct
approver-ineligibility reasons into one code (AC-4). Same reasoning, same
answer.

### 3.3 The last TenantAdmin cannot be removed or deactivated

**Answered: refuse it**, as `LastTenantAdmin`.

This is not politeness. A tenant with zero administrators cannot be managed
through any API in the system, and **there is no SysAdmin user-management path
to rescue it** — FR-1.3 has no controller either, so recovery would be a
database edit. It also breaks decision `0028`: approval requests on a gated
resource with no assigned approvers fall back to
`IUserRepository.FindTenantAdminUserIdsAsync`, so a tenant with no admins
creates Pending bookings that notify **nobody**, and FR-9.3's expiry job then
decides them unseen. That is the exact failure `0028`'s `NotificationsFor`
change was written to prevent.

The guard therefore applies to **both** paths — removing the `TenantAdmin` role
and deactivating the account — because either one produces the same tenant.

### 3.4 Scope

**In:** create a user, deactivate, reactivate, assign and remove roles — plus,
per §3.1, the activation flow that makes a created user able to sign in.

**Out, and deliberately:**

- **Self-service change password.** A user who knows their password cannot
  change it. Flagged in §6.
- **Admin-triggered password reset.** Still out — `POST /users/{id}/invitation`
  (added 2026-09-25) resends to someone who has *never* activated; it refuses
  `UserAlreadyActivated` outright and is not a way to reset a working
  password. Re-issuing an invitation, the other half of this bullet, is no
  longer excluded — see §6.
- **SysAdmin tenant management (FR-1.3).** Different audience, entirely unbuilt,
  and a package of its own.
- **Deleting a user.** Never on the table: CLAUDE.md §4.5, nothing is deleted.

---

## 4. The design problems worth knowing before the first line

### 4.1 Why the invitation does not go through `Notifications`

This is the owner's question answered properly, and it is the most important
paragraph in this document because it is what keeps the package buildable now.

**`Notifications` is a scheduler, not a mailbox.** Read off `Notification.cs`:
the row carries `SendAtUtc`, `SentAtUtc`, `Attempts` and `LastError`, and it is
anchored by `BookingId`, or `RecurrenceRuleId` (+ `OccurrenceDate`). It carries
no address, no subject and no body — content is composed at dispatch time by a
job that does not exist yet. There are three factory methods and all three are
booking-or-series shaped.

Routing an invitation through it would need **all** of:

- a new `NotificationKind`,
- a **second** widening of `CK_Notifications_HasContext` — decision `0026`
  widened it once, for a row anchored to a series rather than a booking; a row
  anchored to a *user* shares none of the table's semantics,
- a meaningless idempotency key, since the unique constraint is
  `(BookingId, RecurrenceRuleId, OccurrenceDate, RecipientUserId, Kind)` and an
  invitation has neither of the first two,
- and **the reminder-dispatch job**, which is unbuilt.

Against which: an invitation has nothing to schedule. It is sent *now*, as a
direct result of a request somebody is waiting on.

**So: `IEmailSender` is called directly by the create-user handler.** The three
background jobs stay entirely out of this package — and, usefully, this package
*builds the sender they will need*, so whoever gets them inherits a working
`IEmailSender` rather than starting from nothing. That is the opposite of a
conflict with the next WP.

The costs, stated rather than hidden: the request now depends on an external
provider's latency, and a send failure has to be handled in front of the admin
rather than retried by a worker. §4.3 is that handling.

### 4.2 The activation token, and why it looks like a refresh token

A single-use, expiring, **hashed** token in its own table. The shape is lifted
wholesale from decision `0011` rather than invented: store SHA-256 of a CSPRNG
value, never the value itself; the plaintext exists only in the email. Consumed
on use.

Two rules worth fixing now:

- **`POST /auth/activate` is anonymous** — the recipient has no credentials yet,
  by definition. It therefore needs the same rate-limiting treatment `login` and
  `refresh` already have in `appsettings.json`.
- **Its failures must be indistinguishable.** Expired, already used, and never
  existed all answer the same way, for `0018`'s reason: a distinguishable
  response makes the endpoint an oracle for which invitations are outstanding.

### 4.3 An email failure must not lose the user

If the provider is down, the alternatives are to fail the whole request — which
couples creating a colleague to a third party's uptime, and needs a rollback —
or to create the user and tell the admin the email did not go out.

**The second.** The account is real either way, and `invitationEmailSent`
tells the admin which happened.

**Corrected 2026-09-25, external hardening pass (finding 2).** This section
used to say the create response "carries the activation link regardless of
whether the email succeeded," reasoning that the admin was standing right
there and could pass it on. That was a real vulnerability, not a convenience:
a TenantAdmin holding the invitee's own activation link could redeem it first,
set the password themselves, and sign in as the person they just created,
before that person ever saw the invitation — on *every* call, not only a
failed one, since nothing on the wire distinguished "I need this because
delivery failed" from "I am choosing to read someone else's credential." The
capability is removed outright, not narrowed: neither `POST /users` nor the
reissue endpoint below ever returns the raw link again. See decision `0029`'s
2026-09-25 amendment for the full reasoning, and §3.4/§6 below — "no way to
re-issue an invitation" was this section's stated reason the exclusion needed
push-back; it is no longer true, and is corrected where it appears.
See §6.

### 4.4 Deactivation's 15-minute tail is the requirement, not a gap

An admin deactivates somebody; that person's current access token keeps working
until it expires, up to 15 minutes later. This will look like a bug to anyone
who tries it, so it needs saying on the screen and in the tests: FR-2.4 asks for
access to be lost *"immediately on next token refresh"*, and that is what the
code does today, family revocation included.

Closing the tail would mean checking a revocation list on every authenticated
request — a cost on every endpoint in the system, to shorten a 15-minute window
on an event that happens a handful of times a year. **Not folded in** (§6).

### 4.5 Role editing has no concurrency protection, and the guard has a race

Two separate problems that look like one.

- **`Users` has no `RowVersion`.** Two admins editing one person's roles
  silently last-write-wins, the same gap `admin-plan.md` §4.2 records for the
  replace-the-set editors. Consistent with today's behaviour elsewhere, so it is
  flagged rather than fixed.
- **The last-admin guard is a read-check-write across two rows**, which is a
  genuine race rather than a theoretical one: two admins, each removing the
  other's `TenantAdmin` role simultaneously, both read "there are two admins",
  both pass, and the tenant ends with none. This is the same shape as the
  FR-3.3 invariant that earned `Resources` its `RowVersion` in the 2026-09-15
  hardening pass.

  **So the guard is checked under a lock**, in the same transaction as the
  write. It is a tier-2 rule by CLAUDE.md §6's table — "must never" belongs in
  tiers 1–3 — and putting it in tier 4 would be the exact mistake that table
  warns about.

### 4.6 `GET /users` has to change, and it is the delicate part

Today it answers the decision `0018` eligible-approver set, and
`ListUsersQueryRequest`'s own header says so deliberately, because "a route
named `/users` that answers with a subset is exactly the thing someone later
reads as broken." The directory needs **every** user in the tenant, including
Members and deactivated accounts.

Two ways, and the choice must be made before phase 4 rather than during it:

- **Widen the existing route with a parameter** (`?eligibleApproversOnly=true`,
  or a `scope`). One route, one handler, and the approvers picker keeps working
  by passing the flag. Risk: a forgotten parameter silently widens what the
  approvers picker offers, and `ReplaceApprovers` would then refuse people the
  picker showed — the precise failure phase 1 of the admin console moved the
  eligibility rule into SQL to avoid.
- **A second, explicitly named route** for the directory, leaving `GET /users`
  exactly as it is. No risk to the picker at all; costs a route whose
  relationship to the first needs explaining.

**Recommendation: widen the existing route, with the eligible-approver set as
the *default*** — so an omitted parameter keeps today's answer and today's
callers byte-identical, and the directory opts *in* to the wider set. That
inverts the risk: a forgotten parameter narrows rather than widens, which is the
safe direction, and it matches how `includeArchived` already works on
`GET /resources`.

### 4.7 A deactivated user's bookings are not touched

Deactivating somebody does not cancel their bookings, and the screen has to say
so — the same thing the archive confirmation had to say about a resource
(`admin-plan.md` §3). `User.Deactivate` flips a flag and nothing else. Whether
their future bookings *should* be cancelled is a real question and the answer is
no: a room booked for a meeting that is still happening should stay booked, and
nothing in the PRD suggests otherwise. Their name still renders on the booking,
because `Bookings` FKs are `NoAction` and nothing is deleted (§4.5).

---

## 5. Phasing

Each phase is built, tested and reported in one go. Backend first, as the admin
console did — its phase 1 proved the value of landing a backend addition on its
own, before any screen depends on it.

### Phase 1 — `IEmailSender`, configuration, development sink
The abstraction in `BookSpace.Application/Abstractions/`, an implementation in
`Infrastructure`, and configuration following the existing convention exactly:
credentials **absent** from `appsettings.json` with a `//` comment saying where
they come from, as `Jwt:SigningKey` already does (CLAUDE.md §4.4).

**Two implementations, chosen by configuration**, because there is no provider
account today and the whole package is otherwise undemonstrable locally: the
real one, and a development sink that logs the message and writes it to a file.
The sink is not a test double — it is how this runs on the owner's machine.

No user-facing behaviour. Reviewable entirely on its own.

**Done 2026-09-23.** 1140 unit + 539 integration tests (69 + 3 new), build
clean, and all four configuration outcomes probed against the real host rather
than reasoned about. What is true before building on it:

- **`IEmailSender` returns a result; it does not throw for a delivery
  failure.** That is §4.3 made structural rather than remembered: as a result
  type the failure is in the signature and phase 3 has to discard it on
  purpose. Both senders wrap everything — transport, an unwritable directory, a
  recipient MimeKit refuses — in a broad catch that produces
  `EmailSendResult.Failed`. The single exception is the *caller's* cancellation,
  which still propagates, guarded on `cancellationToken.IsCancellationRequested`
  so MailKit's own timeout does not masquerade as one.
- **MailKit over an SMTP relay, not a vendor SDK.** There is no provider account
  (§2), and every transactional provider offers an SMTP relay taking the API key
  as the password — so this works against whichever one is chosen later, and
  picking one now would be a dependency taken before the decision it depends on.
  MailKit rather than `System.Net.Mail.SmtpClient`, which Microsoft's own
  documentation tells you not to use for new development. One new package.
- **The mode is fixed at startup by one registration in `AddInfrastructure`**,
  not resolved per send behind a factory, so "why did no email arrive?" is
  answered by reading one branch. `EmailOptions.DeliveryMode` defaults to
  **`Smtp`**, deliberately: an omitted or misspelled `Email` section then fails
  the boot, where a `DevelopmentSink` default would have written production
  invitations to a directory nobody reads.
- **Validation lives in `EmailOptionsValidator`, not in data annotations.** Half
  the rules depend on `DeliveryMode`, which an attribute cannot express, and
  annotations do not recurse into the nested `Smtp`/`DevelopmentSink` objects at
  all — so splitting them across two mechanisms would mean a reader has to check
  both. `ValidateOnStart` gives it the same standing `JwtOptions` has.
- **A credential over an unencrypted transport is refused at boot**, not warned
  about: `Security: None` with a username or password would put the provider's
  credential on the wire in the clear on every send (CLAUDE.md §4.4). An
  unauthenticated local relay stays allowed. `Security` is a three-value enum
  (`StartTls` / `SslOnConnect` / `None`) rather than a bool, and `StartTls` maps
  to MailKit's `StartTls` rather than `Auto` — both `Auto` and
  `StartTlsWhenAvailable` fall back to plaintext against a server that does not
  advertise it, which is the case the setting exists to refuse.
- **The sink writes an `.eml` through the same `MimeMessageFactory` the SMTP
  sender uses**, so what is on disk is what would have gone on the wire — a
  second rendering would drift, and "it looked right in the .eml" would stop
  being evidence about the real path. `backend/src/BookSpace.Api/sent-emails/`
  is gitignored: every file in it is a live activation link.

**Three things the build found rather than assumed**, all measured against
MimeKit 4.18 and all now pinned by tests:

- **`MailboxAddress.TryParse("not-an-address")` returns true.** A bare atom is a
  legal addr-spec for a local mailbox, so the parser alone is not a usable
  address check. `EmailAddressRules` requires a domain on top of it.
- **`new MailboxAddress(name, "")` succeeds**, producing a message with an empty
  `To`. The sink would have written that to disk and reported it delivered.
  `MimeMessageFactory` therefore checks the recipient explicitly rather than
  relying on MimeKit to throw.
- **The sink's file name used to carry the recipient's address, and the log line
  carries the path** — so the §4.4 rule this class documents was false as
  written. Caught by its own test. The file name is now a timestamp and random
  bytes and nothing from the message, which is better than sanitizing the
  address: there is no path-traversal case left to get wrong.

**Verified live** (Staging, real host, 2026-09-23): the shipped `appsettings.json`
with no `Email:Smtp:Host` refuses to boot naming that exact setting; a credential
with `Security: None` refuses to boot naming the §4.4 rule; a host plus a
credential over StartTls boots and serves; Development boots with the sink
selected and answers `/health` 200.

### Phase 2 — Activation tokens, `User.SetPassword`, `POST /auth/activate`
The table, the domain method, the anonymous endpoint, its rate limit, and the
indistinguishable-failure rule from §4.2. Migration: one new table.

Still no way to *create* a user — this phase is only the mechanism by which one
would set a password. It is separated from phase 3 deliberately: it touches the
auth surface, which is the most security-sensitive code in the application, and
it deserves a review that is not also about a create form.

**Done 2026-09-24.** 1194 unit + 555 integration tests (54 + 16 new), build
clean, one migration, and the whole flow probed against the running API. What is
true before building on it:

- **`ActivationTokens` is shaped on `RefreshTokens`, including what it does not
  have.** No `OrgId`, no query filter, no entry in `Security.TenantAccessPolicy`
  — activation runs before the user has ever signed in, so CLAUDE.md §4.2's
  three mechanisms have nothing to act on and an `OrgId` here would be a column
  nothing could filter by at the moment it matters. The token's own secrecy is
  the access control: 256 bits of CSPRNG, stored only as SHA-256, single use,
  absolute expiry. That is decision `0011`'s shape, reused rather than
  reinvented.
- **`SecureToken` now holds that rule once**, and both factories delegate to it.
  Extracted rather than copied because two implementations of a crypto rule is
  where drift is most expensive — somebody "improves" one to a salted hash and
  only one table's lookups break, in a way that reads as a data problem.
- **Single use is a concurrency token, not an `if`.** `ConsumedAtUtc` is
  `IsConcurrencyToken`, exactly as `RefreshTokens.RevokedAtUtc` is, so two
  requests redeeming one invitation at the same instant do not both set a
  password — the loser affects zero rows and answers 409. Model metadata only,
  no DDL.
- **Every refusal is one answer**: expired, already redeemed, unknown, user
  deactivated, organization suspended → `401 InvalidActivationToken`, byte for
  byte. An integration test compares whole response bodies, not just statuses.
  The password policy is checked *before* the token is looked up, so a short
  password cannot be used to probe whether a token is live; and a rejected
  password leaves the token spendable, which matters because there is no way to
  re-issue one (§6).
- **`POST /auth/activate` returns 204, not a session.** Handing back tokens
  would mean re-implementing login's FR-2.4 account-state gate in a second
  place, or signing somebody into a suspended organization. The client already
  holds the password it just set and can call `POST /auth/login`. Reversible
  cheaply if the owner prefers the other shape.
- **`PasswordPolicy` is 12–128 characters with no composition rules.** This is
  the first place BookSpace ever *sets* a password, so the policy had to be
  invented — FR-2.3 says hashed, and nothing says how long. Length only follows
  NIST SP 800-63B; the maximum is not cosmetic, since PBKDF2 at 100k iterations
  over an unbounded input is a cheap way to make an anonymous endpoint do
  unbounded work. **Flagged as a judgement call, not a requirement.**

**The design problem this phase actually turned on, which the plan did not
anticipate:** activation is the first operation in the application that *writes*
to a `Users` row from an unauthenticated request, and two of §4.2's three
mechanisms refuse it.

- RLS is a **filter** predicate, and a filter predicate governs the rows an
  `UPDATE` can see. With no tenant context the row is invisible, the update
  matches nothing, and EF reports a `DbUpdateConcurrencyException` about a row
  that is plainly there. So the save goes through a new, explicitly-named
  `IAuthenticationUserRepository.SaveChangesUnfilteredAsync` — the write half of
  the bootstrap exemption the two reads already carry.
- Mechanism 2, `ValidateTenantOwnership`, then refused it for a different
  reason: `/auth/activate` is anonymous, but a browser holding a session
  attaches its bearer token to same-origin API calls, so `ICurrentTenant` can
  hold *another tenant's* org while the user being activated belongs elsewhere.
  It now stands down inside a `TenantBypassScope`, which is the same signal RLS
  already honours. Nothing widens: the scope is internal and only the
  explicitly-named repository methods may enter it, and that guard exists to
  catch a *forgotten* scope rather than a declared one.

**Both changes were proven load-bearing rather than asserted**: removing the
bypass fails 8 of the 16 new integration tests, and removing only the
`ValidateTenantOwnership` early return fails exactly 1 — the
signed-in-as-somebody-else case.

**Verified live** against the running API (2026-09-24, dev database, migration
applied): login before activation 401; a short password 400 with the policy
message; an unknown token 401 `InvalidActivationToken`; the real token 204;
login afterwards 200 with an access token; the same token again 401. In the
database the row shows `ConsumedAtUtc` set, the placeholder hash replaced, and
`UpdatedByUserId = Id`. The `activate` rate-limit policy fires (429 after the
budget).

### Phase 3 — `POST /users` — create and invite
The create handler: validate, check the email, hash a placeholder, issue an
activation token, send the invitation, return the link (§4.3). New reason code
`EmailAlreadyInUse`, one generic message (§3.2).

The new user is created **with no roles at all** — role assignment is phase 5,
and a user with no roles can sign in and see nothing, which is the correct
resting state for somebody an admin has not yet decided about. Whether creation
should take an initial role set is a phase-3 call, not settled here.

**Done 2026-09-24.** 1252 unit + 579 integration tests (58 + 24 new), build
clean, no migration, and the whole flow walked against the running API —
including reading the invitation off disk and following the link out of it.
What is true before building on it:

- **`EmailAlreadyInUse` is decided by `UQ_Users_Email`, and there is no
  pre-check anywhere.** That is not a shortcut, it is the §3.2 guarantee made
  structural: a pre-check able to see another tenant's row would have to be an
  unfiltered read, and CLAUDE.md §4.2 keeps that surface to the two named
  authentication methods. Letting the index answer is race-free (§6 puts
  uniqueness in tier 1) and means the code raising the refusal **cannot** learn
  which tenant the collision is in, rather than merely declining to say.
  `UserRepository.SaveChangesAsync` translates SQL 2601/2627 — and checks the
  *index name*, because `Users` also carries `UQ_Users_CalendarFeedToken` and
  `UQ_Users_Org_Id` and reporting either as "that email is taken" would send an
  admin hunting a problem that is not there.
- **Order: save, then send.** An email that says "click here" must never go out
  for an account the database refused, and a unit test asserts exactly that.
  The account, its role and its activation token go in **one** save, so an
  account cannot exist with no way into it.
- **The new user gets `Member`, not "no roles at all" — a phase-3 call that
  corrects this plan's own premise.** §5 said a roleless user "can sign in and
  see nothing"; that is false. `AuthorizationPolicies.TenantMember` requires
  only the `orgId` claim, so a roleless account can already browse and book
  exactly as a Member can. Nothing in `src/` branches on `Role.Member` — it is
  purely descriptive — so assigning it grants nothing extra, makes the row
  describe what the account can actually do, and keeps a provisioned user
  structurally identical to a seeded one (the seed assigns `Member` explicitly).
  Approver and TenantAdmin are *not* assigned: those grant something, and
  granting them is phase 5's job, with the last-admin guard.
- **The placeholder credential is a PBKDF2 hash of two fresh Guids**, so there
  is no window in which a provisioned account is sign-innable before its owner
  activates it — proven live by three failed login attempts before activation.
- **`Activation:ActivationUrl` is required with no default**, like
  `Cors:AllowedOrigins`: the API cannot guess its frontend's origin, and a guess
  would produce invitations that look right and lead nowhere. Startup fails
  without it. `ActivationLinkBuilder` uses `UriBuilder`, which matters for the
  case concatenation gets wrong — a configured URL that already has a query
  string.
- **The invitation is `InvitationEmail`, a pure function, text *and* HTML.** The
  text half is required because a client that refuses HTML must still be able to
  get the recipient in. The admin-typed full name is HTML-escaped, ampersand
  first so nothing double-escapes.

**A seam found while building, and closed:** FluentValidation's `.EmailAddress()`
is deliberately lenient and accepts `"ada lovelace@acme.test"`, while MimeKit
refuses an address with a space outright (measured in phase 1). Left alone, that
paste-a-name typo would have produced a 201, a failed send, and a colleague who
never hears anything. The validator now also rejects whitespace — the narrowest
rule that turns it into a 400 naming the field. Login is deliberately **not**
tightened to match: such an address can no longer be created, and refusing to
let an existing account sign in would be a different change.

**Verified live** (2026-09-24, dev database, development email sink): admin
creates a user → `201`, `roles: ["Member"]`, `isActive: true`,
`invitationEmailSent: true`; the sink writes a real multipart/alternative `.eml`
whose HTML shows `Ada &lt;Test&gt; Lovelace` correctly escaped; the link taken
**out of that file** activates the account (`204`) and the new user then signs in
(`200`) with a JWT carrying `role: Member` and the right `orgId`; the link
refuses a second use (`401`). A duplicate in the caller's own tenant and one in
another tenant both answer `409 EmailAlreadyInUse` with **byte-identical**
bodies. A Member and an Approver get `403`, anonymous `401`, a SysAdmin `403`,
and an address with a space `400`.

**The known gap this leaves, deliberately:** an admin can now create a colleague
and **cannot see them anywhere** — `GET /users` still answers the decision `0018`
eligible-approver set, so a freshly created Member is absent (confirmed live:
`totalCount` stays 2). That is exactly what phase 4 is for.

### Phase 4 — The directory read
Widening `GET /users` per §4.6's recommendation, with the eligible-approver set
staying the default so no existing caller changes. The row grows `isActive` and
keeps `roles`; `ListUsersQueryResponse`'s header comment about why `IsActive` is
absent ("every row is active by construction") stops being true and must be
rewritten rather than left to mislead.

**The approvers picker's specs are the regression proof** for this phase, the
way `ApproverEndpointTests` were for admin console phase 1.

**Done 2026-09-24.** 1267 unit + 600 integration tests (15 + 21 new), 1114
vitest tests unchanged, no migration, no new reason code, and the endpoint
probed live. What is true before building on it:

- **`GET /users?scope=All`**, and `scope` is a `UserScope` enum
  (`EligibleApprovers` | `All`) rather than a bool, modelled on `BookingScope`
  — which already means "which rows" on `GET /bookings`, so the two list
  endpoints read the same way.
- **The default is the narrow set, which is the whole design** (§4.6). An
  omitted `scope` gives the decision `0018` eligible-approver answer, byte for
  byte, so a forgotten parameter narrows rather than widens. The failure mode
  that inverts avoids is specific: a picker silently offering people
  `ReplaceApprovers` then refuses, with `0018` collapsing every reason into
  `ApproverNotEligible` so no screen can say why.
- **`UserReadEndpointTests` and `ApproverEndpointTests` pass untouched** — 44
  tests, not one line edited. That is the regression proof this phase was
  supposed to produce, and it only means something because the default did not
  move.
- **The row grew `isActive`.** `ListUsersQueryResponse`'s header used to list it
  among the deliberate absences ("every row is active by construction"); that
  claim died with `scope=All` and the comment is rewritten rather than left to
  mislead. It is still true of the default scope, and now asserted rather than
  stated.
- **`scope` widens within a tenant and nowhere else.** The query filter and RLS
  are what exclude another tenant's rows; no value of this parameter reaches
  them, and a SysAdmin (no `OrgId`) appears in nobody's directory.
- **One method on the repository, not two.** `ListEligibleApproversAsync` became
  `ListAsync` and the scopes differ by exactly one `Where`, so paging, searching
  and ordering are written once. An unrecognized scope throws rather than
  picking a branch — unreachable, because the validator refuses it first, which
  is precisely why it should not guess.
- **The frontend is unchanged and deliberately so.** `UsersService` sends no
  `scope`, so the picker keeps working; only three now-false comments were
  corrected. `EligibleUser` does not gain `isActive` — the directory's row type
  belongs to phase 6.

**A test trap this phase was the first to hit**, worth knowing before writing
anything against the seeded users: `member2@acme.test` is **permanently
deactivated** by `AuthenticationEndpointTests.Refresh_UserDeactivatedSinceLogin`,
by design and documented there. No previous test could see it, because the
eligible set excludes Members anyway — but `scope=All` returns that row, so an
assertion that "every user is active" passed alone and failed only in a full
run. Tests that care about `IsActive` name the account they mean;
`admin@acme.test` is the one nothing deactivates.

**Verified live** (2026-09-24): `scope` omitted → 2 rows; `scope=All` → 4;
omitted and `scope=EligibleApprovers` byte-identical; `isActive` on every row;
`scope=Everyone` and `scope=99` both 400; enum binding is case-insensitive, so
`scope=all` works. And the phase 3 gap closing, end to end: a colleague created
through `POST /users` is **absent** from the default scope and **present** in
`scope=All`, findable by search, and still listed with `isActive: false` once
deactivated.

### Phase 5 — Deactivate, reactivate, roles, and the last-admin guard
The three writes, plus `LastTenantAdmin` checked under a lock (§4.5). Decide
here whether roles are replace-the-set or add/remove — `admin-plan.md` §4.1's
lesson is that the API's shape should decide the screen's, so pick one and let
phase 7 follow it.

**Done 2026-09-24.** 1312 unit + 627 integration tests (45 + 27 new), no
migration, two new reason codes, and every endpoint probed live. Decision
[`0031`](decisions/0031-last-tenant-admin-guard.md) came out of it. What is true
before building on it:

- **Three endpoints**: `POST /users/{id}/deactivate`, `POST
  /users/{id}/reactivate`, `PUT /users/{id}/roles`. The two state changes follow
  `POST /resources/{id}/archive` — a transition with no payload — and both are
  **idempotent**, returning the current state and writing nothing when it
  already holds, so a no-op never moves `UpdatedAtUtc`.
- **Roles are replace-the-set**, the phase-5 call the plan left open. Same answer
  as `PUT /resources/{id}/approvers` and for the same reason: per-role
  POST/DELETE makes an admin swapping Approver for TenantAdmin pass through an
  intermediate state whose content depends on the order the client picked. It
  also makes the guard statable — over a final set the question is "does this
  still contain TenantAdmin?", over a sequence of deltas it has to be re-asked
  after each one. **Phase 7 renders a checkbox group saved in one go**
  (`admin-plan.md` §4.1: the API's shape decides the screen's).
- **An empty role set is refused (400), unlike an empty approver list.** A
  resource with no approvers is a coherent state (`0028` made it a supported
  one); a user with no roles is not, because `TenantMember` needs only the
  `orgId` claim — they keep full member access and the row would lie about it.
  Same reason phase 3 assigns `Member`. An admin who wants to take everything
  away deactivates the account, and the error message says so.
- **`SysAdmin` cannot be assigned, and that is a privilege-escalation guard
  rather than a formatting rule.** `CK_UserRoles_Role` allows the value — the
  bootstrap SysAdmin row needs it — so the validator is the only thing between a
  TenantAdmin and the platform role. A 400 rather than a reason code, because no
  legitimate client can send it; the tests are where it is written down.
- **`LastTenantAdmin` (422) covers both doors and is checked under `UPDLOCK,
  HOLDLOCK` inside the write's own transaction** — see `0031` for the full
  argument. Reactivation takes no lock and opens no transaction, deliberately:
  the set can only grow, and ceremony implying a rule nobody enforces is worse
  than none.
- **`UserNotFound` (404) is the same answer for an unknown id and another
  tenant's real one**, byte for byte, following `ResourceNotFound` (AC-4).

**The finding worth keeping.** The first concurrency test fired two real HTTP
requests at once, passed, and **went on passing with the `UPDLOCK, HOLDLOCK`
hints deleted — five runs out of five.** The window between the guard's read and
its write is too narrow for two TestServer requests to interleave by luck, so the
test proved the endpoint works under parallelism and nothing at all about the
lock. It is kept with its comment corrected, and
`UserLastAdminGuardConcurrencyTests` was added beside it to force the
interleaving: T1 takes the lock and holds it 1.5s before writing, T2 starts 300ms
later and must block. **That one fails 2/2 on every run with the hints removed
and passes with them.** The first version of *it* was also wrong — it resolved
the repository from the host's DI, where `ICurrentTenant` is null outside a
request, so every operation silently found nothing and an assertion inside a
`catch` hid the reason. It now builds its own DbContext with a fixed tenant and
the real RLS interceptor.

A second cross-class bug came out of the same work: the fixture that restores
the seeded admin used `AddRole` on top of whatever a test had written, leaving
`[Member, TenantAdmin]` and failing one assertion in
`UserDirectoryEndpointTests` in a full run only. Restoring state means restoring
the *set*.

**Verified live** (2026-09-24, dev database): deactivate → 200 with `isActive:
false`; a second deactivate → 200, same `updatedAtUtc`; reactivate → 200; roles
replaced to `[Approver, Member]` then back to `[Member]`; empty set, `SysAdmin`,
a duplicate and an unknown role all 400; an unknown id 404. Against the real
seeded tenant, where `admin@acme.test` is the only administrator: deactivating
them → **422 `LastTenantAdmin`**, taking the role off them → **422**, and the two
bodies **identical**. Promote a successor → 200, the original may then step down
→ 200, and the successor immediately becomes the one who cannot → 422. The dev
database was restored to its seeded shape afterwards.

### Phase 6 — Frontend: the user directory and the create form
`/admin/users`, a sibling of `/admin/resources` under the existing `admin`
route tree, so `adminGuard` covers it without being asked. A ninth rejection
dialect over `core/http/rejection.ts`, binding its own field union — **not a
widening of any existing one** (CLAUDE.md §12, admin console phase 2).

The invite outcome screen is the interesting part: the activation link, shown
once, with the failure case reading differently from the success case.

**Done 2026-09-25.** 1161 vitest tests (47 new), production build clean, both
screens' exact requests probed against the running API. No backend change. What
is true before building on it:

- **Two screens, both under `/admin`** — `/admin/users` and `/admin/users/new`,
  flat siblings of `resources` like every other admin route, so `adminGuard`
  covers them without being asked. `users/new` is declared before any future
  `users/:id`, because the router matches in declaration order and the
  parameterised route would otherwise swallow it.
- **The directory's rows are not links, deliberately.** The detail screen is
  phase 7; linking to a route that does not exist is the mistake admin console
  phase 3 avoided by leaving the approvers link out until the screen was there.
  A spec asserts the absence, so phase 7 has to delete it on purpose.
- **`UsersService.listDirectory()` is a separate method, not a `scope` argument
  on `list()`.** One endpoint, two callers, two row types — and putting the
  scope that must never be sent by accident one optional argument away from the
  call that must never send it is how the picker starts offering Members. A spec
  pins that `list()` sends no scope whatever it is passed.
- **The invite outcome replaces the form rather than sitting under it.** `POST
  /users` returns a live activation link, and a still-live submit button beside
  a credential is how a second account gets created. The link is held in one
  signal and nowhere else: no storage, no service cache, no route state, so
  navigating away loses it — and the screen says "shown once" rather than
  pretending otherwise.
- **The failure case shows the same link, framed differently** (§4.3). "Account
  created — but the email didn't send" with a caution border, against
  "Invitation sent". Both are 201s; what changed is who delivers the invitation.
- **`user-rejection.ts` is the ninth dialect**, binding `'email' | 'fullName'`
  rather than widening an existing union. It covers only `ValidationFailed` and
  `EmailAlreadyInUse` — the two codes `POST /users` can actually return — and
  phase 7's writes will extend it when they have controls. Its
  `EmailAlreadyInUse` copy is a **security property, not tone**: decisions
  `0010`/`0030` make the server answer identically whether the address is in
  this tenant or another, so the message names neither, and two tests assert the
  absence.
- **No retry is offered on an unknown outcome — the strictest copy in the
  console.** `POST /users` has no idempotency key, so repeating it either
  creates a second account or answers 409 about the one it just made, and the
  first attempt may already have sent an invitation nothing can withdraw. The
  instruction is to check the directory.
- **The nav gained a second admin entry, "Users".** The shell's existing comment
  refuses a flat list of admin items because windows/approvers/blackouts are all
  sub-resources of `/resources/{id}`; `GET /users` has no such dependency, so
  this one is genuinely top-level. **A tabbed admin console is the tidier answer
  if a third section ever appears** — noted rather than built, because two
  destinations do not need a tab strip and a shared layout route.

**Verified live** (2026-09-25): the directory's own request
(`?page=1&pageSize=50&scope=All`) answers four users with roles and
`isActive`; search narrows to one; the invite form's `POST /users` answers 201
with `roles: ["Member"]`, `invitationEmailSent: true` and a link expiring in
seven days; the same address again answers 409 `EmailAlreadyInUse`; the new
person appears in the directory and **not** in the picker's read; deactivating
them (phase 5) leaves them listed with `isActive: false`, which is the row the
badge renders from. The invitation `.eml` was written to the development sink.

### Phase 6b — Frontend: the activation screen
**Done 2026-09-25**, and **it was missing from this plan entirely.**

§8 below lists three screens to build — directory, invite form, user detail —
and phase 8 opens "No new screens", while its own click-through requires "a real
link followed, a password set, and a first sign-in". Those cannot both be true.
The cause is traceable: §3.1 pulled activation tokens and `POST /auth/activate`
into scope as a *consequence* of the emailed-invitation decision, that
consequence was tracked on the backend and never propagated to the screen list,
and five phases went by without anyone noticing. **Found by the owner pasting an
invitation link and landing on `/login?token=…`** — there was no `/activate`
route, so the catch-all swallowed it.

Numbered 6b rather than renumbering 7 and 8, per CLAUDE.md §12's rule about not
renumbering phases that are already referenced.

What is true now:

- **`/activate`, a sibling of `/login`, outside the shell.** The recipient has
  no account, so `authGuard` would bounce them to the screen they cannot use.
- **Deliberately no `guestOnlyGuard`.** Somebody already signed in on a shared
  machine must be able to redeem their own link, and the backend supports
  exactly that — phase 2 built and tested the mismatched-tenant case, so
  bouncing them here would be the client refusing something the server allows.
- **On success it goes to `/login?activated=1`, not into the app** (owner's
  call, 2026-09-25). That is what the endpoint does — 204, no session — and it
  keeps session minting in the one handler that owns FR-2.4's account-state
  checks. The login screen renders a one-line banner from that query parameter,
  in the URL rather than router state so it survives a refresh.
- **One message for every refusal.** The server answers 401
  `InvalidActivationToken` identically whether the link expired, was used, never
  existed, or belongs to a deactivated account (§4.2, inheriting `0018`), so the
  screen says no more than that — and still says what to do, because nothing
  re-issues an invitation (§6) and asking an administrator is the only route
  left. A test asserts the message is identical across refusals.
- **No tenth rejection dialect.** One reason code, no field to point at, no
  vocabulary to map — a `RejectionDialect` would be machinery around a single
  string. A short function in the component instead, shaped like login's own
  `handleLoginError`.
- **A confirm-password field**, which the server knows nothing about. It exists
  because there is no password reset in this application (§3.4), so a typo here
  is permanent.
- **`PASSWORD_MIN_LENGTH`/`MAX` are mirrored client-side** in
  `features/auth/password-policy.ts` — the message, not the guarantee, on the
  one screen where somebody is inventing a password rather than recalling one.

### Phase 7 — Frontend: the user detail screen
Roles, status, and the two confirmations. Deactivation's confirmation carries
§4.4's 15-minute tail and §4.7's "their bookings are not cancelled", because
both will otherwise be discovered as surprises.

`LastTenantAdmin` needs rendering as a refusal an admin can *act* on — "give
somebody else the administrator role first" — rather than as a wall.

**Done 2026-09-25.** 1212 vitest tests (27 new), production build clean, plus a
backend addition this phase's own planning missed and a stop-and-ask before
writing it. What is true before touching this area:

- **`GET /users/{id}` did not exist, and phase 7 could not be built without
  it.** None of `List` (no id filter), `Create`, `Deactivate`, `Reactivate` or
  `ReplaceRoles` (all three take an id but answer with the write's own result,
  not a screen-shaped read) gave a direct link, a bookmark, or a reload
  anything to load from — the same requirement every other detail screen in
  the app (`GET /resources/{id}`, `GET /bookings/{id}`) already satisfies.
  Flagged to the owner rather than routed around (three options: add the
  endpoint, walk the paged directory client-side by id, or carry the row
  through router state) — **the owner chose adding the endpoint**, for the
  same reason this session's own memory already states: cross-screen state
  belongs in the URL, not hidden client state, and a bookmark has to resolve.
  `GetUserByIdQueryRequest` (`Features/Users/GetUserById/`) is the fourth query
  feature on `UsersController`, follows `GetResourceQueryRequest`'s shape
  exactly (404 covers both "no such user" and another tenant's real id, AC-4),
  and adds one repository method, `IUserRepository.FindDetailAsync` —
  `AsNoTracking`, the read counterpart to `FindForUpdateAsync`, which stayed
  named for what it actually is (a write path's tracked fetch) once a real read
  path existed beside it. No migration, no new reason code (`UserNotFound`
  already existed, unused for a 404 read until now). 1315 unit + 637
  integration tests (3 + 10 new) before the frontend work started.
- **The initial load does not go through `user-rejection.ts`.** That dialect
  describes a refused *write*; loading this screen is a read, and its 404 is
  checked with a plain `error.status === 404`, mirroring
  `BookingDetailComponent`'s own load rather than the shared `resourceNotFound`
  machinery — which is wired to the literal code `ResourceNotFound` and cannot
  fire for `UserNotFound`.
- **`user-rejection.ts` gained a second dialect, not a wider first one.**
  `describeUserDetailRejection` (deactivate/reactivate/replace roles) is a
  separate `RejectionDialect` object from `describeUserRejection` (create),
  matching `cancel-rejection.ts`'s own split between a booking cancel and a
  series cancel: the two forms share `UserFieldName`, but "This account could
  not be created" is actively wrong copy for a refused deactivation, so the
  generic/unmapped/unknown-outcome strings could not be shared even though the
  vocabulary is.
- **`LastTenantAdmin` gets one message for both doors it can come through** —
  deactivating the account and removing the TenantAdmin role — because the fix
  is identical either way and the copy has to work regardless of which control
  triggered it: "Give someone else the administrator role first, then try
  again." Decision `0031`.
- **Roles are replace-the-set**, exactly as `PUT /users/{id}/roles` and
  `admin-plan.md` §4.1 required: a checkbox group over `ASSIGNABLE_ROLES`
  (`TenantAdmin` / `Approver` / `Member` — `SysAdmin` is never offered, the
  same privilege-escalation guard the validator itself is), saved in one PUT.
  An empty selection is refused client-side rather than clamped — the save
  button disables and the copy says to deactivate instead, echoing the
  validator's own rule rather than merely deferring to it.
- **Deactivate and reactivate are two separate, lightweight confirmations, not
  one shared dialog.** Deactivating a colleague is the one an administrator can
  get wrong in a way that matters, so its panel states both facts that would
  otherwise be discovered as surprises: the access-token tail (§4.4 — up to 15
  minutes, not a bug) and that their existing bookings are not touched (§4.7).
  Reactivating is the reverse of a reversible state, so its panel is a plain
  "are you sure" with no acknowledgement tick — the same distinction
  `admin-resource-form.component.ts` draws between an irreversible archive (a
  tick) and a reversible one (a plain confirm).
- **The directory's rows are real links now.** `admin-user-list.component.html`
  drops the "not a link yet" comment phase 6 left and points each row at
  `/admin/users/:id`, the same `.row-title-link` pattern
  `admin-resource-list.component.html` already used — admin console phase 3's
  own reason for waiting (a route leading nowhere is worse than one that leads
  nowhere yet) no longer applies once the screen exists.
- **Proven through the automated suites, not a live click-through.**
  `GetUserByIdEndpointTests` runs the real id / cross-tenant real id / unknown
  id cases against a real SQL Server through the real pipeline — the
  cross-tenant and unknown cases answer byte-identical 404 bodies once
  `traceId`/`correlationId` are stripped, the same comparison
  `CreateUserEndpointTests` already used — and 1315 unit + 637 integration
  tests pass, alongside 1212 vitest tests and a clean production build. **No
  browser was used**: nobody has clicked deactivate, reactivate, or a roles
  save against the running frontend and the real dev database the way earlier
  phases' "Verified live" sections record. Flagged as a verification gap
  rather than skipped silently — the same honesty WP-7 Phase 1 recorded when
  no browser-automation tool was available for its own walkthrough. Worth
  closing before phase 8's click-through, which will exercise this screen
  properly.

### Phase 8 — Wiring, click-through, close
No new screens. Admin-path seams added to `app/tests/navigation-chain.spec.ts`
following rendered `href`s, the systematic coverage sweep (**enumerate files,
do not scan names** — it found two holes in the admin console after an eyeball
audit found none), a click-through script, and the write-up.

**The click-through has a path nothing else in this project has had**: a real
email arriving, a real link followed, a password set, and a first sign-in. That
is the one flow no test can stand in for.

**Built 2026-09-25, and deliberately not marked Done — the click-through
([`docs/user-management-clickthrough.md`](../docs/user-management-clickthrough.md))
has not been walked by the owner yet, and this package follows the same rule
`admin-clickthrough.md` and `wp7-clickthrough.md` established: a click-through
being *written* is not the same claim as it being *walked*, and only the
second one closes anything.** What is true now:

- **`navigation-chain.spec.ts` gained two seams**: the directory's rows lead
  into the detail screen and back (`.row-title-link` → `/admin/users/:id` →
  `.inline-link` back to `/admin/users`), and the admin-only bounce test now
  covers `/admin/users`, `/admin/users/new` and `/admin/users/:id` alongside
  the resource routes it already had — the guard sits on the parent route, so
  this is the proof rather than an assumption. 1215 vitest tests, production
  build clean.
- **The coverage sweep found one hole, not the two admin console phase 7's
  found — but it is the same shape.** Enumerating `features/admin/` end to end
  turned up nothing missing (`users.models.ts` has no spec, matching the
  established exemption for a pure-types file — `resources.models.ts` has
  none either). Enumerating `features/auth/` — the surface phase 2 and 6b
  added — found `ActivationService` had no spec of its own.
  `activate.component.spec.ts` already pinned its exact wire shape (method,
  URL, body) through the component's own HTTP mock, but nothing anywhere
  asserted that the call **opts out of the global error toast** — which matters
  more here than on most calls in the app, since the activate screen exists
  specifically to say no more than the server did, and a toast firing on top
  of its own inline refusal would be exactly that leak. `password-policy.ts`
  is exempt for the same reason `users.models.ts` is: pure constants, no logic.
- **The whole account-creation → email → activation → first-sign-in chain was
  proven once, live, against a running API and a real SQL Server, with
  `curl`** — not a browser, so it does not replace path A, but it is the
  reason path A's own steps are written as concrete facts rather than
  predictions: a real `POST /users`, a real `.eml` written to
  `backend/src/BookSpace.Api/sent-emails/` carrying the identical link,
  `POST /auth/activate` (401 before, 204 to redeem, 200 to sign in after, 401
  `InvalidActivationToken` on reuse), `GET /users/{id}` for both a real id and
  an unknown one, the last-admin guard refusing both deactivation and role
  removal on the tenant's sole administrator (**422 `LastTenantAdmin`**,
  closing phase 7's own "no live verification yet" gap along the way), and the
  validation refusals (empty role set, `SysAdmin`, a duplicate email, a
  Member forbidden from `GET /users/{id}`). Left two accounts behind in the
  dev tenant as a result — **Clickthrough Probe**, alongside phase 3's own
  **Create Test** — both real, both harmless, both left in place because users
  are never deleted (CLAUDE.md §4.5). The click-through script's own "before
  you start" section says so rather than presenting a clean tenant that no
  longer exists.
- **The click-through script itself is the headline deliverable of this
  phase.** Five paths: A is the chain nothing else has proven with a human —
  invite, find the email on disk, follow the link, set a password, sign in for
  the first time, then find the new person from the admin side; B is the
  roles editor including the empty-set refusal; C is deactivate/reactivate,
  carrying FR-2.4's tail and the bookings-not-touched fact into the
  confirmation copy itself, the same discipline `admin-clickthrough.md`'s A6
  used for archiving; D is the last-admin guard, walked both ways (refused,
  then made possible by promoting a second admin first, then refused again on
  the new sole admin); E is deliberate wrong turns — a role-gated screen typed
  directly, a cross-tenant id, a reused or incomplete activation link, a
  mid-edit refresh, and the one **known, not a bug** item: `Users` has no
  `RowVersion` on the wire, so two admins editing one person's roles at once
  silently last-write-wins, exactly as `admin-plan.md` §4.2 already accepted
  for the resource editors.
- **What phase 8 leaves for the owner, and only the owner**: walking paths A–E
  in a real browser and reporting the result. That is what turns this from
  "built" into "closed" — the same distinction this package's own plan has
  drawn at every other phase boundary.

---

## 6. Flagged, not folded in

- **No self-service change password.** Out of scope by §3.4. Consequence stated
  plainly: a user who knows their password cannot change it, and a password the
  activation flow set is theirs forever. `POST /auth/change-password` is a small
  endpoint once `User.SetPassword` exists (phase 2), so this is cheap to add
  later and cheap to add *during* — but it is not in.

- **~~No way to re-issue an invitation~~ — closed 2026-09-25.** This used to be
  the one exclusion flagged for push-back: with no reset, no resend and no
  delete, a provider outage or a closed tab left that person with no route into
  the system at all. `POST /users/{id}/invitation` is built — TenantAdmin-only,
  tenant-filtered, refuses `UserAlreadyActivated`/`UserNotActive`, and
  supersedes any still-live token before issuing a new one — as part of the
  same external hardening pass that removed §4.3's always-show-the-link
  fallback (decision `0029`'s amendment). The two changes are the same
  argument from two directions: the old design needed the link shown because
  there was no other recovery path, and closing the recovery path properly is
  what made removing the link safe.

- **`Users` has no `RowVersion`** (§4.5). Consistent with the rest of the
  application today, and closing it is a migration plus a DTO field.

- **Deactivation's 15-minute access-token tail** (§4.4). Closing it costs a
  check on every authenticated request across the whole API.

- **FR-1.3 — SysAdmin tenant management — remains entirely unbuilt.** No
  controller, no application layer; only `OrganizationStatus` and the refresh
  path that already honours it. It is the other half of "who provisions whom"
  and needs its own package.

- **Nobody tells a deactivated user they have been deactivated.** They discover
  it at their next refresh, as an authentication failure. Adding a notification
  is an email-path decision that belongs with the background jobs.

---

## 7. Decision records this will produce

Per §9's convention — a settled question gets a numbered record with its
reasoning, and its one-liner goes in both CLAUDE.md §9 and
`docs/roadmap/decisions-log-detail.md`.

- **[`0029`](decisions/0029-user-provisioning-and-invitation-delivery.md) — user
  provisioning and invitation delivery. Written 2026-09-24**, covering phases
  1–3: why invitations are emailed synchronously and deliberately do not use the
  `Notifications` outbox (§4.1), why a delivery failure is a return value
  (§4.3), and why the activation token is shaped like a refresh token (§4.2).
  It also records the two §4.2 write exemptions activation needed, which this
  plan did not anticipate.
- **[`0030`](decisions/0030-email-collision-disclosure-at-creation.md) — email
  collision disclosure at creation. Written 2026-09-24.** One generic refusal
  either way, the AC-4 reasoning inherited from `0018` (§3.2), and the
  structural half this plan did not call for: the unique index decides it, not
  a pre-check, so the path cannot learn which tenant the collision is in.
- **`0031` — the last-TenantAdmin guard.** Not written yet — phase 5. Why it
  exists, why it covers both role removal and deactivation, and why it is
  checked under a lock (§3.3, §4.5).

---

## 8. Screens to design

None of these has a provided design, and the outstanding design pass already
covers nine screens. **Settled the same way the admin console settled it**
(`admin-plan.md` §7): follow the app's existing card vocabulary rather than
wait. A console built to the established vocabulary can be restyled; one not
built cannot.

- User directory — `/admin/users`
- Create user form, and the invitation outcome with its once-only link
- User detail — roles, status, and the two confirmations
- **The activation screen — `/activate`. Added 2026-09-25, after the owner found
  it missing.** This list had three entries and should always have had four:
  §3.1 put `POST /auth/activate` in scope, and an endpoint an invited person has
  to reach needs a screen to reach it from. Phase 8's click-through assumed this
  screen existed while phase 8's own scope line said "No new screens" — the
  contradiction was in the document for five phases. See phase 6b.
