using System.Text.RegularExpressions;

namespace MichMapper;

internal static class CervedFieldResolver
{
    public static string GetAttivitaEconomica(CervedRecord record)
    {
        string current = CleanActivity(record.AttivitaEconomica.Value);

        if (record.DocumentType != CervedDocumentType.Company)
            return current;

        foreach (PageText page in record.Pages.OrderBy(item => item.Number).Take(2))
        {
            string[] lines = page.Text.Split('\n')
                .Select(Clean)
                .Where(line => line.Length > 0)
                .ToArray();

            for (int i = 0; i < lines.Length; i++)
            {
                if (!Normalize(lines[i]).StartsWith("ATTIVITAECONOMICA"))
                    continue;

                var parts = new List<string>();
                AddActivityPart(parts, i > 0 ? lines[i - 1] : "");

                Match label = Regex.Match(
                    lines[i],
                    @"Attivit[aà]\s+Economica",
                    RegexOptions.IgnoreCase);
                string sameLine = label.Success
                    ? Clean(lines[i][(label.Index + label.Length)..])
                    : "";
                if (sameLine.StartsWith("(", StringComparison.Ordinal))
                    sameLine = "";
                AddActivityPart(parts, sameLine);

                for (int j = i + 1; j < Math.Min(lines.Length, i + 4); j++)
                {
                    if (IsFieldLine(lines[j]))
                        break;
                    AddActivityPart(parts, lines[j]);
                }

                string candidate = CleanActivity(string.Join(" ", parts));
                if (candidate.Length > current.Length &&
                    (current.Length == 0 ||
                     Normalize(candidate).Contains(Normalize(current))))
                    return candidate;
            }
        }

        return current;
    }

    private static void AddActivityPart(List<string> parts, string value)
    {
        string cleaned = CleanActivity(value);
        if (cleaned.Length > 2 && !IsFieldLine(cleaned))
            parts.Add(cleaned);
    }

    private static bool IsFieldLine(string value)
    {
        string normalized = Normalize(value);
        return normalized == "GROUP" ||
               normalized.StartsWith("SITUAZIONEIMPRESA") ||
               normalized.StartsWith("DATACOSTITUZIONE") ||
               normalized.StartsWith("DATAISCRIZIONE") ||
               normalized.StartsWith("IMPRESAAPPARTENENTE") ||
               normalized.StartsWith("NOMECAPOGRUPPO") ||
               normalized.StartsWith("CODICEATTIVITA") ||
               normalized.StartsWith("FORMAGIURIDICA");
    }

    private static string CleanActivity(string value)
    {
        string cleaned = Clean(value);
        cleaned = Regex.Replace(cleaned, @"\bCerved\s+Group\)?\b", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+Group\)\s*", " ", RegexOptions.IgnoreCase);
        return Clean(cleaned);
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
