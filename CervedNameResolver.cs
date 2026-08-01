using System.Text.RegularExpressions;

namespace MichMapper;

internal static class CervedNameResolver
{
    public static string GetDenominazione(CervedRecord record)
    {
        string current = Clean(record.Denominazione.Value);

        if (record.DocumentType != CervedDocumentType.Company)
            return current;

        string heading = ReadHeading(record.Pages);
        if (IsBetterCompanyName(heading, current))
            return heading;

        string labelled = ReadLabelledValue(record.Pages);
        if (IsBetterCompanyName(labelled, current))
            return labelled;

        return current;
    }

    private static string ReadHeading(IReadOnlyList<PageText> pages)
    {
        PageText? first = pages.OrderBy(page => page.Number).FirstOrDefault();
        if (first is null)
            return "";

        var result = new List<string>();
        foreach (string raw in first.Text.Split('\n'))
        {
            string line = Clean(raw);
            string normalized = Normalize(line);

            if (normalized.Contains("CODICECERVEDGROUP"))
                break;

            if (normalized.Length < 3 ||
                normalized is "HELP" or "DOSSIER" ||
                normalized.StartsWith("CERVED") ||
                normalized.StartsWith("PRODOTTOIL"))
                continue;

            // L'intestazione Cerved può essere preceduta da caratteri grafici
            // illeggibili: la prima riga completa con forma giuridica è il
            // riferimento più affidabile.
            if (ContainsLegalForm(line) &&
                !normalized.Contains("FORMA GIURIDICA") &&
                !normalized.Contains("DENOMINAZIONE"))
                return line;

            result.Add(line);
        }

        return Clean(string.Join(" ", result));
    }

    private static string ReadLabelledValue(IReadOnlyList<PageText> pages)
    {
        foreach (PageText page in pages.OrderBy(item => item.Number).Take(3))
        {
            string[] lines = page.Text.Split('\n')
                .Select(Clean)
                .Where(line => line.Length > 0)
                .ToArray();

            for (int i = 0; i < lines.Length; i++)
            {
                int label = Normalize(lines[i]).IndexOf("DENOMINAZIONE", StringComparison.Ordinal);
                if (label < 0)
                    continue;

                var parts = new List<string>();
                int rawLabel = lines[i].IndexOf("Denominazione", StringComparison.OrdinalIgnoreCase);
                if (rawLabel >= 0)
                {
                    string tail = Clean(lines[i][(rawLabel + "Denominazione".Length)..]);
                    if (tail.Length > 0)
                        parts.Add(tail);
                }

                for (int j = i + 1; j < Math.Min(lines.Length, i + 5); j++)
                {
                    string normalized = Normalize(lines[j]);
                    if (IsNextField(normalized))
                        break;
                    parts.Add(lines[j]);
                }

                string value = Clean(string.Join(" ", parts));
                int sigla = value.IndexOf(
                    "Sigla della denominazione",
                    StringComparison.OrdinalIgnoreCase);
                if (sigla >= 0)
                    value = Clean(value[..sigla]);
                if (ContainsLegalForm(value))
                    return value;
            }
        }

        return "";
    }

    private static bool IsBetterCompanyName(string candidate, string current) =>
        ContainsLegalForm(candidate) &&
        candidate.Length >= Math.Max(4, current.Length) &&
        !Normalize(candidate).Contains("DATIIDENTIFICATIVI");

    private static bool IsNextField(string normalized) =>
        normalized.StartsWith("INDIRIZZOSEDE") ||
        normalized.StartsWith("CODICEFISCALE") ||
        normalized.StartsWith("PARTITAIVA") ||
        normalized.StartsWith("FORMAGIURIDICA") ||
        normalized.StartsWith("SITUAZIONEIMPRESA") ||
        normalized.StartsWith("NUMEROREA") ||
        normalized.StartsWith("TELEFONO") ||
        normalized.StartsWith("EMAIL");

    private static bool ContainsLegalForm(string value)
    {
        string normalized = Normalize(value);
        return normalized.Contains("SRL") ||
               normalized.Contains("SPA") ||
               normalized.Contains("SOCIETASEMPLICE") ||
               normalized.Contains("SOCIETAINNOMECOLLETTIVO") ||
               normalized.Contains("SOCIETAINACCOMANDITASEMPLICE");
    }

    private static string Clean(string value) =>
        Regex.Replace(value ?? "", @"\s+", " ").Trim(' ', '-', '|');

    private static string Normalize(string value)
    {
        string normalized = Clean(value).ToUpperInvariant()
            .Replace("À", "A").Replace("È", "E").Replace("É", "E")
            .Replace("Ì", "I").Replace("Ò", "O").Replace("Ù", "U");
        return Regex.Replace(normalized, @"[^A-Z0-9]", "");
    }
}
