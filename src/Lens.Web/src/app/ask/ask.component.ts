import { JsonPipe } from '@angular/common';
import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { AskMode, AskResult, ProviderStatus, RagStatus } from '../core/models';
import { HitsGridComponent } from '../search/hits-grid.component';

/**
 * The plain-English question box. Default mode answers from the detection store with no model where it can, then a local
 * open model through Ollama, then Claude if a key is set. The optional RAG mode embeds the question, retrieves the closest
 * event descriptions from a vector index, and has the local model answer from those. The badge says which path answered.
 */
@Component({
  selector: 'app-ask',
  imports: [FormsModule, HitsGridComponent, JsonPipe],
  template: `
    <h2>Ask</h2>
    <form class="row" (ngSubmit)="ask()">
      <input class="grow" name="q" [(ngModel)]="question" placeholder="e.g. when was the first bus seen at the junction?" autocomplete="off" autofocus>
      <select name="mode" [(ngModel)]="mode" (ngModelChange)="saveMode()" title="How to answer">
        <option value="auto">Database first (default)</option>
        <option value="rag">RAG (embeddings)</option>
      </select>
      <button type="submit" [disabled]="busy() || !question.trim()">Ask</button>
    </form>
    <p class="muted small">Try:
      @for (s of suggestions; track s) { <a (click)="question = s; ask()">{{ s }}</a>&nbsp;&nbsp; }
    </p>

    <div class="panel" style="min-height:52px"><pre>{{ answer() }}</pre>
      @if (result(); as r) {
        <div class="muted small" style="margin-top:8px">
          <span class="badge" [class.local]="r.provider === 'local'" [class.rag]="r.provider.startsWith('rag:')">{{ providerLabel(r.provider) }}</span>
          · {{ (r.ms / 1000).toFixed(1) }} s · {{ r.toolCalls.length }} step(s)
          @if (r.inputTokens || r.outputTokens) { · {{ r.inputTokens }} in / {{ r.outputTokens }} out tokens }
          · {{ r.stopReason }}
        </div>
      }
    </div>

    @if (result(); as r) {
      @if (r.toolCalls.length) {
        <details class="panel small" style="margin-top:10px"><summary class="muted">How the answer was reached</summary>
          @for (t of r.toolCalls; track $index) { <div class="tc"><code>{{ t.tool }}</code>({{ t.input | json }}) → {{ t.resultChars }} in {{ t.ms.toFixed(0) }} ms</div> }
        </details>
      }
      <div style="margin-top:14px"><app-hits-grid [hits]="r.hits.slice(0, 12)" /></div>
    }

    @if (providers(); as p) {
      <div class="muted small" style="margin-top:18px">
        @if (mode === 'rag') {
          <div>
            <b>RAG mode.</b> Question and events are embedded with <b>{{ p.rag.embedModel }}</b>
            {{ p.rag.available ? '✓' : '✗ ' + (p.rag.reason ?? 'unavailable') }}; the answer is written by {{ p.ollama.model }}.
            Retrieval is by similarity, so it suits fuzzy questions; for exact counts and time windows use the default mode.
          </div>
          <div style="margin-top:6px" class="row">
            @if (rag(); as s) {
              <span>Index: <b>{{ s.indexed }}</b> events
                @if (s.progress.running) { · building, video {{ s.progress.videosDone }}/{{ s.progress.videos }}, {{ s.progress.events }} events seen }
                @else if (s.progress.error) { · <span style="color:var(--bad,#c55)">failed: {{ s.progress.error }}</span> }
                @else if (s.progress.finishedAt) { · up to date }
              </span>
            }
            <button type="button" (click)="buildIndex(false)" [disabled]="indexing() || !p.rag.available">Build index</button>
            <button type="button" (click)="buildIndex(true)" [disabled]="indexing() || !p.rag.available">Rebuild</button>
          </div>
        } @else {
          Answer order: <b>database first</b> (no model)
          → local model <b>{{ p.ollama.model }}</b> {{ p.ollama.available ? '✓ ready' : '✗ ' + (p.ollama.reason ?? 'unavailable') }}
          → Claude {{ p.claude.configured ? '✓ ' + p.claude.model : '✗ no key set (optional)' }}
          @if (p.mode !== 'auto') { · forced mode: <b>{{ p.mode }}</b> }
        }
      </div>
    }
  `,
  styles: `
    .tc { padding: 4px 0; border-top: 1px solid var(--line); word-break: break-all; code { color: var(--text); } }
    details summary { cursor: pointer; }
    .badge { display: inline-block; padding: 1px 8px; border-radius: 10px; border: 1px solid var(--line); color: var(--text); }
    .badge.local { border-color: var(--ok, #3a8); color: var(--ok, #3a8); }
    .badge.rag { border-color: var(--accent, #58a); color: var(--accent, #58a); }
    select { background: var(--panel); color: var(--text); border: 1px solid var(--line); border-radius: 6px; padding: 6px 8px; }
  `,
})
export class AskComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private poll: ReturnType<typeof setInterval> | null = null;
  readonly suggestions = ['what footage do you have?', 'how many trucks passed in the first 30 seconds?', 'when was the first bus seen?', 'show me people after 6pm'];
  readonly answer = signal('Ask a question about the indexed footage and live cameras.');
  readonly result = signal<AskResult | null>(null);
  readonly providers = signal<ProviderStatus | null>(null);
  readonly rag = signal<RagStatus | null>(null);
  readonly busy = signal(false);
  readonly indexing = signal(false);
  question = '';
  mode: AskMode = 'auto';

  ngOnInit(): void {
    try { const m = localStorage.getItem('lens.askMode'); if (m === 'rag' || m === 'auto') this.mode = m; } catch { /* private mode */ }
    this.api.askProviders().subscribe({ next: p => { this.providers.set(p); this.rag.set(p.rag); if (p.rag.progress.running) this.startPolling(); }, error: () => {} });
  }

  ngOnDestroy(): void { this.stopPolling(); }

  saveMode(): void { try { localStorage.setItem('lens.askMode', this.mode); } catch { /* ignore */ } }

  providerLabel(p: string): string {
    if (p === 'local') return 'answered from the database, no model';
    if (p.startsWith('ollama:')) return 'local model ' + p.slice(7);
    if (p.startsWith('claude:')) return 'Claude ' + p.slice(7);
    if (p.startsWith('rag:')) return 'RAG · ' + p.slice(4);
    return p;
  }

  buildIndex(rebuild: boolean): void {
    this.indexing.set(true);
    this.api.ragIndex(rebuild).subscribe({
      next: () => this.startPolling(),
      error: e => { this.indexing.set(false); this.answer.set(e?.error?.error ?? 'Could not start the index build.'); },
    });
  }

  private startPolling(): void {
    this.indexing.set(true);
    this.stopPolling();
    this.poll = setInterval(() => this.api.ragStatus().subscribe({
      next: s => { this.rag.set(s); if (!s.progress.running) { this.indexing.set(false); this.stopPolling(); } },
      error: () => {},
    }), 1500);
  }

  private stopPolling(): void { if (this.poll) { clearInterval(this.poll); this.poll = null; } }

  ask(): void {
    const q = this.question.trim();
    if (!q) return;
    this.busy.set(true);
    this.answer.set(this.mode === 'rag' ? 'Retrieving and writing…' : 'Looking…');
    this.result.set(null);
    this.api.ask(q, undefined, this.mode).subscribe({
      next: r => { this.busy.set(false); this.answer.set(r.answer); this.result.set(r); },
      error: e => { this.busy.set(false); this.answer.set(e?.error?.error ?? 'The request failed.'); },
    });
  }
}
