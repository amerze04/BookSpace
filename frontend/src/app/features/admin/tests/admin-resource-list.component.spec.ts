import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { AdminResourceListComponent } from '../components/admin-resource-list/admin-resource-list.component';
import { PagedResult } from '../../../core/http/paged-result';
import { ResourceSummary } from '../../resources/models/resources.models';

const API = 'http://localhost:5270';

type TestableList = AdminResourceListComponent & {
  items: () => ResourceSummary[];
  loading: () => boolean;
  loadError: () => boolean;
  page: () => number;
  totalPages: () => number;
  hasNextPage: () => boolean;
  includeArchived: () => boolean;
  searchText: () => string;
  onSearchInput(event: Event): void;
  onIncludeArchivedChange(event: Event): void;
  goToNextPage(): void;
  goToPreviousPage(): void;
  retry(): void;
};

function fakeInputEvent(value: string): Event {
  return { target: { value } } as unknown as Event;
}

function fakeCheckboxEvent(checked: boolean): Event {
  return { target: { checked } } as unknown as Event;
}

function resource(overrides: Partial<ResourceSummary> = {}): ResourceSummary {
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

function page(items: ResourceSummary[], overrides: Partial<PagedResult<ResourceSummary>> = {}): PagedResult<ResourceSummary> {
  return {
    items,
    page: 1,
    pageSize: 50,
    totalCount: items.length,
    totalPages: 1,
    hasPreviousPage: false,
    hasNextPage: false,
    ...overrides,
  };
}

describe('AdminResourceListComponent', () => {
  let httpMock: HttpTestingController;

  function create(): TestableList {
    TestBed.configureTestingModule({
      imports: [AdminResourceListComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: { navigate: vi.fn().mockResolvedValue(true) } },
        // RouterLink injects ActivatedRoute as soon as a render pass runs,
        // whether or not a test asks for one.
        { provide: ActivatedRoute, useValue: {} },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    return TestBed.createComponent(AdminResourceListComponent).componentInstance as TestableList;
  }

  afterEach(() => {
    try {
      httpMock.verify();
    } finally {
      TestBed.resetTestingModule();
    }
  });

  function flushInitial(items: ResourceSummary[] = [resource()]): void {
    httpMock.expectOne((r) => r.url === `${API}/resources`).flush(page(items));
  }

  it('loads the catalogue as soon as it is created', () => {
    const component = create();

    const request = httpMock.expectOne((r) => r.url === `${API}/resources`);
    expect(request.request.params.get('pageSize')).toBe('50');
    request.flush(page([resource()]));

    expect(component.items()).toHaveLength(1);
    expect(component.loading()).toBe(false);
  });

  // **`includeArchived` is omitted at its default rather than sent as `false`**,
  // following the service's own convention — the backend's documented default
  // applies instead of the client keeping a second copy of it that could drift.
  it('omits includeArchived until the toggle is on', () => {
    create();

    const initial = httpMock.expectOne((r) => r.url === `${API}/resources`);
    expect(initial.request.params.has('includeArchived')).toBe(false);
    initial.flush(page([]));
  });

  it('asks the server for archived resources when the toggle is switched on', () => {
    const component = create();
    flushInitial();

    component.onIncludeArchivedChange(fakeCheckboxEvent(true));

    const request = httpMock.expectOne((r) => r.url === `${API}/resources`);
    expect(request.request.params.get('includeArchived')).toBe('true');
    request.flush(page([resource({ isArchived: true })]));

    expect(component.items()[0].isArchived).toBe(true);
  });

  // The archived rows come back *from the server*, not from a client-side
  // filter over a fetched page — which is the mistake that would leave the page
  // count and total describing a different set than the rows under them.
  it('never filters the fetched page itself', () => {
    const component = create();

    httpMock
      .expectOne((r) => r.url === `${API}/resources`)
      .flush(page([resource({ id: 'r1' }), resource({ id: 'r2', isArchived: true })], { totalCount: 2 }));

    expect(component.items().map((r) => r.id)).toEqual(['r1', 'r2']);
  });

  it('searches server-side after the debounce, not on every keystroke', () => {
    vi.useFakeTimers();
    try {
      const component = create();
      flushInitial();

      component.onSearchInput(fakeInputEvent('Con'));
      component.onSearchInput(fakeInputEvent('Conf'));
      httpMock.expectNone((r) => r.url === `${API}/resources`);

      vi.advanceTimersByTime(300);

      const request = httpMock.expectOne((r) => r.url === `${API}/resources`);
      expect(request.request.params.get('search')).toBe('Conf');
      request.flush(page([resource()]));
    } finally {
      vi.useRealTimers();
    }
  });

  // Staying on page 3 of a filter that now has one page shows "no resources"
  // for a filter that has plenty.
  it('returns to page 1 when a filter changes the matching set', () => {
    const component = create();
    httpMock
      .expectOne((r) => r.url === `${API}/resources`)
      .flush(page([resource()], { totalCount: 120, totalPages: 3, hasNextPage: true }));

    component.goToNextPage();
    httpMock
      .expectOne((r) => r.url === `${API}/resources`)
      .flush(page([resource()], { page: 2, totalCount: 120, totalPages: 3, hasNextPage: true, hasPreviousPage: true }));
    expect(component.page()).toBe(2);

    component.onIncludeArchivedChange(fakeCheckboxEvent(true));

    const request = httpMock.expectOne((r) => r.url === `${API}/resources`);
    expect(request.request.params.get('page')).toBe('1');
    request.flush(page([resource()]));
    expect(component.page()).toBe(1);
  });

  // Page metadata comes straight off the server's PagedResult and is never
  // recomputed here — one source, not two that could disagree.
  it('mirrors the server page metadata rather than deriving it', () => {
    const component = create();
    httpMock
      .expectOne((r) => r.url === `${API}/resources`)
      .flush(page([resource()], { totalCount: 120, totalPages: 3, hasNextPage: true }));

    expect(component.totalPages()).toBe(3);
    expect(component.hasNextPage()).toBe(true);
  });

  it('shows a retryable error state when the fetch fails', () => {
    const component = create();
    httpMock
      .expectOne((r) => r.url === `${API}/resources`)
      .flush(null, { status: 500, statusText: 'Server Error' });

    expect(component.loadError()).toBe(true);

    component.retry();
    httpMock.expectOne((r) => r.url === `${API}/resources`).flush(page([resource()]));

    expect(component.loadError()).toBe(false);
    expect(component.items()).toHaveLength(1);
  });

  // A stale response must not overwrite a newer one — a toggle click racing a
  // debounced search.
  it('ignores a response that a newer request has already superseded', () => {
    const component = create();
    const first = httpMock.expectOne((r) => r.url === `${API}/resources`);

    component.onIncludeArchivedChange(fakeCheckboxEvent(true));
    const second = httpMock.expectOne((r) => r.url === `${API}/resources`);

    second.flush(page([resource({ id: 'newer' })]));
    first.flush(page([resource({ id: 'stale' })]));

    expect(component.items().map((r) => r.id)).toEqual(['newer']);
  });

  // ---- Rendered output ----
  //
  // The list is mostly template, and what it renders is the point: a row that
  // led to the member-facing detail screen would be a detour on every use, and
  // no signal assertion would notice.
  //
  // These build their own fixture rather than reusing `create()`, because they
  // need a Router stub RouterLink can actually resolve an href against.

  // **A real router, not the stub the other tests use.** RouterLink builds an
  // href by asking the router to create a UrlTree and then serialize it, so a
  // stub whose `serializeUrl` returns a placeholder makes every href in the DOM
  // an empty string — and an assertion about where a link points would then
  // pass or fail on the stub rather than on the template. This component
  // injects no Router of its own, so handing it the real one costs nothing.
  function createRendered(items: ResourceSummary[]) {
    TestBed.configureTestingModule({
      imports: [AdminResourceListComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });

    httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(AdminResourceListComponent);
    httpMock.expectOne((r) => r.url === `${API}/resources`).flush(page(items));
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('links each row and its action at the admin edit form, not the member detail screen', () => {
    const dom = createRendered([resource({ id: 'r7' })]);

    const links = [...dom.querySelectorAll('a')].map((a) => a.getAttribute('href'));

    expect(links).toContain('/admin/resources/r7');
    expect(links).toContain('/admin/resources/new');
    expect(links).not.toContain('/resources/r7');
  });

  it('reads an archived row as View rather than Edit, since it cannot be edited', () => {
    const dom = createRendered([resource({ id: 'r8', isArchived: true, requiresApproval: true })]);

    expect(dom.querySelector('.row-action')?.textContent?.trim()).toBe('View');
    expect(dom.textContent).toContain('Archived');
    expect(dom.textContent).toContain('Approval required');
  });

  it('tells an empty tenant to create its first resource', () => {
    const dom = createRendered([]);

    expect(dom.textContent).toContain('No resources yet');
  });
});
