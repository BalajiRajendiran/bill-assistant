/** Mirrors the API contracts in BillAssistant.Api/Contracts. */

export type UtilityKind = 'Unknown' | 'Electricity' | 'Water' | 'Gas' | 'Internet' | 'Waste' | 'Other';

export type MetadataStatus = 'Pending' | 'Extracted' | 'NeedsReview';

export interface Bill {
  id: string;
  fileName: string;
  utility: UtilityKind;
  provider: string | null;
  accountNumberLast4: string | null;
  periodStart: string | null;
  periodEnd: string | null;
  periodLabel: string;
  amountDue: number | null;
  currency: string | null;
  dueDate: string | null;
  usageQuantity: number | null;
  usageUnit: string | null;
  pageCount: number;
  chunkCount: number;
  metadataStatus: MetadataStatus;
  metadataNotes: string | null;
  uploadedAt: string;
}

export interface IngestResponse {
  bill: Bill;
  chunksIndexed: number;
  warning: string | null;
}

export interface Citation {
  billId: string;
  fileName: string;
  pageNumber: number;
  snippet: string;
  utility: string;
  score: number | null;
}

/** One bill's figures, oldest first. Present when the question was numeric. */
export interface PeriodTotal {
  label: string;
  periodStart: string | null;
  periodEnd: string | null;
  amount: number;
  usage: number | null;
  usageUnit: string | null;
}

export interface Totals {
  billCount: number;
  totalAmount: number;
  currency: string;
  totalUsage: number | null;
  usageUnit: string | null;
  from: string | null;
  to: string | null;
  series: PeriodTotal[];
}

export interface AskResponse {
  answer: string;
  citations: Citation[];
  totals: Totals | null;
}

/** One frame of the SSE stream from POST /api/chat/stream. */
export interface ChatStreamEvent {
  type: 'token' | 'sources' | 'done' | 'error';
  text?: string;
  citations?: Citation[];
  totals?: Totals | null;
}
