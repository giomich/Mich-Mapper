namespace MichMapper;

/// <summary>Predisposizione per la futura estrazione locale dei dati dai PDF Cerved.</summary>
internal sealed class CervedPdfReader
{
    public Task ReadAsync(string pdfPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(pdfPath))
            throw new FileNotFoundException("PDF non trovato.", pdfPath);

        return Task.CompletedTask;
    }
}
