namespace WorkProgress.Models;

public class ProgressRecord
{
    public int Id { get; set; }
    public string OrderNo { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public decimal ComputedNormalPercent { get; set; }
    public decimal ComputedOtPercent { get; set; }
    public decimal ComputedTotalPercent { get; set; }
    public decimal QualityScore { get; set; }
    public string AlgoVersion { get; set; } = "";
    public string BaseInfoJson { get; set; } = "{}";
    public string? EvidenceImagePath { get; set; }
    public decimal? FinalNormalPercent { get; set; }
    public decimal? FinalOtPercent { get; set; }
    public decimal? FinalTotalPercent { get; set; }
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
}

public class ColorAnalysisResult
{
    public decimal NormalPercent { get; set; }
    public decimal OtPercent { get; set; }
    public decimal TotalPercent { get; set; }
    public decimal QualityScore { get; set; }
}

public class OrderInfoViewModel
{
    public BarcodeItem? BarcodeItem { get; set; }
    public List<ProgressRecord> ProgressHistory { get; set; } = new();
    public decimal CumulativeNormal { get; set; }
    public decimal CumulativeOt { get; set; }
    public decimal CumulativeTotal { get; set; }
}
