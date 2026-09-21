import { Injectable } from '@angular/core';
import { ChatStreamEvent } from './models';

/** One completed exchange, sent back so the server can resolve a follow-up against it. */
export interface ChatTurn {
  question: string;
  answer: string;
}

export interface AskOptions {
  question: string;
  utility?: string | null;
  /** Earlier exchanges, oldest first. The API is stateless, so the client carries the conversation. */
  history?: ChatTurn[];
}

/**
 * Streams answers from the API.
 *
 * Uses fetch rather than HttpClient because this endpoint is server-sent events: the answer has to
 * render token by token, and HttpClient would only hand it over once the whole response had arrived.
 */
@Injectable({ providedIn: 'root' })
export class ChatService {
  async *ask(options: AskOptions, signal: AbortSignal): AsyncGenerator<ChatStreamEvent> {
    const response = await fetch('/api/chat/stream', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        question: options.question,
        utility: options.utility || null,
        history: options.history ?? [],
      }),
      signal,
    });

    if (!response.ok || !response.body) {
      const message = await response.text().catch(() => '');
      yield { type: 'error', text: safeError(message) };
      return;
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';

    while (true) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }

      buffer += decoder.decode(value, { stream: true });

      // SSE frames are separated by a blank line; a frame may arrive split across reads.
      let separator = buffer.indexOf('\n\n');
      while (separator !== -1) {
        const frame = buffer.slice(0, separator).trim();
        buffer = buffer.slice(separator + 2);
        separator = buffer.indexOf('\n\n');

        if (frame.startsWith('data:')) {
          try {
            yield JSON.parse(frame.slice(5).trim()) as ChatStreamEvent;
          } catch {
            // A malformed frame is not worth killing the stream over.
          }
        }
      }
    }
  }
}

function safeError(body: string): string {
  try {
    return JSON.parse(body).error ?? 'The assistant is unavailable.';
  } catch {
    return 'The assistant is unavailable.';
  }
}
