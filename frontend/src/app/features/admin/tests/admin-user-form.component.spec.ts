import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { AdminUserFormComponent } from '../components/admin-user-form/admin-user-form.component';
import { CreatedUser } from '../models/users.models';

// User management phase 6, the invite form. Reworked in the 2026-09-25
// hardening pass (finding 2): `POST /users` no longer hands back a raw
// activation link — a TenantAdmin holding it could redeem their new
// colleague's own invitation before the real recipient saw it — so the
// outcome now points at the person's own detail screen, where "Resend
// invitation" (finding 3) lives instead.
//
// The DOM is still asserted directly for the outcome: "is the form gone, is
// the right message on screen" is the actual question, and a signal-level
// check would pass with the template's branches swapped.

const API = 'http://localhost:5270';

type TestableForm = AdminUserFormComponent & {
  email: (value?: string) => string;
  fullName: () => string;
  submitting: () => boolean;
  created: () => CreatedUser | null;
  emailError: () => string | null;
  fullNameError: () => string | null;
  formMessage: () => string | null;
  outcomeUnknown: () => boolean;
  canSubmit: () => boolean;
  onEmailInput(event: Event): void;
  onFullNameInput(event: Event): void;
  submit(): void;
  inviteAnother(): void;
};

function fakeInputEvent(value: string): Event {
  return { target: { value } } as unknown as Event;
}

function created(overrides: Partial<CreatedUser> = {}): CreatedUser {
  return {
    id: 'u1',
    email: 'ada@acme.test',
    fullName: 'Ada Lovelace',
    isActive: true,
    roles: ['Member'],
    createdAtUtc: '2026-09-25T09:00:00Z',
    activationLinkExpiresAtUtc: '2026-10-02T09:00:00Z',
    invitationEmailSent: true,
    ...overrides,
  };
}

