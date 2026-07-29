namespace MichMapper;

internal enum CervedDocumentType
{
    Unknown,
    Company,
    Person
}

internal sealed class CervedRecord
{
    public string SourceFile { get; init; } = "";
    public CervedDocumentType DocumentType { get; init; }
    public ExtractedField Denominazione { get; init; } = ExtractedField.Empty();
    public ExtractedField Cognome { get; init; } = ExtractedField.Empty();
    public ExtractedField Nome { get; init; } = ExtractedField.Empty();
    public ExtractedField PartitaIva { get; init; } = ExtractedField.Empty();
    public ExtractedField CodiceFiscale { get; init; } = ExtractedField.Empty();
    public ExtractedField AttivitaEconomica { get; init; } = ExtractedField.Empty();
    public ExtractedField FormaGiuridica { get; init; } = ExtractedField.Empty();
    public ExtractedField SituazioneImpresa { get; init; } = ExtractedField.Empty();
    public ExtractedField Rea { get; init; } = ExtractedField.Empty();
    public ExtractedField DataCostituzione { get; init; } = ExtractedField.Empty();
    public int PageCount { get; init; }
    public IReadOnlyList<PageText> Pages { get; init; } = [];
    public string ValidationStatus { get; init; } = "";
}

internal sealed record PageText(int Number, string Text);
