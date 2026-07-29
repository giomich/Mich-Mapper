using ClosedXML.Excel;

namespace MichMapper;

internal sealed class ExcelExporter
{
    private const int SafeCellLength = 30000;
    private readonly CervedAdvancedExtractor _advanced = new();

    public void Export(
        string path,
        IReadOnlyList<CervedRecord> records)
    {
        using var workbook = new XLWorkbook();

        ExportAnagrafiche(workbook, records);
        ExportSegnalibri(workbook, records);
        ExportSoci(workbook, records);
        ExportCariche(workbook, records);
        ExportBilancio(workbook, records);
        ExportEvidenze(workbook, records);
        ExportTestoPagine(workbook, records);

        workbook.SaveAs(path);
    }

    private static void ExportAnagrafiche(
        XLWorkbook workbook,
        IReadOnlyList<CervedRecord> records)
    {
        var ws = workbook.Worksheets.Add("ANAGRAFICHE");

        string[] headers =
        [
            "File origine", "Tipo documento", "Denominazione/Nominativo",
            "Cognome", "Nome", "Partita IVA", "Codice fiscale",
            "Attività economica", "Forma giuridica", "Situazione impresa",
            "REA", "Data costituzione", "Pagine", "Segnalibri", "Validazione"
        ];

        WriteHeaders(ws, headers);

        for (int i = 0; i < records.Count; i++)
        {
            int row = i + 2;
            CervedRecord r = records[i];

            ws.Cell(row, 1).Value = r.SourceFile;
            ws.Cell(row, 2).Value = r.DocumentType.ToString();
            ws.Cell(row, 3).Value = r.Denominazione.Value;
            ws.Cell(row, 4).Value = r.Cognome.Value;
            ws.Cell(row, 5).Value = r.Nome.Value;
            ws.Cell(row, 6).Value = r.PartitaIva.Value;
            ws.Cell(row, 7).Value = r.CodiceFiscale.Value;
            ws.Cell(row, 8).Value = Safe(r.AttivitaEconomica.Value);
            ws.Cell(row, 9).Value = r.FormaGiuridica.Value;
            ws.Cell(row, 10).Value = r.SituazioneImpresa.Value;
            ws.Cell(row, 11).Value = r.Rea.Value;
            ws.Cell(row, 12).Value = r.DataCostituzione.Value;
            ws.Cell(row, 13).Value = r.PageCount;
            ws.Cell(row, 14).Value = r.BookmarkStatus;
            ws.Cell(row, 15).Value = r.ValidationStatus;
        }

        FormatSheet(ws, 55);
    }

    private static void ExportSegnalibri(
        XLWorkbook workbook,
        IReadOnlyList<CervedRecord> records)
    {
        var ws = workbook.Worksheets.Add("SEGNALIBRI");

        string[] headers =
        [
            "File origine", "Titolo segnalibro", "Pagina iniziale",
            "Pagina finale", "Livello", "Percorso", "Metodo"
        ];

        WriteHeaders(ws, headers);
        int row = 2;

        foreach (CervedRecord record in records)
        {
            foreach (BookmarkSection section in record.BookmarkSections)
            {
                ws.Cell(row, 1).Value = record.SourceFile;
                ws.Cell(row, 2).Value = section.Title;
                ws.Cell(row, 3).Value = section.StartPage;
                ws.Cell(row, 4).Value = section.EndPage;
                ws.Cell(row, 5).Value = section.Level;
                ws.Cell(row, 6).Value = section.Path;
                ws.Cell(row, 7).Value = section.NavigationMethod;
                row++;
            }
        }

        FormatSheet(ws, 80);
    }

