using System.Net;
using System.Text;

namespace MichMapper;

internal sealed class HtmlExporter
{
    public void Export(string path, IReadOnlyList<CervedRecord> records)
    {
        var html = new StringBuilder("""
<!doctype html>
<html lang="it">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Mich Mapper 3.3 - Verifica Cerved</title>
<style>
body{font-family:Segoe UI,Arial,sans-serif;margin:32px;background:#f4f6f8;color:#1f2937}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(330px,1fr));gap:18px}
.card{background:#fff;border:1px solid #d1d5db;border-radius:12px;padding:18px}
.kind{font-size:12px;color:#4b5563;margin-bottom:8px}
.row{margin-top:12px}.label{font-size:12px;color:#6b7280}
.value{font-size:16px;word-break:break-word}.meta{font-size:12px;color:#374151}
.status{margin-top:16px;padding-top:12px;border-top:1px solid #e5e7eb;font-weight:600}
</style>
</head>
<body>
<h1>Mich Mapper 3.3</h1>
<p>Controllo della lettura specifica dei dossier Cerved.</p>
<div class="grid">
""");

        foreach (CervedRecord record in records)
        {
            html.Append("<section class=\"card\"><div class=\"kind\">")
                .Append(WebUtility.HtmlEncode(record.DocumentType.ToString()))
                .AppendLine("</div>");

            Add(html, "Denominazione / nominativo", record.Denominazione);
            Add(html, "Partita IVA", record.PartitaIva);
            Add(html, "Codice fiscale", record.CodiceFiscale);
            Add(html, "Attività economica", record.AttivitaEconomica);
            Add(html, "Forma giuridica", record.FormaGiuridica);
            Add(html, "Situazione impresa", record.SituazioneImpresa);
            Add(html, "REA", record.Rea);

            html.Append("<div class=\"status\">")
                .Append(WebUtility.HtmlEncode(record.ValidationStatus))
                .AppendLine("</div></section>");
        }

        html.AppendLine("</div></body></html>");
        File.WriteAllText(path, html.ToString(), Encoding.UTF8);
    }

    private static void Add(StringBuilder html, string label, ExtractedField field)
    {
        html.Append("<div class=\"row\"><div class=\"label\">")
            .Append(WebUtility.HtmlEncode(label))
            .Append("</div><div class=\"value\">")
            .Append(WebUtility.HtmlEncode(
                string.IsNullOrWhiteSpace(field.Value) ? "Non trovato" : field.Value))
            .Append("</div><div class=\"meta\">Pagina ")
            .Append(field.Page)
            .Append(" · ")
            .Append(WebUtility.HtmlEncode(field.Confidence))
            .Append(" · ")
            .Append(WebUtility.HtmlEncode(field.Method))
            .AppendLine("</div></div>");
    }
}
