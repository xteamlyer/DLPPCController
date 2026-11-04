using System.Drawing;

namespace DLPPCController;

internal sealed class MainForm : Form
{
    private readonly Label _gpuLabel = new();
    private readonly Label _driverLabel = new();
    private readonly Label _stateLabel = new();
    private readonly Label _detailsLabel = new();
    private readonly Label _messageLabel = new();
    private readonly Button _refreshButton = new();
    private readonly Button _enableButton = new();
    private readonly Button _disableButton = new();
    private readonly CheckBox _watchdogCheck = new();
    private readonly CheckBox _disableOnLaunchCheck = new();
    private readonly CheckBox _startupCheck = new();
    private readonly System.Windows.Forms.Timer _watchdogTimer = new();

    private NvidiaApi? _api;
    private AppSettings _settings = SettingsStore.Load();
    private bool _busy;

    public MainForm()
    {
        Text = "DLPPC Controller";
        MinimumSize = new Size(560, 410);
        Size = new Size(650, 470);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10F);
        BackColor = Color.FromArgb(248, 249, 251);

        BuildUi();

        _watchdogTimer.Interval = 5000;
        _watchdogTimer.Tick += (_, _) => WatchdogTick();

        Shown += (_, _) => InitializeApplication();
        FormClosing += (_, _) =>
        {
            _watchdogTimer.Stop();
            _api?.Dispose();
        };
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(22),
            ColumnCount = 1,
            RowCount = 7,
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        Controls.Add(root);