    private void ExportSoci(
        XLWorkbook workbook,
        IReadOnlyList<CervedRecord> records)
    {
        var ws = workbook.Worksheets.Add("SOCI");

        string[] headers =
        [
            "File origine",
            "Socio/Titolare",
            "Società partecipata",
            "CF/P.IVA socio",
            "CF/P.IVA società",
            "Quota %",
            "Valore nominale",
            "Tipo diritto",
            "Segnalibro",
            "Pagina",
            "Metodo",
            "Evidenza"
        ];

        WriteHeaders(ws, headers);
        int row = 2;

        foreach (CervedRecord record in records)
        {
            foreach (ShareholderRow item in
                     _advanced.ExtractShareholders(record))
            {
                ws.Cell(row, 1).Value = item.SourceFile;
                ws.Cell(row, 2).Value = item.Owner;
                ws.Cell(row, 3).Value = item.ParticipatedCompany;
                ws.Cell(row, 4).Value = item.OwnerFiscalCode;
                ws.Cell(row, 5).Value = item.ParticipatedCompanyFiscalCode;
                ws.Cell(row, 6).Value = item.Percentage;
                ws.Cell(row, 7).Value = item.NominalValue;
                ws.Cell(row, 8).Value = item.RightType;
                ws.Cell(row, 9).Value = item.Bookmark;
                ws.Cell(row, 10).Value = item.Page;
                ws.Cell(row, 11).Value = item.Method;
                ws.Cell(row, 12).Value = Safe(item.Evidence);
                row++;
            }
        }

        FormatSheet(ws, 75);
        ws.Column(12).Width = 100;
        ws.Column(12).Style.Alignment.WrapText = true;
    }

    private void ExportCariche(
        XLWorkbook workbook,
        IReadOnlyList<CervedRecord> records)
    {
        var ws = workbook.Worksheets.Add("CARICHE");

        string[] headers =
        [
            "File origine", "Nominativo", "Codice fiscale",
            "Carica", "Segnalibro", "Pagina", "Metodo", "Evidenza"
        ];

        WriteHeaders(ws, headers);
        int row = 2;

        foreach (CervedRecord record in records)
        {
            foreach (OfficerRow item in
                     _advanced.ExtractOfficers(record))
            {
                ws.Cell(row, 1).Value = item.SourceFile;
                ws.Cell(row, 2).Value = item.Name;
                ws.Cell(row, 3).Value = item.FiscalCode;
                ws.Cell(row, 4).Value = item.Role;
                ws.Cell(row, 5).Value = item.Bookmark;
                ws.Cell(row, 6).Value = item.Page;
                ws.Cell(row, 7).Value = item.Method;
                ws.Cell(row, 8).Value = Safe(item.Evidence);
                row++;
            }
        }

        FormatSheet(ws, 75);
        ws.Column(8).Width = 100;
        ws.Column(8).Style.Alignment.WrapText = true;
    }

    private void ExportBilancio(
        XLWorkbook workbook,
        IReadOnlyList<CervedRecord> records)
    {
        var ws = workbook.Worksheets.Add("BILANCIO");

        string[] headers =
        [
            "File origine", "Esercizio", "Ricavi netti",
            "MOL/EBITDA", "Utile/Perdita esercizio",
            "Totale attivo", "Patrimonio netto", "Cash flow",
            "Segnalibro", "Pagina", "Metodo", "Evidenza"
        ];

        WriteHeaders(ws, headers);
        int row = 2;

        foreach (CervedRecord record in records)
        {
            foreach (BalanceRow item in
                     _advanced.ExtractBalance(record))
            {
                ws.Cell(row, 1).Value = item.SourceFile;
                ws.Cell(row, 2).Value = item.Year;
                ws.Cell(row, 3).Value = item.Revenue;
                ws.Cell(row, 4).Value = item.Ebitda;
                ws.Cell(row, 5).Value = item.NetIncome;
                ws.Cell(row, 6).Value = item.TotalAssets;
                ws.Cell(row, 7).Value = item.Equity;
                ws.Cell(row, 8).Value = item.CashFlow;
                ws.Cell(row, 9).Value = item.Bookmark;
                ws.Cell(row, 10).Value = item.Page;
                ws.Cell(row, 11).Value = item.Method;
                ws.Cell(row, 12).Value = Safe(item.Evidence);
                row++;
            }
        }

        FormatSheet(ws, 75);
        ws.Column(12).Width = 100;
        ws.Column(12).Style.Alignment.WrapText = true;
    }

