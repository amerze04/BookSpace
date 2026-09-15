import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, ActivatedRouteSnapshot, NavigationEnd, Router } from '@angular/router';
import { Subject } from 'rxjs';
import { ShellComponent } from './shell.component';
import { BreadcrumbService } from '../breadcrumb.service';

// The signals this spec drives are `protected` at compile time only — same
// widened-type pattern every other component spec in this app uses.
type TestableShellComponent = ShellComponent & {
  breadcrumb: () => string[];
  title: () => string;
};

// Builds a linked list of bare ActivatedRouteSnapshot-shaped objects — only
// `data` and `firstChild` matter to ShellComponent's own walk, so nothing
// else needs to be real.
function fakeSnapshotChain(levels: Array<{ title?: string }>): ActivatedRouteSnapshot {
  let node: Partial<ActivatedRouteSnapshot> | null = null;
  for (let i = levels.length - 1; i >= 0; i--) {
    node = { data: levels[i].title ? { title: levels[i].title } : {}, firstChild: node as ActivatedRouteSnapshot | null };
  }
  return (node ?? { data: {}, firstChild: null }) as ActivatedRouteSnapshot;
}

describe('ShellComponent breadcrumb', () => {
  let events$: Subject<NavigationEnd>;
  let routerStub: {
    events: Subject<NavigationEnd>;
    routerState: { snapshot: { root: ActivatedRouteSnapshot } };
    navigateByUrl: () => Promise<boolean>;
  };
  let breadcrumbService: BreadcrumbService;

  function createComponent(levels: Array<{ title?: string }>): TestableShellComponent {
    events$ = new Subject();
    routerStub = {
      events: events$,
      routerState: { snapshot: { root: fakeSnapshotChain(levels) } },
      navigateByUrl: () => Promise.resolve(true),
    };

    TestBed.configureTestingModule({
      imports: [ShellComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: routerStub },
        { provide: ActivatedRoute, useValue: {} },
      ],
    });

    breadcrumbService = TestBed.inject(BreadcrumbService);
    return TestBed.createComponent(ShellComponent).componentInstance as TestableShellComponent;
  }

  afterEach(() => {
    breadcrumbService.setOverride(null);
  });

  it('shows a single crumb for a flat, top-level route', () => {
    const component = createComponent([{ title: 'Resources' }]);

    expect(component.breadcrumb()).toEqual(['Resources']);
    expect(component.title()).toBe('Resources');
  });

  it('walks every matched level that carries a title, skipping a parent that has none', () => {
    // The real shape once WP-7 nested `resources` -> `:id`: the parent
    // `resources` route carries no title of its own, only its children do.
    const component = createComponent([{}, { title: 'Resources' }, { title: 'Resource details' }]);

    expect(component.breadcrumb()).toEqual(['Resources', 'Resource details']);
    expect(component.title()).toBe('Resource details');
  });

  it('re-derives the chain on every NavigationEnd rather than only at construction', () => {
    const component = createComponent([{ title: 'Resources' }]);
    expect(component.breadcrumb()).toEqual(['Resources']);

    routerStub.routerState.snapshot.root = fakeSnapshotChain([{ title: 'My Bookings' }]);
    events$.next(new NavigationEnd(1, '/my-bookings', '/my-bookings'));

    expect(component.breadcrumb()).toEqual(['My Bookings']);
  });

  it('replaces only the last crumb with the BreadcrumbService override', () => {
    const component = createComponent([{}, { title: 'Resources' }, { title: 'Resource details' }]);

    breadcrumbService.setOverride('Conference Room A');

    expect(component.breadcrumb()).toEqual(['Resources', 'Conference Room A']);
    expect(component.title()).toBe('Conference Room A');
  });

  it('falls back to the route title again once the override is cleared', () => {
    const component = createComponent([{ title: 'Resources' }, { title: 'Resource details' }]);
    breadcrumbService.setOverride('Conference Room A');
    expect(component.breadcrumb()).toEqual(['Resources', 'Conference Room A']);

    breadcrumbService.setOverride(null);

    expect(component.breadcrumb()).toEqual(['Resources', 'Resource details']);
  });

  it('ignores a leftover override on a route with no crumbs at all', () => {
    // Defensive case: an override with nothing to replace must not conjure
    // a crumb out of nowhere.
    const component = createComponent([]);
    breadcrumbService.setOverride('Conference Room A');

    expect(component.breadcrumb()).toEqual([]);
  });
});
