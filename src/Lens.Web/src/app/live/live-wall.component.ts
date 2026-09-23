import { DatePipe } from '@angular/common';
import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { LiveService } from '../core/live.service';
import { LiveEvent, Source, colourFor } from '../core/models';
import { CameraTileComponent } from './camera-tile.component';

/** The camera wall: every enabled source as a tile, one can be expanded, and a merged detection feed on the side. */
@Component({
  selector: 'app-live-wall',
  imports: [CameraTileComponent, RouterLink, DatePipe],
  template: `
    <div class="bar">
      <h2>Live</h2>
      <span class="muted small">{{ sources().length }} camera(s) · {{ running() }} running · {{ playback() }}</span>
      <span class="grow"></span>
      <label class="small muted" title="Show the exact frames the detector processed, boxes drawn on the server. No overlay timing, at the detector's frame rate.">
        <input type="checkbox" [checked]="analytics()" (change)="setAnalytics($any($event.target).checked)"> Analytics view
      </label>
      <label class="small muted">Grid
        <select [value]="grid()" (change)="grid.set(+$any($event.target).value)">
          <option value="0">auto</option><option value="1">1</option><option value="2">2 × 2</option><option value="3">3 × 3</option><option value="4">4 × 4</option>
        </select>
      </label>
      @if (expanded() !== null) { <button class="ghost small" (click)="expanded.set(null)">back to grid</button> }
      <a class="btn ghost small" routerLink="/cameras">manage cameras</a>
    </div>

    @if (!sources().length) {
      <div class="panel">No cameras yet. <a routerLink="/cameras">Add an RTSP camera</a> and it appears here within seconds.</div>
    }

    <div class="layout" [class.has-expanded]="expanded() !== null">
      <div class="wall" [style.grid-template-columns]="columns()">
        @for (s of visible(); track s.id) {
          <app-camera-tile [source]="s" [webrtcBase]="webrtcBase()" [expanded]="expanded() === s.id" [analytics]="analytics()" (select)="toggle($event)" />
        }
      </div>
      @if (expanded() !== null) {
        <div class="side">
          <div class="panel thumbs">
            @for (s of others(); track s.id) {
              <app-camera-tile [source]="s" [webrtcBase]="webrtcBase()" [analytics]="analytics()" (select)="toggle($event)" />
            }
          </div>
        </div>
      }
      <div class="feed panel">
        <div class="row"><h2>Detections</h2><span class="muted small">last {{ feed().length }}</span></div>
        @for (e of feed(); track e.frameIndex + '-' + e.sourceId) {
          <div class="ev">
            <span class="t muted">{{ e.occurredAt | date:'HH:mm:ss' }}</span>
            <b>{{ e.name }}</b>
            <span>@for (c of summarise(e); track c[0]) { <span class="badge" [style.border-color]="colour(c[0])">{{ c[1] > 1 ? c[1] + ' ' : '' }}{{ c[0] }}</span> }</span>
          </div>
        }
      </div>
    </div>
  `,
  styles: `
    .bar { display: flex; gap: 14px; align-items: center; margin-bottom: 14px; label select { margin-left: 6px; } }
    .layout { display: grid; grid-template-columns: minmax(0, 1fr) 320px; gap: 14px; align-items: start; }
    .layout.has-expanded { grid-template-columns: minmax(0, 1fr) 260px 300px; }
    .wall { display: grid; gap: 12px; }
    .thumbs { display: flex; flex-direction: column; gap: 10px; padding: 10px; }
    .feed { max-height: calc(100vh - 140px); overflow: auto; }
    .ev { display: flex; gap: 8px; align-items: baseline; padding: 5px 0; border-top: 1px solid var(--line); font-size: 13px; flex-wrap: wrap; .t { min-width: 62px; font-variant-numeric: tabular-nums; } }
    @media (max-width: 1100px) { .layout, .layout.has-expanded { grid-template-columns: 1fr; } }
  `,
  providers: [],
})
export class LiveWallComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly live = inject(LiveService);
  private readonly destroyRef = inject(DestroyRef);

  readonly sources = signal<Source[]>([]);
  readonly webrtcBase = signal<string | null>(null);
  readonly grid = signal(0);
  readonly expanded = signal<number | null>(null);
  readonly feed = signal<LiveEvent[]>([]);
  readonly analytics = signal(false);

  readonly enabled = computed(() => this.sources().filter(s => s.enabled));
  readonly running = computed(() => this.sources().filter(s => s.status === 'running').length);
  readonly visible = computed(() => this.expanded() === null ? this.enabled() : this.enabled().filter(s => s.id === this.expanded()));
  readonly others = computed(() => this.enabled().filter(s => s.id !== this.expanded()));
  readonly playback = computed(() => this.webrtcBase() ? 'WebRTC via MediaMTX' : 'MJPEG fallback (MediaMTX not reachable)');
  readonly columns = computed(() => {
    if (this.expanded() !== null) return '1fr';
    const n = this.grid() || Math.ceil(Math.sqrt(Math.max(1, this.enabled().length)));
    return `repeat(${Math.min(4, n)}, minmax(0, 1fr))`;
  });

  colour = colourFor;

  ngOnInit(): void {
    try { this.analytics.set(localStorage.getItem('lens.analytics') === '1'); } catch { /* private mode */ }
    this.refresh();
    const timer = setInterval(() => this.refresh(), 3000);
    this.destroyRef.onDestroy(() => clearInterval(timer));
    this.live.events$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(e => this.feed.update(f => [e, ...f].slice(0, 60)));
  }

  setAnalytics(on: boolean): void {
    this.analytics.set(on);
    try { localStorage.setItem('lens.analytics', on ? '1' : '0'); } catch { /* ignore */ }
  }

  toggle(id: number): void { this.expanded.set(this.expanded() === id ? null : id); }

  summarise(e: LiveEvent): [string, number][] {
    const m = new Map<string, number>();
    for (const d of e.detections) m.set(d.className, (m.get(d.className) ?? 0) + 1);
    return [...m.entries()].sort((a, b) => b[1] - a[1]);
  }

  private refresh(): void {
    this.api.status().subscribe({
      next: st => {
        this.sources.set(st.sources);
        this.webrtcBase.set(st.mediamtx.available ? st.mediamtx.webrtcBaseUrl : null);
      },
      error: () => { /* keep last known state; the header dot shows connectivity */ },
    });
  }
}
