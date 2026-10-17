using System.Collections.ObjectModel;
using System.IO;
using BACprobe.Core.Discovery;
using BACprobe.Core.Mstp;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BACprobe.App;

/// <summary>What the tech agreed to: which trunk to join, and as which master (the plan already checked it is safe).</summary>
public sealed record MstpConnectChoice(string Port, int Baud, MstpJoinPlan Plan);

/// <summary>
/// The "Connect to an MS/TP trunk" dialog. It only LISTENS (baud detection and the 15 s survey); joining, which transmits, is a
/// separate step the window confirms in plain English. <see cref="Choice"/> is set only when the survey says it is safe.
/// </summary>
public sealed partial class MstpConnectViewModel : ObservableObject, IDisposable
{
    private const int SurveySeconds = 15;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<FtdiLatency>? _latencies;

    public MstpConnectViewModel()
    {
        RefreshPorts();
        SelectedBaud = MstpViewModel.Bauds[0];
    }

    public ObservableCollection<string> Ports { get; } = [];
    public static IReadOnlyList<string> Bauds => MstpViewModel.Bauds;
    public ObservableCollection<FindingRow> Findings { get; } = [];

    [ObservableProperty] private string? _selectedPort;
    [ObservableProperty] private string _selectedBaud = "";
    [ObservableProperty] private string _macText = "Auto";
    [ObservableProperty] private string _status = "Pick the adapter's COM port and press Listen. Nothing is sent until you have seen what the trunk looks like and agreed.";
    [ObservableProperty] private string _summary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLatencyWarning))]
    private string _latencyWarning = "";

    public bool HasLatencyWarning => LatencyWarning.Length > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ListenCommand), nameof(CancelListenCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    /// <summary>Set once a survey says the trunk is safe to join; null otherwise (and cleared whenever a new listen starts).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanJoin))]
    private MstpConnectChoice? _choice;

    public bool CanJoin => Choice is not null && !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanJoin));

    partial void OnSelectedPortChanged(string? value)
    {
        Choice = null; // a survey of another port or baud says nothing about this one
        _ = UpdateLatencyWarningAsync(value);
    }

    partial void OnSelectedBaudChanged(string value) => Choice = null;
    partial void OnMacTextChanged(string value) => Choice = null;

    private async Task UpdateLatencyWarningAsync(string? port)
    {
        LatencyWarning = "";
        if (port is null || !OperatingSystem.IsWindows()) return;
        _latencies ??= await Task.Run(() => FtdiLatency.ReadAll());
        if (!string.Equals(SelectedPort, port, StringComparison.Ordinal)) return; // another port was picked while the registry was read
        var bad = _latencies.FirstOrDefault(l => string.Equals(l.PortName, port, StringComparison.OrdinalIgnoreCase) && l.IsTooHigh);
        if (bad is not null) LatencyWarning = bad.Verdict;
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        var keep = SelectedPort;
        Ports.Clear();
        foreach (var p in MstpPortCapture.PortNames()) Ports.Add(p);
        SelectedPort = keep is not null && Ports.Contains(keep) ? keep : Ports.FirstOrDefault();
        _latencies = null; // an adapter may have been replugged or its timer changed
        _ = UpdateLatencyWarningAsync(SelectedPort);
        if (Ports.Count == 0)
            Status = "No serial ports found. Plug in the USB-RS485 adapter and install its driver, then press Refresh.";
    }

    private bool CanListen() => !IsBusy && SelectedPort is not null;

    [RelayCommand(CanExecute = nameof(CanListen))]
    private async Task ListenAsync()
    {
        var port = SelectedPort!;
        byte? mac = null;
        var macText = MacText.Trim();
        if (macText.Length > 0 && !macText.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            if (!byte.TryParse(macText, out var m) || m > 127)
            {
                Status = "The MAC needs to be a master address from 0 to 127, or Auto to let BACprobe pick a free one.";
                return;
            }
            mac = m;
        }

        Choice = null;
        Findings.Clear();
        Summary = "";
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            int baud;
            if (SelectedBaud == Bauds[0])
            {
                Status = $"Finding the trunk's baud rate on {port}: listening about 3 s at each common rate...";
                var progress = new Progress<string>(s => Status = $"Finding the baud rate on {port}. {s}");
                var (picked, _, explanation) = await Task.Run(() =>
                    MstpPortCapture.DetectBaud(port, 3, s => ((IProgress<string>)progress).Report(s), ct));
                if (picked is null)
                {
                    Status = explanation + " Pick the baud rate yourself and listen again.";
                    return;
                }
                baud = picked.Value;
                SelectedBaud = baud.ToString(); // clears Choice, which is still null here
                Status = explanation;
            }
            else baud = int.Parse(SelectedBaud);

            var survey = await MstpActive.SurveyAsync(port, baud, mac, SurveySeconds,
                new Progress<string>(s => Status = s), ct);

            var s = survey.Survey;
            var masters = string.Join(", ", s.Nodes.Where(n => n.Role == "master").Select(n => n.Mac));
            Summary = $"Heard {s.Frames} good frames ({s.Errors} damaged). Masters: {(masters.Length > 0 ? masters : "none")}. " +
                      $"All addresses seen: {(s.Nodes.Count > 0 ? string.Join(", ", s.Nodes.Select(n => n.Mac)) : "none")}.";
            foreach (var note in survey.Plan.Notes) Findings.Add(new FindingRow(note));
            if (survey.Plan.CanJoin)
            {
                Choice = new MstpConnectChoice(port, baud, survey.Plan);
                Status = $"The trunk looks safe to join as master MAC {survey.Plan.Mac}. Nothing has been sent yet.";
            }
            else Status = "Not safe to join this trunk, so nothing was sent. The reasons are listed below.";
        }
        catch (OperationCanceledException) { Status = "Cancelled. Nothing was sent."; }
        catch (IOException ex)
        {
            // The port-opening errors already carry their own likely cause and next step.
            Status = ex.Message.Contains("Likely cause", StringComparison.Ordinal)
                ? ex.Message
                : $"{ex.Message} Likely cause: another program has {port} open, or the adapter was unplugged. Next step: close the other program, then press Listen again.";
        }
        catch (UnauthorizedAccessException) { Status = $"{port} is in use by another program. Next step: close it (a second BACprobe window, a terminal, another MS/TP tool) and press Listen again."; }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void CancelListen() => _cts?.Cancel();

    public void Dispose() => _cts?.Cancel();
}
