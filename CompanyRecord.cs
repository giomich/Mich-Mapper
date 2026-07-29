namespace MichMapper;

internal sealed class CompanyRecord
{
    public string SourceFile { get; init; } = "";
    public string Denominazione { get; set; } = "";
    public string PartitaIva { get; set; } = "";
    public string CodiceFiscale { get; set; } = "";
    public string Attivita { get; set; } = "";
    public int PageCount { get; init; }
    public string ExtractedText { get; init; } = "";
    public string Status { get; set; } = "";
}
