using System.Net;
using System.Text;

namespace MichMapper;

internal sealed class HtmlExporter
{
    public void Export(string outputPath, IReadOnlyList<CompanyRecord> records)
    {
        var html = new StringBuilder();

        html.AppendLine("""
<!doctype html>
<html lang="it">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Mich Mapper 3.2 - Anteprima dati</title>
<style>
body{font-family:Segoe UI,Arial,sans-serif;margin:32px;background:#f4f6f8;color:#1f2937}
h1{margin-bottom:8px}
.note{margin-bottom:24px;color:#4b5563}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:18px}
.card{background:white;border:1px solid #d1d5db;border-radius:12px;padding:18px}
.label{font-size:12px;color:#6b7280;margin-top:10px}
.value{font-size:16px;word-break:break-word}
</style>
</head>
<body>
<h1>Mich Mapper 3.2</h1>
<div class="note">Anteprima dei dati estratti localmente dai PDF.</div>
<div class="grid">
""");

        foreach (CompanyRecord record in records)
        {
            html.AppendLine("<section class=\"card\">");
            AddValue(html, "Denominazione", record.Denominazione);
            AddValue(html, "Partita IVA", record.PartitaIva);
            AddValue(html, "Codice fiscale", record.CodiceFiscale);
            AddValue(html, "Attività", record.Attivita);
            AddValue(html, "File", record.SourceFile);
            AddValue(html, "Pagine", record.PageCount.ToString());
            html.AppendLine("</section>");
        }

        html.AppendLine("</div></body></html>");
        File.WriteAllText(outputPath, html.ToString(), Encoding.UTF8);
    }

    private static void AddValue(StringBuilder html, string label, string value)
    {
        html.Append("<div class=\"label\">")
            .Append(WebUtility.HtmlEncode(label))
            .AppendLine("</div>");

        html.Append("<div class=\"value\">")
            .Append(WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(value) ? "Non rilevato" : value))
            .AppendLine("</div>");
    }
}
