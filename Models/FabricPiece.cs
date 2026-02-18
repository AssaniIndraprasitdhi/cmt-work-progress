namespace WorkProgress.Models;

public class BarcodeItem
{
    public string BarcodeNo { get; set; } = "";
    public string Orno { get; set; } = "";
    public string? DesignName { get; set; }
    public string? ListNo { get; set; }
    public string ItemNo { get; set; } = "";
    public string? CnvId { get; set; }
    public string? CnvDesc { get; set; }
    public string? Asplan { get; set; }
    public decimal? Width { get; set; }
    public decimal? Length { get; set; }
    public decimal? Sqm { get; set; }
    public decimal? Qty { get; set; }
    public string? OrderType { get; set; }
    public DateTime SyncedAt { get; set; }
}
