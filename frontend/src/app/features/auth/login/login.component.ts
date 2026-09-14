import { Component, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../../core/auth/auth.service';

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule],
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

  constructor(
    formBuilder: FormBuilder,
    private readonly auth: AuthService,
    private readonly router: Router,
  ) {
    this.form = formBuilder.nonNullable.group({
      email: ['', [Validators.required, Validators.email]],
      password: ['', Validators.required],
    });
  }

  protected async submit(): Promise<void> {
    if (this.form.invalid || this.submitting()) {
      return;
    }

    this.submitting.set(true);
    this.errorMessage.set(null);

    const { email, password } = this.form.getRawValue();

    try {
      await this.auth.login(email, password);
      await this.router.navigateByUrl('/home');
    } catch {
      // A real per-field/reason-code mapping is phase 4's job (CLAUDE.md §6 /
      // decisions/0016) — this is deliberately the generic placeholder until then.
      this.errorMessage.set('Incorrect email or password.');
    } finally {
      this.submitting.set(false);
    }
  }
}
