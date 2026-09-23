import { Component, DestroyRef, ElementRef, OnChanges, OnDestroy, OnInit, SimpleChanges, inject, input, output, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ApiService } from '../core/api.service';
import { LiveService } from '../core/live.service';
import { LiveEvent, Source, colourFor } from '../core/models';
import { WhepPlayer } from '../core/whep-player';

/**
 * One camera on the wall: live video (WebRTC through MediaMTX, or MJPEG from Lens as fallback) with detection boxes drawn
 * on a canvas over it. Boxes are delayed by the camera's overlay offset so they line up with the picture's latency.
 */
@Component({
  selector: 'app-camera-tile',
  template: `
    <div class="tile" [class.expanded]="expanded()" (click)="select.emit(source().id)">
      <div class="media" [style.aspect-ratio]="aspect()">
        @if (mode() === 'webrtc') {
          <video #video autoplay muted playsinline (loadedmetadata)="onMeta()"></video>
        } @else if (mode() === 'mjpeg') {
          <img #img [src]="mjpegSrc()" alt="" (load)="onMeta()">
        } @else {
          <div class="placeholder">{{ placeholder() }}</div>
        }
        <canvas #canvas></canvas>
        <div class="overlay-top">
          <span class="dot" [class]="'dot ' + source().status"></span>
          <b>{{ source().name }}</b>
          <span class="muted">{{ source().camera }}</span>
          <span class="grow"></span>
          <span class="muted small">{{ mode() === 'webrtc' ? 'WebRTC' : mode() === 'mjpeg' ? 'MJPEG' : '' }}</span>
        </div>
      </div>
      <div class="foot">
        @if (source().status === 'running') {
          <span class="small muted">{{ source().measuredFps }} fps · {{ source().inferenceMs }} ms</span>
        } @else {
          <span class="small" [class.error]="source().status === 'reconnecting'">{{ source().status }}{{ source().lastError ? ' · ' + source().lastError : '' }}</span>
        }
        <span class="grow"></span>
        @for (c of recent(); track c[0]) { <span class="badge" [style.border-color]="colour(c[0])">{{ c[1] }} {{ c[0] }}</span> }
      </div>
    </div>
  `,
  styles: `
    .tile { background: var(--panel); border: 1px solid var(--line); border-radius: 10px; overflow: hidden; cursor: pointer; }
    .tile.expanded { border-color: var(--accent); }
    .media { position: relative; background: #000; width: 100%; aspect-ratio: 16 / 9; }
    video, img, canvas { position: absolute; inset: 0; width: 100%; height: 100%; display: block; }
    video, img { object-fit: fill; }
    canvas { pointer-events: none; }
    .placeholder { position: absolute; inset: 0; display: grid; place-items: center; color: var(--muted); font-size: 13px; padding: 12px; text-align: center; }
    .overlay-top { position: absolute; top: 0; left: 0; right: 0; display: flex; gap: 8px; align-items: center; padding: 6px 10px; background: linear-gradient(rgba(0,0,0,.65), transparent); font-size: 13px; color: #fff; }
    .foot { display: flex; gap: 8px; align-items: center; padding: 6px 10px; min-height: 32px; flex-wrap: wrap; }
  `,
})
export class CameraTileComponent implements OnInit, OnChanges, OnDestroy {
  readonly source = input.required<Source>();
  readonly webrtcBase = input<string | null>(null);
  readonly expanded = input(false);
  readonly select = output<number>();

