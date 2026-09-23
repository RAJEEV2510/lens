import { Injectable, NgZone, inject, signal } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { LiveEvent } from './models';

/** One SignalR connection for the whole app. Components subscribe to events; the connection reconnects on its own. */
@Injectable({ providedIn: 'root' })
export class LiveService {
  private readonly zone = inject(NgZone);
  private connection?: signalR.HubConnection;

  readonly events$ = new Subject<LiveEvent>();
  readonly connected = signal(false);
  readonly lastBySource = new Map<number, LiveEvent>();

  start(): void {
    if (this.connection) return;
    const conn = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/live')
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    conn.on('detection', (raw: Omit<LiveEvent, 'receivedAt'>) => {
      const ev: LiveEvent = { ...raw, receivedAt: Date.now() };
      this.lastBySource.set(ev.sourceId, ev);
      // SignalR callbacks run outside Angular's zone; re-enter so signals and bindings update.
      this.zone.run(() => this.events$.next(ev));
    });
    conn.onreconnecting(() => this.zone.run(() => this.connected.set(false)));
    conn.onreconnected(() => this.zone.run(() => this.connected.set(true)));
    conn.onclose(() => this.zone.run(() => { this.connected.set(false); setTimeout(() => this.connect(conn), 5000); }));

    this.connection = conn;
    this.connect(conn);
  }

  private connect(conn: signalR.HubConnection): void {
    conn.start()
      .then(() => this.zone.run(() => this.connected.set(true)))
      .catch(() => setTimeout(() => this.connect(conn), 5000));
  }
}
