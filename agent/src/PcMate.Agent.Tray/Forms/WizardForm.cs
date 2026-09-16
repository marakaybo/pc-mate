using System.Drawing;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using PcMate.Agent.Tray.Session;
using PcMate.Core.Models;
using PcMate.Core.Protocol;
using PcMate.Core.Util;

namespace PcMate.Agent.Tray.Forms;

/// <summary>
/// Мастер готовности: 12 проверок, у каждой — понятное объяснение и,
/// где это возможно, кнопка «Исправить».
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WizardForm : Form
{
    private readonly AgentLink _link;
    private readonly FlowLayoutPanel _list = new();
    private readonly Label _summary = new();
    private readonly Button _recheck;
    private readonly Button _testWol;
    private readonly Button _testTimer;

    public WizardForm(AgentLink link)
    {
        _link = link;
        UiTheme.ApplyForm(this);

        Text = "PC MATE — проверка готовности компьютера";
        ClientSize = new Size(820, 660);
        MinimumSize = new Size(700, 500);

        var header = UiTheme.Heading("Готовность к удалённому включению");
        header.Location = new Point(20, 16);

        _summary.Font = UiTheme.Body;
        _summary.ForeColor = UiTheme.TextMuted;
        _summary.AutoSize = false;
        _summary.Location = new Point(20, 46);
        _summary.Size = new Size(760, 24);
        _summary.Text = "Выполняем проверки…";

        _list.Location = new Point(12, 78);
        _list.Size = new Size(790, 500);
        _list.AutoScroll = true;
        _list.FlowDirection = FlowDirection.TopDown;
        _list.WrapContents = false;
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

        _recheck = UiTheme.PrimaryButton("Проверить снова");
        _recheck.Location = new Point(20, 596);
        _recheck.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _recheck.Click += async (_, _) => await RunChecksAsync();

        _testWol = UiTheme.SecondaryButton("Тест Wake-on-LAN");
        _testWol.Width = 180;
        _testWol.Location = new Point(160, 596);
        _testWol.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _testWol.Click += async (_, _) => await StartWakeTestAsync("wol");

        _testTimer = UiTheme.SecondaryButton("Тест таймера пробуждения");
        _testTimer.Width = 220;
        _testTimer.Location = new Point(350, 596);
        _testTimer.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _testTimer.Click += async (_, _) => await StartWakeTestAsync("timer");

        var close = UiTheme.SecondaryButton("Закрыть");
        close.Location = new Point(690, 596);
        close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        close.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { header, _summary, _list, _recheck, _testWol, _testTimer, close });
        Load += async (_, _) => await RunChecksAsync();
    }

    private async Task RunChecksAsync()
    {
        _recheck.Enabled = false;
        _summary.Text = "Выполняем проверки…";

        try
        {
            var result = await _link.RunCommandAsync(Commands.WizardRun, null, TimeSpan.FromMinutes(3))
                .ConfigureAwait(true);
            var checks = ParseChecks(result?["checks"]);
            Render(checks);
        }
        catch (Exception ex)
        {
            _summary.Text = "Не удалось выполнить проверки: " + ex.Message;
            _summary.ForeColor = UiTheme.Fail;
        }
        finally
        {
            _recheck.Enabled = true;
        }
    }

    private static List<CheckResult> ParseChecks(JsonNode? node)
    {
        if (node is null) return new List<CheckResult>();
        return PcMateJson.Deserialize<List<CheckResult>>(node.ToJsonString()) ?? new List<CheckResult>();
    }

    private void Render(List<CheckResult> checks)
    {
        _list.SuspendLayout();
        foreach (Control control in _list.Controls) control.Dispose();
        _list.Controls.Clear();

        foreach (var check in checks)
            _list.Controls.Add(BuildCard(check));

        _list.ResumeLayout();

        var ok = checks.Count(c => c.Status == CheckStatus.Ok);
        var warn = checks.Count(c => c.Status == CheckStatus.Warn);
        var fail = checks.Count(c => c.Status == CheckStatus.Fail);

        _summary.ForeColor = fail > 0 ? UiTheme.Fail : warn > 0 ? UiTheme.Warn : UiTheme.Ok;
        _summary.Text = fail > 0
            ? $"Готово: {ok}. Требуют внимания: {fail}. Предупреждений: {warn}."
            : warn > 0
                ? $"Всё основное настроено ({ok}), есть {warn} предупреждени{(warn == 1 ? "е" : "я")}."
                : $"Компьютер полностью готов: все {ok} проверок пройдены.";
    }

    private Panel BuildCard(CheckResult check)
    {
        var card = new Panel
        {
            Width = 750,
            BackColor = UiTheme.Surface,
            Margin = new Padding(6, 6, 6, 0),
            Padding = new Padding(14, 12, 14, 12),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };

        var glyph = new Label
        {
            Text = UiTheme.StatusGlyph(check.Status),
            ForeColor = UiTheme.StatusColor(check.Status),
            Font = new Font("Segoe UI Semibold", 15F),
            AutoSize = false,
            Size = new Size(34, 30),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(8, 10)
        };

        var title = new Label
        {
            Text = check.Title,
            Font = UiTheme.BodyBold,
            ForeColor = UiTheme.Text,
            AutoSize = false,
            Size = new Size(520, 20),
            Location = new Point(46, 12)
        };

        var detail = new Label
        {
            Text = check.Detail,
            Font = UiTheme.Body,
            ForeColor = UiTheme.TextMuted,
            AutoSize = false,
            Size = new Size(getDetailWidth(check), MeasureHeight(check.Detail, getDetailWidth(check))),
            Location = new Point(46, 34)
        };

        card.Controls.Add(glyph);
        card.Controls.Add(title);
        card.Controls.Add(detail);

        var bottom = detail.Bottom + 6;

        if (check.ManualSteps is { Count: > 0 })
        {
            var steps = new Label
            {
                Text = string.Join(Environment.NewLine,
                    check.ManualSteps.Select((s, i) => $"{i + 1}. {s}")),
                Font = UiTheme.Body,
                ForeColor = UiTheme.TextMuted,
                AutoSize = false,
                Size = new Size(650, check.ManualSteps.Count * 19 + 6),
                Location = new Point(46, bottom)
            };
            card.Controls.Add(steps);
            bottom = steps.Bottom + 4;
        }

        if (check.CanFix)
        {
            var fix = UiTheme.PrimaryButton("Исправить");
            fix.Width = 130;
            fix.Height = 30;
            fix.Location = new Point(600, 10);
            fix.Click += async (_, _) =>
            {
                fix.Enabled = false;
                fix.Text = "Исправляем…";
                try
                {
                    await _link.RunCommandAsync(Commands.WizardFix, new { id = check.Id }, TimeSpan.FromMinutes(2))
                        .ConfigureAwait(true);
                    await RunChecksAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Не удалось исправить",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    fix.Enabled = true;
                    fix.Text = "Исправить";
                }
            };
            card.Controls.Add(fix);
        }

        card.Height = Math.Max(bottom + 8, 60);
        return card;

        static int getDetailWidth(CheckResult c) => c.CanFix ? 540 : 680;
    }

    private static int MeasureHeight(string text, int width)
    {
        using var bitmap = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bitmap);
        var size = g.MeasureString(text, UiTheme.Body, width);
        return (int)Math.Ceiling(size.Height) + 4;
    }

    private async Task StartWakeTestAsync(string mode)
    {
        var message = mode == "timer"
            ? "Компьютер уснёт и должен сам проснуться через 2 минуты.\n\n" +
              "Ничего не нажимайте — после пробуждения результат появится в журнале и на телефоне."
            : "Компьютер уснёт на 2 минуты. Пока он спит, нажмите «Включить» в приложении на телефоне,\n" +
              "находясь в домашней сети Wi-Fi.\n\nПродолжить?";

        if (MessageBox.Show(this, message, "Тестовое пробуждение",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            return;

        try
        {
            await _link.RunCommandAsync(Commands.WakeTest, new { mode, sleepSeconds = 120 })
                .ConfigureAwait(true);
            MessageBox.Show(this, "Тест запущен. Компьютер уснёт через несколько секунд.",
                "Тестовое пробуждение", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось запустить тест",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
