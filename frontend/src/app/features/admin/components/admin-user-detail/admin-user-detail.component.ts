import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, WritableSignal, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable, Subject, catchError, distinctUntilChanged, filter, map, merge, of, switchMap } from 'rxjs';
import { BreadcrumbService } from '../../../../layout/breadcrumb.service';
import { UsersService } from '../../services/users.service';
import { ASSIGNABLE_ROLES, UserDetail, UserRole } from '../../models/users.models';
import { UserRejection, describeUserDetailRejection } from '../../rejection/user-rejection';

const ASSIGNABLE_ROLE_SET: ReadonlySet<string> = new Set(ASSIGNABLE_ROLES);

// Roles this screen has a control for. Any other role a row could in principle
// carry (only `SysAdmin`, and only in theory — a tenant user can never hold
// it) is dropped rather than crashing the checkbox group, the same defensive
// stance `applyFields` in the resource form takes toward an unrecognized
// timezone id.
//
// **`Member` is always included** (hardening pass, 2026-09-25, finding 6):
// every tenant user carries it, whatever the server actually sent — the same
// self-healing normalization `admin@acme.test`/`approver@acme.test` (seeded
// before this rule existed) pick up the moment their roles are next saved
// through this screen.
function toAssignableRoles(roles: readonly UserRole[]): Set<UserRole> {
  return new Set([...roles.filter((role) => ASSIGNABLE_ROLE_SET.has(role)), 'Member']);
}

function setsEqual<T>(a: ReadonlySet<T>, b: ReadonlySet<T>): boolean {
  return a.size === b.size && [...a].every((value) => b.has(value));
}

type UserLoadResult = { kind: 'success'; user: UserDetail } | { kind: 'error'; error: unknown };

