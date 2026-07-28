using System.Diagnostics;

namespace MichMapper;

internal sealed class MainForm : Form
{
    private readonly PdfFolderScanner _scanner = new();
    private readonly Button _selectFolderButton;
    private readonly Button _clearButton;
    private readonly Label _folderLabel;
    private readonly Label _countLabel;
    private readonly ListView _pdfList;
    private readonly Label _statusLabel;

    public MainForm()
    {
        Text = "Mich Mapper 3.0";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 560);
        Size = new Size(1040, 720);
        Font = new Font("Segoe UI", 10F);

        var titleLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 24F, FontStyle.Bold),
            Text = "Mich Mapper 3.0",
            Location = new Point(32, 24)
        };

        var subtitleLabel = new Label
        {
            AutoSize = true,
            Text = "Seleziona una cartella e verifica i PDF Cerved presenti.",
            Location = new Point(36, 76)
        };

        _selectFolderButton = new Button
        {
            Text = "Seleziona cartella PDF",
            Location = new Point(36, 116),
            Size = new Size(220, 44)
        };
        _selectFolderButton.Click += SelectFolderButton_Click;

        _clearButton = new Button
        {
            Text = "Azzera",
            Location = new Point(270, 116),
            Size = new Size(110, 44),
            Enabled = false
        };
        _clearButton.Click += ClearButton_Click;

        var folderCaption = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Text = "Cartella selezionata:",
            Location = new Point(36, 184)
        };

        _folderLabel = new Label
        {
            AutoEllipsis = true,
            BorderStyle = BorderStyle.FixedSingle,
            Text = "Nessuna",
            Location = new Point(36, 210),
            Size = new Size(950, 38),
            Padding = new Padding(8)
        };

        _countLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            Text = "PDF trovati: 0",
            Location = new Point(36, 270)
        };

        _pdfList = new ListView
        {
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            Location = new Point(36, 308),
            Size = new Size(950, 300),
            View = View.Details,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        _pdfList.Columns.Add("Nome file", 730);
        _pdfList.Columns.Add("Dimensione", 170);
        _pdfList.DoubleClick += PdfList_DoubleClick;

        _statusLabel = new Label
        {
            AutoSize = true,
            Text = "Pronto.",
            Location = new Point(36, 630),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };

        Controls.Add(titleLabel);
        Controls.Add(subtitleLabel);
        Controls.Add(_selectFolderButton);
        Controls.Add(_clearButton);
        Controls.Add(folderCaption);
        Controls.Add(_folderLabel);
        Controls.Add(_countLabel);
        Controls.Add(_pdfList);
        Controls.Add(_statusLabel);

        Resize += MainForm_Resize;
    }

    private void SelectFolderButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Seleziona la cartella contenente i PDF Cerved",
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            LoadFolder(dialog.SelectedPath);
    }

    private void LoadFolder(string folderPath)
    {
        try
        {
            SetBusy(true, "Lettura della cartella in corso...");
            IReadOnlyList<PdfFileInfo> files = _scanner.Scan(folderPath);

            _pdfList.BeginUpdate();
            _pdfList.Items.Clear();

            foreach (PdfFileInfo file in files)
            {
                var item = new ListViewItem(file.FileName) { Tag = file.FullPath };
                item.SubItems.Add(file.SizeText);
                _pdfList.Items.Add(item);
            }

            _pdfList.EndUpdate();
            _folderLabel.Text = folderPath;
            _countLabel.Text = $"PDF trovati: {files.Count}";
            _clearButton.Enabled = true;
            _statusLabel.Text = files.Count == 0
                ? "Nessun PDF trovato nella cartella."
                : "Scansione completata correttamente.";

            if (files.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "Nella cartella selezionata non sono presenti file PDF.",
                    "Mich Mapper 3.0",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }
        catch (UnauthorizedAccessException)
        {
            ShowError("Il programma non ha il permesso di leggere la cartella selezionata.");
        }
        catch (Exception ex)
        {
            ShowError($"Errore durante la lettura della cartella:\n\n{ex.Message}");
        }
        finally
        {
            SetBusy(false, _statusLabel.Text);
        }
    }

    private void PdfList_DoubleClick(object? sender, EventArgs e)
    {
        if (_pdfList.SelectedItems.Count == 0)
            return;

        string? path = _pdfList.SelectedItems[0].Tag as string;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError($"Non è stato possibile aprire il PDF:\n\n{ex.Message}");
        }
    }

    private void ClearButton_Click(object? sender, EventArgs e)
    {
        _pdfList.Items.Clear();
        _folderLabel.Text = "Nessuna";
        _countLabel.Text = "PDF trovati: 0";
        _statusLabel.Text = "Pronto.";
        _clearButton.Enabled = false;
    }

    private void MainForm_Resize(object? sender, EventArgs e)
    {
        int availableWidth = _pdfList.ClientSize.Width;
        _pdfList.Columns[1].Width = 160;
        _pdfList.Columns[0].Width = Math.Max(300, availableWidth - 165);
    }

    private void SetBusy(bool busy, string status)
    {
        UseWaitCursor = busy;
        _selectFolderButton.Enabled = !busy;
        _statusLabel.Text = status;
    }

    private void ShowError(string message)
    {
        _statusLabel.Text = "Operazione non completata.";
        MessageBox.Show(this, message, "Mich Mapper 3.0", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
