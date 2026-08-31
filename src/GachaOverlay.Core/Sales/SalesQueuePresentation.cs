using System.Globalization;

namespace GachaOverlay.Core.Sales;

public static class SalesAnimationDurations
{
    public static TimeSpan SoldTransition { get; } = TimeSpan.FromMilliseconds(200);

    public static TimeSpan CurrentTurnEnter { get; } = TimeSpan.FromMilliseconds(420);

    public static TimeSpan NextTurnEnter { get; } = TimeSpan.FromMilliseconds(180);
}

public enum SalesQueueContentMode
{
    Hidden,
    Normal,
    Empty,
    NextTurnSelf,
    CurrentTurnSelf,
}

public enum SalesHealthVisualMode
{
    Hidden,
    Live,
    Connecting,
    Resyncing,
    Paused,
    Degraded,
    Disconnected,
    Error,
}

public enum SalesStatusIconKind
{
    None,
    LiveDot,
    Spinner,
    Warning,
    Error,
}

public enum SalesQueueAccentKind
{
    Standard,
    NextTurn,
    CurrentTurn,
}

public enum SalesQueueAnimationRequest
{
    None,
    SoldTransition,
    CurrentTurnEnter,
    NextTurnEnter,
}

public enum SalesQueueChangeReason
{
    None,
    TrustedSold,
    TrustedNotSold,
    SourceDeleted,
    SourceCreated,
    Resync,
    SettingsChanged,
    DisplayNameChanged,
}

public sealed record SalesQueueChangeContext(
    bool CurrentSellerChanged,
    string? PreviousCurrentSellerMessageId,
    string? NewCurrentSellerMessageId,
    SalesQueueChangeReason Reason,
    long StateRevision)
{
    public static SalesQueueChangeContext None { get; } = new(
        false,
        null,
        null,
        SalesQueueChangeReason.None,
        0);
}

public sealed record SalesQueueDisplayOptions(
    bool ShowCurrentSeller,
    bool ShowWaitingCount,
    bool ShowProduct,
    bool ShowNextWaitingUser);

public sealed record SalesQueuePresentationStrings(
    string LiveAccessibleName,
    string Connecting,
    string Resyncing,
    string OpenSalesChannelFormat,
    string Degraded,
    string Disconnected,
    string SensorError,
    string CurrentSellerFormat,
    string WaitingCountFormat,
    string ProductFormat,
    string NextSellerFormat,
    string QueueEmpty,
    string NoDisplayFields,
    string NextTurnSelf,
    string CurrentTurnSelf);

public sealed record SalesQueueFieldMeasurements(
    double CurrentSellerWidth,
    double WaitingCountWidth,
    double ProductWidth,
    double NextWaitingUserWidth)
{
    public static SalesQueueFieldMeasurements Empty { get; } = new(0, 0, 0, 0);
}

public sealed record SalesQueuePresentationInput(
    SalesQueueSnapshot Queue,
    SalesFeatureHealthSnapshot Health,
    SalesQueueDisplayOptions DisplayOptions,
    SalesQueuePresentationStrings Strings,
    string SalesChannelName,
    double AvailableWidth,
    SalesQueueFieldMeasurements Measurements,
    SalesQueuePresentationState? Previous,
    SalesQueueChangeContext Change,
    bool IsUltraCompact,
    bool IsHudVisible,
    bool AnimationsEnabled);

public sealed record SalesQueuePresentationState(
    SalesQueueContentMode ContentMode,
    SalesHealthVisualMode HealthMode,
    SalesStatusIconKind IconKind,
    SalesQueueAccentKind AccentKind,
    SalesQueueAnimationRequest AnimationRequest,
    bool IsVisible,
    bool IsSpinnerActive,
    bool IsTwoLine,
    string PrimaryText,
    string SecondaryText,
    string StatusText,
    string AccessibleStatus,
    SalesQueueVisibleFields VisibleFields,
    string? CurrentMessageId,
    string? NextMessageId,
    bool IsTrustedForNewPersonalAlert)
{
    public static SalesQueuePresentationState Hidden { get; } = new(
        SalesQueueContentMode.Hidden,
        SalesHealthVisualMode.Hidden,
        SalesStatusIconKind.None,
        SalesQueueAccentKind.Standard,
        SalesQueueAnimationRequest.None,
        false,
        false,
        false,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        SalesQueueVisibleFields.None,
        null,
        null,
        false);
}

