using System.Collections.ObjectModel;
using System.Linq;
using mqttMultimeter.Common;
using ReactiveUI;

namespace mqttMultimeter.Pages.Connection;

public sealed class MessageProcessingOptionsViewModel : BaseViewModel
{
    BoundedChannelFullModeOption _selectedFullMode;
    MessageProcessingProfile _selectedProfile;

    public MessageProcessingOptionsViewModel()
    {
        // Populate full-mode options (data-driven)
        foreach (var option in BoundedChannelFullModeOption.All)
        {
            FullModes.Add(option);
        }

        _selectedFullMode = FullModes[1];

        // Populate throughput profiles (from docs/high-throughput-guide.md)
        foreach (var profile in MessageProcessingProfile.BuiltIn)
        {
            Profiles.Add(profile);
        }

        // Default to High throughput and apply its values
        _selectedProfile = Profiles[2];
        ApplyProfile(_selectedProfile);
    }

    // ── Profile ─────────────────────────────────────────────────────────

    public ObservableCollection<MessageProcessingProfile> Profiles { get; } = [];

    public MessageProcessingProfile SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (this.RaiseAndSetIfChanged(ref _selectedProfile, value) == value)
            {
                ApplyProfile(value);
                this.RaisePropertyChanged(nameof(IsCustomProfile));
            }
        }
    }

    /// <summary>
    /// True when the Custom profile is active, allowing individual controls to be edited.
    /// </summary>
    public bool IsCustomProfile => _selectedProfile.IsCustom;

    // ── Channel settings ────────────────────────────────────────────────

    public bool UseBoundedChannel
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public int Capacity
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ObservableCollection<BoundedChannelFullModeOption> FullModes { get; } = [];

    public BoundedChannelFullModeOption SelectedFullMode
    {
        get => _selectedFullMode;
        set => this.RaiseAndSetIfChanged(ref _selectedFullMode, value);
    }

    // ── Delay settings ──────────────────────────────────────────────────

    public int MessageProcessingDelayMs
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public int PacketInspectionDelayMs
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    // ── Buffer time settings (reactive stream .Buffer()) ────────────────

    /// <summary>
    /// Buffer window (ms) used by message-based subscribers
    /// (Inflight, TopicExplorer, Log).
    /// </summary>
    public int MessageBufferMs
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// Buffer window (ms) used by the packet inspection subscriber.
    /// </summary>
    public int PacketBufferMs
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    // ── UI list limits ──────────────────────────────────────────────────

    /// <summary>
    /// Maximum items kept in each UI-bound list (Inflight, Packet Inspector, Log).
    /// When exceeded the oldest items are removed in batches of <see cref="TrimBatchSize"/>.
    /// </summary>
    public int MaxUiItems
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// Number of oldest items removed at once when a UI-bound list exceeds
    /// <see cref="MaxUiItems"/>. Removing in batches avoids per-item layout churn.
    /// </summary>
    public int TrimBatchSize
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// Interval (ms) at which the status-bar counters (Received, Notified, Buffered, Dropped)
    /// are refreshed via an <see cref="System.Reactive.Linq.Observable.Interval"/> observable.
    /// Default is 200 ms which is fast enough for visual feedback without wasting UI cycles.
    /// </summary>
    public int CounterUpdateMs
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    // ── Profile application ─────────────────────────────────────────────

    void ApplyProfile(MessageProcessingProfile profile)
    {
        if (profile.IsCustom)
        {
            return;
        }

        UseBoundedChannel = profile.UseBoundedChannel;
        Capacity = profile.Capacity;
        SelectedFullMode = FullModes.First(fm => fm.Value == profile.FullMode);
        MessageProcessingDelayMs = profile.MessageProcessingDelayMs;
        PacketInspectionDelayMs = profile.PacketInspectionDelayMs;
        MessageBufferMs = profile.MessageBufferMs;
        PacketBufferMs = profile.PacketBufferMs;
        MaxUiItems = profile.MaxUiItems;
        TrimBatchSize = profile.TrimBatchSize;
        CounterUpdateMs = profile.CounterUpdateMs;
    }
}
