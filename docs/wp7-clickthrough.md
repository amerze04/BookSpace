# WP-7 — the click-through

**Phase 7, step 4.** The one verification nothing in this repository can perform
for itself: a person actually clicking. It exists because **seven bugs in WP-7
were found by the owner clicking and none by the test suite** — the pattern in
every one of them being that the assertion which existed was true, but was not
about what determined what you saw.

Written 2026-09-22. Its own file rather than a section of `wp7-plan.md` because
it is held in one hand while the other clicks; the plan is read at a desk.

**Roughly 15 minutes for paths A and B, another 10 for path C.**

When you have walked it, Phase 7 step 5 records the result and WP-7's first
acceptance criterion — *a member completes browse → book → confirm entirely
through the UI* — is either met or has a bug against it. **It is not ticked by
anything in steps 1–3**; those prove every contract on the path, which is a
different claim.

---

## Before you start

| | |
|---|---|
| App | http://localhost:4200 |
| API | http://localhost:5270 |
| Member | `member1@acme.test` / `Passw0rd!` |
| Approver | `approver@acme.test` / `Passw0rd!` |
| Admin | `admin@acme.test` / `Passw0rd!` |

**Two facts that make several checks below meaningful:**

- **Conference Room A is in `America/New_York`; you are not.** It opens
  09:00–17:00 on its own clock, which is 13:00–21:00 UTC, which is 15:00–23:00 in
  `Europe/Warsaw` — a **six-hour** gap in late September (EDT is UTC−4, CEST is
  UTC+2), so every time on these screens moves by six. Screens showing a time have
  to be clear about *whose* clock, and the two-zone reading is a real feature
  rather than noise.
- **Monday 28 September 2026 is open all day** on both Conference Room A
  (capacity 1, no approval) and the 3D Printer (capacity 1, requires approval,
  gated by `approver@acme.test`). Verified against the API on 2026-09-22. Any
  other weekday works; the script uses that one so the expected numbers are
  concrete.

Everything you create is disposable — cancel it at the end, or leave it and say
so. **The two seeded Pending requests on the 3D Printer are the owner's and
should be left alone**; every automated probe in this package has left them
untouched, and the queue should read exactly two before you start.

---

## Path A — the member

This is the acceptance criterion. Sign in as **member1**.

### A1 · Sign in
- [ ] `/login` accepts the credentials and lands you somewhere authenticated.
- [ ] You land on the **calendar**, not a dashboard or a blank home. It is the
      landing screen since Phase 4.

### A2 · The calendar as a first impression
- [ ] Month and week views both render, and switching between them keeps the
      date you were looking at.
- [ ] The URL carries the view and date (`?view=…&date=…`), and **pasting that
      URL into a new tab shows the same thing**.
- [ ] **Chips sit at their stated times.** Look at a chip in week view and read
      its time against the hour axis beside it. *Three separate layout faults
      were found here by eye and none by the suite — jsdom does no layout, so
      the emitted markup is identical whether this is right or wrong.*
- [ ] Day columns line up with their day headers.
- [ ] A short booking's label is readable rather than clipped.

### A3 · Browse
- [ ] Navigate to **Resources**. Both Conference Room A and the 3D Printer are
      listed; the archived probe resource is not.
- [ ] The search box narrows after you stop typing, not on every keystroke.
- [ ] The type filter and the approval filter both change the list.
- [ ] The 3D Printer is marked as needing approval; Conference Room A is not.

### A4 · A resource, then its availability
- [ ] Click **Conference Room A's title** — it is a real link, so middle-click
      or ctrl-click opens it in a new tab.
- [ ] The detail screen names its timezone as `America/New_York`.
- [ ] Follow **Check availability**.
- [ ] Navigate to **Mon 28 Sep 2026**. The day shows bookable hours.
- [ ] **Read the hours carefully.** They should be unambiguous about which
      clock they are on — a naive reading would show the room's 09:00 as your
      09:00, which would be six hours wrong.

