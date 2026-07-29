using System.Text.RegularExpressions;

namespace MichMapper;

internal sealed record ShareholderRow(
    string SourceFile,
    string Owner,
    string ParticipatedCompany,
    string OwnerFiscalCode,
    string ParticipatedCompanyFiscalCode,
    string Percentage,
    string NominalValue,
    string RightType,
    string Bookmark,
    int Page,
    string Evidence,
    string Method);

internal sealed record OfficerRow(
    string SourceFile,
    string Name,
    string FiscalCode,
    string Role,
    string Bookmark,
    int Page,
    string Evidence,
    string Method);

internal sealed record BalanceRow(
    string SourceFile,
    string Year,
    string Revenue,
    string Ebitda,
    string NetIncome,
    string TotalAssets,
    string Equity,
    string CashFlow,
    string Bookmark,
    int Page,
    string Evidence,
    string Method);

internal sealed class CervedAdvancedExtractor
{
    private static readonly Regex FiscalCodePattern =
        new(@"\b(?:[A-Z]{6}\d{2}[A-EHLMPRST]\d{2}[A-Z]\d{3}[A-Z]|\d{11})\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PercentagePattern =
        new(@"(?<!\d)(\d{1,3}(?:[\,\.]\d+)?)\s*%",
            RegexOptions.Compiled);

    private static readonly Regex YearPattern =
        new(@"\b31/12/(20\d{2})\b", RegexOptions.Compiled);

    private static readonly Regex NumberedCompanyPattern =
        new(@"^\s*\d+\.\s+(.+)$", RegexOptions.Compiled);

    public IReadOnlyList<ShareholderRow> ExtractShareholders(CervedRecord record)
    {
        return record.DocumentType switch
        {
            CervedDocumentType.Company => ExtractCompanyShareholders(record),
            CervedDocumentType.Person => ExtractPersonParticipations(record),
            _ => []
        };
    }

    private static IReadOnlyList<ShareholderRow> ExtractCompanyShareholders(
        CervedRecord record)
    {
        BookmarkSection? bookmark = FindBookmark(
            record.BookmarkSections,
            "SOCI",
            ["ARCHIVIO SOCI", "RISULTANTI DA BILANCIO", "SOCI - CARICHE"]);

        if (bookmark is null)
            return [];

        PageText[] pages = PagesInside(record, bookmark);
        string text = string.Join("\n", pages.Select(page => page.Text));
        string[] lines = Lines(text);
        var rows = new List<ShareholderRow>();

        // Parse each percentage as a table row anchor. This avoids the previous
        // list-alignment issue that lost the second shareholder of INDECO/VISACO.
        foreach (Match percentageMatch in PercentagePattern.Matches(text))
        {
            int start = Math.Max(0, percentageMatch.Index - 700);
            int length = Math.Min(
                text.Length - start,
                percentageMatch.Length + 1400);

            string window = text.Substring(start, length);
            string before = text.Substring(
                start,
                percentageMatch.Index - start);
            string after = text.Substring(
                percentageMatch.Index + percentageMatch.Length,
                Math.Min(700, text.Length -
                    (percentageMatch.Index + percentageMatch.Length)));

            string owner = FindOwnerBeforePercentage(before);
            string fiscalCode = FindNearestFiscalCode(after, before);
            string nominal = FindNominalBeforePercentage(before);
            string right = FindRight(after + " " + before);

            if (string.IsNullOrWhiteSpace(owner) ||
                string.IsNullOrWhiteSpace(fiscalCode))
                continue;

            rows.Add(new ShareholderRow(
                record.SourceFile,
                owner,
                record.Denominazione.Value,
                fiscalCode,
                record.CodiceFiscale.Value,
                percentageMatch.Groups[1].Value,
                nominal,
                right,
                bookmark.Title,
                FindEvidencePage(pages, percentageMatch.Groups[0].Value),
                Limit(window.Replace('\n', ' '), 1800),
                "Segnalibro SOCI + riga ancorata alla percentuale"));
        }

        return rows
            .Where(row =>
                row.OwnerFiscalCode != record.CodiceFiscale.Value &&
                !IsNoiseName(row.Owner))
            .GroupBy(
                row => $"{Normalize(row.Owner)}|{row.OwnerFiscalCode}|{row.Percentage}|{Normalize(row.RightType)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static string FindOwnerBeforePercentage(string before)
    {
        string[] lines = Lines(before)
            .TakeLast(18)
            .ToArray();

        // Company owners: choose the latest line containing a legal form.
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string candidate = CleanName(lines[i]);

            if (ContainsLegalForm(candidate) && !IsNoiseName(candidate))
                return TrimAfterLegalForm(candidate);
        }

        // Natural-person owners: choose the name directly before "Nato/a" or CF.
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string normalized = Normalize(lines[i]);

            if (normalized.StartsWith("NATOA") ||
                normalized.StartsWith("NATAA") ||
                normalized.StartsWith("CODICEFISCALE"))
            {
                for (int j = i - 1; j >= Math.Max(0, i - 4); j--)
                {
                    string candidate = CleanName(lines[j]);

                    if (LooksLikePersonName(candidate))
                        return candidate;
                }
            }
        }

        // Structured Cerved label.
        Match labelled = Regex.Match(
            before,
            @"COGNOME\s*/\s*DENOM\.\s*:\s*([A-Z0-9'&\.\-\s]+?)(?=\s+CODICE\s+FISCALE|\s+NOME\s*:)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (labelled.Success)
            return CleanName(labelled.Groups[1].Value);

        return "";
    }

    private static string FindNearestFiscalCode(string after, string before)
    {
        Match afterMatch = FiscalCodePattern.Match(after);
        if (afterMatch.Success)
            return afterMatch.Value.ToUpperInvariant();

        MatchCollection beforeMatches = FiscalCodePattern.Matches(before);
        return beforeMatches.Count > 0
            ? beforeMatches[^1].Value.ToUpperInvariant()
            : "";
    }

    private static string FindNominalBeforePercentage(string before)
    {
        MatchCollection values = Regex.Matches(
            before,
            @"(?<!\d)(\d{1,3}(?:\.\d{3})*(?:,\d{1,2})?)(?!\d)");

        return values.Count > 0
            ? values[^1].Groups[1].Value
            : "";
    }

    private static IReadOnlyList<ShareholderRow> ExtractPersonParticipations(
        CervedRecord record)
    {
        BookmarkSection? bookmark = FindBookmark(
            record.BookmarkSections,
            "PARTECIPAZIONI",
            ["ARCHIVIO SOCI", "RISULTANTI DA BILANCIO"]);

        if (bookmark is null)
            return [];

        PageText[] pages = PagesInside(record, bookmark);
        string[] lines = Lines(string.Join("\n", pages.Select(p => p.Text)));
        var result = new List<ShareholderRow>();

        for (int i = 0; i < lines.Length; i++)
        {
            Match start = NumberedCompanyPattern.Match(lines[i]);
            if (!start.Success)
                continue;

            int end = i + 1;
            while (end < lines.Length &&
                   !NumberedCompanyPattern.IsMatch(lines[end]))
                end++;

            string[] block = lines.Skip(i).Take(end - i).ToArray();
            string evidence = string.Join(" | ", block);
            string company = BuildCompanyName(block, start.Groups[1].Value);
            string companyCf = FindLabelledFiscalCode(evidence);

            MatchCollection percentages = PercentagePattern.Matches(evidence);

            foreach (Match pct in percentages)
            {
                string local = evidence.Substring(
                    pct.Index,
                    Math.Min(220, evidence.Length - pct.Index));

                result.Add(new ShareholderRow(
                    record.SourceFile,
                    CleanPersonRecordName(record.Denominazione.Value),
                    company,
                    record.CodiceFiscale.Value,
                    companyCf,
                    pct.Groups[1].Value,
                    FindNumberAfterPercentage(local),
                    FindRight(local),
                    bookmark.Title,
                    pages.FirstOrDefault()?.Number ?? bookmark.StartPage,
                    Limit(evidence, 1800),
                    "Segnalibro PARTECIPAZIONI + blocco società"));
            }

            i = end - 1;
        }

        return result
            .GroupBy(
                row => $"{Normalize(row.ParticipatedCompany)}|{row.Percentage}|{Normalize(row.RightType)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public IReadOnlyList<OfficerRow> ExtractOfficers(CervedRecord record)
    {
        BookmarkSection? bookmark = FindBookmark(
            record.BookmarkSections,
            "TITOLARI DI CARICHE O QUALIFICHE",
            []);

        if (bookmark is null)
            return [];

        PageText[] pages = PagesInside(record, bookmark);
        string[] lines = Lines(string.Join("\n", pages.Select(p => p.Text)));
        var result = new List<OfficerRow>();

        for (int i = 0; i < lines.Length; i++)
        {
            Match cf = FiscalCodePattern.Match(lines[i]);

            if (!cf.Success || cf.Value.All(char.IsDigit))
                continue;

            string name = FindPersonNameBefore(lines, i);

            if (string.IsNullOrWhiteSpace(name))
                continue;

            int from = Math.Max(0, i - 3);
            int to = Math.Min(lines.Length, i + 18);
            string[] block = lines.Skip(from).Take(to - from).ToArray();

            foreach (string role in ExtractRoles(block))
            {
                result.Add(new OfficerRow(
                    record.SourceFile,
                    name,
                    cf.Value.ToUpperInvariant(),
                    role,
                    bookmark.Title,
                    FindEvidencePage(pages, cf.Value),
                    Limit(string.Join(" | ", block), 1500),
                    "Segnalibro CARICHE + persona identificata dal CF"));
            }
        }

        return result
            .Where(row => !IsNoiseName(row.Name))
            .GroupBy(
                row => $"{Normalize(row.Name)}|{Normalize(row.Role)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public IReadOnlyList<BalanceRow> ExtractBalance(CervedRecord record)
    {
        if (record.DocumentType != CervedDocumentType.Company)
            return [];

        BookmarkSection? bookmark = FindBookmark(
            record.BookmarkSections,
            "BILANCIO",
            []);

        if (bookmark is null)
            return [];

        PageText[] pages = PagesInside(record, bookmark);
        string[] lines = Lines(string.Join("\n", pages.Select(p => p.Text)));

        // Prefer the actual table header, not "Bilanci depositati/disponibili".
        string yearsLine = lines.FirstOrDefault(line =>
            YearPattern.Matches(line).Count >= 2 &&
            !Normalize(line).StartsWith("BILANCIDEPOSITATIDISPONIBILI") &&
            (Normalize(line).Contains("IMPORTIESPRESSI") ||
             Regex.IsMatch(line, @"^\s*31/12/20\d{2}\s+31/12/20\d{2}")))
            ?? "";

        string[] years = YearPattern.Matches(yearsLine)
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .TakeLast(3)
            .ToArray();

        if (years.Length == 0)
            return [];

        string[] revenue = ValuesForMetric(lines, "RICAVI NETTI", years.Length);
        string[] ebitda = ValuesForMetric(lines, "MARGINE OPERATIVO LORDO", years.Length);
        string[] netIncome = ValuesForMetric(lines, "UTILE (PERDITA) DELL'ESERCIZIO", years.Length);
        string[] assets = ValuesForMetric(lines, "ATTIVO", years.Length, exact: true);
        string[] equity = ValuesForMetric(lines, "PATRIMONIO NETTO", years.Length);
        string[] cashFlow = ValuesForMetric(lines, "CASH FLOW", years.Length);

        // OCR/layout artefact guard: a full column of repeated "18" is invalid.
        var columns = new[] { revenue, ebitda, netIncome, assets, equity, cashFlow };
        for (int yearIndex = 0; yearIndex < years.Length; yearIndex++)
        {
            int repeated18 = columns.Count(values =>
                yearIndex < values.Length && values[yearIndex] == "18");

            if (repeated18 >= 4)
            {
                foreach (string[] values in columns)
                {
                    if (yearIndex < values.Length)
                        values[yearIndex] = "";
                }
            }
        }

        var result = new List<BalanceRow>();

        for (int i = 0; i < years.Length; i++)
        {
            result.Add(new BalanceRow(
                record.SourceFile,
                years[i],
                At(revenue, i),
                At(ebitda, i),
                At(netIncome, i),
                At(assets, i),
                At(equity, i),
                At(cashFlow, i),
                bookmark.Title,
                pages.FirstOrDefault()?.Number ?? bookmark.StartPage,
                $"Testata esercizi: {yearsLine}",
                "Segnalibro BILANCIO + testata esercizi reale"));
        }

        return result;
    }

    private static string[] ValuesForMetric(
        IReadOnlyList<string> lines,
        string metric,
        int count,
        bool exact = false)
    {
        string target = Normalize(metric);

        string line = lines.FirstOrDefault(value =>
        {
            string normalized = Normalize(value);

            if (!normalized.StartsWith(target))
                return false;

            if (!exact)
                return true;

            string remainder = normalized[target.Length..];
            return remainder.Length == 0 || char.IsDigit(remainder[0]) ||
                   remainder[0] == '-';
        }) ?? "";

        if (string.IsNullOrWhiteSpace(line))
            return new string[count];

        string[] values = Regex.Matches(
                line,
                @"[-+]?\d{1,3}(?:\.\d{3})*(?:,\d+)?|[-+]?\d+")
            .Select(match => match.Value)
            .TakeLast(count)
            .ToArray();

        if (values.Length == count)
            return values;

        var padded = new string[count];
        Array.Copy(values, 0, padded, count - values.Length, values.Length);
        return padded;
    }

    private static BookmarkSection? FindBookmark(
        IReadOnlyList<BookmarkSection> sections,
        string alias,
        IReadOnlyList<string> excluded)
    {
        IEnumerable<BookmarkSection> allowed = sections.Where(section =>
            !excluded.Any(item =>
                Normalize(section.Title).Contains(Normalize(item))));

        BookmarkSection? exact = allowed.FirstOrDefault(section =>
            Normalize(section.Title) == Normalize(alias));

        return exact ?? allowed
            .Where(section =>
                Normalize(section.Title).Contains(Normalize(alias)))
            .OrderBy(section => section.Title.Length)
            .FirstOrDefault();
    }

    private static PageText[] PagesInside(
        CervedRecord record,
        BookmarkSection bookmark) =>
        record.Pages
            .Where(page => bookmark.ContainsPage(page.Number))
            .ToArray();

    private static string FindPersonNameBefore(
        IReadOnlyList<string> lines,
        int cfIndex)
    {
        for (int i = cfIndex - 1; i >= Math.Max(0, cfIndex - 7); i--)
        {
            string candidate = CleanName(lines[i]);

            if (LooksLikePersonName(candidate) && !IsNoiseName(candidate))
                return candidate;
        }

        return "";
    }

    private static bool LooksLikePersonName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsDigit) ||
            value.Contains(':'))
            return false;

        string[] words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return words.Length is >= 2 and <= 6 &&
               words.All(word =>
                   word.All(character =>
                       char.IsLetter(character) ||
                       character is '\'' or '-'));
    }

    private static string CleanName(string value)
    {
        value = Regex.Replace(
            value,
            @"\s*\((?:rappresentante dell'impresa|socio.*?|beneficiario.*?)\)\s*",
            "",
            RegexOptions.IgnoreCase);

        value = Regex.Replace(value, @"^\s*\d+[\.\)]\s*", "");
        return Regex.Replace(value, @"\s{2,}", " ").Trim();
    }

    private static bool IsNoiseName(string value)
    {
        string normalized = Normalize(value);

        string[] noise =
        [
            "TITOLARIDICARICHEOQUALIFICHE",
            "SOCIOOBENEFICIARIODELVINCOLOSUQUOTEAZIONI",
            "IMPRESAINDIVIDUALE",
            "SOCI",
            "DOSSIER",
            "PRE"
        ];

        return noise.Any(item =>
            normalized == item || normalized.StartsWith(item));
    }

    private static bool ContainsLegalForm(string value)
    {
        string n = Normalize(value);
        return n.Contains("SRL") || n.Contains("SPA") ||
               n.Contains("SOCIETASEMPLICE") ||
               n.Contains("SNC") || n.Contains("SAS");
    }

    private static string TrimAfterLegalForm(string value)
    {
        Match match = Regex.Match(
            value,
            @"^(.+?\b(?:S\.?\s*R\.?\s*L\.?|S\.?\s*P\.?\s*A\.?|SOCIETA'\s+SEMPLICE|S\.?\s*N\.?\s*C\.?|S\.?\s*A\.?\s*S\.?))\b",
            RegexOptions.IgnoreCase);

        return match.Success ? match.Groups[1].Value.Trim() : value.Trim();
    }

    private static string BuildCompanyName(
        IReadOnlyList<string> block,
        string firstLine)
    {
        string result = firstLine.Trim();

        if (ContainsLegalForm(result))
            return TrimAfterLegalForm(result);

        for (int i = 1; i < Math.Min(block.Count, 5); i++)
        {
            string candidate = $"{result} {block[i]}";

            if (ContainsLegalForm(candidate))
                return TrimAfterLegalForm(candidate);

            if (Normalize(block[i]).StartsWith("CODICEFISCALE") ||
                block[i].Any(char.IsDigit))
                break;

            result = candidate;
        }

        return result.Trim();
    }

    private static string CleanPersonRecordName(string value)
    {
        value = Regex.Replace(
            value,
            @"\bCog(?:nome)?\b",
            "",
            RegexOptions.IgnoreCase);

        string[] words = Regex.Replace(value, @"\s{2,}", " ")
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return string.Join(" ", words.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string FindLabelledFiscalCode(string text)
    {
        Match match = Regex.Match(
            text,
            @"CODICE\s+FISCALE\s*:\s*([A-Z0-9]{11,16})",
            RegexOptions.IgnoreCase);

        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : "";
    }

    private static string FindNumberAfterPercentage(string text)
    {
        Match pct = PercentagePattern.Match(text);
        if (!pct.Success)
            return "";

        Match value = Regex.Match(
            text[(pct.Index + pct.Length)..],
            @"(?<!\d)(\d{1,3}(?:\.\d{3})*(?:,\d+)?)(?!\d)");

        return value.Success ? value.Groups[1].Value : "";
    }

    private static string FindRight(string text)
    {
        string n = Normalize(text);

        if (n.Contains("NUDAPROPRIETA")) return "Nuda proprietà";
        if (n.Contains("USUFRUTTO")) return "Usufrutto";
        if (n.Contains("PROPRIETA")) return "Proprietà";
        if (n.Contains("SOCIOUNICO")) return "Socio unico";

        return "";
    }

    private static IReadOnlyList<string> ExtractRoles(
        IReadOnlyList<string> block)
    {
        string text = Normalize(string.Join(" ", block));

        string[] roles =
        [
            "PRESIDENTE CONSIGLIO AMMINISTRAZIONE",
            "AMMINISTRATORE UNICO",
            "AMMINISTRATORE DELEGATO",
            "CONSIGLIERE DELEGATO",
            "CONSIGLIERE",
            "LIQUIDATORE",
            "PROCURATORE",
            "SOCIO AMMINISTRATORE"
        ];

        return roles
            .Where(role => text.Contains(Normalize(role)))
            .Distinct()
            .ToArray();
    }

    private static int FindEvidencePage(
        IReadOnlyList<PageText> pages,
        string token) =>
        pages.FirstOrDefault(page =>
            page.Text.Contains(token, StringComparison.OrdinalIgnoreCase))
        ?.Number ?? pages.FirstOrDefault()?.Number ?? 0;

    private static string At(IReadOnlyList<string> values, int index) =>
        index >= 0 && index < values.Count ? values[index] : "";

    private static string[] Lines(string text) =>
        text.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Select(value => Regex.Replace(value, @"\s{2,}", " ").Trim())
            .Where(value => value.Length > 0)
            .ToArray();

    private static string Normalize(string value) =>
        new(value.ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];
}
