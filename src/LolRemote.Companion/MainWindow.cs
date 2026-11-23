using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LolRemote.Companion;

public sealed class MainWindow : Form
{
    static readonly Color Background = Color.FromArgb(7, 11, 20);

    readonly WebView2 _web;
    readonly string _url;

    public MainWindow(string url)
    {
        _url = url;
        Text = "LolRemote";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
        BackColor = Background;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1180, 780);
        MinimumSize = new Size(860, 600);

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };
        Controls.Add(_web);

        Load += async (_, _) => await InitAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var on = 1;
            if (DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, 19, ref on, sizeof(int));
        }
        catch (Exception)
        {
        }
    }

    async Task InitAsync()
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LolRemote", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, folder);
            await _web.EnsureCoreWebView2Async(environment);
            var settings = _web.CoreWebView2.Settings;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsZoomControlEnabled = false;
            _web.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                OpenExternal(args.Uri);
            };
            _web.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && !uri.IsLoopback)
                {
                    args.Cancel = true;
                    OpenExternal(args.Uri);
                }
            };
            _web.Source = new Uri(_url);
        }
        catch (Exception ex)
        {
            ShowFallback(ex);
        }
    }

    void ShowFallback(Exception ex)
    {
        Controls.Clear();
        var label = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(232, 236, 244),
            BackColor = Background,
            Font = new Font("Segoe UI", 12f),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "A LolRemote fut.\n\nA felület a böngésződben nyílt meg, mert ezen a gépen nincs Microsoft Edge WebView2.\n"
                + "(Telepíthető innen: https://go.microsoft.com/fwlink/p/?LinkId=2124703)\n\n"
                + "Ha bezárod ezt az ablakot, a LolRemote leáll.\n\n" + ex.Message
        };
        Controls.Add(label);
        OpenExternal(_url);
    }

    static void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }

    public void BringToFrontSafe()
    {
        if (!IsHandleCreated) return;
        BeginInvoke(new Action(() =>
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
            TopMost = true;
            TopMost = false;
        }));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
