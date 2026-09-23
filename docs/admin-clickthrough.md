# Admin console — the click-through

**Phase 7.** The one verification this repository cannot perform for itself: a
person actually clicking. It exists for the same reason WP-7's did — **seven
bugs in WP-7 were found by the owner clicking and none by the test suite** — and
the admin console has already added two more of exactly that shape: a breadcrumb
that crashed the whole shell on a repeated crumb, and a route-data inheritance
that produced "Admin > Admin > Resources".

Written 2026-09-23, and **walked end to end by the owner the same day — paths A
to E, everything passing, no defects found.** It is not spent: **re-walk it
after any change to the admin flows**, the way `wp7-clickthrough.md` is
re-walked after a change to booking or approvals. One clean walk says this
console was right on 2026-09-23, not that it will stay right.

Its own file rather than a section of `admin-plan.md` because it is held in one
hand while the other clicks.

**Roughly 20 minutes for paths A–D, another 10 for path E.**

Nothing in phases 1–6 ticks this off. Every screen has been probed against the
running API and every rule has a spec; what none of that establishes is whether
an administrator can **sit down and run their tenant** without being misled.

---

## Before you start

| | |
|---|---|
| App | http://localhost:4200 |
| API | http://localhost:5270 |
| Admin | `admin@acme.test` / `Passw0rd!` |
| Approver | `approver@acme.test` / `Passw0rd!` |
| Member | `member1@acme.test` / `Passw0rd!` |

**The state of the Acme tenant, read off the live API on 2026-09-23** — so the
numbers below are concrete rather than illustrative:

- **Six resources, three of them archived.** Active: **Conference Room A**
  (Room, capacity 1, `America/New_York`, no approval), **3D Printer**
  (Equipment, capacity 1, requires approval, two approvers), **Audi A5**
  (Vehicle, capacity 1, `Europe/Sarajevo`, requires approval, max 120 min).
  Archived: *AC5 probe room*, *Postman Room Updated*, *Scratch Resource* — all
  three left behind by earlier probes, and useful here rather than untidy.
- **Exactly two people are eligible to approve**: *Resource Approver*
  (`Approver`) and *Tenant Admin* (`TenantAdmin`). The two Members are **not**
  in `GET /users`, and that is the endpoint working rather than failing.
- **Two window shapes worth knowing are already in the data.** The 3D Printer
  has `09:00–12:00` **and** `12:00–17:00` on every weekday — adjacent, not
  overlapping, because `ClosesAt` is exclusive. The Audi A5 has a Monday window
  closing at `23:59:59`, which decision `0022` says means the *following
  midnight*.
