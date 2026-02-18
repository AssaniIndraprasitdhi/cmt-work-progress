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
    private readonly IWebHostEnvironment _env;

    public ApiWorkProgressController(DbService db, ColorAnalysisService colorService, IWebHostEnvironment env)
    {
        _db = db;
        _colorService = colorService;
        _env = env;
    }

    [HttpGet("scan/{barcode}")]
    public async Task<IActionResult> ScanBarcode(string barcode)
    {
        var item = await _db.GetBarcodeItem(barcode);
        if (item == null)
            return NotFound(new { message = "ไม่พบข้อมูล Barcode นี้" });

        var history = await _db.GetProgressByOrderNo(barcode);

        var cumNormal = history.Any() ? history.Max(h => h.FinalNormalPercent ?? h.ComputedNormalPercent) : 0m;
        var cumOt = history.Any() ? history.Max(h => h.FinalOtPercent ?? h.ComputedOtPercent) : 0m;

        return Ok(new OrderInfoViewModel
        {
            BarcodeItem = item,
            ProgressHistory = history,
            CumulativeNormal = cumNormal,
            CumulativeOt = cumOt,
            CumulativeTotal = cumNormal + cumOt
        });
    }

    [HttpPost("analyze")]
    public IActionResult AnalyzeImage([FromBody] ImageUploadRequest req)
    {
        if (string.IsNullOrEmpty(req.ImageBase64))
            return BadRequest(new { message = "ไม่มีรูปภาพ" });

        var base64 = req.ImageBase64;
        if (base64.Contains(","))
            base64 = base64.Split(',')[1];

        var imageBytes = Convert.FromBase64String(base64);
        var result = _colorService.Analyze(imageBytes);
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

        req.TotalPercent = req.NormalPercent + req.OtPercent;

        var baseInfo = new { req.BarcodeNo, req.Orno, SavedAt = DateTime.Now };
        var baseInfoJson = JsonSerializer.Serialize(baseInfo);

        var saved = await _db.SaveProgress(req, imagePath, baseInfoJson);
        return Ok(saved);
    }

    [HttpPut("update/{id}")]
    public async Task<IActionResult> UpdateProgress(int id, [FromBody] UpdateProgressRequest req)
    {
        var total = req.NormalPercent + req.OtPercent;
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
    public async Task<IActionResult> GetHistory(string barcode)
    {
        var history = await _db.GetProgressByOrderNo(barcode);
        return Ok(history);
    }
}

public class ImageUploadRequest
{
    public string ImageBase64 { get; set; } = "";
}

public class UpdateProgressRequest
{
    public decimal NormalPercent { get; set; }
    public decimal OtPercent { get; set; }
    public string? Note { get; set; }
}
