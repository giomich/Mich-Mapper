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

    private static readonly Regex PersonFiscalCode =
        new(@"\b[A-Z]{6}\d{2}[A-EHLMPRST]\d{2}[A-Z]\d{3}[A-Z]\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NumericFiscalCode =
        new(@"(?<!\d)\d{11}(?!\d)", RegexOptions.Compiled);

    private static readonly Regex Percentage =
        new(@"(?<!\d)(\d{1,3}(?:[\,\.]\d+)?)\s*%",
            RegexOptions.Compiled);

    private static readonly Regex QuotaValue =
        new(@"(?:QUOTA|VALORE NOMINALE)\s*:\s*([\d\.\,]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ParticipationStart =
        new(@"^\s*(\d+)\.\s+(.+)$", RegexOptions.Compiled);

    private static readonly Regex YearDate =
        new(@"\b31/12/(20\d{2})\b", RegexOptions.Compiled);

    public IReadOnlyList<ShareholderRow> ExtractShareholders(
        CervedRecord record)
    {
        return record.DocumentType switch
        {
            CervedDocumentType.Company =>
                ExtractCompanyShareholders(record),

            CervedDocumentType.Person =>
                ExtractPersonParticipations(record),

            _ => []
        };
    }

    private IReadOnlyList<ShareholderRow> ExtractCompanyShareholders(
        CervedRecord record)
    {
        BookmarkSection? bookmark =
            FindExactPreferredBookmark(
                record.BookmarkSections,
                "SOCI");

        if (bookmark is null)
            return [];

        IReadOnlyList<PageText> pages =
            PagesInside(record, bookmark);

        var rows = new List<ShareholderRow>();

        foreach (PageText page in pages)
        {
            string[] lines = Lines(page.Text);
            int index = 0;

            while (index < lines.Length)
            {
                int blockStart = FindCompanyShareholderStart(lines, index);

                if (blockStart < 0)
                    break;

                int blockEnd = FindNextCompanyShareholderStart(
                    lines,
                    blockStart + 1);

                if (blockEnd < 0)
                    blockEnd = lines.Length;

                string[] block = lines[blockStart..blockEnd];
                ShareholderRow? parsed =
                    ParseCompanyShareholderBlock(
                        record,
                        bookmark,
                        page.Number,
                        block);

                if (parsed is not null)
                    rows.Add(parsed);

                index = Math.Max(blockEnd, blockStart + 1);
            }
        }

        return rows
            .Where(row =>
                !string.IsNullOrWhiteSpace(row.Owner) &&
                (!string.IsNullOrWhiteSpace(row.Percentage) ||
                 !string.IsNullOrWhiteSpace(row.NominalValue)))
            .GroupBy(
                row =>
                    $"{Normalize(row.Owner)}|{row.OwnerFiscalCode}|{row.Percentage}|{Normalize(row.RightType)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static ShareholderRow? ParseCompanyShareholderBlock(
        CervedRecord record,
        BookmarkSection bookmark,
        int page,
        IReadOnlyList<string> block)
    {
        string evidence = string.Join(" | ", block);

        string fiscalCode = FindFiscalCodeNearLabel(evidence);

        Match percentageMatch = Regex.Match(
            evidence,
            @"PERCENTUALE\s*:\s*(\d{1,3}(?:[\,\.]\d+)?)\s*%",
            RegexOptions.IgnoreCase);

        Match quotaMatch = Regex.Match(
            evidence,
            @"QUOTA\s*:\s*([\d\.\,]+)",
            RegexOptions.IgnoreCase);

        string rightType = FindRightType(evidence);

        string owner = ExtractCompanyShareholderName(
            block,
            evidence,
            fiscalCode);

        if (string.IsNullOrWhiteSpace(owner))
            return null;

        return new ShareholderRow(
            record.SourceFile,
            owner,
            record.Denominazione.Value,
            fiscalCode,
            record.CodiceFiscale.Value,
            percentageMatch.Success
                ? percentageMatch.Groups[1].Value
                : "",
            quotaMatch.Success
                ? quotaMatch.Groups[1].Value
                : "",
            rightType,
            bookmark.Title,
            page,
            Limit(evidence, 1800),
            "Segnalibro Soci + blocco standard Cerved");
    }

    private IReadOnlyList<ShareholderRow> ExtractPersonParticipations(
        CervedRecord record)
    {
        BookmarkSection? bookmark =
            FindExactPreferredBookmark(
                record.BookmarkSections,
                "PARTECIPAZIONI");

        if (bookmark is null)
            return [];

        IReadOnlyList<PageText> pages =
            PagesInside(record, bookmark);

        var rows = new List<ShareholderRow>();

        foreach (PageText page in pages)
        {
            string[] lines = Lines(page.Text);

            for (int i = 0; i < lines.Length; i++)
            {
                Match start = ParticipationStart.Match(lines[i]);

                if (!start.Success)
                    continue;

                int end = i + 1;

                while (end < lines.Length &&
                       !ParticipationStart.IsMatch(lines[end]))
                    end++;

                string[] block = lines[i..end];

                rows.AddRange(
                    ParsePersonParticipationBlock(
                        record,
                        bookmark,
                        page.Number,
                        block));

                i = end - 1;
            }
        }

        return rows
            .Where(row =>
                !string.IsNullOrWhiteSpace(row.ParticipatedCompany) &&
                !string.IsNullOrWhiteSpace(row.Percentage))
            .GroupBy(
                row =>
                    $"{Normalize(row.ParticipatedCompany)}|{row.ParticipatedCompanyFiscalCode}|{row.Percentage}|{Normalize(row.RightType)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static IReadOnlyList<ShareholderRow>
        ParsePersonParticipationBlock(
            CervedRecord record,
            BookmarkSection bookmark,
            int page,
            IReadOnlyList<string> block)
    {
        string evidence = string.Join(" | ", block);

        Match firstLine =
            ParticipationStart.Match(block[0]);

        string company =
            firstLine.Success
                ? firstLine.Groups[2].Value.Trim()
                : block[0].Trim();

        company = ExtendCompanyName(block, company);

        string companyFiscalCode =
            FindFiscalCodeNearLabel(evidence);

        var rights = new List<(string Percentage, string Value, string Right)>();

        foreach (string line in block)
        {
            Match percentageMatch = Percentage.Match(line);

            if (!percentageMatch.Success)
                continue;

            string right = FindRightType(line);

            if (string.IsNullOrWhiteSpace(right))
                right = FindRightType(evidence);

            string nominal = ExtractNominalValueFromParticipationLine(line);

            rights.Add((
                percentageMatch.Groups[1].Value,
                nominal,
                right));
        }

        if (rights.Count == 0)
            return [];

        return rights
            .Select(right =>
                new ShareholderRow(
                    record.SourceFile,
                    record.Denominazione.Value,
                    company,
                    record.CodiceFiscale.Value,
                    companyFiscalCode,
                    right.Percentage,
                    right.Value,
                    right.Right,
                    bookmark.Title,
                    page,
                    Limit(evidence, 1800),
                    "Segnalibro Partecipazioni + blocco numerato Cerved"))
            .ToArray();
    }

    public IReadOnlyList<OfficerRow> ExtractOfficers(
        CervedRecord record)
    {
        string[] aliases =
        [
            "TITOLARI DI CARICHE O QUALIFICHE",
            "CARICHE/QUALIFICHE",
            "CARICHE"
        ];

        BookmarkSection? bookmark =
            _navigator.FindBestSection(
                record.BookmarkSections,
                aliases);

        if (bookmark is null)
            return [];

        IReadOnlyList<PageText> pages =
            PagesInside(record, bookmark);

        var result = new List<OfficerRow>();

        foreach (PageText page in pages)
        {
            string[] lines = Lines(page.Text);

            for (int i = 0; i < lines.Length; i++)
            {
                if (!LooksLikePersonHeader(lines, i))
                    continue;

                string name = CleanPersonHeader(lines[i]);
                int end = Math.Min(lines.Length, i + 14);
                string[] block = lines[i..end];
                string evidence = string.Join(" | ", block);

                string fiscalCode =
                    PersonFiscalCode.Match(evidence)
                        .Value
                        .ToUpperInvariant();

                foreach (string role in ExtractRoles(block))
                {
                    result.Add(new OfficerRow(
                        record.SourceFile,
                        name,
                        fiscalCode,
                        role,
                        bookmark.Title,
                        page.Number,
                        Limit(evidence, 1200),
                        "Segnalibro cariche + blocco persona"));
                }
            }
        }

        return result
            .Where(row =>
                !string.IsNullOrWhiteSpace(row.Name) &&
                !string.IsNullOrWhiteSpace(row.Role))
            .GroupBy(
                row =>
                    $"{Normalize(row.Name)}|{Normalize(row.Role)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public IReadOnlyList<BalanceRow> ExtractBalance(
        CervedRecord record)
    {
        if (record.DocumentType != CervedDocumentType.Company)
            return [];

        BookmarkSection? bookmark =
            FindExactPreferredBookmark(
                record.BookmarkSections,
                "BILANCIO");

        if (bookmark is null)
            return [];

        IReadOnlyList<PageText> pages =
            PagesInside(record, bookmark);

        string combined =
            string.Join("\n", pages.Select(page => page.Text));

        string[] lines = Lines(combined);

        string yearsLine =
            lines.FirstOrDefault(line =>
                YearDate.Matches(line).Count >= 2 &&
                (Normalize(line).Contains("31/12/") ||
                 Normalize(line).Contains("IMPORTI ESPRESSI")))
            ?? "";

        string[] years =
            YearDate.Matches(yearsLine)
                .Select(match => match.Groups[1].Value)
                .Distinct()
                .TakeLast(3)
                .ToArray();

        if (years.Length == 0)
            return [];

        string revenueLine =
            FindExactMetricLine(lines, "RICAVI NETTI");

        string ebitdaLine =
            FindExactMetricLine(lines, "MARGINE OPERATIVO LORDO");

        string netIncomeLine =
            FindExactMetricLine(
                lines,
                "UTILE (PERDITA) DELL'ESERCIZIO");

        string assetsLine =
            FindExactMetricLine(lines, "ATTIVO");

        string equityLine =
            FindExactMetricLine(lines, "PATRIMONIO NETTO");

        string cashFlowLine =
            FindExactMetricLine(lines, "CASH FLOW");

        string[] revenue =
            ExtractMetricValues(revenueLine, years.Length);

        string[] ebitda =
            ExtractMetricValues(ebitdaLine, years.Length);

        string[] netIncome =
            ExtractMetricValues(netIncomeLine, years.Length);

        string[] assets =
            ExtractMetricValues(assetsLine, years.Length);

        string[] equity =
            ExtractMetricValues(equityLine, years.Length);

        string[] cashFlow =
            ExtractMetricValues(cashFlowLine, years.Length);

        string evidence = Limit(
            string.Join(
                " | ",
                new[]
                {
                    yearsLine,
                    revenueLine,
                    ebitdaLine,
                    netIncomeLine,
                    assetsLine,
                    equityLine,
                    cashFlowLine
                }.Where(value =>
                    !string.IsNullOrWhiteSpace(value))),
            1800);

        int page =
            pages.FirstOrDefault()?.Number ?? bookmark.StartPage;

        var rows = new List<BalanceRow>();

        for (int i = 0; i < years.Length; i++)
        {
            rows.Add(new BalanceRow(
                record.SourceFile,
                years[i],
                ValueAt(revenue, i),
                ValueAt(ebitda, i),
                ValueAt(netIncome, i),
                ValueAt(assets, i),
                ValueAt(equity, i),
                ValueAt(cashFlow, i),
                bookmark.Title,
                page,
                evidence,
                "Segnalibro Bilancio + righe tabella standard"));
        }

        return rows;
    }

    private static BookmarkSection? FindExactPreferredBookmark(
        IReadOnlyList<BookmarkSection> sections,
        string exactTitle)
    {
        string exact = Normalize(exactTitle);

        BookmarkSection? match =
            sections.FirstOrDefault(section =>
                Normalize(section.Title) == exact);

        if (match is not null)
            return match;

        return sections
            .Where(section =>
                Normalize(section.Title).Contains(exact))
            .Where(section =>
                !Normalize(section.Title)
                    .Contains("ARCHIVIOSOCI") &&
                !Normalize(section.Title)
                    .Contains("RISULTANTIDABILANCIO") &&
                !Normalize(section.Title)
                    .Contains("ALTREIMPRESE"))
            .OrderBy(section => section.Title.Length)
            .FirstOrDefault();
    }

    private static IReadOnlyList<PageText> PagesInside(
        CervedRecord record,
        BookmarkSection bookmark) =>
        record.Pages
            .Where(page => bookmark.ContainsPage(page.Number))
            .ToArray();

    private static int FindCompanyShareholderStart(
        IReadOnlyList<string> lines,
        int start)
    {
        for (int i = start; i < lines.Count; i++)
        {
            if (LooksLikePersonHeader(lines, i))
                return i;

            if (Normalize(lines[i]).Contains("COGNOME/DENOM") &&
                i > 0)
                return Math.Max(0, i - 1);
        }

        return -1;
    }

    private static int FindNextCompanyShareholderStart(
        IReadOnlyList<string> lines,
        int start) =>
        FindCompanyShareholderStart(lines, start);

    private static bool LooksLikePersonHeader(
        IReadOnlyList<string> lines,
        int index)
    {
        string line = lines[index].Trim();

        if (line.Length < 5 ||
            line.Any(char.IsDigit) ||
            line.Contains(':'))
            return false;

        string[] words =
            line.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);

        if (words.Length is < 2 or > 6)
            return false;

        bool uppercase =
            words.All(word =>
                word.All(character =>
                    !char.IsLetter(character) ||
                    char.IsUpper(character)));

        if (!uppercase)
            return false;

        string next =
            index + 1 < lines.Count
                ? Normalize(lines[index + 1])
                : "";

        return next.StartsWith("NATO A") ||
               next.StartsWith("NATA A") ||
               next.StartsWith("CODICE FISCALE") ||
               next.Contains("RAPPRESENTANTE DELL'IMPRESA");
    }

    private static string CleanPersonHeader(string value)
    {
        int parenthesis = value.IndexOf('(');

        return parenthesis > 0
            ? value[..parenthesis].Trim()
            : value.Trim();
    }

    private static string ExtractCompanyShareholderName(
        IReadOnlyList<string> block,
        string evidence,
        string fiscalCode)
    {
        for (int i = 0; i < block.Count; i++)
        {
            if (LooksLikePersonHeader(block, i))
                return CleanPersonHeader(block[i]);
        }

        Match structured = Regex.Match(
            evidence,
            @"COGNOME\s*/\s*DENOM\.\s*:\s*([A-Z0-9'&\.\-\s]+?)\s+CODICE\s+FISCALE\s*:\s*([A-Z0-9]*)",
            RegexOptions.IgnoreCase);

        if (structured.Success)
        {
            string surnameOrCompany =
                structured.Groups[1].Value.Trim();

            Match name = Regex.Match(
                evidence,
                @"\bNOME\s*:\s*([A-Z'&\.\-\s]+?)(?:\s+DATA\s+DI\s+NASCITA|\s+SESSO|\s+CITTADINANZA|$)",
                RegexOptions.IgnoreCase);

            if (name.Success &&
                !ContainsLegalForm(surnameOrCompany))
            {
                return $"{surnameOrCompany} {name.Groups[1].Value.Trim()}"
                    .Trim();
            }

            return surnameOrCompany;
        }

        return "";
    }

    private static string ExtendCompanyName(
        IReadOnlyList<string> block,
        string firstLineName)
    {
        var parts = new List<string> { firstLineName };

        for (int i = 1; i < Math.Min(block.Count, 4); i++)
        {
            string line = block[i];

            if (ContainsLegalForm(line) ||
                Normalize(line).Contains("QUOTE/AZIONI") ||
                Normalize(line).StartsWith("CODICE FISCALE"))
                break;

            if (line.Any(char.IsDigit))
                break;

            parts.Add(line);
        }

        return Regex.Replace(
            string.Join(" ", parts),
            @"\s{2,}",
            " ")
            .Trim();
    }

    private static bool ContainsLegalForm(string value)
    {
        string normalized = Normalize(value);

        return normalized.Contains("S.R.L") ||
               normalized.Contains("S.P.A") ||
               normalized.Contains("SOCIETAARESPONSABILITALIMITATA") ||
               normalized.Contains("SOCIETAPERAZIONI") ||
               normalized.Contains("SOCIETASEMPLICE") ||
               normalized.Contains("S.N.C") ||
               normalized.Contains("S.A.S");
    }

    private static string FindFiscalCodeNearLabel(string text)
    {
        Match labelled = Regex.Match(
            text,
            @"CODICE\s+FISCALE\s*:\s*([A-Z0-9]{11,16})",
            RegexOptions.IgnoreCase);

        if (labelled.Success)
            return labelled.Groups[1].Value.ToUpperInvariant();

        Match person = PersonFiscalCode.Match(text);

        if (person.Success)
            return person.Value.ToUpperInvariant();

        Match numeric = NumericFiscalCode.Match(text);

        return numeric.Success ? numeric.Value : "";
    }

    private static string ExtractNominalValueFromParticipationLine(
        string line)
    {
        Match percentage = Percentage.Match(line);

        if (!percentage.Success)
            return "";

        string after =
            line[(percentage.Index + percentage.Length)..];

        Match value = Regex.Match(
            after,
            @"\b([\d\.\,]+)\b");

        return value.Success
            ? value.Groups[1].Value
            : "";
    }

    private static IReadOnlyList<string> ExtractRoles(
        IReadOnlyList<string> block)
    {
        string text = Normalize(string.Join(" | ", block));

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

    private static string FindRightType(string text)
    {
        string normalized = Normalize(text);

        if (normalized.Contains("NUDAPROPRIETA"))
            return "Nuda proprietà";

        if (normalized.Contains("USUFRUTTO"))
            return "Usufrutto";

        if (normalized.Contains("PROPRIETA"))
            return "Proprietà";

        if (normalized.Contains("SOCIOUNICO"))
            return "Socio unico";

        return "";
    }

    private static string FindExactMetricLine(
        IReadOnlyList<string> lines,
        string metric)
    {
        string normalizedMetric = Normalize(metric);

        return lines.FirstOrDefault(line =>
        {
            string normalized = Normalize(line);

            if (!normalized.StartsWith(normalizedMetric))
                return false;

            string remainder =
                normalized[normalizedMetric.Length..];

            return remainder.Length == 0 ||
                   char.IsDigit(remainder[0]) ||
                   remainder[0] is '-' or '+';
        }) ?? "";
    }

    private static string[] ExtractMetricValues(
        string line,
        int count)
    {
        if (string.IsNullOrWhiteSpace(line))
            return [];

        MatchCollection matches = Regex.Matches(
            line,
            @"(?<![A-Z])[-+]?\d{1,3}(?:\.\d{3})*(?:,\d+)?|[-+]?\d+");

        return matches
            .Select(match => match.Value)
            .TakeLast(count)
            .ToArray();
    }

    private static string ValueAt(
        IReadOnlyList<string> values,
        int index) =>
        index >= 0 && index < values.Count
            ? values[index]
            : "";

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

    private static string Limit(
        string value,
        int maximum) =>
        value.Length <= maximum
            ? value
            : value[..maximum];
}
