import { defaultTimeZoneId, supportedTimeZoneIds, timeZoneLabel } from '../timezones/timezone-catalog';

// Admin console phase 3. The timezone picker's source of values.
//
// Worth testing despite being three small functions, because what they get
// wrong is not a crash: `InvalidTimeZone` is thrown for a *resolvable* id that
// is not canonical IANA (CLAUDE.md §4.3 — `TryConvertIanaIdToWindowsId` has to
// succeed too), so a picker offering the wrong spellings produces a 400 the
// admin cannot act on, from a control that looks like it was designed to
// prevent exactly that.

describe('supportedTimeZoneIds', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  // The real host answer. Asserted for *shape* rather than contents — the tz
  // database changes, and pinning a count or a specific zone would make this
  // test a maintenance item that proves nothing.
  it('returns canonical IANA ids from the host', () => {
    const zones = supportedTimeZoneIds();

    expect(zones.length).toBeGreaterThan(50);
    expect(zones).toContain('Europe/Warsaw');
  });

  // **The host list does not contain "UTC"** — it is a tz database *link*, not
  // a zone, so `supportedValuesOf` runs Africa/Abidjan … Pacific/Wallis without
  // it. This test found that; it was not anticipated.
  //
  // Leaving it out would make the one id an administrator most wants for a
  // resource with no real location unpickable, and would send
  // `defaultTimeZoneId`'s fallback to whatever sorted first. The backend
  // accepts "UTC" — verified against the running API, 2026-09-23 — so adding it
  // is not offering something the server would refuse.
  it('adds UTC, which the host list omits and the API accepts', () => {
    const zones = supportedTimeZoneIds();

    expect(zones).toContain('UTC');
    expect(zones[0]).toBe('UTC');
  });

  it('does not add a second UTC to a host list that already has one', () => {
    vi.spyOn(Intl, 'supportedValuesOf').mockReturnValue(['UTC', 'Europe/Rome'] as unknown as string[]);

    expect(supportedTimeZoneIds().filter((z) => z === 'UTC')).toHaveLength(1);
  });

  // The two spellings decision `0003`/§4.3 refuse. A picker that offered either
  // would be handing the admin a guaranteed 400.
  it('never offers a Windows id or a lower-cased one', () => {
    const zones = supportedTimeZoneIds();

    expect(zones).not.toContain('Eastern Standard Time');
    expect(zones).not.toContain('america/new_york');
  });

  // `supportedValuesOf` throws a RangeError for a key it does not know, and a
  // host could expose the function without the `timeZone` key. A form that threw
  // while building its own options would fail in a way that looks nothing like
  // a missing browser API.
  it('falls back to a short list rather than throwing when the API rejects the key', () => {
    vi.spyOn(Intl, 'supportedValuesOf').mockImplementation(() => {
      throw new RangeError('invalid key');
    });

    const zones = supportedTimeZoneIds();

    expect(zones.length).toBeGreaterThan(0);
    expect(zones).toContain('UTC');
  });

  it('falls back when the host returns nothing usable', () => {
    vi.spyOn(Intl, 'supportedValuesOf').mockReturnValue([] as unknown as string[]);

    expect(supportedTimeZoneIds()).toContain('UTC');
  });
});

describe('defaultTimeZoneId', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  // An admin usually administers the place they are in, so their own zone is
  // right far more often than any fixed default.
  it("prefers the administrator's own timezone when the picker offers it", () => {
    expect(defaultTimeZoneId(['UTC', 'Europe/Warsaw', 'America/New_York'])).toBe(
      ['UTC', 'Europe/Warsaw', 'America/New_York'].includes(
        Intl.DateTimeFormat().resolvedOptions().timeZone,
      )
        ? Intl.DateTimeFormat().resolvedOptions().timeZone
        : 'UTC',
    );
  });

  // Never an arbitrary first entry: UTC is unambiguous, present in both
  // catalogues, and obviously a default rather than a guess at a location.
  it('falls back to UTC rather than to whatever happens to be first', () => {
    expect(defaultTimeZoneId(['Pacific/Apia', 'UTC', 'Asia/Tokyo'])).toBe(
      Intl.DateTimeFormat().resolvedOptions().timeZone === 'Pacific/Apia' ? 'Pacific/Apia' : 'UTC',
    );
  });

  it('uses the first entry only when even UTC is absent', () => {
    expect(defaultTimeZoneId(['Asia/Tokyo', 'Europe/Rome'])).toBe('Asia/Tokyo');
  });

  it('does not throw when the host cannot resolve a local zone', () => {
    vi.spyOn(Intl, 'DateTimeFormat').mockImplementation(() => {
      throw new Error('no ICU');
    });

    expect(defaultTimeZoneId(['UTC', 'Asia/Tokyo'])).toBe('UTC');
  });
});

describe('timeZoneLabel', () => {
  // The id stays the value; only the label changes. An admin who knows the id
  // can still find it by typing into the select, because the words are
  // unchanged and in the same order.
  it('spaces the separators without reordering or renaming anything', () => {
    expect(timeZoneLabel('Europe/Warsaw')).toBe('Europe / Warsaw');
    expect(timeZoneLabel('America/New_York')).toBe('America / New York');
    expect(timeZoneLabel('America/Indiana/Knox')).toBe('America / Indiana / Knox');
  });

  it('leaves a single-segment id alone', () => {
    expect(timeZoneLabel('UTC')).toBe('UTC');
  });
});
