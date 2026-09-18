import { isWebLocksSupported, releaseRefreshLock, runWithWebLock, tryAcquireRefreshLock } from '../refresh-lock';

describe('refresh-lock', () => {
  afterEach(() => {
    localStorage.clear();
  });

  describe('tryAcquireRefreshLock / releaseRefreshLock (localStorage fallback)', () => {
    it('acquires a lock when none is held', () => {
      expect(tryAcquireRefreshLock(8_000)).not.toBeNull();
    });

    it('refuses a second acquire while a live lock is held', () => {
      tryAcquireRefreshLock(8_000);
      expect(tryAcquireRefreshLock(8_000)).toBeNull();
    });

    it('treats a lock older than the TTL as abandoned and lets a new one through', () => {
      localStorage.setItem('bookspace.refreshLock', JSON.stringify({ id: 'stale', acquiredAt: Date.now() - 20_000 }));
      expect(tryAcquireRefreshLock(8_000)).not.toBeNull();
    });

    it('only releases a lock the caller actually holds', () => {
      const ownId = tryAcquireRefreshLock(8_000);
      // Simulate a newer tab having since taken over after this one's lock
      // was judged abandoned.
      localStorage.setItem('bookspace.refreshLock', JSON.stringify({ id: 'someone-else', acquiredAt: Date.now() }));
      releaseRefreshLock(ownId!);
      expect(localStorage.getItem('bookspace.refreshLock')).not.toBeNull();
    });
  });

  describe('isWebLocksSupported', () => {
    const originalLocks = (navigator as unknown as { locks?: unknown }).locks;

    afterEach(() => {
      restoreLocks(originalLocks);
    });

    it('is false when navigator.locks is absent', () => {
      deleteLocks();
      expect(isWebLocksSupported()).toBe(false);
    });

    it('is true when navigator.locks.request exists', () => {
      stubLocks({ request: () => Promise.resolve() });
      expect(isWebLocksSupported()).toBe(true);
    });
  });

  describe('runWithWebLock', () => {
    const originalLocks = (navigator as unknown as { locks?: unknown }).locks;

    afterEach(() => {
      restoreLocks(originalLocks);
    });

    it('requests the BookSpace refresh lock by name and returns the callback result', async () => {
      const requestSpy = vi.fn((_name: string, callback: () => Promise<unknown>) => callback());
      stubLocks({ request: requestSpy });

      const result = await runWithWebLock(async () => 'done');

      expect(result).toBe('done');
      expect(requestSpy).toHaveBeenCalledWith('bookspace.refresh', expect.any(Function));
    });

    it('propagates a rejection from the callback', async () => {
      stubLocks({ request: (_name: string, callback: () => Promise<unknown>) => callback() });

      await expect(runWithWebLock(async () => Promise.reject(new Error('boom')))).rejects.toThrow('boom');
    });
  });
});

function stubLocks(value: unknown): void {
  Object.defineProperty(navigator, 'locks', { configurable: true, value });
}

function deleteLocks(): void {
  Reflect.deleteProperty(navigator, 'locks');
}

function restoreLocks(original: unknown): void {
  if (original === undefined) {
    Reflect.deleteProperty(navigator, 'locks');
  } else {
    Object.defineProperty(navigator, 'locks', { configurable: true, value: original });
  }
}
