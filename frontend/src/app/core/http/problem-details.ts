// The backend's one error contract (CLAUDE.md §6, decisions/0016): every
// rejection is one of these, never a raw exception message. Shape confirmed
// against a real response rather than assumed — notably `errors`' keys are
// PascalCase ("Email", not "email"), matching the C# request property names
// FluentValidation reports against, not the wire's usual camelCase.
export interface ProblemDetails {
  title: string;
  status: number;
  reasonCode: string;
  correlationId: string;
  errors?: Record<string, string[]>;
}

export function isProblemDetails(value: unknown): value is ProblemDetails {
  return (
    typeof value === 'object' &&
    value !== null &&
    typeof (value as ProblemDetails).reasonCode === 'string' &&
    typeof (value as ProblemDetails).title === 'string'
  );
}
