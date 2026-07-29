namespace MichMapper;

internal sealed record ExtractedField(
    string Value,
    int Page,
    string Evidence,
    string Confidence,
    string Method)
{
    public static ExtractedField Empty(string method = "Non trovato") =>
        new("", 0, "", "Non trovato", method);
}
