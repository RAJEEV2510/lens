import { JsonPipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { AskResult, ProviderStatus } from '../core/models';
import { HitsGridComponent } from '../search/hits-grid.component';

/**
 * The plain-English question box. Most questions are answered straight from the detection store with no model at all;
 * the rest go to a local open model through Ollama, and to Claude only if a key is configured. The badge says which.
 */
@Component({
  selector: 'app-ask',
  imports: [FormsModule, HitsGridComponent, JsonPipe],
  template: `
    <h2>Ask</h2>
    <form class="row" (ngSubmit)="ask()">
      <input class="grow" name="q" [(ngModel)]="question" placeholder="e.g. when was the first bus seen at the junction?" autocomplete="off" autofocus>
      <button type="submit" [disabled]="busy() || !question.trim()">Ask</button>
    </form>
    <p class="muted small">Try:
      @for (s of suggestions; track s) { <a (click)="question = s; ask()">{{ s }}</a>&nbsp;&nbsp; }
    </p>

    <div class="panel" style="min-height:52px"><pre>{{ answer() }}</pre>
      @if (result(); as r) {
        <div class="muted small" style="margin-top:8px">
          <span class="badge" [class.local]="r.provider === 'local'">{{ providerLabel(r.provider) }}</span>
          · {{ r.ms.toFixed(0) }} ms · {{ r.toolCalls.length }} store call(s)
          @if (r.inputTokens || r.outputTokens) { · {{ r.inputTokens }} in / {{ r.outputTokens }} out tokens }
          · {{ r.stopReason }}
        </div>
      }
    </div>

    @if (result(); as r) {
      @if (r.toolCalls.length) {
        <details class="panel small" style="margin-top:10px"><summary class="muted">How the answer was reached</summary>
          @for (t of r.toolCalls; track $index) { <div class="tc"><code>{{ t.tool }}</code>({{ t.input | json }}) → {{ t.resultChars }} rows/chars in {{ t.ms.toFixed(0) }} ms</div> }
        </details>
      }
      <div style="margin-top:14px"><app-hits-grid [hits]="r.hits.slice(0, 12)" /></div>
    }

    @if (providers(); as p) {
      <p class="muted small" style="margin-top:18px">
        Answer order: <b>database first</b> (no model)
        → local model <b>{{ p.ollama.model }}</b> {{ p.ollama.available ? '✓ ready' : '✗ ' + (p.ollama.reason ?? 'unavailable') }}
        → Claude {{ p.claude.configured ? '✓ ' + p.claude.model : '✗ no key set (optional)' }}
        @if (p.mode !== 'auto') { · forced mode: <b>{{ p.mode }}</b> }
      </p>
    }
  `,
  styles: `
    .tc { padding: 4px 0; border-top: 1px solid var(--line); word-break: break-all; code { color: var(--text); } }
    details summary { cursor: pointer; }
    .badge { display: inline-block; padding: 1px 8px; border-radius: 10px; border: 1px solid var(--line); color: var(--text); }
    .badge.local { border-color: var(--ok, #3a8); color: var(--ok, #3a8); }
  `,
})
export class AskComponent implements OnInit {
  private readonly api = inject(ApiService);
  readonly suggestions = ['what footage do you have?', 'how many trucks passed in the first 30 seconds?', 'when was the first bus seen?', 'show me people after 6pm'];
  readonly answer = signal('Ask a question about the indexed footage and live cameras.');
  readonly result = signal<AskResult | null>(null);
  readonly providers = signal<ProviderStatus | null>(null);
  readonly busy = signal(false);
  question = '';

  ngOnInit(): void {
    this.api.askProviders().subscribe({ next: p => this.providers.set(p), error: () => {} });
  }

  providerLabel(p: string): string {
    if (p === 'local') return 'answered from the database, no model';
    if (p.startsWith('ollama:')) return 'local model ' + p.slice(7);
    if (p.startsWith('claude:')) return 'Claude ' + p.slice(7);
    return p;
  }

  ask(): void {
    const q = this.question.trim();
    if (!q) return;
    this.busy.set(true);
    this.answer.set('Looking…');
    this.result.set(null);
    this.api.ask(q).subscribe({
      next: r => { this.busy.set(false); this.answer.set(r.answer); this.result.set(r); },
      error: e => { this.busy.set(false); this.answer.set(e?.error?.error ?? 'The request failed.'); },
    });
  }
}
