import { TestBed } from '@angular/core/testing';
import { NotificationListComponent } from './notification-list.component';
import { NotificationService } from './notification.service';

// Item 15: toast/notification feedback has to be announceable to a screen
// reader without the user having to be looking at the screen when it appears.
describe('NotificationListComponent (item 15: accessibility)', () => {
  it('gives an error notification role="alert" so it is announced assertively', () => {
    TestBed.configureTestingModule({ imports: [NotificationListComponent] });
    const notifications = TestBed.inject(NotificationService);
    const fixture = TestBed.createComponent(NotificationListComponent);

    notifications.show('Something failed.', 'error');
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    const alertEl = el.querySelector('[role="alert"]');
    expect(alertEl).not.toBeNull();
    expect(alertEl?.textContent).toContain('Something failed.');
  });

  it('gives an info notification role="status" rather than role="alert"', () => {
    TestBed.configureTestingModule({ imports: [NotificationListComponent] });
    const notifications = TestBed.inject(NotificationService);
    const fixture = TestBed.createComponent(NotificationListComponent);

    notifications.show('Your session has expired. Please sign in again.', 'info');
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[role="status"]')).not.toBeNull();
    expect(el.querySelector('[role="alert"]')).toBeNull();
  });
});
