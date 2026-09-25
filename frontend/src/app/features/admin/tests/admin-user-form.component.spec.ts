import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { AdminUserFormComponent } from '../components/admin-user-form/admin-user-form.component';
import { CreatedUser } from '../models/users.models';

// User management phase 6, the invite form.
//
// Most of this file is about the outcome rather than the form, because that is
// where the unusual behaviour is: `POST /users` hands back a live activation
// link, and this screen is the only place it will ever appear. So the
// assertions are that it is shown, that the form is gone while it is, that the
// failure case still shows it, and that it is not recoverable afterwards.
//
// The DOM is asserted directly for those, not the signals: "is the link on
// screen" is the actual question, and a signal-level check would pass with the
// template's branches swapped.

const API = 'http://localhost:5270';

type TestableForm = AdminUserFormComponent & {
  email: (value?: string) => string;
  fullName: () => string;
  submitting: () => boolean;
  created: () => CreatedUser | null;
  linkCopied: () => boolean;
  emailError: () => string | null;
  fullNameError: () => string | null;
  formMessage: () => string | null;
  outcomeUnknown: () => boolean;
  canSubmit: () => boolean;
  onEmailInput(event: Event): void;
  onFullNameInput(event: Event): void;
  submit(): void;
  inviteAnother(): void;
  copyLink(): Promise<void>;
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
    activationLink: 'https://bookspace.test/activate?token=abc123',
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

  it('shows the activation link', async () => {
    const { fixture, component } = createFixture();
    fill(component);
    component.submit();
    httpMock.expectOne(`${API}/users`).flush(created());
    await fixture.whenStable();
    fixture.detectChanges();

    const input: HTMLInputElement = fixture.nativeElement.querySelector('.link-input');
    expect(input.value).toBe('https://bookspace.test/activate?token=abc123');
    expect(input.readOnly).toBe(true);
  });

  it('says the link is shown once', async () => {
    const { fixture, component } = createFixture();
    fill(component);
    component.submit();
    httpMock.expectOne(`${API}/users`).flush(created());
    await fixture.whenStable();
    fixture.detectChanges();

    const note = fixture.nativeElement.querySelector('.link-note').textContent as string;
    expect(note).toContain('shown once');
    expect(note).toContain("won't be shown again");
  });

  // **The failure case reads differently from the success case, and both show
  // the link** — plan §4.3. The account is real either way; what changed is who
  // delivers the invitation.
  it('still shows the link when the invitation email did not send', async () => {
    const { fixture, component } = createFixture();
    fill(component);
    component.submit();
    httpMock.expectOne(`${API}/users`).flush(created({ invitationEmailSent: false }));
    await fixture.whenStable();
    fixture.detectChanges();

    const input: HTMLInputElement = fixture.nativeElement.querySelector('.link-input');
    expect(input.value).toBe('https://bookspace.test/activate?token=abc123');

    const title = fixture.nativeElement.querySelector('.outcome-title').textContent as string;
    expect(title).toContain("didn't send");
    expect(fixture.nativeElement.querySelector('.outcome').classList).toContain('outcome--warning');
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

  // Shown once means shown once: starting another invitation drops the link
  // rather than parking it somewhere it could be recovered from.
  it('forgets the link when the administrator invites somebody else', async () => {
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
    expect(fixture.nativeElement.querySelector('.link-input')).toBeNull();
    expect(fixture.nativeElement.querySelector('form')).not.toBeNull();
    // ...and the fields are empty, ready for the next person.
    expect(component.email()).toBe('');
    expect(component.fullName()).toBe('');
  });

  it('copies the link to the clipboard', async () => {
    const { component } = createFixture();
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });

    fill(component);
    component.submit();
    httpMock.expectOne(`${API}/users`).flush(created());

    await component.copyLink();

    expect(writeText).toHaveBeenCalledWith('https://bookspace.test/activate?token=abc123');
    expect(component.linkCopied()).toBe(true);
    vi.unstubAllGlobals();
  });

  // Clipboard access can be refused outright — an insecure origin, a denied
  // permission. The link is still on screen and selectable, so saying the copy
  // failed would imply the invitation had.
  it('stays quiet when the clipboard refuses', async () => {
    const { component } = createFixture();
    vi.stubGlobal('navigator', { clipboard: { writeText: vi.fn().mockRejectedValue(new Error('denied')) } });

    fill(component);
    component.submit();
    httpMock.expectOne(`${API}/users`).flush(created());

    await expect(component.copyLink()).resolves.toBeUndefined();
    expect(component.linkCopied()).toBe(false);
    vi.unstubAllGlobals();
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
