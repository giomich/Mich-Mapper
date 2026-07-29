using ClosedXML.Excel;

namespace MichMapper;

internal sealed class ExcelExporter
{
    public void Export(string outputPath, IReadOnlyList<CompanyRecord> records)
    {
        using var workbook = new XLWorkbook();

        var companies = workbook.Worksheets.Add("ANAGRAFICHE");
        companies.Cell(1, 1).Value = "File origine";
        companies.Cell(1, 2).Value = "Denominazione";
        companies.Cell(1, 3).Value = "Partita IVA";
        companies.Cell(1, 4).Value = "Codice fiscale";
        companies.Cell(1, 5).Value = "Attività economica";
        companies.Cell(1, 6).Value = "Pagine";
        companies.Cell(1, 7).Value = "Stato";

        for (int i = 0; i < records.Count; i++)
        {
            int row = i + 2;
            CompanyRecord record = records[i];

            companies.Cell(row, 1).Value = record.SourceFile;
            companies.Cell(row, 2).Value = record.Denominazione;
            companies.Cell(row, 3).Value = record.PartitaIva;
            companies.Cell(row, 4).Value = record.CodiceFiscale;
            companies.Cell(row, 5).Value = record.Attivita;
            companies.Cell(row, 6).Value = record.PageCount;
            companies.Cell(row, 7).Value = record.Status;
        }

        var header = companies.Range(1, 1, 1, 7);
        header.Style.Font.Bold = true;
        companies.SheetView.FreezeRows(1);
        companies.Columns().AdjustToContents(8, 55);
        companies.Column(5).Width = 55;

        var raw = workbook.Worksheets.Add("TESTO_ESTRATTO");
        raw.Cell(1, 1).Value = "File origine";
        raw.Cell(1, 2).Value = "Testo estratto";
        raw.Range(1, 1, 1, 2).Style.Font.Bold = true;

        for (int i = 0; i < records.Count; i++)
        {
            int row = i + 2;
            raw.Cell(row, 1).Value = records[i].SourceFile;
            raw.Cell(row, 2).Value = records[i].ExtractedText;
        }

        raw.Column(1).Width = 45;
        raw.Column(2).Width = 100;
        raw.Column(2).Style.Alignment.WrapText = true;
        raw.SheetView.FreezeRows(1);

        workbook.SaveAs(outputPath);
    }
}
