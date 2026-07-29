using System.Text.RegularExpressions;

namespace MichMapper;

internal sealed record ShareholderRow(
    string SourceFile,
    string Shareholder,
    string FiscalCode,
    string Percentage,
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
        new(@"(?<!\d)\d{11}(?!\d)",
            RegexOptions.Compiled);

    private static readonly Regex Percentage =
        new(@"(?<!\d)(\d{1,3}(?:[\,\.]\d+)?)\s*%",
            RegexOptions.Compiled);

    private static readonly Regex Year =
        new(@"\b(20\d{2})\b",
            RegexOptions.Compiled);

    public IReadOnlyList<ShareholderRow> ExtractShareholders(
        CervedRecord record)
    {
        string[] aliases =
        [
            "SOCI",
            "ASSETTO PROPRIETARIO",
            "SOCI E TITOLARI DI DIRITTI",
            "PARTECIPAZIONI RILEVANTI",
            "TITOLARI DI CARICHE O QUALIFICHE"
        ];

        BookmarkSection? bookmark =
            _navigator.FindBestSection(
                record.BookmarkSections,
                aliases);

        IReadOnlyList<PageText> pages =
            bookmark is not null
                ? record.Pages
                    .Where(page => bookmark.ContainsPage(page.Number))
                    .ToArray()
                : FindFallbackPages(record, aliases);

        string method = bookmark is not null
            ? "Segnalibro PDF"
            : "Fallback per titolo";

        var result = new List<ShareholderRow>();

        foreach (PageText page in pages)
        {
            string[] lines = Lines(page.Text);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                bool ownershipLine =
                    line.Contains(
                        "SOCIO",
                        StringComparison.OrdinalIgnoreCase) ||
                    line.Contains(
                        "QUOTE:",
                        StringComparison.OrdinalIgnoreCase) ||
                    line.Contains(
                        "PROPRIETA",
                        StringComparison.OrdinalIgnoreCase) ||
                    Percentage.IsMatch(line);

                if (!ownershipLine)
                    continue;

                string window = string.Join(
                    " | ",
                    lines.Skip(Math.Max(0, i - 4))
                        .Take(10));

                string shareholder =
                    FindLikelyEntity(lines, i);

                if (string.IsNullOrWhiteSpace(shareholder))
                    continue;

                result.Add(new ShareholderRow(
                    record.SourceFile,
                    shareholder,
                    FindFiscalCode(window),
                    Percentage.Match(window).Groups[1].Value,
                    FindRightType(window),
                    bookmark?.Title ?? "Fallback",
                    page.Number,
                    Limit(window, 1000),
                    method));
            }
        }

        return result
            .GroupBy(
                item =>
                    $"{item.Shareholder}|{item.Percentage}|{item.RightType}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public IReadOnlyList<OfficerRow> ExtractOfficers(
        CervedRecord record)
    {
        string[] aliases =
        [
            "TITOLARI DI CARICHE O QUALIFICHE",
            "CARICHE/QUALIFICHE GESTIONALI",
            "ESPONENTI - CARICHE",
            "CARICHE"
        ];

        BookmarkSection? bookmark =
            _navigator.FindBestSection(
                record.BookmarkSections,
                aliases);

        IReadOnlyList<PageText> pages =
            bookmark is not null
                ? record.Pages
                    .Where(page => bookmark.ContainsPage(page.Number))
                    .ToArray()
                : FindFallbackPages(record, aliases);

        string method = bookmark is not null
            ? "Segnalibro PDF"
            : "Fallback per titolo";

        var result = new List<OfficerRow>();

        foreach (PageText page in pages)
        {
            string[] lines = Lines(page.Text);

            for (int i = 0; i < lines.Length; i++)
            {
                string role = FindRole(lines[i]);

                if (string.IsNullOrWhiteSpace(role))
                    continue;

                string window = string.Join(
                    " | ",
                    lines.Skip(Math.Max(0, i - 6))
                        .Take(12));

                string name =
                    FindLikelyPerson(lines, i);

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                result.Add(new OfficerRow(
                    record.SourceFile,
                    name,
                    PersonFiscalCode.Match(window)
                        .Value
                        .ToUpperInvariant(),
                    role,
                    bookmark?.Title ?? "Fallback",
                    page.Number,
                    Limit(window, 1000),
                    method));
            }
        }

        return result
            .GroupBy(
                item => $"{item.Name}|{item.Role}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public IReadOnlyList<BalanceRow> ExtractBalance(
        CervedRecord record)
    {
        string[] aliases =
        [
            "BILANCIO",
            "ANALISI DI BILANCIO",
            "DATI DI BILANCIO"
        ];

        BookmarkSection? bookmark =
            _navigator.FindBestSection(
                record.BookmarkSections,
                aliases);

        IReadOnlyList<PageText> pages =
            bookmark is not null
                ? record.Pages
                    .Where(page => bookmark.ContainsPage(page.Number))
                    .ToArray()
                : FindFallbackPages(record, aliases);

        string method = bookmark is not null
            ? "Segnalibro PDF"
            : "Fallback per titolo";

        var result = new List<BalanceRow>();

        string combined = string.Join(
            "\n",
            pages.Select(page => page.Text));

        string[] lines = Lines(combined);

        string yearsLine =
            lines.FirstOrDefault(
                line => Year.Matches(line).Count >= 2)
            ?? "";

        string[] years =
            Year.Matches(yearsLine)
                .Select(match => match.Value)
                .Distinct()
                .TakeLast(3)
                .ToArray();

        if (years.Length == 0)
            years = ["Ultimo esercizio"];

        string revenueLine =
            FindMetricLine(lines, "RICAVI NETTI");

        string ebitdaLine =
            FindMetricLine(
                lines,
                "MARGINE OPERATIVO LORDO");

        string netIncomeLine =
            FindMetricLine(
                lines,
                "UTILE (PERDITA) DELL'ESERCIZIO");

        string assetsLine =
            FindMetricLine(lines, "ATTIVO");

        string equityLine =
            FindMetricLine(
                lines,
                "PATRIMONIO NETTO");

        string cashFlowLine =
            FindMetricLine(lines, "CASH FLOW");

        string[] revenue =
            ExtractLastNumbers(revenueLine, years.Length);

        string[] ebitda =
            ExtractLastNumbers(ebitdaLine, years.Length);

        string[] netIncome =
            ExtractLastNumbers(netIncomeLine, years.Length);

        string[] assets =
            ExtractLastNumbers(assetsLine, years.Length);

        string[] equity =
            ExtractLastNumbers(equityLine, years.Length);

        string[] cashFlow =
            ExtractLastNumbers(cashFlowLine, years.Length);

        int evidencePage =
            pages.FirstOrDefault()?.Number ?? 0;

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
            1500);

        for (int i = 0; i < years.Length; i++)
        {
            result.Add(new BalanceRow(
                record.SourceFile,
                years[i],
                ValueAt(revenue, i),
                ValueAt(ebitda, i),
                ValueAt(netIncome, i),
                ValueAt(assets, i),
                ValueAt(equity, i),
                ValueAt(cashFlow, i),
                bookmark?.Title ?? "Fallback",
                evidencePage,
                evidence,
                method));
        }

        return result;
    }

    private static IReadOnlyList<PageText> FindFallbackPages(
        CervedRecord record,
        IReadOnlyList<string> aliases)
    {
        return record.Pages
            .Where(page =>
                aliases.Any(alias =>
                    Normalize(page.Text)
                        .Contains(
                            Normalize(alias),
                            StringComparison.Ordinal)))
            .ToArray();
    }

    private static string FindMetricLine(
        IReadOnlyList<string> lines,
        string metric)
    {
        return lines.FirstOrDefault(
            line =>
                Normalize(line).StartsWith(
                    Normalize(metric),
                    StringComparison.Ordinal))
            ?? "";
    }

    private static string[] ExtractLastNumbers(
        string line,
        int count)
    {
        if (string.IsNullOrWhiteSpace(line))
            return [];

        MatchCollection matches = Regex.Matches(
            line,
            @"[-+]?\d{1,3}(?:\.\d{3})*(?:,\d+)?|[-+]?\d+");

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

    private static string FindLikelyEntity(
        IReadOnlyList<string> lines,
        int index)
    {
        for (int i = index;
             i >= Math.Max(0, index - 6);
             i--)
        {
            string candidate =
                CleanNumbering(lines[i]);

            if (candidate.Length < 4 ||
                candidate.Contains(
                    "QUOTE",
                    StringComparison.OrdinalIgnoreCase) ||
                candidate.Contains(
                    "PROPRIETA",
                    StringComparison.OrdinalIgnoreCase) ||
                candidate.Contains(
                    "N. REA",
                    StringComparison.OrdinalIgnoreCase))
                continue;

            if (LooksLikeEntity(candidate))
                return candidate;
        }

        return "";
    }

    private static string FindLikelyPerson(
        IReadOnlyList<string> lines,
        int index)
    {
        for (int i = index;
             i >= Math.Max(0, index - 7);
             i--)
        {
            string candidate =
                CleanNumbering(lines[i]);

            if (candidate.Length < 5 ||
                candidate.Any(char.IsDigit))
                continue;

            if (FindRole(candidate).Length > 0 ||
                candidate.Contains(
                    "TITOLARI",
                    StringComparison.OrdinalIgnoreCase) ||
                candidate.Contains(
                    "CARICHE",
                    StringComparison.OrdinalIgnoreCase))
                continue;

            string[] words = candidate.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);

            if (words.Length is >= 2 and <= 5 &&
                words.All(word =>
                    word.All(character =>
                        char.IsLetter(character) ||
                        character is '\'' or '-')))
                return candidate;
        }

        return "";
    }

    private static string FindFiscalCode(string text)
    {
        Match person =
            PersonFiscalCode.Match(text);

        if (person.Success)
            return person.Value.ToUpperInvariant();

        Match numeric =
            NumericFiscalCode.Match(text);

        return numeric.Success
            ? numeric.Value
            : "";
    }

    private static string FindRightType(string text)
    {
        string upper = Normalize(text);

        if (upper.Contains("NUDA PROPRIETA"))
            return "Nuda proprietà";

        if (upper.Contains("USUFRUTTO"))
            return "Usufrutto";

        if (upper.Contains("PROPRIETA"))
            return "Proprietà";

        if (upper.Contains("SOCIO UNICO"))
            return "Socio unico";

        if (upper.Contains("SOCIO"))
            return "Socio";

        return "";
    }

    private static string FindRole(string line)
    {
        string upper = Normalize(line);

        string[] roles =
        [
            "PRESIDENTE CONSIGLIO AMMINISTRAZIONE",
            "AMMINISTRATORE UNICO",
            "AMMINISTRATORE DELEGATO",
            "CONSIGLIERE DELEGATO",
            "CONSIGLIERE",
            "LIQUIDATORE",
            "PROCURATORE",
            "SOCIO AMMINISTRATORE",
            "SOCIO UNICO",
            "SOCIO"
        ];

        return roles.FirstOrDefault(
            role => upper.Contains(role))
            ?? "";
    }

    private static bool LooksLikeEntity(string value)
    {
        string upper = Normalize(value);

        return upper.Contains("S.R.L") ||
               upper.Contains("S.P.A") ||
               upper.Contains("SOCIETA") ||
               upper.Contains("HOLDING") ||
               upper.Split(
                   ' ',
                   StringSplitOptions.RemoveEmptyEntries)
                   .Length is >= 2 and <= 8;
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
        Regex.Replace(
            value.ToUpperInvariant(),
            @"\s+",
            " ")
        .Trim();

    private static string CleanNumbering(string value) =>
        Regex.Replace(
            value.Trim(),
            @"^\d+[\.\)]\s*",
            "");

    private static string Limit(
        string value,
        int maximum) =>
        value.Length <= maximum
            ? value
            : value[..maximum];
}
