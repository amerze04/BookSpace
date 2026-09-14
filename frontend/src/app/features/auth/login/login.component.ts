import { Component, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { isProblemDetails } from '../../../core/http/problem-details';
import { AuthService } from '../../../core/auth/auth.service';
import { BrandMarkComponent } from '../../../shared/brand-mark/brand-mark.component';

type FieldName = 'email' | 'password';

// The backend reports FluentValidation failures under the C# property name
// ("Email", "Password" — confirmed against a real 400 response), not the
// form's own lowercase control names. This is the one place that translation
// happens, rather than every reader of `errors` needing to know about it.
const BACKEND_FIELD_NAMES: Record<string, FieldName> = { Email: 'email', Password: 'password' };

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, BrandMarkComponent],
  templateUrl: './login.component.html',
  styleUrl: './login.component.scss',
})
export class LoginComponent {
  protected readonly form;

  // Plain fields here would go stale on screen: this app has no zone.js, so a
  // write that happens after an `await` (i.e. everything in submit()'s catch/
  // finally) has nothing telling Angular to re-render unless it's a signal.
  protected readonly submitting = signal(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly fieldErrors = signal<Partial<Record<FieldName, string[]>>>({});

  // Tracked explicitly, rather than trusting FormControl.touched read
  // directly from the template — same reasoning as the fields above: this
  // component only re-renders in response to something it can point to, and
  // an explicit signal is that something.
  private readonly touchedFields = signal<Set<FieldName>>(new Set());

  constructor(
    formBuilder: FormBuilder,
    private readonly auth: AuthService,
    private readonly router: Router,
    private readonly route: ActivatedRoute,
  ) {
    this.form = formBuilder.nonNullable.group({
      email: ['', [Validators.required, Validators.email]],
      password: ['', Validators.required],
    });
  }

  protected markTouched(field: FieldName): void {
    this.touchedFields.update((current) => new Set(current).add(field));
  }

  // Server-reported errors take priority over the client-side check — if the
  // backend rejected the value, that's the more authoritative answer.
  protected fieldError(field: FieldName): string | null {
    const serverMessages = this.fieldErrors()[field];
    if (serverMessages?.length) {
      return serverMessages[0];
    }

    if (!this.touchedFields().has(field)) {
      return null;
    }

    const control = this.form.controls[field];
    if (control.hasError('required')) {
      return 'This field is required.';
    }
    if (control.hasError('email')) {
      return 'Enter a valid email address.';
    }
    return null;
  }

  protected async submit(): Promise<void> {
    if (this.submitting()) {
      return;
    }

    if (this.form.invalid) {
      this.touchedFields.set(new Set(['email', 'password']));
      return;
    }

    this.submitting.set(true);
    this.errorMessage.set(null);
    this.fieldErrors.set({});

    const { email, password } = this.form.getRawValue();

    try {
      await this.auth.login(email, password);
      const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl') ?? '/';
      await this.router.navigateByUrl(returnUrl);
    } catch (error) {
      this.handleLoginError(error);
    } finally {
      this.submitting.set(false);
    }
  }

  private handleLoginError(error: unknown): void {
    if (error instanceof HttpErrorResponse && isProblemDetails(error.error) && error.error.errors) {
      const mapped: Partial<Record<FieldName, string[]>> = {};
      for (const [backendField, messages] of Object.entries(error.error.errors)) {
        const field = BACKEND_FIELD_NAMES[backendField];
        if (field) {
          mapped[field] = messages;
        }
      }

      if (Object.keys(mapped).length > 0) {
        this.fieldErrors.set(mapped);
        return;
      }
    }

    // Covers InvalidCredentials and anything else with no field to blame —
    // deliberately generic, matching decisions/0011's "don't tell an attacker
    // which part was wrong" rule.
    this.errorMessage.set('Incorrect email or password.');
  }
}
