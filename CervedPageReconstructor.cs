using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace MichMapper;

internal sealed class CervedPageReconstructor
{
    private const double LineTolerance = 2.8;

    public string Reconstruct(Page page)
    {
        var words = NearestNeighbourWordExtractor.Instance
            .GetWords(page.Letters)
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .OrderByDescending(word => word.BoundingBox.Bottom)
            .ThenBy(word => word.BoundingBox.Left)
            .ToArray();

        if (words.Length == 0)
            return page.Text ?? "";

        var lines = new List<List<Word>>();

        foreach (Word word in words)
        {
            List<Word>? line = lines.FirstOrDefault(existing =>
                Math.Abs(existing[0].BoundingBox.Bottom - word.BoundingBox.Bottom) <= LineTolerance);

            if (line is null)
            {
                line = [];
                lines.Add(line);
            }

            line.Add(word);
        }

        var result = new StringBuilder();

        foreach (List<Word> line in lines.OrderByDescending(x => x[0].BoundingBox.Bottom))
        {
            Word[] ordered = line.OrderBy(x => x.BoundingBox.Left).ToArray();

            for (int i = 0; i < ordered.Length; i++)
            {
                if (i > 0)
                    result.Append(' ');

                result.Append(ordered[i].Text);
            }

            result.AppendLine();
        }

        return result.ToString();
    }
}