public static class SalesQueuePresentationFactory
{
    public static SalesQueuePresentationState Create(SalesQueuePresentationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Queue);
        ArgumentNullException.ThrowIfNull(input.Health);
        ArgumentNullException.ThrowIfNull(input.DisplayOptions);
        ArgumentNullException.ThrowIfNull(input.Strings);
        ArgumentNullException.ThrowIfNull(input.Measurements);
        ArgumentNullException.ThrowIfNull(input.Change);

        if (!input.Queue.IsTrackingEnabled ||
            input.Health.State == SalesFeatureHealthState.Disabled)
        {
            return SalesQueuePresentationState.Hidden;
        }

        var healthVisual = ResolveHealth(input.Health, input.Strings, input.SalesChannelName);
        var current = input.Queue.CurrentSeller;
        var next = input.Queue.NextWaitingEntry;
        var currentId = current?.MessageId;
        var nextId = next?.MessageId;
        var trustworthy = input.Health.IsFullyTrustworthy &&
            input.Health.State == SalesFeatureHealthState.Live &&
            input.Health.Coverage == SalesCoverageState.Complete;

        var retainCurrentAlert = input.Previous?.ContentMode ==
                SalesQueueContentMode.CurrentTurnSelf &&
            current is not null &&
            input.Queue.CurrentSellerIsSelf &&
            string.Equals(input.Previous.CurrentMessageId, currentId, StringComparison.Ordinal);
        var enterCurrentAlert = !retainCurrentAlert &&
            current is not null &&
            input.Queue.CurrentSellerIsSelf &&
            current.ObservationTrust == SaleObservationTrust.Trusted &&
            trustworthy;

        var retainNextAlert = !retainCurrentAlert &&
            !enterCurrentAlert &&
            input.Previous?.ContentMode == SalesQueueContentMode.NextTurnSelf &&
            next is not null &&
            input.Queue.NextSellerIsSelf &&
            string.Equals(input.Previous.NextMessageId, nextId, StringComparison.Ordinal);
        var enterNextAlert = !retainCurrentAlert &&
            !enterCurrentAlert &&
            !retainNextAlert &&
            next is not null &&
            input.Queue.NextSellerIsSelf &&
            next.ObservationTrust == SaleObservationTrust.Trusted &&
            trustworthy;

        var mode = retainCurrentAlert || enterCurrentAlert
            ? SalesQueueContentMode.CurrentTurnSelf
            : retainNextAlert || enterNextAlert
                ? SalesQueueContentMode.NextTurnSelf
                : current is null
                    ? SalesQueueContentMode.Empty
                    : SalesQueueContentMode.Normal;
        var accent = mode switch
        {
            SalesQueueContentMode.CurrentTurnSelf => SalesQueueAccentKind.CurrentTurn,
            SalesQueueContentMode.NextTurnSelf => SalesQueueAccentKind.NextTurn,
            _ => SalesQueueAccentKind.Standard,
        };

        var primary = string.Empty;
        var secondary = string.Empty;
        var visibleFields = SalesQueueVisibleFields.None;
        var isTwoLine = false;

        if (mode == SalesQueueContentMode.CurrentTurnSelf)
        {
            primary = input.Strings.CurrentTurnSelf;
            if (!string.IsNullOrWhiteSpace(healthVisual.StatusText))
            {
                secondary = healthVisual.StatusText;
                isTwoLine = true;
            }
        }
        else if (current is null)
        {
            if (trustworthy)
            {
                primary = input.Strings.QueueEmpty;
            }
            else
            {
                primary = healthVisual.StatusText;
            }
        }
        else
        {
            var fields = BuildFields(input, current, next);
            var reserveSecondLine = mode == SalesQueueContentMode.NextTurnSelf ||
                !string.IsNullOrWhiteSpace(healthVisual.StatusText);
            var layout = ResolveLayout(input, fields.Requested, reserveSecondLine);
            visibleFields = layout.VisibleFields;
            primary = Join(layout.FirstLineFields, fields);
            secondary = Join(layout.SecondLineFields, fields);
            isTwoLine = layout.LineCount == 2 && !string.IsNullOrWhiteSpace(secondary);

            if (string.IsNullOrWhiteSpace(primary))
            {
                primary = input.Strings.NoDisplayFields;
            }

            var requiredSecond = mode == SalesQueueContentMode.NextTurnSelf
                ? input.Strings.NextTurnSelf
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(healthVisual.StatusText))
            {
                requiredSecond = string.IsNullOrWhiteSpace(requiredSecond)
                    ? healthVisual.StatusText
                    : $"{requiredSecond} · {healthVisual.StatusText}";
            }

