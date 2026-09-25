import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { UsersService } from '../../services/users.service';
import { CreatedUser } from '../../models/users.models';
import { UserRejection, describeUserRejection } from '../../rejection/user-rejection';

// User management phase 6. Invite a colleague.
//
// **Two screens in one component, and the second is the interesting one.** The
// form is ordinary; the outcome is not, because `POST /users` hands back a live
// activation link and this is the only moment it will ever exist outside the
// recipient's mailbox (plan §4.3). So the outcome replaces the form rather than
// sitting under it — a still-editable form beside a credential invites a second
// submit, and a second submit creates a second account.
//
// The link is deliberately **not** persisted anywhere: no storage, no service
// cache, no route state. Navigating away loses it, and the screen says so
// rather than pretending otherwise, because the honest instruction is "copy it
// now" and anything that made it recoverable would be a second place a
// credential lives.
//
// Create only. Editing a person — roles, deactivation — is phase 7's detail
// screen, and it is a genuinely different shape: this form writes two fields
// once, that one writes three different endpoints repeatedly. The admin
// resource form merged create and edit because every field was shared; here
// almost none is.
//
// Three conventions inherited from WP-7 and not re-litigated: everything
// feeding a submit is disabled while it is in flight, the outcome renders from
// what came back rather than from form state, and a server field message
// outranks a client one until its own control is edited.

// Mirrors CreateUserCommandRequestValidator's caps, which are themselves the
// column widths — so an oversized value is a message under the control rather
// than a 400 round trip. The server is still the authority; these are the
// message, not the guarantee.
const EMAIL_MAX_LENGTH = 320;
const FULL_NAME_MAX_LENGTH = 200;

@Component({
  selector: 'app-admin-user-form',
  imports: [RouterLink, DatePipe],
  templateUrl: './admin-user-form.component.html',
  styleUrl: './admin-user-form.component.scss',
})
export class AdminUserFormComponent {
  private readonly usersService = inject(UsersService);

  protected readonly emailMaxLength = EMAIL_MAX_LENGTH;
  protected readonly fullNameMaxLength = FULL_NAME_MAX_LENGTH;

  protected readonly email = signal('');
  protected readonly fullName = signal('');

  protected readonly submitting = signal(false);
  protected readonly rejection = signal<UserRejection | null>(null);

  // The 201 body. Non-null means the outcome screen is showing — there is no
  // separate "mode" flag, because the two cannot disagree if only one thing
  // says which screen is up.
  protected readonly created = signal<CreatedUser | null>(null);

  protected readonly linkCopied = signal(false);

  // A server message outranks the client's until the control is edited, which
  // is why each of these checks the rejection first: the client cannot know the
  // address is taken, and overwriting that with "Email is required" the moment
  // the user tabs away would lose the only useful thing on screen.
  protected readonly emailError = computed(() => {
    const serverMessage = this.rejection()?.fieldMessages.email;
    if (serverMessage) {
      return serverMessage;
    }

    const value = this.email().trim();
    if (!value) {
      return null;
    }
    if (value.length > EMAIL_MAX_LENGTH) {
      return `An email address cannot be longer than ${EMAIL_MAX_LENGTH} characters.`;
    }
    // Deliberately shallow. The server's own rule is FluentValidation's
    // `.EmailAddress()` plus "no whitespace" (phase 3), and a stricter client
    // regex would refuse addresses the API accepts — being stricter than the
    // endpoint you write to is a bug, the same call the availability editor
    // made about adjacency.
    if (!value.includes('@') || /\s/.test(value)) {
      return 'Enter an email address — no spaces.';
    }
    return null;
  });

  protected readonly fullNameError = computed(() => {
    const serverMessage = this.rejection()?.fieldMessages.fullName;
    if (serverMessage) {
      return serverMessage;
    }

    if (this.fullName().trim().length > FULL_NAME_MAX_LENGTH) {
      return `A name cannot be longer than ${FULL_NAME_MAX_LENGTH} characters.`;
    }
    return null;
  });

  protected readonly formMessage = computed(() => this.rejection()?.formMessage ?? null);
  protected readonly outcomeUnknown = computed(() => this.rejection()?.outcomeUnknown ?? false);

  protected readonly canSubmit = computed(
    () =>
      !this.submitting()
      && this.email().trim().length > 0
      && this.fullName().trim().length > 0
      && !this.emailError()
      && !this.fullNameError(),
  );

  protected onEmailInput(event: Event): void {
    this.email.set((event.target as HTMLInputElement).value);
    this.clearServerMessageFor('email');
  }

  protected onFullNameInput(event: Event): void {
    this.fullName.set((event.target as HTMLInputElement).value);
    this.clearServerMessageFor('fullName');
  }

  protected submit(): void {
    if (!this.canSubmit()) {
      return;
    }

    this.submitting.set(true);
    this.rejection.set(null);

    this.usersService
      .create({ email: this.email().trim(), fullName: this.fullName().trim() })
      .subscribe({
        next: (created) => {
          this.submitting.set(false);
          this.created.set(created);
        },
        error: (error: unknown) => {
          this.submitting.set(false);
          this.rejection.set(describeUserRejection(error));
        },
      });
  }

  // Back to an empty form for the next person. The link is dropped with the
  // rest of the outcome, which is the point — it was shown once.
  protected inviteAnother(): void {
    this.created.set(null);
    this.linkCopied.set(false);
    this.email.set('');
    this.fullName.set('');
    this.rejection.set(null);
  }

  protected async copyLink(): Promise<void> {
    const link = this.created()?.activationLink;
    if (!link) {
      return;
    }

    try {
      await navigator.clipboard.writeText(link);
      this.linkCopied.set(true);
    } catch {
      // Clipboard access can be refused outright (an insecure origin, a denied
      // permission, an older browser). Swallowed rather than surfaced as a
      // failure: the link is on screen and selectable, so the administrator has
      // lost nothing except the shortcut. Telling them the copy failed would
      // imply the *invitation* had.
      this.linkCopied.set(false);
    }
  }

  // A rejection carries a message per control, and it has to stop being shown
  // once that control changes — otherwise "that address is already taken" sits
  // under an address the administrator has just corrected.
  private clearServerMessageFor(field: 'email' | 'fullName'): void {
    const rejection = this.rejection();
    if (rejection?.fieldMessages[field]) {
      this.rejection.set(null);
    }
  }
}
