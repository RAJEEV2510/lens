using System.Reflection;
using System.Text;
using Dapper;
using Lens.Core.Models;
using Npgsql;
using NpgsqlTypes;

namespace Lens.Core.Storage;

/// <summary>
/// PostgreSQL store. Detections are written with binary COPY, which is the fastest path Postgres has
/// for bulk loads: an hour of video at 2 fps with a dozen objects per frame is ~90k rows and lands in well under a second.
/// </summary>
public sealed class PostgresStore : IDetectionStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresStore(string connectionString)
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        var sql = await LoadSchemaSqlAsync(ct);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async Task<int> UpsertVideoAsync(VideoInfo video, bool clearDetections = true, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO videos (path, name, camera, width, height, fps, duration_seconds, started_at, indexed_at, is_live)
            VALUES (@Path, @Name, @Camera, @Width, @Height, @Fps, @DurationSeconds, @StartedAt, now(), @IsLive)
            ON CONFLICT (path) DO UPDATE SET
                name = EXCLUDED.name, camera = EXCLUDED.camera, width = EXCLUDED.width, height = EXCLUDED.height,
                fps = EXCLUDED.fps, duration_seconds = EXCLUDED.duration_seconds, started_at = EXCLUDED.started_at,
                indexed_at = now(), is_live = EXCLUDED.is_live
            RETURNING id;
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql, new
        {
            video.Path, video.Name, video.Camera, video.Width, video.Height, video.Fps, video.DurationSeconds,
            StartedAt = video.StartedAt.UtcDateTime, video.IsLive,
        }, cancellationToken: ct));

        // Re-indexing a file replaces its detections. Live sources keep accumulating.
        if (clearDetections)
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM detections WHERE video_id = @id", new { id }, cancellationToken: ct));
        return id;
    }

    public async Task UpdateVideoDurationAsync(int id, double durationSeconds, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE videos SET duration_seconds = GREATEST(duration_seconds, @durationSeconds) WHERE id = @id",
            new { id, durationSeconds }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<VideoSource>> ListSourcesAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, name, camera, url, detect_url AS DetectUrl, overlay_offset_ms AS OverlayOffsetMs,
                   sample_fps AS SampleFps, confidence, enabled, simulate, created_at AS CreatedAt
            FROM sources ORDER BY id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SourceRow>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(r => r.ToSource()).ToList();
    }

    public async Task<int> SaveSourceAsync(VideoSource source, CancellationToken ct = default)
    {
        const string insert = """
            INSERT INTO sources (name, camera, url, detect_url, overlay_offset_ms, sample_fps, confidence, enabled, simulate)
            VALUES (@Name, @Camera, @Url, @DetectUrl, @OverlayOffsetMs, @SampleFps, @Confidence, @Enabled, @Simulate) RETURNING id
            """;
        const string update = """
            UPDATE sources SET name = @Name, camera = @Camera, url = @Url, detect_url = @DetectUrl, overlay_offset_ms = @OverlayOffsetMs,
                               sample_fps = @SampleFps, confidence = @Confidence, enabled = @Enabled, simulate = @Simulate
            WHERE id = @Id RETURNING id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(source.Id > 0 ? update : insert, source, cancellationToken: ct));
    }

    public async Task DeleteSourceAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM sources WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<long> WriteDetectionsAsync(int videoId, IAsyncEnumerable<Detection> detections, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var startedAt = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT started_at FROM videos WHERE id = @videoId", new { videoId }, cancellationToken: ct));

        const string copy = "COPY detections (video_id, frame_index, ts_seconds, occurred_at, class_id, class_name, confidence, x1, y1, x2, y2) FROM STDIN (FORMAT BINARY)";
        await using var writer = await conn.BeginBinaryImportAsync(copy, ct);
        long count = 0;
        await foreach (var d in detections.WithCancellation(ct))
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(videoId, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(d.FrameIndex, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(d.TimestampSeconds, NpgsqlDbType.Double, ct);
            await writer.WriteAsync(startedAt.AddSeconds(d.TimestampSeconds), NpgsqlDbType.TimestampTz, ct);
            await writer.WriteAsync((short)d.ClassId, NpgsqlDbType.Smallint, ct);
            await writer.WriteAsync(d.ClassName, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(d.Confidence, NpgsqlDbType.Real, ct);
            await writer.WriteAsync(d.X1, NpgsqlDbType.Real, ct);
            await writer.WriteAsync(d.Y1, NpgsqlDbType.Real, ct);
            await writer.WriteAsync(d.X2, NpgsqlDbType.Real, ct);
            await writer.WriteAsync(d.Y2, NpgsqlDbType.Real, ct);
            count++;
        }
        await writer.CompleteAsync(ct);
        return count;
    }

    public async Task<IReadOnlyList<VideoInfo>> ListVideosAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT v.id, v.path, v.name, v.camera, v.width, v.height, v.fps, v.is_live AS IsLive,
                   v.duration_seconds AS DurationSeconds, v.started_at AS StartedAt, v.indexed_at AS IndexedAt,
                   (SELECT count(*) FROM detections d WHERE d.video_id = v.id) AS DetectionCount
            FROM videos v ORDER BY v.id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<VideoRow>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(r => r.ToInfo()).ToList();
    }

    public async Task<VideoInfo?> GetVideoAsync(int id, CancellationToken ct = default)
    {
        const string sql = """
            SELECT v.id, v.path, v.name, v.camera, v.width, v.height, v.fps, v.is_live AS IsLive,
                   v.duration_seconds AS DurationSeconds, v.started_at AS StartedAt, v.indexed_at AS IndexedAt,
                   (SELECT count(*) FROM detections d WHERE d.video_id = v.id) AS DetectionCount
            FROM videos v WHERE v.id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<VideoRow>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return row?.ToInfo();
    }

    public async Task<IReadOnlyList<DetectionHit>> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        var (where, args) = BuildWhere(request.Filter);
        args.Add("limit", Math.Clamp(request.Limit, 1, 500));
        args.Add("win", request.GroupWindowSeconds);

        string sql;
        if (request.GroupWindowSeconds <= 0)
        {
            sql = $"""
                SELECT d.video_id AS VideoId, v.name AS VideoName, v.camera AS Camera, d.ts_seconds AS TimestampSeconds,
                       d.ts_seconds AS EndSeconds, d.ts_seconds AS BestSeconds, d.occurred_at AS OccurredAt, d.class_name AS ClassName, d.confidence AS Confidence,
                       d.x1, d.y1, d.x2, d.y2, 1 AS Count
                FROM detections d JOIN videos v ON v.id = d.video_id
                WHERE {where}
                ORDER BY d.occurred_at, d.confidence DESC
                LIMIT @limit
                """;
        }
        else
        {
            // Gaps-and-islands: consecutive sightings of the same class within @win seconds collapse into one event.
            sql = $"""
                WITH f AS (
                    SELECT d.video_id, v.name AS video_name, v.camera, d.ts_seconds, d.occurred_at, d.class_name, d.confidence,
                           d.x1, d.y1, d.x2, d.y2
                    FROM detections d JOIN videos v ON v.id = d.video_id
                    WHERE {where}
                ), g AS (
                    SELECT *, CASE WHEN ts_seconds - LAG(ts_seconds) OVER (PARTITION BY video_id, class_name ORDER BY ts_seconds) > @win
                                   THEN 1 ELSE 0 END AS brk
                    FROM f
                ), s AS (
                    SELECT *, SUM(brk) OVER (PARTITION BY video_id, class_name ORDER BY ts_seconds ROWS UNBOUNDED PRECEDING) AS grp
                    FROM g
                )
                SELECT video_id AS VideoId, video_name AS VideoName, camera AS Camera,
                       MIN(ts_seconds) AS TimestampSeconds, MAX(ts_seconds) AS EndSeconds, MIN(occurred_at) AS OccurredAt,
                       class_name AS ClassName, MAX(confidence) AS Confidence,
                       (array_agg(x1 ORDER BY confidence DESC))[1] AS x1,
                       (array_agg(y1 ORDER BY confidence DESC))[1] AS y1,
                       (array_agg(x2 ORDER BY confidence DESC))[1] AS x2,
                       (array_agg(y2 ORDER BY confidence DESC))[1] AS y2,
                       (array_agg(ts_seconds ORDER BY confidence DESC))[1] AS BestSeconds,
                       COUNT(*)::int AS Count
                FROM s
                GROUP BY video_id, video_name, camera, class_name, grp
                ORDER BY OccurredAt
                LIMIT @limit
                """;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<HitRow>(new CommandDefinition(sql, args, cancellationToken: ct));
        return rows.Select(r => r.ToHit()).ToList();
    }

    public async Task<IReadOnlyList<ClassCount>> CountByClassAsync(DetectionFilter filter, CancellationToken ct = default)
    {
        var (where, args) = BuildWhere(filter);
        var sql = $"""
            SELECT d.class_name AS ClassName, COUNT(*) AS Count, MIN(d.ts_seconds) AS FirstSeconds, MAX(d.ts_seconds) AS LastSeconds
            FROM detections d JOIN videos v ON v.id = d.video_id
            WHERE {where}
            GROUP BY d.class_name ORDER BY Count DESC
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ClassCount>(new CommandDefinition(sql, args, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<TimeBucketCount>> CountByTimeAsync(DetectionFilter filter, TimeSpan bucket, CancellationToken ct = default)
    {
        var (where, args) = BuildWhere(filter);
        args.Add("bucket", bucket);
        var sql = $"""
            SELECT date_bin(@bucket, d.occurred_at, TIMESTAMPTZ '2000-01-01') AS BucketStart, d.class_name AS ClassName, COUNT(*) AS Count
            FROM detections d JOIN videos v ON v.id = d.video_id
            WHERE {where}
            GROUP BY 1, 2 ORDER BY 1, 3 DESC
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<BucketRow>(new CommandDefinition(sql, args, cancellationToken: ct));
        return rows.Select(r => new TimeBucketCount(new DateTimeOffset(DateTime.SpecifyKind(r.BucketStart, DateTimeKind.Utc)), r.ClassName, r.Count)).ToList();
    }

    private static (string where, DynamicParameters args) BuildWhere(DetectionFilter f)
    {
        var sb = new StringBuilder("1=1");
        var args = new DynamicParameters();
        if (f.VideoId is { } vid) { sb.Append(" AND d.video_id = @videoId"); args.Add("videoId", vid); }
        if (!string.IsNullOrWhiteSpace(f.Camera)) { sb.Append(" AND v.camera ILIKE @camera"); args.Add("camera", f.Camera); }
        if (f.Classes is { Count: > 0 }) { sb.Append(" AND d.class_name = ANY(@classes)"); args.Add("classes", f.Classes.Select(c => c.ToLowerInvariant()).ToArray()); }
        if (f.FromSeconds is { } fs) { sb.Append(" AND d.ts_seconds >= @fromSeconds"); args.Add("fromSeconds", fs); }
        if (f.ToSeconds is { } ts) { sb.Append(" AND d.ts_seconds <= @toSeconds"); args.Add("toSeconds", ts); }
        if (f.FromTime is { } ft) { sb.Append(" AND d.occurred_at >= @fromTime"); args.Add("fromTime", ft.UtcDateTime); }
        if (f.ToTime is { } tt) { sb.Append(" AND d.occurred_at <= @toTime"); args.Add("toTime", tt.UtcDateTime); }
        if (f.MinConfidence is { } mc) { sb.Append(" AND d.confidence >= @minConf"); args.Add("minConf", mc); }
        if (f.MinArea is { } ma) { sb.Append(" AND (d.x2 - d.x1) * (d.y2 - d.y1) >= @minArea"); args.Add("minArea", ma); }
        return (sb.ToString(), args);
    }

    private static async Task<string> LoadSchemaSqlAsync(CancellationToken ct)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("init.sql", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("Embedded init.sql not found");
        await using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return await r.ReadToEndAsync(ct);
    }

    private sealed class VideoRow
    {
        public int Id { get; set; }
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public string Camera { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public double Fps { get; set; }
        public double DurationSeconds { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime IndexedAt { get; set; }
        public long DetectionCount { get; set; }
        public bool IsLive { get; set; }

        public VideoInfo ToInfo() => new()
        {
            Id = Id, Path = Path, Name = Name, Camera = Camera, Width = Width, Height = Height, Fps = Fps,
            DurationSeconds = DurationSeconds,
            StartedAt = new DateTimeOffset(DateTime.SpecifyKind(StartedAt, DateTimeKind.Utc)),
            IndexedAt = new DateTimeOffset(DateTime.SpecifyKind(IndexedAt, DateTimeKind.Utc)),
            DetectionCount = DetectionCount,
            IsLive = IsLive,
        };
    }

    private sealed class SourceRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Camera { get; set; } = "";
        public string Url { get; set; } = "";
        public string? DetectUrl { get; set; }
        public int OverlayOffsetMs { get; set; }
        public double SampleFps { get; set; }
        public float Confidence { get; set; }
        public bool Enabled { get; set; }
        public bool Simulate { get; set; }
        public DateTime CreatedAt { get; set; }

        public VideoSource ToSource() => new()
        {
            Id = Id, Name = Name, Camera = Camera, Url = Url, DetectUrl = DetectUrl, OverlayOffsetMs = OverlayOffsetMs,
            SampleFps = SampleFps, Confidence = Confidence, Enabled = Enabled, Simulate = Simulate,
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
        };
    }

    private sealed class HitRow
    {
        public int VideoId { get; set; }
        public string VideoName { get; set; } = "";
        public string Camera { get; set; } = "";
        public double TimestampSeconds { get; set; }
        public double EndSeconds { get; set; }
        public double BestSeconds { get; set; }
        public DateTime OccurredAt { get; set; }
        public string ClassName { get; set; } = "";
        public float Confidence { get; set; }
        public float X1 { get; set; }
        public float Y1 { get; set; }
        public float X2 { get; set; }
        public float Y2 { get; set; }
        public int Count { get; set; }

        public DetectionHit ToHit() => new()
        {
            VideoId = VideoId, VideoName = VideoName, Camera = Camera, TimestampSeconds = TimestampSeconds, EndSeconds = EndSeconds, BestSeconds = BestSeconds,
            OccurredAt = new DateTimeOffset(DateTime.SpecifyKind(OccurredAt, DateTimeKind.Utc)),
            ClassName = ClassName, Confidence = Confidence, X1 = X1, Y1 = Y1, X2 = X2, Y2 = Y2, Count = Count,
        };
    }

    private sealed class BucketRow
    {
        public DateTime BucketStart { get; set; }
        public string ClassName { get; set; } = "";
        public long Count { get; set; }
    }
}
