namespace PoSeeReview.Mobile.Services;

/// <summary>
/// The web app's three haptic patterns (tap / confirm / error), on the phone's own engine. On by
/// default with one switch in Settings, like the web. A device without the hardware is a no-op.
/// </summary>
public sealed class Haptics
{
    private const string EnabledKey = "poseereview_haptics";
    private readonly IPreferences _preferences = Preferences.Default;

    public bool Enabled
    {
        get => _preferences.Get(EnabledKey, true);
        set => _preferences.Set(EnabledKey, value);
    }

    /// <summary>A pipeline step, a pin, a button.</summary>
    public void Tap() => Perform(HapticFeedbackType.Click);

    /// <summary>The comic is revealed.</summary>
    public void Confirm() => Perform(HapticFeedbackType.LongPress);

    /// <summary>A failure or a "too normal" verdict.</summary>
    public void Error()
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            if (Vibration.Default.IsSupported)
            {
                Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(250));
            }
        }
        catch (FeatureNotSupportedException)
        {
        }
    }

    private void Perform(HapticFeedbackType type)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            if (HapticFeedback.Default.IsSupported)
            {
                HapticFeedback.Default.Perform(type);
            }
        }
        catch (FeatureNotSupportedException)
        {
        }
    }
}