### A5 · Pick a slot
- [ ] Select roughly **10:00–11:00 on the room's clock**, by dragging or with
      the Start/End dropdowns.
- [ ] **The dropdowns show the time you actually selected.** *A `<select>`
      binding once displayed a different time than the one held — found by
      eye, invisible to the suite.*
- [ ] Press **Continue to booking**.

### A6 · The booking form
- [ ] The URL carries the slot as query parameters
      (`?startUtc=…&endUtc=…&quantity=…`) — **this is the check**, since the
      whole point of moving off router state was that a chosen slot is
      shareable and visible.
- [ ] The form is pre-filled with the slot you picked, and the times match A5.
- [ ] Paste that URL into a new tab: the same slot comes back.
- [ ] Submit.

### A7 · Confirm
- [ ] The outcome says **Confirmed**, not Pending — Conference Room A needs no
      approval.
- [ ] It tells you where the booking now lives, and that link works.

### A8 · Back to the calendar
- [ ] Your booking is drawn on the right day, at the right time **on your own
      clock**. If you picked the room's 10:00–11:00, that is **16:00–17:00** in
      `Europe/Warsaw`: late September is EDT (UTC−4) in New York and CEST
      (UTC+2) here, so the gap is **six hours**, not five.

      *This script first said 15:00–16:00, which was wrong — it applied the
      window's own 09:00→15:00 mapping to a 10:00 start and quietly lost an
      hour. Corrected 2026-09-22 after the owner caught it.*
- [ ] Click the chip. The booking detail opens.

### A9 · The booking, read back
- [ ] It shows **both zone readings** — yours, and the room's — because they
      differ. This is the screen where that matters most.
- [ ] Duration, quantity and resource are right.
- [ ] There is **no "Requested by"** line. You are the owner; printing your own
      name back at you would be noise.

### A10 · Cancel
- [ ] **Cancel booking** asks for confirmation rather than acting on the first
      click.
- [ ] After cancelling, the screen says **you** cancelled it, with the time.
- [ ] Return to the calendar: the chip is **gone**.
- [ ] Press the browser **Back** button to the booking. It still renders, shows
      the cancellation, and does **not** offer to cancel it again.

**Criterion A passes if A1–A10 all hold.** ▢ pass ▢ fail — note which step:

---

## Path B — the approver

Sign out, sign in as **approver@acme.test**.

### B1 · The queue exists and is reachable
- [ ] A nav item **Approvals** is present (it is not, for a member).
- [ ] It opens a queue listing **two** waiting requests on the 3D Printer.
- [ ] Each row shows the resource, **who requested it**, when it is for, the
      quantity, and how long it has been waiting.
- [ ] The longest-waiting is at the top.
- [ ] **Known and accepted:** both seeded requests are for dates already past
      (17 and 18 September), because nothing expires stale approvals yet. They
      are shown with no marker saying so. Confirm it looks odd rather than
      broken — that is the accepted behaviour, not a new bug.

### B2 · Open one
- [ ] Click **View booking** on a row. It opens. *This is the one that answered
      404 until decision `0027` — worth a moment's attention.*
- [ ] It says the request is **waiting for a decision** and the time is **not
      held yet**. It must **not** say "not held for **you**" — it is not your
      booking.
- [ ] There **is** a "Requested by" line naming the member.
- [ ] There is **no cancel action**. An approver may read, not cancel.
- [ ] The way back says **Back to approvals**, not "back to your calendar" —
      the booking is not on your calendar.

### B3 · Decide from the booking
- [ ] A **Your decision** panel offers Approve and Reject.
- [ ] Approve asks for confirmation and offers an optional note.
- [ ] Add a note and approve.
- [ ] The screen updates to show the booking **Confirmed**, and the approval
      section now carries your decision, the time, and your note.

### B4 · Decide from the queue
- [ ] Back on `/approvals`, the row you decided is **gone** and the count has
      dropped.
