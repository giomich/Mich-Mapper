namespace MichMapper;

internal sealed class PdfFolderScanner
{
    public IReadOnlyList<PdfFileInfo> Scan(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            throw new ArgumentException("La cartella non è valida.", nameof(folderPath));

        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException("La cartella selezionata non esiste.");

        return Directory
            .EnumerateFiles(folderPath, "*.pdf", SearchOption.TopDirectoryOnly)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new PdfFileInfo(info.FullName, info.Name, info.Length);
            })
            .OrderBy(file => file.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
