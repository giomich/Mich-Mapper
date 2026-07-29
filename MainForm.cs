using System.Diagnostics;

namespace MichMapper;

internal sealed class MainForm : Form
{
    private readonly PdfFolderScanner _scanner = new();
    private readonly CervedPdfReader _reader = new();
    private readonly ExcelExporter _excelExporter = new();
    private readonly HtmlExporter _htmlExporter = new();

    private readonly Button _selectFolderButton = new();
    private readonly Button _analyzeButton = new();
    private readonly Button _exportExcelButton = new();
    private readonly Button _exportHtmlButton = new();
    private readonly Button _clearButton = new();
    private readonly Label _folderValueLabel = new();
    private readonly Label _countLabel = new();
    private readonly ListView _pdfList = new();
    private readonly Label _statusLabel = new();
    private readonly ProgressBar _progressBar = new();

    private string? _selectedFolder;
    private IReadOnlyList<PdfFileInfo> _pdfFiles = [];
    private List<CompanyRecord> _records = [];

    public MainForm()
    {
        Text = "Mich Mapper 3.1";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 620);
        Size = new Size(1120, 760);
        Font = new Font("Segoe UI", 10F);

        var title = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 24F, FontStyle.Bold),
            Text = "Mich Mapper 3.1",
            Location = new Point(32, 22)
        };

        var subtitle = new Label
        {
            AutoSize = true,
            Text = "Scansione e prima analisi locale dei PDF Cerved",
            Location = new Point(36, 74)
        };

        ConfigureButton(_selectFolderButton, "1. Seleziona cartella PDF", 36, 112, 220);
        ConfigureButton(_analyzeButton, "2. Analizza PDF", 270, 112, 160);
        ConfigureButton(_exportExcelButton, "3. Esporta Excel", 444, 112, 160);
        ConfigureButton(_exportHtmlButton, "Esporta anteprima HTML", 618, 112, 210);
        ConfigureButton(_clearButton, "Azzera", 842, 112, 105);

        _analyzeButton.Enabled = false;
        _exportExcelButton.Enabled = false;
        _exportHtmlButton.Enabled = false;
        _clearButton.Enabled = false;

        _selectFolderButton.Click += SelectFolderButton_Click;
        _analyzeButton.Click += AnalyzeButton_Click;
        _exportExcelButton.Click += ExportExcelButton_Click;
        _exportHtmlButton.Click += ExportHtmlButton_Click;
        _clearButton.Click += (_, _) => ClearResults();

        var folderCaption = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Text = "Cartella selezionata:",
            Location = new Point(36, 176)
        };

        _folderValueLabel.AutoEllipsis = true;
        _folderValueLabel.BorderStyle = BorderStyle.FixedSingle;
        _folderValueLabel.Text = "Nessuna";
        _folderValueLabel.Location = new Point(36, 202);
        _folderValueLabel.Size = new Size(1028, 38);
        _folderValueLabel.Padding = new Padding(8);
        _folderValueLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        _countLabel.AutoSize = true;
        _countLabel.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        _countLabel.Text = "PDF trovati: 0";
        _countLabel.Location = new Point(36, 260);

        _pdfList.FullRowSelect = true;
        _pdfList.GridLines = true;
        _pdfList.HideSelection = false;
        _pdfList.Location = new Point(36, 296);
        _pdfList.Size = new Size(1028, 350);
        _pdfList.View = View.Details;
        _pdfList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _pdfList.Columns.Add("Nome file", 420);
        _pdfList.Columns.Add("Dimensione", 110);
        _pdfList.Columns.Add("Denominazione rilevata", 280);
        _pdfList.Columns.Add("P.IVA / CF", 180);
        _pdfList.DoubleClick += PdfList_DoubleClick;

        _progressBar.Location = new Point(36, 664);
        _progressBar.Size = new Size(1028, 18);
        _progressBar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        _statusLabel.AutoSize = true;
        _statusLabel.Text = "Pronto.";
        _statusLabel.Location = new Point(36, 694);
        _statusLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

        Controls.AddRange([
            title, subtitle, _selectFolderButton, _analyzeButton,
            _exportExcelButton, _exportHtmlButton, _clearButton,
            folderCaption, _folderValueLabel, _countLabel,
            _pdfList, _progressBar, _statusLabel
        ]);
    }

    private static void ConfigureButton(Button button, string text, int x, int y, int width)
    {
        button.Text = text;
        button.Location = new Point(x, y);
        button.Size = new Size(width, 44);
    }

    private void SelectFolderButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Seleziona la cartella contenente i PDF Cerved",
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        LoadFolder(dialog.SelectedPath);
    }

    private void LoadFolder(string folderPath)
    {
        try
        {
            SetBusy(true, "Lettura della cartella in corso...");
            _selectedFolder = folderPath;
            _pdfFiles = _scanner.Scan(folderPath);
            _records = [];

            _pdfList.Items.Clear();

            foreach (PdfFileInfo file in _pdfFiles)
            {
                var item = new ListViewItem(file.FileName) { Tag = file.FullPath };
                item.SubItems.Add(file.SizeText);
                item.SubItems.Add("");
                item.SubItems.Add("");
                _pdfList.Items.Add(item);
            }

            _folderValueLabel.Text = folderPath;
            _countLabel.Text = $"PDF trovati: {_pdfFiles.Count}";
            _analyzeButton.Enabled = _pdfFiles.Count > 0;
            _clearButton.Enabled = true;
            _exportExcelButton.Enabled = false;
            _exportHtmlButton.Enabled = false;
            _statusLabel.Text = _pdfFiles.Count == 0
                ? "Nessun PDF trovato nella cartella."
                : "Cartella caricata. Premi «Analizza PDF».";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false, _statusLabel.Text);
        }
    }

    private async void AnalyzeButton_Click(object? sender, EventArgs e)
    {
        if (_pdfFiles.Count == 0)
            return;

        SetBusy(true, "Analisi PDF in corso...");
        _records = [];
        _progressBar.Minimum = 0;
        _progressBar.Maximum = _pdfFiles.Count;
        _progressBar.Value = 0;

        try
        {
            for (int i = 0; i < _pdfFiles.Count; i++)
            {
                PdfFileInfo file = _pdfFiles[i];
                _statusLabel.Text = $"Analisi {i + 1} di {_pdfFiles.Count}: {file.FileName}";
                Application.DoEvents();

                CompanyRecord record = await Task.Run(() => _reader.Read(file.FullPath));
                _records.Add(record);

                ListViewItem item = _pdfList.Items[i];
                item.SubItems[2].Text = record.Denominazione;
                item.SubItems[3].Text = !string.IsNullOrWhiteSpace(record.PartitaIva)
                    ? record.PartitaIva
                    : record.CodiceFiscale;

                _progressBar.Value = i + 1;
            }

            _exportExcelButton.Enabled = true;
            _exportHtmlButton.Enabled = true;
            _statusLabel.Text = $"Analisi completata: {_records.Count} PDF elaborati.";
        }
        catch (Exception ex)
        {
            ShowError($"Errore durante l'analisi:\n\n{ex.Message}");
        }
        finally
        {
            SetBusy(false, _statusLabel.Text);
        }
    }

    private void ExportExcelButton_Click(object? sender, EventArgs e)
    {
        if (_records.Count == 0)
            return;

        using var dialog = new SaveFileDialog
        {
            Title = "Salva il file Excel",
            Filter = "File Excel (*.xlsx)|*.xlsx",
            FileName = $"Mich-Mapper-{DateTime.Now:yyyyMMdd-HHmm}.xlsx"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            _excelExporter.Export(dialog.FileName, _records);
            _statusLabel.Text = "Excel generato correttamente.";
            OpenOutput(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowError($"Errore durante la creazione dell'Excel:\n\n{ex.Message}");
        }
    }

    private void ExportHtmlButton_Click(object? sender, EventArgs e)
    {
        if (_records.Count == 0)
            return;

        using var dialog = new SaveFileDialog
        {
            Title = "Salva l'anteprima HTML",
            Filter = "Pagina HTML (*.html)|*.html",
            FileName = $"Mich-Mapper-Anteprima-{DateTime.Now:yyyyMMdd-HHmm}.html"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            _htmlExporter.Export(dialog.FileName, _records);
            _statusLabel.Text = "Anteprima HTML generata correttamente.";
            OpenOutput(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowError($"Errore durante la creazione dell'HTML:\n\n{ex.Message}");
        }
    }

    private void PdfList_DoubleClick(object? sender, EventArgs e)
    {
        if (_pdfList.SelectedItems.Count == 0)
            return;

        string? path = _pdfList.SelectedItems[0].Tag as string;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            OpenOutput(path);
    }

    private static void OpenOutput(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void ClearResults()
    {
        _selectedFolder = null;
        _pdfFiles = [];
        _records = [];
        _pdfList.Items.Clear();
        _folderValueLabel.Text = "Nessuna";
        _countLabel.Text = "PDF trovati: 0";
        _progressBar.Value = 0;
        _statusLabel.Text = "Pronto.";
        _analyzeButton.Enabled = false;
        _exportExcelButton.Enabled = false;
        _exportHtmlButton.Enabled = false;
        _clearButton.Enabled = false;
    }

    private void SetBusy(bool busy, string status)
    {
        UseWaitCursor = busy;
        _selectFolderButton.Enabled = !busy;
        _analyzeButton.Enabled = !busy && _pdfFiles.Count > 0;
        _statusLabel.Text = status;
        Application.DoEvents();
    }

    private void ShowError(string message)
    {
        _statusLabel.Text = "Operazione non completata.";
        MessageBox.Show(this, message, "Mich Mapper 3.1",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
