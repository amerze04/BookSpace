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

  setOverride(label: string | null): void {
    this.overrideSignal.set(label);
  }
}
