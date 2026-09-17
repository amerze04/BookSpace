import { HttpErrorResponse } from '@angular/common/http';
import { isProblemDetails } from '../../core/http/problem-details';
import {
  CreateRecurrenceSeriesResponse,
  RecurrenceOccurrenceReport,
  RecurrenceOccurrenceReportStatus,
} from './recurrence.models';

// What came of a recurring submit — and the point of this file is that a
// **201 and a 422 carry the same thing**. `CreateRecurrenceSeriesCommandResponse`
// lists every occurrence the expansion produced and what happened to it
// (FR-5.4: never a bare count), and when nothing at all could be created,
// `NoOccurrencesCreatedException` attaches that identical list to a 422 via
// `AppException.Extensions`. So one shape, one renderer, and only the framing
// differs — "here is your series" versus "nothing could be booked".
export interface SeriesOutcome {
  // Null when nothing was created: the handler compensates its own
  // RecurrenceRule away in that case, so there is no series to point at.
  recurrenceRuleId: string | null;
  occurrences: RecurrenceOccurrenceReport[];
}

export function seriesOutcomeFromResponse(response: CreateRecurrenceSeriesResponse): SeriesOutcome {
  return { recurrenceRuleId: response.recurrenceRuleId, occurrences: response.occurrences };
}

// The all-refused 422, if that is what this error is. Recognized by its reason
// code *and* the shape of what rides with it — `occurrences` arrives as a
// top-level key beside `reasonCode` (GlobalExceptionHandler copies extensions
// straight onto ProblemDetails), which step 1 confirmed against the running
// API rather than inferring.
export function parseSeriesRefusal(error: unknown): SeriesOutcome | null {
  if (!(error instanceof HttpErrorResponse) || !isProblemDetails(error.error)) {
    return null;
  }

  const problem = error.error as { reasonCode: string; occurrences?: unknown };
  if (problem.reasonCode !== 'NoOccurrencesCreated' || !Array.isArray(problem.occurrences)) {
    return null;
  }

  return { recurrenceRuleId: null, occurrences: problem.occurrences as RecurrenceOccurrenceReport[] };
}

export interface SeriesSummary {
  created: number;
  skipped: number;
  refused: number;
  total: number;
}

export function summarizeOccurrences(occurrences: readonly RecurrenceOccurrenceReport[]): SeriesSummary {
  const count = (status: RecurrenceOccurrenceReportStatus) =>
    occurrences.filter((occurrence) => occurrence.status === status).length;

  return {
    created: count('Created'),
    skipped: count('SkippedSpringForwardGap'),
    refused: count('Refused'),
    total: occurrences.length,
  };
}

// Why one occurrence didn't happen, in a member's words.
//
// `SkippedSpringForwardGap` gets decision `0008`'s own reasoning spelled out
// rather than a bare "skipped": the local time genuinely does not exist on that
// date, and the policy is deliberately to skip rather than shift — which is
// surprising enough that it has to be said, not implied.
//
// A `Refused` occurrence carries a reason code from the same catalogue the
// one-off path uses, worded for a single date here rather than for the form as
// a whole.
const REFUSAL_LABELS: Record<string, string> = {
  SlotUnavailable: 'Already booked',
  CapacityExceeded: 'Not enough units free',
  BlackoutPeriod: 'Blackout period',
  OutsideAvailability: 'Outside bookable hours',
  BookingInThePast: 'Already in the past',
  ResourceArchived: 'Resource archived',
};

export function occurrenceReasonLabel(occurrence: RecurrenceOccurrenceReport): string {
  if (occurrence.status === 'Created') {
    return 'Booked';
  }

  if (occurrence.status === 'SkippedSpringForwardGap') {
    return 'Skipped — this time does not exist on that date (clocks move forward)';
  }

  return occurrence.reasonCode ? (REFUSAL_LABELS[occurrence.reasonCode] ?? occurrence.reasonCode) : 'Refused';
}

// "3 booked, 1 skipped, 2 refused" — the summary FR-5.4 asks for, above the
// per-date list rather than instead of it.
export function summaryLine(summary: SeriesSummary): string {
  const parts: string[] = [];
  if (summary.created > 0) {
    parts.push(`${summary.created} booked`);
  }
  if (summary.skipped > 0) {
    parts.push(`${summary.skipped} skipped`);
  }
  if (summary.refused > 0) {
    parts.push(`${summary.refused} refused`);
  }
  return parts.length > 0 ? parts.join(', ') : 'No occurrences';
}
