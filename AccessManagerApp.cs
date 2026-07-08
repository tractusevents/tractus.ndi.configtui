using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Tractus.Ndi.ConfigTui;

internal sealed class AccessManagerApp
{
    private const int MinWidth = 60;
    private const int MinHeight = 20;
    private static readonly TimeSpan ResizePollInterval = TimeSpan.FromMilliseconds(100);

    private const int WhiptailHeight = 18;
    private const int WhiptailMaxWidth = 120;
    private const int WhiptailFallbackWidth = 80;
    private const string MulticastWarningMessage =
        "Enabling this option can have dire consequences unless your network has been configured for multicast delivery, including IGMP Snooping and Querying.\n\n" +
        "Please see the NDI White Paper for more information regarding multicast requirements. This document can be found at https://docs.ndi.video/all/getting-started/white-paper/multicast.\n\n" +
        "Unless your network has been specifically set up for multicast, we recommend you use unicast.";

    private readonly NdiConfigDocument _document;
    private readonly bool _createBackup;
    private readonly bool _advanced;
    private readonly List<HitTarget> _hitTargets = [];

    private Screen _screen = Screen.Main;
    private int _selectedIndex;
    private int _focusedButtonIndex;
    private bool _running = true;
    private bool _dirty;
    private string _status;
    private MenuLayout? _lastMenuLayout;
    private int _lastTerminalWidth = -1;
    private int _lastTerminalHeight = -1;

    public AccessManagerApp(NdiConfigDocument document, bool createBackup, bool advanced)
    {
        _document = document;
        _createBackup = createBackup;
        _advanced = advanced;
        _status = document.CreatedNew
            ? $"New file: {document.ConfigPath}"
            : $"Editing: {document.ConfigPath}";
    }

    public void Run()
    {
        var previousTreatControlCAsInput = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;

        using var terminal = new TerminalSession(AppBrand.Title);
        try
        {
            Render();
            while (_running)
            {
                var input = TerminalInputReader.TryRead(ResizePollInterval);
                if (input is null)
                {
                    if (TerminalSizeChanged())
                    {
                        Render();
                    }

                    continue;
                }

                var shouldRender = HandleInput(input);
                if (_running && (shouldRender || TerminalSizeChanged()))
                {
                    Render();
                }
            }
        }
        finally
        {
            Console.ResetColor();
            Console.TreatControlCAsInput = previousTreatControlCAsInput;
        }
    }

    private bool HandleInput(TerminalInput input)
    {
        if (input.Mouse is { } mouse)
        {
            return HandleMouse(mouse);
        }

        return input.Key is { } key && HandleKey(key);
    }

    private bool HandleMouse(TerminalMouseEvent mouse)
    {
        if (!mouse.Pressed || mouse.Button != MouseButton.Left)
        {
            return false;
        }

        for (var i = _hitTargets.Count - 1; i >= 0; i--)
        {
            var hit = _hitTargets[i];
            if (!hit.Contains(mouse.X, mouse.Y))
            {
                continue;
            }

            switch (hit.Action)
            {
                case HitAction.MenuItem:
                    _selectedIndex = hit.Index;
                    _focusedButtonIndex = 0;
                    return ActivateSelected();
                case HitAction.Select:
                    SetFocusedButton(hit.Action);
                    return ActivateSelected();
                case HitAction.Back:
                    SetFocusedButton(hit.Action);
                    if (_screen == Screen.Main)
                    {
                        Cancel();
                    }
                    else
                    {
                        GoBack();
                    }

                    return true;
                case HitAction.Apply:
                    SetFocusedButton(hit.Action);
                    Save();
                    return true;
                case HitAction.Cancel:
                    SetFocusedButton(hit.Action);
                    Cancel();
                    return true;
            }

        }

        return false;
    }

