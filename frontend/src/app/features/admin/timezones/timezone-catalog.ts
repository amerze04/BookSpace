// The timezone picker's source of values (admin console phase 3).
//
// **`Intl.supportedValuesOf('timeZone')`, not a hand-written list**, and the
// reason is CLAUDE.md §4.3: a stored `TimeZoneId` is an IANA id and *only* an
// IANA id. The backend's `ITimeZoneCatalog` does not merely ask whether
// `TimeZoneInfo` can resolve the string — on Windows it resolves Windows ids
// too — it additionally requires `TryConvertIanaIdToWindowsId` to succeed,
// which is true only for a canonical IANA id. Refusing `"Eastern Standard
// Time"` and `"america/new_york"` is the point, not an accident.
//
// `Intl.supportedValuesOf('timeZone')` returns exactly that: canonical IANA
// zone names, already sorted, from the host's own ICU data. A list typed out
// here would be a second copy of the tz database, wrong within a year, and
// wrong in the direction that produces a 400 the admin cannot act on.
//
// **The picker is still not a guarantee.** The two ICU databases — the
// browser's and the server's — can disagree at the edges, most often over a
// zone renamed or added between releases, and a link name that one treats as
// canonical the other may not. So `InvalidTimeZone` stays handled in
// `resource-rejection.ts` rather than being treated as unreachable. Narrowing
// the input to a list is what makes that refusal rare; it is not what makes it
// impossible.

// The fallback, used only where `Intl.supportedValuesOf` is missing. It is not
// a catalogue and does not pretend to be one — it is enough zones to keep the
// form usable rather than empty, spread across offsets so a reasonable default
// is usually present. Any host new enough to run this app has the real thing;
// this exists so a missing API degrades into a short list rather than a select
// with no options and no explanation.
const FALLBACK_TIME_ZONES = [
  'UTC',
  'Europe/London',
  'Europe/Berlin',
  'Europe/Warsaw',
  'Europe/Sarajevo',
  'America/New_York',
  'America/Chicago',
  'America/Denver',
  'America/Los_Angeles',
  'Asia/Dubai',
  'Asia/Kolkata',
  'Asia/Singapore',
  'Asia/Tokyo',
  'Australia/Sydney',
];

interface IntlWithSupportedValues {
  supportedValuesOf?: (key: string) => string[];
}

// Sorted, de-duplicated, and guaranteed non-empty.
//
// Wrapped in try/catch rather than a bare feature check: `supportedValuesOf`
// throws a RangeError for a key it does not know, and a host could plausibly
// expose the function without the `timeZone` key. A form that threw while
// building its own options would fail in a way that looks nothing like a
// missing browser API.
export function supportedTimeZoneIds(): string[] {
  try {
    const intl = Intl as unknown as IntlWithSupportedValues;
    const zones = intl.supportedValuesOf?.('timeZone');

    if (Array.isArray(zones) && zones.length > 0) {
      return withUtc(zones);
    }
  } catch {
    // Fall through to the short list below.
  }

  return [...FALLBACK_TIME_ZONES];
}

// **`Intl.supportedValuesOf('timeZone')` does not return `"UTC"`** — it returns
// the tz database's zone names, of which UTC is a link rather than a zone, so
// the list runs `Africa/Abidjan` … `Pacific/Wallis` with no UTC in it. Found by
// a failing test in admin console phase 3, not reasoned about.
//
// That matters twice over. It would leave the one id an administrator is most
// likely to want for a resource with no real location unpickable; and because
// `defaultTimeZoneId` falls back to UTC, a host whose own zone was absent would
// have defaulted to whatever sorted first — `Africa/Abidjan`.
//
// Adding it is safe rather than hopeful: **verified against the running API on
// 2026-09-23**, `POST /resources` with `"timeZoneId": "UTC"` returns 201 and
// stores it, so the backend's `TryConvertIanaIdToWindowsId` check (CLAUDE.md
// §4.3) accepts it. Prepended rather than appended so it sits at the top of the
// select, where a no-location resource's author will look for it.
function withUtc(zones: string[]): string[] {
  return zones.includes('UTC') ? zones : ['UTC', ...zones];
}

// What a new resource's timezone starts as: the administrator's own, which is
// right far more often than any fixed default, because an admin usually
// administers the place they are in.
//
// Falls back to UTC — never to an arbitrary entry of the list — because UTC is
// the one id that is unambiguous, always present in both catalogues, and
// obviously a default rather than a guess at the admin's location.
export function defaultTimeZoneId(available: string[] = supportedTimeZoneIds()): string {
  try {
    const local = Intl.DateTimeFormat().resolvedOptions().timeZone;
    if (local && available.includes(local)) {
      return local;
    }
  } catch {
    // Fall through.
  }

  return available.includes('UTC') ? 'UTC' : available[0];
}

// "Europe/Warsaw" -> "Europe / Warsaw", and "America/Indiana/Knox" ->
// "America / Indiana / Knox". Underscores are the tz database's word separator,
// so they become spaces.
//
// The id itself stays the value; only the label changes. An admin who knows the
// id can still search the select for it by typing, because the words are
// unchanged and in the same order.
export function timeZoneLabel(id: string): string {
  return id.split('/').join(' / ').replace(/_/g, ' ');
}
