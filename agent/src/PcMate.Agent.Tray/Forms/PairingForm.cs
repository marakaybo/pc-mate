using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using PcMate.Agent.Tray.Session;
using PcMate.Core.Models;
using PcMate.Core.Protocol;
using PcMate.Core.Util;
using QRCoder;

namespace PcMate.Agent.Tray.Forms;

/// <summary>
/// Окно сопряжения: QR-код с одноразовым токеном на 5 минут.
/// Телефон сканирует его и получает публичный ключ ПК, адреса в локальной сети и MAC.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PairingForm : Form
{
    private readonly AgentLink _link;
    private readonly PictureBox _qr = new();
    private readonly Label _timer = new();
    private readonly Label _hint = new();
    private readonly TextBox _uri = new();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };

    private PairingOffer? _offer;

    public PairingForm(AgentLink link)
    {
        _link = link;
        UiTheme.ApplyForm(this);

        Text = "PC MATE — подключить телефон";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(520, 640);

        var header = UiTheme.Heading("Отсканируйте код в приложении PC MATE");
        header.Location = new Point(24, 20);

        _hint.Text = "Откройте приложение на телефоне → «Добавить компьютер» → наведите камеру.";
        _hint.Font = UiTheme.Body;
        _hint.ForeColor = UiTheme.TextMuted;
        _hint.AutoSize = false;
        _hint.Location = new Point(24, 52);
        _hint.Size = new Size(470, 40);

        _qr.Location = new Point(85, 100);
        _qr.Size = new Size(350, 350);
        _qr.SizeMode = PictureBoxSizeMode.Zoom;
        _qr.BackColor = Color.White;

        _timer.Font = UiTheme.BodyBold;
        _timer.ForeColor = UiTheme.Accent;
        _timer.AutoSize = false;
        _timer.TextAlign = ContentAlignment.MiddleCenter;
        _timer.Location = new Point(24, 460);
        _timer.Size = new Size(470, 26);

        var uriLabel = UiTheme.Muted("Если камера не работает, введите этот код вручную:");
        uriLabel.Location = new Point(24, 494);

        _uri.Location = new Point(24, 518);
        _uri.Size = new Size(470, 60);
        _uri.Multiline = true;
        _uri.ReadOnly = true;
        _uri.BackColor = UiTheme.Surface;
        _uri.ForeColor = UiTheme.TextMuted;
        _uri.BorderStyle = BorderStyle.FixedSingle;
        _uri.ScrollBars = ScrollBars.Vertical;
        _uri.Font = new Font("Consolas", 8F);

        var refresh = UiTheme.PrimaryButton("Новый код");
        refresh.Location = new Point(24, 588);
        refresh.Click += async (_, _) => await LoadOfferAsync();

        var copy = UiTheme.SecondaryButton("Скопировать код");
        copy.Location = new Point(154, 588);
        copy.Click += (_, _) =>
        {
            if (_offer is null) return;
            Clipboard.SetText(_offer.ToUri());
            copy.Text = "Скопировано";
        };

        var close = UiTheme.SecondaryButton("Закрыть");
        close.Location = new Point(374, 588);
        close.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { header, _hint, _qr, _timer, uriLabel, _uri, refresh, copy, close });

        _tick.Tick += (_, _) => UpdateTimer();
        Load += async (_, _) => await LoadOfferAsync();
    }

    private async Task LoadOfferAsync()
    {
        try
        {
            _timer.Text = "Запрашиваем код у службы…";
            var result = await _link.RunCommandAsync(Commands.PairOffer).ConfigureAwait(true);
            var offerNode = result?["offer"];
            if (offerNode is null) throw new InvalidOperationException("Служба не вернула код сопряжения.");

            _offer = PcMateJson.Deserialize<PairingOffer>(offerNode.ToJsonString());
            if (_offer is null) throw new InvalidOperationException("Не удалось разобрать код сопряжения.");

            var uri = _offer.ToUri();
            _uri.Text = uri;
            _qr.Image?.Dispose();
            _qr.Image = RenderQr(uri);

            _hint.Text = $"Компьютер: {_offer.PcName}\n" +
                         $"Отпечаток ключа: {_offer.Fp} — он должен совпасть в приложении.";
            _tick.Start();
            UpdateTimer();
        }
        catch (Exception ex)
        {
            _timer.Text = "Ошибка: " + ex.Message;
            _timer.ForeColor = UiTheme.Fail;
        }
    }

    private void UpdateTimer()
    {
        if (_offer is null) return;

        var left = _offer.Exp - TimeUtil.UnixNow();
        if (left <= 0)
        {
            _tick.Stop();
            _timer.Text = "Код истёк — нажмите «Новый код».";
            _timer.ForeColor = UiTheme.Fail;
            _qr.Image?.Dispose();
            _qr.Image = null;
            return;
        }

        _timer.ForeColor = left < 60 ? UiTheme.Warn : UiTheme.Accent;
        _timer.Text = $"Код действует ещё {left / 60}:{left % 60:00}";
    }

    private static Image RenderQr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(12);
        using var stream = new MemoryStream(png);
        return Image.FromStream(stream);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tick.Stop();
        _tick.Dispose();
        _qr.Image?.Dispose();
        base.OnFormClosed(e);
    }
}
