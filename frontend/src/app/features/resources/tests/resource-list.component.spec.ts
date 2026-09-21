import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router } from '@angular/router';
import { ResourceListComponent } from '../components/resource-list/resource-list.component';
import { PagedResult } from '../../../core/http/paged-result';
import { ResourceSummary, ResourceType } from '../models/resources.models';

const API = 'http://localhost:5270';

// The signals/methods this spec drives are `protected` at compile time only
// — reaching them via a widened type is the same pattern
// LoginComponent's own spec already established for testing behaviour
// without going through real DOM events in a zoneless app.
type TestableResourceListComponent = ResourceListComponent & {
  items: () => ResourceSummary[];
  totalCount: () => number;
  loading: () => boolean;
  loadError: () => boolean;
  page: () => number;
  totalPages: () => number;
  hasPreviousPage: () => boolean;
  hasNextPage: () => boolean;
  goToPreviousPage(): void;
  goToNextPage(): void;
  selectedType: () => ResourceType | null;
  includeArchived: () => boolean;
  showMoreFilters: () => boolean;
  searchText: () => string;
  approvalFilter: () => string;
  selectType(type: ResourceType | null): void;
  onSearchInput(event: Event): void;
  onApprovalFilterChange(event: Event): void;
  onIncludeArchivedChange(event: Event): void;
  toggleMoreFilters(): void;
  retry(): void;
  goToDetail(id: string): void;
  capacityLabel(resource: ResourceSummary): string;
  typeLabel(type: ResourceType): string;
};

function fakeInputEvent(value: string): Event {
  return { target: { value } } as unknown as Event;
}

function fakeCheckboxEvent(checked: boolean): Event {
  return { target: { checked } } as unknown as Event;
}

function fakeResource(overrides: Partial<ResourceSummary> = {}): ResourceSummary {
  return {
    id: 'r1',
    name: 'Conference Room A',
    resourceType: 'Room',
    capacity: 1,
    timeZoneId: 'Europe/Sarajevo',
    requiresApproval: false,
    isArchived: false,
    ...overrides,
  };
}

function fakePage(
  items: ResourceSummary[],
  totalCount = items.length,
  page = 1,
  pageSize = 100,
): PagedResult<ResourceSummary> {
  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));
  return {
    items,
    page,
    pageSize,
    totalCount,
    totalPages,
    hasPreviousPage: page > 1,
    hasNextPage: page < totalPages,
  };
}

