import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { AskResult, ProviderStatus, RagStatus, ClassCount, DetectionHit, IndexJob, SearchParams, Source, SourceRequest, Status, VideoInfo } from './models';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  videos(): Observable<VideoInfo[]> { return this.http.get<VideoInfo[]>('/api/videos'); }

  search(p: SearchParams): Observable<DetectionHit[]> {
    let params = new HttpParams();
    for (const [k, v] of Object.entries(p)) {
      if (v !== undefined && v !== null && v !== '') params = params.set(k, String(v));
    }
    return this.http.get<DetectionHit[]>('/api/detections/search', { params });
  }

  counts(videoId?: number, classes?: string): Observable<ClassCount[]> {
    let params = new HttpParams();
    if (videoId) params = params.set('videoId', videoId);
    if (classes) params = params.set('classes', classes);
    return this.http.get<ClassCount[]>('/api/detections/counts', { params });
  }

  ask(question: string, videoId?: number, mode?: string): Observable<AskResult> {
    return this.http.post<AskResult>('/api/ask', { question, videoId, mode });
  }

  ragStatus(): Observable<RagStatus> { return this.http.get<RagStatus>('/api/rag/status'); }
  ragIndex(rebuild = false): Observable<unknown> { return this.http.post(`/api/rag/index?rebuild=${rebuild}`, {}); }

  askProviders(): Observable<ProviderStatus> { return this.http.get<ProviderStatus>('/api/ask/providers'); }

  frameUrl(videoId: number, t: number, cacheKey?: number | string): string {
    return `/api/frame?videoId=${videoId}&t=${t}${cacheKey !== undefined ? `&_=${cacheKey}` : ''}`;
  }

  sources(): Observable<Source[]> { return this.http.get<Source[]>('/api/sources'); }
  addSource(req: SourceRequest): Observable<Source> { return this.http.post<Source>('/api/sources', req); }
  updateSource(id: number, req: SourceRequest): Observable<Source> { return this.http.put<Source>(`/api/sources/${id}`, req); }
  enableSource(id: number, enabled: boolean): Observable<void> { return this.http.post<void>(`/api/sources/${id}/${enabled ? 'enable' : 'disable'}`, {}); }
  deleteSource(id: number): Observable<void> { return this.http.delete<void>(`/api/sources/${id}`); }
  mjpegUrl(id: number): string { return `/api/sources/${id}/mjpeg`; }

  status(): Observable<Status> { return this.http.get<Status>('/api/status'); }

  jobs(): Observable<IndexJob[]> { return this.http.get<IndexJob[]>('/api/jobs'); }
  upload(file: File, camera: string, startedAt?: string, fps?: number): Observable<IndexJob> {
    const fd = new FormData();
    fd.append('file', file);
    fd.append('camera', camera || 'default');
    if (startedAt) fd.append('startedAt', startedAt);
    if (fps) fd.append('fps', String(fps));
    return this.http.post<IndexJob>('/api/videos/upload', fd);
  }
}
