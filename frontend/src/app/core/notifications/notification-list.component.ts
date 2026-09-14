import { Component, inject } from '@angular/core';
import { NotificationService } from './notification.service';

// Mounted once, at the app root (app.html) — so it's visible above the login
// page and every authenticated screen alike, regardless of which one is
// currently routed.
@Component({
  selector: 'app-notification-list',
  imports: [],
  templateUrl: './notification-list.component.html',
  styleUrl: './notification-list.component.scss',
})
export class NotificationListComponent {
  private readonly notificationService = inject(NotificationService);
  protected readonly notifications = this.notificationService.notifications;

  protected dismiss(id: number): void {
    this.notificationService.dismiss(id);
  }
}
