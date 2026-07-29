using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace MichMapper;

internal sealed class CervedPdfReader
{
    private static readonly Regex PartitaIvaRegex =
        new(@"(?:PARTITA\s+IVA|P\.?\s*IVA)\s*[:\-]?\s*(\d{11})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CodiceFiscaleRegex =
        new(@"(?:CODICE\s+FISCALE|C\.?\s*F\.?)\s*[:\-]?\s*([A-Z0-9]{11,16})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public CompanyRecord Read(string pdfPath)
    {
        if (!File.Exists(pdfPath))
            throw new FileNotFoundException("PDF non trovato.", pdfPath);

        var text = new StringBuilder();
        int pageCount;

        using (var document = PdfDocument.Open(pdfPath))
        {
            pageCount = document.NumberOfPages;

            foreach (var page in document.GetPages())
            {
                text.AppendLine(page.Text);
                text.AppendLine();
            }
        }

        string fullText = Normalize(text.ToString());
        string fileName = Path.GetFileName(pdfPath);

        return new CompanyRecord
        {
            SourceFile = fileName,
            Denominazione = ExtractDenominazione(fullText, fileName),
            PartitaIva = MatchValue(PartitaIvaRegex, fullText),
            CodiceFiscale = MatchValue(CodiceFiscaleRegex, fullText),
            Attivita = ExtractAfterLabel(fullText,
                "ATTIVITA ECONOMICA",
                "ATTIVITÀ ECONOMICA",
                "OGGETTO SOCIALE"),
            PageCount = pageCount,
            ExtractedText = fullText,
            Status = "Analizzato"
        };
    }

    private static string MatchValue(Regex regex, string text)
    {
        Match match = regex.Match(text);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    private static string ExtractDenominazione(string text, string fileName)
    {
        string[] labels =
        [
            "DENOMINAZIONE",
            "RAGIONE SOCIALE",
            "DATI IDENTIFICATIVI"
        ];

        foreach (string label in labels)
        {
            string value = ExtractAfterLabel(text, label);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return Path.GetFileNameWithoutExtension(fileName)
            .Replace("_", " ")
            .Replace("-", " ")
            .Trim();
    }

    private static string ExtractAfterLabel(string text, params string[] labels)
    {
        string[] lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (int i = 0; i < lines.Length; i++)
        {
            foreach (string label in labels)
            {
                if (!lines[i].Contains(label, StringComparison.OrdinalIgnoreCase))
                    continue;

                string sameLine = lines[i];
                int separator = sameLine.IndexOf(':');

                if (separator >= 0 && separator < sameLine.Length - 1)
                {
                    string value = sameLine[(separator + 1)..].Trim();
                    if (value.Length >= 3)
                        return Limit(value, 250);
                }

                for (int next = i + 1; next < Math.Min(i + 4, lines.Length); next++)
                {
                    string candidate = lines[next].Trim();
                    if (candidate.Length >= 3)
                        return Limit(candidate, 250);
                }
            }
        }

        return "";
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