        var title = new Label
        {
            Text = "DLPPC Controller",
            Font = new Font("Segoe UI Semibold", 20F, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        root.Controls.Add(title, 0, 0);

        ConfigureInfoLabel(_gpuLabel);
        ConfigureInfoLabel(_driverLabel);
        root.Controls.Add(_gpuLabel, 0, 1);
        root.Controls.Add(_driverLabel, 0, 2);

        var statePanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(18),
            Margin = new Padding(0, 12, 0, 12)
        };
        statePanel.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(220, 225, 232));
            e.Graphics.DrawRectangle(pen, 0, 0, statePanel.Width - 1, statePanel.Height - 1);
        };

        _stateLabel.Dock = DockStyle.Top;
        _stateLabel.Height = 48;
        _stateLabel.Font = new Font("Segoe UI Semibold", 17F, FontStyle.Bold);
        _stateLabel.Text = "State: NOT CHECKED";

        _detailsLabel.Dock = DockStyle.Fill;
        _detailsLabel.ForeColor = Color.FromArgb(85, 94, 105);
        _detailsLabel.Text = "Policy details will appear here.";
        statePanel.Controls.Add(_detailsLabel);
        statePanel.Controls.Add(_stateLabel);
        root.Controls.Add(statePanel, 0, 3);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0)
        };

        ConfigureButton(_enableButton, "Enable", Color.FromArgb(37, 99, 235));
        ConfigureButton(_disableButton, "Disable", Color.FromArgb(185, 55, 55));
        ConfigureButton(_refreshButton, "Refresh", Color.FromArgb(75, 85, 99));
        _enableButton.Click += (_, _) => ApplyState(true);
        _disableButton.Click += (_, _) => ApplyState(false);
        _refreshButton.Click += (_, _) => RefreshStatus();

        actions.Controls.AddRange(new Control[] { _enableButton, _disableButton, _refreshButton });
        root.Controls.Add(actions, 0, 4);

        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0)
        };

        _disableOnLaunchCheck.Text = "Apply saved DLPPC state when the application starts";
        _disableOnLaunchCheck.AutoSize = true;
        _disableOnLaunchCheck.Checked = _settings.DisableDlppc;
        _disableOnLaunchCheck.CheckedChanged += (_, _) =>
        {
            _settings.DisableDlppc = _disableOnLaunchCheck.Checked;
            SaveSettings();
            if (_disableOnLaunchCheck.Checked)
                ApplyState(false);
        };

        _watchdogCheck.Text = "Watchdog: re-disable DLPPC after driver/GPU reset (polls every 5 seconds)";
        _watchdogCheck.AutoSize = true;
        _watchdogCheck.Checked = _settings.WatchdogEnabled;
        _watchdogCheck.CheckedChanged += (_, _) =>
        {
            _settings.WatchdogEnabled = _watchdogCheck.Checked;
            SaveSettings();
            if (_watchdogCheck.Checked)
            {
                _settings.DisableDlppc = true;
                _disableOnLaunchCheck.Checked = true;
                _watchdogTimer.Start();
                SetMessage("Watchdog enabled. Polling NVAPI can wake the discrete GPU.");
            }
            else
            {
                _watchdogTimer.Stop();
                SetMessage("Watchdog disabled.");
            }
        };

        _startupCheck.Text = "Start this application with Windows";
        _startupCheck.AutoSize = true;
        _startupCheck.Checked = StartupManager.IsEnabled();
        _startupCheck.CheckedChanged += (_, _) =>
        {
            try
            {
                StartupManager.SetEnabled(_startupCheck.Checked);
                SetMessage(_startupCheck.Checked ? "Added to Windows startup." : "Removed from Windows startup.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Startup configuration failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _startupCheck.CheckedChanged -= StartupCheckChangedNoop;
            }
        };

        options.Controls.Add(_disableOnLaunchCheck);
        options.Controls.Add(_watchdogCheck);
        options.Controls.Add(_startupCheck);
        root.Controls.Add(options, 0, 5);

        _messageLabel.Dock = DockStyle.Fill;
        _messageLabel.ForeColor = Color.FromArgb(85, 94, 105);
        _messageLabel.TextAlign = ContentAlignment.MiddleLeft;
        _messageLabel.Text = "Ready.";
        root.Controls.Add(_messageLabel, 0, 6);
    }

    private void InitializeApplication()
    {
        try
        {
            EnsureApi();
            _gpuLabel.Text = $"GPU: {_api!.Identity.Name}";
            _driverLabel.Text = $"NVIDIA driver: {_api.Identity.DriverVersion}  |  PCI device: 0x{_api.Identity.DeviceId:X4}";
            RefreshStatus();

            if (_settings.DisableDlppc)
                ApplyState(false);

            if (_settings.WatchdogEnabled)
                _watchdogTimer.Start();
        }
        catch (Exception ex)
        {
            SetUnavailable(ex.Message);
        }
    }

    private void EnsureApi()
    {
        _api ??= new NvidiaApi();
        _gpuLabel.Text = $"GPU: {_api.Identity.Name}";
        _driverLabel.Text = $"NVIDIA driver: {_api.Identity.DriverVersion}  |  PCI device: 0x{_api.Identity.DeviceId:X4}";
    }

    private void RefreshStatus()
    {
        if (_busy) return;
        _busy = true;
        SetButtons(false);

        try
        {
            EnsureApi();
            DlppcState? state = _api!.ReadDlppc();
            if (state is null)
            {
                _stateLabel.Text = "State: NOT SUPPORTED";
                _stateLabel.ForeColor = Color.DarkOrange;
                _detailsLabel.Text = "The driver did not report a DLPPC_1X policy. No changes were made.";
                SetMessage("This GPU/driver combination does not expose DLPPC.");
            }
            else
            {
                ShowState(state);
                SetMessage("Status read successfully.");
            }
        }
        catch (Exception ex)
        {
            _api?.Dispose();
            _api = null;
            SetUnavailable(ex.Message);
        }
        finally
        {
            _busy = false;
            SetButtons(true);
        }
    }

    private void ApplyState(bool active)
    {
        if (_busy) return;
        _busy = true;
        SetButtons(false);

        try
        {
            EnsureApi();
            DlppcState? current = _api!.ReadDlppc();
            if (current is null)
                throw new NotSupportedException("DLPPC_1X policy is not available on this GPU/driver.");

            if (current.Active != active)
                _api.SetDlppc(active);

            DlppcState? verified = _api.ReadDlppc();
            if (verified is null || verified.Active != active)
                throw new InvalidOperationException("DLPPC state verification failed.");

            ShowState(verified);
            SetMessage($"DLPPC {(active ? "enabled" : "disabled")}. Readback verification passed.");
        }
        catch (Exception ex)
        {
            _api?.Dispose();
            _api = null;
            SetUnavailable(ex.Message);
        }
        finally
        {
            _busy = false;
            SetButtons(true);
        }
    }

    private void WatchdogTick()
    {
        if (_busy || !_watchdogCheck.Checked)
            return;

        _busy = true;
        try
        {
            EnsureApi();
            DlppcState? state = _api!.ReadDlppc();
            if (state is null)
            {
                SetMessage("Watchdog: DLPPC policy is unavailable.");
                return;
            }

            if (state.Active)
            {
                _api.SetDlppc(false);
                SetMessage("Watchdog: DLPPC was re-enabled; disabled again.");
            }

            ShowState(_api.ReadDlppc()!);
        }
        catch (Exception ex)
        {
            _api?.Dispose();
            _api = null;
            SetMessage($"Watchdog error: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowState(DlppcState state)
    {
        _stateLabel.Text = state.Active ? "State: ENABLED" : "State: DISABLED";
        _stateLabel.ForeColor = state.Active ? Color.DarkOrange : Color.ForestGreen;
        _detailsLabel.Text =
            $"Policy index: {state.PolicyIndex}{Environment.NewLine}" +
            $"Controller mask: 0x{state.ControllerMask:X8}{Environment.NewLine}" +
            "The state is the driver's requested policy activation flag.";
    }

    private void SetUnavailable(string message)
    {
        _stateLabel.Text = "State: UNAVAILABLE";
        _stateLabel.ForeColor = Color.Firebrick;
        _detailsLabel.Text = message;
        SetMessage("Could not communicate with the NVIDIA driver.");
    }

    private void SetMessage(string message) => _messageLabel.Text = message;

    private void SetButtons(bool enabled)
    {
        _enableButton.Enabled = enabled;
        _disableButton.Enabled = enabled;
        _refreshButton.Enabled = enabled;
    }

    private static void ConfigureInfoLabel(Label label)
    {
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.ForeColor = Color.FromArgb(75, 85, 99);
    }

    private static void ConfigureButton(Button button, string text, Color color)
    {
        button.Text = text;
        button.Width = 112;
        button.Height = 38;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = color;
        button.ForeColor = Color.White;
        button.Margin = new Padding(0, 0, 10, 0);
        button.Cursor = Cursors.Hand;
    }

    private void SaveSettings()
    {
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { SetMessage($"Could not save settings: {ex.Message}"); }
    }

    private void StartupCheckChangedNoop(object? sender, EventArgs e) { }
}

internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DLPPCController";

    public static bool IsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, true)
            ?? throw new InvalidOperationException("Could not open the current-user startup registry key.");

        if (enabled)
        {
            string path = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not determine executable path.");
            key.SetValue(ValueName, $"\"{path}\"");
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}
