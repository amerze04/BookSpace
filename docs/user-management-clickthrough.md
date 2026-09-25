# User management — the click-through

**Phase 8.** The one verification this repository cannot perform for itself: a
person actually clicking — and this package has a flow nothing else in the
project has had. Every other click-through in this repo (`wp7-clickthrough.md`,
`admin-clickthrough.md`) walks a signed-in person through screens. This one
starts with **nobody signed in at all**: an administrator invites a colleague,
a real email is written to disk, the link inside it is the only way into the
account it names, and the person on the other end sets their own password and
signs in for the first time. No test can stand in for that chain — it is one
continuous act by two different people (the admin, then the invitee), and every
automated test in this suite plays only one side of it at a time.

Written 2026-09-25, after phases 1–7 closed. **Not yet walked by the owner** —
unlike `admin-clickthrough.md`, which records a same-day walk, this one is
handed over unwalked. Everything in it was proven once, live, against a running
API during writing (`curl`, not a browser — noted per step), which is why the
wording is concrete rather than illustrative; what it has not had is an actual
person clicking through the Angular app.

**Revised the same day, before its first walk**, by an external hardening pass
that found a real vulnerability in what path A originally described: the
outcome screen used to show the admin the raw activation link, which let a
TenantAdmin redeem their new colleague's own invitation before the colleague
did. That capability is now removed — the steps below reflect the corrected
screen, not the one this document originally walked through while being
written. Marked inline wherever a step changed.

Its own file rather than a section of `user-management-plan.md`, for the same
reason the other two click-throughs are their own files: held in one hand while
the other clicks.

**Roughly 15 minutes for paths A–D, another 10 for path E.**

Nothing in phases 1–7 ticks this off. Every screen has been probed against the
running API or proven by an automated suite; what none of that establishes is
whether an administrator can **bring a colleague into the tenant and manage
their account** without being misled, or without a step turning out to be a
dead end.

---

## Before you start

| | |
|---|---|
| App | http://localhost:4200 |
| API | http://localhost:5270 |
| Admin | `admin@acme.test` / `Passw0rd!` |
| Approver | `approver@acme.test` / `Passw0rd!` |
| Member | `member1@acme.test` / `Passw0rd!` |
| Sent email | `backend/src/BookSpace.Api/sent-emails/` — every invitation this environment has ever sent lands here as a real `.eml` file, newest last if you sort by name. Open one in any mail client, or just read it as text. **The directory is gitignored; every file in it is a live credential.** |

**The state of the Acme tenant, read off the live API on 2026-09-25** — so the
numbers below are concrete rather than illustrative:

- **Six people, before you add a seventh.** *Tenant Admin* (`TenantAdmin`),
  *Resource Approver* (`Approver`), *Member One* and *Member Two* (`Member`),
  **Create Test** — a Member left behind by an earlier phase's own live
  verification — and **Clickthrough Probe**, a Member left behind by *this*
  document's own writing: proving path A's chain end to end with `curl` before
  handing the script over meant actually running it once, and that run created
  a real account the same way path A itself will. All three are left in place
  rather than cleaned up, the same way `admin-clickthrough.md` left three
  archived probe resources: users are never deleted (CLAUDE.md §4.5), so there
  was never a way to remove them, and each is a real example of exactly the
  kind of account this screen has to handle correctly.
- **`admin@acme.test` is this tenant's only administrator.** Path D asks you to
  try emptying that set on purpose — it will refuse you, and that refusal is
  the point.
- **Nothing here has ever gone through `POST /auth/activate` from a browser.**
  The account-creation → email → activation → first-sign-in chain has been
  proven with `curl` (below, and see `user-management-plan.md`'s phase 7
  entry) but never with a person clicking, which is exactly what path A closes.

Everything you create is disposable in the sense that matters: nobody is hurt
by an extra Member account sitting in the tenant forever. **Deactivating
`admin@acme.test` is not possible** — Path D exists to prove that rather than
to let you try it by accident elsewhere.

---

## Path A — inviting a colleague, start to finish

Sign in as **admin@acme.test**.