describe('AdminUserFormComponent', () => {
  let httpMock: HttpTestingController;

  function createFixture() {
    TestBed.configureTestingModule({
      imports: [AdminUserFormComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });

    httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(AdminUserFormComponent);
    fixture.detectChanges();
    return { fixture, component: fixture.componentInstance as TestableForm };
  }

  afterEach(() => {
    try {
      httpMock.verify();
    } finally {
      TestBed.resetTestingModule();
    }
  });

  function fill(component: TestableForm, email = 'ada@acme.test', fullName = 'Ada Lovelace'): void {
    component.onFullNameInput(fakeInputEvent(fullName));
    component.onEmailInput(fakeInputEvent(email));
  }

  // ---- The form's own wiring ----

  // **Regression, and the bug every other test in this file missed.** They all
  // call `component.submit()`, which exercises the handler and says nothing
  // about whether anything on screen reaches it. The template used
  // `(ngSubmit)`, an output of Angular's NgForm directive — and this component
  // imports neither FormsModule nor ReactiveFormsModule, so the directive never
  // attached, the binding listened for a DOM event that does not exist, and the
  // browser submitted the form natively instead. The page reloaded, the fields
  // cleared, and no request was ever made.
  //
  // So this one clicks the real button and asserts a request went out. Verified
  // against the broken template first: with `(ngSubmit)` restored it fails on
  // `expectOne`, because nothing is sent.
  it('submits when the button is clicked, not just when submit() is called', () => {
    const { fixture, component } = createFixture();
    fill(component);
    fixture.detectChanges();

    const button: HTMLButtonElement = fixture.nativeElement.querySelector('button[type="submit"]');
    button.click();

    const request = httpMock.expectOne(`${API}/users`);
    expect(request.request.body).toEqual({ email: 'ada@acme.test', fullName: 'Ada Lovelace' });
    request.flush(created());
  });

  // The other half of the same failure: a native submit reloads the page. The
  // handler has to stop it, or nothing the component does afterwards survives.
  it('prevents the browser from submitting the form itself', () => {
    const { fixture, component } = createFixture();
    fill(component);
    fixture.detectChanges();

    const form: HTMLFormElement = fixture.nativeElement.querySelector('form');
    const event = new Event('submit', { bubbles: true, cancelable: true });
    form.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(true);
    httpMock.expectOne(`${API}/users`).flush(created());
  });

  // ---- The form ----

  it('cannot be submitted until both fields are filled', () => {
    const { component } = createFixture();

    expect(component.canSubmit()).toBe(false);

    component.onFullNameInput(fakeInputEvent('Ada Lovelace'));
    expect(component.canSubmit()).toBe(false);

    component.onEmailInput(fakeInputEvent('ada@acme.test'));
    expect(component.canSubmit()).toBe(true);
  });

  it('posts the trimmed values', () => {
    const { component } = createFixture();
    fill(component, '  ada@acme.test  ', '  Ada Lovelace  ');

    component.submit();

    const request = httpMock.expectOne(`${API}/users`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ email: 'ada@acme.test', fullName: 'Ada Lovelace' });
    request.flush(created());
  });

  it('disables the submit while the request is in flight', () => {
    const { component } = createFixture();
    fill(component);

    component.submit();
    expect(component.submitting()).toBe(true);
    expect(component.canSubmit()).toBe(false);

    httpMock.expectOne(`${API}/users`).flush(created());
    expect(component.submitting()).toBe(false);
  });

  // Deliberately shallow, and the reason is in the component: the server's rule
  // is FluentValidation's `.EmailAddress()` plus "no whitespace", and a
  // stricter client regex would refuse addresses the API accepts.
  it('refuses an address with no @ or with a space, and nothing more clever', () => {
    const { component } = createFixture();

    component.onEmailInput(fakeInputEvent('ada'));
    expect(component.emailError()).not.toBeNull();

    component.onEmailInput(fakeInputEvent('ada lovelace@acme.test'));
    expect(component.emailError()).not.toBeNull();

    // Unusual but legal, and the API accepts it — so this form must too.
    component.onEmailInput(fakeInputEvent('a/b+tag@acme.test'));
    expect(component.emailError()).toBeNull();
  });

  // ---- The refusal ----

  it('shows a taken address against the email control', () => {
    const { component } = createFixture();
    fill(component);
    component.submit();

    httpMock.expectOne(`${API}/users`).flush(
      { status: 409, title: 'Conflict', reasonCode: 'EmailAlreadyInUse' },
      { status: 409, statusText: 'Conflict' },
    );

    expect(component.emailError()).toContain('already has an account');
    expect(component.created()).toBeNull();
  });

  // The copy must not say *where* the collision is: decisions `0010` and `0030`
  // make the server answer identically whether the address is in this tenant or
  // another, precisely so `POST /users` cannot be used to find out.
  it('never says which organization holds a taken address', () => {
    const { component } = createFixture();
    fill(component);
    component.submit();

    httpMock.expectOne(`${API}/users`).flush(
      { status: 409, title: 'Conflict', reasonCode: 'EmailAlreadyInUse' },
      { status: 409, statusText: 'Conflict' },
    );

    const message = component.emailError() ?? '';
    expect(message.toLowerCase()).not.toContain('another organization');
    expect(message.toLowerCase()).not.toContain('another tenant');
    expect(message.toLowerCase()).not.toContain('your organization');
  });

  // Otherwise "that address is already taken" sits under an address the
  // administrator has just corrected.
  it('drops the server message once the control is edited', () => {
    const { component } = createFixture();
    fill(component);
    component.submit();

    httpMock.expectOne(`${API}/users`).flush(
      { status: 409, title: 'Conflict', reasonCode: 'EmailAlreadyInUse' },
      { status: 409, statusText: 'Conflict' },
    );
    expect(component.emailError()).not.toBeNull();

    component.onEmailInput(fakeInputEvent('grace@acme.test'));
    expect(component.emailError()).toBeNull();
  });

  // No retry is offered on an unknown outcome — `POST /users` has no
  // idempotency key, so a repeat either creates a second account or comes back
  // 409 about the one it just made.
  it('sends the administrator to look rather than to resubmit when the outcome is unknown', () => {
    const { component } = createFixture();
    fill(component);
    component.submit();

    httpMock.expectOne(`${API}/users`).flush({}, { status: 500, statusText: 'Server Error' });

    expect(component.outcomeUnknown()).toBe(true);
    expect(component.formMessage()).toContain('Check the user list');
    expect(component.created()).toBeNull();
  });

  // ---- The outcome ----

  it('replaces the form with the outcome once the account exists', async () => {
    const { fixture, component } = createFixture();
    fill(component);
    component.submit();
    httpMock.expectOne(`${API}/users`).flush(created());
    await fixture.whenStable();
    fixture.detectChanges();

    // The form is gone, not merely disabled: a live submit button beside a
    // credential is how a second account gets created.
    expect(fixture.nativeElement.querySelector('form')).toBeNull();
    expect(fixture.nativeElement.querySelector('.outcome')).not.toBeNull();
  });

  // Hardening pass, 2026-09-25 (finding 2): the response carries no credential
  // any more, so nothing on this screen should render one — a regression guard
  // against the raw link creeping back in.
  it('never renders an activation link on the outcome screen', async () => {
    const { fixture, component } = createFixture();
    fill(component);
    component.submit();
    httpMock.expectOne(`${API}/users`).flush(created());
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).not.toContain('bookspace.test/activate');
    expect(fixture.nativeElement.querySelector('input[readonly]')).toBeNull();
  });

  // The outcome points at the new person's own page — where resending lives —
  // rather than handing over a credential itself.
  it('links to the new person’s own detail screen', async () => {
    const { fixture, component } = createFixture();
    fill(component);
    component.submit();
    httpMock.expectOne(`${API}/users`).flush(created({ id: 'u9' }));
    await fixture.whenStable();
    fixture.detectChanges();

    const view = [...fixture.nativeElement.querySelectorAll('a')].find((a: HTMLAnchorElement) =>
      a.textContent?.includes('Ada Lovelace'),
    ) as HTMLAnchorElement;
    expect(view.getAttribute('href')).toBe('/admin/users/u9');
  });

  // The failure case reads differently from the success case (finding 3: the
  // fix is to resend from the person's own page, not a link shown here).
  it('points at resending from the person’s page when the invitation email did not send', async () => {
    const { fixture, component } = createFixture();
    fill(component);
    component.submit();
    httpMock.expectOne(`${API}/users`).flush(created({ invitationEmailSent: false }));
    await fixture.whenStable();
    fixture.detectChanges();

    const title = fixture.nativeElement.querySelector('.outcome-title').textContent as string;
    expect(title).toContain("didn't send");
    expect(fixture.nativeElement.querySelector('.outcome').classList).toContain('outcome--warning');
    expect(fixture.nativeElement.textContent).toContain('resend');
  });

  it('reads as a plain success when the invitation did send', async () => {
    const { fixture, component } = createFixture();
    fill(component);
    component.submit();
    httpMock.expectOne(`${API}/users`).flush(created({ invitationEmailSent: true }));
    await fixture.whenStable();
    fixture.detectChanges();

    const title = fixture.nativeElement.querySelector('.outcome-title').textContent as string;
    expect(title).toContain('Invitation sent');
    expect(fixture.nativeElement.querySelector('.outcome').classList).not.toContain('outcome--warning');
  });

  // Starting another invitation drops the previous outcome and goes back to
  // an empty form.
  it('clears the outcome when the administrator invites somebody else', async () => {
    const { fixture, component } = createFixture();
    fill(component);
    component.submit();
    httpMock.expectOne(`${API}/users`).flush(created());
    await fixture.whenStable();
    fixture.detectChanges();

    component.inviteAnother();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(component.created()).toBeNull();
    expect(fixture.nativeElement.querySelector('.outcome')).toBeNull();
    expect(fixture.nativeElement.querySelector('form')).not.toBeNull();
    // ...and the fields are empty, ready for the next person.
    expect(component.email()).toBe('');
    expect(component.fullName()).toBe('');
  });

  it('links back to the directory from both screens', async () => {
    const { fixture, component } = createFixture();

    const cancel = [...fixture.nativeElement.querySelectorAll('a')].find(
      (a: HTMLAnchorElement) => a.textContent?.trim() === 'Cancel',
    ) as HTMLAnchorElement;
    expect(cancel.getAttribute('href')).toBe('/admin/users');

    fill(component);
    component.submit();
    httpMock.expectOne(`${API}/users`).flush(created());
    await fixture.whenStable();
    fixture.detectChanges();

    const back = [...fixture.nativeElement.querySelectorAll('a')].find(
      (a: HTMLAnchorElement) => a.textContent?.trim() === 'Back to users',
    ) as HTMLAnchorElement;
    expect(back.getAttribute('href')).toBe('/admin/users');
  });
});
