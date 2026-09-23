import { DatePipe } from '@angular/common';
import { Component, ElementRef, inject, input } from '@angular/core';
import { ApiService } from '../core/api.service';
import { DetectionHit, colourFor, fmtClock, fmtClockPrecise } from '../core/models';

/** Result cards: the archived or seeked frame for each hit with its box drawn on. Shared by Search and Ask. */
@Component({
  selector: 'app-hits-grid',
  imports: [DatePipe],
  template: `
    <div class="cards">
      @for (h of hits(); track h.videoId + '-' + h.className + '-' + h.timestampSeconds) {
        <div class="card">
          <canvas [attr.data-key]="key(h)" (click)="open(h)"></canvas>
          <img hidden [src]="api.frameUrl(h.videoId, h.bestSeconds ?? h.timestampSeconds)" (load)="draw($event, h)" (error)="failed($event)" alt="">
          <div class="cap">
            <span><b>{{ h.className }}</b> · {{ range(h) }}</span>
            <span>{{ h.videoName }} · {{ h.occurredAt | date:'HH:mm:ss' }}</span>
          </div>
        </div>
      }
    </div>
  `,
  styles: `.card canvas { cursor: zoom-in; }`,
})
export class HitsGridComponent {
  readonly hits = input.required<DetectionHit[]>();
  protected readonly api = inject(ApiService);
  private readonly host = inject(ElementRef<HTMLElement>);

  key(h: DetectionHit): string { return `${h.videoId}-${h.className}-${h.timestampSeconds}`; }

  range(h: DetectionHit): string {
    const at = h.bestSeconds ?? h.timestampSeconds;
    return h.endSeconds > h.timestampSeconds
      ? `${fmtClock(h.timestampSeconds)}–${fmtClock(h.endSeconds)} (${h.count} sightings, frame ${fmtClockPrecise(at)})`
      : fmtClockPrecise(at);
  }

  draw(ev: Event, h: DetectionHit): void {
    const img = ev.target as HTMLImageElement;
    const canvas = img.parentElement?.querySelector('canvas') as HTMLCanvasElement | null;
    if (!canvas) return;
    canvas.width = img.naturalWidth; canvas.height = img.naturalHeight;
    const ctx = canvas.getContext('2d')!;
    ctx.drawImage(img, 0, 0);
    const x = h.x1 * canvas.width, y = h.y1 * canvas.height, w = (h.x2 - h.x1) * canvas.width, hh = (h.y2 - h.y1) * canvas.height;
    ctx.lineWidth = Math.max(3, canvas.width / 300);
    ctx.strokeStyle = ctx.fillStyle = colourFor(h.className);
    ctx.strokeRect(x, y, w, hh);
    ctx.font = `bold ${Math.max(16, canvas.width / 45)}px sans-serif`;
    ctx.fillText(`${h.className} ${(h.confidence * 100).toFixed(0)}%`, x + 4, Math.max(20, y - 8));
  }

  failed(ev: Event): void {
    const img = ev.target as HTMLImageElement;
    const canvas = img.parentElement?.querySelector('canvas') as HTMLCanvasElement | null;
    if (!canvas) return;
    canvas.width = 640; canvas.height = 480;
    const ctx = canvas.getContext('2d')!;
    ctx.fillStyle = '#8b93a7'; ctx.font = '20px sans-serif'; ctx.textAlign = 'center';
    ctx.fillText('no frame available for this moment', 320, 240);
  }

  open(h: DetectionHit): void {
    window.open(this.api.frameUrl(h.videoId, h.bestSeconds ?? h.timestampSeconds), '_blank');
  }
}
