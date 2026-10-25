using System.Diagnostics;
using System.Globalization;
using Vcns.VirtualCom.Bridge;
using Vcns.VirtualCom.Bridge.Configuration;
using Vcns.VirtualCom.Bridge.Diagnostics;
using Vcns.VirtualCom.Bridge.Server;
using Vcns.VirtualCom.Bridge.Transport;
using Vcns.VirtualCom.Bridge.VirtualPorts;

namespace Vcns.VirtualCom.Desktop;

/// <summary>Edits connection profiles, options and server ports. Changes take effect only when saved.</summary>
internal sealed class SettingsForm : Form
{
    private readonly BridgeEdition _edition;
    private string BaseTitle => $"{_edition.ProductName} — Settings";

    private readonly ConfigStore _store;
    private readonly SessionManager _sessions;
    private readonly Action<AppConfig> _onSaved;
    private readonly ServerSettingsPanel _serverPanel;
    private readonly TabControl _tabs = new();
    private AppConfig _saved;
    private PortInventory? _inventory;
    private bool _dirty;
    private bool _serverDirty;
    private bool _loading;
    private bool _busy;

    private readonly DataGridView _grid = new();
    private readonly DataGridViewCheckBoxColumn _enabledColumn = new();
    private readonly DataGridViewTextBoxColumn _customerColumn = new();
    private readonly DataGridViewTextBoxColumn _labelColumn = new();
    private readonly DataGridViewTextBoxColumn _deviceColumn = new();
    private readonly DataGridViewTextBoxColumn _notesColumn = new();
    private readonly TextBox _filter = new();
    private readonly Label _filterCount = new();
    private readonly DataGridViewTextBoxColumn _serverColumn = new();
    private readonly DataGridViewTextBoxColumn _portColumn = new();
    private readonly DataGridViewComboBoxColumn _comColumn = new();
    private readonly DataGridViewTextBoxColumn _statusColumn = new();
    private readonly DataGridViewTextBoxColumn _bridgeColumn = new();
    private readonly Label _driverStatus = new();
    private readonly LinkLabel _driverLink = new();
    private readonly Button _addButton = new();
    private readonly Button _removeButton = new();
    private readonly Button _createPortsButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _closeButton = new();
    private readonly NumericUpDown _connectTimeout = new();
    private readonly NumericUpDown _releaseInterval = new();
    private readonly NumericUpDown _inUseWindow = new();
    private readonly CheckBox _connectReminder = new();
    private readonly CheckBox _logPayload = new();
    private readonly ToolTip _toolTip = new();

    public SettingsForm(AppConfig saved, ConfigStore store, SessionManager sessions, Action<AppConfig> onSaved, Icon icon, BridgeEdition edition)
    {
        _edition = edition;
        _saved = saved;
        _store = store;
        _sessions = sessions;
        _onSaved = onSaved;
        Icon = icon;
        _serverPanel = new ServerSettingsPanel(edition, MarkServerDirty);

        BuildLayout();
        LoadFrom(saved);
        var server = ServerConfigStore.Load(new ServerLocations(edition).ConfigFile, out var serverWarning);
        _serverPanel.LoadFrom(server);
        _serverDirty = false;
        UpdateTitle();

        Load += async (_, _) =>
        {
            if (serverWarning is not null)
                Dialogs.Error("Server settings problem", serverWarning + " Saving the Server ports page replaces the file.", owner: this);
            await RefreshInventoryAsync();
        };
        FormClosing += OnFormClosing;
        _sessions.SessionChanged += OnSessionChanged;
        FormClosed += (_, _) => _sessions.SessionChanged -= OnSessionChanged;
    }

    private void BuildLayout()
    {
        SuspendLayout();
        Text = BaseTitle;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(1280, 660);
        MinimumSize = new Size(900, 540);
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(12);

        var intro = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
            Text = $"Each row maps one local virtual COM port, which {_edition.SerialApplication} opens, to one {_edition.DeviceNoun} endpoint. "
                + "Nothing connects until you start a profile from the tray menu (Start → COMx). Customer, Label and Device tell profiles "
                + "apart: the tray menu groups profiles by customer, and Device Manager shows each port's customer and label.",
            MaximumSize = new Size(1200, 0),
        };

        _driverStatus.AutoSize = true;
        _driverStatus.Margin = new Padding(0, 0, 6, 0);
        _driverStatus.Text = "Checking COM ports…";
        _driverLink.AutoSize = true;
        _driverLink.Text = "Download com0com";
        _driverLink.Visible = false;
        _driverLink.LinkClicked += (_, _) => OpenUrl(Com0comSetup.DownloadUrl);
        var driverRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        driverRow.Controls.AddRange([_driverStatus, _driverLink]);