// User management phase 7. Roles, status, and the confirmations — deactivate,
// reactivate, and (2026-09-25 hardening pass, finding 3) resend invitation.
//
// **The initial load does not go through `user-rejection.ts` at all.** That
// dialect describes a refused *write*; loading this screen is a read, and its
// only failure worth naming specially is 404 — checked with a plain
// `error.status === 404`, mirroring `BookingDetailComponent`'s own load rather
// than the shared `resourceNotFound` machinery, which is wired to the literal
// code `ResourceNotFound` and cannot fire for `UserNotFound`.
//
// **The route id is observed, not read once** (hardening pass, 2026-09-25,
// finding 5). Angular reuses this component if it is ever navigated from one
// person's detail screen straight to another's — same route config, only
// `:id` differs — and a snapshot taken once at construction would go stale:
// the loaded data, the roles editor's selection and every in-flight write
// would keep answering for the *first* person while the URL and breadcrumb
// already said the second. `switchMap` is what makes a stale response
// impossible rather than merely ignored — a route-id change cancels whatever
// fetch is still in flight (switchMap unsubscribes the previous inner
// Observable, aborting the underlying HTTP request) before starting the next
// one, so an older response can never arrive after a newer one and overwrite
// it. Matches `ResourceDetailComponent`'s own fix from the 2026-09-16
// hardening pass.
//
// **Roles are replace-the-set**, following `PUT /users/{id}/roles` exactly —
// `admin-plan.md` §4.1's rule that the API's shape decides the screen's, the
// same call the approvers editor made. A checkbox group saved in one go, never
// per-role toggles that each round-trip.
//
// **`SysAdmin` is never offered.** `ASSIGNABLE_ROLES` excludes it — the
// validator refuses it outright as a privilege-escalation guard (PRD §2,
// decision `0012`), and a control that could send it would only ever come back
// 400. **`Member` cannot be unticked either** (finding 6): every tenant user
// carries it regardless of any other role, so the checkbox is always checked
// and disabled — matching the backend validator, which now refuses a set that
// omits it.
//
// **Deactivate and reactivate are two separate, lightweight confirmations, not
// one.** Deactivating a colleague is the one an administrator can genuinely get
// wrong, so its panel states both facts worth knowing before doing it: the
// access-token tail (§4.4 — up to 15 minutes, not a bug) and that their existing
// bookings are not touched (§4.7). Reactivating is the reverse of a reversible
// state, so its panel is a plain "are you sure", the same distinction
// `admin-resource-form.component.ts` draws between an irreversible archive
// (an acknowledgement tick) and a reversible one (a plain confirm).
//
// **"Resend invitation" (finding 3) needs no confirmation at all** — it is
// low-stakes (the worst case is a second email) and, unlike deactivate, cannot
// be un-done in a way that matters, so a plain button with its own loading and
// result state is enough. It is shown only when `isActivated` is false and the
// account is active — resending to an already-activated or deactivated account
// is refused server-side, and the control should not be offered only to be
// explained away.
@Component({
  selector: 'app-admin-user-detail',
  imports: [RouterLink],
  templateUrl: './admin-user-detail.component.html',
  styleUrl: './admin-user-detail.component.scss',
})
export class AdminUserDetailComponent {
  private readonly usersService = inject(UsersService);
  private readonly route = inject(ActivatedRoute);
  private readonly breadcrumbService = inject(BreadcrumbService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly assignableRoles = ASSIGNABLE_ROLES;

  private userId: string;

  // Fed by retry() alongside route-id changes, so both funnel through the same
  // switchMap below rather than each racing it with a separate call to a
  // shared load() method — the same shape ResourceDetailComponent uses.
  private readonly retry$ = new Subject<void>();

  // ---- Load ----

  protected readonly loading = signal(true);
  protected readonly loadError = signal(false);
  protected readonly notFound = signal(false);
  protected readonly user = signal<UserDetail | null>(null);

  protected readonly heading = computed(() => this.user()?.fullName ?? 'Person');
  protected readonly isActive = computed(() => this.user()?.isActive ?? true);

  protected readonly canResendInvitation = computed(() => {
    const current = this.user();
    return current !== null && current.isActive && !current.isActivated;
  });

  // ---- Roles ----

  // What the server last confirmed — what "has anything changed" is judged
  // against, and what a cancelled edit reverts to.
  private readonly savedRoles = signal<ReadonlySet<UserRole>>(new Set());
  protected readonly selectedRoles = signal<ReadonlySet<UserRole>>(new Set());

  protected readonly rolesDirty = computed(() => !setsEqual(this.selectedRoles(), this.savedRoles()));

  protected readonly savingRoles = signal(false);
  protected readonly rolesSaved = signal(false);
  protected readonly rolesRejection = signal<UserRejection | null>(null);

  // `rolesEmpty` no longer varies through this screen's own UI — Member can
  // never be unticked (finding 6) — but the computed stays as the client-side
  // echo of the validator's own rule, in case a future change to the checkbox
  // group ever makes an empty selection reachable again.
  protected readonly rolesEmpty = computed(() => this.selectedRoles().size === 0);

  protected readonly canSaveRoles = computed(
    () => !this.savingRoles() && this.rolesDirty() && !this.rolesEmpty(),
  );

  // ---- Deactivate / reactivate ----

  protected readonly confirmingDeactivate = signal(false);
  protected readonly confirmingReactivate = signal(false);
  protected readonly statusChanging = signal(false);
  protected readonly statusRejection = signal<UserRejection | null>(null);

  // ---- Resend invitation ----

  protected readonly resending = signal(false);
  protected readonly resendResult = signal<{ invitationEmailSent: boolean } | null>(null);
  protected readonly resendRejection = signal<UserRejection | null>(null);

  constructor() {
    const initialId = this.route.snapshot.paramMap.get('id');
    if (!initialId) {
      throw new Error('AdminUserDetailComponent requires an "id" route param.');
    }
    this.userId = initialId;

    const idChanges$ = this.route.paramMap.pipe(
      map((params) => params.get('id')),
      filter((id): id is string => !!id),
      distinctUntilChanged(),
    );

    merge(idChanges$, this.retry$.pipe(map(() => this.userId)))
      .pipe(
        map((id) => {
          this.userId = id;
          return id;
        }),
        switchMap((id) => this.fetchUser$(id)),
        takeUntilDestroyed(),
      )
      .subscribe((result) => this.applyResult(result));

    // The override has to be cleared, or this person's name lingers onto
    // whatever page the breadcrumb walk computes next — the same reason
    // `admin-resource-form.component.ts` clears its own on destroy.
    this.destroyRef.onDestroy(() => this.breadcrumbService.setOverride(null));
  }

  // ---- Roles ----

  // "Administrator" reads better than "TenantAdmin" to the person choosing it;
  // the other two are already plain English.
  protected roleDisplayLabel(role: UserRole): string {
    return role === 'TenantAdmin' ? 'Administrator' : role;
  }

  protected toggleRole(role: UserRole): void {
    // Member is mandatory (finding 6) — the checkbox renders disabled, but
    // this is the line that makes it actually true rather than merely
    // decorative if something ever calls this directly.
    if (role === 'Member') {
      return;
    }

    this.selectedRoles.update((current) => {
      const next = new Set(current);
      if (next.has(role)) {
        next.delete(role);
      } else {
        next.add(role);
      }
      return next;
    });

    this.rolesSaved.set(false);
    this.rolesRejection.set(null);
  }

  protected saveRoles(): void {
    if (!this.canSaveRoles()) {
      return;
    }

    this.savingRoles.set(true);
    this.rolesSaved.set(false);
    this.rolesRejection.set(null);

    this.usersService
      .replaceRoles(this.userId, { roles: [...this.selectedRoles()] })
      .subscribe({
        next: (response) => {
          this.savingRoles.set(false);
          this.rolesSaved.set(true);

          // Re-seeded from the response, never from the selection — the
          // server is what decided what was actually stored, and its order is
          // not this component's to guess at.
          const assignable = toAssignableRoles(response.roles);
          this.savedRoles.set(assignable);
          this.selectedRoles.set(assignable);

          this.applyWriteResult(response);
        },
        error: (error: unknown) => this.handleWriteError(error, this.rolesRejection),
      });
  }

  // ---- Deactivate ----

  protected startConfirmingDeactivate(): void {
    this.confirmingDeactivate.set(true);
    this.statusRejection.set(null);
  }

  protected cancelConfirmingDeactivate(): void {
    this.confirmingDeactivate.set(false);
  }

  protected confirmDeactivate(): void {
    if (this.statusChanging()) {
      return;
    }

    this.statusChanging.set(true);
    this.statusRejection.set(null);

    this.usersService.deactivate(this.userId).subscribe({
      next: (response) => {
        this.statusChanging.set(false);
        this.confirmingDeactivate.set(false);
        this.applyWriteResult(response);
      },
      error: (error: unknown) => this.handleWriteError(error, this.statusRejection),
    });
  }

  // ---- Reactivate ----

  protected startConfirmingReactivate(): void {
    this.confirmingReactivate.set(true);
    this.statusRejection.set(null);
  }

  protected cancelConfirmingReactivate(): void {
    this.confirmingReactivate.set(false);
  }

  protected confirmReactivate(): void {
    if (this.statusChanging()) {
      return;
    }

    this.statusChanging.set(true);
    this.statusRejection.set(null);

    this.usersService.reactivate(this.userId).subscribe({
      next: (response) => {
        this.statusChanging.set(false);
        this.confirmingReactivate.set(false);
        this.applyWriteResult(response);
      },
      error: (error: unknown) => this.handleWriteError(error, this.statusRejection),
    });
  }

  // ---- Resend invitation ----

  protected resendInvitation(): void {
    if (this.resending() || !this.canResendInvitation()) {
      return;
    }

    this.resending.set(true);
    this.resendResult.set(null);
    this.resendRejection.set(null);

    this.usersService.reissueInvitation(this.userId).subscribe({
      next: (result) => {
        this.resending.set(false);
        this.resendResult.set({ invitationEmailSent: result.invitationEmailSent });
      },
      error: (error: unknown) => {
        this.resending.set(false);
        this.handleWriteError(error, this.resendRejection);
      },
    });
  }

  // ---- Internals ----

  protected retryLoad(): void {
    this.retry$.next();
  }

  private fetchUser$(id: string): Observable<UserLoadResult> {
    this.loading.set(true);
    this.loadError.set(false);
    this.notFound.set(false);
    this.breadcrumbService.setOverride(null);

    // A route-id change is a new person, so nothing from the previous one's
    // in-flight writes or confirmations should still be showing once this
    // resolves — reusing the component (rather than destroying it) is exactly
    // what finding 5 is about, and a stale "Roles updated" banner or an open
    // deactivate confirmation for the *last* person would be the same class
    // of bug the load race itself is.
    this.savingRoles.set(false);
    this.rolesSaved.set(false);
    this.rolesRejection.set(null);
    this.confirmingDeactivate.set(false);
    this.confirmingReactivate.set(false);
    this.statusChanging.set(false);
    this.statusRejection.set(null);
    this.resending.set(false);
    this.resendResult.set(null);
    this.resendRejection.set(null);

    return this.usersService.getById(id).pipe(
      map((user) => ({ kind: 'success' as const, user })),
      catchError((error: unknown) => of({ kind: 'error' as const, error })),
    );
  }

  private applyResult(result: UserLoadResult): void {
    this.loading.set(false);
    if (result.kind === 'error') {
      if (result.error instanceof HttpErrorResponse && result.error.status === 404) {
        this.notFound.set(true);
      } else {
        this.loadError.set(true);
      }
      return;
    }

    this.user.set(result.user);
    const assignable = toAssignableRoles(result.user.roles);
    this.savedRoles.set(assignable);
    this.selectedRoles.set(assignable);
    this.breadcrumbService.setOverride(result.user.fullName);
  }

  // Shared by all three writes: reseed the loaded user from what the server
  // actually stored, so the status badge and the roles editor never show a
  // value only this client believes.
  private applyWriteResult(response: { isActive: boolean; roles: UserRole[] }): void {
    const current = this.user();
    if (current !== null) {
      this.user.set({ ...current, isActive: response.isActive, roles: response.roles });
    }
  }

  private handleWriteError(error: unknown, target: WritableSignal<UserRejection | null>): void {
    this.savingRoles.set(false);
    this.statusChanging.set(false);

    target.set(describeUserDetailRejection(error));

    // The account left this administrator's reach mid-edit (CLAUDE.md §4.5:
    // never through an actual delete, but the AC-4 answer is identical either
    // way) — nothing on this screen is left to save. Checked directly against
    // the status, not by sniffing the rejection's message text, which is a
    // fact about copy and not something this branch should depend on.
    if (error instanceof HttpErrorResponse && error.status === 404) {
      this.notFound.set(true);
    }
  }
}
