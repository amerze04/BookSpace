import { isProblemDetails } from '../problem-details';

// WP-7 Phase 7 step 2. Twenty-one lines, one type guard, and **quietly the most
// load-bearing untested function in the app**: every rejection dialect asks it
// whether a response body is the backend's error contract, and a `false` sends
// the caller down the generic-message path instead of the coded one.
//
// It has already cost real assertions. Five of the decision panel's first
// refusal tests passed while asserting the wrong message, because their fixtures
// carried `reasonCode` but not `title` and so fell through to the generic
// branch. That is the failure mode this spec exists to pin: not a crash, a
// silently worse message.
describe('isProblemDetails', () => {
  const real = {
    title: 'The request was rejected by a rule.',
    status: 422,
    reasonCode: 'BookingNotPending',
    correlationId: 'abc',
  };

  it('accepts what the backend actually sends', () => {
    expect(isProblemDetails(real)).toBe(true);
  });

  it('accepts one carrying per-field validation errors too', () => {
    expect(isProblemDetails({ ...real, errors: { Note: ['too long'] } })).toBe(true);
  });

  // **Both fields are required, and this is the trap.** A body with a reason
  // code but no title looks like a ProblemDetails to a reader and is rejected
  // here — which is correct (a real one always has both) but is exactly what
  // makes a hand-written test fixture silently take the generic path.
  it('rejects a body carrying only a reason code', () => {
    expect(isProblemDetails({ reasonCode: 'BookingNotPending' })).toBe(false);
  });

  it('rejects a body carrying only a title', () => {
    expect(isProblemDetails({ title: 'Something went wrong' })).toBe(false);
  });

  // A status-0 failure gives `HttpErrorResponse.error` a ProgressEvent, not a
  // body. Reading a reason code off it would find nothing; the dialects check
  // status 0 before ever getting here, and this is the backstop.
  it('rejects the things a failed transport actually produces', () => {
    expect(isProblemDetails(null)).toBe(false);
    expect(isProblemDetails(undefined)).toBe(false);
    expect(isProblemDetails('Internal Server Error')).toBe(false);
    expect(isProblemDetails(new ProgressEvent('error'))).toBe(false);
  });

  // `typeof null === 'object'` is the classic way a guard like this lets null
  // through; the explicit null check is why it does not.
  it('is not fooled by typeof null being object', () => {
    expect(isProblemDetails(null)).toBe(false);
  });

  it('rejects non-string values in the two fields it checks', () => {
    expect(isProblemDetails({ ...real, reasonCode: 422 })).toBe(false);
    expect(isProblemDetails({ ...real, title: null })).toBe(false);
  });

  it('rejects an array, which is an object but never this contract', () => {
    expect(isProblemDetails([real])).toBe(false);
  });
});
