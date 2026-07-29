using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace MichMapper;

internal sealed class CervedPdfReader
{
    private readonly CervedPageReconstructor _reconstructor = new();
    private readonly CervedBookmarkNavigator _bookmarkNavigator = new();

    private static readonly Regex ElevenDigits =
        new(@"(?<!\d)\d{11}(?!\d)", RegexOptions.Compiled);

    private static readonly Regex FiscalCode16 =
        new(@"\b[A-Z]{6}\d{2}[A-EHLMPRST]\d{2}[A-Z]\d{3}[A-Z]\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public CervedRecord Read(string pdfPath)
    {
        if (!File.Exists(pdfPath))
            throw new FileNotFoundException("PDF non trovato.", pdfPath);

        var pages = new List<PageText>();
        IReadOnlyList<BookmarkSection> bookmarkSections;

        using (PdfDocument document = PdfDocument.Open(pdfPath))
        {
            foreach (var page in document.GetPages())
            {
                string reconstructed = Normalize(
                    _reconstructor.Reconstruct(page));

                pages.Add(new PageText(page.Number, reconstructed));
            }

            bookmarkSections = _bookmarkNavigator.ReadSections(
                document,
                document.NumberOfPages);
        }

        string firstPages = string.Join(
            "\n",
            pages.Take(3).Select(page => page.Text));

        CervedDocumentType documentType = DetectType(firstPages);

        return documentType switch
        {
            CervedDocumentType.Company =>
                ParseCompany(pdfPath, pages, bookmarkSections),

            CervedDocumentType.Person =>
                ParsePerson(pdfPath, pages, bookmarkSections),

            _ =>
                ParseUnknown(pdfPath, pages, bookmarkSections)
        };
    }

    private CervedRecord ParseCompany(
        string pdfPath,
        IReadOnlyList<PageText> allPages,
        IReadOnlyList<BookmarkSection> bookmarkSections)
    {
        IReadOnlyList<PageText> identificationPages = GetSectionPages(
            allPages,
            bookmarkSections,
            ["DATI IDENTIFICATIVI & CARATTERISTICI",
             "DATI IDENTIFICATIVI",
             "CARATTERISTICHE"]);

        bool bookmarkUsed = identificationPages.Count > 0;

        if (!bookmarkUsed)
            identificationPages = allPages.Take(5).ToArray();

        string navigationMethod = bookmarkUsed
            ? "Segnalibro: Dati identificativi"
            : "Fallback: prime pagine";

        ExtractedField denomination = FindLabelValue(
            identificationPages,
            ["Denominazione"],
            StopLabels(),
            allowContinuation: false,
            navigationMethod);

        denomination = CleanCompanyDenomination(
            denomination,
            pdfPath);

        ExtractedField vat = FindValidatedNumber(
            identificationPages,
            ["Partita Iva", "Partita IVA", "P. IVA", "P.IVA"],
            ItalianValidators.IsValidVat,
            "Partita IVA",
            navigationMethod);

        ExtractedField fiscalCode = FindFiscalCode(
            identificationPages,
            vat.Value,
            navigationMethod);

        ExtractedField activity = FindLabelValue(
            identificationPages,
            ["Attività Economica",
             "Attivita Economica",
             "Attività Economica (Rettificata Cerved Group)",
             "Attivita Economica (Rettificata Cerved Group)"],
            StopLabels(),
            allowContinuation: true,
            navigationMethod);

        activity = CleanEconomicActivity(activity);

        ExtractedField legalForm = FindLabelValue(
            identificationPages,
            ["Forma Giuridica"],
            StopLabels(),
            allowContinuation: true,
            navigationMethod);

        ExtractedField status = FindLabelValue(
            identificationPages,
            ["Situazione Impresa"],
            StopLabels(),
            allowContinuation: false,
            navigationMethod);

        ExtractedField rea = FindLabelValue(
            identificationPages,
            ["CCIAA/REA", "N. REA", "N.REA"],
            StopLabels(),
            allowContinuation: false,
            navigationMethod);

        ExtractedField incorporation = FindLabelValue(
            identificationPages,
            ["Data Costituzione"],
            StopLabels(),
            allowContinuation: false,
            navigationMethod);

        if (string.IsNullOrWhiteSpace(denomination.Value))
        {
            denomination = new ExtractedField(
                NameFromFile(pdfPath),
                0,
                "Nome del file usato soltanto come fallback.",
                "Fallback",
                "Nome file");
        }

        return new CervedRecord
        {
            SourceFile = Path.GetFileName(pdfPath),
            DocumentType = CervedDocumentType.Company,
            Denominazione = denomination,
            PartitaIva = vat,
            CodiceFiscale = fiscalCode,
            AttivitaEconomica = activity,
            FormaGiuridica = legalForm,
            SituazioneImpresa = status,
            Rea = rea,
            DataCostituzione = incorporation,
            PageCount = allPages.Count,
            Pages = allPages,
            BookmarkSections = bookmarkSections,
            BookmarkStatus = bookmarkSections.Count > 0
                ? $"{bookmarkSections.Count} segnalibri letti"
                : "Segnalibri non rilevati",
            ValidationStatus = ValidateCompany(
                vat,
                fiscalCode,
                denomination,
                activity)
        };
    }

    private CervedRecord ParsePerson(
        string pdfPath,
        IReadOnlyList<PageText> allPages,
        IReadOnlyList<BookmarkSection> bookmarkSections)
    {
        IReadOnlyList<PageText> personalPages = GetSectionPages(
            allPages,
            bookmarkSections,
            ["DATI ANAGRAFICI",
             "ANAGRAFICA",
             "DATI IDENTIFICATIVI"]);

        bool bookmarkUsed = personalPages.Count > 0;

        if (!bookmarkUsed)
            personalPages = allPages.Take(5).ToArray();

        string method = bookmarkUsed
            ? "Segnalibro: Dati anagrafici"
            : "Fallback: prime pagine";

        ExtractedField surname = FindPersonField(
            personalPages,
            "Cognome",
            method);

        ExtractedField name = FindPersonField(
            personalPages,
            "Nome",
            method);

        ExtractedField fiscalCode = FindFiscalCode(
            personalPages,
            "",
            method);

        ExtractedField denomination =
            !string.IsNullOrWhiteSpace(surname.Value) ||
            !string.IsNullOrWhiteSpace(name.Value)
                ? new ExtractedField(
                    $"{surname.Value} {name.Value}".Trim(),
                    surname.Page > 0 ? surname.Page : name.Page,
                    $"{surname.Evidence} | {name.Evidence}".Trim(' ', '|'),
                    "Alta",
                    method)
                : new ExtractedField(
                    NameFromFile(pdfPath),
                    0,
                    "Nome del file usato come fallback.",
                    "Fallback",
                    "Nome file");

        return new CervedRecord
        {
            SourceFile = Path.GetFileName(pdfPath),
            DocumentType = CervedDocumentType.Person,
            Denominazione = denomination,
            Cognome = surname,
            Nome = name,
            CodiceFiscale = fiscalCode,
            PageCount = allPages.Count,
            Pages = allPages,
            BookmarkSections = bookmarkSections,
            BookmarkStatus = bookmarkSections.Count > 0
                ? $"{bookmarkSections.Count} segnalibri letti"
                : "Segnalibri non rilevati",
            ValidationStatus =
                ItalianValidators.IsPlausibleFiscalCode(fiscalCode.Value)
                    ? "Dati persona validati"
                    : "Da verificare"
        };
    }

    private static CervedRecord ParseUnknown(
        string pdfPath,
        IReadOnlyList<PageText> pages,
        IReadOnlyList<BookmarkSection> bookmarkSections)
    {
        return new CervedRecord
        {
            SourceFile = Path.GetFileName(pdfPath),
            DocumentType = CervedDocumentType.Unknown,
            Denominazione = new ExtractedField(
                NameFromFile(pdfPath),
                0,
                "Tipo documento non riconosciuto.",
                "Fallback",
                "Nome file"),
            PageCount = pages.Count,
            Pages = pages,
            BookmarkSections = bookmarkSections,
            BookmarkStatus = bookmarkSections.Count > 0
                ? $"{bookmarkSections.Count} segnalibri letti"
                : "Segnalibri non rilevati",
            ValidationStatus = "Formato Cerved non riconosciuto"
        };
    }

    private IReadOnlyList<PageText> GetSectionPages(
        IReadOnlyList<PageText> allPages,
        IReadOnlyList<BookmarkSection> sections,
        string[] aliases)
    {
        BookmarkSection? section =
            _bookmarkNavigator.FindBestSection(sections, aliases);

        if (section is null)
            return [];

        return allPages
            .Where(page => section.ContainsPage(page.Number))
            .ToArray();
    }

    private static CervedDocumentType DetectType(string text)
    {
        string normalized = SearchText(text);

        if (normalized.Contains("DATIIDENTIFICATIVI&CARATTERISTICI") ||
            normalized.Contains("PARTITAIVA") ||
            normalized.Contains("FORMAGIURIDICA"))
            return CervedDocumentType.Company;

        if (normalized.Contains("DOSSIERPERSONAAPPROFONDITO") ||
            normalized.Contains("DATIANAGRAFICI") &&
            normalized.Contains("LUOGODINASCITA"))
            return CervedDocumentType.Person;

        return CervedDocumentType.Unknown;
    }

    private static ExtractedField FindValidatedNumber(
        IReadOnlyList<PageText> pages,
        IReadOnlyList<string> labels,
        Func<string, bool> validator,
        string method,
        string navigationMethod)
    {
        foreach (PageText page in pages)
        {
            string[] lines = Lines(page.Text);

            for (int i = 0; i < lines.Length; i++)
            {
                if (!labels.Any(label => Contains(lines[i], label)))
                    continue;

                string evidence = string.Join(
                    " | ",
                    lines.Skip(i).Take(4));

                foreach (Match match in ElevenDigits.Matches(evidence))
                {
                    if (validator(match.Value))
                    {
                        return new ExtractedField(
                            match.Value,
                            page.Number,
                            Limit(evidence, 500),
                            "Alta",
                            $"{navigationMethod}; {method}; controllo formale");
                    }
                }
            }
        }

        return ExtractedField.Empty($"{navigationMethod}; {method}");
    }

    private static ExtractedField FindFiscalCode(
        IReadOnlyList<PageText> pages,
        string excludedVat,
        string navigationMethod)
    {
        string[] labels =
        [
            "Codice Fiscale",
            "Codice fiscale",
            "C. F.",
            "C.F."
        ];

        foreach (PageText page in pages)
        {
            string[] lines = Lines(page.Text);

            for (int i = 0; i < lines.Length; i++)
            {
                if (!labels.Any(label => Contains(lines[i], label)))
                    continue;

                string evidence = string.Join(
                    " | ",
                    lines.Skip(i).Take(4))
                    .ToUpperInvariant();

                Match person = FiscalCode16.Match(evidence);

                if (person.Success)
                {
                    return new ExtractedField(
                        person.Value.ToUpperInvariant(),
                        page.Number,
                        Limit(evidence, 500),
                        "Alta",
                        $"{navigationMethod}; codice fiscale persona");
                }

                foreach (Match match in ElevenDigits.Matches(evidence))
                {
                    string value = match.Value;

                    if (value == excludedVat)
                    {
                        return new ExtractedField(
                            value,
                            page.Number,
                            Limit(evidence, 500),
                            "Alta",
                            $"{navigationMethod}; CF impresa coincidente con P.IVA");
                    }

                    if (ItalianValidators.IsValidVat(value))
                    {
                        return new ExtractedField(
                            value,
                            page.Number,
                            Limit(evidence, 500),
                            "Media",
                            $"{navigationMethod}; codice fiscale numerico");
                    }
                }
            }
        }

        return ExtractedField.Empty(
            $"{navigationMethod}; codice fiscale");
    }

    private static ExtractedField FindLabelValue(
        IReadOnlyList<PageText> pages,
        IReadOnlyList<string> labels,
        IReadOnlyList<string> stopLabels,
        bool allowContinuation,
        string navigationMethod)
    {
        foreach (PageText page in pages)
        {
            string[] lines = Lines(page.Text);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                string? matchedLabel = labels.FirstOrDefault(
                    label => Contains(line, label));

                if (matchedLabel is null)
                    continue;

                string value = RemoveLabel(line, matchedLabel);

                if (string.IsNullOrWhiteSpace(value) &&
                    i + 1 < lines.Length)
                    value = lines[i + 1];

                var collected = new List<string>();

                if (!string.IsNullOrWhiteSpace(value) &&
                    !IsStopLine(value, stopLabels))
                    collected.Add(value);

                if (allowContinuation)
                {
                    for (int j = i + 1;
                         j < Math.Min(i + 5, lines.Length);
                         j++)
                    {
                        string candidate = lines[j];

                        if (j == i + 1 &&
                            collected.Count > 0 &&
                            candidate == collected[0])
                            continue;

                        if (IsStopLine(candidate, stopLabels) ||
                            LooksLikeSection(candidate))
                            break;

                        collected.Add(candidate);
                    }
                }

                string finalValue =
                    Clean(string.Join(" ", collected));

                if (finalValue.Length >= 2)
                {
                    string evidence = string.Join(
                        " | ",
                        lines.Skip(i)
                            .Take(Math.Min(5, lines.Length - i)));

                    return new ExtractedField(
                        Limit(finalValue, 600),
                        page.Number,
                        Limit(evidence, 700),
                        "Alta",
                        $"{navigationMethod}; etichetta Cerved");
                }
            }
        }

        return ExtractedField.Empty(
            $"{navigationMethod}; etichetta Cerved");
    }

