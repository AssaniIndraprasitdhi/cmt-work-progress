using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using WorkProgress.Models;
using WorkProgress.Services;

namespace WorkProgress.Controllers;

public class WorkProgressController : Controller
{
    public IActionResult Index() => View();
}

[Route("api/[controller]")]
[ApiController]
public class ApiWorkProgressController : ControllerBase
{
    private readonly DbService _db;
    private readonly ColorAnalysisService _colorService;
    private readonly TemplateMaskService _templateService;
    private readonly IWebHostEnvironment _env;

    public ApiWorkProgressController(DbService db, ColorAnalysisService colorService,
        TemplateMaskService templateService, IWebHostEnvironment env)
    {
        _db = db;
        _colorService = colorService;
        _templateService = templateService;
        _env = env;
    }

    [HttpGet("scan/{barcode}")]
    public async Task<IActionResult> ScanBarcode(string barcode)
    {
        var item = await _db.GetBarcodeItem(barcode);
        if (item == null)
            return NotFound(new { message = "ไม่พบข้อมูล Barcode นี้" });

        var (history, totalCount) = await _db.GetProgressByOrderNo(barcode, limit: 7, offset: 0);

        var cumNormal = history.Any() ? history.Max(h => h.ComputedNormalPercent) : 0m;
        var cumOt = history.Any() ? history.Max(h => h.ComputedOtPercent) : 0m;
        var cumTotal = Math.Min(cumNormal + cumOt, 100m);

        var hasProfile = await _db.HasColorProfile(barcode);
        var hasTemplate = await _db.HasTemplate(barcode);
        var dailySummaries = await _db.GetDailySummaries(barcode);

        // Get latest day's delta from DB summaries
        decimal deltaNormal = 0, deltaOt = 0, deltaTotal = 0;
        if (dailySummaries.Any())
        {
            var latest = dailySummaries.First(); // already sorted DESC
            deltaNormal = latest.DeltaNormal;
            deltaOt = latest.DeltaOt;
            deltaTotal = latest.DeltaTotal;
        }

        return Ok(new OrderInfoViewModel
        {
            BarcodeItem = item,
            ProgressHistory = history,
            ProgressTotalCount = totalCount,
            CumulativeNormal = cumNormal,
            CumulativeOt = cumOt,
            CumulativeTotal = cumTotal,
            CumulativeIsComplete = cumTotal >= 99.5m,
            HasColorProfile = hasProfile,
            HasTemplate = hasTemplate,
            DailyDeltaNormal = deltaNormal,
            DailyDeltaOt = deltaOt,
            DailyDeltaTotal = deltaTotal,
            DailySummaries = dailySummaries
        });
    }

    [HttpPost("analyze")]
    public async Task<IActionResult> AnalyzeImage([FromBody] ImageUploadRequest req)
    {
        if (string.IsNullOrEmpty(req.ImageBase64))
            return BadRequest(new { message = "ไม่มีรูปภาพ" });

        var base64 = req.ImageBase64;
        if (base64.Contains(","))
            base64 = base64.Split(',')[1];

        var imageBytes = Convert.FromBase64String(base64);

        ColorProfile? profile = null;
        OrderTemplate? template = null;

        if (!string.IsNullOrEmpty(req.OrderNo))
        {
            profile = await _db.GetColorProfile(req.OrderNo);
            template = await _db.GetTemplate(req.OrderNo);
        }

        ColorAnalysisResult result;

        if (template != null)
        {
            var templateImgPath = Path.Combine(_env.WebRootPath, template.TemplateImagePath.TrimStart('/'));
            var maskPath = Path.Combine(_env.WebRootPath, template.PaintableMaskPath.TrimStart('/'));
            result = _colorService.AnalyzeWithTemplate(imageBytes, templateImgPath, maskPath, profile);
        }
        else
        {
            result = _colorService.Analyze(imageBytes, profile);
        }

        return Ok(result);
    }

    [HttpPost("save")]
    public async Task<IActionResult> SaveProgress([FromBody] ProgressSaveRequest req)
    {
        string? imagePath = null;

        if (!string.IsNullOrEmpty(req.ImageBase64))
        {
            var base64 = req.ImageBase64;
            if (base64.Contains(","))
                base64 = base64.Split(',')[1];

            var imageBytes = Convert.FromBase64String(base64);
            var uploadsDir = Path.Combine(_env.WebRootPath, "uploads");
            Directory.CreateDirectory(uploadsDir);
            var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{req.BarcodeNo.Trim()}.jpg";
            var filePath = Path.Combine(uploadsDir, fileName);
            await System.IO.File.WriteAllBytesAsync(filePath, imageBytes);
            imagePath = $"/uploads/{fileName}";
        }

        req.TotalPercent = Math.Min(req.NormalPercent + req.OtPercent, 100m);

        var baseInfo = new { req.BarcodeNo, req.Orno, SavedAt = DateTime.Now };
        var baseInfoJson = JsonSerializer.Serialize(baseInfo);

        var saved = await _db.SaveProgress(req, imagePath, baseInfoJson);

        // Upsert daily summary
        var targetDate = req.RecordDate?.Date ?? DateTime.Now.Date;
        await _db.UpsertDailySummary(req.BarcodeNo.Trim(), targetDate,
            req.NormalPercent, req.OtPercent, req.TotalPercent);

        return Ok(saved);
    }

