using System.Globalization;
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
    string FinancialIncomeNet,
    string FinancialIncomeGross,
    string TotalRevenue,
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

    private static readonly Regex CompactOwnerRowPattern =
        new(
            @"^(?<name>[A-ZÀ-Ü][A-ZÀ-Ü0-9'&\.\-\s]{2,110}?)\s+" +
            @"(?<cf>[A-Z]{6}\d{2}[A-EHLMPRST]\d{2}[A-Z]\d{3}[A-Z]|\d{11})\s+" +
            @"(?<nominal>\d{1,3}(?:\.\d{3})*(?:,\d{1,2})?)\s+" +
            @"(?<percentage>\d{1,3}(?:[\,\.]\d+)?)%\s+" +
            @"\(?\s*(?<right>NUDA\s+PROPRIETA'|NUDA\s+PROPRIETÀ|USUFRUTTO|PROPRIETA'|PROPRIETÀ|SOCIO\s+UNICO)\s*\)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CompanyParticipationStart =
        new(@"^\s*\d+\.\s+(.+)$", RegexOptions.Compiled);

    private static readonly Regex SectionValueRowPattern =
        new(
            @"(?<cf>[A-Z]{6}\d{2}[A-EHLMPRST]\d{2}[A-Z]\d{3}[A-Z]|\d{11})\s+" +
            @"(?<nominal>\d{1,3}(?:\.\d{3})*(?:,\d{1,2})?)\s+" +
            @"(?<percentage>\d{1,3}(?:[\,\.]\d+)?)\s*%\s*" +
            @"\(?\s*(?<right>NUDA\s+PROPRIETA'|NUDA\s+PROPRIETÀ|USUFRUTTO|PROPRIETA'|PROPRIETÀ|SOCIO\s+UNICO)\s*\)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<ShareholderRow> ExtractShareholders(CervedRecord record)
    {
        // Il foglio SOCI viene alimentato esclusivamente dai DOSSIER TOP
        // delle società. Le partecipazioni dei dossier persona non vengono
        // più esportate, per evitare duplicazioni e ricostruzioni incomplete.
        return record.DocumentType == CervedDocumentType.Company
            ? ExtractCompanyShareholders(record)
            : [];
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

        // Nei dossier Cerved la tabella SOCI può iniziare in fondo alla pagina
        // del segnalibro e proseguire nella pagina successiva. Per questo
        // includiamo fino a due pagine di continuazione e ritagliamo il testo
        // usando i titoli reali delle sezioni.
        PageText[] pages = PagesForShareholderTable(record, bookmark);
        string sectionText = ExtractExactShareholderTableText(pages);

        if (string.IsNullOrWhiteSpace(sectionText))
            return [];

        var rows = new List<ShareholderRow>();

        // La riga dati è l'elemento più stabile della tabella:
        // CF/P.IVA | valore nominale | percentuale | diritto.
        // Il nome può essere sulla stessa riga oppure in un blocco precedente.
        foreach (Match values in SectionValueRowPattern.Matches(sectionText))
        {
            string cf = values.Groups["cf"].Value.ToUpperInvariant();
            string percentage = values.Groups["percentage"].Value;
            string right = NormalizeRight(values.Groups["right"].Value);

            string owner = FindOwnerBeforeValueMatch(
                sectionText,
                values.Index);

            if (string.IsNullOrWhiteSpace(owner))
                continue;

            int evidenceStart = Math.Max(0, values.Index - 1200);
            int evidenceLength = Math.Min(
                sectionText.Length - evidenceStart,
                values.Length + 1600);

            rows.Add(CreateShareholderRow(
                record,
                bookmark,
                pages,
                owner,
                cf,
                percentage,
                values.Groups["nominal"].Value,
                right,
                sectionText.Substring(evidenceStart, evidenceLength)
                    .Replace('\n', ' '),
                "Segnalibro SOCI + tabella delimitata"));
        }

        return rows
            .Where(row =>
                !string.IsNullOrWhiteSpace(row.Owner) &&
                !string.IsNullOrWhiteSpace(row.OwnerFiscalCode) &&
                row.OwnerFiscalCode != record.CodiceFiscale.Value &&
                !IsNoiseName(row.Owner))
            .GroupBy(
                row =>
                    $"{row.OwnerFiscalCode}|{row.Percentage}|{Normalize(row.RightType)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(row => ParsePercentage(row.Percentage))
            .ToArray();
    }

    private static PageText[] PagesForShareholderTable(
        CervedRecord record,
        BookmarkSection bookmark)
    {
        int lastPage = Math.Min(
            record.Pages.Max(page => page.Number),
            bookmark.StartPage + 2);

        return record.Pages
            .Where(page =>
                page.Number >= bookmark.StartPage &&
                page.Number <= lastPage)
            .OrderBy(page => page.Number)
            .ToArray();
    }

    private static string ExtractExactShareholderTableText(
        IReadOnlyList<PageText> pages)
    {
        string text = string.Join(
            "\n",
            pages.Select(page => page.Text));

        // Usiamo il primo titolo SOCI in maiuscolo a partire dalla pagina
        // indicata dal segnalibro. Evitiamo il successivo sottotitolo "Soci"
        // presente nella sezione immobiliare.
        Match start = Regex.Match(
            text,
            @"(?m)^\s*SOCI\s*$");

        if (start.Success)
            text = text[(start.Index + start.Length)..];

        Match end = Regex.Match(
            text,
            @"(?m)^\s*(?:" +
            @"SOCI\s*-\s*CARICHE|" +
            @"PARTECIPAZIONI\s+DA\s+.*ARCHIVIO\s+SOCI|" +
            @"PARTECIPAZIONI\s+R\s*ISULTANTI\s+DA\s+B\s*ILANCIO|" +
            @"INFORMAZIONI\s+IMMOBILIARI" +
            @")\b",
            RegexOptions.IgnoreCase);

        if (end.Success)
            text = text[..end.Index];

        return text.Trim();
    }

    private static ShareholderRow CreateShareholderRow(
        CervedRecord record,
        BookmarkSection bookmark,
        IReadOnlyList<PageText> pages,
        string owner,
        string fiscalCode,
        string percentage,
        string nominal,
        string right,
        string evidence,
        string method)
    {
        return new ShareholderRow(
            record.SourceFile,
            CleanOwnerName(owner),
            record.Denominazione.Value,
            fiscalCode.ToUpperInvariant(),
            record.CodiceFiscale.Value,
            percentage,
            nominal,
            right,
            bookmark.Title,
            FindEvidencePage(pages, fiscalCode),
            Limit(evidence, 2000),
            method);
    }


    private static string FindOwnerBeforeValueMatch(
        string sectionText,
        int valueIndex)
    {
        int lineStart = sectionText.LastIndexOf(
            '\n',
            Math.Max(0, valueIndex - 1));

        lineStart = lineStart < 0 ? 0 : lineStart + 1;

        string sameLinePrefix = CleanOwnerName(
            sectionText.Substring(
                lineStart,
                valueIndex - lineStart));

        if (IsPlausibleOwner(sameLinePrefix) &&
            !LooksLikeAddressContamination(sameLinePrefix))
        {
            return ContainsLegalForm(sameLinePrefix)
                ? TrimAfterLegalForm(sameLinePrefix)
                : sameLinePrefix;
        }

        int windowStart = Math.Max(0, valueIndex - 7000);
        string before = sectionText.Substring(
            windowStart,
            valueIndex - windowStart);

        string[] previousLines = Lines(before);

        // Prima cerchiamo una denominazione societaria completa.
        for (int i = previousLines.Length - 1;
             i >= Math.Max(0, previousLines.Length - 90);
             i--)
        {
            string candidate = CleanOwnerName(previousLines[i]);

            if (ContainsLegalForm(candidate) &&
                IsPlausibleOwner(candidate) &&
                !LooksLikeAddressContamination(candidate))
            {
                return TrimAfterLegalForm(candidate);
            }
        }

        // Se non è una società, cerchiamo il nominativo della persona.
        for (int i = previousLines.Length - 1;
             i >= Math.Max(0, previousLines.Length - 90);
             i--)
        {
            string candidate = CleanOwnerName(previousLines[i]);

            if (LooksLikePersonName(candidate) &&
                !IsNoiseName(candidate) &&
                !LooksLikeAddressContamination(candidate))
            {
                return candidate;
            }
        }

        return "";
    }

    private static bool LooksLikeAddressContamination(string value)
    {
        string normalized = Normalize(value);

        return normalized.Contains("VIA") ||
               normalized.Contains("VIALE") ||
               normalized.Contains("PIAZZA") ||
               normalized.Contains("CONTRADA") ||
               normalized.Contains("CAP") ||
               normalized.Contains("NREA") ||
               normalized.Contains("LUOGODINASCITA") ||
               normalized.Contains("INDIRIZZISTORICI") ||
               normalized.Contains("PRESSOLASOCIETA");
    }

    private static bool IsPlausibleOwner(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            IsNoiseName(value) ||
            FiscalCodePattern.IsMatch(value) ||
            PercentagePattern.IsMatch(value))
            return false;

        return ContainsLegalForm(value) ||
               LooksLikePersonName(value);
    }

    private static string FindOwnerForSeparatedValueRow(
        IReadOnlyList<string> lines,
        int valueRowIndex)
    {
        for (int i = valueRowIndex - 1; i >= Math.Max(0, valueRowIndex - 18); i--)
        {
            string candidate = CleanOwnerName(lines[i]);

            if (ContainsLegalForm(candidate) && !IsNoiseName(candidate))
                return TrimAfterLegalForm(candidate);
        }

        for (int i = valueRowIndex - 1; i >= Math.Max(0, valueRowIndex - 8); i--)
        {
            string candidate = CleanOwnerName(lines[i]);

            if (LooksLikePersonName(candidate) && !IsNoiseName(candidate))
                return candidate;
        }

        return "";
    }

    private static string FindBestOwnerInBlock(
        IReadOnlyList<string> block,
        string fiscalCode)
    {
        int cfLine = -1;

        for (int i = 0; i < block.Count; i++)
        {
            if (block[i].Contains(fiscalCode, StringComparison.OrdinalIgnoreCase))
            {
                cfLine = i;
                break;
            }
        }

        if (cfLine < 0)
            cfLine = block.Count - 1;

        for (int i = cfLine - 1; i >= 0; i--)
        {
            string candidate = CleanOwnerName(block[i]);

            if (ContainsLegalForm(candidate) && !IsNoiseName(candidate))
                return TrimAfterLegalForm(candidate);

            if (LooksLikePersonName(candidate) && !IsNoiseName(candidate))
                return candidate;
        }

        return "";
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
        string[] lines = Lines(string.Join("\n", pages.Select(page => page.Text)));
        var rows = new List<ShareholderRow>();

        for (int i = 0; i < lines.Length; i++)
        {
            Match start = CompanyParticipationStart.Match(lines[i]);

            if (!start.Success)
                continue;

            int end = i + 1;
            while (end < lines.Length &&
                   !CompanyParticipationStart.IsMatch(lines[end]))
                end++;

            string[] block = lines.Skip(i).Take(end - i).ToArray();
            string evidence = string.Join(" | ", block);
            string company = BuildCompanyName(block, start.Groups[1].Value);
            string companyCf = FindLabelledFiscalCode(evidence);

            foreach (Match percentage in PercentagePattern.Matches(evidence))
            {
                int localStart = percentage.Index;
                string local = evidence.Substring(
                    localStart,
                    Math.Min(260, evidence.Length - localStart));

                rows.Add(new ShareholderRow(
                    record.SourceFile,
                    CleanPersonRecordName(record.Denominazione.Value),
                    company,
                    record.CodiceFiscale.Value,
                    companyCf,
                    percentage.Groups[1].Value,
                    FindNumberAfterPercentage(local),
                    FindRight(local),
                    bookmark.Title,
                    pages.FirstOrDefault()?.Number ?? bookmark.StartPage,
                    Limit(evidence, 1800),
                    "Segnalibro PARTECIPAZIONI + tutte le righe quota"));
            }

            i = end - 1;
        }

        return rows
            .Where(row =>
                !string.IsNullOrWhiteSpace(row.ParticipatedCompany) &&
                !string.IsNullOrWhiteSpace(row.Percentage))
            .GroupBy(
                row =>
                    $"{Normalize(row.ParticipatedCompany)}|{row.Percentage}|{Normalize(row.RightType)}",
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
        string[] lines = Lines(string.Join("\n", pages.Select(page => page.Text)));
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
                    "Segnalibro CARICHE + tabella completa"));
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
        string[] lines = Lines(string.Join("\n", pages.Select(page => page.Text)));

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

        string[] revenue = ValuesForMetric(
            lines,
            ["RICAVI NETTI", "RICAVI NETTI BENI E SERVIZI"],
            years.Length);

        string[] financialNet = ValuesForMetric(
            lines,
            ["PROVENTI FINANZIARI NETTI"],
            years.Length);

        string[] financialGross = ValuesForMetric(
            lines,
            ["PROVENTI FINANZIARI LORDI"],
            years.Length);

        string[] totalRevenue = new string[years.Length];

        for (int i = 0; i < years.Length; i++)
        {
            decimal operating = ParseItalianNumber(At(revenue, i));
            decimal financial =
                !string.IsNullOrWhiteSpace(At(financialNet, i))
                    ? ParseItalianNumber(At(financialNet, i))
                    : ParseItalianNumber(At(financialGross, i));

            totalRevenue[i] = FormatItalianNumber(operating + financial);
        }

        string[] ebitda = ValuesForMetric(
            lines,
            ["MARGINE OPERATIVO LORDO"],
            years.Length);

        string[] netIncome = ValuesForMetric(
            lines,
            ["UTILE (PERDITA) DELL'ESERCIZIO"],
            years.Length);

        string[] assets = ValuesForMetric(
            lines,
            ["ATTIVO"],
            years.Length,
            exact: true);

        string[] equity = ValuesForMetric(
            lines,
            ["PATRIMONIO NETTO"],
            years.Length);

        string[] cashFlow = ValuesForMetric(
            lines,
            ["CASH FLOW"],
            years.Length);

        string evidence = Limit(
            string.Join(" | ",
                new[]
                {
                    yearsLine,
                    FindMetricLine(lines, ["RICAVI NETTI", "RICAVI NETTI BENI E SERVIZI"]),
                    FindMetricLine(lines, ["PROVENTI FINANZIARI NETTI"]),
                    FindMetricLine(lines, ["PROVENTI FINANZIARI LORDI"]),
                    FindMetricLine(lines, ["MARGINE OPERATIVO LORDO"]),
                    FindMetricLine(lines, ["UTILE (PERDITA) DELL'ESERCIZIO"]),
                    FindMetricLine(lines, ["ATTIVO"], exact: true),
                    FindMetricLine(lines, ["PATRIMONIO NETTO"]),
                    FindMetricLine(lines, ["CASH FLOW"])
                }.Where(value => !string.IsNullOrWhiteSpace(value))),
            2200);

        var result = new List<BalanceRow>();

        for (int i = 0; i < years.Length; i++)
        {
            result.Add(new BalanceRow(
                record.SourceFile,
                years[i],
                At(revenue, i),
                At(financialNet, i),
                At(financialGross, i),
                At(totalRevenue, i),
                At(ebitda, i),
                At(netIncome, i),
                At(assets, i),
                At(equity, i),
                At(cashFlow, i),
                bookmark.Title,
                pages.FirstOrDefault()?.Number ?? bookmark.StartPage,
                evidence,
                "Segnalibro BILANCIO + tutte le colonne della tabella"));
        }

        return result;
    }

    private static string[] ValuesForMetric(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> aliases,
        int count,
        bool exact = false)
    {
        string line = FindMetricLine(lines, aliases, exact);

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

    private static string FindMetricLine(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> aliases,
        bool exact = false)
    {
        foreach (string alias in aliases)
        {
            string target = Normalize(alias);

            string? line = lines.FirstOrDefault(value =>
            {
                string normalized = Normalize(value);

                if (!normalized.StartsWith(target))
                    return false;

                if (!exact)
                    return true;

                string remainder = normalized[target.Length..];

                return remainder.Length == 0 ||
                       char.IsDigit(remainder[0]) ||
                       remainder[0] == '-';
            });

            if (line is not null)
                return line;
        }

        return "";
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
            string candidate = CleanOwnerName(lines[i]);

            if (LooksLikePersonName(candidate) && !IsNoiseName(candidate))
                return candidate;
        }

        return "";
    }

    private static bool LooksLikePersonName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsDigit) ||
            value.Contains(':') ||
            ContainsLegalForm(value))
            return false;

        string[] words = value.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);

        return words.Length is >= 2 and <= 6 &&
               words.All(word =>
                   word.All(character =>
                       char.IsLetter(character) ||
                       character is '\'' or '-'));
    }

    private static string CleanOwnerName(string value)
    {
        value = Regex.Replace(
            value,
            @"\s*\((?:rappresentante dell'impresa|socio.*?|beneficiario.*?)\)\s*",
            "",
            RegexOptions.IgnoreCase);

        value = Regex.Replace(value, @"^\s*\d+[\.\)]\s*", "");
        value = Regex.Replace(value, @"\s*\(\d+\)\s*$", "");

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
            "CAPITALESOCIALE",
            "INTERROGAZIONISUCERVEDGROUP",
            "ATTIVITA",
            "SITUAZIONEIMPRESA",
            "CAPOGRUPPO",
            "QUOTEQUOTE",
            "TIPODIRITTO",
            "CODICEFISCALE",
            "NREACODICEFISCALE",
            "SOCIETAPERAZIONI",
            "SOCIETAARESPONSABILITALIMITATA"
        ];

        return noise.Any(item =>
            normalized == item ||
            normalized.StartsWith(item));
    }

    private static bool ContainsLegalForm(string value)
    {
        string normalized = Normalize(value);

        return normalized.Contains("SRL") ||
               normalized.Contains("SPA") ||
               normalized.Contains("SOCIETASEMPLICE") ||
               normalized.Contains("SNC") ||
               normalized.Contains("SAS");
    }

    private static string TrimAfterLegalForm(string value)
    {
        Match match = Regex.Match(
            value,
            @"^(.+?\b(?:S\.?\s*R\.?\s*L\.?|S\.?\s*P\.?\s*A\.?|SOCIETA'\s+SEMPLICE|SOCIETÀ\s+SEMPLICE|S\.?\s*N\.?\s*C\.?|S\.?\s*A\.?\s*S\.?))\b",
            RegexOptions.IgnoreCase);

        return match.Success
            ? match.Groups[1].Value.Trim()
            : value.Trim();
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

        return string.Join(
            " ",
            words.Distinct(StringComparer.OrdinalIgnoreCase));
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

    private static string FindNumberAfterPercentage(string text)
    {
        Match percentage = PercentagePattern.Match(text);

        if (!percentage.Success)
            return "";

        Match value = Regex.Match(
            text[(percentage.Index + percentage.Length)..],
            @"(?<!\d)(\d{1,3}(?:\.\d{3})*(?:,\d+)?)(?!\d)");

        return value.Success
            ? value.Groups[1].Value
            : "";
    }

    private static string FindNominalNearPercentage(
        IReadOnlyList<string> block,
        string percentage)
    {
        foreach (string line in block)
        {
            MatchCollection numbers = Regex.Matches(
                line,
                @"(?<!\d)(\d{1,3}(?:\.\d{3})*(?:,\d+)?)(?!\d)");

            foreach (Match number in numbers)
            {
                if (number.Groups[1].Value != percentage &&
                    number.Groups[1].Value.Length >= 3)
                    return number.Groups[1].Value;
            }
        }

        return "";
    }

    private static string NormalizeRight(string value)
    {
        string normalized = Normalize(value);

        if (normalized.Contains("NUDAPROPRIETA"))
            return "Nuda proprietà";

        if (normalized.Contains("USUFRUTTO"))
            return "Usufrutto";

        if (normalized.Contains("SOCIOUNICO"))
            return "Socio unico";

        if (normalized.Contains("PROPRIETA"))
            return "Proprietà";

        return "";
    }

    private static string FindRight(string text) =>
        NormalizeRight(text);

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
            page.Text.Contains(
                token,
                StringComparison.OrdinalIgnoreCase))
        ?.Number
        ?? pages.FirstOrDefault()?.Number
        ?? 0;

    private static decimal ParsePercentage(string value)
    {
        decimal.TryParse(
            value.Replace(".", ","),
            NumberStyles.Number,
            CultureInfo.GetCultureInfo("it-IT"),
            out decimal result);

        return result;
    }

    private static decimal ParseItalianNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        decimal.TryParse(
            value,
            NumberStyles.Number |
            NumberStyles.AllowLeadingSign,
            CultureInfo.GetCultureInfo("it-IT"),
            out decimal result);

        return result;
    }

    private static string FormatItalianNumber(decimal value) =>
        value.ToString(
            "0.###",
            CultureInfo.GetCultureInfo("it-IT"));

    private static string At(
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
