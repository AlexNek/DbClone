using System.ComponentModel;
using System.Windows.Threading;

using DbClone.UI.Settings;

namespace DbClone.UI.Services;

/// <summary>
/// Debounces <see cref="UserSettings"/> property changes and persists them.
/// Since every property in UserSettings IS a setting, no filtering is needed —
/// any PropertyChanged triggers a debounced save.
/// </summary>
public sealed class SettingsPersistenceManager
{
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(300);

    private readonly DispatcherTimer _debounceTimer;

    private readonly UserSettings _settings;

    private readonly ISettingsService _settingsService;

    private bool _isSuspended;

    public bool IsSuspended => _isSuspended;

    public SettingsPersistenceManager(ISettingsService settingsService, UserSettings settings)
    {
        _settingsService = settingsService;
        _settings = settings;
        _settings.PropertyChanged += OnSettingsPropertyChanged;

        _debounceTimer = new DispatcherTimer { Interval = DebounceInterval };
        _debounceTimer.Tick += OnDebounceElapsed;
    }

    public void Resume() => _isSuspended = false;

    /// <summary>
    /// Saves immediately (e.g. before a copy/compare operation or on window close).
    /// </summary>
    public void SaveNow()
    {
        if (_isSuspended) return;
        _debounceTimer.Stop();
        _settingsService.Save(_settings);
    }

    public void Suspend() => _isSuspended = true;

    private void OnDebounceElapsed(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        _settingsService.Save(_settings);
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isSuspended) return;
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }
}