    private static IReadOnlyList<string> StopLabels() =>
    [
        "Indirizzo Sede",
        "Codice Fiscale",
        "Partita Iva",
        "Partita IVA",
        "CCIAA/REA",
        "Forma Giuridica",
        "Situazione Impresa",
        "Attività Economica",
        "Attivita Economica",
        "Impresa Appartenente",
        "Nome Capogruppo",
        "Data Costituzione",
        "Data Iscrizione",
        "Data Inizio Attività",
        "Capitale Sociale",
        "Totale Quote",
        "Nr. Addetti",
        "Nr. Dipendenti",
        "Interrogazioni su Cerved Group",
        "Nr. Uffici",
        "Movimentazioni R. I.",
        "Sito Internet",
        "Telefono",
        "E-MAIL Certificata",
        "Sigla della denominazione"
    ];


    private static ExtractedField CleanCompanyDenomination(
        ExtractedField field,
        string pdfPath)
    {
        string value = field.Value;

        if (string.IsNullOrWhiteSpace(value))
            return field;

        string[] addressMarkers =
        [
            " BARI (", " BRINDISI (", " FASANO (", " MATERA (",
            " MILANO (", " ROMA (", " VIA ", " PIAZZA ", " CORSO "
        ];

        int cut = addressMarkers
            .Select(marker => value.IndexOf(
                marker,
                StringComparison.OrdinalIgnoreCase))
            .Where(index => index > 0)
            .DefaultIfEmpty(-1)
            .Min();

        if (cut > 0)
            value = value[..cut].Trim();

        if (value.Equals("LIMITATA", StringComparison.OrdinalIgnoreCase) ||
            value.Length < 4)
            value = NameFromFile(pdfPath);

        return field with { Value = value };
    }

