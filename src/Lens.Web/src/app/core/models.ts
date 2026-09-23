export interface VideoInfo {
  id: number;
  path: string;
  name: string;
  camera: string;
  width: number;
  height: number;
  fps: number;
  durationSeconds: number;
  startedAt: string;
  indexedAt: string;
  detectionCount: number;
  isLive: boolean;
}

export interface DetectionHit {
  videoId: number;
  videoName: string;
  camera: string;
  timestampSeconds: number;
  endSeconds: number;
  bestSeconds: number;
  occurredAt: string;
  className: string;
  confidence: number;
  x1: number; y1: number; x2: number; y2: number;
  count: number;
}

export interface ClassCount { className: string; count: number; firstSeconds: number; lastSeconds: number; }

export interface SearchParams {
  videoId?: number;
  camera?: string;
  classes?: string;
  fromSeconds?: number;
  toSeconds?: number;
  from?: string;
  to?: string;
  minConfidence?: number;
  minArea?: number;
  limit?: number;
  group?: number;
}

export interface ToolCallTrace { tool: string; input: unknown; resultChars: number; ms: number; }

export interface AskResult {
  answer: string;
  hits: DetectionHit[];
  toolCalls: ToolCallTrace[];
  stopReason: string;
  inputTokens: number;
  outputTokens: number;
  /** "local" (no model), "ollama:<model>" or "claude:<model>". */
  provider: string;
  ms: number;
}

export interface ProviderStatus {
  mode: string;
  local: boolean;
  ollama: { url: string; model: string; available: boolean; reason?: string | null };
  claude: { model: string; configured: boolean };
}

export type SourceStatus = 'connecting' | 'running' | 'reconnecting' | 'disabled' | 'stopped';

export interface Source {
  id: number;
  name: string;
  camera: string;
  url: string;
  detectUrl: string | null;
  overlayOffsetMs: number;
  sampleFps: number;
  confidence: number;
  enabled: boolean;
  simulate: boolean;
  status: SourceStatus;
  connectedAt: string | null;
  lastFrameAt: string | null;
  lastError: string | null;
  attempts: number;
  nextRetryAt: string | null;
  measuredFps: number;
  frames: number;
  detections: number;
  inferenceMs: number;
  videoId: number | null;
  recent: Record<string, number>;
  webrtcPath: string | null;
}

export interface SourceRequest {
  url: string;
  name?: string;
  camera?: string;
  detectUrl?: string | null;
  overlayOffsetMs?: number;
  sampleFps?: number;
  confidence?: number;
}

export interface LiveDetection { className: string; confidence: number; x1: number; y1: number; x2: number; y2: number; }

export interface LiveEvent {
  sourceId: number;
  videoId: number;
  camera: string;
  name: string;
  frameIndex: number;
  timestampSeconds: number;
  occurredAt: string;
  frameSaved: boolean;
  detections: LiveDetection[];
  /** client receive time, ms since epoch */
  receivedAt: number;
}

export interface IndexJob {
  id: string;
  fileName: string;
  camera: string;
  status: 'queued' | 'running' | 'done' | 'failed';
  percent: number;
  frames: number;
  detections: number;
  framesPerSecond: number;
  videoId: number | null;
  error: string | null;
  perClass: Record<string, number> | null;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
}

export interface Status {
  sources: Source[];
  jobs: IndexJob[];
  framesArchiveBytes: number;
  uptimeSeconds: number;
  mediamtx: { available: boolean; webrtcBaseUrl: string };
}

export const CLASS_COLOURS: Record<string, string> = {
  person: '#ff4d4d', car: '#4dff88', truck: '#ffd24d', bus: '#4dc3ff', motorcycle: '#ff8c4d', bicycle: '#c84dff', dog: '#ff4dc3',
  face: '#4dfff0',
};

export const colourFor = (cls: string) => CLASS_COLOURS[cls] ?? '#ffffff';

export const fmtClock = (s: number) => `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}`;
export const fmtClockPrecise = (s: number) => `${Math.floor(s / 60)}:${(s % 60).toFixed(1).padStart(4, '0')}`;
