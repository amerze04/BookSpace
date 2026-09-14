import { Component } from '@angular/core';

// The BookSpace logo mark + wordmark, as a component now that a third place
// (the authenticated shell) needs it — the login page's two copies were fine
// duplicated once; a third made it worth naming instead.
@Component({
  selector: 'app-brand-mark',
  imports: [],
  templateUrl: './brand-mark.component.html',
  styleUrl: './brand-mark.component.scss',
})
export class BrandMarkComponent {}
