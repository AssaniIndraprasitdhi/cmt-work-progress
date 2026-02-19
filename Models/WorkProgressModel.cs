namespace WorkProgress.Models;

public class ProgressRecord
{
    public int Id { get; set; }
    public string OrderNo { get; set; } = "";
    public DateOnly WorkDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public decimal ComputedNormalPercent { get; set; }
    public decimal ComputedOtPercent { get; set; }
    public decimal ComputedTotalPercent { get; set; }
    public decimal QualityScore { get; set; }
    public string AlgoVersion { get; set; } = "";
    public string BaseInfoJson { get; set; } = "{}";
    public string? EvidenceImagePath { get; set; }
    public decimal DeltaNormalPercent { get; set; }
    public decimal DeltaOtPercent { get; set; }
    public decimal DeltaTotalPercent { get; set; }
    public string? Note { get; set; }
    public string? CreatedBy { get; set; }
}

public class ProgressSaveRequest
{
    public string BarcodeNo { get; set; } = "";
    public string Orno { get; set; } = "";
    public decimal NormalPercent { get; set; }
    public decimal OtPercent { get; set; }
    public decimal TotalPercent { get; set; }
    public decimal QualityScore { get; set; }
    public string? ImageBase64 { get; set; }
    public string? Note { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? RecordDate { get; set; }
}

public class ColorAnalysisResult
{
    public decimal NormalPercent { get; set; }
    public decimal OtPercent { get; set; }
    public decimal TotalPercent { get; set; }
    public decimal QualityScore { get; set; }
    public bool IsComplete { get; set; }
}

public class OrderInfoViewModel
{
    public BarcodeItem? BarcodeItem { get; set; }
    public List<ProgressRecord> ProgressHistory { get; set; } = new();
    public int ProgressTotalCount { get; set; }
    public decimal CumulativeNormal { get; set; }
    public decimal CumulativeOt { get; set; }
    public decimal CumulativeTotal { get; set; }
    public bool CumulativeIsComplete { get; set; }
    public bool HasColorProfile { get; set; }
    public bool HasTemplate { get; set; }

    public decimal DailyDeltaNormal { get; set; }
    public decimal DailyDeltaOt { get; set; }
    public decimal DailyDeltaTotal { get; set; }

    public List<DailyProgressSummary> DailySummaries { get; set; } = new();
}

public class DailyProgressSummary
{
    public int Id { get; set; }
    public string OrderNo { get; set; } = "";
    public DateTime WorkDate { get; set; }
    public decimal NormalPercent { get; set; }
    public decimal OtPercent { get; set; }
    public decimal TotalPercent { get; set; }
    public decimal DeltaNormal { get; set; }
    public decimal DeltaOt { get; set; }
    public decimal DeltaTotal { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ColorProfile
{
    public int Id { get; set; }
    public string OrderNo { get; set; } = "";
    public int Tolerance { get; set; } = 30;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<ColorProfileColor> Colors { get; set; } = new();
}

public class ColorProfileColor
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public string ColorGroup { get; set; } = "normal";
    public string HexColor { get; set; } = "#000000";
    public float HsvH { get; set; }
    public float HsvS { get; set; }
    public float HsvV { get; set; }
    public int SortOrder { get; set; }
}

public class ColorProfileSaveRequest
{
    public string OrderNo { get; set; } = "";
    public int Tolerance { get; set; } = 30;
    public List<ColorProfileColorInput> Colors { get; set; } = new();
}

public class ColorProfileColorInput
{
    public string ColorGroup { get; set; } = "normal";
    public string HexColor { get; set; } = "#000000";
}

public class OrderTemplate
{
    public int Id { get; set; }
    public string OrderNo { get; set; } = "";
    public string TemplateImagePath { get; set; } = "";
    public string PaintableMaskPath { get; set; } = "";
    public int PaintablePixels { get; set; }
    public int TemplateWidth { get; set; }
    public int TemplateHeight { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class TemplateSaveRequest
{
    public string OrderNo { get; set; } = "";
    public string ImageBase64 { get; set; } = "";
}
