import { Component } from '@angular/core';
import { AuthService } from '../../core/auth/auth.service';

// Throwaway — proves phase 1's session works. Phase 3 replaces this with the
// real authenticated shell (wp6-plan.md §4/§5).
@Component({
  selector: 'app-home',
  imports: [],
  templateUrl: './home.component.html',
})
export class HomeComponent {
  constructor(protected readonly auth: AuthService) {}
}
