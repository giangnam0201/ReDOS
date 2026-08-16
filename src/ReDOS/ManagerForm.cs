using System.Diagnostics;
using System.Windows.Forms;

namespace ReDOS;

/// <summary>
/// The ReDOS manager: a window onto the sandbox. Adding, running and deleting programs, plus a
/// shortcut into Explorer for anything this list does not cover.
/// </summary>
internal sealed class ManagerForm : Form
{
    private readonly ListView _programs;
    private readonly Label _status;
    private readonly Button _runButton;
    private readonly Button _deleteButton;

    internal ManagerForm()
    {
        Text = "ReDOS manager";
        Width = 760;
        Height = 480;
        MinimumSize = new Size(620, 380);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        _programs = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
        };
        _programs.Columns.Add("Program", 150);
        _programs.Columns.Add("DOS path", 210);
        _programs.Columns.Add("Executable", 130);
        _programs.Columns.Add("Size", 80, HorizontalAlignment.Right);
        _programs.Columns.Add("Last changed", 130);
        _programs.DoubleClick += (_, _) => RunSelected();
        _programs.SelectedIndexChanged += (_, _) => UpdateButtons();

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8, 8, 8, 4),
            WrapContents = true,
        };

        _runButton = AddButton(toolbar, "Run", RunSelected);
        AddButton(toolbar, "Add program...", AddProgram);
        _deleteButton = AddButton(toolbar, "Delete", DeleteSelected);
        AddButton(toolbar, "Open C:\\ in Explorer", () => Sandbox.OpenInExplorer());
        AddButton(toolbar, "DOS prompt", OpenPrompt);
        AddButton(toolbar, "Refresh", Reload);
        AddButton(toolbar, "Reset sandbox...", ResetSandbox);

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 46,
            Padding = new Padding(10, 6, 10, 6),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var hint = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 26,
            Padding = new Padding(10, 4, 10, 4),
            Text = "Every program below shares one virtual drive C:. Drop files into the sandbox folder and DOS sees them straight away.",
            ForeColor = SystemColors.GrayText,
        };

        Controls.Add(_programs);
        Controls.Add(hint);
        Controls.Add(toolbar);
        Controls.Add(_status);

        AllowDrop = true;
        DragEnter += (_, e) =>
            e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += OnFilesDropped;

        Reload();
    }

    private static Button AddButton(Control parent, string text, Action onClick)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
        button.Click += (_, _) => onClick();
        parent.Controls.Add(button);
        return button;
    }

    private void Reload()
    {
        Sandbox.Ensure();
        _programs.BeginUpdate();
        _programs.Items.Clear();

        foreach (var program in Sandbox.ListPrograms())
        {
            var item = new ListViewItem(program.Name) { Tag = program };
            item.SubItems.Add($"C:\\PROGRAMS\\{program.Name}");
            item.SubItems.Add(program.Executable is null ? "(none found)" : Path.GetFileName(program.Executable));
            item.SubItems.Add(FormatSize(program.SizeBytes));
            item.SubItems.Add(program.Modified.ToString("yyyy-MM-dd HH:mm"));
            _programs.Items.Add(item);
        }

        _programs.EndUpdate();

        string core = CoreProvider.Find() is { } path ? Path.GetFileName(path) : "not downloaded yet";
        _status.Text =
            $"Sandbox (drive C:):  {Sandbox.Root}\n" +
            $"{_programs.Items.Count} program(s)   ·   core: {core}   ·   " +
            $"associations: {(ShellIntegration.IsInstalled() ? "enabled" : "not enabled - run ReDOS install")}";

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool hasSelection = _programs.SelectedItems.Count > 0;
        _runButton.Enabled = hasSelection;
        _deleteButton.Enabled = hasSelection;
    }

    private Sandbox.InstalledProgram? Selected =>
        _programs.SelectedItems.Count > 0 ? (Sandbox.InstalledProgram)_programs.SelectedItems[0].Tag! : null;

    private void RunSelected()
    {
        if (Selected is not { } program) return;
        if (program.Executable is null)
        {
            MessageBox.Show(this,
                $"No DOS executable was found in {program.Name}.\n\nOpen the folder to check what is inside.",
                "ReDOS", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        StartDetached("run", program.Executable);
    }

    private void AddProgram()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Add a DOS program to the sandbox",
            Filter = "DOS programs (*.exe;*.com;*.bat)|*.exe;*.com;*.bat|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ImportFiles([dialog.FileName]);
    }

    private void OnFilesDropped(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) ImportFiles(paths);
    }

    private void ImportFiles(IReadOnlyList<string> paths)
    {
        var failures = new List<string>();
        foreach (string path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    // A dropped folder: import the first DOS executable in it, folder and all.
                    string? exe = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                        .FirstOrDefault(f => DosDetector.IsDosKind(DosDetector.Detect(f)));

                    if (exe is null) { failures.Add($"{Path.GetFileName(path)}: no DOS executable inside"); continue; }
                    Sandbox.Import(exe, Path.GetFileName(path));
                }
                else
                {
                    Sandbox.Import(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        Reload();

        if (failures.Count > 0)
        {
            MessageBox.Show(this, "Some items could not be added:\n\n" + string.Join('\n', failures),
                "ReDOS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DeleteSelected()
    {
        if (Selected is not { } program) return;

        var answer = MessageBox.Show(this,
            $"Delete {program.Name} and everything in it?\n\n{program.Directory}\n\nThis cannot be undone.",
            "ReDOS", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes) return;

        try
        {
            Sandbox.RemoveProgram(program.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            MessageBox.Show(this, ex.Message, "ReDOS", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        Reload();
    }

    private void ResetSandbox()
    {
        var answer = MessageBox.Show(this,
            "Reset the sandbox to a fresh machine?\n\nEvery imported program, save file and document in " +
            $"{Sandbox.Root} will be deleted.",
            "ReDOS", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes) return;

        try
        {
            Sandbox.Reset();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "ReDOS", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        Reload();
    }

    private void OpenPrompt() => StartDetached("dos");

    /// <summary>Launch a second ReDOS instance so the manager window stays responsive.</summary>
    private void StartDetached(params string[] args)
    {
        var psi = new ProcessStartInfo(AppPaths.ExecutablePath) { UseShellExecute = false };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        try
        {
            using var _ = Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "ReDOS", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
