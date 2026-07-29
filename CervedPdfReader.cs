using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace MichMapper;

internal sealed class CervedPdfReader
{
    private readonly CervedPageReconstructor _reconstructor = new();

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

        using (PdfDocument document = PdfDocument.Open(pdfPath))
        {
            foreach (var page in document.GetPages())
            {
                string reconstructed = Normalize(_reconstructor.Reconstruct(page));
                pages.Add(new PageText(page.Number, reconstructed));
            }
        }

        string firstPages = string.Join("\n", pages.Take(3).Select(p => p.Text));
        CervedDocumentType type = DetectType(firstPages);

        return type switch
        {
            CervedDocumentType.Company => ParseCompany(pdfPath, pages),
            CervedDocumentType.Person => ParsePerson(pdfPath, pages),
            _ => ParseUnknown(pdfPath, pages)
        };
    }

    private static CervedDocumentType DetectType(string text)
    {
        string normalized = SearchText(text);

        if (normalized.Contains("DATIIDENTIFICATIVI&CARATTERISTICI") ||
            normalized.Contains("PARTITAIVA") ||
            normalized.Contains("FORMA GIURIDICA"))
            return CervedDocumentType.Company;

        if (normalized.Contains("DOSSIERPERSONAAPPROFONDITO") ||
            normalized.Contains("DATIANAGRAFICI") &&
            normalized.Contains("LUOGODINASCITA"))
            return CervedDocumentType.Person;

        return CervedDocumentType.Unknown;
    }

    private static CervedRecord ParseCompany(string pdfPath, IReadOnlyList<PageText> pages)
    {
        PageText page1 = pages.First();

        ExtractedField denomination = FindLabelValue(
            pages, ["Denominazione"], StopLabels(), allowContinuation: true);

        ExtractedField vat = FindValidatedNumber(
            pages, ["Partita Iva", "Partita IVA", "P. IVA", "P.IVA"],
            ItalianValidators.IsValidVat, "Partita IVA");

        ExtractedField fiscalCode = FindFiscalCode(pages, vat.Value);

        ExtractedField activity = FindLabelValue(
            pages,
            ["Attività Economica", "Attivita Economica",
             "Attività Economica (Rettificata Cerved Group)",
             "Attivita Economica (Rettificata Cerved Group)"],
            StopLabels(),
            allowContinuation: true);

        ExtractedField legalForm = FindLabelValue(
            pages, ["Forma Giuridica"], StopLabels(), allowContinuation: true);

        ExtractedField status = FindLabelValue(
            pages, ["Situazione Impresa"], StopLabels(), allowContinuation: false);

        ExtractedField rea = FindLabelValue(
            pages, ["CCIAA/REA", "N. REA", "N.REA"], StopLabels(), allowContinuation: false);

        ExtractedField incorporation = FindLabelValue(
            pages, ["Data Costituzione"], StopLabels(), allowContinuation: false);

        if (string.IsNullOrWhiteSpace(denomination.Value))
        {
            denomination = new ExtractedField(
                NameFromFile(pdfPath),
                0,
                "Nome del file usato soltanto come fallback.",
                "Fallback",
                "Nome file");
        }

        string validation = ValidateCompany(vat, fiscalCode, denomination, activity);

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
            PageCount = pages.Count,
            Pages = pages,
            ValidationStatus = validation
        };
    }

    private static CervedRecord ParsePerson(string pdfPath, IReadOnlyList<PageText> pages)
    {
        ExtractedField surname = FindLabelValue(
            pages, ["Cognome"], ["Nome", "Luogo di nascita", "Data di nascita", "Codice Fiscale"],
            allowContinuation: false);

        ExtractedField name = FindLabelValue(
            pages, ["Nome"], ["Luogo di nascita", "Data di nascita", "Codice Fiscale"],
            allowContinuation: false);

        ExtractedField fiscalCode = FindFiscalCode(pages, "");

        ExtractedField denomination;

        if (!string.IsNullOrWhiteSpace(surname.Value) || !string.IsNullOrWhiteSpace(name.Value))
        {
            denomination = new ExtractedField(
                $"{surname.Value} {name.Value}".Trim(),
                surname.Page > 0 ? surname.Page : name.Page,
                $"{surname.Evidence} | {name.Evidence}".Trim(' ', '|'),
                "Alta",
                "Dati anagrafici Cerved");
        }
        else
        {
            denomination = new ExtractedField(
                NameFromFile(pdfPath),
                0,
                "Nome del file usato come fallback.",
                "Fallback",
                "Nome file");
        }

        return new CervedRecord
        {
            SourceFile = Path.GetFileName(pdfPath),
            DocumentType = CervedDocumentType.Person,
            Denominazione = denomination,
            Cognome = surname,
            Nome = name,
            CodiceFiscale = fiscalCode,
            PageCount = pages.Count,
            Pages = pages,
            ValidationStatus = ItalianValidators.IsPlausibleFiscalCode(fiscalCode.Value)
                ? "Dati persona validati"
                : "Da verificare"
        };
    }

    private static CervedRecord ParseUnknown(string pdfPath, IReadOnlyList<PageText> pages)
    {
        return new CervedRecord
        {
            SourceFile = Path.GetFileName(pdfPath),
            DocumentType = CervedDocumentType.Unknown,
            Denominazione = new ExtractedField(
                NameFromFile(pdfPath), 0, "Tipo documento non riconosciuto.",
                "Fallback", "Nome file"),
            PageCount = pages.Count,
            Pages = pages,
            ValidationStatus = "Formato Cerved non riconosciuto"
        };
    }

    private static ExtractedField FindValidatedNumber(
        IReadOnlyList<PageText> pages,
        IReadOnlyList<string> labels,
        Func<string, bool> validator,
        string method)
    {
        foreach (PageText page in pages.Take(5))
        {
            string[] lines = Lines(page.Text);

            for (int i = 0; i < lines.Length; i++)
            {
                if (!labels.Any(label => Contains(lines[i], label)))
                    continue;

                string evidence = string.Join(" | ", lines.Skip(i).Take(4));

                foreach (Match match in ElevenDigits.Matches(evidence))
                {
                    string value = match.Value;

                    if (validator(value))
                    {
                        return new ExtractedField(
                            value, page.Number, Limit(evidence, 500),
                            "Alta", method + " + controllo formale");
                    }
                }
            }
        }

        return ExtractedField.Empty(method);
    }

    private static ExtractedField FindFiscalCode(
        IReadOnlyList<PageText> pages,
        string excludedVat)
    {
        string[] labels = ["Codice Fiscale", "Codice fiscale", "C. F.", "C.F."];

        foreach (PageText page in pages.Take(5))
        {
            string[] lines = Lines(page.Text);

            for (int i = 0; i < lines.Length; i++)
            {
                if (!labels.Any(label => Contains(lines[i], label)))
                    continue;

                string evidence = string.Join(" | ", lines.Skip(i).Take(4)).ToUpperInvariant();

                Match personMatch = FiscalCode16.Match(evidence);
                if (personMatch.Success)
                {
                    return new ExtractedField(
                        personMatch.Value.ToUpperInvariant(),
                        page.Number,
                        Limit(evidence, 500),
                        "Alta",
                        "Codice fiscale persona + formato");
                }

                foreach (Match match in ElevenDigits.Matches(evidence))
                {
                    if (match.Value == excludedVat)
                    {
                        return new ExtractedField(
                            match.Value, page.Number, Limit(evidence, 500),
                            "Alta", "Codice fiscale impresa coincidente con P.IVA");
                    }

                    if (ItalianValidators.IsValidVat(match.Value))
                    {
                        return new ExtractedField(
                            match.Value, page.Number, Limit(evidence, 500),
                            "Media", "Codice fiscale numerico");
                    }
                }
            }
        }

        return ExtractedField.Empty("Codice fiscale");
    }

    private static ExtractedField FindLabelValue(
        IReadOnlyList<PageText> pages,
        IReadOnlyList<string> labels,
        IReadOnlyList<string> stopLabels,
        bool allowContinuation)
    {
        foreach (PageText page in pages.Take(5))
        {
            string[] lines = Lines(page.Text);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string? matchedLabel = labels.FirstOrDefault(label => Contains(line, label));

                if (matchedLabel is null)
                    continue;

                string value = RemoveLabel(line, matchedLabel);

                if (string.IsNullOrWhiteSpace(value) && i + 1 < lines.Length)
                    value = lines[i + 1];

                var collected = new List<string>();

                if (!string.IsNullOrWhiteSpace(value) &&
                    !IsStopLine(value, stopLabels))
                {
                    collected.Add(value);
                }

                if (allowContinuation)
                {
                    for (int j = i + 1; j < Math.Min(i + 5, lines.Length); j++)
                    {
                        string candidate = lines[j];

                        if (j == i + 1 && collected.Count > 0 && candidate == collected[0])
                            continue;

                        if (IsStopLine(candidate, stopLabels))
                            break;

                        if (LooksLikeSection(candidate))
                            break;

                        collected.Add(candidate);
                    }
                }

                string finalValue = Clean(string.Join(" ", collected));

                if (finalValue.Length >= 2)
                {
                    string evidence = string.Join(" | ", lines.Skip(i).Take(Math.Min(5, lines.Length - i)));

                    return new ExtractedField(
                        Limit(finalValue, 600),
                        page.Number,
                        Limit(evidence, 700),
                        "Alta",
                        "Etichetta Cerved");
                }
            }
        }

        return ExtractedField.Empty("Etichetta Cerved");
    }

    private static IReadOnlyList<string> StopLabels() =>
    [
        "Indirizzo Sede", "Codice Fiscale", "Partita Iva", "Partita IVA",
        "CCIAA/REA", "Forma Giuridica", "Situazione Impresa",
        "Attività Economica", "Attivita Economica", "Impresa Appartenente",
        "Nome Capogruppo", "Data Costituzione", "Data Iscrizione",
        "Data Inizio Attività", "Capitale Sociale", "Totale Quote",
        "Nr. Addetti", "Nr. Dipendenti", "Interrogazioni su Cerved Group",
        "Nr. Uffici", "Movimentazioni R. I.", "Sito Internet",
        "Telefono", "E-MAIL Certificata", "Sigla della denominazione"
    ];

    private static string ValidateCompany(
        ExtractedField vat,
        ExtractedField fiscalCode,
        ExtractedField denomination,
        ExtractedField activity)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(denomination.Value)) missing.Add("denominazione");
        if (!ItalianValidators.IsValidVat(vat.Value)) missing.Add("P.IVA");
        if (!ItalianValidators.IsPlausibleFiscalCode(fiscalCode.Value)) missing.Add("CF");
        if (string.IsNullOrWhiteSpace(activity.Value)) missing.Add("attività");

        return missing.Count == 0
            ? "Campi identificativi validati"
            : "Da verificare: " + string.Join(", ", missing);
    }

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Clean)
            .Where(x => x.Length > 0)
            .ToArray();

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string SearchText(string value) =>
        Regex.Replace(value.ToUpperInvariant(), @"\s+", "");

    private static bool Contains(string source, string value) =>
        SearchText(source).Contains(SearchText(value), StringComparison.Ordinal);

    private static string RemoveLabel(string line, string label)
    {
        string normalizedLine = SearchText(line);
        string normalizedLabel = SearchText(label);

        if (!normalizedLine.StartsWith(normalizedLabel, StringComparison.Ordinal))
        {
            int colon = line.IndexOf(':');
            return colon >= 0 && colon < line.Length - 1
                ? Clean(line[(colon + 1)..])
                : "";
        }

        string[] labelParts = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string result = line;

        foreach (string part in labelParts)
        {
            int index = result.IndexOf(part, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
                result = result.Remove(index, part.Length);
        }

        return Clean(result.TrimStart(':', '-', ' '));
    }

    private static bool IsStopLine(string value, IReadOnlyList<string> stopLabels) =>
        stopLabels.Any(label => Contains(value, label));

    private static bool LooksLikeSection(string value)
    {
        string v = value.Trim();
        return v.Length > 8 &&
               v.All(c => !char.IsLetter(c) || char.IsUpper(c)) &&
               !v.Any(char.IsDigit);
    }

    private static string Clean(string value) =>
        Regex.Replace(value, @"\s{2,}", " ").Trim();

    private static string Limit(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string NameFromFile(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        name = Regex.Replace(name, @"\s*-\s*Cerve[vd].*$", "", RegexOptions.IgnoreCase);
        return Clean(name.Replace('_', ' '));
    }
}
