import { DecimalPipe } from '@angular/common';
import { Component, DestroyRef, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { BillsService } from '../core/bills.service';
import { ChatService } from '../core/chat.service';
import { Citation, Totals } from '../core/models';

interface Turn {
  /** Stable identity: the turn object is replaced on every streamed token, so it cannot be its own key. */
  id: number;
  question: string;
  answer: string;
  citations: Citation[];
  totals: Totals | null;
  streaming: boolean;
  error: string | null;
}

@Component({
  selector: 'app-chat-page',
  imports: [FormsModule, DecimalPipe],
  templateUrl: './chat-page.html',
  styleUrl: './chat-page.css',
})
export class ChatPage {
  private readonly chat = inject(ChatService);
  private readonly bills = inject(BillsService);
  private readonly router = inject(Router);

  protected readonly question = signal('');
  protected readonly utility = signal<string>('');
  protected readonly turns = signal<Turn[]>([]);
  protected readonly busy = signal(false);

  protected readonly billList = this.bills.bills;

  protected readonly suggestions = [
    'How much did I pay for electricity in July?',
    'Is my water usage going up?',
    'How much did I spend on utilities altogether in 2025?',
    'When is my gas bill due?',
  ];

  private controller: AbortController | null = null;
  private nextTurnId = 0;

  constructor() {
    void this.bills.refresh();

    // Abandon an in-flight answer if the user navigates away mid-stream.
    inject(DestroyRef).onDestroy(() => this.controller?.abort());
  }

  protected use(suggestion: string): void {
    this.question.set(suggestion);
    void this.send();
  }

  protected async send(): Promise<void> {
    const question = this.question().trim();
    if (!question || this.busy()) {
      return;
    }

    this.question.set('');
    this.busy.set(true);
    this.controller = new AbortController();

    const id = this.nextTurnId++;
    this.turns.update((t) => [...t, { id, question, answer: '', citations: [], totals: null, streaming: true, error: null }]);

    try {
      for await (const event of this.chat.ask({ question, utility: this.utility() || null }, this.controller.signal)) {
        this.patch(id, (current) => {
          switch (event.type) {
            case 'sources':
              return { ...current, citations: event.citations ?? [], totals: event.totals ?? null };
            case 'token':
              return { ...current, answer: current.answer + (event.text ?? '') };
            case 'error':
              return { ...current, error: event.text ?? 'Something went wrong.', streaming: false };
            case 'done':
              return { ...current, streaming: false };
          }
        });
      }
    } catch (err) {
      if (!this.controller.signal.aborted) {
        this.patch(id, (c) => ({ ...c, error: 'Lost contact with the assistant.', streaming: false }));
      }
    } finally {
      this.patch(id, (c) => ({ ...c, streaming: false }));
      this.busy.set(false);
      this.controller = null;
    }
  }

  protected stop(): void {
    this.controller?.abort();
  }

  /** Replaces one turn by id, producing a new array so the signal notifies. */
  private patch(id: number, update: (current: Turn) => Turn): void {
    this.turns.update((turns) => turns.map((turn) => (turn.id === id ? update(turn) : turn)));
  }

  protected openBill(citation: Citation): void {
    void this.router.navigate(['/bills'], { fragment: citation.billId });
  }
}