### A1 · The way in
- [ ] A **Users** item is in the nav, alongside **Resources**. It is not there
      for a member or an approver — you will check that in E1.
- [ ] It opens `/admin/users`, listing all five people above (Member One and
      Member Two included — this is the directory, not the approvers picker,
      which would hide them).

### A2 · Invite someone
Press **Invite someone**.
- [ ] The form asks for a name and an email address, and nothing else — no
      password field, no role picker. *The recipient chooses the password
      through the link; the server assigns `Member` automatically (user
      management phase 3).*
- [ ] Type a name with a space in the email, e.g. `ada lovelace@acme.test`. It
      is refused **before** the request goes out, naming the email field.
      *Login is deliberately not this strict — see the plan's phase 3 entry for
      why.*
- [ ] Fix it to a real address — anything `@acme.test` you have not used
      before — and submit.

### A3 · The outcome
**Rewritten 2026-09-25 — an external hardening pass removed the activation
link from this screen.** It used to be shown here, once, for the admin to copy;
a TenantAdmin holding it could redeem their new colleague's own invitation
first and sign in as them before the real recipient ever saw it. See decision
`0029`'s amendment.
- [ ] You land on a confirmation, not back on the empty form.
- [ ] It says the invitation was **sent**, not just that the account was
      created — the two are different facts, since a send failure still
      creates the account.
- [ ] **No activation link appears anywhere on this screen.** Check the page
      source or select-all if you want to be sure — this is the regression the
      rewrite exists to catch.
- [ ] It links to the new person's own page (**View {name}**). Follow it —
      you will need it in A5b regardless of whether the email sent.

### A4 · The email, as a real file
- [ ] Open `backend/src/BookSpace.Api/sent-emails/` and find the newest
      `.eml`. Open it as text if nothing else is handy.
- [ ] It is addressed to the person you just invited, has both a plain-text
      and an HTML part, and both contain the same link.
- [ ] The name you typed is escaped correctly in the HTML part even if it had
      an ampersand or an angle bracket in it. *Try this with a name like `A & B`
      if you want to see it — the plain-text part is there specifically for a
      client that refuses the HTML one.*
- [ ] Copy the link out of the email — this is now the **only** place to get
      it. You will need it in A5.

### A5 · Following the link
- [ ] Open the link from the email. It is `/activate`, **outside the
      shell** — no nav, no sidebar, because the person on the other end has no
      account to see one from.
- [ ] Enter a password under 12 characters. Refused, naming the rule.
- [ ] Enter two passwords that do not match. Refused.
- [ ] Enter a real password (12+ characters) in both boxes and submit.
- [ ] You land on **the login screen**, with a banner saying the account is
      ready. *Deliberate: activation returns no session (204, not a token
      pair), because minting one would duplicate login's own FR-2.4 checks in a
      second place.*
- [ ] Check the address bar right after the page first loaded, before you
      submitted: the `?token=…` should be gone from it, even though the form
      still worked. *Finding 7 — the token is read into memory once and then
      scrubbed from the visible URL and history, so it does not linger in a
      browser's back button, a copied link, or a screenshot.*

### A5b · Resending, from the person's own page
**Rewritten 2026-09-25** — this used to be "there is no recovery"; there is
now. On the person's detail page (the **View {name}** link from A3):
- [ ] A section titled **Invitation** is visible, with a **Resend invitation**
      button — because this account has not activated yet.
- [ ] Press it. It reports **Invitation resent**, and a second `.eml` appears
      in the sent-emails directory for the same address.
- [ ] The **original** link from A4 no longer activates anything — try it
      (in a private window, so you do not disturb the session you are about
      to use in A6): refused, same generic message as an expired or unknown
      link. *Reissuing supersedes whatever was still live, so an account never
      has two simultaneously usable links.*
- [ ] The **new** link (from the second `.eml`) does activate the account —
      use it for A6 if you tried the original one above.
- [ ] Now that the account is activated, reload the person's page: **Resend
      invitation** is gone. *Resending to an already-activated account is
      refused server-side (`409 UserAlreadyActivated`); the control does not
      wait to be told that — it is not offered.*

