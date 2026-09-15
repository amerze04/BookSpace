import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { LoginComponent } from './login.component';
import { buildFakeAccessToken } from '../../../core/auth/testing/jwt-fixture';

const API = 'http://localhost:5270';

// submit()/errorMessage/form are `protected` at compile time only — reaching
// them via a widened type is the standard way to unit-test a component's
// behaviour without going through real DOM events in a zoneless app.
type TestableLoginComponent = LoginComponent & {
  submit(): Promise<void>;
  errorMessage: () => string | null;
  form: { setValue(value: { email: string; password: string }): void };
};

describe('LoginComponent (item 9: login error mapping)', () => {
  let httpMock: HttpTestingController;
  let navigateByUrl: ReturnType<typeof vi.fn>;

  function createFixture(returnUrl?: string) {
    navigateByUrl = vi.fn().mockResolvedValue(true);

    TestBed.configureTestingModule({
      imports: [LoginComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: { navigateByUrl } },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap(returnUrl ? { returnUrl } : {}) } },
        },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    return TestBed.createComponent(LoginComponent);
  }

  function createComponent(returnUrl?: string) {
    const component = createFixture(returnUrl).componentInstance as TestableLoginComponent;
    component.form.setValue({ email: 'member1@acme.test', password: 'Passw0rd!' });
    return component;
  }

  afterEach(() => {
    localStorage.clear();
    httpMock.verify();
  });

  it('shows the generic credentials message for an actual 401, and nothing else', async () => {
    const component = createComponent();

    const submitPromise = component.submit();
    httpMock.expectOne(`${API}/auth/login`).flush(null, { status: 401, statusText: 'Unauthorized' });
    await submitPromise;

    expect(component.errorMessage()).toBe('Incorrect email or password.');
    expect(navigateByUrl).not.toHaveBeenCalled();
  });

  it('does not call a network error "incorrect email or password"', async () => {
    const component = createComponent();

    const submitPromise = component.submit();
    httpMock.expectOne(`${API}/auth/login`).error(new ProgressEvent('error'));
    await submitPromise;

    expect(component.errorMessage()).not.toBe('Incorrect email or password.');
    expect(component.errorMessage()).toContain('Unable to reach the server');
  });

  it('does not call a 429 "incorrect email or password"', async () => {
    const component = createComponent();

    const submitPromise = component.submit();
    httpMock.expectOne(`${API}/auth/login`).flush(null, { status: 429, statusText: 'Too Many Requests' });
    await submitPromise;

    expect(component.errorMessage()).not.toBe('Incorrect email or password.');
    expect(component.errorMessage()).toContain('Too many attempts');
  });

  it('does not call a 500 "incorrect email or password"', async () => {
    const component = createComponent();

    const submitPromise = component.submit();
    httpMock.expectOne(`${API}/auth/login`).flush(null, { status: 500, statusText: 'Server Error' });
    await submitPromise;

    expect(component.errorMessage()).not.toBe('Incorrect email or password.');
    expect(component.errorMessage()).toContain('went wrong on our end');
  });

  it('treats a post-login navigation failure separately from an authentication failure', async () => {
    const component = createComponent('/resources');
    navigateByUrl.mockRejectedValueOnce(new Error('navigation failed')).mockResolvedValueOnce(true);

    const accessToken = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1' });
    const submitPromise = component.submit();
    httpMock
      .expectOne(`${API}/auth/login`)
      .flush({ accessToken, expiresIn: 900, refreshToken: 'refresh-1' });
    await submitPromise;

    // Login itself succeeded — no auth error, ever, from a navigation problem.
    expect(component.errorMessage()).toBeNull();
    expect(navigateByUrl).toHaveBeenNthCalledWith(1, '/resources');
    expect(navigateByUrl).toHaveBeenNthCalledWith(2, '/');
  });
});

// Item 15: a field error has to be reachable by a screen reader from the
// input itself, not just visible as a nearby paragraph.
describe('LoginComponent (item 15: field error accessibility)', () => {
  let httpMock: HttpTestingController;

  function createFixture() {
    TestBed.configureTestingModule({
      imports: [LoginComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: { navigateByUrl: vi.fn().mockResolvedValue(true) } },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({}) } } },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    return TestBed.createComponent(LoginComponent);
  }

  afterEach(() => httpMock.verify());

  it('marks the email input aria-invalid and points aria-describedby at the error message once touched empty', async () => {
    const fixture = createFixture();
    const component = fixture.componentInstance as TestableLoginComponent;

    // Submitting a blank form is what production wires to markTouched for
    // every field (see submit()'s form.invalid branch).
    await component.submit();
    fixture.detectChanges();

    const email = fixture.nativeElement.querySelector('#email') as HTMLInputElement;
    expect(email.getAttribute('aria-invalid')).toBe('true');
    expect(email.getAttribute('aria-describedby')).toBe('email-error');

    const errorEl = fixture.nativeElement.querySelector('#email-error');
    expect(errorEl?.textContent).toContain('required');
  });

  it('does not mark a field aria-invalid before it has been touched', () => {
    const fixture = createFixture();
    fixture.detectChanges();

    const email = fixture.nativeElement.querySelector('#email') as HTMLInputElement;
    expect(email.getAttribute('aria-invalid')).toBeNull();
    expect(email.getAttribute('aria-describedby')).toBeNull();
  });
});
