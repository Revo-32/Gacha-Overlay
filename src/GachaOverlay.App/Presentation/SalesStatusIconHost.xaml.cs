using System.Windows.Media.Animation;

namespace GachaOverlay.App.Presentation;

public partial class SalesStatusIconHost : System.Windows.Controls.UserControl
{
    public SalesStatusIconHost() => InitializeComponent();

    public void SetSpinnerActive(bool active)
    {
        if (!active)
        {
            SpinnerRotation.BeginAnimation(
                System.Windows.Media.RotateTransform.AngleProperty,
                null);
            SpinnerRotation.Angle = 0;
            return;
        }

        if (SpinnerRotation.HasAnimatedProperties)
        {
            return;
        }

        SpinnerRotation.BeginAnimation(
            System.Windows.Media.RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            },
            HandoffBehavior.SnapshotAndReplace);
    }
}