### A6 · The first sign-in
- [ ] Sign in with the address you invited and the password just set.
- [ ] You land in the app as an ordinary member — the calendar, resources,
      booking all work. *A roleless-looking account already has full member
      access, because `TenantMember` needs only the `orgId` claim — `Member`
      describes that rather than granting it.*
- [ ] Try the link **again**, with a different password. Refused, with the
      same message a wrong or expired link would give. *`InvalidActivationToken`
      is deliberately one answer for expired, used, and unknown — distinguishing
      them would be an oracle for which invitations are outstanding.*

### A7 · Finding them again, as the admin
Back as **admin@acme.test**.
- [ ] Open the **Users** directory. The person you invited is there, listed as
      active with the `Member` role.
- [ ] Click their row. It opens `/admin/users/<their id>` — a real link, not a
      dead end, and it loads their name, email, status and roles.

**Path A passes if A1–A7 hold.** ▢ pass ▢ fail — note which step:

---

## Path B — roles

Stay on the person you just invited (or open **Member One** if you would
rather not touch a real account).

### B1 · What is offered
- [ ] Three checkboxes: **Administrator**, **Approver**, **Member** — nothing
      else. *`SysAdmin` is never offered; the validator refuses it outright as
      a privilege-escalation guard, not a formatting rule.*
- [ ] The person's current role(s) are already ticked.

### B2 · Emptying it
- [ ] Untick every box. **Save is disabled**, and a message says to deactivate
      the account instead of clearing every role. *A roleless account still
      has full member access — the row would lie about what it can do.*
- [ ] Tick one box back. Save re-enables.

### B3 · A real change
- [ ] Tick **Approver** in addition to whatever is already ticked, and save.
      It confirms, and the directory reflects the new role if you go back and
      check.
- [ ] Untick it again and save, to leave the account as you found it.

**Path B passes if B1–B3 hold.** ▢ pass ▢ fail — note which:

---

## Path C — deactivate and reactivate

Use the person from path A, not a real seeded account.

### C1 · Deactivating
- [ ] The **Deactivate** control sits in its own clearly-marked section, and
      pressing it does **not** act immediately — it opens a confirmation.
- [ ] The confirmation states **two** things plainly: that they lose access on
      their **next token refresh, at most 15 minutes** from now (not
      immediately), and that their **existing bookings are not cancelled**.
      *Both are FR-2.4 and §4.7's own wording — stated so neither is discovered
      as a surprise.*
- [ ] Confirm it. The badge flips to **Deactivated**, and the section below it
      changes to offer **Reactivate** instead.

### C2 · What deactivation actually does
- [ ] Try signing in as that person now. **Refused** — `IsActive` is checked
      on login, immediately, not just on refresh.
- [ ] *The 15-minute tail is about someone who was already signed in when you
      deactivated them — their existing access token keeps working until it
      expires. Not practically checkable inside a 15-minute click-through
      without leaving a second browser tab signed in for that long; recorded
      as a known property rather than something to reproduce here.*

### C3 · Reactivating
- [ ] Press **Reactivate**. Its confirmation is a plain "are you sure" — no
      15-minute caveat, because signing back in works immediately once this
      is confirmed.
- [ ] Confirm it. The badge flips back to **Active**.
- [ ] Sign in as that person again. It works.

**Path C passes if C1–C3 hold.** ▢ pass ▢ fail — note which:

---

## Path D — the last-administrator guard

**`admin@acme.test` is this tenant's only administrator. Do not leave it any
other way when you are done.**

### D1 · Deactivating the only admin
- [ ] Open `admin@acme.test`'s own detail screen and try to deactivate them.
- [ ] **Refused**, with a message that names the fix rather than just the
      rule: *"This is the only active administrator your organization has
      left, so this change is refused. Give someone else the administrator
      role first, then try again."*
      *Verified live: `POST /{id}/deactivate` on the tenant's sole admin
      answers **422 `LastTenantAdmin`**.*

### D2 · Removing the role instead
- [ ] On the same account, untick **Administrator** in the roles editor
      (leaving another box ticked, so B2's empty-set refusal does not fire
      instead) and save.
