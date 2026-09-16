// A best-effort mutex over one localStorage key, so that when several tabs'
// access tokens expire around the same moment they don't all fire their own
// POST /auth/refresh with the same refresh token — decisions/0011 treats a
// second use of an already-rotated token as theft and revokes the whole
// family, so two tabs racing that call can destroy a session that was never
// actually compromised.
//
// Not a true atomic compare-and-swap — localStorage has no such primitive,
// and two tabs reading+writing in the same JS macrotask can still both
// believe they won. That residual race is accepted deliberately: the backend
// reuse-detection is the actual safety net (it already has to be, since a
// legitimate stolen-token case looks identical), and this lock only exists to
// make the two-tabs-racing case rare instead of the common one every access
// token expiry would otherwise produce.
const LOCK_KEY = 'bookspace.refreshLock';

interface RefreshLock {
  id: string;
  acquiredAt: number;
}

function randomLockId(): string {
  return typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
    ? crypto.randomUUID()
    : `${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

// Returns the id to release with on success, or null if another tab already
// holds a live lock. A lock older than ttlMs is treated as abandoned — the
// tab that took it may have crashed or been closed mid-refresh — so a stuck
// lock cannot wedge every other tab out of refreshing forever.
export function tryAcquireRefreshLock(ttlMs: number): string | null {
  const raw = localStorage.getItem(LOCK_KEY);

  if (raw) {
    try {
      const existing = JSON.parse(raw) as RefreshLock;
      if (Date.now() - existing.acquiredAt < ttlMs) {
        return null;
      }
    } catch {
      // Malformed value — fall through and treat it as abandoned.
    }
  }

  const id = randomLockId();
  localStorage.setItem(LOCK_KEY, JSON.stringify({ id, acquiredAt: Date.now() } satisfies RefreshLock));
  return id;
}

// Only releases a lock this call site actually holds — never a newer one
// belonging to a tab that has since taken over after this one's lock was
// judged abandoned.
export function releaseRefreshLock(id: string): void {
  const raw = localStorage.getItem(LOCK_KEY);
  if (!raw) {
    return;
  }

  try {
    const existing = JSON.parse(raw) as RefreshLock;
    if (existing.id === id) {
      localStorage.removeItem(LOCK_KEY);
    }
  } catch {
    localStorage.removeItem(LOCK_KEY);
  }
}

// Web Locks API (navigator.locks) is a true cross-tab mutex — a lock is held
// for exactly as long as the callback's promise is pending, with no TTL to
// misjudge and no window where two tabs can both believe they hold it, which
// is precisely what the localStorage-based lock above can't guarantee. It has
// been supported in every evergreen browser (Chrome/Edge 69+, Firefox 96+,
// Safari 15.4+) since well before this app's realistic target environment, so
// it's used as the primary mechanism; the localStorage lock above stays as
// the fallback for whatever doesn't have it.
export function isWebLocksSupported(): boolean {
  return typeof navigator !== 'undefined' && typeof navigator.locks?.request === 'function';
}

const WEB_LOCK_NAME = 'bookspace.refresh';

// Runs `fn` under the named lock, held for the lifetime of the returned
// promise. Rejection of `fn` propagates normally; the lock is always released
// on settlement, by the browser itself.
export function runWithWebLock<T>(fn: () => Promise<T>): Promise<T> {
  return navigator.locks.request(WEB_LOCK_NAME, () => fn());
}
