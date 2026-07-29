using ClosedXML.Excel;

namespace MichMapper;

internal sealed class ExcelExporter
{
    private const int MaxCellLength = 30000;

    public void Export(string outputPath, IReadOnlyList<CompanyRecord> records)
    {
        using var workbook = new XLWorkbook();
        var companies = workbook.Worksheets.Add("ANAGRAFICHE");
        string[] headers = ["File origine", "Denominazione", "Partita IVA", "Codice fiscale", "Attività economica", "Pagine", "Stato"];
        for (int c = 0; c < headers.Length; c++) companies.Cell(1, c + 1).Value = headers[c];

        for (int i = 0; i < records.Count; i++)
        {
            int row = i + 2;
            var x = records[i];
            companies.Cell(row, 1).Value = x.SourceFile;
            companies.Cell(row, 2).Value = x.Denominazione;
            companies.Cell(row, 3).Value = x.PartitaIva;
            companies.Cell(row, 4).Value = x.CodiceFiscale;
            companies.Cell(row, 5).Value = Safe(x.Attivita);
            companies.Cell(row, 6).Value = x.PageCount;
            companies.Cell(row, 7).Value = x.Status;
        }
        companies.Range(1,1,1,headers.Length).Style.Font.Bold = true;
        companies.SheetView.FreezeRows(1);
        companies.Columns().AdjustToContents(8,55);

        var raw = workbook.Worksheets.Add("TESTO_ESTRATTO");
        raw.Cell(1,1).Value = "File origine";
        raw.Cell(1,2).Value = "Parte";
        raw.Cell(1,3).Value = "Testo estratto";
        raw.Range(1,1,1,3).Style.Font.Bold = true;
        int rr = 2;
        foreach (var x in records)
        {
            var chunks = Split(x.ExtractedText);
            for (int i = 0; i < chunks.Count; i++)
            {
                raw.Cell(rr,1).Value = x.SourceFile;
                raw.Cell(rr,2).Value = i + 1;
                raw.Cell(rr,3).Value = chunks[i];
                rr++;
            }
        }
        raw.Column(1).Width = 45;
        raw.Column(2).Width = 10;
        raw.Column(3).Width = 100;
        raw.Column(3).Style.Alignment.WrapText = true;
        raw.SheetView.FreezeRows(1);
        workbook.SaveAs(outputPath);
    }

    private static string Safe(string value) => string.IsNullOrEmpty(value) ? "" : value[..Math.Min(value.Length, MaxCellLength)];

    private static IReadOnlyList<string> Split(string text)
    {
        if (string.IsNullOrEmpty(text)) return [""];
        var result = new List<string>();
        for (int i = 0; i < text.Length; i += MaxCellLength)
            result.Add(text.Substring(i, Math.Min(MaxCellLength, text.Length - i)));
        return result;
    }
}
