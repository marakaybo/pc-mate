using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace PcMate.Agent.Tray.Forms;

/// <summary>Единое оформление окон помощника: тёмная тема, крупный читаемый текст.</summary>
[SupportedOSPlatform("windows")]
public static class UiTheme
{
    public static readonly Color Background = Color.FromArgb(24, 28, 38);
    public static readonly Color Surface = Color.FromArgb(33, 39, 52);
    public static readonly Color SurfaceAlt = Color.FromArgb(42, 49, 64);
    public static readonly Color Text = Color.FromArgb(236, 240, 248);
    public static readonly Color TextMuted = Color.FromArgb(160, 172, 194);
    public static readonly Color Accent = Color.FromArgb(33, 118, 255);
    public static readonly Color Ok = Color.FromArgb(86, 200, 130);
    public static readonly Color Warn = Color.FromArgb(240, 180, 70);
    public static readonly Color Fail = Color.FromArgb(232, 92, 92);

    public static Font Title => new("Segoe UI Semibold", 13F);
    public static Font Body => new("Segoe UI", 9.75F);
    public static Font BodyBold => new("Segoe UI Semibold", 9.75F);
    public static Font Huge => new("Segoe UI Light", 34F);
    public static Font Mono => new("Consolas", 11F);

    public static void ApplyForm(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text;
        form.Font = Body;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.ShowIcon = true;
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "pcmate.ico");
            if (File.Exists(iconPath)) form.Icon = new Icon(iconPath);
        }
        catch
        {
            // Иконка не критична.
        }
    }

    public static Button PrimaryButton(string text) => StyleButton(new Button { Text = text }, Accent, Color.White);

    public static Button SecondaryButton(string text) => StyleButton(new Button { Text = text }, SurfaceAlt, Text);

    public static Button DangerButton(string text) => StyleButton(new Button { Text = text }, Fail, Color.White);

    private static Button StyleButton(Button button, Color back, Color fore)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = back;
        button.ForeColor = fore;
        button.Font = BodyBold;
        button.Height = 36;
        button.MinimumSize = new Size(120, 36);
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
        return button;
    }

    public static Label Heading(string text) => new()
    {
        Text = text,
        Font = Title,
        ForeColor = Text,
        AutoSize = true
    };

    public static Label Muted(string text) => new()
    {
        Text = text,
        Font = Body,
        ForeColor = TextMuted,
        AutoSize = true,
        MaximumSize = new Size(560, 0)
    };

    public static Color StatusColor(string status) => status switch
    {
        "ok" => Ok,
        "warn" => Warn,
        "fail" => Fail,
        _ => TextMuted
    };

    public static string StatusGlyph(string status) => status switch
    {
        "ok" => "✓",
        "warn" => "!",
        "fail" => "✕",
        _ => "?"
    };
}