    private static ExtractedField CleanEconomicActivity(
        ExtractedField field)
    {
        string value = field.Value;

        if (string.IsNullOrWhiteSpace(value))
            return field;

        value = Regex.Replace(
            value,
            @"^\s*\(Rettificata\s+Cerved\s*",
            "",
            RegexOptions.IgnoreCase);

        value = Regex.Replace(
            value,
            @"\s+Group\)\s*$",
            "",
            RegexOptions.IgnoreCase);

        value = Regex.Replace(
            value,
            @"^\s*Attivit[aà]'\s+",
            "",
            RegexOptions.IgnoreCase);

        value = value.Trim(' ', '(', ')');

        if (value.Equals("18", StringComparison.OrdinalIgnoreCase))
            value = "";

        return field with { Value = value };
    }

    private static ExtractedField FindPersonField(
        IReadOnlyList<PageText> pages,
        string label,
        string method)
    {
        foreach (PageText page in pages)
        {
            foreach (string line in Lines(page.Text))
            {
                Match match = Regex.Match(
                    line,
                    $@"^\s*{Regex.Escape(label)}\s*[:\-]?\s+(.+)$",
                    RegexOptions.IgnoreCase);

                if (!match.Success)
                    continue;

                string value = match.Groups[1].Value.Trim();

                value = Regex.Replace(
                    value,
                    @"^(Cog(?:nome)?|Nom(?:e)?)\s+",
                    "",
                    RegexOptions.IgnoreCase);

                value = Regex.Replace(
                    value,
                    @"\s+(Luogo di nascita|Data di nascita|Codice Fiscale).*$",
                    "",
                    RegexOptions.IgnoreCase);

                if (value.Length >= 2 &&
                    !value.Equals(label, StringComparison.OrdinalIgnoreCase) &&
                    !value.Equals("18", StringComparison.OrdinalIgnoreCase))
                {
                    return new ExtractedField(
                        value,
                        page.Number,
                        line,
                        "Alta",
                        $"{method}; campo anagrafico Cerved");
                }
            }
        }

        return ExtractedField.Empty($"{method}; {label}");
    }

