using System.Windows;
using System.Windows.Threading;
using TraductorVozTeams.App.Services;
using TraductorVozTeams.Core.Settings;

namespace TraductorVozTeams.App;

public partial class App : Application
{
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var selfTestIndex = Array.FindIndex(e.Args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        if (selfTestIndex >= 0)
        {
            var reportPath = selfTestIndex + 1 < e.Args.Length ? e.Args[selfTestIndex + 1] : null;
            Shutdown(SelfTest.Run(reportPath));
            return;
        }

        var voicesIndex = Array.FindIndex(e.Args, a => a.Equals("--voices", StringComparison.OrdinalIgnoreCase));
        if (voicesIndex >= 0)
        {
            Shutdown(VoiceListing.Run(voicesIndex + 1 < e.Args.Length ? e.Args[voicesIndex + 1] : null));
            return;
        }

        var pairingIndex = Array.FindIndex(e.Args, a => a.Equals("--pairing-test", StringComparison.OrdinalIgnoreCase));
        if (pairingIndex >= 0)
        {
            Shutdown(PairingCommand.Run(pairingIndex + 1 < e.Args.Length && !e.Args[pairingIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? e.Args[pairingIndex + 1]
                : null));
            return;
        }

        var latencyIndex = Array.FindIndex(e.Args, a => a.Equals("--latency-test", StringComparison.OrdinalIgnoreCase));
        if (latencyIndex >= 0)
        {
            try
            {
                Shutdown(LatencyBenchmark.Run(LatencyBenchmark.Parse(e.Args, latencyIndex)));
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show(ex.Message, "Prueba de demora", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(2);
            }

            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        try
        {
            _services = new AppServices(AppPaths.Default());
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo cargar la configuración: " + ex.Message, "Traductor de voz para reuniones en Windows más Azure",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _services.Logger.Info($"Inicio. Ejecutable: {Environment.ProcessPath}; datos: {_services.Paths.Root}");

        if (!SystemHelpers.IsVcRedistInstalled())
        {
            var answer = MessageBox.Show(
                "No se detectó Microsoft Visual C++ Redistributable x64 (2015-2022), requerido por Azure AI Speech SDK.\n\n" +
                "¿Quieres abrir la descarga oficial de Microsoft?",
                "Requisito faltante", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                SystemHelpers.OpenUrl(SystemHelpers.VcRedistUrl);
            }
        }

        var window = new MainWindow(_services);
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Logger.Error("Excepción no controlada", e.Exception);
        MessageBox.Show("Error inesperado: " + e.Exception.Message, "Traductor de voz para reuniones en Windows más Azure",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
