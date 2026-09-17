import { TestBed } from '@angular/core/testing';
import { ChatService } from './chat.service';
import { ChatStreamEvent } from './models';

/** Builds a fetch Response whose body streams the given string pieces, one chunk per piece. */
function streamingResponse(pieces: string[], ok = true): Response {
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      const encoder = new TextEncoder();
      for (const piece of pieces) {
        controller.enqueue(encoder.encode(piece));
      }
      controller.close();
    },
  });

  return new Response(body, { status: ok ? 200 : 500 });
}

async function collect(service: ChatService): Promise<ChatStreamEvent[]> {
  const events: ChatStreamEvent[] = [];
  for await (const event of service.ask({ question: 'q' }, new AbortController().signal)) {
    events.push(event);
  }
  return events;
}

describe('ChatService', () => {
  let service: ChatService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(ChatService);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('parses one event per frame', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        streamingResponse([
          'data: {"type":"sources","citations":[]}\n\n',
          'data: {"type":"token","text":"Hello"}\n\n',
          'data: {"type":"done"}\n\n',
        ]),
      ),
    );

    const events = await collect(service);

    expect(events.map((e) => e.type)).toEqual(['sources', 'token', 'done']);
    expect(events[1].text).toBe('Hello');
  });

  it('reassembles a frame split across reads', async () => {
    // The network does not respect frame boundaries; half an event can arrive in one chunk.
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(streamingResponse(['data: {"type":"tok', 'en","text":"split"}\n\n'])),
    );

    const events = await collect(service);

    expect(events).toHaveLength(1);
    expect(events[0].text).toBe('split');
  });

  it('handles several frames arriving in one read', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        streamingResponse(['data: {"type":"token","text":"a"}\n\ndata: {"type":"token","text":"b"}\n\n']),
      ),
    );

    const events = await collect(service);

    expect(events.map((e) => e.text)).toEqual(['a', 'b']);
  });

  it('skips a malformed frame rather than dropping the stream', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        streamingResponse(['data: {not json}\n\n', 'data: {"type":"token","text":"survived"}\n\n']),
      ),
    );

    const events = await collect(service);

    expect(events).toHaveLength(1);
    expect(events[0].text).toBe('survived');
  });

  it('reports a failed request as an error event', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{"error":"Ask a question."}', { status: 400 })));

    const events = await collect(service);

    expect(events[0].type).toBe('error');
    expect(events[0].text).toBe('Ask a question.');
  });
});
