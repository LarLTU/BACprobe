using System.Windows;
using BACprobe.Core.Mstp;
using BACprobe.Core.Writing;

namespace BACprobe.App;

/// <summary>Listen to an MS/TP trunk, then (only if it looks safe and the tech types JOIN) choose to join it.</summary>
public partial class MstpConnectWindow : Window
{
    private readonly MstpConnectViewModel _vm;

    public MstpConnectWindow(MstpConnectViewModel viewModel)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        _vm = viewModel;
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose(); // stops a listen that is still running
    }

    /// <summary>The trunk the tech agreed to join, or null if they closed the window.</summary>
    public MstpConnectChoice? Result => DialogResult == true ? _vm.Choice : null;

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnJoin(object sender, RoutedEventArgs e)
    {
        if (_vm.Choice is not { } c) return;
        var content = new PromptContent(
            "Join the MS/TP trunk",
            $"BACprobe will TRANSMIT on {c.Port} as master MAC {c.Plan.Mac}",
            [
                new ConfirmFact("Port", $"{c.Port} at {c.Baud} baud"),
                new ConfirmFact("BACprobe's address", $"MAC {c.Plan.Mac} (Max_Master {c.Plan.MaxMaster})"),
            ],
            MstpActive.ConfirmationText(c.Port, c.Baud, c.Plan),
            Warning: "Joining and leaving makes the token ring pause for a moment while the other masters notice. Do not do this on a trunk you cannot afford to disturb.",
            TypeToConfirm: "JOIN");
        if (PromptWindow.Show(this, content, [new PromptButton("Join the trunk"), new PromptButton("Go back", IsCancel: true)], focusIndex: 1) == 0)
            DialogResult = true;
    }
}
