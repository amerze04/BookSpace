import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { ActivationService } from '../../services/activation.service';
import { PASSWORD_MAX_LENGTH, PASSWORD_MIN_LENGTH } from '../../password-policy';
import { BrandMarkComponent } from '../../../../shared/brand-mark/brand-mark.component';

// Where an emailed invitation lands. The recipient's first contact with this
// application, and the only screen in it they reach without an account.
//
// **This screen was missing from the plan**, and was found by the owner pasting
// a link on 2026-09-25. `docs/user-management-plan.md` §8 listed three screens
// to build — directory, invite form, user detail — and phase 8 opens "No new
// screens", while its own click-through requires "a real link followed, a
// password set, and a first sign-in". Activation tokens entered scope in §3.1
// as a *consequence* of the emailed-invitation decision; that consequence was
// tracked on the backend and never propagated to the screen list. Built as its
// own phase between 6 and 7, on the owner's call.
//
// **Anonymous, and outside the shell** — like /login, unlike everything else in
// this package. Deliberately *not* behind `guestOnlyGuard`: somebody already
// signed in on a shared machine must still be able to redeem their own link,
// and the backend supports exactly that (the auth interceptor attaches their
// bearer token, and `POST /auth/activate` handles a mismatched tenant rather
// than failing — user management phase 2 built and tested that case).
//
// On success it goes to `/login?activated=1` rather than signing anybody in.
// That is what the endpoint does — 204, no session — and it keeps session
// minting in the one handler that owns FR-2.4's active-user and suspended-org
// checks. Owner's call, 2026-09-25.

type FieldName = 'password' | 'confirmPassword';

@Component({
  selector: 'app-activate',
  imports: [BrandMarkComponent],
  templateUrl: './activate.component.html',
  styleUrl: './activate.component.scss',
})
export class ActivateComponent {
  private readonly activation = inject(ActivationService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly passwordMinLength = PASSWORD_MIN_LENGTH;
  protected readonly passwordMaxLength = PASSWORD_MAX_LENGTH;

  // Read once, from the URL the recipient arrived on. Never stored, never put
  // in a service, never logged — it is a credential with one use, and the only
  // place it belongs is the request body.
  private readonly token = this.route.snapshot.queryParamMap.get('token') ?? '';

  // A link with no token at all is not something to ask for a password about.
  // Its own state rather than an error under a control, because there is no
  // control that could fix it.
  protected readonly tokenMissing = this.token.length === 0;

  protected readonly password = signal('');
  protected readonly confirmPassword = signal('');
  protected readonly submitting = signal(false);
  protected readonly errorMessage = signal<string | null>(null);

  // Only after the control has been left, so somebody typing their twelfth
  // character is not told off for having eleven.
  private readonly touched = signal<Set<FieldName>>(new Set());

  protected readonly passwordError = computed(() => {
    if (!this.touched().has('password')) {
      return null;
    }

    const value = this.password();
    if (value.length === 0) {
      return 'Choose a password.';
    }
    if (value.length < PASSWORD_MIN_LENGTH) {
      return `Use at least ${PASSWORD_MIN_LENGTH} characters.`;
    }
    if (value.length > PASSWORD_MAX_LENGTH) {
      return `Use at most ${PASSWORD_MAX_LENGTH} characters.`;
    }
    return null;
  });

  // A confirm field is not something the server knows about — it sends one
  // password. This exists because the recipient is inventing a credential they
  // will need again and cannot recover: there is no password reset in this
  // application (plan §3.4), so a typo here is permanent.
  protected readonly confirmError = computed(() => {
    if (!this.touched().has('confirmPassword')) {
      return null;
    }
    return this.confirmPassword() === this.password() ? null : 'Both passwords must match.';
  });

  protected readonly canSubmit = computed(
    () =>
      !this.submitting()
      && !this.tokenMissing
      && this.password().length >= PASSWORD_MIN_LENGTH
      && this.password().length <= PASSWORD_MAX_LENGTH
      && this.confirmPassword() === this.password(),
  );

  protected markTouched(field: FieldName): void {
    this.touched.update((current) => new Set(current).add(field));
  }

  protected onPasswordInput(event: Event): void {
    this.password.set((event.target as HTMLInputElement).value);
    this.errorMessage.set(null);
  }

  protected onConfirmInput(event: Event): void {
    this.confirmPassword.set((event.target as HTMLInputElement).value);
    this.errorMessage.set(null);
  }

  protected submit(): void {
    if (!this.canSubmit()) {
      this.touched.set(new Set<FieldName>(['password', 'confirmPassword']));
      return;
    }

    this.submitting.set(true);
    this.errorMessage.set(null);

    this.activation.activate(this.token, this.password()).subscribe({
      next: () => {
        this.submitting.set(false);
        // In the URL rather than router state or a service, so the signed-in
        // banner survives a refresh and the two screens share nothing but an
        // address.
        void this.router.navigate(['/login'], { queryParams: { activated: 1 } });
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        this.errorMessage.set(describeActivationFailure(error));
      },
    });
  }
}

// **One message for every way a token can be refused, and that is the point.**
//
// The server answers `401 InvalidActivationToken` identically whether the link
// expired, was already used, never existed, or belongs to an account that has
// since been deactivated — deliberately, so the endpoint cannot be used to
// discover which invitations are outstanding (user management plan §4.2,
// inheriting decision `0018`'s reasoning). Saying more here than the server
// said would give away exactly what it withheld.
//
// So this is a short function rather than a tenth `RejectionDialect`: there is
// one reason code, no field to point at, and no vocabulary to map. A dialect
// would be machinery around a single string.
function describeActivationFailure(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    // Checked before anything else, as `handleLoginError` does: status 0 means
    // the request reached no server at all, and the body is a ProgressEvent
    // rather than a ProblemDetails.
    if (error.status === 0) {
      return 'Unable to reach the server. Check your connection and try again.';
    }

    if (error.status === 429) {
      return 'Too many attempts. Please wait a moment and try again.';
    }

    if (error.status >= 500) {
      return 'Something went wrong on our end. Please try again shortly.';
    }

    if (error.status === 400) {
      // The only 400 this endpoint produces is the password policy, and the
      // control already says what the rule is.
      return `Choose a password between ${PASSWORD_MIN_LENGTH} and ${PASSWORD_MAX_LENGTH} characters.`;
    }
  }

  // 401, and anything else with nothing better to say. It names no cause on
  // purpose — and tells them what to do instead, because there is no way to
  // re-issue an invitation (plan §6) and "ask your administrator" is genuinely
  // the only route left.
  return 'This link is no longer valid. It may have expired or already been used — '
    + 'ask your administrator to invite you again.';
}