- Conference Room A carries a blackout over **25–26 December 2026** ("Public
  holiday").

Everything you create is disposable. **Archiving is not** — read A6 before you
press it.

---

## Path A — the catalogue

Sign in as **admin@acme.test**.

### A1 · The way in
- [ ] An **Admin** item is in the nav. It is not there for a member or an
      approver — you will check that in E1.
- [ ] It opens `/admin/resources`, a **separate screen** from the member-facing
      Resources list, not a mode on it.
- [ ] The breadcrumb reads **"Admin > Resources"** — two crumbs, not three.
      *This is the one that took the entire shell down in phase 3. If it reads
      "Admin > Admin > Resources", or the page is blank, stop and say so.*

### A2 · The list shows what the member list hides
- [ ] Three resources are listed by default.
- [ ] Tick **Include archived**. Three more appear, marked as archived.
- [ ] An archived row's action reads **View**; an active row's reads **Edit**.
      Archived resources cannot be edited, so offering "Edit" would be a lie.
- [ ] Untick it again. The count returns to three.
- [ ] Search and the type filter both narrow the list, and the paging controls
      describe the *filtered* set rather than the whole catalogue.

### A3 · Create a resource
Press **New resource**.
- [ ] The timezone picker is a long list of real IANA ids, and **`UTC` is in
      it**. *`UTC` is a tz database link rather than a zone, so the browser's own
      catalogue leaves it out and the form puts it back.*
- [ ] `Eastern Standard Time` is not offered. That is a Windows id, and
      CLAUDE.md §4.3 refuses it deliberately.
- [ ] Fill in a name, a type, capacity 2 and a timezone, and save.
- [ ] You land somewhere that confirms it was created and offers **Manage this
      resource**. Follow it.

### A4 · Approval gating, with nobody to approve
On the resource you just made, turn **Requires approval** on and save.
- [ ] It is **accepted**. *Decision `0028`, 2026-09-23: a resource may be gated
      with an empty approver list. The old rule refused this, which forced every
      gated resource through a window where it was published and freely
      bookable.*
- [ ] A warning appears saying requests will go to your organization's
      administrators until you assign some, and that bookings are held pending
      in the meantime.
- [ ] That warning has an **Assign approvers** link, and it works. *It
      deliberately pointed nowhere in phases 3 and 4, because the screen did not
      exist yet.*

### A5 · Changing the timezone is not a shift
Edit a resource that has opening hours, change its timezone, and save.
- [ ] You are told **how many windows were reinterpreted**. *Decision `0003`:
      09:00 stays the string "09:00" and now means a different instant. That is
      surprising enough that the server reports it and the form has to show it.*

### A6 · Archive — read this before clicking
- [ ] The archive control is **on the form, not on a list row**. The one thing
      worth buying is that you are looking at the resource when you decide.
- [ ] The confirmation says **both** things: that it cannot be undone, **and**
      that existing bookings are *not* cancelled by it.
- [ ] It needs an explicit acknowledgement, not a single click.
- [ ] Archive the resource you created in A3. The form goes **read-only**, and
      the list now shows it only with Include archived on.
- [ ] Try to edit it anyway. You cannot. *Verified against the live API: `PUT`
      on an archived resource answers **422 `ResourceArchived`**.*

**Path A passes if A1–A6 hold.** ▢ pass ▢ fail — note which step:

---

## Path B — opening hours

Open **3D Printer** → **Edit opening hours**.

### B1 · What is already there
- [ ] Every weekday shows **two** windows, `09:00–12:00` and `12:00–17:00`.
- [ ] They are **not** flagged as overlapping. *`ClosesAt` is exclusive, so
      adjacency is legal — the live API accepts this exact pair. A client
      stricter than the API it writes to would be a bug.*

### B2 · The midnight convention
Open **Audi A5** → **Edit opening hours**. Its Monday evening window closes at
`23:59:59`.
- [ ] It renders as an **"Until midnight"** tick, not as the time `23:59:59`.
      *Decision `0022`. A stored `23:59:59` and a deliberate one-second-to-
      midnight are indistinguishable on the wire and must not be on screen.*
- [ ] Untick it. A real closing time appears and can be edited.
- [ ] Tick it again. The window reads as closing at midnight.

### B3 · A real overlap is refused before it is sent
On any resource, add a window that genuinely overlaps another — `09:00–12:00`
against `11:00–17:00` on the same day.
- [ ] The row is flagged **in the editor**, and Save is withheld.
      *The server would answer **409** — verified live — but being told at the
      row beats being told after a round trip.*

### B4 · Saving
- [ ] **Save is disabled until something actually changes.** Delete a window and
      re-add it identically: that is *not* an edit, because the endpoint
      replaces the whole set.
- [ ] Make a real change and save. It confirms.
- [ ] Delete every window and save. This is accepted — **"closed" is a real
      state**, not a gap to fill. Put them back afterwards.
- [ ] **Back to resource** takes you to the resource, not all the way out to the
      list — and **ctrl-click it**: it opens in a new tab. *It was a `<button>`
      until phase 7 and could do neither.*

**Path B passes if B1–B4 hold.** ▢ pass ▢ fail — note which:

---

## Path C — approvers

Open **3D Printer** → **Manage approvers**.

### C1 · Who is offered
- [ ] Exactly **two** people are listed: *Resource Approver* and *Tenant Admin*.
- [ ] **No Member appears, and there is no way to type an id.** *Decision `0018`
      collapses every ineligibility reason into one code, deliberately, because
      naming the cause would confirm a cross-tenant id exists. So a picker that
      let you type could only ever answer "no" without saying why.*
- [ ] Each person shows what makes them eligible — their role — and their email,
      which is there to tell two people with the same name apart.

### C2 · A selection survives a search
- [ ] Tick somebody. Search for the *other* person, so the ticked one leaves the
      list.
- [ ] Clear the search. **The tick is still there.** *The selection is held as
      ids in their own signal; the option objects are replaced wholesale on
      every search, so a tick stored on one would be silently lost — and a lost
      tick is a silent removal on the next save.*

### C3 · Emptying the list
- [ ] Untick everyone and save. It is **accepted**, and the screen says requests
      will go to the tenant's administrators.
- [ ] *Before decision `0028` the only way to drop the last approver was to
      un-gate the resource first — which turned a staffing change into a window
      where anyone could book it.*
- [ ] Put both approvers back.

### C4 · The one group you will not see
- [ ] There is a **"no longer able to approve"** group, for somebody who was
      assigned and has since been deactivated. **It will be empty**, and it
      cannot be produced through the UI — deactivating a user has no backend at
      all. Noted here so its absence is not read as it being missing.

**Path C passes if C1–C3 hold.** ▢ pass ▢ fail — note which:

---

## Path D — blackout periods

Open **Conference Room A** → **Manage blackouts**. This is the screen that
cancels other people's bookings, so it earns the most attention.

### D1 · What is there, and in whose clock
- [ ] The Christmas blackout is listed.
- [ ] It shows **both** readings — the resource's timezone and yours. Conference
      Room A is in `America/New_York`; you are not.
- [ ] The screen is a **list with per-row Edit and Delete**, not an edit-
      everything-save-once form like the last two. *That is the API's shape
      (decision `0019`), not a styling choice: blackouts are per-row CRUD with a
      real hard delete.*

### D2 · You type in the resource's clock
Start creating a blackout.
- [ ] The form says plainly that the times are in the **resource's** timezone.
- [ ] Enter a window and check the second reading updates to your own zone with
      the right offset.

### D3 · The preview
Pick a window that covers a real booking — make one as `member1` first if there
is none.
- [ ] There is an opt-in **"check what this would cancel"**, and it lists the
      bookings.
- [ ] It says the real answer is **decided at save time**. *A booking created in
      between will be cancelled too; that race is in CLAUDE.md §6 and cannot be
      closed from a browser, so the wording promises only what it can.*

### D4 · The cascade, afterwards
Save it.
- [ ] The screen reports **what was actually cancelled** — who, when, and
      whether it belonged to a recurring series.
- [ ] Sign in as that member and check: the booking is gone from their calendar,
      and opening it by direct link shows it cancelled **with no person named**.
      *`Booking.CancelForBlackout` leaves the actor null precisely because no
      person did it individually.*

### D5 · Deleting is not an undo
Back as the admin, delete the blackout you just made.
- [ ] The confirmation says plainly that **the bookings it cancelled stay
      cancelled** and nobody is notified. *Decision `0019`'s cascade is
      forwards-only. An admin who assumed otherwise would be wrong in a way
      nothing else on the screen corrects.*
- [ ] Confirm, and check the member's booking is indeed still cancelled.

### D6 · A blackout in the past
Try to create one that **ended** yesterday.
- [ ] Refused, with a reason. *Verified live: **422 `BlackoutPeriodElapsed`**.*

Now create one that **started** this morning and runs through tomorrow.
- [ ] **Accepted.** The rule is about the end, not the start — which is exactly
      what you need when a room floods. Delete it afterwards.

**Path D passes if D1–D6 hold.** ▢ pass ▢ fail — note which:

---

## Path E — the deliberate wrong turns

**This is where this project's bugs have actually lived.** None of these is an
unreasonable thing for a real person to do.

### E1 · The console, without the role
- [ ] As **member1**, and again as **approver**, check there is **no Admin nav
      item**.
- [ ] Type `/admin/resources` directly. You land on the calendar.
- [ ] Type `/admin/resources/<some id>/blackout-periods` directly. Same. *The
      guard is on the parent route, so it covers every screen rather than only
      the entrance.*
- [ ] *Already verified at the API level on 2026-09-23: a member and an approver
      both get **403** from `GET /users` and `POST /resources`; anonymous gets
      **401**. What you are checking here is the UI half.*

### E2 · Two admins, one resource
Open the **same** resource's opening hours in a normal window and a private one,
both signed in as the admin. Change them differently and save both.
- [ ] The second save **wins silently**, and the first admin is never told.
      **This is known and accepted** — `admin-plan.md` §4.2. `Resources` has a
      `RowVersion`, but no Resources DTO carries it, so no version reaches the
      wire for the replace-the-set editors. Confirm it behaves as described
      rather than crashing or corrupting the list.
- [ ] Now do the same on the resource **form** itself (name and capacity). Here
      the version *is* checked: the loser should be told someone else changed it
      and to **reload**, not to save again.

### E3 · A resource id that is not yours
- [ ] Take a resource id from the Globex tenant, or invent one, and open
      `/admin/resources/<id>`.
- [ ] You get **not found** — and the wording does not reveal whether it exists.

### E4 · An archived resource, every way in
- [ ] Open an archived resource's form: read-only.
- [ ] Open its opening hours, approvers and blackouts screens by URL.
- [ ] Each renders read-only and says why, and the way back goes to the
      **resource**, not to the list.

### E5 · Capacity below what is booked
- [ ] Book a resource as `member1`, then as the admin try to set its capacity
      to 0.
- [ ] Refused, and the message says the count **includes pending requests** — a
      pending booking reserves its units in full (decision `0005`).

### E6 · Refresh everywhere
On each admin screen in turn, press F5.
- [ ] Every one comes back to the same place. None bounces you to `/login` with
      a valid session, and none loses the resource it was editing.

### E7 · The browser's own Back button
Walk resource → opening hours → back → approvers → back → blackouts, using the
browser Back button rather than the on-screen links.
- [ ] Every screen re-renders correctly, with no stale resource name left in the
      breadcrumb.

**Path E passes if E1–E7 hold.** ▢ pass ▢ fail — note which:

---

## If something is wrong

Note **the screen, what you did, what you saw, and what you expected** — that is
enough. Do not work around it; a workaround hides the size of the problem.

If it is cosmetic rather than wrong, it belongs to the outstanding design pass
(this console was built to the app's existing card vocabulary rather than to a
provided design — `admin-plan.md` §7) rather than to a bug list.

## Afterwards

- [ ] The resource you created in A3 is archived and will stay in the list
      forever. That is the system working as designed; there is no unarchive.
- [ ] Any blackout you created is deleted, and any booking you made as `member1`
      is cancelled — or say what you left behind.
- [ ] The 3D Printer should end with both of its approvers assigned.
- [ ] Tell me the result; it closes phase 7, and the admin console with it.