- [ ] The remaining row has its own Approve/Reject inline.
- [ ] **Reject** it, with a note.
- [ ] That row leaves too. The queue now says **"Nothing is waiting for you"** —
      in a sentence, not a shrug.

### B5 · Put one back (so the seed is not left empty)
- [ ] As **member1**, book the **3D Printer** for Mon 28 Sep, any hour.
- [ ] It comes back **Pending**, not Confirmed, and says so.
- [ ] As the approver, it is in the queue again.

**Criterion B passes if B1–B5 hold.** ▢ pass ▢ fail — note which step:

---

## Path C — the deliberate wrong turns

**This is where this package's bugs actually lived.** None of these is an
unreasonable thing for a real person to do.

### C1 · Hand-edit the quantity
On the booking form for Conference Room A (capacity 1), change `quantity=1` to
`quantity=16` in the address bar and reload.
- [ ] It must **not** silently book one unit. Either it refuses with a message
      naming the problem, or the form shows the real number it will send —
      never accept 16 and create a booking for 1.
      *That silent-clamp is a bug that shipped once and was found by eye.*

### C2 · Hand-edit the dates
Change `startUtc` to something outside the room's hours, or to the past.
- [ ] You get a specific reason — outside availability, in the past — not a
      generic failure.

### C3 · A slot that has just gone
In two tabs, open the booking form on the **same** Conference Room A slot.
Submit one, then submit the other.
- [ ] The second is refused, clearly, as the slot being taken — and it does
      **not** offer a retry that would just lose again.

### C4 · Decide the same request twice
**Two tabs in the same browser will not do this**, and that is not a bug to
file: tokens live in `localStorage`, which is shared per origin, so signing in
as the admin in a second tab signs the approver out of the first. That is a
direct consequence of decision `0011`'s accepted storage choice, and two
simultaneous identities in one browser is not a use case this app has.

Use **one normal window and one private/incognito window** — separate storage,
so two identities coexist. Sign in as the approver in one and the admin in the
other, open the same pending request in both, and approve in both.

- [ ] One succeeds. The other says the request has **already been decided** —
      not a generic error, not silence.

*Already proven at the API level on 2026-09-21: fired simultaneously, the
approver got `200 Confirmed` and the admin `422 BookingNotPending`. So the race
itself is covered; what this step adds is whether the **screen** says something
useful when it loses. Skip it if the private window is a nuisance — the loss is
one UI reading of an outcome that is otherwise verified.*

### C5 · A stale tab
Leave the approvals queue open, decide the request from another tab, then act on
the stale row.
- [ ] Refused with "already been decided", and the screen recovers rather than
      wedging.

### C6 · A direct link to something cancelled
Paste the URL of the booking you cancelled in A10.
- [ ] It renders, and tells you what happened and when.

### C7 · A booking that is not yours
As **member2@acme.test**, paste the URL of member1's booking.
- [ ] Not found — and the wording does **not** reveal whether it exists.

### C8 · Approvals without the right role
As **member1**, type `/approvals` directly.
- [ ] You are bounced to the calendar, and the nav never offered the link.

### C9 · A link from before the rename
Type `/my-bookings` — a route removed in Phase 4.
- [ ] You land on the **calendar** rather than a dead end or a sign-in form.

### C10 · Refresh everywhere
On each screen in turn, press F5.
- [ ] Every one comes back to the same place. None bounces you to `/login` with
      a valid session.

**Path C passes if C1–C10 hold.** ▢ pass ▢ fail — note which:

---

## If something is wrong

Note **the screen, what you did, what you saw, and what you expected** — that is
enough. Do not work around it; a workaround hides the size of the problem.

If it is cosmetic rather than wrong, it probably belongs to the outstanding
design pass (the calendar, the booking detail, the cancel confirmation and the
approval queue were all built without a provided design) rather than to a bug
list.

## Afterwards

- [ ] Cancel anything you created, or say what you left behind.
- [ ] The 3D Printer queue should end with at least one waiting request (B5).
- [ ] Tell me the result; step 5 records it and closes WP-7.
