using System.Diagnostics;

namespace MichMapper;

internal sealed class MainForm : Form
{
    private const string AppVersion = "3.13";

    private readonly PdfFolderScanner _scanner = new();
    private readonly CervedPdfReader _reader = new();
    private readonly ExcelExporter _excelExporter = new();
    private readonly HtmlExporter _htmlExporter = new();

    private readonly Button _selectButton = new();
    private readonly Button _analyseButton = new();
    private readonly Button _excelButton = new();
    private readonly Button _htmlButton = new();
    private readonly Button _clearButton = new();
    private readonly Label _folderLabel = new();
    private readonly Label _countLabel = new();
    private readonly ListView _list = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();

    private IReadOnlyList<PdfFileInfo> _files = [];
    private List<CervedRecord> _records = [];

    public MainForm()
    {
        Text = $"Mich Mapper {AppVersion} - Bookmark Table Parser";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);
        Size = new Size(1220, 800);
        Font = new Font("Segoe UI", 10F);

        var title = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 24F, FontStyle.Bold),
            Text = $"Mich Mapper {AppVersion}",
            Location = new Point(32, 22)
        };

        var subtitle = new Label
        {
            AutoSize = true,
            Text = "Lettura Cerved tramite segnalibri e tabelle standard",
            Location = new Point(36, 74)
        };

        SetupButton(_selectButton, "1. Seleziona cartella PDF", 36, 112, 220);
        SetupButton(_analyseButton, "2. Analizza Cerved", 270, 112, 175);
        SetupButton(_excelButton, "3. Esporta Excel", 459, 112, 160);
        SetupButton(_htmlButton, "Verifica HTML", 633, 112, 150);
        SetupButton(_clearButton, "Azzera", 797, 112, 105);

        _analyseButton.Enabled = false;
        _excelButton.Enabled = false;
        _htmlButton.Enabled = false;
        _clearButton.Enabled = false;

        _selectButton.Click += SelectButton_Click;
        _analyseButton.Click += AnalyseButton_Click;
        _excelButton.Click += ExcelButton_Click;
        _htmlButton.Click += HtmlButton_Click;
        _clearButton.Click += (_, _) => ClearAll();

        var caption = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Text = "Cartella selezionata:",
            Location = new Point(36, 176)
        };

        _folderLabel.Text = "Nessuna";
        _folderLabel.BorderStyle = BorderStyle.FixedSingle;
        _folderLabel.AutoEllipsis = true;
        _folderLabel.Location = new Point(36, 202);
        _folderLabel.Size = new Size(1128, 38);
        _folderLabel.Padding = new Padding(8);
        _folderLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        _countLabel.AutoSize = true;
        _countLabel.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        _countLabel.Text = "PDF trovati: 0";
        _countLabel.Location = new Point(36, 260);

        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.HideSelection = false;
        _list.View = View.Details;
        _list.Location = new Point(36, 296);
        _list.Size = new Size(1128, 390);
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom |
                       AnchorStyles.Left | AnchorStyles.Right;

        _list.Columns.Add("Nome file", 280);
        _list.Columns.Add("Tipo", 85);
        _list.Columns.Add("Denominazione / nominativo", 245);
        _list.Columns.Add("P.IVA", 115);
        _list.Columns.Add("CF", 145);
        _list.Columns.Add("Segnalibri", 145);
        _list.Columns.Add("Validazione", 245);

        _progress.Location = new Point(36, 705);
        _progress.Size = new Size(1128, 18);
        _progress.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        _status.AutoSize = true;
        _status.Text = "Pronto.";
        _status.Location = new Point(36, 735);
        _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

        Controls.AddRange([
            title, subtitle, _selectButton, _analyseButton,
            _excelButton, _htmlButton, _clearButton,
            caption, _folderLabel, _countLabel, _list,
            _progress, _status
        ]);
    }

    private static void SetupButton(Button button, string text, int x, int y, int width)
    {
        button.Text = text;
        button.Location = new Point(x, y);
        button.Size = new Size(width, 44);
    }

    private void SelectButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Seleziona la cartella contenente i dossier Cerved",
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            _files = _scanner.Scan(dialog.SelectedPath);
            _records = [];
            _list.Items.Clear();

            foreach (PdfFileInfo file in _files)
            {
                var item = new ListViewItem(file.FileName) { Tag = file.FullPath };
                for (int i = 0; i < 6; i++)
                    item.SubItems.Add("");
                _list.Items.Add(item);
            }

            _folderLabel.Text = dialog.SelectedPath;
            _countLabel.Text = $"PDF trovati: {_files.Count}";
            _analyseButton.Enabled = _files.Count > 0;
            _clearButton.Enabled = true;
            _excelButton.Enabled = false;
            _htmlButton.Enabled = false;
            _status.Text = "Cartella caricata. Premi «Analizza Cerved».";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void AnalyseButton_Click(object? sender, EventArgs e)
    {
        if (_files.Count == 0)
            return;

        SetBusy(true);
        _records = [];
        _progress.Minimum = 0;
        _progress.Maximum = _files.Count;
        _progress.Value = 0;

        try
        {
            for (int i = 0; i < _files.Count; i++)
            {
                PdfFileInfo file = _files[i];
                _status.Text = $"Analisi {i + 1}/{_files.Count}: {file.FileName}";
                Application.DoEvents();

                CervedRecord record = await Task.Run(() => _reader.Read(file.FullPath));
                _records.Add(record);

                ListViewItem item = _list.Items[i];
                item.SubItems[1].Text = record.DocumentType.ToString();
                item.SubItems[2].Text = record.Denominazione.Value;
                item.SubItems[3].Text = record.PartitaIva.Value;
                item.SubItems[4].Text = record.CodiceFiscale.Value;
                item.SubItems[5].Text = record.BookmarkStatus;
                item.SubItems[6].Text = record.ValidationStatus;

                _progress.Value = i + 1;
            }

            _excelButton.Enabled = true;
            _htmlButton.Enabled = true;
            _status.Text = $"Analisi completata: {_records.Count} dossier.";
        }
        catch (Exception ex)
        {
            ShowError($"Errore durante l'analisi:\n\n{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ExcelButton_Click(object? sender, EventArgs e)
    {
        if (_records.Count == 0)
            return;

        using var dialog = new SaveFileDialog
        {
            Filter = "File Excel (*.xlsx)|*.xlsx",
            FileName = $"Mich-Mapper-v{AppVersion}-{DateTime.Now:yyyyMMdd-HHmm}.xlsx"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            _excelExporter.Export(dialog.FileName, _records);
            Open(dialog.FileName);
            _status.Text = $"Excel v{AppVersion} creato correttamente.";
        }
        catch (Exception ex)
        {
            ShowError($"Errore nella creazione dell'Excel:\n\n{ex.Message}");
        }
    }

    private void HtmlButton_Click(object? sender, EventArgs e)
    {
        if (_records.Count == 0)
            return;

        using var dialog = new SaveFileDialog
        {
            Filter = "Pagina HTML (*.html)|*.html",
            FileName = $"Mich-Mapper-v{AppVersion}-Verifica-{DateTime.Now:yyyyMMdd-HHmm}.html"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            _htmlExporter.Export(dialog.FileName, _records);
            Open(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowError($"Errore nella creazione dell'HTML:\n\n{ex.Message}");
        }
    }

    private static void Open(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private void ClearAll()
    {
        _files = [];
        _records = [];
        _list.Items.Clear();
        _folderLabel.Text = "Nessuna";
        _countLabel.Text = "PDF trovati: 0";
        _progress.Value = 0;
        _status.Text = "Pronto.";
        _analyseButton.Enabled = false;
        _excelButton.Enabled = false;
        _htmlButton.Enabled = false;
        _clearButton.Enabled = false;
    }

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        _selectButton.Enabled = !busy;
        _analyseButton.Enabled = !busy && _files.Count > 0;
        Application.DoEvents();
    }

    private void ShowError(string message)
    {
        _status.Text = "Operazione non completata.";
        MessageBox.Show(
            this,
            message,
            $"Mich Mapper {AppVersion}",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
