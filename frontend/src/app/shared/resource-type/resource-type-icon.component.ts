import { Component, input } from '@angular/core';
import { ResourceType } from '../../features/resources/resources.models';

// The per-ResourceType glyph, extracted 2026-09-16 (WP-7 Phase 2) from the
// identical @switch duplicated in ResourceListComponent and
// ResourceDetailComponent — the third caller (this screen's own resource
// summary card) is what WP-6's brand-mark precedent treats as the point to
// name a shared component instead of a third copy.
//
// Deliberately just the glyph, not the circular badge around it: the badge's
// size and background differ per host (list card vs. detail header vs. this
// screen's summary card), so that styling stays with each host's own
// `.resource-icon` wrapper rather than being parameterized here.
@Component({
  selector: 'app-resource-type-icon',
  imports: [],
  templateUrl: './resource-type-icon.component.html',
  styleUrl: './resource-type-icon.component.scss',
})
export class ResourceTypeIconComponent {
  readonly type = input.required<ResourceType>();
}
