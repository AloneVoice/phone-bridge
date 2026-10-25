using System.IO;
using System.Media;
using System.Windows;
using Microsoft.Win32;
using PhoneBridge.Core.Server;

namespace PhoneBridge.App.Windows;

public partial class IncomingWindow : Window
{
    private const int MaxListed = 200;

    private readonly IncomingRequest _request;
    private readonly TaskCompletionSource<IncomingDecision> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _folder = "";

    public IncomingWindow(IncomingRequest request, string folder)
    {
        InitializeComponent();
        Theme.ApplyRoundedCorners(this);
        _request = request;

        Header.Text = $"„{request.Device.Name}” wysyła {Format.Files(request.Files.Count)}";
        Summary.Text = $"Łącznie {Format.Bytes(request.TotalBytes)}";
        var items = request.Files.Take(MaxListed).Select(f => new { f.Name, Size = Format.Bytes(f.Size) }).ToList();
        if (request.Files.Count > MaxListed)
            items.Add(new { Name = $"… i {Format.Files(request.Files.Count - MaxListed)} więcej", Size = "" });
        FileList.ItemsSource = items;
        SetFolder(folder);

        Closed += (_, _) => _result.TrySetResult(IncomingDecision.Rejected);
        Loaded += (_, _) =>
        {
            Activate();
            SystemSounds.Asterisk.Play();
        };
    }

    public Task<IncomingDecision> Result => _result.Task;
    public bool RememberAsAutoAccept => AutoAccept.IsChecked == true;

    private void SetFolder(string folder)
    {
        _folder = folder;
        FolderText.Text = folder;
        FolderText.ToolTip = folder;
        long? free = null;
        try { free = new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { }

        var enough = free is null || free >= _request.TotalBytes;
        SpaceWarning.Visibility = enough ? Visibility.Collapsed : Visibility.Visible;
        SpaceWarning.Text = enough ? "" : $"Za mało miejsca na dysku: wolne {Format.Bytes(free!.Value)}, potrzeba {Format.Bytes(_request.TotalBytes)}.";
        AcceptButton.IsEnabled = enough && Directory.Exists(folder);
    }

    private void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Wybierz folder dla odebranych plików", InitialDirectory = _folder };
        if (dialog.ShowDialog(this) == true)
            SetFolder(dialog.FolderName);
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        _result.TrySetResult(new IncomingDecision(true, _folder));
        Close();
    }

    private void OnReject(object sender, RoutedEventArgs e) => Close();
}
