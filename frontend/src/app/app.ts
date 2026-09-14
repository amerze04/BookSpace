import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { NotificationListComponent } from './core/notifications/notification-list.component';

@Component({
  imports: [RouterOutlet, NotificationListComponent],
  selector: 'app-root',
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App {}
