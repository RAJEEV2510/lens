import { DatePipe } from '@angular/common';
import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { Source, SourceRequest } from '../core/models';

/** Add, edit, pause and remove camera sources. Edits that change the stream restart it; the overlay offset applies instantly. */
@Component({
  selector: 'app-cameras',
  imports: [FormsModule, DatePipe, RouterLink],
  template: `
    <div class="row" style="margin-bottom:14px"><h2>Cameras</h2><span class="grow"></span><a class="btn ghost small" routerLink="/live">open live wall</a></div>

    <form class="panel" (ngSubmit)="save()" #f="ngForm">
      <h3 class="small muted">{{ editing() ? 'Edit camera #' + editing() : 'Add a camera' }}</h3>
      <div class="fgrid">
        <label class="field">Stream URL <input name="url" [(ngModel)]="form.url" placeholder="rtsp://user:pass@192.168.1.10:554/stream1" required></label>
        <label class="field">Name <input name="name" [(ngModel)]="form.name" placeholder="front gate"></label>
        <label class="field">Camera label <input name="camera" [(ngModel)]="form.camera" placeholder="gate-1"></label>
        <label class="field">Detection sub-stream (optional) <input name="detectUrl" [(ngModel)]="form.detectUrl" placeholder="rtsp://…/stream2 (lower resolution)"></label>
        <label class="field">Frames / second analysed <input name="fps" type="number" min="0.5" max="10" step="0.5" [(ngModel)]="form.sampleFps"></label>
        <label class="field">Min confidence <input name="conf" type="number" min="0.05" max="0.95" step="0.05" [(ngModel)]="form.confidence"></label>
        <label class="field">Overlay offset (ms) <input name="offset" type="number" min="-2000" max="5000" step="50" [(ngModel)]="form.overlayOffsetMs"></label>
        <div class="row">
          <button type="submit" [disabled]="busy() || !form.url">{{ editing() ? 'Save' : 'Start watching' }}</button>
          @if (editing()) { <button type="button" class="ghost" (click)="cancel()">Cancel</button> }
        </div>
      </div>
      @if (error()) { <p class="error small">{{ error() }}</p> }
      <p class="muted small" style="margin:10px 0 0">A local file path also works: it is played at real-time speed and looped, so you can test without a camera.
        The overlay offset shifts live boxes to match playback latency; raise it if boxes lead the picture, lower it if they lag.</p>
    </form>

    <div class="panel" style="margin-top:14px">
      @if (!sources().length) { <div class="muted">No cameras configured.</div> }
      @for (s of sources(); track s.id) {
        <div class="src">
          <span class="dot" [class]="'dot ' + s.status"></span>
          <div class="grow">
            <div><b>{{ s.name }}</b> <span class="muted">· {{ s.camera }} · #{{ s.id }}{{ s.simulate ? ' · simulated file' : '' }}</span></div>
            <div class="small muted mono">{{ s.url }}@if (s.detectUrl) { <span> · detect: {{ s.detectUrl }}</span> }</div>
            <div class="small">
              <span [class.error]="s.status === 'reconnecting'">{{ s.status }}</span>
              @if (s.status === 'running') { <span class="muted"> · {{ s.measuredFps }} fps · {{ s.inferenceMs }} ms/frame · {{ s.detections }} detections since {{ s.connectedAt | date:'HH:mm:ss' }}</span> }
              @if (s.lastError && s.status !== 'running') { <span class="muted"> · {{ s.lastError }}</span> }
              @if (s.nextRetryAt && s.status === 'reconnecting') { <span class="muted"> · retry {{ s.nextRetryAt | date:'HH:mm:ss' }}</span> }
            </div>
          </div>
          <button class="ghost small" (click)="edit(s)">edit</button>
          <button class="ghost small" (click)="toggle(s)">{{ s.enabled ? 'pause' : 'resume' }}</button>
          @if (confirming() === s.id) {
            <button class="danger small" (click)="remove(s)">confirm remove</button>
            <button class="ghost small" (click)="confirming.set(null)">keep</button>
          } @else {
            <button class="danger small" (click)="confirming.set(s.id)">remove</button>
          }
        </div>
      }
    </div>
  `,
  styles: `
    .src { display: flex; gap: 12px; align-items: center; padding: 10px 0; border-top: 1px solid var(--line); &:first-child { border-top: 0; } }
    .mono { font-family: ui-monospace, Consolas, monospace; word-break: break-all; }
  `,
})
export class CamerasComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly sources = signal<Source[]>([]);
  readonly editing = signal<number | null>(null);
  readonly busy = signal(false);
  readonly error = signal('');

  form: SourceRequest = this.blank();

  ngOnInit(): void {
    this.refresh();
    const t = setInterval(() => this.refresh(), 3000);
    this.destroyRef.onDestroy(() => clearInterval(t));
  }

  refresh(): void { this.api.sources().subscribe({ next: s => this.sources.set(s), error: () => {} }); }

  save(): void {
    this.busy.set(true); this.error.set('');
    const req: SourceRequest = { ...this.form, detectUrl: this.form.detectUrl || null };
    const call = this.editing() ? this.api.updateSource(this.editing()!, req) : this.api.addSource(req);
    call.subscribe({
      next: () => { this.busy.set(false); this.cancel(); this.refresh(); },
      error: e => { this.busy.set(false); this.error.set(e?.error?.error ?? 'request failed'); },
    });
  }

  edit(s: Source): void {
    this.editing.set(s.id);
    this.form = { url: s.url, name: s.name, camera: s.camera, detectUrl: s.detectUrl ?? '', sampleFps: s.sampleFps, confidence: s.confidence, overlayOffsetMs: s.overlayOffsetMs };
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  cancel(): void { this.editing.set(null); this.form = this.blank(); }

  toggle(s: Source): void { this.api.enableSource(s.id, !s.enabled).subscribe(() => this.refresh()); }

  readonly confirming = signal<number | null>(null);

  remove(s: Source): void {
    this.confirming.set(null);
    this.api.deleteSource(s.id).subscribe(() => this.refresh());
  }

  private blank(): SourceRequest { return { url: '', name: '', camera: '', detectUrl: '', sampleFps: 2, confidence: 0.35, overlayOffsetMs: 300 }; }
}