            if (!string.IsNullOrWhiteSpace(requiredSecond))
            {
                secondary = requiredSecond;
                isTwoLine = true;
            }
        }

        var isVisible = true;
        if (input.IsUltraCompact &&
            mode is SalesQueueContentMode.Normal or SalesQueueContentMode.Empty)
        {
            var actionRequired = input.Health.State is
                SalesFeatureHealthState.Paused or
                SalesFeatureHealthState.Degraded or
                SalesFeatureHealthState.Disconnected or
                SalesFeatureHealthState.Error;
            isVisible = actionRequired;
            if (actionRequired)
            {
                primary = healthVisual.StatusText;
                secondary = string.Empty;
                visibleFields = SalesQueueVisibleFields.None;
                isTwoLine = false;
            }
        }

        var animation = SalesQueueAnimationRequest.None;
        if (input.AnimationsEnabled && input.IsHudVisible && isVisible)
        {
            if (enterCurrentAlert)
            {
                animation = SalesQueueAnimationRequest.CurrentTurnEnter;
            }
            else if (enterNextAlert)
            {
                animation = SalesQueueAnimationRequest.NextTurnEnter;
            }
            else if (input.Change.CurrentSellerChanged &&
                input.Change.Reason == SalesQueueChangeReason.TrustedSold &&
                !string.Equals(
                    input.Change.PreviousCurrentSellerMessageId,
                    input.Change.NewCurrentSellerMessageId,
                    StringComparison.Ordinal))
            {
                animation = SalesQueueAnimationRequest.SoldTransition;
            }
        }

        return new SalesQueuePresentationState(
            mode,
            healthVisual.Mode,
            healthVisual.Icon,
            accent,
            animation,
            isVisible,
            isVisible && input.IsHudVisible && healthVisual.Icon == SalesStatusIconKind.Spinner,
            isTwoLine,
            primary,
            secondary,
            healthVisual.StatusText,
            healthVisual.AccessibleStatus,
            visibleFields,
            currentId,
            nextId,
            trustworthy);
    }

    private static FieldValues BuildFields(
        SalesQueuePresentationInput input,
        SalesQueueEntry current,
        SalesQueueEntry? next)
    {
        var currentText = input.DisplayOptions.ShowCurrentSeller
            ? Format(input.Strings.CurrentSellerFormat, current.DisplayName)
            : string.Empty;
        var waitingText = input.DisplayOptions.ShowWaitingCount
            ? Format(input.Strings.WaitingCountFormat, input.Queue.WaitingCount)
            : string.Empty;
        var productText = input.DisplayOptions.ShowProduct && current.AllProducts.Count > 0
            ? SalesProductSummaryFormatter.Format(current.AllProducts)
            : string.Empty;
        var nextText = input.DisplayOptions.ShowNextWaitingUser && next is not null
            ? Format(input.Strings.NextSellerFormat, next.DisplayName)
            : string.Empty;
        var requested = SalesQueueVisibleFields.None;
        Add(currentText, SalesQueueVisibleFields.CurrentSeller);
        Add(waitingText, SalesQueueVisibleFields.WaitingCount);
        Add(productText, SalesQueueVisibleFields.Product);
        Add(nextText, SalesQueueVisibleFields.NextWaitingUser);
        return new FieldValues(
            currentText,
            waitingText,
            productText,
            nextText,
            requested);

        void Add(string text, SalesQueueVisibleFields field)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                requested |= field;
            }
        }
    }

    private static SalesQueueLayoutResult ResolveLayout(
        SalesQueuePresentationInput input,
        SalesQueueVisibleFields requested,
        bool reserveSecondLine)
    {
        var layoutInput = new SalesQueueLayoutInput(
            Math.Max(0, input.AvailableWidth),
            input.Measurements.CurrentSellerWidth,
            input.Measurements.WaitingCountWidth,
            input.Measurements.ProductWidth,
            input.Measurements.NextWaitingUserWidth,
            requested);
        var layout = SalesQueueLayoutPolicy.Decide(layoutInput);
        if (!reserveSecondLine || layout.LineCount == 1)
        {
            return layout;
        }

        var visible = requested;
        while (visible != SalesQueueVisibleFields.None)
        {
            var single = SalesQueueLayoutPolicy.Decide(layoutInput with
            {
                RequestedFields = visible,
            });
            if (single.LineCount == 1 && single.VisibleFields == visible)
            {
                return single;
            }

            visible = DropLowestPriority(visible);
        }

        return new SalesQueueLayoutResult(
            1,
            SalesQueueVisibleFields.None,
            SalesQueueVisibleFields.None,
            SalesQueueVisibleFields.None);
    }

    private static string Join(SalesQueueVisibleFields fields, FieldValues values)
    {
        var result = new List<string>(4);
        Add(SalesQueueVisibleFields.CurrentSeller, values.Current);
        Add(SalesQueueVisibleFields.Product, values.Product);
        Add(SalesQueueVisibleFields.NextWaitingUser, values.Next);
        Add(SalesQueueVisibleFields.WaitingCount, values.Waiting);
        return string.Join(" · ", result);

        void Add(SalesQueueVisibleFields field, string value)
        {
            if (fields.HasFlag(field) && !string.IsNullOrWhiteSpace(value))
            {
                result.Add(value);
            }
        }
    }

    private static SalesQueueVisibleFields DropLowestPriority(SalesQueueVisibleFields fields)
    {
        if (fields.HasFlag(SalesQueueVisibleFields.NextWaitingUser))
        {
            return fields & ~SalesQueueVisibleFields.NextWaitingUser;
        }

        if (fields.HasFlag(SalesQueueVisibleFields.Product))
        {
            return fields & ~SalesQueueVisibleFields.Product;
        }

        if (fields.HasFlag(SalesQueueVisibleFields.WaitingCount))
        {
            return fields & ~SalesQueueVisibleFields.WaitingCount;
        }

        return fields & ~SalesQueueVisibleFields.CurrentSeller;
    }

    private static HealthVisual ResolveHealth(
        SalesFeatureHealthSnapshot health,
        SalesQueuePresentationStrings strings,
        string salesChannelName)
    {
        var channel = string.IsNullOrWhiteSpace(salesChannelName)
            ? "#sales"
            : salesChannelName.Trim();
        if (!channel.StartsWith('#'))
        {
            channel = $"#{channel}";
        }
        return health.State switch
        {
            SalesFeatureHealthState.Disabled => new(
                SalesHealthVisualMode.Hidden,
                SalesStatusIconKind.None,
                string.Empty,
                string.Empty),
            SalesFeatureHealthState.Live => new(
                SalesHealthVisualMode.Live,
                SalesStatusIconKind.LiveDot,
                string.Empty,
                strings.LiveAccessibleName),
            SalesFeatureHealthState.Connecting => new(
                SalesHealthVisualMode.Connecting,
                SalesStatusIconKind.Spinner,
                strings.Connecting,
                strings.Connecting),
            SalesFeatureHealthState.Resyncing => new(
                SalesHealthVisualMode.Resyncing,
                SalesStatusIconKind.Spinner,
                strings.Resyncing,
                strings.Resyncing),
            SalesFeatureHealthState.Paused => CreateStatus(
                SalesHealthVisualMode.Paused,
                SalesStatusIconKind.Warning,
                Format(strings.OpenSalesChannelFormat, channel)),
            SalesFeatureHealthState.Degraded => CreateStatus(
                SalesHealthVisualMode.Degraded,
                SalesStatusIconKind.Warning,
                strings.Degraded),
            SalesFeatureHealthState.Disconnected => CreateStatus(
                SalesHealthVisualMode.Disconnected,
                SalesStatusIconKind.Error,
                strings.Disconnected),
            _ => CreateStatus(
                SalesHealthVisualMode.Error,
                SalesStatusIconKind.Error,
                strings.SensorError),
        };

        static HealthVisual CreateStatus(
            SalesHealthVisualMode mode,
            SalesStatusIconKind icon,
            string text) => new(mode, icon, text, text);
    }

    private static string Format(string format, object value) =>
        string.Format(CultureInfo.CurrentUICulture, format, value);

    private sealed record FieldValues(
        string Current,
        string Waiting,
        string Product,
        string Next,
        SalesQueueVisibleFields Requested);

    private sealed record HealthVisual(
        SalesHealthVisualMode Mode,
        SalesStatusIconKind Icon,
        string StatusText,
        string AccessibleStatus);
}
