import { Injectable, signal } from '@angular/core';

// Lets a leaf route replace the *last* crumb ShellComponent's route-title
// walk would otherwise show with something only that page knows at runtime —
// a loaded resource's own name, for instance, which no static
// `data: { title }` in app.routes.ts could ever carry. Route-config titles
// stay the fallback (and the only thing shown before a page has loaded
// anything), so a page that never needs this never has to touch it.
//
// A page that sets an override is responsible for clearing it again when it
// no longer applies — ResourceDetailComponent clears it on destroy so a
// stale resource name can't linger onto the next page the breadcrumb walk
// computes for.
@Injectable({ providedIn: 'root' })
export class BreadcrumbService {
  private readonly overrideSignal = signal<string | null>(null);
  readonly override = this.overrideSignal.asReadonly();

  // WP-7 Phase 2: the availability screen sits at `/resources/:id/availability`,
  // a route that's a *sibling* of `:id` rather than nested under it
  // (app.routes.ts), so its own route-title chain only ever contributes
  // `['Resources', 'Availability']` — two crumbs, with nothing standing for
  // the resource itself. `override` can't fix that: it replaces the *last*
  // crumb, and 'Availability' is meant to stay. This is a second, independent
  // slot that ShellComponent splices in just before the last crumb instead —
  // giving 'Resources > Conference Room A > Availability' without restructuring
  // routing or touching how ResourceDetailComponent's own override behaves.
  private readonly insertBeforeLastSignal = signal<string | null>(null);
  readonly insertBeforeLast = this.insertBeforeLastSignal.asReadonly();

  setOverride(label: string | null): void {
    this.overrideSignal.set(label);
  }

  setInsertBeforeLast(label: string | null): void {
    this.insertBeforeLastSignal.set(label);
  }
}
