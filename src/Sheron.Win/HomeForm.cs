using System.Globalization;
using System.Net;
using Sheron.Core;
using Sheron.Core.Rendezvous;
using Sheron.Core.Session;
using Sheron.Win.Ui;

namespace Sheron.Win;

/// <summary>
/// The home window (Plan step 15a; PRD decisions 1 Nov and 5 Nov): layout 3 "Address bar" with the wordmark. It is
/// also this PC's sharing: it holds the PC's socket and registration (<see cref="ThisPc"/>), shows the incoming request
/// and, while a viewer is let in, the banner. Connect opens the viewer window on the same registration. The partner ID
/// and password boxes are the only native controls (CLAUDE.md › Windows UI). Closing it ends sharing too.
/// </summary>
internal sealed class HomeForm : PaintedForm
{
    private const int RecentRows = 6;
    private static readonly TimeSpan StatusEvery = TimeSpan.FromSeconds(60);

    private readonly ThisPc _pc;
    private readonly bool _hardwareDecoder;
    private readonly RecentList _recent;
    private readonly TextBox _partner = new() { BorderStyle = BorderStyle.None, PlaceholderText = "Partner ID" };
    // Never put on the command line, where other programs can read it (TRD › Connect by IP). Only for unattended
    // access (PRD F5); empty asks the person at the remote PC (PRD decision 4 Nov).
    private readonly TextBox _password = new() { BorderStyle = BorderStyle.None, PlaceholderText = "Password (unattended only)", UseSystemPasswordChar = true };
    private readonly PaintedButton _connect;
    private readonly PaintedButton _copy;
    private readonly List<PaintedButton> _rowButtons = [];
    private readonly Dictionary<uint, PcState> _states = [];
    private readonly System.Windows.Forms.Timer _statusTimer = new();
    private readonly System.Windows.Forms.Timer _copiedTimer = new() { Interval = 1500 };
    private readonly CancellationTokenSource _closing = new();
    private RequestForm? _request;
    private BannerForm? _banner;
    private ViewerTestForm? _viewer;
    private (string Text, bool Bad)? _message;
    private bool _copied;
    private int _scroll;
    private ServerState _lastServer = ServerState.Connecting;

    /// <param name="address">An ID or IP address to fill in (from <c>--view</c>); the password is then typed.</param>
    public HomeForm(string? address = null, bool hardwareDecoder = true, bool hardwareEncoder = true)
    {
        _hardwareDecoder = hardwareDecoder;
        _pc = new ThisPc(Link.DefaultPort, hardwareEncoder);
        _recent = RecentList.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sheron", "recent.json"));
        _connect = new PaintedButton(ButtonLook.Primary, "Connect", Connect);
        _copy = new PaintedButton(ButtonLook.Icon, "Copy ID", CopyId) { DrawIcon = Draw.CopyIcon };
        Buttons.Add(_connect);
        Buttons.Add(_copy);
        for (int i = 0; i < RecentRows; i++)
        {
            int row = i;
            var button = new PaintedButton(ButtonLook.Outline, "Connect", () => ConnectRecent(row)) { Visible = false };
            _rowButtons.Add(button);
            Buttons.Add(button);
        }

        SuspendLayout();
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        _partner.Text = address ?? "";
        foreach (var box in new[] { _partner, _password })
        {
            box.BackColor = Theme.Background;
            box.ForeColor = Theme.Ink;
            box.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                Connect();
            };
            box.Enter += (_, _) => Invalidate();
            box.Leave += (_, _) => Invalidate();
            Controls.Add(box);
        }
        OnFontsChanged();
        ResumeLayout(false);
        SizeAt96(880, 680);

