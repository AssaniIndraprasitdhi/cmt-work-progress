using Dapper;
using Npgsql;
using WorkProgress.Models;

namespace WorkProgress.Services;

public class DbService
{
    private readonly string _connectionString;

    public DbService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")!;
    }

    private NpgsqlConnection CreateConnection() => new(_connectionString);

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

    public async Task<List<ProgressRecord>> GetProgressByOrderNo(string orderNo)
    {
        using var conn = CreateConnection();
        var result = await conn.QueryAsync<ProgressRecord>(
            @"SELECT id, order_no AS OrderNo, created_at AS CreatedAt,
                     computed_normal_percent AS ComputedNormalPercent,
                     computed_ot_percent AS ComputedOtPercent,
                     computed_total_percent AS ComputedTotalPercent,
                     quality_score AS QualityScore, algo_version AS AlgoVersion,
                     evidence_image_path AS EvidenceImagePath,
                     final_normal_percent AS FinalNormalPercent,
                     final_ot_percent AS FinalOtPercent,
                     final_total_percent AS FinalTotalPercent,
                     note, created_by AS CreatedBy
              FROM progress_records WHERE TRIM(order_no) = @OrderNo
              ORDER BY created_at DESC",
            new { OrderNo = orderNo.Trim() });
        return result.ToList();
    }

    public async Task<ProgressRecord> SaveProgress(ProgressSaveRequest req, string? imagePath, string baseInfoJson)
    {
        using var conn = CreateConnection();
        var id = await conn.QuerySingleAsync<int>(
            @"INSERT INTO progress_records
              (order_no, computed_normal_percent, computed_ot_percent, computed_total_percent,
               quality_score, algo_version, base_info_json, evidence_image_path,
               final_normal_percent, final_ot_percent, final_total_percent, note, created_by)
              VALUES (@OrderNo, @ComputedNormal, @ComputedOt, @ComputedTotal,
                      @QualityScore, @AlgoVersion, @BaseInfoJson::jsonb, @ImagePath,
                      @FinalNormal, @FinalOt, @FinalTotal, @Note, @CreatedBy)
              RETURNING id",
            new
            {
                OrderNo = req.BarcodeNo.Trim(),
                ComputedNormal = req.NormalPercent,
                ComputedOt = req.OtPercent,
                ComputedTotal = req.TotalPercent,
                req.QualityScore,
                AlgoVersion = "v1.0",
                BaseInfoJson = baseInfoJson,
                ImagePath = imagePath,
                FinalNormal = req.NormalPercent,
                FinalOt = req.OtPercent,
                FinalTotal = req.TotalPercent,
                req.Note,
                req.CreatedBy
            });

        return await conn.QueryFirstAsync<ProgressRecord>(
            @"SELECT id, order_no AS OrderNo, created_at AS CreatedAt,
                     computed_normal_percent AS ComputedNormalPercent,
                     computed_ot_percent AS ComputedOtPercent,
                     computed_total_percent AS ComputedTotalPercent,
                     quality_score AS QualityScore, algo_version AS AlgoVersion,
                     evidence_image_path AS EvidenceImagePath,
                     final_normal_percent AS FinalNormalPercent,
                     final_ot_percent AS FinalOtPercent,
                     final_total_percent AS FinalTotalPercent,
                     note, created_by AS CreatedBy
              FROM progress_records WHERE id = @Id", new { Id = id });
    }

    public async Task<ProgressRecord?> UpdateProgress(int id, decimal finalNormal, decimal finalOt, decimal finalTotal, string? note)
    {
        using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(
            @"UPDATE progress_records SET final_normal_percent = @FinalNormal,
              final_ot_percent = @FinalOt, final_total_percent = @FinalTotal, note = @Note
              WHERE id = @Id",
            new { Id = id, FinalNormal = finalNormal, FinalOt = finalOt, FinalTotal = finalTotal, Note = note });

        if (affected == 0) return null;

        return await conn.QueryFirstOrDefaultAsync<ProgressRecord>(
            @"SELECT id, order_no AS OrderNo, created_at AS CreatedAt,
                     computed_normal_percent AS ComputedNormalPercent,
                     computed_ot_percent AS ComputedOtPercent,
                     computed_total_percent AS ComputedTotalPercent,
                     quality_score AS QualityScore, algo_version AS AlgoVersion,
                     evidence_image_path AS EvidenceImagePath,
                     final_normal_percent AS FinalNormalPercent,
                     final_ot_percent AS FinalOtPercent,
                     final_total_percent AS FinalTotalPercent,
                     note, created_by AS CreatedBy
              FROM progress_records WHERE id = @Id", new { Id = id });
    }

    public async Task<bool> DeleteProgress(int id)
    {
        using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync("DELETE FROM progress_records WHERE id = @Id", new { Id = id });
        return affected > 0;
    }
}
