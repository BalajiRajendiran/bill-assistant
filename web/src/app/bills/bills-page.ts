import { DecimalPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { BillsService, describe } from '../core/bills.service';
import { Bill } from '../core/models';

@Component({
  selector: 'app-bills-page',
  imports: [DecimalPipe],
  templateUrl: './bills-page.html',
  styleUrl: './bills-page.css',
})
export class BillsPage {
  private readonly service = inject(BillsService);

  protected readonly bills = this.service.bills;
  protected readonly loading = this.service.loading;
  protected readonly loadError = this.service.error;

  protected readonly uploading = signal(false);
  protected readonly uploadError = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);
  protected readonly dragging = signal(false);

  /** Shown as a running tally at the top of the list. */
  protected readonly summary = computed(() => {
    const priced = this.bills().filter((b) => b.amountDue !== null);
    return {
      count: this.bills().length,
      total: priced.reduce((sum, b) => sum + (b.amountDue ?? 0), 0),
      currency: priced[0]?.currency ?? 'USD',
      needsReview: this.bills().filter((b) => b.metadataStatus === 'NeedsReview').length,
    };
  });

  constructor() {
    void this.service.refresh();
  }

  protected onDragOver(event: DragEvent): void {
    event.preventDefault();
    this.dragging.set(true);
  }

  protected onDragLeave(): void {
    this.dragging.set(false);
  }

  protected async onDrop(event: DragEvent): Promise<void> {
    event.preventDefault();
    this.dragging.set(false);
    await this.uploadAll(event.dataTransfer?.files);
  }

  protected async onFilePicked(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    await this.uploadAll(input.files);
    input.value = '';
  }

  private async uploadAll(files: FileList | null | undefined): Promise<void> {
    if (!files?.length) {
      return;
    }

    this.uploading.set(true);
    this.uploadError.set(null);
    this.notice.set(null);

    // Sequential on purpose: each upload runs a model twice, and firing a folder's worth at a local
    // Ollama at once just makes them all slow.
    for (const file of Array.from(files)) {
      try {
        const result = await this.service.upload(file);
        this.notice.set(
          result.warning
            ? `${file.name}: ${result.warning}`
            : `Added ${file.name} - ${result.chunksIndexed} passage(s) indexed.`,
        );
      } catch (err) {
        this.uploadError.set(`${file.name}: ${describe(err, 'Upload failed.')}`);
      }
    }

    this.uploading.set(false);
  }

  protected async remove(bill: Bill): Promise<void> {
    try {
      await this.service.remove(bill.id);
      this.notice.set(`Removed ${bill.fileName}.`);
    } catch (err) {
      this.uploadError.set(describe(err, 'Could not delete that bill.'));
    }
  }

  protected utilityClass(utility: string): string {
    return `pill pill-${utility.toLowerCase()}`;
  }
}
