// Mirrors `PasswordPolicy` on the backend, which is where the rule actually
// lives — these are the *message*, not the guarantee. `POST /auth/activate`
// validates the same bounds and answers 400 with a per-field error regardless
// of what this file says.
//
// Worth mirroring at all because the alternative is telling somebody their
// password is too short only after a round trip, on the one screen where they
// are inventing a password rather than recalling one.
//
// 12 to 128, length only, no composition rules. That follows NIST SP 800-63B,
// and the maximum is not cosmetic: the server runs PBKDF2 at 100k iterations
// over whatever it is given, so an unbounded password is a cheap way to make an
// anonymous endpoint do unbounded work. The reasoning is recorded in full
// beside the server-side constants; it was invented in user management phase 2
// because nothing in the PRD specifies one.
export const PASSWORD_MIN_LENGTH = 12;
export const PASSWORD_MAX_LENGTH = 128;
