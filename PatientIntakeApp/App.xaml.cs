using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PatientIntakeApp.Data;
using PatientIntakeApp.Services;
using PatientIntakeApp.Services.ExternalChecks;
using PatientIntakeApp.Services.Stores;
using PatientIntakeApp.ViewModels;
using PatientIntakeApp.Views;
using System;
using System.IO;

namespace PatientIntakeApp;

public partial class App : Application
{
    public new static App Current => (App)Application.Current;
    public IServiceProvider Services { get; }

    public App()
    {
        // Global exception handling
        this.DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        try
        {
            Services = ConfigureServices();
        }
        catch (Exception ex)
        {
            // If we can't even configure services, we cannot show an in-app dialog yet.
            MessageBox.Show($"Error configuring services: {ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Services
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddDbContextFactory<PatientIntakeDbContext>((sp, options) =>
        {
            var config = sp.GetRequiredService<IConfigurationService>();
            var cs = config.GetDbConnectionString();

            // Local dev default: SQLite file-based DB (no SQL Server install required).
            // Shared mode: set PATIENTINTAKE_DB_CONNECTION_STRING to a SQL Server connection string.
            if (string.IsNullOrWhiteSpace(cs))
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var dir = Path.Combine(appData, "PatientIntakeApp");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "patientintake.dev.sqlite");
                cs = $"Data Source={path}";
                options.UseSqlite(cs);
                return;
            }

            // If the string looks like a SQL Server connection string, use SQL Server.
            // Otherwise assume SQLite (lets you point at a .db file directly).
            var looksLikeSqlServer = cs.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
                                     cs.Contains("Data Source=.", StringComparison.OrdinalIgnoreCase) ||
                                     cs.Contains("Data Source=(", StringComparison.OrdinalIgnoreCase) ||
                                     cs.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase);

            if (looksLikeSqlServer)
            {
                if (!cs.Contains("Connect Timeout=", StringComparison.OrdinalIgnoreCase) &&
                    !cs.Contains("Connection Timeout=", StringComparison.OrdinalIgnoreCase))
                {
                    cs = cs.Trim().TrimEnd(';') + ";Connect Timeout=5";
                }
                options.UseSqlServer(cs);
            }
            else
            {
                options.UseSqlite(cs);
            }
        });
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IUserStore, UserStore>();
        services.AddSingleton<IPresenceStore, PresenceStore>();
        services.AddSingleton<IReferralEventStore, ReferralEventStore>();
        services.AddSingleton<IFacilityStore, FacilityStore>();
        services.AddSingleton<IReferralStore, ReferralStore>();
        services.AddSingleton<IReviewStore, ReviewStore>();
        services.AddSingleton<IRuleStore, RuleStore>();
        services.AddSingleton<IExternalCheckProvider, StubFinancialCheckProvider>();
        services.AddSingleton<IExternalCheckProvider, StubLitigationCheckProvider>();
        services.AddSingleton<IExternalCheckProvider, StubCriminalCheckProvider>();
        services.AddSingleton<IExternalCheckService, ExternalCheckService>();
        services.AddSingleton<IPdfProcessingService, PdfProcessingService>();
        services.AddSingleton<IAnalysisService, AnalysisService>();
        services.AddSingleton<IThemeService, ThemeService>();

        // ViewModels
        // IMPORTANT: MainViewModel is scoped so each window can have its own navigation state.
        services.AddScoped<MainViewModel>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<IngestionViewModel>();
        services.AddTransient<ProcessingViewModel>();
        services.AddTransient<ReviewViewModel>();
        services.AddTransient<FinalReportViewModel>();
        services.AddTransient<AllClearViewModel>();

        // Windows/Views
        services.AddTransient<MainWindow>();

        return services.BuildServiceProvider();
    }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            // Bring up DB before rendering UI so we can reliably show shared state.
            // Timebox startup so a missing SQL Server doesn't look like "nothing happened".
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
            var dbInit = Services.GetRequiredService<IDatabaseInitializer>();
            // IMPORTANT: Avoid deadlocking the WPF UI thread by blocking on async work that captures the sync context.
            // Run DB init on a background thread.
            System.Threading.Tasks.Task.Run(() => dbInit.InitializeAsync(cts.Token)).GetAwaiter().GetResult();

            var scope = Services.CreateScope();
            var mainWindow = scope.ServiceProvider.GetRequiredService<MainWindow>();
            var mainViewModel = scope.ServiceProvider.GetRequiredService<MainViewModel>();
            
            // Break circular dependency initialization
            mainViewModel.Initialize();
            
            mainWindow.DataContext = mainViewModel;
            // dispose scope when window closes
            mainWindow.Closed += (_, __) => scope.Dispose();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(
                    "startup_error.txt",
                    $"{DateTime.Now:u} {ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }

            // If the main window is available, show a polished in-app dialog; otherwise fallback.
            if (Current?.MainWindow?.DataContext is MainViewModel vm)
            {
                _ = vm.ShowInfoAsync("Startup Error", $"Error showing main window:\n\n{ex}", iconKind: "AlertCircleOutline");
            }
            else
            {
                MessageBox.Show($"Error showing main window:\n\n{ex}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        if (Current?.MainWindow?.DataContext is MainViewModel vm)
        {
            _ = vm.ShowInfoAsync("Error", $"An unhandled exception occurred: {e.Exception.Message}", iconKind: "AlertCircleOutline");
        }
        else
        {
            MessageBox.Show($"An unhandled exception occurred: {e.Exception.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        e.Handled = true;
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            if (Current?.MainWindow?.DataContext is MainViewModel vm)
            {
                _ = vm.ShowInfoAsync("Critical Error", $"Critical error: {ex.Message}", iconKind: "AlertOctagonOutline");
            }
            else
            {
                MessageBox.Show($"Critical error: {ex.Message}", "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