    private bool HandleKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            Cancel();
            return true;
        }

        if ((key.Key == ConsoleKey.S && key.Modifiers.HasFlag(ConsoleModifiers.Control)) || key.Key == ConsoleKey.F10)
        {
            Save();
            return true;
        }

        if (key.Key == ConsoleKey.Escape)
        {
            if (_screen == Screen.Main)
            {
                Cancel();
            }
            else
            {
                GoBack();
            }

            return true;
        }

        if (key.Key == ConsoleKey.F1 || key.KeyChar == '?')
        {
            ShowHelp();
            return true;
        }

        if (key.Key == ConsoleKey.Backspace && _screen != Screen.Main)
        {
            GoBack();
            return true;
        }

        var items = BuildItems();
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                MoveSelectionWithPartialRedraw(-1, items.Count);
                return false;
            case ConsoleKey.DownArrow:
                MoveSelectionWithPartialRedraw(1, items.Count);
                return false;
            case ConsoleKey.Home:
                SetSelectionWithPartialRedraw(0, items.Count);
                return false;
            case ConsoleKey.End:
                SetSelectionWithPartialRedraw(Math.Max(0, items.Count - 1), items.Count);
                return false;
            case ConsoleKey.LeftArrow:
                MoveButtonFocus(-1);
                return false;
            case ConsoleKey.RightArrow:
            case ConsoleKey.Tab:
                MoveButtonFocus(1);
                return false;
            case ConsoleKey.Enter:
            case ConsoleKey.Spacebar:
                return ActivateFocusedButton();
            case ConsoleKey.Delete:
                DeleteSelected();
                return true;
        }

        switch (char.ToLowerInvariant(key.KeyChar))
        {
            case 'a':
                AddOnCurrentScreen();
                return true;
            case 'd':
                DeleteSelected();
                return true;
            case 'b':
                if (_screen == Screen.Main)
                {
                    Cancel();
                }
                else
                {
                    GoBack();
                }

                return true;
            case 's':
                Save();
                return true;
            case 'q':
                Cancel();
                return true;
            case >= '1' and <= '9':
                return ActivateNumericShortcut(key.KeyChar - '0');
            default:
                return false;
        }
    }

    private void MoveSelectionWithPartialRedraw(int delta, int count)
    {
        if (count == 0)
        {
            SetSelectionWithPartialRedraw(0, count);
            return;
        }

        SetSelectionWithPartialRedraw((_selectedIndex + delta + count) % count, count);
    }

    private void SetSelectionWithPartialRedraw(int index, int count)
    {
        var previousIndex = _selectedIndex;
        if (!SetSelection(index, count))
        {
            return;
        }

        var buttonFocusChanged = _focusedButtonIndex != 0;
        _focusedButtonIndex = 0;
        if (!RedrawSelectionOnly(previousIndex))
        {
            RedrawMenuOnly();
        }
        else if (buttonFocusChanged)
        {
            RedrawButtonsOnly();
        }
    }

    private bool SetSelection(int index, int count)
    {
        var next = count == 0 ? 0 : Math.Clamp(index, 0, count - 1);
        if (_selectedIndex == next)
        {
            return false;
        }

        _selectedIndex = next;
        return true;
    }

    private void MoveButtonFocus(int delta)
    {
        var buttons = BuildButtons();
        if (buttons.Length == 0)
        {
            _focusedButtonIndex = 0;
            return;
        }

        ClampButtonFocus(buttons.Length);
        _focusedButtonIndex = (_focusedButtonIndex + delta + buttons.Length) % buttons.Length;
        RedrawButtonsOnly();
    }

    private bool ActivateFocusedButton()
    {
        var buttons = BuildButtons();
        if (buttons.Length == 0)
        {
            return false;
        }

        ClampButtonFocus(buttons.Length);
        return ActivateButton(buttons[_focusedButtonIndex].Action);
    }

    private bool ActivateButton(HitAction action)
    {
        switch (action)
        {
            case HitAction.Select:
                return ActivateSelected();
            case HitAction.Back:
                if (_screen == Screen.Main)
                {
                    Cancel();
                }
                else
                {
                    GoBack();
                }

                return true;
            case HitAction.Apply:
                Save();
                return true;
            case HitAction.Cancel:
                Cancel();
                return true;
            default:
                return false;
        }
    }

    private void SetFocusedButton(HitAction action)
    {
        var buttons = BuildButtons();
        for (var i = 0; i < buttons.Length; i++)
        {
            if (buttons[i].Action == action)
            {
                _focusedButtonIndex = i;
                return;
            }
        }
    }

    private void ClampButtonFocus(int? count = null)
    {
        count ??= BuildButtons().Length;
        _focusedButtonIndex = count.Value == 0 ? 0 : Math.Clamp(_focusedButtonIndex, 0, count.Value - 1);
    }

    private bool ActivateNumericShortcut(int number)
    {
        var items = BuildItems();
        var index = number - 1;
        if (index >= 0 && index < items.Count)
        {
            _selectedIndex = index;
            _focusedButtonIndex = 0;
            return ActivateSelected();
        }

        return false;
    }

    private bool ActivateSelected()
    {
        var item = GetSelectedItem();
        if (item is null)
        {
            return false;
        }

        if (_screen == Screen.Main)
        {
            EnterScreen(item.TargetScreen);
            return true;
        }

        switch (item.Kind)
        {
            case ItemKind.EditReceiveGroups:
                EditGroups(NdiDirection.Receive);
                break;
            case ItemKind.EditSendGroups:
                EditGroups(NdiDirection.Send);
                break;
            case ItemKind.ExternalSource:
                EditExternalSource(item.Index);
                break;
            case ItemKind.EmptyExternalSources:
                AddExternalSource();
                break;
            case ItemKind.ToggleAllowUdp:
                Toggle(() => _document.AllowUdpReceive, value => _document.AllowUdpReceive = value, "UDP receive");
                return !RedrawCurrentItemAndStatus();
            case ItemKind.ToggleAllowMultiTcp:
                Toggle(() => _document.AllowMultiTcpReceive, value => _document.AllowMultiTcpReceive = value, "Multi-TCP receive");
                return !RedrawCurrentItemAndStatus();
            case ItemKind.ToggleAllowReliableUdp:
                Toggle(() => _document.AllowReliableUdpReceive, value => _document.AllowReliableUdpReceive = value, "Reliable UDP receive");
                return !RedrawCurrentItemAndStatus();
            case ItemKind.ToggleMulticast:
                return ToggleMulticast();
            case ItemKind.MulticastPrefix:
                PromptAndSet("Multicast Send IP", _document.MulticastSendPrefix, text => ValidateIpAddress(text, allowEmpty: false), value => _document.MulticastSendPrefix = value);
                break;
            case ItemKind.MulticastMask:
                PromptAndSet("Multicast Mask", _document.MulticastSendMask, text => ValidateIpAddress(text, allowEmpty: false), value => _document.MulticastSendMask = value);
                break;
            case ItemKind.MulticastTtl:
                PromptAndSet("Multicast TTL", _document.MulticastTtl.ToString(), ValidateTtl, value => _document.MulticastTtl = int.Parse(value));
                break;
            case ItemKind.MulticastSubnets:
                PromptAndSet("Multicast Receive Subnets", _document.MulticastReceiveSubnets, ValidateSubnetList, value => _document.MulticastReceiveSubnets = value);
                break;
            case ItemKind.ToggleDiscovery:
                return ToggleDiscoveryServers();
            case ItemKind.DiscoveryServers:
                PromptAndSetList("Discovery Server IPs", _document.GetDiscoveryServers(), ValidateDiscoveryServerList, _document.SetDiscoveryServers);
                break;
            case ItemKind.TogglePreferredNic:
                return TogglePreferredNic();
            case ItemKind.PreferredNicAddresses:
                PromptPreferredNicAddresses();
                break;
            case ItemKind.ToggleMachineName:
                return ToggleMachineName();
            case ItemKind.MachineName:
                PromptAndSet("NDI Device Alias", _document.MachineName ?? string.Empty, ValidateOptionalName, value => _document.MachineName = value);
                break;
            case ItemKind.ToggleSourceFilter:
                return ToggleSourceFilter();
            case ItemKind.SourceFilterRegex:
                PromptAndSet("Source Filter Regex", _document.SourceFilterRegex ?? string.Empty, ValidateRegex, value => _document.SourceFilterRegex = value);
                break;
            case ItemKind.ConfigPath:
                _status = _document.ConfigPath;
                break;
            case ItemKind.ReliableUdpStatus:
                ShowMessage("Reliable UDP System", ReliableUdpSystemCheck.GetStatus().Detail);
                break;
            case ItemKind.CodecShqQuality:
                PromptOptionalInt("Codec SHQ Quality", _document.CodecShqQuality?.ToString() ?? string.Empty, ValidateCodecQuality, value => _document.CodecShqQuality = value);
                break;
            case ItemKind.CodecShqMode:
                PromptAndSet("Codec SHQ Mode", _document.CodecShqMode ?? string.Empty, ValidateCodecMode, value => _document.CodecShqMode = value);
                break;
            case ItemKind.VendorName:
                PromptAndSet("Vendor Name", _document.VendorName ?? string.Empty, ValidateOptionalText, value => _document.VendorName = value);
                break;
            case ItemKind.VendorId:
                PromptAndSet("Vendor ID", _document.VendorId ?? string.Empty, ValidateOptionalText, value => _document.VendorId = value);
                break;
        }

        ClampSelection();
        return true;
    }

    private void DeleteSelected()
    {
        var item = GetSelectedItem();
        if (item?.Kind == ItemKind.ExternalSource)
        {
            DeleteExternalSource(item.Index);
        }
    }

    private void AddOnCurrentScreen()
    {
        if (_screen == Screen.ExternalSources)
        {
            AddExternalSource();
        }
    }

    private MenuItem? GetSelectedItem()
    {
        var items = BuildItems();
        if (items.Count == 0)
        {
            return null;
        }

        ClampSelection(items.Count);
        return items[_selectedIndex];
    }

    private void EnterScreen(Screen screen)
    {
        _screen = screen;
        _selectedIndex = 0;
        _focusedButtonIndex = 0;
        _status = _dirty ? "Modified" : $"Editing: {_document.ConfigPath}";
    }

    private void GoBack()
    {
        _screen = Screen.Main;
        _selectedIndex = 0;
        _focusedButtonIndex = 0;
        _status = _dirty ? "Modified" : $"Editing: {_document.ConfigPath}";
    }

    private void ClampSelection(int? count = null)
    {
        count ??= BuildItems().Count;
        _selectedIndex = count.Value == 0 ? 0 : Math.Clamp(_selectedIndex, 0, count.Value - 1);
    }

    private List<MenuItem> BuildItems() =>
        _screen switch
        {
            Screen.Main => BuildMainMenu(),
            Screen.Groups => BuildGroupMenu(),
            Screen.ExternalSources => BuildExternalSourceMenu(),
            Screen.Receive => BuildReceiveMenu(),
            Screen.Multicast => BuildMulticastMenu(),
            Screen.NetworkMapping => BuildNetworkMappingMenu(),
            Screen.ConfigFile => BuildConfigFileMenu(),
            Screen.Advanced => BuildAdvancedMenu(),
            _ => []
        };

    private ButtonSpec[] BuildButtons() =>
        _screen == Screen.Main
            ? [ButtonSpec.Select, ButtonSpec.Apply, ButtonSpec.Finish]
            : [ButtonSpec.Select, ButtonSpec.Back, ButtonSpec.Apply];

    private List<MenuItem> BuildMainMenu()
    {
        var items = new List<MenuItem>
        {
            MenuItem.Screen("Groups", "Send and receive group membership", Screen.Groups),
            MenuItem.Screen("External Sources", "Manual source IP addresses across subnets", Screen.ExternalSources),
            MenuItem.Screen("Receive", "Receive protocol enablement", Screen.Receive),
            MenuItem.Screen("Multicast", "Multicast send and receive settings", Screen.Multicast),
            MenuItem.Screen("Network Mapping", "Discovery servers, NICs, alias, source filter", Screen.NetworkMapping),
            MenuItem.Screen("Config File", "Current target and backup behavior", Screen.ConfigFile)
        };

        if (_advanced)
        {
            items.Add(MenuItem.Screen("Advanced", "Advanced SDK codec and vendor settings", Screen.Advanced));
        }

        return items;
    }

    private List<MenuItem> BuildGroupMenu() =>
    [
        new("Receive Groups", string.Join(", ", _document.GetGroups(NdiDirection.Receive)), ItemKind.EditReceiveGroups),
        new("Send Groups", string.Join(", ", _document.GetGroups(NdiDirection.Send)), ItemKind.EditSendGroups)
    ];

    private List<MenuItem> BuildExternalSourceMenu()
    {
        var ips = _document.GetExternalSourceIps();
        var items = new List<MenuItem>();
        for (var i = 0; i < ips.Count; i++)
        {
            items.Add(new(ips[i], "Local NDI name: NDI", ItemKind.ExternalSource, Index: i));
        }

        items.Add(new(
            "Add External Source",
            ips.Count == 0 ? "No manual sources configured" : "Add another manual source",
            ItemKind.EmptyExternalSources));

        return items;
    }

    private List<MenuItem> BuildReceiveMenu()
    {
        var reliableUdpStatus = ReliableUdpSystemCheck.GetStatus();
        var reliableUdpValue = Checkbox(_document.AllowReliableUdpReceive);
        if (_document.AllowReliableUdpReceive && !reliableUdpStatus.IsOptimized)
        {
            reliableUdpValue += "  !";
        }

        return
        [
            new("Multi-TCP", Checkbox(_document.AllowMultiTcpReceive), ItemKind.ToggleAllowMultiTcp),
            new("UDP", Checkbox(_document.AllowUdpReceive), ItemKind.ToggleAllowUdp),
            new("Reliable UDP", reliableUdpValue, ItemKind.ToggleAllowReliableUdp),
            new("Reliable UDP System", reliableUdpStatus.Summary, ItemKind.ReliableUdpStatus)
        ];
    }

    private List<MenuItem> BuildMulticastMenu() =>
    [
        new("Enable Multicast", Checkbox(IsMulticastEnabled()), ItemKind.ToggleMulticast),
        new("Send IP", _document.MulticastSendPrefix, ItemKind.MulticastPrefix),
        new("Send Mask", _document.MulticastSendMask, ItemKind.MulticastMask),
        new("TTL", _document.MulticastTtl.ToString(), ItemKind.MulticastTtl),
        new("Receive Subnets", EmptyText(_document.MulticastReceiveSubnets), ItemKind.MulticastSubnets)
    ];

    private List<MenuItem> BuildNetworkMappingMenu() =>
    [
        new("Discovery Servers", Checkbox(_document.GetDiscoveryServers().Count > 0), ItemKind.ToggleDiscovery),
        new("Discovery IPs", EmptyText(string.Join(", ", _document.GetDiscoveryServers())), ItemKind.DiscoveryServers),
        new("Preferred NIC", Checkbox(_document.GetAllowedAdapters().Count > 0), ItemKind.TogglePreferredNic),
        new("Preferred NIC IPs", EmptyText(string.Join(", ", _document.GetAllowedAdapters())), ItemKind.PreferredNicAddresses),
        new("NDI Device Alias", Checkbox(!string.IsNullOrWhiteSpace(_document.MachineName)), ItemKind.ToggleMachineName),
        new("Machine Name", EmptyText(_document.MachineName), ItemKind.MachineName),
        new("Source Filter", Checkbox(!string.IsNullOrWhiteSpace(_document.SourceFilterRegex)), ItemKind.ToggleSourceFilter),
        new("Regex", EmptyText(_document.SourceFilterRegex), ItemKind.SourceFilterRegex)
    ];

    private List<MenuItem> BuildConfigFileMenu() =>
    [
        new("Target", _document.ConfigPath, ItemKind.ConfigPath),
        new("Backup on Save", _createBackup ? "yes" : "no", ItemKind.ConfigPath),
        new("State", _dirty ? "modified" : "clean", ItemKind.ConfigPath)
    ];

    private List<MenuItem> BuildAdvancedMenu() =>
    [
        new("Codec SHQ Quality", _document.CodecShqQuality?.ToString() ?? "<default>", ItemKind.CodecShqQuality),
        new("Codec SHQ Mode", EmptyText(_document.CodecShqMode), ItemKind.CodecShqMode),
        new("Vendor Name", EmptyText(_document.VendorName), ItemKind.VendorName),
        new("Vendor ID", EmptyText(_document.VendorId), ItemKind.VendorId)
    ];

    private void EditGroups(NdiDirection direction)
    {
        var title = direction == NdiDirection.Receive ? "Receive Groups" : "Send Groups";
        PromptAndSetList(title, _document.GetGroups(direction), ValidateGroupList, values =>
        {
            _document.SetGroups(direction, values.Count == 0 ? ["Public"] : values);
        });
    }

    private void AddExternalSource()
    {
        var value = PromptText("Add External Source", string.Empty, text => ValidateIpAddress(text, allowEmpty: false));
        if (value is null)
        {
            return;
        }

        var ips = _document.GetExternalSourceIps().ToList();
        ips.Add(value.Trim());
        _document.SetExternalSourceIps(ips);
        _selectedIndex = ips.Count - 1;
        MarkDirty("External source added");
    }

    private void EditExternalSource(int index)
    {
        var ips = _document.GetExternalSourceIps().ToList();
        if (index < 0 || index >= ips.Count)
        {
            return;
        }

        var value = PromptText("Edit External Source", ips[index], text => ValidateIpAddress(text, allowEmpty: false));
        if (value is null)
        {
            return;
        }

        ips[index] = value.Trim();
        _document.SetExternalSourceIps(ips);
        MarkDirty("External source updated");
    }

    private void DeleteExternalSource(int index)
    {
        var ips = _document.GetExternalSourceIps().ToList();
        if (index < 0 || index >= ips.Count)
        {
            return;
        }

        var removed = ips[index];
        ips.RemoveAt(index);
        _document.SetExternalSourceIps(ips);
        MarkDirty($"Removed {removed}");
    }

    private void Toggle(Func<bool> getter, Action<bool> setter, string label)
    {
        setter(!getter());
        MarkDirty($"{label} toggled");
    }

    private bool ToggleMulticast()
    {
        if (IsMulticastEnabled())
        {
            SetMulticastEnabled(false);
            MarkDirty("Multicast disabled");
            return !RedrawCurrentItemAndStatus();
        }

        if (!ConfirmDetailed("Warning", MulticastWarningMessage, "<Enable>", "<Cancel>"))
        {
            _status = _dirty ? "Modified" : $"Editing: {_document.ConfigPath}";
            return true;
        }

        SetMulticastEnabled(true);
        MarkDirty("Multicast enabled");
        return true;
    }

    private bool IsMulticastEnabled() =>
        _document.MulticastSendEnabled || _document.MulticastReceiveEnabled;

    private void SetMulticastEnabled(bool enabled)
    {
        _document.MulticastSendEnabled = enabled;
        _document.MulticastReceiveEnabled = enabled;
    }

    private bool ToggleDiscoveryServers()
    {
        if (_document.GetDiscoveryServers().Count > 0)
        {
            _document.SetDiscoveryServers([]);
            MarkDirty("Discovery servers disabled");
            RedrawMenuOnly();
            return false;
        }

        PromptAndSetList("Discovery Server IPs", [], ValidateDiscoveryServerList, _document.SetDiscoveryServers);
        return true;
    }

    private bool TogglePreferredNic()
    {
        if (_document.GetAllowedAdapters().Count > 0)
        {
            _document.SetAllowedAdapters([]);
            MarkDirty("Preferred NIC disabled");
            RedrawMenuOnly();
            return false;
        }

        PromptPreferredNicAddresses();
        return true;
    }

    private bool ToggleMachineName()
    {
        if (!string.IsNullOrWhiteSpace(_document.MachineName))
        {
            _document.MachineName = null;
            MarkDirty("NDI device alias disabled");
            RedrawMenuOnly();
            return false;
        }

        PromptAndSet("NDI Device Alias", string.Empty, ValidateOptionalName, value => _document.MachineName = value);
        return true;
    }

    private bool ToggleSourceFilter()
    {
        if (!string.IsNullOrWhiteSpace(_document.SourceFilterRegex))
        {
            _document.SourceFilterRegex = null;
            MarkDirty("Source filter disabled");
            RedrawMenuOnly();
            return false;
        }

        PromptAndSet("Source Filter Regex", string.Empty, ValidateRegex, value => _document.SourceFilterRegex = value);
        return true;
    }

    private void PromptAndSet(string title, string current, Func<string, string?> validate, Action<string> setter)
    {
        var value = PromptText(title, current, validate);
        if (value is null)
        {
            return;
        }

        setter(value.Trim());
        MarkDirty($"{title} updated");
    }

    private void PromptAndSetList(string title, IReadOnlyList<string> current, Func<string, string?> validate, Action<IReadOnlyList<string>> setter)
    {
        var value = PromptText(title, string.Join(",", current), validate);
        if (value is null)
        {
            return;
        }

        setter(SplitList(value));
        MarkDirty($"{title} updated");
    }

    private void PromptOptionalInt(string title, string current, Func<string, string?> validate, Action<int?> setter)
    {
        var value = PromptText(title, current, validate);
        if (value is null)
        {
            return;
        }

        value = value.Trim();
        setter(value.Length == 0 ? null : int.Parse(value));
        MarkDirty($"{title} updated");
    }

    private void PromptPreferredNicAddresses()
    {
        var options = GetLocalAdapterAddressOptions();
        var configured = _document.GetAllowedAdapters();
        foreach (var adapter in configured)
        {
            if (options.All(option => !string.Equals(option.Address, adapter, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new NicAddressOption(adapter, "configured"));
            }
        }

        if (options.Count == 0)
        {
            PromptAndSetList("Preferred NIC IPs", configured, ValidateIpList, _document.SetAllowedAdapters);
            return;
        }

        var selected = configured.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rowIndex = 0;
        var start = 0;
        var buttonsFocused = false;
        var buttonIndex = 2;
        var terminalWidth = 0;
        var terminalHeight = 0;
        var dialogWidth = 0;
        var dialogHeight = 0;
        var x = 0;
        var y = 0;
        var listX = 0;
        var listY = 0;
        var listWidth = 0;
        var visibleRows = 0;
        List<HitTarget> rowTargets = [];
        List<HitTarget> buttonTargets = [];

        DrawPickerDialog();

        while (true)
        {
            if (Console.WindowWidth != terminalWidth || Console.WindowHeight != terminalHeight)
            {
                DrawPickerDialog();
            }

            var input = TerminalInputReader.TryRead(ResizePollInterval);
            if (input is null)
            {
                continue;
            }

            if (input.Mouse is { Pressed: true, Button: MouseButton.Left } mouse)
            {
                var rowHit = rowTargets.FirstOrDefault(hit => hit.Contains(mouse.X, mouse.Y));
                if (rowHit is not null)
                {
                    rowIndex = rowHit.Index;
                    buttonsFocused = false;
                    ToggleRow();
                    continue;
                }

                var buttonHit = buttonTargets.FirstOrDefault(hit => hit.Contains(mouse.X, mouse.Y));
                if (buttonHit is not null)
                {
                    buttonsFocused = true;
                    buttonIndex = Math.Max(0, buttonTargets.IndexOf(buttonHit));
                    if (ActivatePickerButton())
                    {
                        return;
                    }
                }

                continue;
            }

            if (input.Key is not { } key)
            {
                continue;
            }

            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    return;
                case ConsoleKey.UpArrow:
                    if (buttonsFocused)
                    {
                        buttonsFocused = false;
                        DrawPickerButtons();
                    }
                    else
                    {
                        MoveRow(-1);
                    }

                    break;
                case ConsoleKey.DownArrow:
                    if (rowIndex == options.Count - 1)
                    {
                        buttonsFocused = true;
                        DrawPickerRows();
                        DrawPickerButtons();
                    }
                    else
                    {
                        MoveRow(1);
                    }

                    break;
                case ConsoleKey.LeftArrow:
                    buttonsFocused = true;
                    MovePickerButton(-1);
                    break;
                case ConsoleKey.RightArrow:
                case ConsoleKey.Tab:
                    buttonsFocused = true;
                    MovePickerButton(1);
                    break;
                case ConsoleKey.Enter:
                case ConsoleKey.Spacebar:
                    if (buttonsFocused)
                    {
                        if (ActivatePickerButton())
                        {
                            return;
                        }
                    }
                    else
                    {
                        ToggleRow();
                    }

                    break;
            }
        }

        void DrawPickerDialog()
        {
            Render();
            terminalWidth = Console.WindowWidth;
            terminalHeight = Console.WindowHeight;
            dialogWidth = Math.Min(Math.Max(72, "Preferred NIC IPs".Length + 18), terminalWidth);
            dialogHeight = Math.Min(16, terminalHeight - 4);
            x = (terminalWidth - dialogWidth) / 2;
            y = Math.Max(1, (terminalHeight - dialogHeight) / 2);
            listX = x + 3;
            listY = y + 4;
            listWidth = dialogWidth - 6;
            visibleRows = Math.Max(1, dialogHeight - 8);

            DrawDialog(x, y, dialogWidth, dialogHeight, AppBrand.Title);
            WriteAt(x + 3, y + 2, "Preferred NIC IPs", ConsoleColor.Black, ConsoleColor.Gray, dialogWidth - 6);
            WriteAt(x + 3, y + 3, "Select one or more local addresses.", ConsoleColor.Black, ConsoleColor.Gray, dialogWidth - 6);
            DrawPickerRows();
            DrawPickerButtons();
        }

        void DrawPickerRows()
        {
            rowTargets.Clear();
            start = CalculateVisibleStart(options.Count, visibleRows, rowIndex);
            for (var row = 0; row < visibleRows; row++)
            {
                var index = start + row;
                var rowY = listY + row;
                if (index >= options.Count)
                {
                    WriteAt(listX, rowY, string.Empty, ConsoleColor.Black, ConsoleColor.Gray, listWidth);
                    continue;
                }

                var option = options[index];
                var checkedText = selected.Contains(option.Address) ? "[*]" : "[ ]";
                var label = $" {checkedText} {option.Address,-15} {option.Memo}";
                var focused = !buttonsFocused && index == rowIndex;
                WriteAt(listX, rowY, label, focused ? ConsoleColor.White : ConsoleColor.Black, focused ? ConsoleColor.Red : ConsoleColor.Gray, listWidth);
                rowTargets.Add(new HitTarget(listX, rowY, listWidth, 1, HitAction.MenuItem, index));
            }
        }

        void DrawPickerButtons()
        {
            buttonTargets.Clear();
            var labels = new[] { "<Toggle>", "<Manual>", "<OK>", "<Cancel>" };
            var total = labels.Sum(label => label.Length + 4);
            var currentX = x + Math.Max(2, (dialogWidth - total) / 2);
            var buttonY = y + dialogHeight - 2;
            for (var i = 0; i < labels.Length; i++)
            {
                var action = i switch
                {
                    0 => HitAction.Select,
                    1 => HitAction.Apply,
                    2 => HitAction.Back,
                    _ => HitAction.Cancel
                };
                var target = new HitTarget(currentX, buttonY, labels[i].Length + 2, 1, action);
                buttonTargets.Add(target);
                DrawModalButton(target, labels[i], buttonsFocused && i == buttonIndex);
                currentX += labels[i].Length + 4;
            }
        }

        void MoveRow(int delta)
        {
            var previous = rowIndex;
            rowIndex = (rowIndex + delta + options.Count) % options.Count;
            if (CalculateVisibleStart(options.Count, visibleRows, previous) != CalculateVisibleStart(options.Count, visibleRows, rowIndex))
            {
                DrawPickerRows();
                return;
            }

            DrawPickerRows();
        }

        void MovePickerButton(int delta)
        {
            buttonIndex = (buttonIndex + delta + 4) % 4;
            DrawPickerRows();
            DrawPickerButtons();
        }

        void ToggleRow()
        {
            var address = options[rowIndex].Address;
            if (!selected.Add(address))
            {
                selected.Remove(address);
            }

            DrawPickerRows();
        }

        bool ActivatePickerButton()
        {
            switch (buttonIndex)
            {
                case 0:
                    ToggleRow();
                    return false;
                case 1:
                    var manual = PromptText("Preferred NIC IPs", string.Join(',', options.Where(option => selected.Contains(option.Address)).Select(option => option.Address)), ValidateIpList);
                    if (manual is null)
                    {
                        DrawPickerDialog();
                        return false;
                    }

                    _document.SetAllowedAdapters(SplitList(manual));
                    MarkDirty("Preferred NIC IPs updated");
                    return true;
                case 2:
                    _document.SetAllowedAdapters(options.Where(option => selected.Contains(option.Address)).Select(option => option.Address).ToList());
                    MarkDirty("Preferred NIC IPs updated");
                    return true;
                default:
                    return true;
            }
        }
    }

    private void MarkDirty(string status)
    {
        _dirty = true;
        _status = status;
    }

    private void Save()
    {
        try
        {
            _document.Save(_createBackup);
            _dirty = false;
            _status = $"Saved: {_document.ConfigPath}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status = $"Save failed: {ex.Message}";
            ShowMessage("Save failed", ex.Message);
        }
    }

    private void Cancel()
    {
        if (!_dirty || Confirm("Exit without saving?"))
        {
            _running = false;
        }
    }

    private void Render()
    {
        try
        {
            Console.CursorVisible = false;
            _hitTargets.Clear();
            var width = Console.WindowWidth;
            var height = Console.WindowHeight;
            _lastTerminalWidth = width;
            _lastTerminalHeight = height;

            if (width < MinWidth || height < MinHeight)
            {
                _lastMenuLayout = null;
                Console.ResetColor();
                WriteAt(0, 0, $"Terminal too small. Need at least {MinWidth}x{MinHeight}.", ConsoleColor.White, ConsoleColor.Black);
                return;
            }

            FillRect(0, 0, width, height, ConsoleColor.Blue);
            DrawBackTitle(width);

            var dialogWidth = CalculateWhiptailWidth(width);
            var dialogHeight = Math.Min(WhiptailHeight, height - 4);
            var x = (width - dialogWidth) / 2;
            var y = Math.Max(1, (height - dialogHeight) / 2);

            _lastMenuLayout = new MenuLayout(
                x + 3,
                y + 2,
                dialogWidth - 6,
                dialogHeight - 6,
                x,
                y + dialogHeight - 2,
                dialogWidth,
                width,
                height);

            DrawDialog(x, y, dialogWidth, dialogHeight, AppBrand.Title);
            DrawMenu(_lastMenuLayout.MenuX, _lastMenuLayout.MenuY, _lastMenuLayout.MenuWidth, _lastMenuLayout.MenuHeight);
            DrawButtons(_lastMenuLayout.DialogX, _lastMenuLayout.ButtonY, _lastMenuLayout.DialogWidth);
            DrawStatus(width, height);
            Console.SetCursorPosition(0, height - 1);
        }
        catch (ArgumentOutOfRangeException)
        {
            // The terminal was resized mid-frame. The next input redraws.
        }
        catch (IOException)
        {
            _running = false;
        }
    }

    private void RedrawMenuOnly()
    {
        try
        {
            if (_lastMenuLayout is not { } layout ||
                Console.WindowWidth != layout.TerminalWidth ||
                Console.WindowHeight != layout.TerminalHeight)
            {
                Render();
                return;
            }

            Console.CursorVisible = false;
            _hitTargets.Clear();
            DrawMenu(layout.MenuX, layout.MenuY, layout.MenuWidth, layout.MenuHeight);
            DrawButtons(layout.DialogX, layout.ButtonY, layout.DialogWidth);
            DrawStatus(layout.TerminalWidth, layout.TerminalHeight);
            Console.SetCursorPosition(0, layout.TerminalHeight - 1);
        }
        catch (ArgumentOutOfRangeException)
        {
            Render();
        }
        catch (IOException)
        {
            _running = false;
        }
    }


    private void RedrawButtonsOnly()
    {
        try
        {
            if (_lastMenuLayout is not { } layout ||
                Console.WindowWidth != layout.TerminalWidth ||
                Console.WindowHeight != layout.TerminalHeight)
            {
                Render();
                return;
            }

            Console.CursorVisible = false;
            DrawButtons(layout.DialogX, layout.ButtonY, layout.DialogWidth, addHitTargets: false);
            Console.SetCursorPosition(0, layout.TerminalHeight - 1);
        }
        catch (ArgumentOutOfRangeException)
        {
            Render();
        }
        catch (IOException)
        {
            _running = false;
        }
    }

    private bool RedrawSelectionOnly(int previousIndex)
    {
        try
        {
            if (_lastMenuLayout is not { } layout ||
                Console.WindowWidth != layout.TerminalWidth ||
                Console.WindowHeight != layout.TerminalHeight)
            {
                return false;
            }

            var items = BuildItems();
            var visibleRows = Math.Max(1, layout.MenuHeight - 2);
            var previousStart = CalculateVisibleStart(items.Count, visibleRows, previousIndex);
            var currentStart = CalculateVisibleStart(items.Count, visibleRows, _selectedIndex);
            if (previousStart != currentStart || previousIndex < 0 || previousIndex >= items.Count)
            {
                return false;
            }

            var rowY = layout.MenuY + 2;
            Console.CursorVisible = false;
            DrawMenuRow(layout.MenuX, rowY + previousIndex - currentStart, layout.MenuWidth, previousIndex, items[previousIndex], selected: false, addHitTarget: false);
            DrawMenuRow(layout.MenuX, rowY + _selectedIndex - currentStart, layout.MenuWidth, _selectedIndex, items[_selectedIndex], selected: true, addHitTarget: false);
            Console.SetCursorPosition(0, layout.TerminalHeight - 1);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (IOException)
        {
            _running = false;
            return true;
        }
    }

    private bool RedrawCurrentItemAndStatus()
    {
        try
        {
            if (_lastMenuLayout is not { } layout ||
                Console.WindowWidth != layout.TerminalWidth ||
                Console.WindowHeight != layout.TerminalHeight)
            {
                return false;
            }

            var items = BuildItems();
            if (items.Count == 0)
            {
                return false;
            }

            ClampSelection(items.Count);
            var visibleRows = Math.Max(1, layout.MenuHeight - 2);
            var start = CalculateVisibleStart(items.Count, visibleRows, _selectedIndex);
            if (_selectedIndex < start || _selectedIndex >= start + visibleRows)
            {
                return false;
            }

            var rowY = layout.MenuY + 2 + _selectedIndex - start;
            Console.CursorVisible = false;
            DrawMenuRow(layout.MenuX, rowY, layout.MenuWidth, _selectedIndex, items[_selectedIndex], selected: true, addHitTarget: false);
            DrawStatus(layout.TerminalWidth, layout.TerminalHeight);
            Console.SetCursorPosition(0, layout.TerminalHeight - 1);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (IOException)
        {
            _running = false;
            return true;
        }
    }

    private void DrawBackTitle(int width)
    {
        var left = $" {AppBrand.Title} ";
        WriteAt(0, 0, left, ConsoleColor.White, ConsoleColor.Blue, Math.Min(left.Length, width));

        var path = $" {_document.ConfigPath} ";
        var rightWidth = Math.Min(path.Length, Math.Max(0, width - left.Length - 1));
        if (rightWidth > 12)
        {
            WriteAt(width - rightWidth, 0, Fit(path, rightWidth), ConsoleColor.Gray, ConsoleColor.Blue, rightWidth);
        }
    }

    private void DrawMenu(int x, int y, int width, int height)
    {
        var items = BuildItems();
        ClampSelection(items.Count);

        WriteAt(x, y, DialogPrompt(), ConsoleColor.Black, ConsoleColor.Gray, width);
        y += 2;
        height -= 2;

        var visibleRows = Math.Max(1, height);
        var start = CalculateVisibleStart(items.Count, visibleRows, _selectedIndex);

        for (var row = 0; row < visibleRows; row++)
        {
            var index = start + row;
            var rowY = y + row;
            if (index >= items.Count)
            {
                WriteAt(x, rowY, string.Empty, ConsoleColor.Black, ConsoleColor.Gray, width);
                continue;
            }

            var selected = index == _selectedIndex;
            DrawMenuRow(x, rowY, width, index, items[index], selected);
        }

        if (start > 0)
        {
            WriteAt(x + width - 2, y, "↑", ConsoleColor.DarkBlue, ConsoleColor.Gray);
        }

        if (start + visibleRows < items.Count)
        {
            WriteAt(x + width - 2, y + visibleRows - 1, "↓", ConsoleColor.DarkBlue, ConsoleColor.Gray);
        }
    }

    private void DrawMenuRow(int x, int y, int width, int index, MenuItem item, bool selected, bool addHitTarget = true)
    {
        var fg = selected ? ConsoleColor.White : ConsoleColor.Black;
        var bg = selected ? ConsoleColor.Red : ConsoleColor.Gray;
        var labelWidth = Math.Min(30, Math.Max(18, width / 4));
        var valueWidth = Math.Max(0, width - labelWidth - 4);
        var numberedLabel = index < 9 ? $"{index + 1} {item.Label}" : $"  {item.Label}";
        var label = Fit(numberedLabel, labelWidth).PadRight(labelWidth);
        var value = Fit(item.Value, valueWidth);
        if (addHitTarget)
        {
            _hitTargets.Add(new HitTarget(x, y, width, 1, HitAction.MenuItem, index));
        }

        WriteAt(x, y, $" {label}  {value}", fg, bg, width);
    }

    private void DrawButtons(int x, int y, int width, bool addHitTargets = true)
    {
        var buttons = BuildButtons();
        ClampButtonFocus(buttons.Length);

        var total = buttons.Sum(button => button.Label.Length + 4);
        var currentX = x + Math.Max(2, (width - total) / 2);
        for (var i = 0; i < buttons.Length; i++)
        {
            var button = buttons[i];
            var buttonWidth = button.Label.Length + 2;
            if (addHitTargets)
            {
                _hitTargets.Add(new HitTarget(currentX, y, buttonWidth, 1, button.Action));
            }

            var focused = i == _focusedButtonIndex;
            var foreground = focused ? ConsoleColor.White : ConsoleColor.Black;
            var background = focused ? ConsoleColor.Red : ConsoleColor.Gray;
            WriteAt(currentX, y, $" {button.Label} ", foreground, background, buttonWidth);
            currentX += button.Label.Length + 4;
        }
    }

    private static void DrawModalButton(HitTarget target, string label, bool focused)
    {
        var foreground = focused ? ConsoleColor.White : ConsoleColor.Black;
        var background = focused ? ConsoleColor.Red : ConsoleColor.Gray;
        WriteAt(target.X, target.Y, $" {label} ", foreground, background, target.Width);
    }

    private void DrawStatus(int width, int height)
    {
        var text = (_dirty ? " * " : "   ") + _status;
        WriteAt(0, height - 1, Fit(text, width), _dirty ? ConsoleColor.Yellow : ConsoleColor.Gray, ConsoleColor.Blue, width);
    }

    private static int CalculateVisibleStart(int itemCount, int visibleRows, int selectedIndex) =>
        Math.Max(0, Math.Min(selectedIndex - visibleRows + 1, Math.Max(0, itemCount - visibleRows)));

    private static int CalculateInputViewStart(int cursor, int valueLength, int inputWidth)
    {
        var viewStart = Math.Max(0, Math.Min(cursor, valueLength) - inputWidth + 1);
        if (cursor < viewStart)
        {
            viewStart = cursor;
        }

        return viewStart;
    }

    private string DialogPrompt() =>
        _screen switch
        {
            Screen.Main => "Setup Options",
            Screen.Groups => "Group Options",
            Screen.ExternalSources => "External Source Options",
            Screen.Receive => "Receive Options",
            Screen.Multicast => "Multicast Options",
            Screen.NetworkMapping => "Network Mapping Options",
            Screen.ConfigFile => "Configuration File",
            Screen.Advanced => "Advanced SDK Options",
            _ => "NDI Configuration"
        };

    private string? PromptText(string title, string initialValue, Func<string, string?> validate)
    {
        var value = new StringBuilder(initialValue);
        var cursor = value.Length;
        string? error = null;
        int terminalWidth = 0;
        int terminalHeight = 0;
        int dialogWidth = 0;
        int dialogHeight = 0;
        int x = 0;
        int y = 0;
        int inputX = 0;
        int inputY = 0;
        int inputWidth = 0;
        int viewStart = 0;
        HitTarget ok = new(0, 0, 0, 0, HitAction.Select);
        HitTarget cancel = new(0, 0, 0, 0, HitAction.Cancel);
        var inputFocused = true;
        var buttonIndex = 0;

        DrawPromptDialog();

        while (true)
        {
            if (Console.WindowWidth != terminalWidth || Console.WindowHeight != terminalHeight)
            {
                DrawPromptDialog();
            }
            else
            {
                DrawPromptInput();
            }

            var input = TerminalInputReader.TryRead(ResizePollInterval);
            if (input is null)
            {
                continue;
            }

            Console.CursorVisible = false;

            if (input.Mouse is { Pressed: true, Button: MouseButton.Left } mouse)
            {
                if (ok.Contains(mouse.X, mouse.Y))
                {
                    inputFocused = false;
                    buttonIndex = 0;
                    var result = value.ToString().Trim();
                    error = validate(result);
                    if (error is null)
                    {
                        return result;
                    }
                }
                else if (cancel.Contains(mouse.X, mouse.Y))
                {
                    inputFocused = false;
                    buttonIndex = 1;
                    return null;
                }
                else if (mouse.Y == inputY && mouse.X >= inputX && mouse.X < inputX + inputWidth)
                {
                    inputFocused = true;
                    cursor = Math.Clamp(viewStart + mouse.X - inputX, 0, value.Length);
                    DrawPromptButtons();
                }

                continue;
            }

            if (input.Key is not { } key)
            {
                continue;
            }

            if (key.Key == ConsoleKey.Escape)
            {
                return null;
            }

            if (!inputFocused)
            {
                if (key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.Tab)
                {
                    buttonIndex = buttonIndex == 0 ? 1 : 0;
                    DrawPromptButtons();
                    continue;
                }

                if (key.Key == ConsoleKey.UpArrow)
                {
                    inputFocused = true;
                    DrawPromptButtons();
                    continue;
                }

                if (key.Key is ConsoleKey.Enter or ConsoleKey.Spacebar)
                {
                    if (buttonIndex == 1)
                    {
                        return null;
                    }

                    var result = value.ToString().Trim();
                    error = validate(result);
                    if (error is null)
                    {
                        return result;
                    }
                }

                continue;
            }

            if (key.Key is ConsoleKey.DownArrow or ConsoleKey.Tab)
            {
                inputFocused = false;
                DrawPromptButtons();
            }
            else if (key.Key == ConsoleKey.Enter)
            {
                var result = value.ToString().Trim();
                error = validate(result);
                if (error is null)
                {
                    return result;
                }
            }
            else if (key.Key == ConsoleKey.LeftArrow)
            {
                cursor = Math.Max(0, cursor - 1);
            }
            else if (key.Key == ConsoleKey.RightArrow)
            {
                cursor = Math.Min(value.Length, cursor + 1);
            }
            else if (key.Key == ConsoleKey.Home)
            {
                cursor = 0;
            }
            else if (key.Key == ConsoleKey.End)
            {
                cursor = value.Length;
            }
            else if (key.Key == ConsoleKey.Backspace && cursor > 0)
            {
                value.Remove(cursor - 1, 1);
                cursor--;
                error = null;
            }
            else if (key.Key == ConsoleKey.Delete && cursor < value.Length)
            {
                value.Remove(cursor, 1);
                error = null;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                value.Insert(cursor, key.KeyChar);
                cursor++;
                error = null;
            }
        }

        void DrawPromptDialog()
        {
            Render();
            terminalWidth = Console.WindowWidth;
            terminalHeight = Console.WindowHeight;
            dialogWidth = Math.Min(Math.Max(60, title.Length + 18), terminalWidth);
            dialogHeight = 10;
            x = (terminalWidth - dialogWidth) / 2;
            y = (terminalHeight - dialogHeight) / 2;
            inputX = x + 3;
            inputY = y + 4;
            inputWidth = Math.Max(1, dialogWidth - 6);

            DrawDialog(x, y, dialogWidth, dialogHeight, AppBrand.Title);
            WriteAt(x + 3, y + 2, title, ConsoleColor.Black, ConsoleColor.Gray, dialogWidth - 6);

            var buttonY = y + dialogHeight - 2;
            ok = new HitTarget(x + dialogWidth / 2 - 11, buttonY, 6, 1, HitAction.Select);
            cancel = new HitTarget(x + dialogWidth / 2 + 4, buttonY, 10, 1, HitAction.Cancel);
            DrawPromptButtons();
            DrawPromptInput();
        }

        void DrawPromptButtons()
        {
            DrawModalButton(ok, "<Ok>", !inputFocused && buttonIndex == 0);
            DrawModalButton(cancel, "<Cancel>", !inputFocused && buttonIndex == 1);
        }

        void DrawPromptInput()
        {
            viewStart = CalculateInputViewStart(cursor, value.Length, inputWidth);
            var visible = value.ToString(viewStart, Math.Min(inputWidth, value.Length - viewStart));
            WriteAt(inputX, inputY, visible, ConsoleColor.White, ConsoleColor.Blue, inputWidth);
            WriteAt(x + 3, y + 6, error ?? string.Empty, error is null ? ConsoleColor.Black : ConsoleColor.Red, ConsoleColor.Gray, dialogWidth - 6);
            Console.CursorVisible = inputFocused;
            if (inputFocused)
            {
                Console.SetCursorPosition(Math.Min(inputX + cursor - viewStart, inputX + inputWidth - 1), inputY);
            }
        }
    }

    private bool Confirm(string message)
    {
        var buttonIndex = 0;

        while (true)
        {
            Render();
            var width = Console.WindowWidth;
            var height = Console.WindowHeight;
            var dialogWidth = Math.Min(Math.Max(48, message.Length + 10), width);
            var dialogHeight = 8;
            var x = (width - dialogWidth) / 2;
            var y = (height - dialogHeight) / 2;
            DrawDialog(x, y, dialogWidth, dialogHeight, AppBrand.Title);
            WriteAt(x + 3, y + 3, message, ConsoleColor.Black, ConsoleColor.Gray, dialogWidth - 6);

            var yes = new HitTarget(x + dialogWidth / 2 - 12, y + 5, 7, 1, HitAction.Select);
            var no = new HitTarget(x + dialogWidth / 2 + 5, y + 5, 6, 1, HitAction.Cancel);
            DrawConfirmButtons();

            while (true)
            {
                var input = TerminalInputReader.TryRead(ResizePollInterval);
                if (input is null)
                {
                    if (TerminalSizeChanged())
                    {
                        break;
                    }

                    continue;
                }

                if (input.Mouse is { Pressed: true, Button: MouseButton.Left } mouse)
                {
                    if (yes.Contains(mouse.X, mouse.Y))
                    {
                        return true;
                    }

                    if (no.Contains(mouse.X, mouse.Y))
                    {
                        return false;
                    }

                    continue;
                }

                if (input.Key is not { } key)
                {
                    continue;
                }

                if (key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.Tab)
                {
                    buttonIndex = buttonIndex == 0 ? 1 : 0;
                    DrawConfirmButtons();
                    continue;
                }

                if (key.Key is ConsoleKey.Enter or ConsoleKey.Spacebar)
                {
                    return buttonIndex == 0;
                }

                if (key.Key == ConsoleKey.Y)
                {
                    return true;
                }

                if (key.Key is ConsoleKey.Escape or ConsoleKey.N)
                {
                    return false;
                }
            }

            void DrawConfirmButtons()
            {
                DrawModalButton(yes, "<Yes>", buttonIndex == 0);
                DrawModalButton(no, "<No>", buttonIndex == 1);
            }
        }
    }

    private bool ConfirmDetailed(string title, string message, string yesLabel, string noLabel)
    {
        var buttonIndex = 0;

        while (true)
        {
            Render();
            var width = Console.WindowWidth;
            var height = Console.WindowHeight;
            var dialogWidth = Math.Min(width, Math.Max(MinWidth, Math.Min(78, width - 8)));
            var contentWidth = Math.Max(1, dialogWidth - 6);
            var lines = WrapText(message, contentWidth);
            var dialogHeight = Math.Min(height, lines.Count + 7);
            var x = (width - dialogWidth) / 2;
            var y = Math.Max(0, (height - dialogHeight) / 2);
            DrawDialog(x, y, dialogWidth, dialogHeight, AppBrand.Title);
            WriteAt(x + 3, y + 2, title, ConsoleColor.DarkRed, ConsoleColor.Gray, contentWidth);

            var availableLines = Math.Max(0, dialogHeight - 6);
            for (var i = 0; i < availableLines; i++)
            {
                var line = i < lines.Count ? lines[i] : string.Empty;
                WriteAt(x + 3, y + 3 + i, line, ConsoleColor.Black, ConsoleColor.Gray, contentWidth);
            }

            var yesWidth = yesLabel.Length + 2;
            var noWidth = noLabel.Length + 2;
            var totalButtonWidth = yesWidth + noWidth + 4;
            var buttonY = y + dialogHeight - 2;
            var yes = new HitTarget(x + Math.Max(2, (dialogWidth - totalButtonWidth) / 2), buttonY, yesWidth, 1, HitAction.Select);
            var no = new HitTarget(yes.X + yesWidth + 4, buttonY, noWidth, 1, HitAction.Cancel);
            DrawConfirmButtons();

            while (true)
            {
                var input = TerminalInputReader.TryRead(ResizePollInterval);
                if (input is null)
                {
                    if (TerminalSizeChanged())
                    {
                        break;
                    }

                    continue;
                }

                if (input.Mouse is { Pressed: true, Button: MouseButton.Left } mouse)
                {
                    if (yes.Contains(mouse.X, mouse.Y))
                    {
                        return true;
                    }

                    if (no.Contains(mouse.X, mouse.Y))
                    {
                        return false;
                    }

                    continue;
                }

                if (input.Key is not { } key)
                {
                    continue;
                }

                if (key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.Tab)
                {
                    buttonIndex = buttonIndex == 0 ? 1 : 0;
                    DrawConfirmButtons();
                    continue;
                }

                if (key.Key is ConsoleKey.Enter or ConsoleKey.Spacebar)
                {
                    return buttonIndex == 0;
                }

                if (key.Key is ConsoleKey.Escape or ConsoleKey.C)
                {
                    return false;
                }
            }

            void DrawConfirmButtons()
            {
                DrawModalButton(yes, yesLabel, buttonIndex == 0);
                DrawModalButton(no, noLabel, buttonIndex == 1);
            }
        }
    }

    private void ShowMessage(string title, string message)
    {
        while (true)
        {
            Render();
            var width = Console.WindowWidth;
            var height = Console.WindowHeight;
            var dialogWidth = Math.Min(Math.Max(52, message.Length + 10), width);
            var dialogHeight = 8;
            var x = (width - dialogWidth) / 2;
            var y = (height - dialogHeight) / 2;
            DrawDialog(x, y, dialogWidth, dialogHeight, AppBrand.Title);
            WriteAt(x + 3, y + 2, title, ConsoleColor.Black, ConsoleColor.Gray, dialogWidth - 6);
            WriteAt(x + 3, y + 3, message, ConsoleColor.Black, ConsoleColor.Gray, dialogWidth - 6);

            var ok = new HitTarget(x + (dialogWidth - 6) / 2, y + 5, 6, 1, HitAction.Select);
            DrawModalButton(ok, "<OK>", focused: true);

            while (true)
            {
                var input = TerminalInputReader.TryRead(ResizePollInterval);
                if (input is null)
                {
                    if (TerminalSizeChanged())
                    {
                        break;
                    }

                    continue;
                }

                if (input.Mouse is { Pressed: true, Button: MouseButton.Left } mouse && !ok.Contains(mouse.X, mouse.Y))
                {
                    continue;
                }

                return;
            }
        }
    }

    private void ShowHelp()
    {
        var lines = new[]
        {
            "This editor follows the menuconfig / raspi-config pattern.",
            "Use Up/Down for rows; Left/Right or Tab for buttons.",
            "Use A to add external sources, D/Delete to remove one.",
            "Preferred NIC IPs can be selected from local addresses.",
            "F10 or Ctrl+S writes the JSON file.",
            "Esc backs out of a menu or cancels from the main menu.",
            "Mouse support uses standard SGR mouse reporting in SSH terminals."
        };

        while (true)
        {
            Render();
            var width = Console.WindowWidth;
            var height = Console.WindowHeight;
            var dialogWidth = Math.Min(72, width - 8);
            var dialogHeight = lines.Length + 6;
            var x = (width - dialogWidth) / 2;
            var y = (height - dialogHeight) / 2;
            DrawDialog(x, y, dialogWidth, dialogHeight, AppBrand.Title);
            WriteAt(x + 3, y + 2, "Help", ConsoleColor.Black, ConsoleColor.Gray, dialogWidth - 6);

            for (var i = 0; i < lines.Length; i++)
            {
                WriteAt(x + 3, y + 3 + i, lines[i], ConsoleColor.Black, ConsoleColor.Gray, dialogWidth - 6);
            }

            var ok = new HitTarget(x + (dialogWidth - 6) / 2, y + dialogHeight - 2, 6, 1, HitAction.Select);
            DrawModalButton(ok, "<OK>", focused: true);

            while (true)
            {
                var input = TerminalInputReader.TryRead(ResizePollInterval);
                if (input is null)
                {
                    if (TerminalSizeChanged())
                    {
                        break;
                    }

                    continue;
                }

                if (input.Mouse is { Pressed: true, Button: MouseButton.Left } mouse && !ok.Contains(mouse.X, mouse.Y))
                {
                    continue;
                }

                return;
            }
        }
    }

    private static string? ValidateGroupList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Split(',', StringSplitOptions.TrimEntries).Any(string.IsNullOrWhiteSpace)
            ? "Group names cannot be blank."
            : null;
    }

    private static string? ValidateIpAddress(string value, bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return allowEmpty ? null : "IP address is required.";
        }

        return IPAddress.TryParse(value.Trim(), out _) ? null : "Enter a valid IP address.";
    }

    private static string? ValidateIpList(string value)
    {
        var parts = SplitList(value);
        if (parts.Count == 0)
        {
            return "At least one IP address is required.";
        }

        return parts.Any(part => !IPAddress.TryParse(part, out _))
            ? "Enter comma-separated IP addresses."
            : null;
    }

    private static string? ValidateDiscoveryServerList(string value)
    {
        var parts = SplitList(value);
        if (parts.Count == 0)
        {
            return "At least one discovery server is required.";
        }

        foreach (var part in parts)
        {
            if (part.Any(char.IsWhiteSpace))
            {
                return "Discovery servers cannot contain whitespace.";
            }

            if (part.Contains(':', StringComparison.Ordinal) && !IPAddress.TryParse(part, out _))
            {
                var lastColon = part.LastIndexOf(':');
                var host = part[..lastColon];
                var portText = part[(lastColon + 1)..];
                if (host.Length == 0 || !int.TryParse(portText, out var port) || port is < 1 or > 65535)
                {
                    return "Use host or host:port. Ports must be 1-65535.";
                }
            }
        }

        return null;
    }

    private static string? ValidateSubnetList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        foreach (var part in SplitList(value))
        {
            var pieces = part.Split('/', StringSplitOptions.TrimEntries);
            if (pieces.Length > 2 || !IPAddress.TryParse(pieces[0], out var address))
            {
                return "Enter IP addresses or CIDR subnets.";
            }

            if (pieces.Length == 2)
            {
                var maxPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
                if (!int.TryParse(pieces[1], out var prefix) || prefix < 0 || prefix > maxPrefix)
                {
                    return $"CIDR prefix must be 0-{maxPrefix}.";
                }
            }
        }

        return null;
    }

    private static string? ValidateTtl(string value)
    {
        if (!int.TryParse(value, out var ttl) || ttl is < 1 or > 255)
        {
            return "TTL must be between 1 and 255.";
        }

        return null;
    }

    private static string? ValidateOptionalName(string value) =>
        value.Contains(',', StringComparison.Ordinal) ? "Commas are not supported here." : null;

    private static string? ValidateOptionalText(string value) => null;

    private static string? ValidateCodecQuality(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value, out var quality) && quality > 0
            ? null
            : "Enter a positive integer percentage, or leave blank for default.";
    }

    private static string? ValidateCodecMode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "auto" or "4:2:2" or "4:2:0"
            ? null
            : "Use auto, 4:2:2, or 4:2:0.";
    }

    private static string? ValidateRegex(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            _ = new Regex(value);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    private static List<string> SplitList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> WrapText(string text, int width)
    {
        var lines = new List<string>();
        if (width <= 0)
        {
            return lines;
        }

        foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var line = new StringBuilder();
            foreach (var word in words)
            {
                if (word.Length > width)
                {
                    if (line.Length > 0)
                    {
                        lines.Add(line.ToString());
                        line.Clear();
                    }

                    for (var offset = 0; offset < word.Length; offset += width)
                    {
                        lines.Add(word.Substring(offset, Math.Min(width, word.Length - offset)));
                    }

                    continue;
                }

                if (line.Length == 0)
                {
                    line.Append(word);
                    continue;
                }

                if (line.Length + 1 + word.Length <= width)
                {
                    line.Append(' ').Append(word);
                    continue;
                }

                lines.Add(line.ToString());
                line.Clear();
                line.Append(word);
            }

            if (line.Length > 0)
            {
                lines.Add(line.ToString());
            }
        }

        return lines;
    }

    private static List<NicAddressOption> GetLocalAdapterAddressOptions()
    {
        var options = new List<NicAddressOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                IPInterfaceProperties properties;
                try
                {
                    properties = nic.GetIPProperties();
                }
                catch (NetworkInformationException)
                {
                    continue;
                }

                foreach (var addressInfo in properties.UnicastAddresses)
                {
                    var address = addressInfo.Address;
                    if (IPAddress.IsLoopback(address) ||
                        address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) ||
                        address.IsIPv6LinkLocal)
                    {
                        continue;
                    }

                    var text = address.ToString();
                    if (seen.Add(text))
                    {
                        options.Add(new NicAddressOption(text, nic.Name));
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            return options;
        }

        return options;
    }

    private static string Checkbox(bool value) => value ? "[*]" : "[ ]";

    private static string EmptyText(string? value) => string.IsNullOrWhiteSpace(value) ? "<not set>" : value;

    private bool TerminalSizeChanged() =>
        Console.WindowWidth != _lastTerminalWidth || Console.WindowHeight != _lastTerminalHeight;

    private static int CalculateWhiptailWidth(int terminalWidth)
    {
        var dialogWidth = terminalWidth < MinWidth ? WhiptailFallbackWidth : terminalWidth;
        if (terminalWidth > 80)
        {
            var margin = Math.Clamp((terminalWidth - 80) / 2, 4, 8);
            dialogWidth = terminalWidth - (margin * 2);
        }

        if (terminalWidth > 178)
        {
            dialogWidth = Math.Min(dialogWidth, WhiptailMaxWidth);
        }

        return Math.Clamp(dialogWidth, MinWidth, terminalWidth);
    }

    private static void DrawDialog(int x, int y, int width, int height, string title)
    {
        FillRect(x + 2, y + 1, width, height, ConsoleColor.DarkBlue);
        FillRect(x, y, width, height, ConsoleColor.Gray);
        WriteAt(x, y, "┌" + new string('─', width - 2) + "┐", ConsoleColor.Black, ConsoleColor.Gray, width);
        for (var row = y + 1; row < y + height - 1; row++)
        {
            WriteAt(x, row, "│", ConsoleColor.Black, ConsoleColor.Gray, 1);
            WriteAt(x + width - 1, row, "│", ConsoleColor.Black, ConsoleColor.Gray, 1);
        }

        WriteAt(x, y + height - 1, "└" + new string('─', width - 2) + "┘", ConsoleColor.Black, ConsoleColor.Gray, width);
        var titleText = $" {Fit(title, Math.Max(0, width - 8))} ";
        var titleX = x + Math.Max(2, (width - titleText.Length) / 2);
        WriteAt(titleX, y, titleText, ConsoleColor.DarkRed, ConsoleColor.Gray);
    }

    private static void FillRect(int x, int y, int width, int height, ConsoleColor background)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        for (var row = 0; row < height; row++)
        {
            WriteAt(x, y + row, string.Empty, ConsoleColor.White, background, width);
        }
    }

    private static void WriteAt(int x, int y, string text, ConsoleColor foreground, ConsoleColor background, int width = -1)
    {
        if (x < 0 || y < 0 || x >= Console.WindowWidth || y >= Console.WindowHeight)
        {
            return;
        }

        var available = Console.WindowWidth - x;
        if (width < 0)
        {
            width = Math.Min(text.Length, available);
        }
        else if (width > available)
        {
            width = available;
        }

        if (width <= 0)
        {
            return;
        }

        Console.ForegroundColor = foreground;
        Console.BackgroundColor = background;
        Console.SetCursorPosition(x, y);
        Console.Write(Fit(text, width).PadRight(width));
    }

    private static string Center(string value, int width)
    {
        value = Fit(value, width);
        var left = Math.Max(0, (width - value.Length) / 2);
        return new string(' ', left) + value;
    }

    private static string Fit(string value, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        if (value.Length <= width)
        {
            return value;
        }

        return width == 1 ? "~" : value[..(width - 1)] + "~";
    }
}

