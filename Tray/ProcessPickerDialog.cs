using System.Diagnostics;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Tray;

/// <summary>
/// Picks a running process to configure a ManagedApp from, instead of hand-typing its process name.
///
/// Typing was the error-prone path: ProcessName must match exactly (no .exe), it's what the window
/// rules key on, and getting it wrong fails silently — the rules simply never find anything.
/// Confirmed live 2026-08-22: SlimeVR runs five same-named processes and only one owns the GUI
/// window, which is impossible to know from a text box. This list marks window ownership and sorts
/// those entries first, so that shape is visible while configuring rather than a silent no-op later.
///
/// Idea borrowed from tomaae/AppSupervisor, which offers pickers for running processes, executables,
/// Steam apps and services rather than free-text fields.
/// </summary>
public sealed class ProcessPickerDialog : Form
{
    private readonly ListView _list;

    /// <summary>Process name with no .exe — what ManagedApp.ProcessName wants.</summary>
    public string SelectedProcessName { get; private set; } = "";

    /// <summary>Full path to the executable, or empty when it couldn't be read (common for
    /// processes owned by another user or elevated above us — not an error worth blocking on).</summary>
    public string SelectedExePath { get; private set; } = "";

    public ProcessPickerDialog()
    {
        Text = "Pick a running application";
        Width = 700;
        Height = 520;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
        };
        _list.Columns.Add("Process", 160);
        _list.Columns.Add("Window", 240);
        _list.Columns.Add("PID", 70, HorizontalAlignment.Right);
        _list.Columns.Add("Path", 190);
        _list.DoubleClick += (_, _) => AcceptSelection();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(8),
        };
        var ok = new Button { Text = "Select", AutoSize = true, DialogResult = DialogResult.None };
        ok.Click += (_, _) => AcceptSelection();
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += (_, _) => Populate();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(refresh);

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(8, 8, 8, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Applications owning a window are listed first — those are the ones window rules can act on.",
        };

        Controls.Add(_list);
        Controls.Add(buttons);
        Controls.Add(hint);
        CancelButton = cancel;

        Populate();
    }

    private void Populate()
    {
        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (var entry in EnumerateProcesses())
        {
            var item = new ListViewItem(entry.Name);
            item.SubItems.Add(entry.WindowTitle);
            item.SubItems.Add(entry.Pid.ToString());
            item.SubItems.Add(entry.ExePath);
            item.Tag = entry;
            // Greyed = no window, so window rules would have nothing to act on. Still selectable:
            // a headless helper is a legitimate thing to track presence of.
            if (!entry.HasWindow) item.ForeColor = SystemColors.GrayText;
            _list.Items.Add(item);
        }

        _list.EndUpdate();
    }

    private sealed record ProcessEntry(string Name, string WindowTitle, int Pid, string ExePath, bool HasWindow);

    /// <summary>One row per process, windowed first then alphabetical. Deduplicated by name+window
    /// state so a 37-process Chrome doesn't bury everything else — the distinction that matters
    /// when configuring an app is "this name, and whether it has a window", not every instance.</summary>
    private static List<ProcessEntry> EnumerateProcesses()
    {
        var entries = new List<ProcessEntry>();
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception ex)
        {
            Log.Warn("ProcessPicker", $"Enumerating processes failed: {ex.Message}");
            return entries;
        }

        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in all)
            {
                try
                {
                    var hasWindow = p.MainWindowHandle != IntPtr.Zero;
                    // Keep the windowed instance of a name AND one windowless representative, so a
                    // multi-process app like SlimeVR shows its GUI process rather than whichever
                    // instance happened to enumerate first.
                    var key = $"{p.ProcessName}|{hasWindow}";
                    if (!seen.Add(key)) continue;

                    var path = "";
                    try { path = p.MainModule?.FileName ?? ""; }
                    catch { /* elevated or cross-user process — path simply isn't readable */ }

                    entries.Add(new ProcessEntry(p.ProcessName, hasWindow ? p.MainWindowTitle : "", p.Id, path, hasWindow));
                }
                catch { /* process exited mid-enumeration — skip it */ }
            }
        }
        finally
        {
            foreach (var p in all) p.Dispose();
        }

        return entries
            .OrderByDescending(e => e.HasWindow)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void AcceptSelection()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not ProcessEntry entry)
        {
            MessageBox.Show(this, "Select an application first.", "VR Session Monitor",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SelectedProcessName = entry.Name;
        SelectedExePath = entry.ExePath;
        DialogResult = DialogResult.OK;
        Close();
    }
}