        _filter.Width = 340;
        _filter.PlaceholderText = "Customer, label, device, address, COM port or notes";
        _filter.TextChanged += (_, _) => ApplyFilter();
        _filterCount.AutoSize = true;
        _filterCount.Margin = new Padding(8, 6, 0, 0);
        _filterCount.ForeColor = SystemColors.GrayText;
        var filterRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        filterRow.Controls.AddRange([new Label { Text = "&Filter:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, _filter, _filterCount]);

        ConfigureGrid();

        ConfigureButton(_addButton, "&Add", (_, _) => AddProfile());
        ConfigureButton(_removeButton, "&Remove", (_, _) => RemoveProfile());
        ConfigureButton(_createPortsButton, "Create missing &virtual ports…", async (_, _) => await CreateMissingPortsAsync());
        _toolTip.SetToolTip(_createPortsButton, "Creates a virtual COM port for each profile whose COM port doesn't exist yet. Requires administrator approval.");
        var gridButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 6) };
        gridButtons.Controls.AddRange([_addButton, _removeButton, _createPortsButton]);

        var options = BuildOptions();

        ConfigureButton(_saveButton, "&Save", async (_, _) => await SaveAsync());
        ConfigureButton(_closeButton, "Close", (_, _) => Close());
        var bottom = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 0),
        };
        bottom.Controls.AddRange([_closeButton, _saveButton]);

        var diagram = Flows.Client(_edition);
        diagram.Dock = DockStyle.Fill;

        var client = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(6) };
        client.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        client.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        client.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        client.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        client.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        client.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        client.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        client.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        client.Controls.Add(intro, 0, 0);
        client.Controls.Add(diagram, 0, 1);
        client.Controls.Add(driverRow, 0, 2);
        client.Controls.Add(filterRow, 0, 3);
        client.Controls.Add(_grid, 0, 4);
        client.Controls.Add(gridButtons, 0, 5);
        client.Controls.Add(options, 0, 6);

        _tabs.Dock = DockStyle.Fill;
        _tabs.TabPages.Add(new TabPage("Client profiles") { Controls = { client } });
        _tabs.TabPages.Add(new TabPage("Server ports") { Controls = { _serverPanel } });
        _tabs.TabPages.Add(new TabPage("About") { Controls = { new AboutPanel(_edition) } });

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_tabs, 0, 0);
        root.Controls.Add(bottom, 0, 1);
        Controls.Add(root);
        CancelButton = _closeButton;
        ResumeLayout(performLayout: true);
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.AllowUserToOrderColumns = false;
        _grid.RowHeadersVisible = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.EditMode = DataGridViewEditMode.EditOnEnter;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.FixedSingle;

        _enabledColumn.HeaderText = "Enabled";
        _enabledColumn.FillWeight = 7;
        _customerColumn.HeaderText = "Customer";
        _customerColumn.FillWeight = 16;
        _customerColumn.MaxInputLength = ConfigValidator.MaxNameLength;
        _customerColumn.ToolTipText = "Optional. Who or where the device belongs to, e.g. a customer and site. The tray menu groups profiles by it.";
        _labelColumn.HeaderText = "Label";
        _labelColumn.FillWeight = 16;
        _labelColumn.MaxInputLength = ConfigValidator.MaxNameLength;
        _labelColumn.ToolTipText = "Optional. What this port is for, e.g. \"Main hall LED wall\". Shown in the tray menu and in Device Manager.";
        _deviceColumn.HeaderText = "Device";
        _deviceColumn.FillWeight = 12;
        _deviceColumn.MaxInputLength = ConfigValidator.MaxNameLength;
        _deviceColumn.ToolTipText = $"Optional. The {_edition.DeviceNoun}'s make and model.";
        _notesColumn.HeaderText = "Notes";
        _notesColumn.FillWeight = 14;
        _notesColumn.MaxInputLength = ConfigValidator.MaxNotesLength;
        _notesColumn.ToolTipText = "Optional. Anything else worth knowing, such as the monitoring host to pause or who to call.";
        _serverColumn.HeaderText = "Target Server";
        _serverColumn.FillWeight = 16;
        _serverColumn.MaxInputLength = 253;
        _serverColumn.ToolTipText = $"IP address or host name of the {_edition.DeviceNoun}.";
        _portColumn.HeaderText = "Target Port";
        _portColumn.FillWeight = 9;
        _portColumn.MaxInputLength = 5;
        _portColumn.ToolTipText = _edition.DefaultTargetPort is { } usual
            ? $"TCP port on the {_edition.DeviceNoun}, normally {usual}."
            : $"TCP port on the {_edition.DeviceNoun}.";
        _comColumn.HeaderText = "Local COM Port";
        _comColumn.FillWeight = 12;
        _comColumn.DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton;
        _comColumn.ToolTipText = $"The virtual COM port {_edition.SerialApplication} opens. New numbers are chosen above every port already used on this PC.";
        _statusColumn.HeaderText = "Virtual Port";
        _statusColumn.FillWeight = 24;
        _statusColumn.ReadOnly = true;
        _statusColumn.DefaultCellStyle.ForeColor = SystemColors.GrayText;
        _bridgeColumn.HeaderText = "Proxy-side Port";
        _bridgeColumn.FillWeight = 9;
        _bridgeColumn.MaxInputLength = 32;
        _bridgeColumn.ToolTipText = "Optional. Leave blank for VCNS and com0com virtual ports. "
            + "Only needed with another virtual serial driver: enter the port at the other end of its pair.";
        _grid.Columns.AddRange(_enabledColumn, _customerColumn, _labelColumn, _serverColumn, _portColumn, _comColumn, _deviceColumn,
            _statusColumn, _notesColumn, _bridgeColumn);

        // Click a heading to sort, e.g. by customer when there are dozens of profiles.
        foreach (var column in new DataGridViewColumn[] { _customerColumn, _labelColumn, _serverColumn, _comColumn, _deviceColumn })
            column.SortMode = DataGridViewColumnSortMode.Automatic;
        _grid.EditingControlShowing += (_, e) => OfferCompletions(e.Control as TextBox);

        // Commit check box and drop-down changes immediately rather than when the cell loses focus.
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, e) =>
        {
            if (_loading || e.RowIndex < 0 || e.ColumnIndex == _statusColumn.Index)
                return;
            MarkDirty();
            if (e.ColumnIndex == _comColumn.Index || e.ColumnIndex == _bridgeColumn.Index)
                UpdateRowStatus(_grid.Rows[e.RowIndex]);
        };
        _grid.DataError += (_, e) => e.ThrowException = false;
        _grid.CurrentCellChanged += (_, _) => UpdateButtons();
    }

    private GroupBox BuildOptions()
    {
        _connectTimeout.Minimum = 1;
        _connectTimeout.Maximum = 120;
        _releaseInterval.Minimum = 0;
        _releaseInterval.Maximum = 60;
        _inUseWindow.Minimum = 0;
        _inUseWindow.Maximum = 10000;
        _inUseWindow.Increment = 100;
        foreach (var number in new[] { _connectTimeout, _releaseInterval, _inUseWindow })
        {
            number.Width = 80;
            number.TextAlign = HorizontalAlignment.Right;
            number.ValueChanged += (_, _) => { if (!_loading) MarkDirty(); };
        }

        var device = _edition.DeviceNoun;
        _connectReminder.Text = _edition.ConnectReminder?.OptionLabel ?? "";
        _connectReminder.AutoSize = true;
        _logPayload.Text = $"Log forwarded data in hex (troubleshooting only: may record {device} configuration)";
        _logPayload.AutoSize = true;
        foreach (var check in new[] { _connectReminder, _logPayload })
            check.CheckedChanged += (_, _) => { if (!_loading) MarkDirty(); };

        _toolTip.SetToolTip(_connectTimeout, $"How long to wait for the {device} to accept a connection.");
        _toolTip.SetToolTip(_releaseInterval, $"After a session to a {device} closes, wait this long before connecting to it again so it can release the session.");
        _toolTip.SetToolTip(_inUseWindow, $"If the {device} closes a new connection within this time, it is reported as in use by another client"
            + (_edition.OtherClientExample is { } example ? $" (e.g. {example})." : "."));

        var table = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 3 };
        table.Controls.Add(OptionLabel("Connect timeout (seconds):"), 0, 0);
        table.Controls.Add(_connectTimeout, 1, 0);
        table.Controls.Add(OptionLabel("Release interval after disconnect (seconds):"), 2, 0);
        table.Controls.Add(_releaseInterval, 3, 0);
        table.Controls.Add(OptionLabel("In-use detection window (milliseconds):"), 0, 1);
        table.Controls.Add(_inUseWindow, 1, 1);
        if (_edition.ConnectReminder is not null)
        {
            table.Controls.Add(_connectReminder, 0, 2);
            table.SetColumnSpan(_connectReminder, 2);
        }
        table.Controls.Add(_logPayload, 2, 2);
        table.SetColumnSpan(_logPayload, 2);

        var group = new GroupBox { Text = "Connection options", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 6) };
        group.Controls.Add(table);
        return group;
    }

    private static Label OptionLabel(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 6) };

    private static void ConfigureButton(Button button, string text, EventHandler onClick)
    {
        button.Text = text;
        button.AutoSize = true;
        button.MinimumSize = new Size(88, 0);
        button.Padding = new Padding(6, 0, 6, 0);
        button.Click += onClick;
    }

    private void LoadFrom(AppConfig config)
    {
        _loading = true;
        try
        {
            _grid.Rows.Clear();
            SetComChoices(config.Profiles.Select(p => p.LocalComPort));
            foreach (var profile in config.Profiles)
            {
                AddRow(profile.Id, profile.Enabled, profile.Customer ?? "", profile.Label ?? "", profile.TargetServer,
                    profile.TargetPort.ToString(CultureInfo.InvariantCulture), NullIfEmpty(profile.LocalComPort)!, profile.Device ?? "", "",
                    profile.Notes ?? "", profile.BridgePort ?? "");
            }

            var options = config.Options;
            _connectTimeout.Value = Math.Clamp(options.ConnectTimeoutSeconds, (int)_connectTimeout.Minimum, (int)_connectTimeout.Maximum);
            _releaseInterval.Value = Math.Clamp(options.ReleaseIntervalSeconds, (int)_releaseInterval.Minimum, (int)_releaseInterval.Maximum);
            _inUseWindow.Value = Math.Clamp(options.InUseDetectionMilliseconds, (int)_inUseWindow.Minimum, (int)_inUseWindow.Maximum);
            _connectReminder.Checked = options.ShowConnectReminder;
            _logPayload.Checked = options.LogPayload;
        }
        finally
        {
            _loading = false;
        }
        _dirty = false;
        UpdateTitle();
        RefreshRowStates();
        ApplyFilter();
    }

    private async Task RefreshInventoryAsync()
    {
        _driverStatus.Text = "Checking COM ports…";
        IReadOnlySet<int> takenTcp;
        try
        {
            (_inventory, takenTcp) = await Task.Run(() => (PortInventory.Capture(), (IReadOnlySet<int>)TcpPortSuggester.TakenOnThisPc()));
        }
        catch (Exception ex)
        {
            Log.Error("Reading the COM port list failed", ex);
            _driverStatus.Text = $"Couldn't read this PC's COM port list: {ex.Message}";
            return;
        }
        if (IsDisposed)
            return;

        var drivers = new List<string>();
        if (_inventory.VcnsDriverAvailable || _inventory.VcnsPorts.Count > 0)
            drivers.Add($"VCNS virtual COM{(_inventory.VcnsDriverAvailable ? " (included)" : "")}, {_inventory.VcnsPorts.Count} port(s)");
        if (_inventory.Com0comInstalled)
        {
            drivers.Add(Com0comSetup.FindSetupc(_saved.Options.SetupcPath) is null
                ? $"com0com, {_inventory.Pairs.Count} pair(s) (setupc.exe not found)"
                : $"com0com, {_inventory.Pairs.Count} pair(s)");
        }
        _driverStatus.Text = drivers.Count > 0
            ? "Virtual port drivers: " + string.Join("; ", drivers) + "."
            : "No virtual port driver: this build doesn't include the VCNS driver and com0com isn't installed.";
        _driverLink.Visible = !_inventory.VcnsDriverAvailable && !_inventory.Com0comInstalled;

        SetComChoices([]);
        RefreshRowStates();
        _serverPanel.SetInventory(_inventory, takenTcp);
    }

    /// <summary>Brings the Server ports page to the front, e.g. when opened from the tray's Server menu.</summary>
    public void ShowServerPage() => _tabs.SelectedIndex = 1;

    /// <summary>
    /// Fills the COM drop-down with the ports already in use by rows, existing virtual ports, and a few free numbers
    /// above everything in use on this PC.
    /// </summary>
    private void SetComChoices(IEnumerable<string> extra)
    {
        var names = new HashSet<string>(RowComPorts(), StringComparer.OrdinalIgnoreCase);
        names.UnionWith(extra.Where(s => !string.IsNullOrEmpty(s)));
        if (_inventory is not null)
        {
            names.UnionWith(_inventory.VirtualComPorts);
            names.UnionWith(_inventory.SuggestFreePorts(names, 6));
        }

        var ordered = names.OrderBy(ComOrder).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        if (_comColumn.Items.Cast<string>().SequenceEqual(ordered, StringComparer.OrdinalIgnoreCase))
            return;
        if (_grid.IsCurrentCellInEditMode && _grid.CurrentCell is DataGridViewComboBoxCell)
            _grid.EndEdit();
        _comColumn.Items.Clear();
        _comColumn.Items.AddRange(ordered);
    }

    private void AddProfile()
    {
        if (_inventory is null)
            return;
        _grid.EndEdit();
        var next = _inventory.SuggestFreePorts(RowComPorts(), 1).FirstOrDefault();
        if (next is null)
        {
            Dialogs.Error("No free COM port numbers", $"Every number up to COM{ConfigValidator.MaxComNumber} above the ports in use is taken.", owner: this);
            return;
        }

        // Profiles often come in batches for one customer, so a new row starts with the selected row's customer.
        var customer = _grid.CurrentRow is { } current ? CellText(current, _customerColumn) : "";
        _filter.Text = "";
        SetComChoices([next]);
        DataGridViewRow row;
        _loading = true;
        try
        {
            row = AddRow(Guid.NewGuid(), true, customer, "", "", _edition.DefaultTargetPort?.ToString(CultureInfo.InvariantCulture) ?? "", next, "", "", "", "");
        }
        finally
        {
            _loading = false;
        }
        SetComChoices([]);
        MarkDirty();
        UpdateRowStatus(row);
        ApplyFilter();
        _grid.CurrentCell = row.Cells[(customer.Length == 0 ? _customerColumn : _labelColumn).Index];
        _grid.BeginEdit(selectAll: true);
    }

    private void RemoveProfile()
    {
        if (_grid.CurrentRow is not { } row)
            return;
        if (ActiveSession(row) is not null)
        {
            Dialogs.Info("Profile is active", $"Disconnect {CellText(row, _comColumn)} from the tray menu before removing its profile.", this);
            return;
        }
        _grid.Rows.Remove(row);
        MarkDirty();
        SetComChoices([]);
        UpdateButtons();
        ApplyFilter();
    }

    /// <summary>Shows only rows with the filter text in any of their text columns.</summary>
    private void ApplyFilter()
    {
        var text = _filter.Text.Trim();
        if (text.Length > 0 && _grid.CurrentCell is not null)
        {
            // The current row can't be hidden, so leave it before filtering.
            _grid.EndEdit();
            _grid.CurrentCell = null;
        }
        var searched = new DataGridViewColumn[] { _customerColumn, _labelColumn, _deviceColumn, _serverColumn, _comColumn, _notesColumn };
        var shown = 0;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            row.Visible = text.Length == 0 || searched.Any(c => CellText(row, c).Contains(text, StringComparison.OrdinalIgnoreCase));
            if (row.Visible)
                shown++;
        }
        _filterCount.Text = text.Length == 0 ? $"{_grid.Rows.Count} profile(s)" : $"Showing {shown} of {_grid.Rows.Count}";
        UpdateButtons();
    }

    /// <summary>Customer and Device suggest what's already been typed elsewhere (and, for Device, the edition's models).</summary>
    private void OfferCompletions(TextBox? editor)
    {
        if (editor is null)
            return;
        // The grid reuses one text box for every column, so switch completion off unless this column wants it.
        var column = _grid.CurrentCell?.ColumnIndex;
        if (column != _customerColumn.Index && column != _deviceColumn.Index)
        {
            editor.AutoCompleteMode = AutoCompleteMode.None;
            return;
        }
        var values = new AutoCompleteStringCollection();
        values.AddRange(_grid.Rows.Cast<DataGridViewRow>()
            .Select(r => CellText(r, _grid.Columns[column.Value]))
            .Concat(column == _deviceColumn.Index ? _edition.DeviceSuggestions : [])
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
        editor.AutoCompleteCustomSource = values;
        editor.AutoCompleteSource = AutoCompleteSource.CustomSource;
        editor.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
    }

    private async Task<bool> SaveAsync()
    {
        if (_busy || !_grid.EndEdit())
            return false;
        if (!TryBuildConfig(out var config, out var errors))
        {
            ShowErrors(errors);
            return false;
        }

        errors.AddRange(ConfigValidator.Validate(config));
        errors.AddRange(ConfigValidator.ValidateActiveChanges(config, _sessions.ActiveSessions.Select(s => s.Profile)));
        if (_inventory is not null)
            errors.AddRange(ConfigValidator.ValidatePorts(config, _inventory));

        ServerConfig? server = null;
        if (_serverDirty)
        {
            if (!_serverPanel.TryBuild(out server, out var serverErrors))
                errors.AddRange(serverErrors);
            else
            {
                errors.AddRange(ServerConfigValidator.Validate(server));
                if (_inventory is not null)
                    errors.AddRange(ServerConfigValidator.ValidatePorts(server, _inventory));
            }
        }
        if (errors.Count > 0)
        {
            ShowErrors(errors);
            return false;
        }

        if (_dirty)
        {
            try
            {
                _store.Save(config);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigValidationException)
            {
                Log.Error("Saving the configuration failed", ex);
                Dialogs.Error("Couldn't save the configuration", $"{ex.Message}\n\nThe previously saved configuration has not been changed.", owner: this);
                return false;
            }

            var previous = _saved;
            _saved = config.Clone();
            _dirty = false;
            UpdateTitle();
            _onSaved(config.Clone());
            await OfferVirtualPortChangesAsync(previous, config);
        }

        if (server is not null)
        {
            var result = await RunJobAsync(new PortJob { Server = server });
            if (result.Succeeded)
            {
                _serverDirty = false;
                UpdateTitle();
            }
            _serverPanel.RefreshStatus();
            return result.Succeeded;
        }
        return true;
    }

    private bool TryBuildConfig(out AppConfig config, out List<string> errors)
    {
        errors = [];
        config = new AppConfig
        {
            Options = new ProxyOptions
            {
                ConnectTimeoutSeconds = (int)_connectTimeout.Value,
                ReleaseIntervalSeconds = (int)_releaseInterval.Value,
                InUseDetectionMilliseconds = (int)_inUseWindow.Value,
                ShowConnectReminder = _connectReminder.Checked,
                LogPayload = _logPayload.Checked,
                SetupcPath = _saved.Options.SetupcPath,
            },
        };

        foreach (DataGridViewRow row in _grid.Rows)
        {
            var portText = CellText(row, _portColumn);
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                errors.Add($"Row {row.Index + 1}: Target Port '{portText}' must be a whole number between 1 and 65535.");
            config.Profiles.Add(new ConnectionProfile
            {
                Id = row.Tag is Guid id ? id : Guid.NewGuid(),
                Enabled = row.Cells[_enabledColumn.Index].Value is true,
                TargetServer = CellText(row, _serverColumn),
                TargetPort = port,
                LocalComPort = CellText(row, _comColumn),
                BridgePort = NullIfEmpty(CellText(row, _bridgeColumn)),
                Customer = CellText(row, _customerColumn),
                Label = CellText(row, _labelColumn),
                Device = CellText(row, _deviceColumn),
                Notes = CellText(row, _notesColumn),
            });
        }
        ConfigStore.Normalise(config);
        return errors.Count == 0;
    }

    private async Task OfferVirtualPortChangesAsync(AppConfig previous, AppConfig current)
    {
        if (_inventory is null)
            return;
        var inventory = _inventory;
        var missing = MissingPorts(current.Profiles.Where(p => p.BridgePort is null).Select(p => p.LocalComPort));
        var stillUsed = current.Profiles.Select(p => p.LocalComPort).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var released = previous.Profiles
            .Where(p => p.BridgePort is null && !stillUsed.Contains(p.LocalComPort))
            .Select(p => p.LocalComPort)
            .ToList();
        var orphanedVcns = released.Where(inventory.IsVcnsPort).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var orphanedPairs = released
            .Select(inventory.PairOf)
            .OfType<Com0comPair>()
            .Where(pair => !stillUsed.Contains(pair.PortA) && !stillUsed.Contains(pair.PortB))
            .DistinctBy(pair => pair.Index)
            .ToList();
        var orphanNames = orphanedVcns.Concat(orphanedPairs.Select(p => p.PortA)).ToList();

        var job = new PortJob { SetupcPath = _saved.Options.SetupcPath };
        if (missing.Count > 0)
        {
            if (!CanCreatePorts(out var reason))
                Dialogs.Info("Saved", $"The configuration was saved, but {string.Join(", ", missing)} can't be used until its virtual port exists. {reason}", this);
            else if (ConfirmCreate(missing))
                AddCreations(job, missing);
        }
        if (orphanNames.Count > 0 && Dialogs.Confirm(
                $"Remove unused virtual port{(orphanNames.Count > 1 ? "s" : "")} {string.Join(", ", orphanNames)}?",
                "No profile uses them any more. Removing them frees their COM port numbers. Windows will ask for administrator approval.",
                "Remove", requiresAdmin: true, owner: this))
        {
            job.RemoveVcns.AddRange(orphanedVcns);
            job.RemoveCom0comPairs.AddRange(orphanedPairs.Select(p => p.Index));
        }

        // Device Manager names of existing VCNS ports whose customer or label changed. A blank name restores the
        // standard one, but a new profile without a name leaves its port alone.
        var renamed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in current.Profiles.Where(p => p.BridgePort is null && inventory.IsVcnsPort(p.LocalComPort)))
        {
            var before = previous.Profiles.FirstOrDefault(p => p.Id == profile.Id
                && string.Equals(p.LocalComPort, profile.LocalComPort, StringComparison.OrdinalIgnoreCase));
            if (profile.PortDisplayName != before?.PortDisplayName && !(before is null && profile.PortDisplayName is null))
                renamed[profile.LocalComPort] = profile.PortDisplayName;
        }
        if (renamed.Count > 0 && (!job.IsEmpty || Dialogs.Confirm(
                "Update the names shown in Device Manager?",
                string.Join("\n", renamed.Take(6).Select(r => $"{r.Key}: {r.Value ?? "VCNS Virtual COM Port"} ({r.Key})"))
                    + (renamed.Count > 6 ? $"\n…and {renamed.Count - 6} more." : "")
                    + "\n\nWindows will ask for administrator approval.",
                "Update", requiresAdmin: true, owner: this)))
        {
            foreach (var (port, name) in renamed)
                job.PortNames.TryAdd(port, name);
        }

        if (!job.IsEmpty)
            await RunJobAsync(job);
    }

    private async Task CreateMissingPortsAsync()
    {
        if (_inventory is null || _busy)
            return;
        _grid.EndEdit();
        var missing = MissingPorts(_grid.Rows.Cast<DataGridViewRow>()
            .Where(r => CellText(r, _bridgeColumn).Length == 0)
            .Select(r => CellText(r, _comColumn)));
        if (missing.Count == 0)
        {
            Dialogs.Info("Nothing to create", "Every profile's COM port already exists, belongs to another device, or uses a manual proxy-side port.", this);
            return;
        }
        if (!CanCreatePorts(out var reason))
        {
            Dialogs.Error("Can't create virtual ports", reason, owner: this);
            return;
        }
        if (ConfirmCreate(missing))
        {
            var job = new PortJob { SetupcPath = _saved.Options.SetupcPath };
            AddCreations(job, missing);
            await RunJobAsync(job);
        }
    }

    // New ports use the VCNS driver when this build includes it; com0com is the fallback.
    private void AddCreations(PortJob job, IEnumerable<string> ports)
    {
        if (!_inventory!.VcnsDriverAvailable)
        {
            job.CreateCom0com.AddRange(ports);
            return;
        }
        job.CreateVcns.AddRange(ports);

        // Name the new ports. Creating them also reinstalls the driver, which can reset existing ports' names, so
        // name every other VCNS port that has a customer or label too.
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var com = CellText(row, _comColumn);
            if (CellText(row, _bridgeColumn).Length > 0 || !(job.CreateVcns.Contains(com, StringComparer.OrdinalIgnoreCase) || _inventory.IsVcnsPort(com)))
                continue;
            if (RowPortName(row) is { } name)
                job.PortNames[com] = name;
        }
    }

    /// <summary>The Device Manager name the row's customer and label give its port, or null for the standard name.</summary>
    private string? RowPortName(DataGridViewRow row) => new ConnectionProfile
    {
        Customer = ConfigStore.Tidy(CellText(row, _customerColumn)),
        Label = ConfigStore.Tidy(CellText(row, _labelColumn)),
    }.PortDisplayName;

    private bool ConfirmCreate(IReadOnlyList<string> ports) => Dialogs.Confirm(
        $"Create virtual port{(ports.Count > 1 ? "s" : "")} {string.Join(", ", ports)}?",
        (_inventory!.VcnsDriverAvailable
            ? $"{_edition.SerialApplicationAtStart} opens the COM port and the proxy connects to its other end. The virtual COM driver is installed with it if needed. "
            : $"Each one is a com0com port pair: {_edition.SerialApplication} uses the COM port, and the proxy uses the hidden other end. ")
            + "Existing COM ports are not changed. Windows will ask for administrator approval.",
        "Create", requiresAdmin: true, owner: this);

    private async Task<PortJobResult> RunJobAsync(PortJob job)
    {
        SetBusy(true);
        _driverStatus.Text = "Waiting for administrator approval…";
        PortJobResult result;
        try
        {
            result = await VirtualPortSetup.RunElevatedAsync(job);
        }
        catch (Exception ex)
        {
            result = new PortJobResult { Log = ex.Message };
        }
        finally
        {
            SetBusy(false);
        }

        var created = job.CreateVcns.Concat(job.CreateCom0com).ToList();
        var removed = job.RemoveVcns.Concat(job.RemoveCom0comPairs.Select(i => $"com0com pair {i}"));
        var what = job.Server is not null ? "Server settings" : $"Virtual port changes (create: {string.Join(", ", created)}; remove: {string.Join(", ", removed)})";
        var summary = $"{what}: {(result.Declined ? "declined" : result.Succeeded ? "succeeded" : "failed")}.{Environment.NewLine}{result.Log.Trim()}";
        if (result.Succeeded)
            Log.Info(summary);
        else
            Log.Warn(summary);

        await RefreshInventoryAsync();
        if (result.Declined)
            return result;
        var notRunning = created.Where(p => _inventory is not null && _inventory.IsVirtual(p) && !_inventory.IsRunning(p)).ToList();
        if (!result.Succeeded)
        {
            Dialogs.Error(job.Server is not null ? "The server settings weren't applied" : "Some virtual port changes failed",
                "The details below are also in the log.", result.Log.Trim(), this);
        }
        else if (job.Server is not null)
        {
            Dialogs.Info(job.Server.Enabled ? "Server settings applied" : "Server mode switched off", result.Log.Trim(), this);
        }
        else if (notRunning.Count > 0)
        {
            Dialogs.Error($"{string.Join(", ", notRunning)} created, but Windows hasn't started it",
                $"The port exists, but its driver isn't running, so {_edition.SerialApplication} won't see it. In Device Manager, a warning icon with "
                + "Code 52 means Windows rejected the driver's signature. See \"Troubleshooting\" in the installation guide.",
                result.Log.Trim(), this);
        }
        else if (created.Count > 0)
        {
            Dialogs.Info("Virtual ports ready", $"{string.Join(", ", created)} can now be started from the tray menu.", this);
        }
        return result;
    }

    private List<string> MissingPorts(IEnumerable<string> ports) =>
        ports.Where(p => ConfigValidator.TryParseComNumber(p, out _)
                && !_inventory!.IsVirtual(p)
                && _inventory.ForeignUse(p) is null)
            .Select(p => p.ToUpperInvariant())
            .Distinct()
            .ToList();

    private bool CanCreatePorts(out string reason)
    {
        reason = "";
        if (_inventory is { VcnsDriverAvailable: true })
            return true;
        if (_inventory is not { Com0comInstalled: true })
        {
            reason = "This build doesn't include the VCNS virtual COM driver and com0com isn't installed. "
                     + "Use a build that includes the driver, or install com0com (link at the top of this window).";
            return false;
        }
        if (Com0comSetup.FindSetupc(_saved.Options.SetupcPath) is null)
        {
            reason = "com0com's setupc.exe wasn't found in its usual folder. Set \"setupcPath\" in the configuration file to its location.";
            return false;
        }
        return true;
    }

    private void RefreshRowStates()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var locked = ActiveSession(row) is not null;
            foreach (var column in new DataGridViewColumn[] { _serverColumn, _portColumn, _comColumn, _bridgeColumn })
            {
                row.Cells[column.Index].ReadOnly = locked;
                row.Cells[column.Index].Style.ForeColor = locked ? SystemColors.GrayText : Color.Empty;
            }
            UpdateRowStatus(row);
        }
        UpdateButtons();
    }

    private void UpdateRowStatus(DataGridViewRow row) => row.Cells[_statusColumn.Index].Value = DescribeRow(row);

    private string DescribeRow(DataGridViewRow row)
    {
        if (ActiveSession(row) is { } session)
            return $"{(session.State == SessionState.Connected ? "Connected" : "Active")}: disconnect it to change this row";
        var com = CellText(row, _comColumn);
        var bridge = CellText(row, _bridgeColumn);
        if (com.Length == 0)
            return "Choose a COM port";
        if (bridge.Length > 0)
            return $"Manual pairing: the proxy opens {bridge.ToUpperInvariant()}";
        return _inventory?.Describe(com) ?? "Checking ports…";
    }

    private void UpdateButtons()
    {
        var current = _grid.CurrentRow;
        _addButton.Enabled = !_busy && _inventory is not null;
        _removeButton.Enabled = !_busy && current is not null && ActiveSession(current) is null;
        _createPortsButton.Enabled = !_busy && _inventory is not null;
        _saveButton.Enabled = !_busy;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        _grid.Enabled = !busy;
        _closeButton.Enabled = !busy;
        _serverPanel.SetBusy(busy);
        UpdateButtons();
    }

    private bool IsDirty => _dirty || _serverDirty;

    private void MarkDirty()
    {
        if (_dirty)
            return;
        _dirty = true;
        UpdateTitle();
    }

    private void MarkServerDirty()
    {
        if (_serverDirty)
            return;
        _serverDirty = true;
        UpdateTitle();
    }

    private void UpdateTitle() => Text = IsDirty ? BaseTitle + " (unsaved changes)" : BaseTitle;

    private void ShowErrors(IReadOnlyCollection<string> errors) =>
        Dialogs.Error("Please fix these problems before saving", string.Join("\n", errors.Select(e => "• " + e)), owner: this);

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
            return;
        if (_busy)
        {
            e.Cancel = true;
            return;
        }
        _grid.EndEdit();
        if (!IsDirty)
            return;

        var choice = MessageBox.Show(this, "Save your changes before closing?", _edition.ProductName,
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (choice == DialogResult.No)
            return;
        e.Cancel = true;
        if (choice == DialogResult.Yes && await SaveAsync())
            Close();
    }

    private void OnSessionChanged(ProxySession session)
    {
        if (!IsHandleCreated || IsDisposed)
            return;
        try
        {
            BeginInvoke(RefreshRowStates);
        }
        catch (InvalidOperationException)
        {
            // The form is closing.
        }
    }

    private IEnumerable<string> RowComPorts() =>
        _grid.Rows.Cast<DataGridViewRow>().Select(r => CellText(r, _comColumn)).Where(s => s.Length > 0);

    /// <summary>
    /// Adds a row that already carries its profile ID. Inserting the first row makes it the current cell, which runs
    /// event handlers that read the ID before an Add-then-set-Tag sequence would have set it.
    /// </summary>
    private DataGridViewRow AddRow(Guid profileId, params object[] values)
    {
        var row = new DataGridViewRow { Tag = profileId };
        row.CreateCells(_grid, values);
        return _grid.Rows[_grid.Rows.Add(row)];
    }

    /// <summary>The running session for a row's profile; null for a row that isn't connected (or has no ID yet).</summary>
    private ProxySession? ActiveSession(DataGridViewRow row) => row.Tag is Guid id ? _sessions.Find(id) : null;

    private static string CellText(DataGridViewRow row, DataGridViewColumn column) =>
        (row.Cells[column.Index].Value as string ?? row.Cells[column.Index].Value?.ToString() ?? "").Trim();

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int ComOrder(string port) => ConfigValidator.TryParseComNumber(port, out var n) ? n : int.MaxValue;

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warn($"Could not open {url}: {ex.Message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _toolTip.Dispose();
        base.Dispose(disposing);
    }
}
