import { Component } from '@angular/core';

// Every shell nav item routes here for now — WP-7 replaces each of these one
// at a time with a real screen. Deliberately not styled to match the design
// mock's "Page content will appear here" illustration; that's decoration with
// no functional value at this stage.
@Component({
  selector: 'app-placeholder',
  imports: [],
  template: `<p>Placeholder</p>`,
})
export class PlaceholderComponent {}
