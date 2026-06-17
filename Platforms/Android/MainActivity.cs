using Android.App;
using Android.Content.PM;
using Android.OS;

namespace SnapStakMobile;

[Activity(
    Theme = "@style/MainTheme",
    MainLauncher = true,
    // AdjustNothing prevents Android from resizing or panning the window
    // when the keyboard appears — stops our bottom action bar disappearing.
    WindowSoftInputMode = Android.Views.SoftInput.AdjustNothing |
                          Android.Views.SoftInput.StateHidden,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation |
                           ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize)]
public class MainActivity : MauiAppCompatActivity { }