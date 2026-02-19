using Dapper;
using Npgsql;
using WorkProgress.Models;

namespace WorkProgress.Services;

public class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override DateOnly Parse(object value) => DateOnly.FromDateTime((DateTime)value);
    public override void SetValue(System.Data.IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = System.Data.DbType.Date;
        parameter.Value = value.ToDateTime(TimeOnly.MinValue);
    }
}

public class DbService
{
    private readonly string _connectionString;

    static DbService()
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
    }

    public DbService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")!;
        EnsureColorProfileTables().GetAwaiter().GetResult();
        EnsureOrderTemplateTables().GetAwaiter().GetResult();
        EnsureDailyProgressTable().GetAwaiter().GetResult();
        EnsureWorkDateColumn().GetAwaiter().GetResult();
        EnsureDeltaColumns().GetAwaiter().GetResult();
        EnsureDropFinalColumns().GetAwaiter().GetResult();
    }

    private NpgsqlConnection CreateConnection() => new(_connectionString);

    private async Task EnsureColorProfileTables()
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS color_profiles (
                id         SERIAL PRIMARY KEY,
                order_no   VARCHAR(50) NOT NULL UNIQUE,
                tolerance  INTEGER NOT NULL DEFAULT 30,
                created_at TIMESTAMP NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMP NOT NULL DEFAULT NOW()
            );
            CREATE TABLE IF NOT EXISTS color_profile_colors (
                id         SERIAL PRIMARY KEY,
                profile_id INTEGER NOT NULL REFERENCES color_profiles(id) ON DELETE CASCADE,
                color_group VARCHAR(10) NOT NULL CHECK (color_group IN ('normal','ot')),
                hex_color  VARCHAR(7) NOT NULL,
                hsv_h      REAL NOT NULL,
                hsv_s      REAL NOT NULL,
                hsv_v      REAL NOT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0
            );
        ");
    }

    public async Task<BarcodeItem?> GetBarcodeItem(string barcode)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<BarcodeItem>(
            @"SELECT barcode_no AS BarcodeNo, orno AS Orno, design_name AS DesignName,
                     list_no AS ListNo, item_no AS ItemNo, cnv_id AS CnvId,
                     cnv_desc AS CnvDesc, asplan AS Asplan, width, length, sqm, qty,
                     order_type AS OrderType, synced_at AS SyncedAt
              FROM barcode_items WHERE TRIM(barcode_no) = @Barcode",
            new { Barcode = barcode.Trim() });
    }

    public async Task<(List<ProgressRecord> Records, int TotalCount)> GetProgressByOrderNo(
        string orderNo, int limit = 7, int offset = 0)
    {
        using var conn = CreateConnection();
        var trimmed = orderNo.Trim();

        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM progress_records WHERE TRIM(order_no) = @OrderNo",
            new { OrderNo = trimmed });

        var result = await conn.QueryAsync<ProgressRecord>(
            @"SELECT id, order_no AS OrderNo, work_date AS WorkDate, created_at AS CreatedAt,
                     computed_normal_percent AS ComputedNormalPercent,
                     computed_ot_percent AS ComputedOtPercent,
                     computed_total_percent AS ComputedTotalPercent,
                     quality_score AS QualityScore, algo_version AS AlgoVersion,
                     evidence_image_path AS EvidenceImagePath,
                     delta_normal_percent AS DeltaNormalPercent,
                     delta_ot_percent AS DeltaOtPercent,
                     delta_total_percent AS DeltaTotalPercent,
                     note, created_by AS CreatedBy
              FROM progress_records WHERE TRIM(order_no) = @OrderNo
              ORDER BY work_date DESC
              LIMIT @Limit OFFSET @Offset",
            new { OrderNo = trimmed, Limit = limit, Offset = offset });

        return (result.ToList(), total);
    }

    public async Task<ProgressRecord> SaveProgress(ProgressSaveRequest req, string? imagePath, string baseInfoJson)
    {
        using var conn = CreateConnection();
        var trimmedOrder = req.BarcodeNo.Trim();
        var targetDate = req.RecordDate?.Date ?? DateTime.Now.Date;
        var workDate = DateOnly.FromDateTime(targetDate);

        // Get previous day's record for delta calculation
        var prev = await conn.QueryFirstOrDefaultAsync<dynamic>(
            @"SELECT computed_normal_percent AS n,
                     computed_ot_percent AS o,
                     computed_total_percent AS t
              FROM progress_records
              WHERE TRIM(order_no) = @OrderNo AND work_date < @WorkDate
              ORDER BY work_date DESC LIMIT 1",
            new { OrderNo = trimmedOrder, WorkDate = workDate });

        decimal prevN = prev != null ? (decimal)prev.n : 0;
        decimal prevO = prev != null ? (decimal)prev.o : 0;
        decimal prevT = prev != null ? (decimal)prev.t : 0;

        var deltaN = Math.Round(req.NormalPercent - prevN, 2);
        var deltaO = Math.Round(req.OtPercent - prevO, 2);
        var deltaT = Math.Round(req.TotalPercent - prevT, 2);

        var id = await conn.QuerySingleAsync<int>(
            @"INSERT INTO progress_records
              (order_no, work_date, created_at,
               computed_normal_percent, computed_ot_percent, computed_total_percent,
               quality_score, algo_version, base_info_json, evidence_image_path,
               delta_normal_percent, delta_ot_percent, delta_total_percent,
               note, created_by)
              VALUES
              (@OrderNo, @WorkDate, NOW(),
               @ComputedNormal, @ComputedOt, @ComputedTotal,
               @QualityScore, @AlgoVersion, @BaseInfoJson::jsonb, @ImagePath,
               @DeltaN, @DeltaO, @DeltaT,
               @Note, @CreatedBy)
              ON CONFLICT (order_no, work_date) DO UPDATE SET
               created_at              = NOW(),
               computed_normal_percent = EXCLUDED.computed_normal_percent,
               computed_ot_percent     = EXCLUDED.computed_ot_percent,
               computed_total_percent  = EXCLUDED.computed_total_percent,
               quality_score           = EXCLUDED.quality_score,
               delta_normal_percent    = EXCLUDED.delta_normal_percent,
               delta_ot_percent        = EXCLUDED.delta_ot_percent,
               delta_total_percent     = EXCLUDED.delta_total_percent,
               note                    = EXCLUDED.note,
               created_by              = EXCLUDED.created_by,
               evidence_image_path     = COALESCE(EXCLUDED.evidence_image_path, progress_records.evidence_image_path),
               base_info_json          = EXCLUDED.base_info_json
              RETURNING id",
            new
            {
                OrderNo = trimmedOrder,
                WorkDate = workDate,
                ComputedNormal = req.NormalPercent,
                ComputedOt = req.OtPercent,
                ComputedTotal = req.TotalPercent,
                req.QualityScore,
                AlgoVersion = "v1.0",
                BaseInfoJson = baseInfoJson,
                ImagePath = imagePath,
                DeltaN = deltaN,
                DeltaO = deltaO,
                DeltaT = deltaT,
                req.Note,
                req.CreatedBy
            });

        // Recalculate deltas for days after this date (backdated entry support)
        await RecalcDeltasAfter(conn, trimmedOrder, workDate);

        return await conn.QueryFirstAsync<ProgressRecord>(
            @"SELECT id, order_no AS OrderNo, work_date AS WorkDate, created_at AS CreatedAt,
                     computed_normal_percent AS ComputedNormalPercent,
                     computed_ot_percent AS ComputedOtPercent,
                     computed_total_percent AS ComputedTotalPercent,
                     quality_score AS QualityScore, algo_version AS AlgoVersion,
                     evidence_image_path AS EvidenceImagePath,
                     delta_normal_percent AS DeltaNormalPercent,
                     delta_ot_percent AS DeltaOtPercent,
                     delta_total_percent AS DeltaTotalPercent,
                     note, created_by AS CreatedBy
              FROM progress_records WHERE id = @Id", new { Id = id });
    }

    private async Task RecalcDeltasAfter(NpgsqlConnection conn, string orderNo, DateOnly afterDate)
    {
        var laterDays = await conn.QueryAsync<dynamic>(
            @"SELECT id,
                     computed_normal_percent AS n,
                     computed_ot_percent AS o,
                     computed_total_percent AS t
              FROM progress_records
              WHERE TRIM(order_no) = @OrderNo AND work_date >= @AfterDate
              ORDER BY work_date ASC",
            new { OrderNo = orderNo, AfterDate = afterDate });

        decimal prevN = 0, prevO = 0, prevT = 0;

        // Get the day before afterDate as baseline
        var baseline = await conn.QueryFirstOrDefaultAsync<dynamic>(
            @"SELECT computed_normal_percent AS n,
                     computed_ot_percent AS o,
                     computed_total_percent AS t
              FROM progress_records
              WHERE TRIM(order_no) = @OrderNo AND work_date < @AfterDate
              ORDER BY work_date DESC LIMIT 1",
            new { OrderNo = orderNo, AfterDate = afterDate });

        if (baseline != null)
        {
            prevN = (decimal)baseline.n;
            prevO = (decimal)baseline.o;
            prevT = (decimal)baseline.t;
        }

        foreach (var day in laterDays)
        {
            var dN = Math.Round((decimal)day.n - prevN, 2);
            var dO = Math.Round((decimal)day.o - prevO, 2);
            var dT = Math.Round((decimal)day.t - prevT, 2);

            await conn.ExecuteAsync(
                @"UPDATE progress_records SET
                    delta_normal_percent = @DeltaN,
                    delta_ot_percent     = @DeltaO,
                    delta_total_percent  = @DeltaT
                  WHERE id = @Id",
                new { Id = (int)day.id, DeltaN = dN, DeltaO = dO, DeltaT = dT });

            prevN = (decimal)day.n;
            prevO = (decimal)day.o;
            prevT = (decimal)day.t;
        }
    }

    public async Task<ProgressRecord?> UpdateProgress(int id, decimal computedNormal, decimal computedOt, decimal computedTotal, string? note)
    {
        using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(
            @"UPDATE progress_records SET computed_normal_percent = @Normal,
              computed_ot_percent = @Ot, computed_total_percent = @Total, note = @Note
              WHERE id = @Id",
            new { Id = id, Normal = computedNormal, Ot = computedOt, Total = computedTotal, Note = note });

        if (affected == 0) return null;

        // Recalculate deltas for this record and subsequent days
        var record = await conn.QueryFirstOrDefaultAsync<ProgressRecord>(
            @"SELECT id, order_no AS OrderNo, work_date AS WorkDate, created_at AS CreatedAt,
                     computed_normal_percent AS ComputedNormalPercent,
                     computed_ot_percent AS ComputedOtPercent,
                     computed_total_percent AS ComputedTotalPercent,
                     quality_score AS QualityScore, algo_version AS AlgoVersion,
                     evidence_image_path AS EvidenceImagePath,
                     delta_normal_percent AS DeltaNormalPercent,
                     delta_ot_percent AS DeltaOtPercent,
                     delta_total_percent AS DeltaTotalPercent,
                     note, created_by AS CreatedBy
              FROM progress_records WHERE id = @Id", new { Id = id });

        if (record != null)
            await RecalcDeltasAfter(conn, record.OrderNo.Trim(), record.WorkDate);

        return record;
    }

    public async Task<bool> DeleteProgress(int id)
    {
        using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync("DELETE FROM progress_records WHERE id = @Id", new { Id = id });
        return affected > 0;
    }

    public async Task<bool> HasColorProfile(string orderNo)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM color_profiles WHERE order_no = @OrderNo)",
            new { OrderNo = orderNo.Trim() });
    }

    public async Task<ColorProfile?> GetColorProfile(string orderNo)
    {
        using var conn = CreateConnection();
        var profile = await conn.QueryFirstOrDefaultAsync<ColorProfile>(
            @"SELECT id, order_no AS OrderNo, tolerance, created_at AS CreatedAt, updated_at AS UpdatedAt
              FROM color_profiles WHERE order_no = @OrderNo",
            new { OrderNo = orderNo.Trim() });

        if (profile == null) return null;

        var colors = await conn.QueryAsync<ColorProfileColor>(
            @"SELECT id, profile_id AS ProfileId, color_group AS ColorGroup, hex_color AS HexColor,
                     hsv_h AS HsvH, hsv_s AS HsvS, hsv_v AS HsvV, sort_order AS SortOrder
              FROM color_profile_colors WHERE profile_id = @ProfileId ORDER BY sort_order",
            new { ProfileId = profile.Id });

        profile.Colors = colors.ToList();
        return profile;
    }

    public async Task<ColorProfile> SaveColorProfile(ColorProfileSaveRequest req)
    {
        using var conn = CreateConnection();
        await conn.OpenAsync();
        using var tx = await conn.BeginTransactionAsync();

        var profileId = await conn.QuerySingleAsync<int>(
            @"INSERT INTO color_profiles (order_no, tolerance)
              VALUES (@OrderNo, @Tolerance)
              ON CONFLICT (order_no) DO UPDATE SET tolerance = @Tolerance, updated_at = NOW()
              RETURNING id",
            new { OrderNo = req.OrderNo.Trim(), req.Tolerance }, tx);

        await conn.ExecuteAsync(
            "DELETE FROM color_profile_colors WHERE profile_id = @ProfileId",
            new { ProfileId = profileId }, tx);

        for (int i = 0; i < req.Colors.Count; i++)
        {
            var c = req.Colors[i];
            var (h, s, v) = ColorAnalysisService.HexToHsv(c.HexColor);
            await conn.ExecuteAsync(
                @"INSERT INTO color_profile_colors (profile_id, color_group, hex_color, hsv_h, hsv_s, hsv_v, sort_order)
                  VALUES (@ProfileId, @ColorGroup, @HexColor, @H, @S, @V, @Sort)",
                new { ProfileId = profileId, c.ColorGroup, c.HexColor, H = h, S = s, V = v, Sort = i }, tx);
        }

        await tx.CommitAsync();
        return (await GetColorProfile(req.OrderNo))!;
    }

    public async Task<bool> DeleteColorProfile(string orderNo)
    {
        using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(
            "DELETE FROM color_profiles WHERE order_no = @OrderNo",
            new { OrderNo = orderNo.Trim() });
        return affected > 0;
    }

    private async Task EnsureOrderTemplateTables()
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS order_templates (
                id                  SERIAL PRIMARY KEY,
                order_no            VARCHAR(50) NOT NULL UNIQUE,
                template_image_path TEXT NOT NULL,
                paintable_mask_path TEXT NOT NULL,
                paintable_pixels    INTEGER NOT NULL DEFAULT 0,
                template_width      INTEGER NOT NULL DEFAULT 0,
                template_height     INTEGER NOT NULL DEFAULT 0,
                created_at          TIMESTAMP NOT NULL DEFAULT NOW()
            );
        ");
    }

    public async Task<OrderTemplate> SaveTemplate(OrderTemplate template)
    {
        using var conn = CreateConnection();
        var id = await conn.QuerySingleAsync<int>(@"
            INSERT INTO order_templates (order_no, template_image_path, paintable_mask_path,
                                         paintable_pixels, template_width, template_height)
            VALUES (@OrderNo, @TemplateImagePath, @PaintableMaskPath,
                    @PaintablePixels, @TemplateWidth, @TemplateHeight)
            ON CONFLICT (order_no) DO UPDATE SET
                template_image_path = @TemplateImagePath,
                paintable_mask_path = @PaintableMaskPath,
                paintable_pixels = @PaintablePixels,
                template_width = @TemplateWidth,
                template_height = @TemplateHeight,
                created_at = NOW()
            RETURNING id",
            template);
        template.Id = id;
        return template;
    }

    public async Task<OrderTemplate?> GetTemplate(string orderNo)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<OrderTemplate>(@"
            SELECT id, order_no AS OrderNo, template_image_path AS TemplateImagePath,
                   paintable_mask_path AS PaintableMaskPath, paintable_pixels AS PaintablePixels,
                   template_width AS TemplateWidth, template_height AS TemplateHeight,
                   created_at AS CreatedAt
            FROM order_templates WHERE order_no = @OrderNo",
            new { OrderNo = orderNo.Trim() });
    }

    public async Task<bool> HasTemplate(string orderNo)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM order_templates WHERE order_no = @OrderNo)",
            new { OrderNo = orderNo.Trim() });
    }

    public async Task<bool> DeleteTemplate(string orderNo)
    {
        using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(
            "DELETE FROM order_templates WHERE order_no = @OrderNo",
            new { OrderNo = orderNo.Trim() });
        return affected > 0;
    }

    // ── Daily Progress Summary ──

    private async Task EnsureDailyProgressTable()
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS daily_progress_summary (
                id             SERIAL PRIMARY KEY,
                order_no       VARCHAR(50) NOT NULL,
                work_date      DATE NOT NULL,
                normal_percent NUMERIC(7,2) NOT NULL DEFAULT 0,
                ot_percent     NUMERIC(7,2) NOT NULL DEFAULT 0,
                total_percent  NUMERIC(7,2) NOT NULL DEFAULT 0,
                delta_normal   NUMERIC(7,2) NOT NULL DEFAULT 0,
                delta_ot       NUMERIC(7,2) NOT NULL DEFAULT 0,
                delta_total    NUMERIC(7,2) NOT NULL DEFAULT 0,
                updated_at     TIMESTAMP NOT NULL DEFAULT NOW(),
                UNIQUE(order_no, work_date)
            );
        ");
    }

    public async Task UpsertDailySummary(string orderNo, DateTime workDate,
        decimal normalPct, decimal otPct, decimal totalPct)
    {
        using var conn = CreateConnection();
        var trimmedOrder = orderNo.Trim();
        var date = workDate.Date;

        // Get previous day's best to calculate delta
        var prev = await conn.QueryFirstOrDefaultAsync<DailyProgressSummary>(
            @"SELECT normal_percent AS NormalPercent, ot_percent AS OtPercent, total_percent AS TotalPercent
              FROM daily_progress_summary
              WHERE order_no = @OrderNo AND work_date < @WorkDate
              ORDER BY work_date DESC LIMIT 1",
            new { OrderNo = trimmedOrder, WorkDate = date });

        var deltaN = prev != null ? Math.Round(normalPct - prev.NormalPercent, 2) : normalPct;
        var deltaO = prev != null ? Math.Round(otPct - prev.OtPercent, 2) : otPct;
        var deltaT = prev != null ? Math.Round(totalPct - prev.TotalPercent, 2) : totalPct;

        await conn.ExecuteAsync(@"
            INSERT INTO daily_progress_summary
                (order_no, work_date, normal_percent, ot_percent, total_percent,
                 delta_normal, delta_ot, delta_total)
            VALUES (@OrderNo, @WorkDate, @Normal, @Ot, @Total,
                    @DeltaN, @DeltaO, @DeltaT)
            ON CONFLICT (order_no, work_date) DO UPDATE SET
                normal_percent = @Normal,
                ot_percent     = @Ot,
                total_percent  = @Total,
                delta_normal   = @DeltaN,
                delta_ot       = @DeltaO,
                delta_total    = @DeltaT,
                updated_at     = NOW()",
            new
            {
                OrderNo = trimmedOrder,
                WorkDate = date,
                Normal = normalPct,
                Ot = otPct,
                Total = totalPct,
                DeltaN = deltaN,
                DeltaO = deltaO,
                DeltaT = deltaT
            });

        // Recalculate deltas for days after this date (in case of backdated entry)
        var laterDays = await conn.QueryAsync<DailyProgressSummary>(
            @"SELECT id, order_no AS OrderNo, work_date AS WorkDate,
                     normal_percent AS NormalPercent, ot_percent AS OtPercent,
                     total_percent AS TotalPercent
              FROM daily_progress_summary
              WHERE order_no = @OrderNo AND work_date > @WorkDate
              ORDER BY work_date ASC",
            new { OrderNo = trimmedOrder, WorkDate = date });

        decimal prevN = normalPct, prevO = otPct, prevT = totalPct;
        foreach (var day in laterDays)
        {
            var dN = Math.Round(day.NormalPercent - prevN, 2);
            var dO = Math.Round(day.OtPercent - prevO, 2);
            var dT = Math.Round(day.TotalPercent - prevT, 2);

            await conn.ExecuteAsync(
                @"UPDATE daily_progress_summary SET
                    delta_normal = @DeltaN, delta_ot = @DeltaO, delta_total = @DeltaT,
                    updated_at = NOW()
                  WHERE id = @Id",
                new { Id = day.Id, DeltaN = dN, DeltaO = dO, DeltaT = dT });

            prevN = day.NormalPercent;
            prevO = day.OtPercent;
            prevT = day.TotalPercent;
        }
    }

    private async Task EnsureWorkDateColumn()
    {
        using var conn = CreateConnection();

        // Check if work_date column already exists
        var exists = await conn.ExecuteScalarAsync<bool>(
            @"SELECT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name   = 'progress_records'
                  AND column_name  = 'work_date'
            )");

        if (exists) return;

        // Add column, backfill, dedup, set NOT NULL, add UNIQUE constraint
        await conn.ExecuteAsync(@"
            ALTER TABLE progress_records ADD COLUMN work_date DATE;

            UPDATE progress_records
               SET work_date = (created_at AT TIME ZONE 'Asia/Bangkok')::date
             WHERE work_date IS NULL;

            DELETE FROM progress_records a
             USING progress_records b
             WHERE a.order_no  = b.order_no
               AND a.work_date = b.work_date
               AND a.id < b.id;

            ALTER TABLE progress_records ALTER COLUMN work_date SET NOT NULL;
            ALTER TABLE progress_records ALTER COLUMN work_date SET DEFAULT CURRENT_DATE;

            ALTER TABLE progress_records
                ADD CONSTRAINT uq_progress_order_date UNIQUE (order_no, work_date);

            CREATE INDEX IF NOT EXISTS idx_progress_order_no
                ON progress_records (order_no);
        ");
    }

    private async Task EnsureDeltaColumns()
    {
        using var conn = CreateConnection();

        var exists = await conn.ExecuteScalarAsync<bool>(
            @"SELECT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name   = 'progress_records'
                  AND column_name  = 'delta_normal_percent'
            )");

        if (exists) return;

        await conn.ExecuteAsync(@"
            ALTER TABLE progress_records
                ADD COLUMN delta_normal_percent NUMERIC(7,2) NOT NULL DEFAULT 0,
                ADD COLUMN delta_ot_percent     NUMERIC(7,2) NOT NULL DEFAULT 0,
                ADD COLUMN delta_total_percent  NUMERIC(7,2) NOT NULL DEFAULT 0;

            -- Backfill deltas from previous day per order_no
            WITH ordered AS (
                SELECT id, order_no, work_date,
                       computed_normal_percent AS cur_n,
                       computed_ot_percent     AS cur_o,
                       computed_total_percent  AS cur_t,
                       LAG(computed_normal_percent) OVER (PARTITION BY order_no ORDER BY work_date) AS prev_n,
                       LAG(computed_ot_percent)     OVER (PARTITION BY order_no ORDER BY work_date) AS prev_o,
                       LAG(computed_total_percent)  OVER (PARTITION BY order_no ORDER BY work_date) AS prev_t
                FROM progress_records
            )
            UPDATE progress_records p SET
                delta_normal_percent = ROUND(o.cur_n - COALESCE(o.prev_n, 0), 2),
                delta_ot_percent     = ROUND(o.cur_o - COALESCE(o.prev_o, 0), 2),
                delta_total_percent  = ROUND(o.cur_t - COALESCE(o.prev_t, 0), 2)
            FROM ordered o WHERE p.id = o.id;
        ");
    }

    private async Task EnsureDropFinalColumns()
    {
        using var conn = CreateConnection();

        var exists = await conn.ExecuteScalarAsync<bool>(
            @"SELECT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name   = 'progress_records'
                  AND column_name  = 'final_normal_percent'
            )");

        if (!exists) return;

        await conn.ExecuteAsync(@"
            ALTER TABLE progress_records
                DROP COLUMN IF EXISTS final_normal_percent,
                DROP COLUMN IF EXISTS final_ot_percent,
                DROP COLUMN IF EXISTS final_total_percent;
        ");
    }

    public async Task<List<DailyProgressSummary>> GetDailySummaries(string orderNo)
    {
        using var conn = CreateConnection();
        var result = await conn.QueryAsync<DailyProgressSummary>(
            @"SELECT id, order_no AS OrderNo, work_date AS WorkDate,
                     normal_percent AS NormalPercent, ot_percent AS OtPercent,
                     total_percent AS TotalPercent,
                     delta_normal AS DeltaNormal, delta_ot AS DeltaOt,
                     delta_total AS DeltaTotal, updated_at AS UpdatedAt
              FROM daily_progress_summary
              WHERE order_no = @OrderNo
              ORDER BY work_date DESC",
            new { OrderNo = orderNo.Trim() });
        return result.ToList();
    }
}