    private static void ExportEvidenze(
        XLWorkbook workbook,
        IReadOnlyList<CervedRecord> records)
    {
        var ws = workbook.Worksheets.Add("EVIDENZE");

        string[] headers =
        [
            "File origine", "Campo", "Valore", "Pagina",
            "Affidabilità", "Metodo", "Testo sorgente"
        ];

        WriteHeaders(ws, headers);
        int row = 2;

        foreach (CervedRecord r in records)
        {
            AddEvidence(ws, ref row, r.SourceFile, "Denominazione", r.Denominazione);
            AddEvidence(ws, ref row, r.SourceFile, "Cognome", r.Cognome);
            AddEvidence(ws, ref row, r.SourceFile, "Nome", r.Nome);
            AddEvidence(ws, ref row, r.SourceFile, "Partita IVA", r.PartitaIva);
            AddEvidence(ws, ref row, r.SourceFile, "Codice fiscale", r.CodiceFiscale);
            AddEvidence(ws, ref row, r.SourceFile, "Attività economica", r.AttivitaEconomica);
            AddEvidence(ws, ref row, r.SourceFile, "Forma giuridica", r.FormaGiuridica);
            AddEvidence(ws, ref row, r.SourceFile, "Situazione impresa", r.SituazioneImpresa);
            AddEvidence(ws, ref row, r.SourceFile, "REA", r.Rea);
            AddEvidence(ws, ref row, r.SourceFile, "Data costituzione", r.DataCostituzione);
        }

        FormatSheet(ws, 75);
        ws.Column(7).Width = 100;
        ws.Column(7).Style.Alignment.WrapText = true;
    }

    private static void ExportTestoPagine(
        XLWorkbook workbook,
        IReadOnlyList<CervedRecord> records)
    {
        var ws = workbook.Worksheets.Add("TESTO_PER_PAGINA");

        string[] headers =
        [
            "File origine", "Pagina", "Parte", "Testo ricostruito"
        ];

        WriteHeaders(ws, headers);
        int row = 2;

        foreach (CervedRecord record in records)
        {
            foreach (PageText page in record.Pages)
            {
                IReadOnlyList<string> chunks = Split(page.Text);

                for (int i = 0; i < chunks.Count; i++)
                {
                    ws.Cell(row, 1).Value = record.SourceFile;
                    ws.Cell(row, 2).Value = page.Number;
                    ws.Cell(row, 3).Value = i + 1;
                    ws.Cell(row, 4).Value = chunks[i];
                    row++;
                }
            }
        }

        FormatSheet(ws, 80);
        ws.Column(4).Width = 100;
        ws.Column(4).Style.Alignment.WrapText = true;
    }

    private static void FormatSheet(
        IXLWorksheet ws,
        double maximumWidth)
    {
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents(8, maximumWidth);
        ws.RangeUsed()?.SetAutoFilter();
    }

    private static void WriteHeaders(
        IXLWorksheet ws,
        IReadOnlyList<string> headers)
    {
        for (int i = 0; i < headers.Count; i++)
            ws.Cell(1, i + 1).Value = headers[i];

        ws.Range(1, 1, 1, headers.Count).Style.Font.Bold = true;
    }

    private static void AddEvidence(
        IXLWorksheet ws,
        ref int row,
        string sourceFile,
        string fieldName,
        ExtractedField field)
    {
        ws.Cell(row, 1).Value = sourceFile;
        ws.Cell(row, 2).Value = fieldName;
        ws.Cell(row, 3).Value = Safe(field.Value);
        ws.Cell(row, 4).Value = field.Page;
        ws.Cell(row, 5).Value = field.Confidence;
        ws.Cell(row, 6).Value = field.Method;
        ws.Cell(row, 7).Value = Safe(field.Evidence);
        row++;
    }

    private static string Safe(string value) =>
        string.IsNullOrEmpty(value)
            ? ""
            : value[..Math.Min(value.Length, SafeCellLength)];

    private static IReadOnlyList<string> Split(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [""];

        var result = new List<string>();

        for (int i = 0; i < text.Length; i += SafeCellLength)
            result.Add(text.Substring(
                i,
                Math.Min(SafeCellLength, text.Length - i)));

        return result;
    }
}
