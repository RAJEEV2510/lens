import { Component, OnInit, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { LiveService } from './core/live.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header>
      <a class="brand" routerLink="/live"><span>Lens</span><small>ask your cameras</small></a>
      <nav>
        <a routerLink="/live" routerLinkActive="active">Live</a>
        <a routerLink="/search" routerLinkActive="active">Search</a>
        <a routerLink="/ask" routerLinkActive="active">Ask</a>
        <a routerLink="/cameras" routerLinkActive="active">Cameras</a>
        <a routerLink="/jobs" routerLinkActive="active">Videos</a>
      </nav>
      <span class="grow"></span>
      <span class="conn" [class.on]="live.connected()" [title]="live.connected() ? 'live feed connected' : 'live feed disconnected'"></span>
    </header>
    <main><router-outlet /></main>
  `,
  styles: `
    header { display: flex; align-items: center; gap: 24px; padding: 12px 24px; border-bottom: 1px solid var(--line); background: var(--panel); position: sticky; top: 0; z-index: 10; }
    .brand { display: flex; align-items: baseline; gap: 8px; color: var(--text); span { font-size: 20px; font-weight: 700; letter-spacing: .3px; } small { color: var(--muted); } }
    nav { display: flex; gap: 4px; a { padding: 6px 12px; border-radius: 6px; color: var(--muted); &.active { color: var(--text); background: var(--panel-2); } &:hover { color: var(--text); } } }
    .conn { width: 10px; height: 10px; border-radius: 50%; background: var(--bad); &.on { background: var(--ok); } }
    main { padding: 20px 24px; max-width: 1600px; margin: 0 auto; }
  `,
})
export class App implements OnInit {
  protected readonly live = inject(LiveService);
  ngOnInit(): void { this.live.start(); }
}
