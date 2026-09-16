using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using PcMate.Core.Ipc;

namespace PcMate.Agent.Tray.Forms;

/// <summary>
/// Окно обратного отсчёта перед выключением или сном. Всегда поверх окон,
/// с крупной кнопкой «Отмена» — команда с телефона не должна заставать врасплох.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CountdownForm : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Label _counter = new();
    private readonly Label _subtitle = new();
    private int _secondsLeft;

    public event Action? Cancelled;

    public CountdownForm(CountdownRequest request)
    {
        UiTheme.ApplyForm(this);

        Text = "PC MATE";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        ShowInTaskbar = true;
        ClientSize = new Size(460, 250);
        _secondsLeft = Math.Max(0, request.Seconds);

        var title = new Label
        {
            Text = request.ActionTitle,
            Font = UiTheme.Title,
            ForeColor = UiTheme.Text,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(0, 16, 0, 0)
        };

        _counter.Text = FormatSeconds(_secondsLeft);
        _counter.Font = UiTheme.Huge;
        _counter.ForeColor = UiTheme.Accent;
        _counter.AutoSize = false;
        _counter.TextAlign = ContentAlignment.MiddleCenter;
        _counter.Dock = DockStyle.Top;
        _counter.Height = 76;

        _subtitle.Text = "Команда пришла с телефона. Нажмите «Отмена», чтобы остановить.";
        _subtitle.Font = UiTheme.Body;
        _subtitle.ForeColor = UiTheme.TextMuted;
        _subtitle.AutoSize = false;
        _subtitle.TextAlign = ContentAlignment.MiddleCenter;
        _subtitle.Dock = DockStyle.Top;
        _subtitle.Height = 44;
        _subtitle.Padding = new Padding(24, 0, 24, 0);

        var cancel = UiTheme.DangerButton("Отмена");
        cancel.Width = 180;
        cancel.Height = 44;
        cancel.Enabled = request.Cancellable;
        cancel.Click += (_, _) =>
        {
            Cancelled?.Invoke();
            Close();
        };

        var buttons = new Panel { Dock = DockStyle.Fill };
        buttons.Controls.Add(cancel);
        cancel.Location = new Point((ClientSize.Width - cancel.Width) / 2, 10);
        cancel.Anchor = AnchorStyles.Top;

        Controls.Add(buttons);
        Controls.Add(_subtitle);
        Controls.Add(_counter);
        Controls.Add(title);

        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _secondsLeft--;
        if (_secondsLeft <= 0)
        {
            _timer.Stop();
            _counter.Text = "0:00";
            _subtitle.Text = "Выполняется…";
            return;
        }

        _counter.Text = FormatSeconds(_secondsLeft);
        if (_secondsLeft <= 5) _counter.ForeColor = UiTheme.Fail;
    }

    private static string FormatSeconds(int seconds) => $"{seconds / 60}:{seconds % 60:00}";

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>Закрывает окно, когда отсчёт отменили с телефона.</summary>
    public void DismissFromAgent()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(DismissFromAgent);
            return;
        }
        _timer.Stop();
        Close();
    }
}