internal enum Screen
{
    None,
    Main,
    Groups,
    ExternalSources,
    Receive,
    Multicast,
    NetworkMapping,
    ConfigFile,
    Advanced
}

internal enum ItemKind
{
    None,
    EditReceiveGroups,
    EditSendGroups,
    ExternalSource,
    EmptyExternalSources,
    ToggleAllowUdp,
    ToggleAllowMultiTcp,
    ToggleAllowReliableUdp,
    ToggleMulticast,
    MulticastPrefix,
    MulticastMask,
    MulticastTtl,
    MulticastSubnets,
    ToggleDiscovery,
    DiscoveryServers,
    TogglePreferredNic,
    PreferredNicAddresses,
    ToggleMachineName,
    MachineName,
    ToggleSourceFilter,
    SourceFilterRegex,
    ConfigPath,
    ReliableUdpStatus,
    CodecShqQuality,
    CodecShqMode,
    VendorName,
    VendorId
}

internal sealed record MenuItem(
    string Label,
    string Value,
    ItemKind Kind = ItemKind.None,
    Screen TargetScreen = Screen.None,
    int Index = -1)
{
    public static MenuItem Screen(string label, string value, Screen target) =>
        new(label, value, TargetScreen: target);
}

internal sealed record MenuLayout(
    int MenuX,
    int MenuY,
    int MenuWidth,
    int MenuHeight,
    int DialogX,
    int ButtonY,
    int DialogWidth,
    int TerminalWidth,
    int TerminalHeight);

internal sealed record NicAddressOption(string Address, string Memo);

internal sealed record ButtonSpec(string Label, HitAction Action)
{
    public static readonly ButtonSpec Select = new("<Select>", HitAction.Select);
    public static readonly ButtonSpec Back = new("<Back>", HitAction.Back);
    public static readonly ButtonSpec Apply = new("<Apply>", HitAction.Apply);
    public static readonly ButtonSpec Finish = new("<Finish>", HitAction.Cancel);
}
