using BACprobe.Core.Discovery;
using BACprobe.Core.Mstp;
using CommunityToolkit.Mvvm.Input;

namespace BACprobe.App;

/// <summary>
/// Working through an MS/TP trunk instead of a BACnet/IP adapter. BACprobe joins the trunk as a master (after listening first, see
/// <see cref="MstpConnectViewModel"/>) and from then on everything else in the app (devices, points, live values, writes, export...)
/// runs over that connection exactly as it does over IP.
/// </summary>
public sealed partial class MainViewModel
{
    // How long to keep asking for devices: a master only passes the token to a newcomer when it next polls for one.
    private static readonly TimeSpan MstpDiscoveryWait = TimeSpan.FromSeconds(30);

    private MstpConnectChoice? _mstp;

    public bool IsMstpConnected => _mstp is not null;

    /// <summary>Shown in the status bar while joined to a trunk.</summary>
    public string MstpConnectionText => _mstp is { } m ? $"MS/TP {m.Port} · {m.Baud} baud · BACprobe is MAC {m.Plan.Mac}" : "";

    /// <summary>Set by the window: shows the connect dialog; returns what the tech agreed to join, or null.</summary>
    public Func<MstpConnectViewModel, MstpConnectChoice?> ShowMstpConnect { get; set; } = _ => null;

    private void SetMstp(MstpConnectChoice? choice)
    {
        _mstp = choice;
        OnPropertyChanged(nameof(IsMstpConnected));
        OnPropertyChanged(nameof(MstpConnectionText));
        ScanCommand.NotifyCanExecuteChanged();
        LeaveMstpCommand.NotifyCanExecuteChanged();
    }

    private bool CanConnectMstp() => !IsScanning && !IsExporting;

    /// <summary>Listen to a trunk, then (if it is safe and the tech agrees) join it as a master and find the devices on it.</summary>
    [RelayCommand(CanExecute = nameof(CanConnectMstp))]
    private async Task ConnectMstpAsync()
    {
        if (ShowMstpConnect(new MstpConnectViewModel()) is not { } choice) return;
        if (!await ResolveOverridesAsync()) return; // joining replaces the current connection

        int low = -1, high = -1;
        if (LowText.Length > 0 || HighText.Length > 0)
        {
            if (!int.TryParse(LowText, out low) || !int.TryParse(HighText, out high) || low < 0 || high < low)
            {
                Status = "Instance range (Scan options) needs two whole numbers, low then high, or leave both empty.";
                return;
            }
        }

        IsScanning = true;
        BeginNewConnection();
        Status = $"Joining the trunk on {choice.Port} as master MAC {choice.Plan.Mac}...";
        try
        {
            // Opening the port and starting the token state machine can take a moment: keep it off the UI thread.
            var svc = await Task.Run(() => MstpActive.Join(choice.Port, choice.Baud, choice.Plan));
            _svc = svc;
            _writer = svc.CreateWriter(_log, _overrides);
            _writer.ReadOnly = ReadOnlyMode;
            CanUseLive = true;
            BbmdStatus = "";
            SetMstp(choice);
            await DiscoverMstpAsync(svc, low, high);
        }
        catch (Exception ex)
        {
            Status = $"Could not join the trunk: {ex.Message} Likely cause: another program has {choice.Port} open, or the adapter was unplugged. " +
                     "Next step: close the other program and try again.";
        }
        finally
        {
            IsScanning = false;
            ExportAllCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Scan while joined: ask the same trunk again (no new connection, so nothing about the ring changes).</summary>
    private async Task RescanMstpAsync(int low, int high)
    {
        if (_svc is not { } svc) return;
        IsScanning = true;
        BeginNewConnection(keepConnection: true);
        try { await DiscoverMstpAsync(svc, low, high); }
        catch (Exception ex)
        {
            Status = $"Scan failed: {ex.Message} Likely cause: the adapter was unplugged or another program took the port. Next step: Tools > Leave MS/TP trunk, then connect again.";
        }
        finally
        {
            IsScanning = false;
            ExportAllCommand.NotifyCanExecuteChanged();
        }
    }

    private void BeginNewConnection(bool keepConnection = false)
    {
        ClearNetworkCheck();
        StopLive();
        CanUseLive = keepConnection;
        ConnectionReset?.Invoke();
        ResetBrowsing();
        OfflineBanner = "";
        if (keepConnection) return;
        _svc?.Dispose();
        _svc = null;
        _writer = null;
        SetMstp(null);
    }

    private async Task DiscoverMstpAsync(DiscoveryService svc, int low, int high)
    {
        // The ring needs a moment to hand BACprobe its first token: keep asking until something answers or the wait runs out.
        var started = DateTime.UtcNow;
        IReadOnlyList<DiscoveredDevice> found = [];
        while (found.Count == 0 && DateTime.UtcNow - started < MstpDiscoveryWait)
        {
            var waited = (int)(DateTime.UtcNow - started).TotalSeconds;
            Status = $"Joined as MAC {_mstp?.Plan.Mac}. Waiting for the token, then asking every device to identify itself ({waited} of {MstpDiscoveryWait.TotalSeconds:0} s)...";
            found = await svc.WhoIsAsync(low, high, TimeSpan.FromSeconds(3));
        }
        CanUseLive = true;
        if (found.Count == 0)
        {
            Status = "No devices answered on the trunk. Likely cause: BACprobe has not been given the token yet (masters only poll for new nodes now and then), " +
                     "the devices are slaves that need a longer wait, or the instance range excludes them. Next step: press Scan again, and check the trunk with Tools > MS/TP monitor.";
            return;
        }
        await ShowFoundDevicesAsync(svc, found, scanWasFiltered: low >= 0);
    }

    /// <summary>Drop off the trunk (after offering to release any overrides this session left) and go back to the adapter list.</summary>
    [RelayCommand(CanExecute = nameof(IsMstpConnected))]
    private async Task LeaveMstpAsync()
    {
        if (!await ResolveOverridesAsync()) return;
        BeginNewConnection();
        Status = "Left the trunk. The other masters notice within a few seconds and the token ring settles on its own. Pick an adapter to scan BACnet/IP again.";
    }
}