    [HttpPut("update/{id}")]
    public async Task<IActionResult> UpdateProgress(int id, [FromBody] UpdateProgressRequest req)
    {
        var total = Math.Min(req.NormalPercent + req.OtPercent, 100m);
        var updated = await _db.UpdateProgress(id, req.NormalPercent, req.OtPercent, total, req.Note);
        if (updated == null)
            return NotFound(new { message = "ไม่พบข้อมูล" });
        return Ok(updated);
    }

    [HttpDelete("delete/{id}")]
    public async Task<IActionResult> DeleteProgress(int id)
    {
        var deleted = await _db.DeleteProgress(id);
        if (!deleted)
            return NotFound(new { message = "ไม่พบข้อมูล" });
        return Ok(new { message = "ลบสำเร็จ" });
    }

    [HttpGet("history/{barcode}")]
    public async Task<IActionResult> GetHistory(string barcode, [FromQuery] int limit = 7, [FromQuery] int offset = 0)
    {
        var (records, totalCount) = await _db.GetProgressByOrderNo(barcode, limit, offset);
        return Ok(new { records, totalCount, limit, offset });
    }

    [HttpGet("color-profile/{orderNo}")]
    public async Task<IActionResult> GetColorProfile(string orderNo)
    {
        var profile = await _db.GetColorProfile(orderNo);
        return Ok(profile);
    }

    [HttpPost("color-profile")]
    public async Task<IActionResult> SaveColorProfile([FromBody] ColorProfileSaveRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.OrderNo))
            return BadRequest(new { message = "ต้องระบุ Order No" });
        if (req.Colors == null || req.Colors.Count == 0)
            return BadRequest(new { message = "ต้องเลือกสีอย่างน้อย 1 สี" });

        var profile = await _db.SaveColorProfile(req);
        return Ok(profile);
    }

    [HttpDelete("color-profile/{orderNo}")]
    public async Task<IActionResult> DeleteColorProfile(string orderNo)
    {
        var deleted = await _db.DeleteColorProfile(orderNo);
        if (!deleted)
            return NotFound(new { message = "ไม่พบการตั้งค่า" });
        return Ok(new { message = "ลบการตั้งค่าสำเร็จ" });
    }

    [HttpPost("template")]
    public async Task<IActionResult> CreateTemplate([FromBody] TemplateSaveRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.OrderNo))
            return BadRequest(new { message = "ต้องระบุ Order No" });
        if (string.IsNullOrEmpty(req.ImageBase64))
            return BadRequest(new { message = "ไม่มีรูปภาพ" });

        var base64 = req.ImageBase64;
        if (base64.Contains(","))
            base64 = base64.Split(',')[1];

        var imageBytes = Convert.FromBase64String(base64);

        try
        {
            var template = await _templateService.CreateTemplate(req.OrderNo, imageBytes);
            return Ok(template);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"สร้าง Template ไม่สำเร็จ: {ex.Message}" });
        }
    }

    [HttpGet("template/{orderNo}")]
    public async Task<IActionResult> GetTemplate(string orderNo)
    {
        var template = await _db.GetTemplate(orderNo);
        return Ok(template);
    }

    [HttpDelete("template/{orderNo}")]
    public async Task<IActionResult> DeleteTemplate(string orderNo)
    {
        var template = await _db.GetTemplate(orderNo);
        if (template != null)
        {
            var imgPath = Path.Combine(_env.WebRootPath, template.TemplateImagePath.TrimStart('/'));
            var maskPath = Path.Combine(_env.WebRootPath, template.PaintableMaskPath.TrimStart('/'));
            if (System.IO.File.Exists(imgPath)) System.IO.File.Delete(imgPath);
            if (System.IO.File.Exists(maskPath)) System.IO.File.Delete(maskPath);
        }

        var deleted = await _db.DeleteTemplate(orderNo);
        if (!deleted)
            return NotFound(new { message = "ไม่พบ Template" });
        return Ok(new { message = "ลบ Template สำเร็จ" });
    }
}

public class ImageUploadRequest
{
    public string ImageBase64 { get; set; } = "";
    public string? OrderNo { get; set; }
}

public class UpdateProgressRequest
{
    public decimal NormalPercent { get; set; }
    public decimal OtPercent { get; set; }
    public string? Note { get; set; }
}
