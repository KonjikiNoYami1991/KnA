using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KnA.App.ViewModels;
using KnA.Core.Localization;
using KnA.Core.Remote;

namespace KnA.App.Views;

/// <summary>
/// Settings → KnA server: encode on a KnA server (a powerful PC or a homelab server) instead of this computer. Servers
/// on the local network are found by themselves; the password stays in the system's keychain. Back to this computer is
/// one click.
/// </summary>
public sealed class ServerPage : StackPanel
{
    private readonly MainViewModel _vm;
    private readonly Window _owner;
    private readonly ISecretStore _secrets = SecretStores.ForThisSystem();
    private readonly TextBlock _now = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly Button _local;
    private readonly TextBox _url, _user, _password;
    private readonly CheckBox _remember;
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _warning = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkOrange };
    private readonly StackPanel _found = new() { Spacing = 6 };
    private readonly Button _connect;

    public ServerPage(MainViewModel vm, Window owner)
    {
        _vm = vm;
        _owner = owner;
        Spacing = 14;
        var rs = vm.Queue.Settings.Remote;

        _local = Ui.Button(L.T("Encode on this computer"), async () => { await _vm.DisconnectAsync(); Refresh(); });
        _url = Ui.Text(rs.Url, _ => ShowWarning(), "192.168.1.10:8585 · https://kna.example.com");
        _user = Ui.Text(rs.User, _ => { }, L.T("User name"));
        _password = new TextBox { PasswordChar = '•', Watermark = L.T("Password") };
        if (SavedPassword(rs) is not null) _password.Watermark = L.T("Saved in {0}", _secrets.Name);
        _remember = new CheckBox { Content = L.T("Remember the password in {0}", _secrets.Name), IsChecked = rs.Remember && _secrets.Unavailable is null, IsEnabled = _secrets.Unavailable is null };
        _connect = Ui.Button(L.T("Connect and encode on this server"), ConnectAsync, primary: true);

        Children.Add(Ui.Muted(L.T("A powerful PC or a homelab server runs kna-server; this computer keeps the same program, and the work (encodes, GPU, components) happens there. Files of this computer are uploaded by themselves, and the results come back by themselves into the output folder — also after this computer was switched off.")));
        Children.Add(Ui.Card(new StackPanel { Spacing = 10, Children = { _now, _local } }));

        var search = Ui.Button(L.T("Search the local network"), SearchAsync);
        var form = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.H2(L.T("A KnA server")),
                Ui.Row(8, search),
                _found,
                Ui.Field("Address", _url, L.T("Found by itself on the local network, or typed: an address, a name, an https:// address behind a reverse proxy.")),
                Ui.Columns(Ui.Field("User name", _user), Ui.Field("Password", _password)),
                _remember,
                _secrets.Unavailable is { } why ? Ui.Muted("⚠ " + why) : Ui.Muted(L.T("The password is kept by {0}, never in a file of KnA.", _secrets.Name)),
                _warning,
                Ui.Row(8, _connect),
                _result,
            },
        };
        Children.Add(Ui.Card(form));
        Children.Add(Ui.Muted(L.T("In this version on a server: the queue (add, start, stop, pause, order, settings of the side panel, presets of this computer), uploads, results back home, logs and the server's hardware. The editor, previews, OCR and the AI assistant of a server's file open in its web page for now.")));
        Refresh();
        ShowWarning();
    }

    private string? SavedPassword(RemoteServerSettings rs)
    {
        if (string.IsNullOrWhiteSpace(rs.Url) || string.IsNullOrWhiteSpace(rs.User) || _secrets.Unavailable is not null) return null;
        try { return _secrets.Read(SecretStores.Key(KnaClient.NormalizeUrl(rs.Url).ToString(), rs.User)); }
        catch (Exception ex) when (ex is RemoteException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private void Refresh()
    {
        _now.Text = _vm.Remote is { } r
            ? L.T("Now: on the KnA server {0} ({1}), as {2}.", r.Server?.Name ?? r.Client.BaseUri.Host, r.Client.BaseUri, r.Client.User)
              + (r.Connected ? "" : " " + L.T("Not reachable at the moment: retrying by itself."))
            : L.T("Now: on this computer.");
        _local.IsVisible = _vm.Remote is not null;
    }

    private void ShowWarning()
    {
        try
        {
            var uri = KnaClient.NormalizeUrl(_url.Text ?? "");
            _warning.Text = KnaClient.IsInsecure(uri)
                ? "⚠ " + L.T("Plain HTTP outside the local network: the password and the files travel readable. Use the server's HTTPS address (reverse proxy).")
                : "";
        }
        catch (RemoteException) { _warning.Text = ""; }
    }

    private async Task SearchAsync()
    {
        _found.Children.Clear();
        _found.Children.Add(Ui.Muted(L.T("Searching…")));
        var list = await ServerDiscovery.FindAsync(TimeSpan.FromSeconds(2));
        _found.Children.Clear();
        if (list.Count == 0)
        {
            _found.Children.Add(Ui.Muted(L.T("No KnA server answered on this network (another subnet, a VPN or Docker without host network): type its address.")));
            return;
        }
        foreach (var s in list)
            _found.Children.Add(new Button
            {
                Content = $"{s.Name} · KnA {s.Version} · {s.Platform}\n{s.Url}", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            }.Also(b => b.Click += (_, _) => { _url.Text = s.Url; ShowWarning(); _user.Focus(); }));
    }

    private async Task ConnectAsync()
    {
        _connect.IsEnabled = false;
        _result.Text = L.T("Connecting…");
        try
        {
            var current = _vm.Queue.Settings.Remote;
            var url = KnaClient.NormalizeUrl(_url.Text ?? "").ToString();
            var user = (_user.Text ?? "").Trim();
            if (user.Length == 0) throw new RemoteException(L.T("Enter the user name and the password."));
            var server = current with { Url = url, User = user, Remember = _remember.IsChecked == true, Enabled = true,
                PinnedCertificate = current.Url == url ? current.PinnedCertificate : null };
            var password = string.IsNullOrEmpty(_password.Text) ? SavedPassword(server) : _password.Text;
            if (string.IsNullOrEmpty(password)) throw new RemoteException(L.T("Enter the user name and the password."));
            try { await _vm.ConnectAsync(server, password); }
            catch (RemoteException ex) when (ex.UntrustedCertificate is { } fp)
            {
                // A self-signed certificate: accepted once, then pinned (any other certificate is refused).
                if (!await Ui.ConfirmAsync(_owner, L.T("Certificate"), L.T("The server's HTTPS certificate is not signed by an authority this computer knows (self-signed?).\n\nFingerprint (SHA-256):\n{0}\n\nCompare it with the one of the server and trust it?", KnaClient.Pretty(fp))))
                    throw;
                server = server with { PinnedCertificate = fp };
                await _vm.ConnectAsync(server, password);
            }
            var key = SecretStores.Key(url, user);
            string? kept = null;
            try
            {
                if (server.Remember && _secrets.Unavailable is null) _secrets.Write(key, password);
                else if (_secrets.Unavailable is null) _secrets.Delete(key);
            }
            catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
            {
                kept = ex.Message;
                server = server with { Remember = false };
            }
            _vm.Queue.Settings.Remote = server;
            _vm.Queue.SaveSettings();
            _password.Text = "";
            _result.Text = "✓ " + L.T("Connected: the queue is the server's now.") + (kept is null ? "" : "\n⚠ " + kept);
        }
        catch (RemoteException ex)
        {
            _result.Text = "✕ " + ex.Message + "\n" + L.T("Encoding stays on this computer.");
        }
        finally
        {
            _connect.IsEnabled = true;
            Refresh();
        }
    }
}