describe('ResourceListComponent', () => {
  let httpMock: HttpTestingController;
  let navigate: ReturnType<typeof vi.fn>;

  function createFixture() {
    navigate = vi.fn().mockResolvedValue(true);

    TestBed.configureTestingModule({
      imports: [ResourceListComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: { navigate } },
        // RouterLink (the "Book resource" link) injects ActivatedRoute
        // whether or not a test ever calls detectChanges() — Angular's
        // zoneless auto-render can still flush a scheduled render pass once
        // fake timers are advanced (see the debounce tests below), which is
        // enough to instantiate it and hit this if it's missing.
        { provide: ActivatedRoute, useValue: {} },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    return TestBed.createComponent(ResourceListComponent).componentInstance as TestableResourceListComponent;
  }

  // Every other test in this file drives the component instance directly
  // (signals/methods only) — this is the one exception, for item 9/10's
  // fixes, which live entirely in the template rather than in any exposed
  // signal or method, so there's nothing to assert on without rendering it.
  function createRenderedFixture() {
    navigate = vi.fn().mockResolvedValue(true);

    TestBed.configureTestingModule({
      imports: [ResourceListComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: { navigate } },
        { provide: ActivatedRoute, useValue: {} },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    return TestBed.createComponent(ResourceListComponent);
  }

  // Flushes the construction-time fetch (every test triggers one, since
  // ResourceListComponent loads as soon as it's created) with a plain,
  // non-truncated page, so tests about later behaviour don't also have to
  // account for the very first request.
  function createLoadedComponent(): TestableResourceListComponent {
    const component = createFixture();
    httpMock.expectOne((req) => req.url === `${API}/resources`).flush(fakePage([fakeResource()]));
    return component;
  }

  afterEach(() => {
    httpMock.verify();
  });

  it('fetches at construction with the shared page-size ceiling, page 1, and no type filter', () => {
    const component = createFixture();

    const req = httpMock.expectOne((r) => r.url === `${API}/resources`);
    expect(req.request.params.get('pageSize')).toBe('100');
    expect(req.request.params.get('page')).toBe('1');
    expect(req.request.params.has('type')).toBe(false);

    req.flush(fakePage([fakeResource(), fakeResource({ id: 'r2', name: 'Pool Cars', capacity: 5 })], 2));

    expect(component.items().length).toBe(2);
    expect(component.totalCount()).toBe(2);
    expect(component.loading()).toBe(false);
    expect(component.totalPages()).toBe(1);
    expect(component.hasNextPage()).toBe(false);
  });

  // Item 8: real pagination, not a "narrow by type" truncation notice — a
  // tenant with more resources than one page can carry can actually reach
  // the rest.
  describe('pagination', () => {
    it('exposes hasNextPage/totalPages when the server reports more rows than one page could carry', () => {
      const component = createFixture();

      httpMock
        .expectOne((r) => r.url === `${API}/resources`)
        .flush(fakePage(Array.from({ length: 100 }, (_, i) => fakeResource({ id: `r${i}` })), 150));

      expect(component.items().length).toBe(100);
      expect(component.totalCount()).toBe(150);
      expect(component.totalPages()).toBe(2);
      expect(component.hasNextPage()).toBe(true);
      expect(component.hasPreviousPage()).toBe(false);
    });

    it('goToNextPage requests page 2 and updates hasPreviousPage/hasNextPage from the response', () => {
      const component = createFixture();
      httpMock
        .expectOne((r) => r.url === `${API}/resources`)
        .flush(fakePage(Array.from({ length: 100 }, (_, i) => fakeResource({ id: `r${i}` })), 150));

      component.goToNextPage();

      const req = httpMock.expectOne((r) => r.url === `${API}/resources`);
      expect(req.request.params.get('page')).toBe('2');
      req.flush(fakePage(Array.from({ length: 50 }, (_, i) => fakeResource({ id: `s${i}` })), 150, 2));

      expect(component.page()).toBe(2);
      expect(component.hasPreviousPage()).toBe(true);
      expect(component.hasNextPage()).toBe(false);
      expect(component.items()).toHaveLength(50);
    });

    it('goToNextPage does nothing when there is no next page', () => {
      const component = createLoadedComponent(); // a single page, hasNextPage false by default

      component.goToNextPage();

      httpMock.expectNone((r) => r.url === `${API}/resources`);
    });

    it('goToPreviousPage does nothing on page 1', () => {
      const component = createLoadedComponent();

      component.goToPreviousPage();

      httpMock.expectNone((r) => r.url === `${API}/resources`);
    });

    it('changing the type filter resets to page 1', () => {
      const component = createFixture();
      httpMock
        .expectOne((r) => r.url === `${API}/resources`)
        .flush(fakePage(Array.from({ length: 100 }, (_, i) => fakeResource({ id: `r${i}` })), 150));

      component.goToNextPage();
      httpMock
        .expectOne((r) => r.params.get('page') === '2')
        .flush(fakePage(Array.from({ length: 50 }, (_, i) => fakeResource({ id: `s${i}` })), 150, 2));
      expect(component.page()).toBe(2);

      component.selectType('Room');

      const req = httpMock.expectOne((r) => r.url === `${API}/resources`);
      expect(req.request.params.get('page')).toBe('1');
      req.flush(fakePage([fakeResource({ resourceType: 'Room' })]));
      expect(component.page()).toBe(1);
    });

    it('toggling "include archived" resets to page 1', () => {
      const component = createFixture();
      httpMock
        .expectOne((r) => r.url === `${API}/resources`)
        .flush(fakePage(Array.from({ length: 100 }, (_, i) => fakeResource({ id: `r${i}` })), 150));

      component.goToNextPage();
      httpMock
        .expectOne((r) => r.params.get('page') === '2')
        .flush(fakePage(Array.from({ length: 50 }, (_, i) => fakeResource({ id: `s${i}` })), 150, 2));

      component.onIncludeArchivedChange(fakeCheckboxEvent(true));

      const req = httpMock.expectOne((r) => r.url === `${API}/resources`);
      expect(req.request.params.get('page')).toBe('1');
      req.flush(fakePage([fakeResource()]));
      expect(component.page()).toBe(1);
    });
  });

  it('re-requests with the matching type when a pill is selected', () => {
    const component = createLoadedComponent();

    component.selectType('Vehicle');

    const req = httpMock.expectOne((r) => r.url === `${API}/resources`);
    expect(req.request.params.get('type')).toBe('Vehicle');
    req.flush(fakePage([fakeResource({ id: 'r2', name: 'Pool Cars', resourceType: 'Vehicle', capacity: 5 })]));

    expect(component.selectedType()).toBe('Vehicle');
    expect(component.items()[0].resourceType).toBe('Vehicle');
  });

  it('does not re-request when the already-selected pill is clicked again', () => {
    const component = createLoadedComponent();

    component.selectType(null);

    httpMock.expectNone((r) => r.url === `${API}/resources`);
  });

  it('ignores a stale response that resolves after a newer request already landed', () => {
    const component = createLoadedComponent();

    component.selectType('Room');
    const roomReq = httpMock.expectOne((r) => r.params.get('type') === 'Room');

    component.selectType('Vehicle');
    const vehicleReq = httpMock.expectOne((r) => r.params.get('type') === 'Vehicle');

    // Out-of-order resolution: the newer (Vehicle) request lands first.
    vehicleReq.flush(fakePage([fakeResource({ id: 'r2', name: 'Pool Cars', resourceType: 'Vehicle' })]));
    roomReq.flush(fakePage([fakeResource({ id: 'r3', name: 'Stale Room', resourceType: 'Room' })]));

    expect(component.items()).toHaveLength(1);
    expect(component.items()[0].name).toBe('Pool Cars');
  });

  it('surfaces a load failure and recovers via retry()', () => {
    const component = createFixture();

    httpMock.expectOne((r) => r.url === `${API}/resources`).flush(null, { status: 500, statusText: 'Server Error' });

    expect(component.loading()).toBe(false);
    expect(component.loadError()).toBe(true);
    expect(component.items()).toHaveLength(0);

    component.retry();
    httpMock.expectOne((r) => r.url === `${API}/resources`).flush(fakePage([fakeResource()]));

    expect(component.loadError()).toBe(false);
    expect(component.items()).toHaveLength(1);
  });

  it('navigates to the resource detail route when a card is activated', () => {
    const component = createLoadedComponent();

    component.goToDetail('r1');

    expect(navigate).toHaveBeenCalledWith(['/resources', 'r1']);
  });

  it('labels an exclusive resource "Single resource" and a pooled one by its unit count', () => {
    const component = createLoadedComponent();

    expect(component.capacityLabel(fakeResource({ capacity: 1 }))).toBe('Single resource');
    expect(component.capacityLabel(fakeResource({ capacity: 5 }))).toBe('5 units');
  });

  it('renders the human-readable type label, including the two-word LabSlot case', () => {
    const component = createLoadedComponent();

    expect(component.typeLabel('LabSlot')).toBe('Lab slot');
    expect(component.typeLabel('Room')).toBe('Room');
  });

  // Step 3 redo (2026-09-15): search and approval moved server-side once
  // GET /resources actually supported them — see CLAUDE.md's "Resource list
  // filters extended for WP-7" entry. Neither does any client-side narrowing
  // any more; `items()` is exactly what the last fetch returned.
  describe('search (debounced) and approval filter (server round-trips)', () => {
    it('does not fire a request on every keystroke — it waits for the debounce window', () => {
      vi.useFakeTimers();
      try {
        const component = createLoadedComponent();

        component.onSearchInput(fakeInputEvent('r'));
        component.onSearchInput(fakeInputEvent('ro'));
        component.onSearchInput(fakeInputEvent('room'));

        httpMock.expectNone((r) => r.url === `${API}/resources`);
        // The displayed value updates immediately, independent of the debounce.
        expect(component.searchText()).toBe('room');
      } finally {
        vi.useRealTimers();
      }
    });

    it('fires exactly one request, with the final text, once the debounce window elapses', async () => {
      vi.useFakeTimers();
      try {
        const component = createLoadedComponent();

        component.onSearchInput(fakeInputEvent('r'));
        component.onSearchInput(fakeInputEvent('ro'));
        component.onSearchInput(fakeInputEvent('room'));

        await vi.advanceTimersByTimeAsync(500);

        // expectOne throws if more than one request went out — the case a
        // missing debounce would produce (one per keystroke).
        const req = httpMock.expectOne((r) => r.url === `${API}/resources`);
        expect(req.request.params.get('search')).toBe('room');
        req.flush(fakePage([fakeResource({ name: 'Conference Room A' })]));

        expect(component.items().map((r) => r.name)).toEqual(['Conference Room A']);
      } finally {
        vi.useRealTimers();
      }
    });

    // The race a slow network (or a slow server) can trigger even with a
    // debounce in place: two searches each survive their own debounce
    // window (the user paused after each), so two requests are genuinely
    // in flight together, and the *first* one to be typed is the *last* to
    // resolve.
    it('ignores a stale search response that resolves after a newer search has already landed', async () => {
      vi.useFakeTimers();
      try {
        const component = createLoadedComponent();

        component.onSearchInput(fakeInputEvent('abc'));
        await vi.advanceTimersByTimeAsync(500);
        const staleReq = httpMock.expectOne((r) => r.params.get('search') === 'abc');

        component.onSearchInput(fakeInputEvent('abcd'));
        await vi.advanceTimersByTimeAsync(500);
        const freshReq = httpMock.expectOne((r) => r.params.get('search') === 'abcd');

        // Out-of-order resolution: the newer request ("abcd") lands first.
        freshReq.flush(fakePage([fakeResource({ id: 'r2', name: 'Fresh Match' })]));
        staleReq.flush(fakePage([fakeResource({ id: 'r1', name: 'Stale Match' })]));

        expect(component.items().map((r) => r.name)).toEqual(['Fresh Match']);
      } finally {
        vi.useRealTimers();
      }
    });

    it('omits the search param entirely once the box is cleared', async () => {
      vi.useFakeTimers();
      try {
        const component = createLoadedComponent();

        component.onSearchInput(fakeInputEvent('room'));
        await vi.advanceTimersByTimeAsync(500);
        httpMock.expectOne((r) => r.params.get('search') === 'room').flush(fakePage([fakeResource()]));

        component.onSearchInput(fakeInputEvent(''));
        await vi.advanceTimersByTimeAsync(500);

        const req = httpMock.expectOne((r) => r.url === `${API}/resources`);
        expect(req.request.params.has('search')).toBe(false);
        req.flush(fakePage([fakeResource()]));
      } finally {
        vi.useRealTimers();
      }
    });

    it('maps the approval dropdown onto requiresApproval and re-fetches immediately, without debouncing', () => {
      const component = createLoadedComponent();

      component.onApprovalFilterChange(fakeInputEvent('required'));
      let req = httpMock.expectOne((r) => r.url === `${API}/resources`);
      expect(req.request.params.get('requiresApproval')).toBe('true');
      req.flush(fakePage([fakeResource({ name: 'Conference Room A', requiresApproval: true })]));

      component.onApprovalFilterChange(fakeInputEvent('notRequired'));
      req = httpMock.expectOne((r) => r.url === `${API}/resources`);
      expect(req.request.params.get('requiresApproval')).toBe('false');
      req.flush(fakePage([fakeResource({ name: 'Pool Cars', requiresApproval: false })]));

      component.onApprovalFilterChange(fakeInputEvent('all'));
      req = httpMock.expectOne((r) => r.url === `${API}/resources`);
      expect(req.request.params.has('requiresApproval')).toBe(false);
      req.flush(fakePage([fakeResource()]));
    });

    it('combines search and approval into one request once both are set', async () => {
      vi.useFakeTimers();
      try {
        const component = createLoadedComponent();

        component.onApprovalFilterChange(fakeInputEvent('notRequired'));
        httpMock.expectOne((r) => r.params.get('requiresApproval') === 'false').flush(fakePage([fakeResource()]));

        component.onSearchInput(fakeInputEvent('pool'));
        await vi.advanceTimersByTimeAsync(500);

        const req = httpMock.expectOne((r) => r.url === `${API}/resources`);
        expect(req.request.params.get('search')).toBe('pool');
        expect(req.request.params.get('requiresApproval')).toBe('false');
        req.flush(fakePage([fakeResource({ name: 'Pool Cars', requiresApproval: false })]));
      } finally {
        vi.useRealTimers();
      }
    });
  });

  describe('"More filters" — includeArchived', () => {
    it('toggles the panel open and closed without touching the network', () => {
      const component = createLoadedComponent();

      expect(component.showMoreFilters()).toBe(false);
      component.toggleMoreFilters();
      expect(component.showMoreFilters()).toBe(true);
      component.toggleMoreFilters();
      expect(component.showMoreFilters()).toBe(false);

      httpMock.expectNone((r) => r.url === `${API}/resources`);
    });

    it('re-fetches with includeArchived=true when checked, and omits the param again once unchecked', () => {
      const component = createLoadedComponent();

      component.onIncludeArchivedChange(fakeCheckboxEvent(true));
      const withArchived = httpMock.expectOne((r) => r.url === `${API}/resources`);
      expect(withArchived.request.params.get('includeArchived')).toBe('true');
      withArchived.flush(fakePage([fakeResource({ id: 'r4', name: 'Retired Room', isArchived: true })]));
      expect(component.includeArchived()).toBe(true);

      component.onIncludeArchivedChange(fakeCheckboxEvent(false));
      const withoutArchived = httpMock.expectOne((r) => r.url === `${API}/resources`);
      expect(withoutArchived.request.params.has('includeArchived')).toBe(false);
      withoutArchived.flush(fakePage([fakeResource()]));
    });
  });

  // Items 9 and 10 — both live entirely in the template.
  describe('rendered card markup', () => {
    it('shows "Book resource" for an active resource and no archived notice', () => {
      const fixture = createRenderedFixture();
      httpMock.expectOne((r) => r.url === `${API}/resources`).flush(fakePage([fakeResource({ isArchived: false })]));
      fixture.detectChanges();

      const el = fixture.nativeElement as HTMLElement;
      expect(el.querySelector('.book-link')).not.toBeNull();
      expect(el.querySelector('.archived-notice')).toBeNull();
    });

    // Item 9: an archived resource's card must not offer a CTA that leads
    // into a known dead end (the availability screen correctly refuses
    // booking, but routing there anyway is still a bad flow).
    it('shows a plain archived notice instead of "Book resource" for an archived resource', () => {
      const fixture = createRenderedFixture();
      httpMock.expectOne((r) => r.url === `${API}/resources`).flush(fakePage([fakeResource({ isArchived: true })]));
      fixture.detectChanges();

      const el = fixture.nativeElement as HTMLElement;
      expect(el.querySelector('.book-link')).toBeNull();
      expect(el.querySelector('.archived-notice')?.textContent).toContain('Archived');
    });

    // Item 10: the card's only real, keyboard-reachable "view details"
    // interaction is now a genuine anchor, not a role="link" div wrapping
    // another real anchor (nested interactive elements).
    it('exposes "view details" as a real anchor, not a div with role="link"', () => {
      const fixture = createRenderedFixture();
      httpMock.expectOne((r) => r.url === `${API}/resources`).flush(fakePage([fakeResource()]));
      fixture.detectChanges();

      const el = fixture.nativeElement as HTMLElement;
      expect(el.querySelector('.resource-card')?.getAttribute('role')).toBeNull();
      expect(el.querySelector('.resource-card')?.getAttribute('tabindex')).toBeNull();
      const titleLink = el.querySelector('.card-title-link');
      expect(titleLink?.tagName).toBe('A');
      expect(titleLink?.getAttribute('aria-label')).toBe('View Conference Room A');
    });

    // Item 10: filters that behave as filters, not fake tabs.
    it('uses a plain button group with aria-pressed for the type filters, not tab/tablist roles', () => {
      const fixture = createRenderedFixture();
      httpMock.expectOne((r) => r.url === `${API}/resources`).flush(fakePage([fakeResource()]));
      fixture.detectChanges();

      const el = fixture.nativeElement as HTMLElement;
      expect(el.querySelector('.type-filters')?.getAttribute('role')).toBe('group');
      const pills = Array.from(el.querySelectorAll('.type-pill'));
      expect(pills.length).toBeGreaterThan(0);
      for (const pill of pills) {
        expect(pill.getAttribute('role')).toBeNull();
        expect(pill.hasAttribute('aria-pressed')).toBe(true);
      }
      const allPill = pills.find((p) => p.textContent?.trim() === 'All');
      expect(allPill?.getAttribute('aria-pressed')).toBe('true');
    });
  });
});
