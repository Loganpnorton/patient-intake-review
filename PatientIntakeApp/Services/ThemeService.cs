using MaterialDesignThemes.Wpf;

namespace PatientIntakeApp.Services;

public interface IThemeService
{
    void ApplyDarkMode(bool isDarkMode);
}

public class ThemeService : IThemeService
{
    public void ApplyDarkMode(bool isDarkMode)
    {
        var paletteHelper = new PaletteHelper();
        var theme = paletteHelper.GetTheme();

        // MaterialDesignThemes.Wpf supports switching base theme at runtime via PaletteHelper.
        theme.SetBaseTheme(isDarkMode ? BaseTheme.Dark : BaseTheme.Light);

        paletteHelper.SetTheme(theme);
    }
}


