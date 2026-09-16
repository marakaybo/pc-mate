using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using PcMate.Agent.Tray.Session;
using PcMate.Core.Models;
using PcMate.Core.Protocol;
using PcMate.Core.Util;

namespace PcMate.Agent.Tray.Forms;

/// <summary>
/// Настройки агента и список сопряжённых телефонов.
/// Опасные переключатели (произвольные команды) доступны только здесь, на самом ПК.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SettingsForm : Form
{
    private readonly AgentLink _link;

    private readonly NumericUpDown _countdown = new();
    private readonly TextBox _relay = new();
    private readonly NumericUpDown _lanPort = new();
    private readonly CheckBox _lanEnabled = new();
    private readonly CheckBox _allowShell = new();
    private readonly CheckBox _wakeOnBattery = new();
    private readonly ListView _phones = new();
    private readonly Label _status = new();

    private AgentSettings _settings = new();

    public SettingsForm(AgentLink link)
    {
        _link = link;
        UiTheme.ApplyForm(this);

        Text = "PC MATE — настройки";
        ClientSize = new Size(620, 600);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var header = UiTheme.Heading("Настройки агента");
        header.Location = new Point(20, 18);

        var y = 60;

        Controls.Add(Field("Обратный отсчёт перед выключением, секунд", y));
        _countdown.Location = new Point(20, y + 22);
        _countdown.Size = new Size(120, 26);
        _countdown.Minimum = 0;
        _countdown.Maximum = 600;
        _countdown.BackColor = UiTheme.Surface;
        _countdown.ForeColor = UiTheme.Text;
        _countdown.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_countdown);
        y += 62;

        Controls.Add(Field("Адрес сервера-ретранслятора (пусто — только локальная сеть)", y));
        _relay.Location = new Point(20, y + 22);
        _relay.Size = new Size(560, 26);
        _relay.BackColor = UiTheme.Surface;
        _relay.ForeColor = UiTheme.Text;
        _relay.BorderStyle = BorderStyle.FixedSingle;
        _relay.PlaceholderText = "wss://relay.example.com";
        Controls.Add(_relay);
        y += 62;

        Controls.Add(Field("Порт локального сервера", y));
        _lanPort.Location = new Point(20, y + 22);
        _lanPort.Size = new Size(120, 26);
        _lanPort.Minimum = 1024;
        _lanPort.Maximum = 65535;
        _lanPort.BackColor = UiTheme.Surface;
        _lanPort.ForeColor = UiTheme.Text;
        _lanPort.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_lanPort);

        _lanEnabled.Text = "Разрешить управление из домашней сети";
        _lanEnabled.Location = new Point(160, y + 24);
        _lanEnabled.AutoSize = true;
        _lanEnabled.ForeColor = UiTheme.Text;
        Controls.Add(_lanEnabled);
        y += 62;

        _wakeOnBattery.Text = "Разрешить таймеры пробуждения при работе от батареи (для ноутбуков)";
        _wakeOnBattery.Location = new Point(20, y);
        _wakeOnBattery.AutoSize = true;
        _wakeOnBattery.ForeColor = UiTheme.Text;
        Controls.Add(_wakeOnBattery);
        y += 30;

        _allowShell.Text = "Разрешить шаг «Выполнить команду» (cmd / PowerShell)";
        _allowShell.Location = new Point(20, y);
        _allowShell.AutoSize = true;
        _allowShell.ForeColor = UiTheme.Warn;
        Controls.Add(_allowShell);
        y += 22;

        var shellHint = UiTheme.Muted(
            "Включайте только если понимаете риск: сценарий сможет выполнить на компьютере любую команду.");
        shellHint.Location = new Point(40, y);
        shellHint.MaximumSize = new Size(540, 0);
        Controls.Add(shellHint);
        y += 46;

        var phonesLabel = UiTheme.Heading("Сопряжённые телефоны");
        phonesLabel.Font = UiTheme.BodyBold;
        phonesLabel.Location = new Point(20, y);
        Controls.Add(phonesLabel);
        y += 26;

        _phones.Location = new Point(20, y);
        _phones.Size = new Size(560, 140);
        _phones.View = View.Details;
        _phones.FullRowSelect = true;
        _phones.BackColor = UiTheme.Surface;
        _phones.ForeColor = UiTheme.Text;
        _phones.BorderStyle = BorderStyle.FixedSingle;
        _phones.Columns.Add("Телефон", 200);
        _phones.Columns.Add("Отпечаток", 220);
        _phones.Columns.Add("Сопряжён", 120);
        Controls.Add(_phones);
        y += 150;

        var revoke = UiTheme.DangerButton("Отозвать доступ");
        revoke.Location = new Point(20, y);
        revoke.Width = 170;
        revoke.Click += async (_, _) => await RevokeSelectedAsync();
        Controls.Add(revoke);

        var save = UiTheme.PrimaryButton("Сохранить");
        save.Location = new Point(360, y);
        save.Click += async (_, _) => await SaveAsync();
        Controls.Add(save);

        var close = UiTheme.SecondaryButton("Закрыть");
        close.Location = new Point(490, y);
        close.Width = 90;
        close.Click += (_, _) => Close();
        Controls.Add(close);

        _status.Location = new Point(20, y + 44);
        _status.Size = new Size(560, 20);
        _status.ForeColor = UiTheme.TextMuted;
        Controls.Add(_status);

        Controls.Add(header);
        Load += async (_, _) => await LoadAsync();
    }

    private static Label Field(string text, int y)
    {
        var label = new Label
        {
            Text = text,
            Font = UiTheme.BodyBold,
            ForeColor = UiTheme.Text,
            AutoSize = true,
            Location = new Point(20, y)
        };
        return label;
    }

    private async Task LoadAsync()
    {
        try
        {
            var result = await _link.RunCommandAsync(Commands.SettingsGet).ConfigureAwait(true);
            var node = result?["settings"];
            if (node is not null)
                _settings = PcMateJson.Deserialize<AgentSettings>(node.ToJsonString()) ?? new AgentSettings();

            _countdown.Value = Math.Clamp(_settings.CountdownSec, 0, 600);
            _relay.Text = _settings.RelayUrl;
            _lanPort.Value = Math.Clamp(_settings.LanPort, 1024, 65535);
            _lanEnabled.Checked = _settings.LanEnabled;
            _allowShell.Checked = _settings.AllowShellSteps;
            _wakeOnBattery.Checked = _settings.WakeTimersOnBattery;

            await LoadPhonesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _status.Text = "Не удалось загрузить настройки: " + ex.Message;
            _status.ForeColor = UiTheme.Fail;
        }
    }

    private async Task LoadPhonesAsync()
    {
        _phones.Items.Clear();
        var result = await _link.RunCommandAsync(Commands.PairList).ConfigureAwait(true);
        var node = result?["phones"];
        if (node is null) return;

        var phones = PcMateJson.Deserialize<List<PairedPhone>>(node.ToJsonString()) ?? new List<PairedPhone>();
        foreach (var phone in phones)
        {
            var item = new ListViewItem(phone.PhoneName) { Tag = phone.PhoneId };
            item.SubItems.Add(phone.Fingerprint);
            item.SubItems.Add(DateTimeOffset.TryParse(phone.PairedAt, out var at)
                ? at.LocalDateTime.ToString("dd.MM.yyyy")
                : "—");
            _phones.Items.Add(item);
        }

        if (phones.Count == 0)
            _status.Text = "Ни один телефон ещё не подключён. Нажмите «Подключить телефон» в меню трея.";
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_allowShell.Checked && !_settings.AllowShellSteps)
            {
                var confirm = MessageBox.Show(this,
                    "Сценарии смогут выполнять любые команды cmd и PowerShell на этом компьютере.\n\n" +
                    "Включать только если вы сами составляете сценарии. Продолжить?",
                    "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (confirm != DialogResult.Yes)
                {
                    _allowShell.Checked = false;
                    return;
                }
            }

            _settings.CountdownSec = (int)_countdown.Value;
            _settings.RelayUrl = _relay.Text.Trim();
            _settings.LanPort = (int)_lanPort.Value;
            _settings.LanEnabled = _lanEnabled.Checked;
            _settings.AllowShellSteps = _allowShell.Checked;
            _settings.WakeTimersOnBattery = _wakeOnBattery.Checked;

            await _link.RunCommandAsync(Commands.SettingsSet, new { settings = _settings }).ConfigureAwait(true);

            _status.ForeColor = UiTheme.Ok;
            _status.Text = "Настройки сохранены. Изменение адреса сервера применится в течение минуты.";
        }
        catch (Exception ex)
        {
            _status.ForeColor = UiTheme.Fail;
            _status.Text = "Не удалось сохранить: " + ex.Message;
        }
    }

    private async Task RevokeSelectedAsync()
    {
        if (_phones.SelectedItems.Count == 0)
        {
            _status.Text = "Выберите телефон в списке.";
            return;
        }

        var phoneId = _phones.SelectedItems[0].Tag?.ToString();
        var name = _phones.SelectedItems[0].Text;
        if (string.IsNullOrEmpty(phoneId)) return;

        if (MessageBox.Show(this, $"Отозвать доступ телефона «{name}»?\nОн больше не сможет управлять компьютером.",
                "Отзыв доступа", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        try
        {
            await _link.RunCommandAsync(Commands.PairRevoke, new { phoneId }).ConfigureAwait(true);
            await LoadPhonesAsync().ConfigureAwait(true);
            _status.ForeColor = UiTheme.Ok;
            _status.Text = $"Доступ телефона «{name}» отозван.";
        }
        catch (Exception ex)
        {
            _status.ForeColor = UiTheme.Fail;
            _status.Text = "Не удалось отозвать доступ: " + ex.Message;
        }
    }
}
