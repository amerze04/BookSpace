import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, WritableSignal, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
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
function toAssignableRoles(roles: readonly UserRole[]): Set<UserRole> {
  return new Set(roles.filter((role) => ASSIGNABLE_ROLE_SET.has(role)));
}

function setsEqual<T>(a: ReadonlySet<T>, b: ReadonlySet<T>): boolean {
  return a.size === b.size && [...a].every((value) => b.has(value));
}

// User management phase 7. Roles, status, and the two confirmations —
// deactivate and reactivate.
//
// **The initial load does not go through `user-rejection.ts` at all.** That
// dialect describes a refused *write*; loading this screen is a read, and its
// only failure worth naming specially is 404 — checked with a plain
// `error.status === 404`, mirroring `BookingDetailComponent`'s own load rather
// than the shared `resourceNotFound` machinery, which is wired to the literal
// code `ResourceNotFound` and cannot fire for `UserNotFound`.
//
// **Roles are replace-the-set**, following `PUT /users/{id}/roles` exactly —
// `admin-plan.md` §4.1's rule that the API's shape decides the screen's, the
// same call the approvers editor made. A checkbox group saved in one go, never
// per-role toggles that each round-trip.
//
// **`SysAdmin` is never offered.** `ASSIGNABLE_ROLES` excludes it — the
// validator refuses it outright as a privilege-escalation guard (PRD §2,
// decision `0012`), and a control that could send it would only ever come back
// 400.
//
// **Deactivate and reactivate are two separate, lightweight confirmations, not
// one.** Deactivating a colleague is the one an administrator can genuinely get
// wrong, so its panel states both facts worth knowing before doing it: the
// access-token tail (§4.4 — up to 15 minutes, not a bug) and that their existing
// bookings are not touched (§4.7). Reactivating is the reverse of a reversible
// state, so its panel is a plain "are you sure", the same distinction
// `admin-resource-form.component.ts` draws between an irreversible archive
// (an acknowledgement tick) and a reversible one (a plain confirm).
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

  // Read once: this is a form a route id does not change underneath, the same
  // call `admin-resource-form.component.ts` makes for its own id.
  private readonly userId = this.route.snapshot.paramMap.get('id')!;

  // ---- Load ----

  protected readonly loading = signal(true);
  protected readonly loadError = signal(false);
  protected readonly notFound = signal(false);
  protected readonly user = signal<UserDetail | null>(null);

  protected readonly heading = computed(() => this.user()?.fullName ?? 'Person');
  protected readonly isActive = computed(() => this.user()?.isActive ?? true);

  // ---- Roles ----

  // What the server last confirmed — what "has anything changed" is judged
  // against, and what a cancelled edit reverts to.
  private readonly savedRoles = signal<ReadonlySet<UserRole>>(new Set());
  protected readonly selectedRoles = signal<ReadonlySet<UserRole>>(new Set());

  protected readonly rolesDirty = computed(() => !setsEqual(this.selectedRoles(), this.savedRoles()));

  // A clear client-side echo of the one rule the validator states outright
  // (`ReplaceUserRolesCommandRequestValidator`): a user cannot be left with no
  // roles at all, because `TenantMember` needs only the `orgId` claim, so the
  // row would lie about what the account can do. Refused, not clamped — the
  // save button stays off rather than silently keeping the last-unchecked box
  // ticked.
  protected readonly rolesEmpty = computed(() => this.selectedRoles().size === 0);

  protected readonly savingRoles = signal(false);
  protected readonly rolesSaved = signal(false);
  protected readonly rolesRejection = signal<UserRejection | null>(null);

  protected readonly canSaveRoles = computed(
    () => !this.savingRoles() && this.rolesDirty() && !this.rolesEmpty(),
  );

  // ---- Deactivate / reactivate ----

  protected readonly confirmingDeactivate = signal(false);
  protected readonly confirmingReactivate = signal(false);
  protected readonly statusChanging = signal(false);
  protected readonly statusRejection = signal<UserRejection | null>(null);

  constructor() {
    this.load();

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

  // ---- Internals ----

  protected retryLoad(): void {
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.loadError.set(false);
    this.notFound.set(false);

    this.usersService.getById(this.userId).subscribe({
      next: (user) => {
        this.user.set(user);
        const assignable = toAssignableRoles(user.roles);
        this.savedRoles.set(assignable);
        this.selectedRoles.set(assignable);
        this.loading.set(false);
        this.breadcrumbService.setOverride(user.fullName);
      },
      error: (error: unknown) => {
        this.loading.set(false);
        if (error instanceof HttpErrorResponse && error.status === 404) {
          this.notFound.set(true);
        } else {
          this.loadError.set(true);
        }
      },
    });
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
