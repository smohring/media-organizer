using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaOrganizer.App.Localization;
using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Update;

namespace MediaOrganizer.App.ViewModels;

/// <summary>The update panel: which version is running, whether a newer one
/// exists, and the two steps that lead to it.
///
/// <para><b>Nothing happens without a click.</b> The automatic check at start
/// asks the feed and then stops. It does not download, it does not install, and
/// it does not pop a window in front of whatever the user was doing — an
/// organizing run can take hours, and interrupting it to announce a patch
/// release would be worse than the patch is worth. The result shows up as a
/// line of text in the settings panel; the user decides when.</para></summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    /// <summary>How long a successful check stays good. Releases happen every
    /// few weeks at most, so asking once a day is already generous — and every
    /// check is a request the user did not ask for.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly AppSettings _settings;
    private readonly Action _persist;
    private readonly UpdateService _service;

    private UpdateManifest? _available;
    private string? _stagedDirectory;

    public UpdateViewModel(AppSettings settings, Action persist)
    {
        _settings = settings;
        _persist = persist;
        _service = new UpdateService(settings);
        _checkOnStart = settings.CheckForUpdatesOnStart;
    }

    public string CurrentVersionText => $"Version {AppVersion.CurrentDisplay}";

    [ObservableProperty]
    private bool _checkOnStart;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(InstallCommand))]
    private bool _isBusy;

    /// <summary>What the user reads under the version line. Always says
    /// something; "" would look like the panel is broken.</summary>
    [ObservableProperty]
    private string _statusText = Loc.Get("S.Update.NoCheckYet");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private string? _availableVersion;

    [ObservableProperty]
    private string? _releaseNotes;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isProgressVisible;

    public bool HasUpdate => !string.IsNullOrEmpty(AvailableVersion);

    public bool CanCheck => !IsBusy;

    public bool CanInstall => HasUpdate && !IsBusy;

    partial void OnCheckOnStartChanged(bool value)
    {
        _settings.CheckForUpdatesOnStart = value;
        _persist();
    }

    /// <summary>Runs the check at application start, if the user left it on and
    /// the last one is old enough. Silent about failures: a laptop that starts
    /// without network is the normal case, not an error worth a message.</summary>
    public async Task CheckOnStartAsync()
    {
        if (!_settings.CheckForUpdatesOnStart)
        {
            return;
        }

        if (_settings.LastUpdateCheckUtc is { } last
            && DateTime.UtcNow - last < CheckInterval)
        {
            StatusText = Loc.Format("S.Update.LastChecked", last.ToLocalTime());
            return;
        }

        await RunCheckAsync(announceFailure: false).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => RunCheckAsync(announceFailure: true);

    private async Task RunCheckAsync(bool announceFailure)
    {
        IsBusy = true;
        StatusText = Loc.Get("S.Update.Checking");
        try
        {
            var result = await _service.CheckAsync().ConfigureAwait(true);
            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable:
                    _available = result.Manifest;
                    AvailableVersion = result.Manifest!.ParsedVersion!.ToString(3);
                    ReleaseNotes = result.Manifest.Notes;
                    StatusText = string.IsNullOrWhiteSpace(result.Manifest.Released)
                        ? Loc.Format("S.Update.Available", AvailableVersion)
                        : Loc.Format(
                            "S.Update.AvailableReleased", AvailableVersion, result.Manifest.Released);
                    MarkChecked();
                    break;

                case UpdateCheckStatus.UpToDate:
                    _available = null;
                    AvailableVersion = null;
                    ReleaseNotes = null;
                    StatusText = Loc.Get("S.Update.UpToDate");
                    MarkChecked();
                    break;

                default:
                    // A failed check must not overwrite a pending update that an
                    // earlier check already found.
                    StatusText = announceFailure
                        ? Loc.Format("S.Update.CheckFailed", result.Message)
                        : StatusText;
                    break;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (_available is null)
        {
            return;
        }

        // Asked before the download, not after: nobody should wait for 150 MB to
        // find out that this copy cannot replace itself.
        if (!UpdateService.CanUpdateInPlace(out var problem))
        {
            StatusText = Loc.Format(problem.MessageKey, problem.MessageArgs);
            return;
        }

        IsBusy = true;
        IsProgressVisible = true;
        try
        {
            var progress = new Progress<(StatusReport Message, double Fraction)>(p =>
            {
                StatusText = Loc.Format(p.Message.MessageKey, p.Message.MessageArgs);
                Progress = p.Fraction;
            });

            _stagedDirectory = await _service
                .DownloadAsync(_available, progress)
                .ConfigureAwait(true);

            // The exchange can only happen once this process is gone. Ask before
            // pulling the rug out — a run in progress would be lost.
            var answer = MessageBox.Show(
                Loc.Format("S.Update.InstallConfirm", AvailableVersion),
                Loc.Get("S.Update.InstallTitle"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.OK)
            {
                StatusText = Loc.Format("S.Update.ReadyToInstall", AvailableVersion);
                return;
            }

            if (UpdateService.ApplyAndRestart(_stagedDirectory))
            {
                Application.Current.Shutdown();
            }
            else
            {
                StatusText = Loc.Get("S.Update.StartFailed");
            }
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            IsProgressVisible = false;
        }
    }

    /// <summary>Opens the working folder with the downloaded archive — the way
    /// out when the automatic exchange does not work on a locked-down
    /// machine.</summary>
    [RelayCommand]
    private void OpenDownloadFolder()
    {
        try
        {
            if (Directory.Exists(UpdateService.WorkDirectory))
            {
                Process.Start(new ProcessStartInfo(UpdateService.WorkDirectory)
                {
                    UseShellExecute = true,
                });
            }
        }
        catch
        {
            // Explorer refused; nothing to recover from.
        }
    }

    private void MarkChecked()
    {
        _settings.LastUpdateCheckUtc = DateTime.UtcNow;
        _persist();
    }
}