        _pc.Changed += OnPcChanged;
        _pc.ConsentNeeded += ShowRequest;
        _pc.ConsentEnded += HideRequest;
        _pc.SharingStarted += ShowBanner;
        _pc.SharingEnded += HideBanner;
        _statusTimer.Interval = (int)StatusEvery.TotalMilliseconds;
        _statusTimer.Tick += (_, _) => RefreshStates();
        _copiedTimer.Tick += (_, _) =>
        {
            _copiedTimer.Stop();
            _copied = false;
            Invalidate();
        };
        Shown += (_, _) =>
        {
            _pc.Start();
            if (_pc.Problem is { } problem) Say(problem, bad: true);
            _statusTimer.Start();
            (address is not null ? _password : _partner).Focus();
        };
    }

    protected override void OnFontsChanged()
    {
        _partner.Font = Mono(24);
        _password.Font = Sans(15);
        _connect.Font = Sans(17, Weight.Bold);
        foreach (var button in _rowButtons) button.Font = Sans(14);
    }

    // ---- Layout (board pixels at 96 DPI) ----

    private Rectangle Header => Px(0, 0, 880, 64);
    private Rectangle Bar => Px(24, 88, 832, 88);
    private Rectangle PartnerFrame => Px(40, 104, 360, 56);
    private Rectangle PasswordFrame => Px(412, 104, 268, 56);
    private Rectangle MessageLine => Px(24, 184, 832, 24);
    private Rectangle ThisPcPanel => Px(24, 216, 404, 400);
    private Rectangle RecentPanel => Px(452, 216, 404, 400);
    private Rectangle Footer => Px(0, 640, 880, 40);
    private Rectangle Row(int i) => Px(480, 276 + 52 * i, 348, 52);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_connect is null) return;   // not made yet: Windows may lay out a window while it is being built
        Place(_partner, PartnerFrame);
        Place(_password, PasswordFrame);
        _connect.Bounds = Px(692, 104, 148, 56);
        for (int i = 0; i < _rowButtons.Count; i++)
        {
            var row = Row(i);
            _rowButtons[i].Bounds = new Rectangle(row.Right - Px(96), row.Top + Px(8), Px(96), Px(36));
        }
    }

    /// <summary>A borderless text box inside its painted frame, centred top to bottom.</summary>
    private void Place(TextBox box, Rectangle frame)
    {
        int height = box.PreferredHeight;
        box.Bounds = new Rectangle(frame.Left + Px(16), frame.Top + (frame.Height - height) / 2, frame.Width - Px(32), height);
    }

    // ---- Drawing ----

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        DrawHeader(g);
        DrawBar(g);
        if (_message is { } message)
            Draw.Text(g, message.Text, Sans(14), MessageLine, message.Bad ? Theme.StopText : Theme.Soft);
        DrawThisPc(g);
        DrawRecent(g);
        DrawFooter(g);
        DrawButtons(g);
        if (_copied)
        {
            var tip = new Rectangle(_copy.Bounds.Right + Px(8), _copy.Bounds.Top, Px(80), _copy.Bounds.Height);
            Draw.Text(g, "Copied", Sans(13), tip, Theme.Good);
        }
    }

    private void DrawHeader(Graphics g)
    {
        var header = Header;
        using (var bar = new SolidBrush(Theme.Bar)) g.FillRectangle(bar, header);
        using (var line = new Pen(Theme.Line)) g.DrawLine(line, header.Left, header.Bottom - 1, header.Right, header.Bottom - 1);
        var logo = Sans(26, Weight.SemiBold);
        Draw.Wordmark(g, logo, new Point(Px(24), header.Top + (header.Height - logo.Height) / 2), Theme.Ink, Theme.Accent);

        (string text, Color dot) = _pc.Server switch
        {
            _ when _pc.Problem is not null => ("Not running", Theme.Stop),
            ServerState.Online => ("Online", Theme.Good),
            ServerState.Connecting => ("Connecting to the server …", Theme.Accent),
            ServerState.WrongServer => ("Not our server: Sheron cannot connect", Theme.Stop),
            _ => ("Offline · trying again", Theme.Stop),
        };
        var font = Sans(13);
        int width = Draw.Measure(text, font).Width + Px(12 + 8 + 8 + 12);
        var pill = new Rectangle(header.Right - Px(24) - width, header.Top + Px(16), width, Px(32));
        Draw.Box(g, pill, Px(999), null, Theme.Line, Px(1));
        Draw.Dot(g, new Point(pill.Left + Px(16), pill.Top + pill.Height / 2), Px(8), dot);
        Draw.Text(g, text, font, new Rectangle(pill.Left + Px(28), pill.Top, width - Px(28), pill.Height), Theme.Soft);
    }

    private void DrawBar(Graphics g)
    {
        Draw.Box(g, Bar, Px(12), Theme.Panel, Theme.Line, Px(1));
        foreach (var (box, frame) in new[] { (_partner, PartnerFrame), (_password, PasswordFrame) })
        {
            bool focused = box.Focused;
            Draw.Box(g, frame, Px(8), Theme.Background, focused ? Theme.Accent : Theme.Edge, Px(focused ? 2 : 1));
        }
        bool canConnect = !_pc.IsShared && !_pc.IsViewing && _pc.Problem is null;
        _connect.Enabled = canConnect;
        foreach (var button in _rowButtons) button.Enabled = canConnect;
    }

    private void DrawThisPc(Graphics g)
    {
        var panel = ThisPcPanel;
        Draw.Box(g, panel, Px(12), Theme.Panel, Theme.Line, Px(1));
        int left = panel.Left + Px(28);
        int width = panel.Width - Px(56);
        Draw.Spaced(g, "THIS PC", Sans(13, Weight.SemiBold), new Point(left, panel.Top + Px(28)), Theme.Muted, Px(13) * 0.12f);
        Draw.Text(g, "Your ID", Sans(14), new Rectangle(left, panel.Top + Px(64), width, Px(20)), Theme.Muted);

        var idFont = Mono(40, Weight.SemiBold);
        string id = _pc.Id is { } known ? RendezvousWire.Show(known) : "– – – –";
        int idWidth = Draw.Spaced(g, id, idFont, new Point(left, panel.Top + Px(90)), _pc.Id is null ? Theme.Muted : Theme.Ink, Px(40) * 0.04f);
        _copy.Bounds = new Rectangle(left + idWidth + Px(16), panel.Top + Px(94), Px(44), Px(44));
        _copy.Visible = _pc.Id is not null;

        Draw.Text(g, "Give this ID to the person who connects. You choose Accept or Deny each time.", Sans(14),
            new Rectangle(left, panel.Top + Px(160), width, Px(44)), Theme.Muted, Draw.Wrapped);

        // What this PC is doing now.
        var (name, where) = _pc.Viewer;
        (string state, Color dot) = _pc.Problem is { } problem ? (problem, Theme.Stop)
            : _pc.IsShared ? ($"Controlled by {name} ({where})", Theme.Accent)
            : _pc.Pending is not null ? ($"{name} asks to connect: Accept or Deny", Theme.Accent)
            : _pc.IsViewing ? ("In a session with another PC", Theme.Accent)
            : _pc.Server == ServerState.Online ? ("Ready for a session", Theme.Good)
            : ("Waiting for the server", Theme.Edge);
        Draw.Dot(g, new Point(left + Px(4), panel.Top + Px(236)), Px(8), dot);
        Draw.Text(g, state, Sans(15), new Rectangle(left + Px(18), panel.Top + Px(222), width - Px(18), Px(28)), Theme.Soft);
        // Why there is no ID yet: what the server link last said.
        if (_pc.Problem is null && _pc.Server != ServerState.Online && !_pc.IsShared && !_pc.IsViewing)
            Draw.Text(g, _pc.ServerDetail, Sans(13), new Rectangle(left + Px(18), panel.Top + Px(254), width - Px(18), Px(40)), Theme.Muted, Draw.Wrapped);

        int lineY = panel.Bottom - Px(72);
        using (var line = new Pen(Theme.Line)) g.DrawLine(line, left, lineY, left + width, lineY);
        bool unattended = _pc.Gate?.FixedPassword is not null;
        var rowArea = new Rectangle(left, lineY + Px(20), width, Px(24));
        string label = "Unattended access · ";
        Draw.Text(g, label, Sans(15), rowArea, Theme.Soft);
        int at = Draw.Measure(label, Sans(15)).Width;
        Draw.Text(g, unattended ? "On" : "Off", Sans(15, Weight.SemiBold), new Rectangle(left + at, rowArea.Top, width - at, rowArea.Height), Theme.Ink);
    }

    private void DrawRecent(Graphics g)
    {
        var panel = RecentPanel;
        Draw.Box(g, panel, Px(12), Theme.Panel, Theme.Line, Px(1));
        int left = panel.Left + Px(28);
        Draw.Spaced(g, "RECENT", Sans(13, Weight.SemiBold), new Point(left, panel.Top + Px(28)), Theme.Muted, Px(13) * 0.12f);
        var items = _recent.Items;
        if (items.Count == 0)
        {
            Draw.Text(g, "PCs you connect to show here.", Sans(14), new Rectangle(left, panel.Top + Px(64), panel.Width - Px(56), Px(24)), Theme.Muted);
        }
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, items.Count - RecentRows));
        using var line = new Pen(Theme.RowLine);
        for (int i = 0; i < RecentRows; i++)
        {
            var button = _rowButtons[i];
            int index = _scroll + i;
            button.Visible = index < items.Count;
            if (!button.Visible) continue;
            var pc = items[index];
            var row = Row(i);
            g.DrawLine(line, row.Left, row.Top, row.Right, row.Top);
            Color dot = pc.Id is { } id && _states.TryGetValue(id, out var state)
                ? state switch { PcState.Online => Theme.Good, PcState.Busy => Theme.Accent, _ => Theme.Edge }
                : Theme.Edge;
            Draw.Dot(g, new Point(row.Left + Px(4), row.Top + Px(18)), Px(8), dot);
            int textLeft = row.Left + Px(18);
            int textWidth = button.Bounds.Left - Px(12) - textLeft;
            string name = pc.Name.Length > 0 ? pc.Name : pc.Target;
            Draw.Text(g, name, Sans(15, Weight.SemiBold), new Rectangle(textLeft, row.Top + Px(6), textWidth, Px(22)), Theme.Ink);
            string detail = $"{pc.Target} · {When(pc.LastUsed)}" + (dot == Theme.Accent ? " · in a session" : "");
            Draw.Text(g, detail, Mono(13), new Rectangle(textLeft, row.Top + Px(28), textWidth, Px(18)), Theme.Muted);
        }
        if (items.Count > RecentRows)
            Draw.Text(g, $"{_scroll + 1}–{Math.Min(items.Count, _scroll + RecentRows)} of {items.Count} · scroll for more", Sans(12),
                new Rectangle(left, panel.Bottom - Px(26), panel.Width - Px(56), Px(18)), Theme.Muted);
    }

    /// <summary>"today 09:12", "yesterday" or "22 Oct", as on the Home board.</summary>
    private static string When(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today) return $"today {local:HH:mm}";
        if (local.Date == today.AddDays(-1)) return "yesterday";
        return local.ToString("d MMM", CultureInfo.InvariantCulture);
    }

    private void DrawFooter(Graphics g)
    {
        var footer = Footer;
        using (var bar = new SolidBrush(Theme.Bar)) g.FillRectangle(bar, footer);
        using (var line = new Pen(Theme.Line)) g.DrawLine(line, footer.Left, footer.Top, footer.Right, footer.Top);
        var font = Sans(13);
        Draw.LockIcon(g, new Rectangle(Px(24), footer.Top + Px(13), Px(14), Px(14)), Theme.Muted);
        Draw.Text(g, "End-to-end encrypted", font, new Rectangle(Px(46), footer.Top, Px(300), footer.Height), Theme.Muted);
        Draw.Text(g, $"Version {AppInfo.Version}", font, new Rectangle(footer.Right - Px(224), footer.Top, Px(200), footer.Height), Theme.Muted,
            Draw.OneLine | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    // ---- Actions ----

    private void Say(string text, bool bad = false)
    {
        _message = (text, bad);
        Invalidate(MessageLine);
    }

    private void CopyId()
    {
        if (_pc.Id is not { } id) return;
        try
        {
            Clipboard.SetText(RendezvousWire.Show(id));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return;   // another program holds the clipboard
        }
        _copied = true;
        _copiedTimer.Stop();
        _copiedTimer.Start();
        Invalidate();
    }

    /// <summary>A click on a Recent row away from its button puts its ID in the box.</summary>
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left || RowAt(e.Location) is not { } index || _rowButtons[index - _scroll].Bounds.Contains(e.Location)) return;
        _partner.Text = _recent.Items[index].Target;
        _password.Focus();
    }

    protected override Cursor CursorAt(Point at) => RowAt(at) is not null ? Cursors.Hand : Cursors.Default;

    private int? RowAt(Point at)
    {
        for (int i = 0; i < RecentRows; i++)
            if (Row(i).Contains(at) && _scroll + i < _recent.Items.Count) return _scroll + i;
        return null;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!RecentPanel.Contains(e.Location) || _recent.Items.Count <= RecentRows) return;
        _scroll = Math.Clamp(_scroll - Math.Sign(e.Delta), 0, _recent.Items.Count - RecentRows);
        Invalidate(RecentPanel);
    }

    private void ConnectRecent(int row)
    {
        int index = _scroll + row;
        if (index >= _recent.Items.Count) return;
        _partner.Text = _recent.Items[index].Target;
        Connect();
    }

    private void Connect()
    {
        string typed = _partner.Text.Trim();
        bool byId = RendezvousWire.TryParseId(typed, out uint id);
        IPEndPoint? remote = null;
        if (typed.Length == 0)
        {
            Say("Type the partner's ID first: the 4 characters their Sheron shows.", bad: true);
            _partner.Focus();
            return;
        }
        if (!byId && (!IPEndPoint.TryParse(typed, out remote) || remote.Address.Equals(IPAddress.Any)))
        {
            Say($"\"{typed}\" is not an ID: type the 4 characters the other PC's Sheron shows, such as 4821.", bad: true);
            return;
        }
        if (remote is { Port: 0 }) remote.Port = Link.DefaultPort;
        if (System.Text.Encoding.UTF8.GetByteCount(_password.Text) > byte.MaxValue)
        {
            Say("That password is too long.", bad: true);
            return;
        }
        if (_pc.Problem is { } problem)
        {
            Say(problem, bad: true);
            return;
        }
        if (byId && _pc.Id == id)
        {
            Say("That is this PC's own ID: type the ID the other PC shows.", bad: true);
            return;
        }
        if (byId && _pc.Server != ServerState.Online)
        {
            Say("Not connected to the Sheron server yet: wait until the header says Online.", bad: true);
            return;
        }
        // One session at a time (PRD decision 5 Nov).
        if (_pc.IsShared)
        {
            Say("This PC is being controlled: press Disconnect on the banner first.", bad: true);
            return;
        }
        if (_pc.Pending is not null)
        {
            Say("Someone is asking to connect to this PC: answer Accept or Deny first.", bad: true);
            return;
        }
        if (!_pc.BeginViewing())
        {
            Say("This PC is in another session.", bad: true);
            return;
        }

        _message = null;
        string target = byId ? RendezvousWire.Show(id) : remote!.ToString();
        var viewer = byId ? new ViewerTestForm(_pc, id, _password.Text, _hardwareDecoder) : new ViewerTestForm(_pc, remote!, _password.Text, _hardwareDecoder);
        _password.Clear();
        _viewer = viewer;
        viewer.LetIn += name =>
        {
            _recent.Add(target, name ?? "", DateTimeOffset.Now);
            _recent.Save();
            _scroll = 0;
        };
        // Closing the viewer comes back here, to view another PC or be viewed.
        viewer.FormClosed += (_, _) =>
        {
            _viewer = null;
            _pc.EndViewing();
            if (_closing.IsCancellationRequested) return;   // the home is closing too
            Show();
            Activate();
            RefreshStates();
            _partner.Focus();
        };
        viewer.Show();
        Hide();
    }

    // ---- This PC's events ----

    private void OnPcChanged()
    {
        // Back online: the Recent dots are asked for again.
        if (_pc.Server == ServerState.Online && _lastServer != ServerState.Online) RefreshStates();
        _lastServer = _pc.Server;
        Invalidate();
    }

    private async void RefreshStates()
    {
        if (!Visible || WindowState == FormWindowState.Minimized || _pc.Server != ServerState.Online) return;
        var ids = _recent.Items.Select(i => i.Id).OfType<uint>().Distinct().Take(RendezvousWire.MaxStatusIds).ToList();
        if (ids.Count == 0) return;
        var states = await _pc.StatusAsync(ids, _closing.Token);
        if (IsDisposed) return;
        foreach (var (id, state) in states)
            if (state != PcState.NotChecked) _states[id] = state;
        Invalidate(RecentPanel);
    }

    private void ShowRequest(ConsentRequest request)
    {
        _request?.Close();
        _request = new RequestForm(request, _pc.WhereFrom(request));
        _request.FormClosed += (_, _) => _request = null;
        _request.Show();
        _request.Activate();
        Invalidate();
    }

    private void HideRequest()
    {
        if (_pc.Pending is null) _request?.Close();
        Invalidate();
    }

    private void ShowBanner()
    {
        _banner?.End();
        _banner = new BannerForm(_pc);
        _banner.Show();
        Invalidate();
    }

    private void HideBanner()
    {
        _banner?.End();
        _banner = null;
        Invalidate();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing.Cancel();
        _statusTimer.Stop();
        _request?.Close();
        _banner?.End();
        _viewer?.Close();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pc.Dispose();
            _statusTimer.Dispose();
            _copiedTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
