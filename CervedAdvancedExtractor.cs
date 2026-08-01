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

        // Il bookmark SOCI indica l'inizio della sezione. La tabella puo'
        // continuare sulla pagina puntata dal bookmark successivo: quella
        // pagina va inclusa e poi ritagliata sul titolo della nuova sezione.
        PageText[] pages = ShareholderPages(record, bookmark)
            .OrderBy(page => page.Number)
            .ToArray();

        ShareholderSection section = ExtractShareholderSection(pages);

        if (section.Lines.Length == 0)
            return [];

        var rows = new List<ShareholderRow>();

        /*
         * Regola fondamentale v3.20: SOCI viene letto per record logici.
         * Nei dossier Cerved la quota e la P.IVA di un socio-societa' sono
         * spesso su righe diverse; per le persone, invece, possono essere
         * sulla stessa riga. Ogni occorrenza quota-percentuale-diritto apre
         * un solo record e riceve il CF/P.IVA migliore nel proprio blocco.
         */
        IReadOnlyList<QuotaOccurrence> quotas = FindQuotaOccurrences(
            section.Lines);

        for (int quotaIndex = 0; quotaIndex < quotas.Count; quotaIndex++)
        {
            QuotaOccurrence quota = quotas[quotaIndex];
            ShareholderBlock block = BuildShareholderBlock(
                section.Lines,
                quotas,
                quotaIndex);
            FiscalCodeOccurrence? fiscalCode = FindFiscalCodeForQuota(
                section.Lines,
                block,
                quota);

            if (fiscalCode is null)
                continue;

            string owner = FindOwnerForFiscalCode(
                section.Lines,
                fiscalCode,
                block);

            if (string.IsNullOrWhiteSpace(owner))
                continue;

            rows.Add(CreateShareholderRow(
                record,
                bookmark,
                pages,
                owner,
                fiscalCode.Value,
                quota.Percentage,
                quota.Nominal,
                quota.Right,
                BuildShareholderEvidence(
                    section.Lines,
                    block.StartLine,
                    block.EndLine),
                "Segnalibro SOCI + record logico per socio"));
        }

        return rows
            .Where(row =>
                !string.IsNullOrWhiteSpace(row.Owner) &&
                !string.IsNullOrWhiteSpace(row.OwnerFiscalCode) &&
                row.OwnerFiscalCode != record.CodiceFiscale.Value &&
                !IsNoiseName(row.Owner))
            .GroupBy(
                row =>
                    $"{row.OwnerFiscalCode}|{row.NominalValue}|" +
                    $"{row.Percentage}|{Normalize(row.RightType)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(row => ParsePercentage(row.Percentage))
            .ThenBy(row => row.Owner)
            .ToArray();
    }

    private sealed record ShareholderSection(string[] Lines);

    private sealed record FiscalCodeOccurrence(
        int LineIndex,
        int CharacterIndex,
        string Value,
        string Line);

    private sealed record QuotaOccurrence(
        int LineIndex,
        int CharacterIndex,
        string Nominal,
        string Percentage,
        string Right,
        string Line);

    private sealed record ShareholderBlock(
        int StartLine,
        int EndLine);


    private static readonly Regex QuotaValuePattern =
        new(
            @"(?<nominal>\d{1,3}(?:\.\d{3})*(?:,\d{1,2})?)\s+" +
            @"(?<percentage>\d{1,3}(?:[\,\.]\d+)?)\s*%",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static IReadOnlyList<QuotaOccurrence> FindQuotaOccurrences(
        IReadOnlyList<string> lines)
    {
        var result = new List<QuotaOccurrence>();

        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            foreach (Match match in QuotaValuePattern.Matches(lines[lineIndex]))
            {
                string right = FindRightForQuota(lines, lineIndex, match);

                if (string.IsNullOrWhiteSpace(right))
                    continue;

                result.Add(new QuotaOccurrence(
                    lineIndex,
                    match.Index,
                    match.Groups["nominal"].Value,
                    match.Groups["percentage"].Value,
                    right,
                    lines[lineIndex]));
            }
        }

        return result;
    }

    private static string FindRightForQuota(
        IReadOnlyList<string> lines,
        int lineIndex,
        Match quotaMatch)
    {
        string sameLineTail = lines[lineIndex][quotaMatch.Index..];
        string normalizedTail = Normalize(sameLineTail);

        if (normalizedTail.Contains("NUDAPROPRIETA"))
            return "Nuda proprietà";
        if (normalizedTail.Contains("USUFRUTTO"))
            return "Usufrutto";
        if (normalizedTail.Contains("SOCIOUNICO"))
            return "Socio unico";
        if (normalizedTail.Contains("PROPRIETA"))
            return "Proprietà";

        // Nelle tabelle Cerved il diritto può essere spezzato dalla lettura
        // per coordinate in tre righe: NUDA / socio-quota-% / PROPRIETA'.
        string previous = lineIndex > 0 ? Normalize(lines[lineIndex - 1]) : "";
        string next = lineIndex + 1 < lines.Count
            ? Normalize(lines[lineIndex + 1])
            : "";

        if (previous == "NUDA" && next.StartsWith("PROPRIETA"))
            return "Nuda proprietà";
        if (next.StartsWith("USUFRUTTO"))
            return "Usufrutto";
        if (next.StartsWith("SOCIOUNICO"))
            return "Socio unico";
        if (next.StartsWith("PROPRIETA"))
            return "Proprietà";

        return "";
    }

    private static ShareholderBlock BuildShareholderBlock(
        IReadOnlyList<string> lines,
        IReadOnlyList<QuotaOccurrence> quotas,
        int quotaIndex)
    {
        QuotaOccurrence current = quotas[quotaIndex];
        int previousLine = quotaIndex == 0
            ? 0
            : quotas[quotaIndex - 1].LineIndex;
        int nextLine = quotaIndex == quotas.Count - 1
            ? lines.Count - 1
            : quotas[quotaIndex + 1].LineIndex;

        int start = quotaIndex == 0
            ? Math.Max(0, current.LineIndex - 45)
            : Math.Min(current.LineIndex, previousLine + 1);
        int end = quotaIndex == quotas.Count - 1
            ? Math.Min(lines.Count - 1, current.LineIndex + 18)
            : Math.Min(lines.Count - 1, nextLine - 1);

        return new ShareholderBlock(start, Math.Max(start, end));
    }

    private static FiscalCodeOccurrence? FindFiscalCodeForQuota(
        IReadOnlyList<string> lines,
        ShareholderBlock block,
        QuotaOccurrence quota)
    {
        var candidates = new List<(FiscalCodeOccurrence Item, int Score)>();
        int from = Math.Max(block.StartLine, quota.LineIndex - 18);
        int to = Math.Min(block.EndLine, quota.LineIndex + 6);

        for (int lineIndex = from; lineIndex <= to; lineIndex++)
        {
            foreach (Match match in FiscalCodePattern.Matches(lines[lineIndex]))
            {
                string value = match.Value.ToUpperInvariant();
                int distance = Math.Abs(lineIndex - quota.LineIndex);
                int score = 100 - (distance * 10);

                if (lineIndex == quota.LineIndex)
                    score += 100;
                else if (lineIndex > quota.LineIndex)
                    score += 35;

                if (value.All(char.IsDigit) &&
                    lineIndex >= quota.LineIndex)
                    score += 30;

                candidates.Add((
                    new FiscalCodeOccurrence(
                        lineIndex,
                        match.Index,
                        value,
                        lines[lineIndex]),
                    score));
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate =>
                Math.Abs(candidate.Item.LineIndex - quota.LineIndex))
            .Select(candidate => candidate.Item)
            .FirstOrDefault();
    }

    private static ShareholderSection ExtractShareholderSection(
        IReadOnlyList<PageText> pages)
    {
        var sourceLines = new List<string>();

        foreach (PageText page in pages)
        {
            foreach (string line in page.Text.Split('\n'))
            {
                string cleaned = Regex.Replace(line, @"\s{2,}", " ").Trim();

                if (cleaned.Length > 0)
                    sourceLines.Add(cleaned);
            }
        }

        int start = -1;

        for (int i = 0; i < sourceLines.Count; i++)
        {
            if (Normalize(sourceLines[i]) != "SOCI")
                continue;

            bool hasCapitalHeader = sourceLines
                .Skip(i + 1)
                .Take(15)
                .Any(item =>
                    Normalize(item).StartsWith("CAPITALESOCIALEEURO"));

            if (hasCapitalHeader)
            {
                start = i;
                break;
            }
        }

        if (start < 0)
            return new ShareholderSection([]);

        int end = sourceLines.Count;

        for (int i = start + 1; i < sourceLines.Count; i++)
        {
            string normalized = Normalize(sourceLines[i]);

            if (normalized.StartsWith(
                    "SOCICARICHEQUALIFICHEINALTREIMPRESE"))
            {
                end = i;
                break;
            }

            // Nei dossier senza SOCI-CARICHE la tabella termina davanti
            // alla prima sezione principale successiva.
            if (normalized is
                "PARTECIPAZIONIDAARCHIVIOSOCI" or
                "PARTECIPAZIONIRISULTANTIDABILANCIO" or
                "INFORMAZIONIIMMOBILIARI" or
                "ATTIVITAECONOMICA")
            {
                end = i;
                break;
            }
        }

        return new ShareholderSection(
            sourceLines.Skip(start).Take(end - start).ToArray());
    }

    private static string FindOwnerForFiscalCode(
        IReadOnlyList<string> lines,
        FiscalCodeOccurrence fiscalCode,
        ShareholderBlock block)
    {
        int index = fiscalCode.LineIndex;
        string line = lines[index];
        int codePosition = line.IndexOf(
            fiscalCode.Value,
            StringComparison.OrdinalIgnoreCase);

        if (codePosition > 0)
        {
            string sameLineOwner = CleanOwnerName(line[..codePosition]);

            if (IsPlausibleOwner(sameLineOwner) &&
                !LooksLikeAddressOrDescription(sameLineOwner))
            {
                return IsLegalFormOnly(sameLineOwner)
                    ? FindCompanyNameBefore(
                        lines,
                        index - 1,
                        block.StartLine)
                    : TrimAfterLegalForm(sameLineOwner);
            }
        }

        string company = FindCompanyNameBefore(
            lines,
            index,
            block.StartLine);

        if (!string.IsNullOrWhiteSpace(company))
            return company;

        for (int i = index - 1;
             i >= Math.Max(block.StartLine, index - 55);
             i--)
        {
            string candidate = CleanOwnerName(lines[i]);

            if (LooksLikePersonName(candidate) &&
                !LooksLikeAddressOrDescription(candidate) &&
                !IsNoiseName(candidate))
            {
                return candidate;
            }
        }

        return "";
    }

    private static string FindCompanyNameBefore(
        IReadOnlyList<string> lines,
        int fromIndex,
        int minimumIndex = 0)
    {
        for (int i = Math.Min(fromIndex, lines.Count - 1);
             i >= Math.Max(minimumIndex, fromIndex - 55);
             i--)
        {
            string candidate = CleanOwnerName(lines[i]);

            if (LooksLikeAddressOrDescription(candidate) ||
                IsNoiseName(candidate))
                continue;

            if (ContainsLegalForm(candidate) &&
                !IsLegalFormOnly(candidate))
            {
                return TrimAfterLegalForm(candidate);
            }

            if (IsLegalFormOnly(candidate))
            {
                for (int previous = i - 1;
                     previous >= Math.Max(minimumIndex, i - 4);
                     previous--)
                {
                    string name = CleanOwnerName(lines[previous]);

                    if (!string.IsNullOrWhiteSpace(name) &&
                        !LooksLikeAddressOrDescription(name) &&
                        !IsNoiseName(name) &&
                        !FiscalCodePattern.IsMatch(name) &&
                        !PercentagePattern.IsMatch(name))
                    {
                        return name;
                    }
                }
            }

            bool legalFormFollows = lines
                .Skip(i + 1)
                .Take(Math.Min(4, lines.Count - i - 1))
                .Any(IsLegalFormOnly);

            if (legalFormFollows &&
                !string.IsNullOrWhiteSpace(candidate) &&
                !FiscalCodePattern.IsMatch(candidate) &&
                !PercentagePattern.IsMatch(candidate))
            {
                return candidate;
            }
        }

        return "";
    }

    private static bool IsLegalFormOnly(string value)
    {
        string normalized = Normalize(value);

        return normalized is
            "SOCIETAPERAZIONI" or
            "SOCIETAARESPONSABILITALIMITATA" or
            "SOCIETASEMPLICE" or
            "SOCIETAINNOMECOLLETTIVO" or
            "SOCIETAINACCOMANDITASEMPLICE";
    }

    private static bool LooksLikeAddressOrDescription(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        string normalized = Normalize(value);

        string[] forbidden =
        [
            "VIA", "VIALE", "PIAZZA", "CONTRADA", "CORSO", "LOCALITA",
            "CAP", "RIFERIMENTO", "NREA", "LUOGODINASCITA", "DATADINASCITA",
            "INDIRIZZISTORICI", "SITUAZIONEIMPRESA", "ATTIVITA",
            "CAPITALESOCIALE", "INTERROGAZIONI", "IMPRESAAPPARTENENTE",
            "CAPOGRUPPO", "CODICERAE", "CODICESAE", "DATAATTO",
            "DATADEPOSITO", "DATAPROTOCOLLO", "NUMEROPROTOCOLLO",
            "SOCIOOBENEFICIARIO", "TIPODIRITTO", "QUOTE"
        ];

        return value.Contains(':') ||
               forbidden.Any(item => normalized.StartsWith(item));
    }

    private static string BuildShareholderEvidence(
        IReadOnlyList<string> lines,
        int quotaLine,
        int fiscalCodeLine)
    {
        int from = Math.Max(0, Math.Min(quotaLine, fiscalCodeLine) - 12);
        int to = Math.Min(
            lines.Count,
            Math.Max(quotaLine, fiscalCodeLine) + 8);

        return string.Join(" | ", lines.Skip(from).Take(to - from));
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
            CervedNameResolver.GetDenominazione(record),
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

        if (IsPlausibleOwner(sameLinePrefix))
            return ContainsLegalForm(sameLinePrefix)
                ? TrimAfterLegalForm(sameLinePrefix)
                : sameLinePrefix;

        int windowStart = Math.Max(0, valueIndex - 900);
        string before = sectionText.Substring(
            windowStart,
            valueIndex - windowStart);

        string[] previousLines = Lines(before);

        for (int i = previousLines.Length - 1;
             i >= Math.Max(0, previousLines.Length - 15);
             i--)
        {
            string candidate = CleanOwnerName(previousLines[i]);

            if (IsPlausibleOwner(candidate))
                return ContainsLegalForm(candidate)
                    ? TrimAfterLegalForm(candidate)
                    : candidate;
        }

        return "";
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

    private static PageText[] ShareholderPages(
        CervedRecord record,
        BookmarkSection shareholderBookmark)
    {
        int startPage = shareholderBookmark.StartPage;

        // La pagina del bookmark successivo e' intenzionalmente inclusa:
        // Cerved vi colloca spesso la continuazione della tabella SOCI.
        int? boundaryPage = record.BookmarkSections
            .Where(section =>
                !ReferenceEquals(section, shareholderBookmark) &&
                section.StartPage >= startPage &&
                IsShareholderEndBookmark(section.Title))
            .OrderBy(section => section.StartPage)
            .ThenBy(section => EndBookmarkPriority(section.Title))
            .Select(section => (int?)section.StartPage)
            .FirstOrDefault();

        int lastDocumentPage = record.Pages.Count == 0
            ? startPage
            : record.Pages.Max(page => page.Number);
        int endPage = boundaryPage ?? Math.Min(lastDocumentPage, startPage + 3);

        return record.Pages
            .Where(page =>
                page.Number >= startPage &&
                page.Number <= endPage)
            .ToArray();
    }

    private static bool IsShareholderEndBookmark(string title)
    {
        string normalized = Normalize(title);

        return normalized.StartsWith("SOCICARICHEQUALIFICHEINALTREIMPRESE") ||
               normalized.StartsWith("PARTECIPAZIONIDAARCHIVIOSOCI") ||
               normalized.StartsWith("PARTECIPAZIONIRISULTANTIDABILANCIO") ||
               normalized.StartsWith("INFORMAZIONIIMMOBILIARI") ||
               normalized.StartsWith("ATTIVITAECONOMICA");
    }

    private static int EndBookmarkPriority(string title)
    {
        string normalized = Normalize(title);

        if (normalized.StartsWith("SOCICARICHEQUALIFICHEINALTREIMPRESE"))
            return 0;
        if (normalized.StartsWith("PARTECIPAZIONIDAARCHIVIOSOCI"))
            return 1;
        if (normalized.StartsWith("PARTECIPAZIONIRISULTANTIDABILANCIO"))
            return 2;

        return 3;
    }

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
            "CAPOGRUPPO"
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