- [ ] **Refused, with the same message.** *One copy covers both doors on
      purpose — decision `0031` — because the fix is identical either way.*
      *Verified live: `PUT /{id}/roles` with `TenantAdmin` removed from the
      sole admin also answers **422 `LastTenantAdmin`**.*

### D3 · The only way through
- [ ] Promote **Resource Approver** (or the person from path A) to
      Administrator — tick the box, save.
- [ ] **Now** remove Administrator from `admin@acme.test`. It **succeeds**,
      because the tenant now has two.
- [ ] Try to remove Administrator from the **new** admin. **Refused** — they
      are now the only one.
- [ ] Put it back exactly as it was: Administrator restored to
      `admin@acme.test`, and removed from whoever you promoted, so the tenant
      ends this path with exactly the one administrator it started with.

**Path D passes if D1–D3 hold.** ▢ pass ▢ fail — note which:

---

## Path E — the deliberate wrong turns

**This is where this project's bugs have actually lived.** None of these is an
unreasonable thing for a real person to do.

### E1 · The console, without the role
- [ ] As **member1**, and again as **approver**, check there is **no Users**
      nav item.
- [ ] Type `/admin/users` directly. You land on the calendar.
- [ ] Type `/admin/users/<some id>` directly. Same. *The guard is on the
      parent route, so it covers this screen the same way it covers the
      resource-management ones.*
- [ ] *Already verified at the API level while writing this: a Member gets
      **403** from `GET /users/{id}`. What you are checking here is the UI
      half.*

### E2 · An account id that is not yours
- [ ] Take a real user id from the Globex tenant (or invent one) and open
      `/admin/users/<id>` as the Acme admin.
- [ ] You get **not found** — and the wording does not reveal whether the id
      exists somewhere else. *Verified live: a real Globex admin id, read as
      Acme's admin, answers the same **404 `UserNotFound`** as an id that
      exists nowhere at all.*

### E3 · A used or incomplete activation link
- [ ] Follow the link from path A **a second time**. Same generic refusal as
      an expired or unknown one — nothing here should say "already used"
      specifically. *`InvalidActivationToken` is deliberately one answer for
      all three, so this screen cannot become an oracle for which invitations
      are outstanding.*
- [ ] Open `/activate` with **no token at all** in the URL. The screen says
      the link looks incomplete, rather than trying to submit and failing
      confusingly.

### E4 · Refresh everywhere
On the directory, the invite form, and a person's detail screen, press F5.
- [ ] Every one comes back to the same place with a valid session. None
      bounces you to `/login`, and the detail screen re-loads the same person
      rather than losing your place.

### E5 · Two admins, one account — known, not a bug
- [ ] Open the same person's roles editor in two tabs (or a normal and a
      private window), both signed in as the admin. Change their roles
      differently in each tab and save both.
- [ ] The second save **wins silently**, and the first tab is never told.
      **This is known and accepted**, not something to report — `Users` has no
      `RowVersion` on the wire (plan §4.5), the same gap `admin-plan.md` §4.2
      records for the replace-the-set resource editors. Confirm it behaves as
      described — quietly overwritten — rather than crashing or corrupting the
      account.

### E6 · The browser's own Back button
Walk directory → a person → roles change → back, using the browser's Back
button rather than the on-screen link.
- [ ] The directory re-renders correctly rather than showing stale data from
      before your edit.

**Path E passes if E1–E6 hold.** ▢ pass ▢ fail — note which:

---

## If something is wrong

Note **the screen, what you did, what you saw, and what you expected** — that
is enough. Do not work around it; a workaround hides the size of the problem.

If it is cosmetic rather than wrong, it belongs to the outstanding design pass
(this console was built to the app's existing card vocabulary rather than to a
provided design, the same call `admin-plan.md` §7 made) rather than to a bug
list.

## Afterwards

- [ ] `admin@acme.test` is the tenant's **only** active administrator again —
      check D3's cleanup actually happened.
- [ ] The account you invited in path A is left **active**, with whatever
      roles path B's own cleanup left it — a Member account sitting in the
      tenant forever is expected, the same way admin-clickthrough's probe
      resources were left archived rather than removed.
- [ ] Tell me the result; it closes phase 8, and the user management package
      with it.
