namespace MichMapper;

internal sealed record PdfFileInfo(string FullPath, string FileName, long SizeBytes)
{
    public string SizeText
    {
        get
        {
            double size = SizeBytes;
            string[] units = ["B", "KB", "MB", "GB"];
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return $"{size:0.##} {units[unit]}";
        }
    }
}
