using System.Diagnostics;
using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using PcMate.Agent.Tray.Forms;
using PcMate.Agent.Tray.Session;
using PcMate.Core.Ipc;
using PcMate.Core.Models;
using PcMate.Core.Protocol;
using PcMate.Core.Util;

namespace PcMate.Agent.Tray;

/// <summary>
/// Значок в области уведомлений: быстрые действия, окна мастера и сопряжения,
/// окно обратного отсчёта. Всё тяжёлое делает служба — помощник только показывает.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly AgentLink _link = new();
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _scenariosItem;
    private readonly ToolStripMenuItem _cancelItem;

    private CountdownForm? _countdown;
    private WizardForm? _wizard;
    private SettingsForm? _settings;
    private PairingForm? _pairing;

    public TrayApplicationContext(bool showPairingOnStart, bool showWizardOnStart = false)
    {
        _statusItem = new ToolStripMenuItem("Подключение к службе…") { Enabled = false };
        _scenariosItem = new ToolStripMenuItem("Сценарии");
        _cancelItem = new ToolStripMenuItem("Отменить действие питания", null, async (_, _) => await CancelPowerAsync())
        {
            Visible = false
        };

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "PC MATE",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _icon.DoubleClick += (_, _) => OpenWizard();
        _icon.BalloonTipClicked += (_, _) => OpenWizard();

        _link.ConnectionChanged += OnConnectionChanged;
        _link.CountdownRequested += OnCountdownRequested;
        _link.CountdownDismissed += OnCountdownDismissed;
        _link.NotifyRequested += OnNotifyRequested;
        _link.Start();

        // Раз в сутки спрашиваем GitHub про новую версию: магазина, который
        // обновил бы программу сам, здесь нет.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
            await CheckUpdatesAsync(force: false).ConfigureAwait(false);
        });

        if (showPairingOnStart || showWizardOnStart)
        {
            // После установки сразу показываем QR — путь «от установки до первого включения» короче.
            var timer = new System.Windows.Forms.Timer { Interval = 2500 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                if (showWizardOnStart) OpenWizard();
                if (showPairingOnStart) OpenPairing();
            };
            timer.Start();
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = UiTheme.Surface,
            ForeColor = UiTheme.Text,
            Font = UiTheme.Body,
            RenderMode = ToolStripRenderMode.System
        };

        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Подключить телефон…", null, (_, _) => OpenPairing()));
        menu.Items.Add(new ToolStripMenuItem("Проверка готовности…", null, (_, _) => OpenWizard()));
        menu.Items.Add(_scenariosItem);

        var power = new ToolStripMenuItem("Питание");
        power.DropDownItems.Add(new ToolStripMenuItem("Сон", null, async (_, _) => await PowerAsync(Commands.PowerSleep)));
        power.DropDownItems.Add(new ToolStripMenuItem("Гибернация", null, async (_, _) => await PowerAsync(Commands.PowerHibernate)));
        power.DropDownItems.Add(new ToolStripMenuItem("Заблокировать", null, async (_, _) => await PowerAsync(Commands.PowerLock)));
        power.DropDownItems.Add(new ToolStripSeparator());
        power.DropDownItems.Add(new ToolStripMenuItem("Выключить", null, async (_, _) => await PowerAsync(Commands.PowerShutdown)));
        power.DropDownItems.Add(new ToolStripMenuItem("Перезагрузить", null, async (_, _) => await PowerAsync(Commands.PowerReboot)));
        menu.Items.Add(power);

        menu.Items.Add(_cancelItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Настройки…", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("Папка с журналами", null, (_, _) => OpenLogs()));
        menu.Items.Add(new ToolStripMenuItem("Проверить обновления", null, async (_, _) => await CheckUpdatesAsync(true)));
        menu.Items.Add(new ToolStripMenuItem("О программе", null, (_, _) => ShowAbout()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => ExitTray()));

        menu.Opening += async (_, _) => await RefreshScenariosAsync();
        return menu;
    }

    private static Icon LoadIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "pcmate.ico");
            if (File.Exists(path)) return new Icon(path);
        }
        catch
        {
            // Падать из-за иконки нельзя.
        }
        return SystemIcons.Application;
    }

    // ---------- Реакция на события службы ----------

    private void OnConnectionChanged(bool connected)
    {
        RunOnUi(() =>
        {
            _statusItem.Text = connected
                ? $"Служба PC MATE работает · {Environment.MachineName}"
                : "Нет связи со службой PC MATE";
            _icon.Text = connected ? "PC MATE — подключено" : "PC MATE — нет связи со службой";
        });
    }

    private void OnCountdownRequested(CountdownRequest request)
    {
        RunOnUi(() =>
        {
            _countdown?.DismissFromAgent();
            _countdown = new CountdownForm(request);
            _countdown.Cancelled += () => _ = _link.ReportCountdownCancelledAsync();
            _countdown.FormClosed += (_, _) =>
            {
                _countdown = null;
                _cancelItem.Visible = false;
            };
            _cancelItem.Visible = true;
            _countdown.Show();
            _countdown.BringToFront();
        });
    }

    private void OnCountdownDismissed() => RunOnUi(() =>
    {
        _countdown?.DismissFromAgent();
        _cancelItem.Visible = false;
    });

    private void OnNotifyRequested(NotifyRequest request) => RunOnUi(() =>
    {
        var icon = request.Level switch
        {
            "error" => ToolTipIcon.Error,
            "warn" => ToolTipIcon.Warning,
            _ => ToolTipIcon.Info
        };
        _icon.ShowBalloonTip(6000, request.Title, request.Text, icon);
    });

    // ---------- Действия меню ----------

    private async Task PowerAsync(string command)
    {
        try
        {
            await _link.RunCommandAsync(command).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async Task CancelPowerAsync()
    {
        try
        {
            await _link.RunCommandAsync(Commands.PowerCancel).ConfigureAwait(true);
            RunOnUi(() => _cancelItem.Visible = false);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async Task RefreshScenariosAsync()
    {
        try
        {
            var result = await _link.RunCommandAsync(Commands.ScenarioList, null, TimeSpan.FromSeconds(10))
                .ConfigureAwait(true);
            var node = result?["scenarios"];
            var scenarios = node is null
                ? new List<Scenario>()
                : PcMateJson.Deserialize<List<Scenario>>(node.ToJsonString()) ?? new List<Scenario>();

            RunOnUi(() =>
            {
                _scenariosItem.DropDownItems.Clear();

                if (scenarios.Count == 0)
                {
                    _scenariosItem.DropDownItems.Add(new ToolStripMenuItem("Сценариев пока нет") { Enabled = false });
                    _scenariosItem.DropDownItems.Add(new ToolStripMenuItem("Создайте их в приложении на телефоне")
                    {
                        Enabled = false
                    });
                    return;
                }

                foreach (var scenario in scenarios.OrderByDescending(s => s.Favorite).ThenBy(s => s.Name))
                {
                    var title = string.IsNullOrWhiteSpace(scenario.Icon)
                        ? scenario.Name
                        : $"{scenario.Icon}  {scenario.Name}";

                    _scenariosItem.DropDownItems.Add(new ToolStripMenuItem(title, null, async (_, _) =>
                    {
                        try
                        {
                            await _link.RunCommandAsync(Commands.ScenarioRun, new { id = scenario.Id })
                                .ConfigureAwait(true);
                            _icon.ShowBalloonTip(4000, "PC MATE", $"Запущен сценарий «{scenario.Name}»", ToolTipIcon.Info);
                        }
                        catch (Exception ex)
                        {
                            ShowError(ex.Message);
                        }
                    }));
                }
            });
        }
        catch
        {
            RunOnUi(() =>
            {
                _scenariosItem.DropDownItems.Clear();
                _scenariosItem.DropDownItems.Add(new ToolStripMenuItem("Служба недоступна") { Enabled = false });
            });
        }
    }

    private void OpenPairing()
    {
        if (_pairing is { IsDisposed: false })
        {
            _pairing.BringToFront();
            return;
        }
        _pairing = new PairingForm(_link);
        _pairing.FormClosed += (_, _) => _pairing = null;
        _pairing.Show();
    }

    private void OpenWizard()
    {
        if (_wizard is { IsDisposed: false })
        {
            _wizard.BringToFront();
            return;
        }
        _wizard = new WizardForm(_link);
        _wizard.FormClosed += (_, _) => _wizard = null;
        _wizard.Show();
    }

    private void OpenSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            _settings.BringToFront();
            return;
        }
        _settings = new SettingsForm(_link);
        _settings.FormClosed += (_, _) => _settings = null;
        _settings.Show();
    }

    private static void OpenLogs()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcMate", "logs");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true })?.Dispose();
    }

    private async Task CheckUpdatesAsync(bool force)
    {
        UpdateChecker.UpdateInfo? update;
        try
        {
            update = await UpdateChecker.CheckAsync(force).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (force) ShowError("Не удалось проверить обновления: " + ex.Message);
            return;
        }

        if (update is null)
        {
            if (force)
                RunOnUi(() => MessageBox.Show(
                    $"Установлена последняя версия ({UpdateChecker.CurrentVersion}).",
                    "PC MATE", MessageBoxButtons.OK, MessageBoxIcon.Information));
            return;
        }

        RunOnUi(() =>
        {
            var text = $"Установлена {update.CurrentVersion}, доступна {update.Version}.\n\n" +
                       (string.IsNullOrWhiteSpace(update.Notes) ? "" : update.Notes + "\n\n") +
                       "Скачать и установить обновление?\n" +
                       "Настройки, сценарии и сопряжения сохранятся.";

            var answer = MessageBox.Show(text, "PC MATE — вышло обновление",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);

            switch (answer)
            {
                case DialogResult.Yes:
                    OpenUrl(update.SetupUrl ?? update.ReleaseUrl);
                    break;
                case DialogResult.No:
                    UpdateChecker.SkipVersion(update.Version);
                    break;
            }
        });
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })?.Dispose();
        }
        catch
        {
            // Браузера может не быть — тогда пользователь откроет ссылку сам.
        }
    }

    private void ShowAbout()
    {
        var version = typeof(TrayApplicationContext).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        MessageBox.Show(
            $"PC MATE {version}\n\n" +
            "Удалённое управление компьютером с телефона:\n" +
            "включение, выключение, сон, сценарии и расписания.\n\n" +
            $"Компьютер: {Environment.MachineName}\n" +
            $"Пользователь: {Environment.UserName}",
            "О программе PC MATE", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowError(string message) => RunOnUi(() =>
        _icon.ShowBalloonTip(6000, "PC MATE", message, ToolTipIcon.Warning));

    private void ExitTray()
    {
        if (MessageBox.Show(
                "Закрыть помощник PC MATE?\n\n" +
                "Служба продолжит работать, но сценарии не смогут запускать программы,\n" +
                "а окно обратного отсчёта не появится.",
                "PC MATE", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _icon.Visible = false;
        _ = _link.DisposeAsync();
        ExitThread();
    }

    private void RunOnUi(Action action)
    {
        if (_icon.ContextMenuStrip is { } menu && menu.InvokeRequired)
            menu.BeginInvoke(action);
        else
            action();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
