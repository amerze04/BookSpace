import { Injectable, signal } from '@angular/core';

export type NotificationKind = 'error' | 'info';

export interface Notification {
  id: number;
  kind: NotificationKind;
  message: string;
}

const AUTO_DISMISS_MS = 6000;

// The one place a failure the user didn't already get local feedback for
// becomes something visible — CLAUDE.md §6's rule that a rejection is never
// silent, applied on the client side of the same contract.
@Injectable({ providedIn: 'root' })
export class NotificationService {
  private readonly notificationsSignal = signal<Notification[]>([]);
  readonly notifications = this.notificationsSignal.asReadonly();

  private nextId = 0;

  show(message: string, kind: NotificationKind = 'error'): void {
    const id = this.nextId++;
    this.notificationsSignal.update((current) => [...current, { id, kind, message }]);
    setTimeout(() => this.dismiss(id), AUTO_DISMISS_MS);
  }

  dismiss(id: number): void {
    this.notificationsSignal.update((current) => current.filter((notification) => notification.id !== id));
  }
}
