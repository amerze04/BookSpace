import { TestBed } from '@angular/core/testing';
import { BreadcrumbService } from '../breadcrumb.service';

describe('BreadcrumbService', () => {
  it('starts with no override', () => {
    const service = TestBed.inject(BreadcrumbService);
    expect(service.override()).toBeNull();
  });

  it('reflects whatever was last set, including clearing it back to null', () => {
    const service = TestBed.inject(BreadcrumbService);

    service.setOverride('Conference Room A');
    expect(service.override()).toBe('Conference Room A');

    service.setOverride(null);
    expect(service.override()).toBeNull();
  });

  it('starts with no insertBeforeLast, independently of override', () => {
    const service = TestBed.inject(BreadcrumbService);
    expect(service.insertBeforeLast()).toBeNull();
  });

  it('reflects whatever insertBeforeLast was last set, including clearing it back to null', () => {
    const service = TestBed.inject(BreadcrumbService);

    service.setInsertBeforeLast('Conference Room A');
    expect(service.insertBeforeLast()).toBe('Conference Room A');

    service.setInsertBeforeLast(null);
    expect(service.insertBeforeLast()).toBeNull();
  });
});
