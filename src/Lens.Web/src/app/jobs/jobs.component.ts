import { DatePipe } from '@angular/common';
import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { IndexJob, VideoInfo } from '../core/models';

/** Recorded videos: upload files for indexing, watch job progress, and see what is in the library. */
@Component({
  selector: 'app-jobs',
  imports: [FormsModule, DatePipe],
  template: `
    <h2>Videos</h2>
    <form class="panel" (ngSubmit)="upload()">
      <div class="fgrid">
        <label class="field">Video file <input type="file" name="file" accept=".mp4,.mkv,.mov,.avi,.ts,.m4v,.webm" (change)="file = $any($event.target).files[0] ?? null" required></label>
        <label class="field">Camera label <input name="camera" [(ngModel)]="camera" placeholder="gate-2"></label>
        <label class="field">Footage starts at <input name="start" type="datetime-local" [(ngModel)]="startedAt"></label>
        <label class="field">Frames / second to analyse <input name="fps" type="number" min="0.5" max="10" step="0.5" [(ngModel)]="fps"></label>
        <button type="submit" [disabled]="busy() || !file">{{ busy() ? 'Uploading…' : 'Upload and index' }}</button>
      </div>
      @if (error()) { <p class="error small">{{ error() }}</p> }
    </form>

    @if (jobs().length) {
      <div class="panel" style="margin-top:14px">
        <h3 class="small muted">Indexing jobs</h3>
        @for (j of jobs(); track j.id) {
          <div class="job" [class]="'job ' + j.status">
            <span style="min-width:220px"><b>{{ j.fileName }}</b> · {{ j.camera }}</span>
            <span class="bar"><i [style.width.%]="j.percent"></i></span>
            <span style="min-width:260px" class="small">{{ j.status }} {{ j.percent.toFixed(0) }}% · {{ j.frames }} frames · {{ j.detections }} detections{{ j.framesPerSecond ? ' · ' + j.framesPerSecond + ' fps' : '' }}{{ j.error ? ' · ' + j.error : '' }}</span>
          </div>
        }
      </div>
    }

    <div class="panel" style="margin-top:14px">
      <h3 class="small muted">Library</h3>
      <table>
        <thead><tr><th>#</th><th>Name</th><th>Camera</th><th>Type</th><th>Size</th><th>Length</th><th>Starts</th><th>Detections</th></tr></thead>
        <tbody>
          @for (v of videos(); track v.id) {
            <tr><td>{{ v.id }}</td><td>{{ v.name }}</td><td>{{ v.camera }}</td><td>{{ v.isLive ? 'live' : 'file' }}</td><td>{{ v.width }}×{{ v.height }}</td>
                <td>{{ (v.durationSeconds / 60).toFixed(1) }} min</td><td>{{ v.startedAt | date:'d MMM HH:mm' }}</td><td>{{ v.detectionCount }}</td></tr>
          }
        </tbody>
      </table>
    </div>
  `,
  styles: `
    .job { display: flex; gap: 12px; align-items: center; padding: 6px 0; border-top: 1px solid var(--line); }
    .bar { flex: 1; height: 8px; background: var(--bg); border-radius: 4px; overflow: hidden; i { display: block; height: 100%; background: var(--accent); transition: width .4s; } }
    .job.done .bar i { background: var(--ok); } .job.failed .bar i { background: var(--bad); }
    table { width: 100%; border-collapse: collapse; font-size: 13px; th { text-align: left; color: var(--muted); font-weight: 500; padding: 4px 8px; } td { padding: 6px 8px; border-top: 1px solid var(--line); } }
  `,
})
export class JobsComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);
  readonly jobs = signal<IndexJob[]>([]);
  readonly videos = signal<VideoInfo[]>([]);
  readonly busy = signal(false);
  readonly error = signal('');
  file: File | null = null;
  camera = '';
  startedAt = '';
  fps = 2;

  ngOnInit(): void {
    this.refresh();
    const t = setInterval(() => this.refresh(), 2000);
    this.destroyRef.onDestroy(() => clearInterval(t));
  }

  refresh(): void {
    this.api.jobs().subscribe({ next: j => this.jobs.set(j), error: () => {} });
    this.api.videos().subscribe({ next: v => this.videos.set(v), error: () => {} });
  }

  upload(): void {
    if (!this.file) return;
    this.busy.set(true); this.error.set('');
    this.api.upload(this.file, this.camera, this.startedAt ? new Date(this.startedAt).toISOString() : undefined, this.fps).subscribe({
      next: () => { this.busy.set(false); this.file = null; this.refresh(); },
      error: e => { this.busy.set(false); this.error.set(e?.error?.error ?? 'upload failed'); },
    });
  }
}
