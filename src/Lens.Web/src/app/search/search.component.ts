import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { DetectionHit, SearchParams, VideoInfo, fmtClock } from '../core/models';
import { HitsGridComponent } from './hits-grid.component';

const CLASSES = ['person', 'face', 'car', 'truck', 'bus', 'motorcycle', 'bicycle', 'dog', 'cat', 'backpack', 'handbag', 'suitcase', 'umbrella'];

/** Filter search across every indexed video and live camera. No model involved; this is the honest view of what was detected. */
@Component({
  selector: 'app-search',
  imports: [FormsModule, HitsGridComponent],
  template: `
    <h2>Search</h2>
    <form class="panel" (ngSubmit)="run()">
      <div class="fgrid">
        <label class="field">Object
          <select name="cls" [(ngModel)]="q.classes"><option value="">any</option>@for (c of classes; track c) { <option [value]="c">{{ c }}</option> }</select>
        </label>
        <label class="field">Video / camera
          <select name="video" [(ngModel)]="q.videoId"><option [ngValue]="undefined">all</option>@for (v of videos(); track v.id) { <option [ngValue]="v.id">{{ v.name }} ({{ v.camera }}{{ v.isLive ? ', live' : '' }})</option> }</select>
        </label>
        <label class="field">From (seconds into video) <input name="from" type="number" min="0" [(ngModel)]="q.fromSeconds" placeholder="0"></label>
        <label class="field">To (seconds) <input name="to" type="number" min="0" [(ngModel)]="q.toSeconds" placeholder="end"></label>
        <label class="field">After (clock) <input name="fromT" type="datetime-local" [(ngModel)]="fromTime"></label>
        <label class="field">Before (clock) <input name="toT" type="datetime-local" [(ngModel)]="toTime"></label>
        <label class="field">Min confidence <input name="conf" type="number" min="0" max="1" step="0.05" [(ngModel)]="q.minConfidence"></label>
        <label class="field">Result type
          <select name="mode" [(ngModel)]="q.group"><option [ngValue]="2">events (merge within 2 s)</option><option [ngValue]="0">raw sightings (per frame)</option></select>
        </label>
        <button type="submit" [disabled]="busy()">Search</button>
      </div>
    </form>

    @if (summary()) {
      <div class="panel" style="margin-top:14px"><pre>{{ summary() }}</pre><div class="muted small" style="margin-top:6px">{{ meta() }}</div></div>
    }
    <div style="margin-top:14px"><app-hits-grid [hits]="hits()" /></div>
  `,
})
export class SearchComponent implements OnInit {
  private readonly api = inject(ApiService);
  readonly classes = CLASSES;
  readonly videos = signal<VideoInfo[]>([]);
  readonly hits = signal<DetectionHit[]>([]);
  readonly summary = signal('');
  readonly meta = signal('');
  readonly busy = signal(false);

  q: SearchParams = { classes: '', minConfidence: 0.35, group: 2, limit: 48 };
  fromTime = '';
  toTime = '';

  ngOnInit(): void { this.api.videos().subscribe(v => this.videos.set(v)); }

  run(): void {
    this.busy.set(true);
    const t0 = performance.now();
    const p: SearchParams = { ...this.q, from: this.fromTime ? new Date(this.fromTime).toISOString() : undefined, to: this.toTime ? new Date(this.toTime).toISOString() : undefined };
    this.api.search(p).subscribe({
      next: hits => {
        this.busy.set(false);
        this.hits.set(hits);
        const what = this.q.classes || 'any object';
        this.summary.set(hits.length
          ? `${hits.length} ${what} ${this.q.group ? 'event' : 'sighting'}(s)${hits.length >= (this.q.limit ?? 48) ? ' (showing first ' + hits.length + ')' : ''}:\n` +
            hits.slice(0, 10).map(h => `• ${h.videoName} · ${h.camera} · ${fmtClock(h.timestampSeconds)}${h.endSeconds > h.timestampSeconds ? '–' + fmtClock(h.endSeconds) : ''} · ${h.count} sighting(s) · ${(h.confidence * 100).toFixed(0)}%`).join('\n')
          : `No ${what} found with those filters.`);
        this.meta.set(`filter search · ${(performance.now() - t0).toFixed(0)} ms · no model involved`);
      },
      error: () => { this.busy.set(false); this.summary.set('Search failed.'); },
    });
  }
}
