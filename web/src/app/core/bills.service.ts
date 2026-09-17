import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Bill, IngestResponse } from './models';

/**
 * Holds the bill list. A single signal is the source of truth for every view, so an upload made on
 * the bills page is immediately reflected in the utility filter on the chat page.
 */
@Injectable({ providedIn: 'root' })
export class BillsService {
  private readonly http = inject(HttpClient);

  readonly bills = signal<Bill[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  async refresh(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.bills.set(await firstValueFrom(this.http.get<Bill[]>('/api/bills')));
    } catch (err) {
      this.error.set(describe(err, 'Could not load your bills.'));
    } finally {
      this.loading.set(false);
    }
  }

  async upload(file: File): Promise<IngestResponse> {
    const form = new FormData();
    form.append('file', file, file.name);

    const response = await firstValueFrom(this.http.post<IngestResponse>('/api/bills', form));
    this.bills.update((current) => [response.bill, ...current]);
    return response;
  }

  async remove(id: string): Promise<void> {
    await firstValueFrom(this.http.delete(`/api/bills/${id}`));
    this.bills.update((current) => current.filter((b) => b.id !== id));
  }
}

/** Surfaces the API's own error text when it sent one, rather than a generic HTTP message. */
export function describe(err: unknown, fallback: string): string {
  const body = (err as { error?: { error?: string } })?.error;
  return body?.error ?? fallback;
}
