using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Vcns.VirtualCom.Bridge;
using Vcns.VirtualCom.Bridge.Configuration;
using Vcns.VirtualCom.Bridge.Server;
using Vcns.VirtualCom.Bridge.VirtualPorts;

namespace Vcns.VirtualCom.Desktop;

/// <summary>
/// The Settings window's "Server ports" page: this PC's COM ports, each shared on a TCP port by the bridge's Windows
/// service. Changes are applied by the elevated helper when the window is saved.
/// </summary>
internal sealed class ServerSettingsPanel : UserControl
{
    private const string AllInterfaces = "0.0.0.0";
    private static readonly string[] Formats = ["8N1", "8E1", "8O1", "8N2", "7E1", "7O1", "7N1", "7E2"];
    private static readonly (SerialFlowControl Value, string Text)[] FlowChoices =
        [(SerialFlowControl.None, "None"), (SerialFlowControl.RtsCts, "RTS/CTS"), (SerialFlowControl.XonXoff, "XON/XOFF")];

    private readonly BridgeEdition _edition;
    private readonly ServerLocations _locations;
    private readonly Action _markDirty;
    private readonly CheckBox _enabled = new();
    private readonly Label _serviceStatus = new();
    private readonly LinkLabel _openLogs = new();
    private readonly DataGridView _grid = new();
    private readonly DataGridViewCheckBoxColumn _onColumn = new();
    private readonly DataGridViewComboBoxColumn _comColumn = new();
    private readonly DataGridViewTextBoxColumn _labelColumn = new();
    private readonly DataGridViewTextBoxColumn _tcpColumn = new();
    private readonly DataGridViewComboBoxColumn _listenColumn = new();
    private readonly DataGridViewTextBoxColumn _clientsColumn = new();
    private readonly DataGridViewTextBoxColumn _baudColumn = new();
    private readonly DataGridViewComboBoxColumn _formatColumn = new();
    private readonly DataGridViewComboBoxColumn _flowColumn = new();
    private readonly DataGridViewTextBoxColumn _statusColumn = new();
    private readonly Button _addButton = new();
    private readonly Button _removeButton = new();
    private readonly CheckBox _logPayload = new();
    private readonly ToolTip _toolTip = new();
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 3000 };
    private PortInventory? _inventory;
    private IReadOnlySet<int> _takenTcp = new HashSet<int>();
    private ServerSnapshot? _snapshot;
    private bool _loading;
    private bool _busy;

    public ServerSettingsPanel(BridgeEdition edition, Action markDirty)
    {
        _edition = edition;
        _locations = new ServerLocations(edition);
        _markDirty = markDirty;
        BuildLayout();
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
    }

    private void BuildLayout()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(6);

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(940, 0),
            Margin = new Padding(0, 0, 0, 6),
            Text = "Share this PC's COM ports, such as USB-attached devices, on the network. Each row listens on one TCP port and passes "
                + "bytes to and from one COM port, one client at a time, like a serial device server. A Windows service does the work, so "
                + "the ports stay available when nobody is logged in. Saving changes here asks for administrator approval, and copies "
                + $"this program to {_locations.ProgramDirectory} for the service if it's running from somewhere else.",
        };

        _enabled.Text = "&Share COM ports on the network (server mode)";
        _enabled.AutoSize = true;
        _enabled.CheckedChanged += (_, _) =>
        {
            if (!_loading)
                _markDirty();
            UpdateStatuses();
        };

        _serviceStatus.AutoSize = true;
        _serviceStatus.Margin = new Padding(0, 4, 8, 0);
        _openLogs.AutoSize = true;
        _openLogs.Margin = new Padding(0, 4, 0, 0);
        _openLogs.Text = "Open server log folder";
        _openLogs.LinkClicked += (_, _) => OpenFolder(_locations.LogDirectory);
        var statusRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        statusRow.Controls.AddRange([_serviceStatus, _openLogs]);

        ConfigureGrid();

        ConfigureButton(_addButton, "A&dd", (_, _) => AddRow());
        ConfigureButton(_removeButton, "Re&move", (_, _) => RemoveRow());
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 6) };
        buttons.Controls.AddRange([_addButton, _removeButton]);

        _logPayload.Text = "Log forwarded data in hex (troubleshooting only: may record device configuration)";
        _logPayload.AutoSize = true;
        _logPayload.CheckedChanged += (_, _) => { if (!_loading) _markDirty(); };

        var diagram = Flows.Server(_edition);
        diagram.Dock = DockStyle.Fill;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var style in new[] { SizeType.AutoSize, SizeType.AutoSize, SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize, SizeType.AutoSize })
            root.RowStyles.Add(style == SizeType.Percent ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        root.Controls.Add(intro, 0, 0);
        root.Controls.Add(diagram, 0, 1);
        root.Controls.Add(_enabled, 0, 2);
        root.Controls.Add(statusRow, 0, 3);
        root.Controls.Add(_grid, 0, 4);
        root.Controls.Add(buttons, 0, 5);
        root.Controls.Add(_logPayload, 0, 6);
        Controls.Add(root);
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.EditMode = DataGridViewEditMode.EditOnEnter;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.FixedSingle;

        _onColumn.HeaderText = "Enabled";
        _onColumn.FillWeight = 7;
        _comColumn.HeaderText = "COM Port";
        _comColumn.FillWeight = 9;
        _comColumn.DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton;
        _comColumn.ToolTipText = "The COM port to share. The list shows the ports present on this PC now.";
        _labelColumn.HeaderText = "Label";
        _labelColumn.FillWeight = 14;
        _labelColumn.MaxInputLength = ConfigValidator.MaxNameLength;
        _labelColumn.ToolTipText = "Optional. What's attached, e.g. \"Acme Leeds – MCTRL300 #1\". Shown in the status, tray menu and server log.";
        _tcpColumn.HeaderText = "TCP Port";
        _tcpColumn.FillWeight = 8;
        _tcpColumn.MaxInputLength = 5;
        _tcpColumn.ToolTipText = $"Clients connect to this port. Suggested as {_edition.ServerPortBase} plus the COM number, e.g. COM3 on {_edition.ServerPortBase + 3}.";
        _listenColumn.HeaderText = "Listen On";
        _listenColumn.FillWeight = 16;
        _listenColumn.DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton;
        _listenColumn.ValueMember = nameof(Choice.Value);
        _listenColumn.DisplayMember = nameof(Choice.Display);
        _listenColumn.ToolTipText = "The network address to accept connections on. One interface is safer than all of them.";
        _clientsColumn.HeaderText = "Allowed Clients";
        _clientsColumn.FillWeight = 20;
        _clientsColumn.ToolTipText = "Addresses or ranges that may connect, separated by commas, e.g. 10.20.0.15, 10.30.0.0/24. Use 0.0.0.0/0 to allow any.";
        _baudColumn.HeaderText = "Baud";
        _baudColumn.FillWeight = 8;
        _baudColumn.MaxInputLength = 7;
        _baudColumn.ToolTipText = "The device's baud rate. Raw TCP clients can't set it, so it's fixed here.";
        _formatColumn.HeaderText = "Format";
        _formatColumn.FillWeight = 7;
        _formatColumn.DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton;
        _formatColumn.Items.AddRange(Formats);
        _formatColumn.ToolTipText = "Data bits, parity and stop bits, e.g. 8N1.";
        _flowColumn.HeaderText = "Flow Control";
        _flowColumn.FillWeight = 9;
        _flowColumn.DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton;
        _flowColumn.Items.AddRange(FlowChoices.Select(c => (object)c.Text).ToArray());
        _statusColumn.HeaderText = "Status";
        _statusColumn.FillWeight = 22;
        _statusColumn.ReadOnly = true;
        _statusColumn.DefaultCellStyle.ForeColor = SystemColors.GrayText;
        _grid.Columns.AddRange(_onColumn, _comColumn, _labelColumn, _tcpColumn, _listenColumn, _clientsColumn, _baudColumn, _formatColumn, _flowColumn, _statusColumn);

        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, e) =>
        {
            if (_loading || e.RowIndex < 0 || e.ColumnIndex == _statusColumn.Index)
                return;
            var row = _grid.Rows[e.RowIndex];
            if (e.ColumnIndex == _tcpColumn.Index)
                row.Cells[_tcpColumn.Index].Tag = null;
            else if (e.ColumnIndex == _comColumn.Index && row.Cells[_tcpColumn.Index].Tag is true)
                SuggestTcpPort(row);
            _markDirty();
            UpdateStatuses();
        };
        _grid.DataError += (_, e) => e.ThrowException = false;
        _grid.CurrentCellChanged += (_, _) => UpdateButtons();
    }

    private static void ConfigureButton(Button button, string text, EventHandler onClick)
    {
        button.Text = text;
        button.AutoSize = true;
        button.MinimumSize = new Size(88, 0);
        button.Padding = new Padding(6, 0, 6, 0);
        button.Click += onClick;
    }

    public void LoadFrom(ServerConfig config)
    {
        _loading = true;
        try
        {
            _grid.Rows.Clear();
            SetComChoices(config.Ports.Select(p => p.ComPort));
            SetListenChoices(config.Ports.Select(p => p.ListenAddress));
            foreach (var format in config.Ports.Select(p => p.Serial.Format).Where(f => !_formatColumn.Items.Contains(f)).Distinct())
                _formatColumn.Items.Add(format);
            _enabled.Checked = config.Enabled;
            _logPayload.Checked = config.LogPayload;
            foreach (var port in config.Ports)
            {
                AddGridRow(port.Id, port.Enabled, NullIfEmpty(port.ComPort)!, port.Label ?? "", port.TcpPort.ToString(CultureInfo.InvariantCulture),
                    port.ListenAddress, string.Join(", ", port.AllowedClients), port.Serial.BaudRate.ToString(CultureInfo.InvariantCulture),
                    port.Serial.Format, FlowText(port.Serial.FlowControl), "");
            }
        }
        finally
        {
            _loading = false;
        }
        RefreshStatus();
        UpdateButtons();
    }

    /// <summary>Called once the PC's COM ports and busy TCP ports are known.</summary>
    public void SetInventory(PortInventory inventory, IReadOnlySet<int> takenTcpPorts)
    {
        _inventory = inventory;
        _takenTcp = takenTcpPorts;
        SetComChoices([]);
        UpdateStatuses();
        UpdateButtons();
    }

    public bool TryBuild(out ServerConfig config, out List<string> errors)
    {
        _grid.EndEdit();
        errors = [];
        config = new ServerConfig { Enabled = _enabled.Checked, LogPayload = _logPayload.Checked };
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var label = $"Server row {row.Index + 1}";
            var tcpText = CellText(row, _tcpColumn);
            if (!int.TryParse(tcpText, NumberStyles.None, CultureInfo.InvariantCulture, out var tcp))
                errors.Add($"{label}: TCP Port '{tcpText}' must be a whole number between 1 and 65535.");
            var baudText = CellText(row, _baudColumn);
            if (!int.TryParse(baudText, NumberStyles.None, CultureInfo.InvariantCulture, out var baud))
                errors.Add($"{label}: Baud '{baudText}' must be a whole number, e.g. 115200.");
            if (!SerialSettings.TryParseFormat(CellText(row, _formatColumn), out var dataBits, out var parity, out var stopBits))
            {
                errors.Add($"{label}: choose a format such as 8N1.");
                (dataBits, parity, stopBits) = (8, SerialParity.None, SerialStopBits.One);
            }
            var flowText = CellText(row, _flowColumn);
            var flow = FlowChoices.FirstOrDefault(c => c.Text == flowText).Value;

            config.Ports.Add(new ServerPort
            {
                Id = row.Tag is Guid id ? id : Guid.NewGuid(),
                Enabled = row.Cells[_onColumn.Index].Value is true,
                ComPort = CellText(row, _comColumn),
                Label = CellText(row, _labelColumn),
                TcpPort = tcp,
                ListenAddress = CellText(row, _listenColumn),
                AllowedClients = CellText(row, _clientsColumn).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).ToList(),
                Serial = new SerialSettings { BaudRate = baud, DataBits = dataBits, Parity = parity, StopBits = stopBits, FlowControl = flow },
            });
        }
        ServerConfigStore.Normalise(config);
        return errors.Count == 0;
    }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        _grid.Enabled = !busy;
        _enabled.Enabled = !busy;
        _logPayload.Enabled = !busy;
        UpdateButtons();
    }

    /// <summary>Reads the service's state and status file and shows them.</summary>
    public void RefreshStatus()
    {
        if (IsDisposed)
            return;
        try
        {
            _snapshot = ServerSnapshot.Read(_locations);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _snapshot = null;
        }
        UpdateStatuses();
    }

    private void UpdateStatuses()
    {
        var snapshot = _snapshot;
        _serviceStatus.Text = snapshot is null ? "Service: unknown" : $"Service ({_locations.ServiceName}): {snapshot.ServiceText}";
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var com = CellText(row, _comColumn);
            var cell = row.Cells[_statusColumn.Index];
            var live = snapshot?.StatusOf(com);
            cell.Value = com.Length == 0 ? "Choose a COM port"
                : !_enabled.Checked ? "Server mode is off"
                : row.Cells[_onColumn.Index].Value is not true ? "Off"
                : live is not null ? live.Describe()
                : snapshot is { IsRunning: true } ? "Not shared yet: save to apply"
                : $"Not shared: the service is {snapshot?.ServiceText ?? "unknown"}";
            cell.ToolTipText = live?.LastEvent ?? "";
            row.Cells[_comColumn.Index].ToolTipText = _inventory?.DevicePorts.FirstOrDefault(p => p.PortName == com)?.Device
                ?? (com.Length > 0 ? $"{com} isn't present at the moment" : "");
        }
    }

    private void AddRow()
    {
        _grid.EndEdit();
        var used = RowValues(_comColumn).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var com = _inventory?.DevicePorts.Select(p => p.PortName).FirstOrDefault(n => !used.Contains(n)) ?? "";
        var listen = DefaultListenAddress();
        SetComChoices([]);
        SetListenChoices([listen]);
        DataGridViewRow row;
        _loading = true;
        try
        {
            row = AddGridRow(Guid.NewGuid(), true, NullIfEmpty(com)!, "", "", listen, DefaultClients(listen), "115200", "8N1", "None", "");
            SuggestTcpPort(row);
        }
        finally
        {
            _loading = false;
        }
        _markDirty();
        UpdateStatuses();
        UpdateButtons();
        _grid.CurrentCell = row.Cells[_comColumn.Index];
    }

    private void RemoveRow()
    {
        if (_grid.CurrentRow is not { } row)
            return;
        _grid.Rows.Remove(row);
        _markDirty();
        UpdateButtons();
    }

    /// <summary>Base plus COM number, skipping TCP ports other rows or other programs use. Marked as automatic until edited.</summary>
    private void SuggestTcpPort(DataGridViewRow row)
    {
        var com = CellText(row, _comColumn);
        if (com.Length == 0)
            return;
        var taken = new HashSet<int>(_takenTcp);
        foreach (DataGridViewRow other in _grid.Rows)
        {
            if (other != row && int.TryParse(CellText(other, _tcpColumn), out var port))
                taken.Add(port);
        }
        var suggestion = TcpPortSuggester.Suggest(com, _edition.ServerPortBase, taken);
        var wasLoading = _loading;
        _loading = true;
        try
        {
            row.Cells[_tcpColumn.Index].Value = suggestion.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _loading = wasLoading;
        }
        row.Cells[_tcpColumn.Index].Tag = true;
    }

    private void SetComChoices(IEnumerable<string> extra)
    {
        var names = new HashSet<string>(RowValues(_comColumn), StringComparer.OrdinalIgnoreCase);
        names.UnionWith(extra.Where(s => !string.IsNullOrEmpty(s)));
        if (_inventory is not null)
            names.UnionWith(_inventory.DevicePorts.Select(p => p.PortName));
        var ordered = names.OrderBy(n => ConfigValidator.TryParseComNumber(n, out var number) ? number : int.MaxValue).ToArray();
        if (_comColumn.Items.Cast<string>().SequenceEqual(ordered, StringComparer.OrdinalIgnoreCase))
            return;
        if (_grid.IsCurrentCellInEditMode && _grid.CurrentCell is DataGridViewComboBoxCell)
            _grid.EndEdit();
        _comColumn.Items.Clear();
        _comColumn.Items.AddRange(ordered);
    }

    private void SetListenChoices(IEnumerable<string> extra)
    {
        var choices = new List<Choice> { new(AllInterfaces, "All interfaces (0.0.0.0)") };
        foreach (var (address, name) in LocalAddresses())
            choices.Add(new Choice(address, $"{address} ({name})"));
        foreach (var value in extra.Concat(RowValues(_listenColumn)).Where(v => v.Length > 0))
        {
            if (!choices.Any(c => c.Value == value))
                choices.Add(new Choice(value, $"{value} (not on this PC now)"));
        }
        if (_listenColumn.DataSource is List<Choice> current && current.Select(c => c.Value).SequenceEqual(choices.Select(c => c.Value)))
            return;
        _listenColumn.DataSource = choices;
    }

    private static IEnumerable<(string Address, string Interface)> LocalAddresses()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }
        foreach (var nic in interfaces.Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = unicast.Address;
                if (ip.AddressFamily == AddressFamily.InterNetwork || (ip.AddressFamily == AddressFamily.InterNetworkV6 && !ip.IsIPv6LinkLocal))
                    yield return (ip.ToString(), nic.Name);
            }
        }
    }

    /// <summary>The first IPv4 address of an interface with a default gateway, else of any interface, else all interfaces.</summary>
    private static string DefaultListenAddress()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
            return candidates.FirstOrDefault()?.Address.ToString() ?? AllInterfaces;
        }
        catch (NetworkInformationException)
        {
            return AllInterfaces;
        }
    }

    /// <summary>A new row allows the listen address's own subnet, a safe starting point to widen from.</summary>
    private static string DefaultClients(string listen)
    {
        try
        {
            var unicast = NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .FirstOrDefault(u => u.Address.ToString() == listen);
            if (unicast is not null && ClientFilter.TryParse($"{listen}/{unicast.PrefixLength}", out var network, out var prefix))
                return $"{network}/{prefix}";
        }
        catch (NetworkInformationException)
        {
        }
        return "";
    }

    private void UpdateButtons()
    {
        _addButton.Enabled = !_busy;
        _removeButton.Enabled = !_busy && _grid.CurrentRow is not null;
    }

    /// <summary>Adds a row that already carries its ID, so handlers run while it's inserted can rely on it.</summary>
    private DataGridViewRow AddGridRow(Guid id, params object[] values)
    {
        var row = new DataGridViewRow { Tag = id };
        row.CreateCells(_grid, values);
        return _grid.Rows[_grid.Rows.Add(row)];
    }

    private IEnumerable<string> RowValues(DataGridViewColumn column) =>
        _grid.Rows.Cast<DataGridViewRow>().Select(r => CellText(r, column)).Where(s => s.Length > 0);

    private static string FlowText(SerialFlowControl flow) => FlowChoices.First(c => c.Value == flow).Text;

    private static string CellText(DataGridViewRow row, DataGridViewColumn column) =>
        (row.Cells[column.Index].Value as string ?? row.Cells[column.Index].Value?.ToString() ?? "").Trim();

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start("explorer.exe", $"\"{path}\"");
            else
                Dialogs.Info("No server log yet", $"The service hasn't written a log yet. It will appear in {path}.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusTimer.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    internal sealed record Choice(string Value, string Display);
}
