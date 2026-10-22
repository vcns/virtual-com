using System.Diagnostics;
using System.Globalization;
using NovaStarProxy.Configuration;
using NovaStarProxy.Diagnostics;
using NovaStarProxy.Transport;
using NovaStarProxy.VirtualPorts;

namespace NovaStarProxy.UI;

/// <summary>Edits connection profiles and options. Changes take effect only when saved.</summary>
internal sealed class SettingsForm : Form
{
    private const string BaseTitle = "NovaStar Proxy — Settings";

    private readonly ConfigStore _store;
    private readonly SessionManager _sessions;
    private readonly Action<AppConfig> _onSaved;
    private AppConfig _saved;
    private PortInventory? _inventory;
    private bool _dirty;
    private bool _loading;
    private bool _busy;

    private readonly DataGridView _grid = new();
    private readonly DataGridViewCheckBoxColumn _enabledColumn = new();
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
    private readonly CheckBox _zabbixReminder = new();
    private readonly CheckBox _logPayload = new();
    private readonly ToolTip _toolTip = new();

    public SettingsForm(AppConfig saved, ConfigStore store, SessionManager sessions, Action<AppConfig> onSaved, Icon icon)
    {
        _saved = saved;
        _store = store;
        _sessions = sessions;
        _onSaved = onSaved;
        Icon = icon;

        BuildLayout();
        LoadFrom(saved);

        Load += async (_, _) => await RefreshInventoryAsync();
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
        ClientSize = new Size(980, 580);
        MinimumSize = new Size(780, 480);
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(12);

        var intro = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
            Text = "Each row maps one local virtual COM port, which NovaLCT opens, to one controller endpoint. "
                + "Nothing connects until you start a profile from the tray menu (Start → COMx).",
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

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(intro, 0, 0);
        root.Controls.Add(driverRow, 0, 1);
        root.Controls.Add(_grid, 0, 2);
        root.Controls.Add(gridButtons, 0, 3);
        root.Controls.Add(options, 0, 4);
        root.Controls.Add(bottom, 0, 5);
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
        _enabledColumn.FillWeight = 8;
        _serverColumn.HeaderText = "Target Server";
        _serverColumn.FillWeight = 24;
        _serverColumn.MaxInputLength = 253;
        _serverColumn.ToolTipText = "IP address or host name of the NovaStar controller.";
        _portColumn.HeaderText = "Target Port";
        _portColumn.FillWeight = 9;
        _portColumn.MaxInputLength = 5;
        _portColumn.ToolTipText = "TCP port on the controller, normally 5200.";
        _comColumn.HeaderText = "Local COM Port";
        _comColumn.FillWeight = 12;
        _comColumn.DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton;
        _comColumn.ToolTipText = "The virtual COM port NovaLCT opens. New numbers are chosen above every port already used on this PC.";
        _statusColumn.HeaderText = "Virtual Port";
        _statusColumn.FillWeight = 33;
        _statusColumn.ReadOnly = true;
        _statusColumn.DefaultCellStyle.ForeColor = SystemColors.GrayText;
        _bridgeColumn.HeaderText = "Proxy-side Port";
        _bridgeColumn.FillWeight = 12;
        _bridgeColumn.MaxInputLength = 32;
        _bridgeColumn.ToolTipText = "Optional. Leave blank for VCNS and com0com virtual ports. "
            + "Only needed with another virtual serial driver: enter the port at the other end of its pair.";
        _grid.Columns.AddRange(_enabledColumn, _serverColumn, _portColumn, _comColumn, _statusColumn, _bridgeColumn);

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

        _zabbixReminder.Text = "Remind me to pause Zabbix polling before connecting";
        _zabbixReminder.AutoSize = true;
        _logPayload.Text = "Log forwarded data in hex (troubleshooting only: may record controller configuration)";
        _logPayload.AutoSize = true;
        foreach (var check in new[] { _zabbixReminder, _logPayload })
            check.CheckedChanged += (_, _) => { if (!_loading) MarkDirty(); };

        _toolTip.SetToolTip(_connectTimeout, "How long to wait for the controller to accept a connection.");
        _toolTip.SetToolTip(_releaseInterval, "After a session to a controller closes, wait this long before connecting to it again so it can release the session.");
        _toolTip.SetToolTip(_inUseWindow, "If the controller closes a new connection within this time, it is reported as in use by another client (e.g. Zabbix).");

        var table = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 3 };
        table.Controls.Add(OptionLabel("Connect timeout (seconds):"), 0, 0);
        table.Controls.Add(_connectTimeout, 1, 0);
        table.Controls.Add(OptionLabel("Release interval after disconnect (seconds):"), 2, 0);
        table.Controls.Add(_releaseInterval, 3, 0);
        table.Controls.Add(OptionLabel("In-use detection window (milliseconds):"), 0, 1);
        table.Controls.Add(_inUseWindow, 1, 1);
        table.Controls.Add(_zabbixReminder, 0, 2);
        table.SetColumnSpan(_zabbixReminder, 2);
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
                var index = _grid.Rows.Add(profile.Enabled, profile.TargetServer, profile.TargetPort.ToString(CultureInfo.InvariantCulture),
                    NullIfEmpty(profile.LocalComPort)!, "", profile.BridgePort ?? "");
                _grid.Rows[index].Tag = profile.Id;
            }

            var options = config.Options;
            _connectTimeout.Value = Math.Clamp(options.ConnectTimeoutSeconds, (int)_connectTimeout.Minimum, (int)_connectTimeout.Maximum);
            _releaseInterval.Value = Math.Clamp(options.ReleaseIntervalSeconds, (int)_releaseInterval.Minimum, (int)_releaseInterval.Maximum);
            _inUseWindow.Value = Math.Clamp(options.InUseDetectionMilliseconds, (int)_inUseWindow.Minimum, (int)_inUseWindow.Maximum);
            _zabbixReminder.Checked = options.ShowZabbixReminder;
            _logPayload.Checked = options.LogPayload;
        }
        finally
        {
            _loading = false;
        }
        _dirty = false;
        UpdateTitle();
        RefreshRowStates();
    }

    private async Task RefreshInventoryAsync()
    {
        _driverStatus.Text = "Checking COM ports…";
        try
        {
            _inventory = await Task.Run(() => PortInventory.Capture());
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
    }

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

        SetComChoices([next]);
        DataGridViewRow row;
        _loading = true;
        try
        {
            row = _grid.Rows[_grid.Rows.Add(true, "", "5200", next, "", "")];
            row.Tag = Guid.NewGuid();
        }
        finally
        {
            _loading = false;
        }
        SetComChoices([]);
        MarkDirty();
        UpdateRowStatus(row);
        _grid.CurrentCell = row.Cells[_serverColumn.Index];
        _grid.BeginEdit(selectAll: true);
    }

    private void RemoveProfile()
    {
        if (_grid.CurrentRow is not { } row)
            return;
        if (_sessions.Find((Guid)row.Tag!) is not null)
        {
            Dialogs.Info("Profile is active", $"Disconnect {CellText(row, _comColumn)} from the tray menu before removing its profile.", this);
            return;
        }
        _grid.Rows.Remove(row);
        MarkDirty();
        SetComChoices([]);
        UpdateButtons();
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
        if (errors.Count > 0)
        {
            ShowErrors(errors);
            return false;
        }

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
                ShowZabbixReminder = _zabbixReminder.Checked,
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
                Id = (Guid)row.Tag!,
                Enabled = row.Cells[_enabledColumn.Index].Value is true,
                TargetServer = CellText(row, _serverColumn),
                TargetPort = port,
                LocalComPort = CellText(row, _comColumn),
                BridgePort = NullIfEmpty(CellText(row, _bridgeColumn)),
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
        if (_inventory!.VcnsDriverAvailable)
            job.CreateVcns.AddRange(ports);
        else
            job.CreateCom0com.AddRange(ports);
    }

    private bool ConfirmCreate(IReadOnlyList<string> ports) => Dialogs.Confirm(
        $"Create virtual port{(ports.Count > 1 ? "s" : "")} {string.Join(", ", ports)}?",
        (_inventory!.VcnsDriverAvailable
            ? "NovaLCT opens the COM port and the proxy connects to its other end. The virtual COM driver is installed with it if needed. "
            : "Each one is a com0com port pair: NovaLCT uses the COM port, and the proxy uses the hidden other end. ")
            + "Existing COM ports are not changed. Windows will ask for administrator approval.",
        "Create", requiresAdmin: true, owner: this);

    private async Task RunJobAsync(PortJob job)
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
        var summary = $"Virtual port changes (create: {string.Join(", ", created)}; remove: {string.Join(", ", removed)}): "
            + $"{(result.Declined ? "declined" : result.Succeeded ? "succeeded" : "failed")}.{Environment.NewLine}{result.Log.Trim()}";
        if (result.Succeeded)
            Log.Info(summary);
        else
            Log.Warn(summary);

        await RefreshInventoryAsync();
        if (result.Declined)
            return;
        var notRunning = created.Where(p => _inventory is not null && _inventory.IsVirtual(p) && !_inventory.IsRunning(p)).ToList();
        if (!result.Succeeded)
        {
            Dialogs.Error("Some virtual port changes failed", "The details below are also in the log.", result.Log.Trim(), this);
        }
        else if (notRunning.Count > 0)
        {
            Dialogs.Error($"{string.Join(", ", notRunning)} created, but Windows hasn't started it",
                "The port exists, but its driver isn't running, so NovaLCT won't see it. In Device Manager, a warning icon with "
                + "Code 52 means Windows rejected the driver's signature. See \"Troubleshooting\" in the installation guide.",
                result.Log.Trim(), this);
        }
        else if (created.Count > 0)
        {
            Dialogs.Info("Virtual ports ready", $"{string.Join(", ", created)} can now be started from the tray menu.", this);
        }
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
            var locked = _sessions.Find((Guid)row.Tag!) is not null;
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
        if (_sessions.Find((Guid)row.Tag!) is { } session)
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
        _removeButton.Enabled = !_busy && current is not null && _sessions.Find((Guid)current.Tag!) is null;
        _createPortsButton.Enabled = !_busy && _inventory is not null;
        _saveButton.Enabled = !_busy;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        _grid.Enabled = !busy;
        _closeButton.Enabled = !busy;
        UpdateButtons();
    }

    private void MarkDirty()
    {
        if (_dirty)
            return;
        _dirty = true;
        UpdateTitle();
    }

    private void UpdateTitle() => Text = _dirty ? BaseTitle + " (unsaved changes)" : BaseTitle;

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
        if (!_dirty)
            return;

        var choice = MessageBox.Show(this, "Save your changes before closing?", "NovaStar Proxy",
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
