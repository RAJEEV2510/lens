-- Lens schema. Idempotent: safe to run on every start.
-- Works on plain PostgreSQL 14+. If TimescaleDB or pgvector are present they are used, otherwise skipped.

DO $$ BEGIN
    BEGIN
        CREATE EXTENSION IF NOT EXISTS timescaledb;
    EXCEPTION WHEN OTHERS THEN
        RAISE NOTICE 'timescaledb not available, using plain tables';
    END;
    BEGIN
        CREATE EXTENSION IF NOT EXISTS vector;
    EXCEPTION WHEN OTHERS THEN
        RAISE NOTICE 'pgvector not available, embeddings disabled';
    END;
END $$;

CREATE TABLE IF NOT EXISTS videos (
    id               serial PRIMARY KEY,
    path             text NOT NULL UNIQUE,
    name             text NOT NULL,
    camera           text NOT NULL DEFAULT 'default',
    width            int NOT NULL,
    height           int NOT NULL,
    fps              double precision NOT NULL,
    duration_seconds double precision NOT NULL,
    started_at       timestamptz NOT NULL,
    indexed_at       timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE videos ADD COLUMN IF NOT EXISTS is_live boolean NOT NULL DEFAULT false;

CREATE TABLE IF NOT EXISTS sources (
    id          serial PRIMARY KEY,
    name        text NOT NULL,
    camera      text NOT NULL DEFAULT 'default',
    url         text NOT NULL,
    sample_fps  double precision NOT NULL DEFAULT 2,
    confidence  real NOT NULL DEFAULT 0.35,
    enabled     boolean NOT NULL DEFAULT true,
    simulate    boolean NOT NULL DEFAULT false,
    created_at  timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE sources ADD COLUMN IF NOT EXISTS detect_url text;
ALTER TABLE sources ADD COLUMN IF NOT EXISTS overlay_offset_ms int NOT NULL DEFAULT 300;

CREATE TABLE IF NOT EXISTS detections (
    id          bigserial,
    video_id    int NOT NULL REFERENCES videos(id) ON DELETE CASCADE,
    frame_index int NOT NULL,
    ts_seconds  double precision NOT NULL,
    occurred_at timestamptz NOT NULL,
    class_id    smallint NOT NULL,
    class_name  text NOT NULL,
    confidence  real NOT NULL,
    x1          real NOT NULL,
    y1          real NOT NULL,
    x2          real NOT NULL,
    y2          real NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_detections_video_class_ts ON detections (video_id, class_name, ts_seconds);
CREATE INDEX IF NOT EXISTS ix_detections_class_time    ON detections (class_name, occurred_at);
CREATE INDEX IF NOT EXISTS ix_detections_time          ON detections (occurred_at DESC);

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'timescaledb') THEN
        PERFORM create_hypertable('detections', 'occurred_at', if_not_exists => TRUE, migrate_data => TRUE);
    END IF;
    IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector') THEN
        -- Week 2: CLIP embeddings per detection crop for "looks like" search.
        EXECUTE 'ALTER TABLE detections ADD COLUMN IF NOT EXISTS embedding vector(512)';
    END IF;
END $$;