    private static string ValidateCompany(
        ExtractedField vat,
        ExtractedField fiscalCode,
        ExtractedField denomination,
        ExtractedField activity)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(denomination.Value))
            missing.Add("denominazione");

        if (!ItalianValidators.IsValidVat(vat.Value))
            missing.Add("P.IVA");

        if (!ItalianValidators.IsPlausibleFiscalCode(fiscalCode.Value))
            missing.Add("CF");

        if (string.IsNullOrWhiteSpace(activity.Value))
            missing.Add("attività");

        return missing.Count == 0
            ? "Campi identificativi validati"
            : "Da verificare: " + string.Join(", ", missing);
    }

    private static string[] Lines(string text) =>
        text.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Select(Clean)
            .Where(value => value.Length > 0)
            .ToArray();

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n")
            .Replace('\r', '\n');

    private static string SearchText(string value) =>
        Regex.Replace(
            value.ToUpperInvariant(),
            @"\s+",
            "");

    private static bool Contains(
        string source,
        string value) =>
        SearchText(source)
            .Contains(
                SearchText(value),
                StringComparison.Ordinal);

    private static string RemoveLabel(
        string line,
        string label)
    {
        string[] labelParts = label.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);

        string result = line;

        foreach (string part in labelParts)
        {
            int index = result.IndexOf(
                part,
                StringComparison.OrdinalIgnoreCase);

            if (index >= 0)
                result = result.Remove(index, part.Length);
        }

        return Clean(
            result.TrimStart(':', '-', ' '));
    }

    private static bool IsStopLine(
        string value,
        IReadOnlyList<string> stopLabels) =>
        stopLabels.Any(label => Contains(value, label));

    private static bool LooksLikeSection(string value)
    {
        string trimmed = value.Trim();

        return trimmed.Length > 8 &&
               trimmed.All(character =>
                   !char.IsLetter(character) ||
                   char.IsUpper(character)) &&
               !trimmed.Any(char.IsDigit);
    }

    private static string Clean(string value) =>
        Regex.Replace(value, @"\s{2,}", " ").Trim();

    private static string Limit(
        string value,
        int maxLength) =>
        value.Length <= maxLength
            ? value
            : value[..maxLength];

    private static string NameFromFile(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);

        name = Regex.Replace(
            name,
            @"\s*-\s*Cerve[vd].*$",
            "",
            RegexOptions.IgnoreCase);

        return Clean(name.Replace('_', ' '));
    }
}