  private readonly api = inject(ApiService);
  private readonly live = inject(LiveService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly videoRef = viewChild<ElementRef<HTMLVideoElement>>('video');
  private readonly imgRef = viewChild<ElementRef<HTMLImageElement>>('img');
  private readonly canvasRef = viewChild<ElementRef<HTMLCanvasElement>>('canvas');

  readonly mode = signal<'webrtc' | 'mjpeg' | 'none'>('none');
  readonly placeholder = signal('connecting…');
  readonly aspect = signal('16 / 9');
  readonly mjpegSrc = signal('');
  readonly recent = signal<[string, number][]>([]);

  private player?: WhepPlayer;
  private events: LiveEvent[] = [];
  private raf = 0;
  private playedPath: string | null = null;
  private lastMode: string | null = null;

  ngOnInit(): void {
    this.live.events$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(ev => {
      if (ev.sourceId !== this.source().id) return;
      this.events.push(ev);
      const cutoff = Date.now() - 5000;
      while (this.events.length && this.events[0].receivedAt < cutoff) this.events.shift();
    });
    this.raf = requestAnimationFrame(() => this.draw());
    this.choosePlayer();
  }

  ngOnChanges(changes: SimpleChanges): void {
    const s = this.source();
    this.recent.set(Object.entries(s.recent ?? {}).sort((a, b) => b[1] - a[1]).slice(0, 5));
    if (changes['source'] || changes['webrtcBase']) this.choosePlayer();
  }

  ngOnDestroy(): void {
    cancelAnimationFrame(this.raf);
    this.player?.stop();
  }

  colour = colourFor;

  onMeta(): void {
    const v = this.videoRef()?.nativeElement;
    const i = this.imgRef()?.nativeElement;
    const w = v?.videoWidth || i?.naturalWidth || 16;
    const h = v?.videoHeight || i?.naturalHeight || 9;
    this.aspect.set(`${w} / ${h}`);
    const c = this.canvasRef()?.nativeElement;
    if (c) { c.width = w; c.height = h; }
  }

  private choosePlayer(): void {
    const s = this.source();
    if (!s.enabled || s.status === 'disabled') {
      this.setMode('none', 'camera disabled');
      return;
    }
    const base = this.webrtcBase();
    const wantWebRtc = !!base && !!s.webrtcPath;
    const path = wantWebRtc ? `${base}/${s.webrtcPath}/whep` : null;

    if (wantWebRtc && path) {
      if (this.mode() === 'webrtc' && this.playedPath === path) return;
      this.setMode('webrtc', '');
      this.playedPath = path;
      // The <video> element appears on the next change detection pass; retry until it exists.
      setTimeout(() => this.startWebRtc(path), 50);
      return;
    }
    if (s.status === 'running' || s.status === 'connecting') {
      if (this.mode() !== 'mjpeg') {
        this.player?.stop();
        this.mjpegSrc.set(this.api.mjpegUrl(s.id) + '?_=' + Date.now());
        this.setMode('mjpeg', '');
      }
      return;
    }
    this.setMode('none', s.status === 'reconnecting' ? 'stream down, reconnecting…' : 'waiting for stream…');
  }

  private async startWebRtc(path: string, attempt = 0): Promise<void> {
    const video = this.videoRef()?.nativeElement;
    if (!video) { if (attempt < 20) setTimeout(() => this.startWebRtc(path, attempt + 1), 100); return; }
    this.player?.stop();
    this.player = new WhepPlayer(video);
    try {
      await this.player.play(path);
    } catch {
      // MediaMTX may still be pulling the camera; fall back to MJPEG so the tile is never blank, retry WebRTC later.
      const s = this.source();
      this.mjpegSrc.set(this.api.mjpegUrl(s.id) + '?_=' + Date.now());
      this.setMode('mjpeg', '');
      this.playedPath = null;
      setTimeout(() => { if (this.mode() === 'mjpeg') this.choosePlayer(); }, 15000);
    }
  }

  private setMode(mode: 'webrtc' | 'mjpeg' | 'none', text: string): void {
    if (mode !== 'webrtc') { this.player?.stop(); this.playedPath = null; }
    if (mode !== 'mjpeg') this.mjpegSrc.set('');
    this.mode.set(mode);
    this.placeholder.set(text);
    this.lastMode = mode;
  }

  /** Draws the newest detection set whose delayed time has arrived, and clears when it goes stale. */
  private draw(): void {
    this.raf = requestAnimationFrame(() => this.draw());
    const canvas = this.canvasRef()?.nativeElement;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    const offset = this.source().overlayOffsetMs ?? 300;
    const now = Date.now();
    let show: LiveEvent | undefined;
    for (const e of this.events) if (e.receivedAt + offset <= now) show = e;
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    if (!show || now - (show.receivedAt + offset) > 1200) return;

    const W = canvas.width, H = canvas.height;
    ctx.lineWidth = Math.max(2, W / 400);
    ctx.font = `bold ${Math.max(12, W / 60)}px sans-serif`;
    for (const d of show.detections) {
      const x = d.x1 * W, y = d.y1 * H, w = (d.x2 - d.x1) * W, h = (d.y2 - d.y1) * H;
      ctx.strokeStyle = ctx.fillStyle = colourFor(d.className);
      ctx.strokeRect(x, y, w, h);
      ctx.fillText(`${d.className} ${(d.confidence * 100).toFixed(0)}%`, x + 3, Math.max(14, y - 5));
    }
  }
}
