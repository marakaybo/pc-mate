using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using PcMate.Agent.Service.Diagnostics;
using PcMate.Agent.Service.Ipc;
using PcMate.Agent.Service.Pairing;
using PcMate.Agent.Service.Power;
using PcMate.Agent.Service.Readiness;
using PcMate.Agent.Service.Scenarios;
using PcMate.Agent.Service.Services;
using PcMate.Agent.Service.State;
using PcMate.Core.Util;

namespace PcMate.Agent.Service;

public static class Program
{
    public const string ServiceName = "PcMateAgent";

    public static async Task<int> Main(string[] args)
    {
        // Консоль Windows по умолчанию в кодировке OEM — кириллица в журнале
        // превращалась бы в мусор при запуске службы вручную для отладки.
        if (!WindowsServiceHelpers.IsWindowsService())
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
            catch (IOException) { /* вывод перенаправлен — не критично */ }
        }

        // Режим «сработал таймер пробуждения»: задача Планировщика запускает нас
        // коротким процессом, мы оставляем файл-триггер и выходим. Службу это будит
        // к работе даже если она стартует позже нас.
        if (args.Length > 0 && args[0] is "--fired" or "-f")
            return WriteWakeTrigger(args);

        if (args.Contains("--version"))
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
            return 0;
        }

        // Диагностика без установки службы: показать, готов ли компьютер.
        if (args.Contains("--check") || args.Contains("--wizard"))
            return await RunReadinessAsync(args).ConfigureAwait(false);

        using var host = BuildHost(args);
        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    public static IHost BuildHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);

        // --- Журналирование ---
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        var dataDir = Environment.GetEnvironmentVariable("PCMATE_DATA_DIR")
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcMate");
        builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(dataDir, "logs")));
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        if (WindowsServiceHelpers.IsWindowsService())
            builder.Logging.AddEventLog(settings => settings.SourceName = "PC MATE");

        // --- Состояние и инфраструктура ---
        builder.Services.AddSingleton<AgentStore>();
        builder.Services.AddSingleton<AgentEventBus>();
        builder.Services.AddSingleton<RelayStatus>();
        builder.Services.AddSingleton<LocalCommandGateway>();

        // --- Домен ---
        builder.Services.AddSingleton<PairingService>();
        builder.Services.AddSingleton<PowerController>();
        builder.Services.AddSingleton<WakeTimerService>();
        builder.Services.AddSingleton<ReadinessChecker>();
        builder.Services.AddSingleton<ScenarioEngine>();
        builder.Services.AddSingleton<StatusProvider>();
        builder.Services.AddSingleton<WakeTestService>();
        builder.Services.AddSingleton<CommandDispatcher>();
        builder.Services.AddSingleton<EnvelopeProcessor>();

        // --- Фоновые службы ---
        builder.Services.AddSingleton<SessionHost>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionHost>());

        builder.Services.AddSingleton<ScheduleService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ScheduleService>());

        builder.Services.AddSingleton<LanServer>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LanServer>());

        builder.Services.AddSingleton<RelayClient>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RelayClient>());

        builder.Services.AddSingleton<PowerEventWatcher>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<PowerEventWatcher>());

        builder.Services.AddHostedService<StartupReporter>();

        return builder.Build();
    }

    /// <summary>
    /// Режим «--check»: прогоняет мастер готовности и печатает результат в консоль.
    /// Нужен для поддержки: пользователь может прислать вывод одной командой.
    /// </summary>
    private static async Task<int> RunReadinessAsync(string[] args)
    {
        using var host = BuildHost(args);
        var checker = host.Services.GetRequiredService<ReadinessChecker>();
        var fix = args.Contains("--fix");

        Console.WriteLine("PC MATE — проверка готовности компьютера");
        Console.WriteLine(new string('─', 72));

        var checks = await checker.RunAsync().ConfigureAwait(false);

        if (fix)
        {
            foreach (var broken in checks.Where(c => c.CanFix && c.Status is "fail" or "warn").ToList())
            {
                Console.WriteLine($"  … исправляем «{broken.Title}»");
                await checker.FixAsync(broken.Id).ConfigureAwait(false);
            }
            checks = await checker.RunAsync().ConfigureAwait(false);
        }

        foreach (var check in checks)
        {
            var glyph = check.Status switch
            {
                "ok" => "[ OK ]",
                "warn" => "[ !  ]",
                "fail" => "[ X  ]",
                _ => "[ ?  ]"
            };

            Console.WriteLine($"{glyph} {check.Title}");
            Console.WriteLine($"       {check.Detail}");
            if (check.CanFix && check.Status != "ok")
                Console.WriteLine($"       → можно исправить автоматически: {check.FixHint}");
            Console.WriteLine();
        }

        var summary = checker.Summarize(checks);
        Console.WriteLine(new string('─', 72));
        Console.WriteLine($"Итог: {summary.Ok} в порядке, {summary.Warn} предупреждений, {summary.Fail} проблем.");
        Console.WriteLine(summary.WakeReady
            ? "Компьютер готов просыпаться по расписанию."
            : "Компьютер пока НЕ готов к удалённому включению.");

        if (!fix && checks.Any(c => c.CanFix && c.Status != "ok"))
            Console.WriteLine("Запустите с ключом --fix от имени администратора, чтобы починить автоматически.");

        return summary.Fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// Задача Планировщика вызывает агента с --fired &lt;id&gt;. Мы кладём файл-триггер,
    /// который подхватит служба: это надёжнее, чем пытаться достучаться до неё сразу
    /// после пробуждения, когда сеть и службы ещё поднимаются.
    /// </summary>
    private static int WriteWakeTrigger(string[] args)
    {
        try
        {
            var id = args.Length > 1 ? args[1] : "unknown";
            string? scenarioId = null;

            for (var i = 2; i < args.Length - 1; i++)
                if (args[i] is "--scenario" or "-s")
                    scenarioId = string.IsNullOrWhiteSpace(args[i + 1]) ? null : args[i + 1];

            var dataDir = Environment.GetEnvironmentVariable("PCMATE_DATA_DIR")
                          ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcMate");
            var triggersDir = Path.Combine(dataDir, "triggers");
            Directory.CreateDirectory(triggersDir);

            var trigger = new WakeTrigger
            {
                Id = id,
                ScenarioId = scenarioId,
                At = TimeUtil.IsoNow()
            };

            var path = Path.Combine(triggersDir, $"{id}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, PcMateJson.SerializePretty(trigger));
            Console.WriteLine($"PC MATE: зафиксировано срабатывание расписания {id}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("PC MATE: не удалось записать файл-триггер: " + ex.Message);
            return 1;
        }
    }
}

/// <summary>Пишет в журнал сводку о состоянии агента при старте — помогает в поддержке.</summary>
public sealed class StartupReporter : BackgroundService
{
    private readonly ILogger<StartupReporter> _log;
    private readonly AgentStore _store;
    private readonly ReadinessChecker _readiness;
    private readonly PairingService _pairing;

    public StartupReporter(ILogger<StartupReporter> log, AgentStore store, ReadinessChecker readiness,
        PairingService pairing)
    {
        _log = log;
        _store = store;
        _readiness = readiness;
        _pairing = pairing;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("PC MATE агент запущен. Компьютер: {PcName}, идентификатор: {DeviceId}",
            _store.Identity.PcName, _store.Identity.DeviceId);
        _log.LogInformation("Сопряжённых телефонов: {Count}. Данные: {Path}",
            _pairing.Phones.Count, _store.RootPath);
        _store.AppendEvent("agent.start", "Агент запущен");

        // Windows после обновлений сбрасывает настройки питания — проверяем при каждом старте (ТЗ §10).
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            var checks = await _readiness.RunAsync(ct: stoppingToken).ConfigureAwait(false);
            var summary = _readiness.Summarize(checks);

            _log.LogInformation("Готовность: {Ok} ок, {Warn} предупреждений, {Fail} проблем. К пробуждению {Ready}",
                summary.Ok, summary.Warn, summary.Fail, summary.WakeReady ? "готов" : "не готов");

            foreach (var check in checks.Where(c => c.Status is "fail"))
                _log.LogWarning("Проверка «{Title}»: {Detail}", check.Title, check.Detail);
        }
        catch (OperationCanceledException)
        {
            // Останов службы.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Не удалось выполнить проверку готовности при старте");
        }
    }
}
