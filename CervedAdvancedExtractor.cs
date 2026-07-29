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
    private readonly CervedBookmarkNavigator _navigator = new();

    private static readonly Regex FiscalCodePattern =
        new(@"\b(?:[A-Z]{6}\d{2}[A-EHLMPRST]\d{2}[A-Z]\d{3}[A-Z]|\d{11})\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PercentagePattern =
        new(@"(?<!\d)(\d{1,3}(?:[\,\.]\d+)?)\s*%",
            RegexOptions.Compiled);

    private static readonly Regex MoneyPattern =
        new(@"(?<!\d)(\d{1,3}(?:\.\d{3})*(?:,\d{1,2})?)(?!\d)",
            RegexOptions.Compiled);

    private static readonly Regex NumberedCompanyPattern =
        new(@"^\s*\d+\.\s+(.+)$", RegexOptions.Compiled);

    private static readonly Regex YearPattern =
        new(@"\b31/12/(20\d{2})\b", RegexOptions.Compiled);

    public IReadOnlyList<ShareholderRow> ExtractShareholders(CervedRecord record)
    {
        return record.DocumentType switch
        {
            CervedDocumentType.Company => ExtractCompanyOwners(record),
            CervedDocumentType.Person => ExtractPersonParticipations(record),
            _ => []
        };
    }

    private IReadOnlyList<ShareholderRow> ExtractCompanyOwners(CervedRecord record)
    {
        BookmarkSection? bookmark = FindBookmark(
            record.BookmarkSections,
            ["SOCI"],
            excludedTitles:
            [
                "PARTECIPAZIONI DA ARCHIVIO SOCI",
                "PARTECIPAZIONI RISULTANTI DA BILANCIO",
                "SOCI - CARICHE"
            ]);

        if (bookmark is null)
            return [];

        IReadOnlyList<PageText> pages = PagesInside(record, bookmark);
        string[] lines = Lines(string.Join("\n", pages.Select(x => x.Text)));

        int tableStart = FindLine(lines, "SOCIO O BENEFICIARIO");
        if (tableStart < 0)
            tableStart = FindLine(lines, "TIPO DIRITTO");
        if (tableStart < 0)
            tableStart = 0;

        int tableEnd = FindFirstLineAfter(
            lines,
            tableStart,
            [
                "PARTECIPAZIONI DA ARCHIVIO SOCI",
                "PARTECIPAZIONI RISULTANTI DA BILANCIO",
                "INFORMAZIONI IMMOBILIARI",
                "SOCI - CARICHE"
            ]);

        if (tableEnd < 0)
            tableEnd = lines.Length;

        string[] table = lines[tableStart..tableEnd];

        List<string> fiscalCodes = table
            .SelectMany(line => FiscalCodePattern.Matches(line).Select(m => m.Value.ToUpperInvariant()))
            .Where(cf => cf != record.CodiceFiscale.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<string> percentages = table
            .SelectMany(line => PercentagePattern.Matches(line).Select(m => m.Groups[1].Value))
            .ToList();

        List<string> rights = ExtractRights(table);
        List<string> names = ExtractOwnerNames(table);

        var rows = new List<ShareholderRow>();

        int baseCount = Math.Min(
            fiscalCodes.Count,
            Math.Min(names.Count, percentages.Count));

        for (int i = 0; i < baseCount; i++)
        {
            string right = i < rights.Count ? rights[i] : "";
            string nominal = FindNominalValueNear(table, fiscalCodes[i], percentages[i]);

            rows.Add(NewOwnerRow(
                record,
                bookmark,
                pages,
                names[i],
                fiscalCodes[i],
                percentages[i],
                nominal,
                right,
                table,
                "Segnalibro SOCI + allineamento colonne Cerved"));
        }

        // Gestione delle righe aggiuntive di usufrutto/nuda proprietà.
        // Cerved può riportare una seconda serie di nominativi e percentuali
        // dopo le righe principali della tabella.
        if (percentages.Count > baseCount && names.Count > baseCount)
        {
            int extraCount = Math.Min(
                percentages.Count - baseCount,
                names.Count - baseCount);

            for (int i = 0; i < extraCount; i++)
            {
                int index = baseCount + i;
                string name = names[index];
                string percentage = percentages[index];
                string right = index < rights.Count ? rights[index] : "";

                string fiscalCode = ResolveFiscalCodeByName(
                    name,
                    names,
                    fiscalCodes);

                rows.Add(NewOwnerRow(
                    record,
                    bookmark,
                    pages,
                    name,
                    fiscalCode,
                    percentage,
                    "",
                    right,
                    table,
                    "Segnalibro SOCI + diritto aggiuntivo Cerved"));
            }
        }

        // Fallback per società con socio unico: se la tabella testuale viene
        // ricostruita in ordine diverso, cerca comunque il blocco con 100%.
        if (rows.Count == 0)
        {
            rows.AddRange(ParseSimpleCompanyOwnerBlocks(
                record,
                bookmark,
                pages,
                table));
        }

        return rows
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.Owner) &&
                !string.IsNullOrWhiteSpace(x.Percentage))
            .GroupBy(
                x => $"{Normalize(x.Owner)}|{x.OwnerFiscalCode}|{x.Percentage}|{Normalize(x.RightType)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }

    private static ShareholderRow NewOwnerRow(
        CervedRecord record,
        BookmarkSection bookmark,
        IReadOnlyList<PageText> pages,
        string owner,
        string ownerFiscalCode,
        string percentage,
        string nominalValue,
        string rightType,
        IReadOnlyList<string> evidenceLines,
        string method)
    {
        return new ShareholderRow(
            record.SourceFile,
            CleanOwnerName(owner),
            record.Denominazione.Value,
            ownerFiscalCode,
            record.CodiceFiscale.Value,
            percentage,
            nominalValue,
            rightType,
            bookmark.Title,
            pages.FirstOrDefault()?.Number ?? bookmark.StartPage,
            Limit(string.Join(" | ", evidenceLines), 2200),
            method);
    }

    private static IReadOnlyList<ShareholderRow> ParseSimpleCompanyOwnerBlocks(
        CervedRecord record,
        BookmarkSection bookmark,
        IReadOnlyList<PageText> pages,
        IReadOnlyList<string> lines)
    {
        var result = new List<ShareholderRow>();

        for (int i = 0; i < lines.Count; i++)
        {
            Match percentage = PercentagePattern.Match(lines[i]);
            if (!percentage.Success)
                continue;

            int from = Math.Max(0, i - 16);
            int to = Math.Min(lines.Count, i + 8);
            string[] block = lines.Skip(from).Take(to - from).ToArray();
            string text = string.Join(" | ", block);

            Match cf = FiscalCodePattern.Match(text);
            string owner = FindBestOwnerName(block);

            if (string.IsNullOrWhiteSpace(owner))
                continue;

            result.Add(NewOwnerRow(
                record,
                bookmark,
                pages,
                owner,
                cf.Success ? cf.Value.ToUpperInvariant() : "",
                percentage.Groups[1].Value,
                FindNominalValue(block),
                FindRight(text),
                block,
                "Segnalibro SOCI + fallback blocco quota"));
        }

        return result;
    }

    private IReadOnlyList<ShareholderRow> ExtractPersonParticipations(CervedRecord record)
    {
        BookmarkSection? bookmark = FindBookmark(
            record.BookmarkSections,
            ["PARTECIPAZIONI"],
            excludedTitles:
            [
                "PARTECIPAZIONI DA ARCHIVIO SOCI",
                "PARTECIPAZIONI RISULTANTI DA BILANCIO"
            ]);

        if (bookmark is null)
            return [];

        IReadOnlyList<PageText> pages = PagesInside(record, bookmark);
        string[] lines = Lines(string.Join("\n", pages.Select(x => x.Text)));
        var rows = new List<ShareholderRow>();

        for (int i = 0; i < lines.Length; i++)
        {
            Match companyStart = NumberedCompanyPattern.Match(lines[i]);

            if (!companyStart.Success)
                continue;

            int end = i + 1;
            while (end < lines.Length &&
                   !NumberedCompanyPattern.IsMatch(lines[end]))
                end++;

            string[] block = lines[i..end];
            string evidence = string.Join(" | ", block);

            string company = BuildCompanyName(block, companyStart.Groups[1].Value);
            string companyCf = FindLabelledFiscalCode(evidence);
            Match percentage = PercentagePattern.Match(evidence);

            if (string.IsNullOrWhiteSpace(company) || !percentage.Success)
            {
                i = end - 1;
                continue;
            }

            rows.Add(new ShareholderRow(
                record.SourceFile,
                record.Denominazione.Value,
                company,
                record.CodiceFiscale.Value,
                companyCf,
                percentage.Groups[1].Value,
                FindNominalValue(block),
                FindRight(evidence),
                bookmark.Title,
                pages.FirstOrDefault(p => p.Text.Contains(lines[i]))?.Number
                    ?? bookmark.StartPage,
                Limit(evidence, 1800),
                "Segnalibro PARTECIPAZIONI + blocco società numerato"));

            i = end - 1;
        }

        return rows
            .GroupBy(
                x => $"{Normalize(x.Owner)}|{Normalize(x.ParticipatedCompany)}|{x.Percentage}|{Normalize(x.RightType)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }

    public IReadOnlyList<OfficerRow> ExtractOfficers(CervedRecord record)
    {
        BookmarkSection? bookmark = FindBookmark(
            record.BookmarkSections,
            ["TITOLARI DI CARICHE O QUALIFICHE", "CARICHE / QUALIFICHE"],
            excludedTitles: []);

        if (bookmark is null)
            return [];

        IReadOnlyList<PageText> pages = PagesInside(record, bookmark);
        string[] lines = Lines(string.Join("\n", pages.Select(x => x.Text)));
        var rows = new List<OfficerRow>();

        for (int i = 0; i < lines.Length; i++)
        {
            Match cf = FiscalCodePattern.Match(lines[i]);
            if (!cf.Success || cf.Value.All(char.IsDigit))
                continue;

            string name = FindPersonNameBefore(lines, i);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            int end = Math.Min(lines.Length, i + 14);
            string[] block = lines[Math.Max(0, i - 3)..end];

            foreach (string role in ExtractRoles(block))
            {
                rows.Add(new OfficerRow(
                    record.SourceFile,
                    name,
                    cf.Value.ToUpperInvariant(),
                    role,
                    bookmark.Title,
                    pages.FirstOrDefault()?.Number ?? bookmark.StartPage,
                    Limit(string.Join(" | ", block), 1400),
                    "Segnalibro CARICHE + blocco nominativo"));
            }
        }

        return rows
            .GroupBy(
                x => $"{Normalize(x.Name)}|{Normalize(x.Role)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }

    public IReadOnlyList<BalanceRow> ExtractBalance(CervedRecord record)
    {
        if (record.DocumentType != CervedDocumentType.Company)
            return [];

        BookmarkSection? bookmark = FindBookmark(
            record.BookmarkSections,
            ["BILANCIO"],
            excludedTitles: []);

        if (bookmark is null)
            return [];

        IReadOnlyList<PageText> pages = PagesInside(record, bookmark);
        string[] lines = Lines(string.Join("\n", pages.Select(x => x.Text)));

        string yearsLine = lines.FirstOrDefault(x =>
            YearPattern.Matches(x).Count >= 2) ?? "";

        string[] years = YearPattern.Matches(yearsLine)
            .Select(x => x.Groups[1].Value)
            .Distinct()
            .TakeLast(3)
            .ToArray();

        if (years.Length == 0)
            return [];

        string revenue = FindMetric(lines, ["RICAVI NETTI", "RICAVI NETTI BENI E SERVIZI"]);
        string ebitda = FindMetric(lines, ["MARGINE OPERATIVO LORDO"]);
        string netIncome = FindMetric(lines, ["UTILE (PERDITA) DELL'ESERCIZIO"]);
        string assets = FindMetric(lines, ["ATTIVO"]);
        string equity = FindMetric(lines, ["PATRIMONIO NETTO"]);
        string cashFlow = FindMetric(lines, ["CASH FLOW"]);

        string[] revenueValues = LastValues(revenue, years.Length);
        string[] ebitdaValues = LastValues(ebitda, years.Length);
        string[] netIncomeValues = LastValues(netIncome, years.Length);
        string[] assetValues = LastValues(assets, years.Length);
        string[] equityValues = LastValues(equity, years.Length);
        string[] cashFlowValues = LastValues(cashFlow, years.Length);

        string evidence = Limit(string.Join(" | ",
            new[] { yearsLine, revenue, ebitda, netIncome, assets, equity, cashFlow }
                .Where(x => !string.IsNullOrWhiteSpace(x))), 2000);

        var result = new List<BalanceRow>();

        for (int i = 0; i < years.Length; i++)
        {
            result.Add(new BalanceRow(
                record.SourceFile,
                years[i],
                At(revenueValues, i),
                At(ebitdaValues, i),
                At(netIncomeValues, i),
                At(assetValues, i),
                At(equityValues, i),
                At(cashFlowValues, i),
                bookmark.Title,
                pages.FirstOrDefault()?.Number ?? bookmark.StartPage,
                evidence,
                "Segnalibro BILANCIO + righe tabella Cerved"));
        }

        return result;
    }

    private static BookmarkSection? FindBookmark(
        IReadOnlyList<BookmarkSection> sections,
        IReadOnlyList<string> aliases,
        IReadOnlyList<string> excludedTitles)
    {
        IEnumerable<BookmarkSection> allowed = sections.Where(section =>
            !excludedTitles.Any(excluded =>
                Normalize(section.Title).Contains(Normalize(excluded))));

        foreach (string alias in aliases)
        {
            BookmarkSection? exact = allowed.FirstOrDefault(section =>
                Normalize(section.Title) == Normalize(alias));

            if (exact is not null)
                return exact;
        }

        return allowed
            .Where(section => aliases.Any(alias =>
                Normalize(section.Title).Contains(Normalize(alias))))
            .OrderBy(section => section.Title.Length)
            .FirstOrDefault();
    }

    private static IReadOnlyList<PageText> PagesInside(
        CervedRecord record,
        BookmarkSection bookmark) =>
        record.Pages
            .Where(page => bookmark.ContainsPage(page.Number))
            .ToArray();

    private static List<string> ExtractOwnerNames(IReadOnlyList<string> lines)
    {
        var names = new List<string>();

        foreach (string raw in lines)
        {
            string line = CleanOwnerName(raw);

            if (!LooksLikeOwnerName(line))
                continue;

            if (names.Count == 0 ||
                !string.Equals(names[^1], line, StringComparison.OrdinalIgnoreCase))
                names.Add(line);
        }

        return names;
    }

    private static bool LooksLikeOwnerName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length < 5 ||
            value.Length > 110 ||
            value.Any(char.IsDigit) ||
            value.Contains(':') ||
            PercentagePattern.IsMatch(value))
            return false;

        string n = Normalize(value);

        string[] excluded =
        [
            "SOCI", "CAPITALESOCIALE", "DATAATTO", "DATADEPOSITO",
            "DATAPROTOCOLLO", "NUMEROPROTOCOLLO", "SOCIOOBENEFICIARIO",
            "CODICEFISCALE", "QUOTE", "TIPODIRITTO", "PROPRIETA",
            "USUFRUTTO", "NUDAPROPRIETA", "SITUAZIONEIMPRESA",
            "ATTIVITA", "INTERROGAZIONI", "CAPOGRUPPO", "AVVERTENZA",
            "SOCIETAARESPONSABILITALIMITATA", "SOCIETAPERAZIONI",
            "VIA", "BARI", "DOSSIER", "PAG"
        ];

        if (excluded.Any(x => n == x || n.StartsWith(x)))
            return false;

        string[] words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        bool allUpper = words.All(word =>
            word.All(c => !char.IsLetter(c) || char.IsUpper(c)));

        return allUpper && words.Length is >= 2 and <= 12;
    }

    private static List<string> ExtractRights(IReadOnlyList<string> lines)
    {
        var result = new List<string>();

        for (int i = 0; i < lines.Count; i++)
        {
            string normalized = Normalize(lines[i]);

            if (normalized.Contains("NUDAPROPRIETA"))
                result.Add("Nuda proprietà");
            else if (normalized.Contains("USUFRUTTO"))
                result.Add("Usufrutto");
            else if (normalized == "PROPRIETA" || normalized.Contains("PROPRIETA"))
                result.Add("Proprietà");
        }

        return result;
    }

    private static string ResolveFiscalCodeByName(
        string name,
        IReadOnlyList<string> names,
        IReadOnlyList<string> fiscalCodes)
    {
        int firstIndex = names
            .Select((value, index) => new { value, index })
            .FirstOrDefault(x =>
                Normalize(x.value) == Normalize(name))
            ?.index ?? -1;

        return firstIndex >= 0 && firstIndex < fiscalCodes.Count
            ? fiscalCodes[firstIndex]
            : "";
    }

    private static string FindNominalValueNear(
        IReadOnlyList<string> lines,
        string fiscalCode,
        string percentage)
    {
        int cfIndex = lines
            .Select((line, index) => new { line, index })
            .FirstOrDefault(x => x.line.Contains(
                fiscalCode,
                StringComparison.OrdinalIgnoreCase))
            ?.index ?? -1;

        if (cfIndex < 0)
            return "";

        int to = Math.Min(lines.Count, cfIndex + 8);
        string[] block = lines.Skip(cfIndex).Take(to - cfIndex).ToArray();

        return FindNominalValue(block, percentage);
    }

    private static string FindNominalValue(
        IReadOnlyList<string> lines,
        string percentageToExclude = "")
    {
        foreach (string line in lines)
        {
            foreach (Match match in MoneyPattern.Matches(line))
            {
                string value = match.Groups[1].Value;

                if (!string.IsNullOrWhiteSpace(percentageToExclude) &&
                    value == percentageToExclude)
                    continue;

                if (value.Length >= 3)
                    return value;
            }
        }

        return "";
    }

    private static string FindBestOwnerName(IReadOnlyList<string> block) =>
        block
            .Select(CleanOwnerName)
            .FirstOrDefault(LooksLikeOwnerName) ?? "";

    private static string BuildCompanyName(
        IReadOnlyList<string> block,
        string firstLine)
    {
        var parts = new List<string> { firstLine.Trim() };

        for (int i = 1; i < Math.Min(block.Count, 5); i++)
        {
            string line = block[i];
            string n = Normalize(line);

            if (n.StartsWith("CODICEFISCALE") ||
                n.StartsWith("NREA") ||
                n.StartsWith("VIA") ||
                n.StartsWith("SITUAZIONEIMPRESA") ||
                line.Any(char.IsDigit))
                break;

            parts.Add(line);
        }

        return Regex.Replace(string.Join(" ", parts), @"\s{2,}", " ").Trim();
    }

    private static string FindLabelledFiscalCode(string text)
    {
        Match match = Regex.Match(
            text,
            @"CODICE\s+FISCALE\s*:\s*([A-Z0-9]{11,16})",
            RegexOptions.IgnoreCase);

        return match.Success
            ? match.Groups[1].Value.ToUpperInvariant()
            : "";
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

    private static string FindPersonNameBefore(
        IReadOnlyList<string> lines,
        int index)
    {
        for (int i = index - 1; i >= Math.Max(0, index - 5); i--)
        {
            string value = CleanOwnerName(lines[i]);

            if (LooksLikeOwnerName(value) &&
                !ContainsLegalForm(value))
                return value;
        }

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

    private static bool ContainsLegalForm(string value)
    {
        string n = Normalize(value);

        return n.Contains("SRL") ||
               n.Contains("SPA") ||
               n.Contains("SOCIETA");
    }

    private static string FindMetric(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> aliases)
    {
        foreach (string alias in aliases)
        {
            string normalizedAlias = Normalize(alias);

            string? line = lines.FirstOrDefault(value =>
            {
                string normalized = Normalize(value);

                if (!normalized.StartsWith(normalizedAlias))
                    return false;

                string remainder = normalized[normalizedAlias.Length..];

                return remainder.Length == 0 ||
                       char.IsDigit(remainder[0]) ||
                       remainder[0] == '-';
            });

            if (line is not null)
                return line;
        }

        return "";
    }

    private static string[] LastValues(string line, int count)
    {
        if (string.IsNullOrWhiteSpace(line))
            return [];

        return Regex.Matches(
                line,
                @"[-+]?\d{1,3}(?:\.\d{3})*(?:,\d+)?|[-+]?\d+")
            .Select(x => x.Value)
            .TakeLast(count)
            .ToArray();
    }

    private static string At(IReadOnlyList<string> values, int index) =>
        index >= 0 && index < values.Count ? values[index] : "";

    private static int FindLine(IReadOnlyList<string> lines, string text)
    {
        string target = Normalize(text);

        for (int i = 0; i < lines.Count; i++)
        {
            if (Normalize(lines[i]).Contains(target))
                return i;
        }

        return -1;
    }

    private static int FindFirstLineAfter(
        IReadOnlyList<string> lines,
        int start,
        IReadOnlyList<string> texts)
    {
        for (int i = start + 1; i < lines.Count; i++)
        {
            string normalized = Normalize(lines[i]);

            if (texts.Any(text =>
                normalized.Contains(Normalize(text))))
                return i;
        }

        return -1;
    }

    private static string CleanOwnerName(string value)
    {
        value = Regex.Replace(value, @"^\s*\d+[\.\)]\s*", "");
        value = Regex.Replace(value, @"\s*\(\d+\)\s*$", "");
        value = Regex.Replace(value, @"\s*\(SOCIO.*?\)\s*$",
            "", RegexOptions.IgnoreCase);

        return Regex.Replace(value, @"\s{2,}", " ").Trim();
    }

    private static string[] Lines(string text) =>
        text.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Select(value =>
                Regex.Replace(value, @"\s{2,}", " ").Trim())
            .Where(value => value.Length > 0)
            .ToArray();

    private static string Normalize(string value) =>
        new(value
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

    private static string Limit(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
