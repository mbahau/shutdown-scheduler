using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Shutdown Scheduler")]
[assembly: System.Reflection.AssemblyProduct("Shutdown Scheduler")]
[assembly: System.Reflection.AssemblyDescription("Shut down, restart or sleep Windows after a timer")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 MD BAHAUDDIN - MIT License")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

public class MainForm : Form
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    public static readonly Color Blue = Color.FromArgb(0, 120, 212);
    public static readonly Color Red = Color.FromArgb(209, 52, 56);
    static readonly Color Grey = Color.FromArgb(243, 243, 243);
    public const string FontName = "Segoe UI";

    // --dry-run: log commands to a file instead of running them (for testing)
    static bool dryRun;
    static string dryLog = Path.Combine(Path.GetTempPath(), "ShutdownScheduler-dryrun.log");

    readonly string stateFile;
    RadioButton[] actionBtns;
    Button[] presetBtns;
    int[] presetMins = { 10, 15, 20, 30, 45, 60, 120 };
    RadioButton radIn, radAt;
    NumericUpDown numH, numM;
    DateTimePicker dtp;
    CheckBox chkForce;
    Label lblStatus, lblCount, lblNote;
    Button btnStart, btnCancel, btnBubble;
    Timer timer;
    TimerBubble bubble;
    NotifyIcon tray;
    bool trayTipShown;

    DateTime? target;
    DateTime started;
    string action = "Shutdown";
    // The app always runs the countdown itself and only calls Windows at zero.
    // (Handing Windows a "shutdown /t" timer makes it show its own "You're about to be signed out" pop-ups.)
    bool force;
    bool exiting;
    bool warned;

    [STAThread]
    static void Main(string[] args)
    {
        dryRun = Array.IndexOf(args, "--dry-run") >= 0;
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }

    static void Log(string s) { if (dryRun) File.AppendAllText(dryLog, s + Environment.NewLine); }

    static Label NewLabel(string text, int x, int y, int w, int h, float size, FontStyle style)
    {
        Label l = new Label();
        l.Text = text; l.Location = new Point(x, y); l.Size = new Size(w, h);
        l.Font = new Font(FontName, size, style);
        return l;
    }

    static Button NewButton(string text, int x, int y, int w, int h, float size)
    {
        Button b = new Button();
        b.Text = text; b.Location = new Point(x, y); b.Size = new Size(w, h);
        b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = Color.Silver;
        b.BackColor = Color.White; b.Cursor = Cursors.Hand;
        b.Font = new Font(FontName, size);
        return b;
    }

    public static Icon MakeIcon()
    {
        Bitmap bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Blue)) g.FillEllipse(b, 1, 1, 30, 30);
            using (Pen p = new Pen(Color.White, 3.2f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawArc(p, 8, 9, 16, 16, -60, 300);
                g.DrawLine(p, 16, 6, 16, 15);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public MainForm()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShutdownScheduler");
        Directory.CreateDirectory(dir);
        stateFile = Path.Combine(dir, "state.txt");

        Text = "Shutdown Scheduler";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(440, 580);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        BackColor = Color.White;
        Font = new Font(FontName, 10);
        Icon = MakeIcon();

        // ---- Step 1: what to do ----
        Controls.Add(NewLabel("1.  What should the PC do?", 20, 14, 400, 28, 12, FontStyle.Bold));
        string[] actions = { "Shutdown", "Restart", "Sleep" };
        actionBtns = new RadioButton[actions.Length];
        Panel actionPanel = new Panel(); actionPanel.Location = new Point(20, 48); actionPanel.Size = new Size(400, 56);
        Controls.Add(actionPanel);
        for (int i = 0; i < actions.Length; i++)
        {
            RadioButton r = new RadioButton();
            r.Appearance = Appearance.Button; r.Text = actions[i]; r.Tag = actions[i];
            r.Location = new Point(i * 136, 0); r.Size = new Size(128, 56);
            r.TextAlign = ContentAlignment.MiddleCenter;
            r.FlatStyle = FlatStyle.Flat; r.FlatAppearance.BorderColor = Color.Silver; r.FlatAppearance.CheckedBackColor = Blue;
            r.Font = new Font(FontName, 12, FontStyle.Bold); r.Cursor = Cursors.Hand;
            r.CheckedChanged += delegate { UpdateActionStyle(); };
            actionPanel.Controls.Add(r); actionBtns[i] = r;
        }

        // ---- Step 2: when ----
        Controls.Add(NewLabel("2.  When?", 20, 122, 400, 28, 12, FontStyle.Bold));
        string[] presetNums = { "10", "15", "20", "30", "45", "1", "2" };
        string[] presetUnits = { "min", "min", "min", "min", "min", "hour", "hours" };
        presetBtns = new Button[presetNums.Length];
        for (int i = 0; i < presetNums.Length; i++)
        {
            Button b = NewButton("", 20 + i * 58, 152, 54, 46, 9);
            // Draw "number over unit" ourselves so the label always fits at any display scaling
            b.AccessibleName = presetNums[i] + " " + presetUnits[i];
            string num = presetNums[i], unit = presetUnits[i];
            b.Paint += delegate(object s, PaintEventArgs e)
            {
                Button btn = (Button)s;
                bool sel = btn.BackColor == Blue;
                Color fg = sel ? Color.White : btn.Enabled ? Color.Black : Color.Gray;
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                StringFormat c = new StringFormat(); c.Alignment = StringAlignment.Center; c.LineAlignment = StringAlignment.Center;
                int h = btn.ClientSize.Height, w = btn.ClientSize.Width;
                using (Font big = new Font(FontName, h * 0.36f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Font small = new Font(FontName, h * 0.25f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush br = new SolidBrush(fg))
                using (SolidBrush br2 = new SolidBrush(sel ? Color.White : btn.Enabled ? Color.DimGray : Color.Gray))
                {
                    e.Graphics.DrawString(num, big, br, new RectangleF(0, h * 0.04f, w, h * 0.52f), c);
                    e.Graphics.DrawString(unit, small, br2, new RectangleF(0, h * 0.54f, w, h * 0.4f), c);
                }
            };
            int m = presetMins[i];
            b.Click += delegate { numH.Value = m / 60; numM.Value = m % 60; radIn.Checked = true; };
            Controls.Add(b); presetBtns[i] = b;
        }

        Panel whenPanel = new Panel(); whenPanel.Location = new Point(0, 200); whenPanel.Size = new Size(440, 86);
        radIn = new RadioButton(); radIn.Text = "In"; radIn.Location = new Point(20, 8); radIn.Size = new Size(50, 30); radIn.Checked = true;
        numH = new NumericUpDown(); numH.Location = new Point(74, 8); numH.Size = new Size(60, 30); numH.Maximum = 99; numH.Value = 0;
        numM = new NumericUpDown(); numM.Location = new Point(194, 8); numM.Size = new Size(60, 30); numM.Maximum = 59; numM.Value = 20;
        radAt = new RadioButton(); radAt.Text = "At"; radAt.Location = new Point(20, 48); radAt.Size = new Size(50, 30);
        dtp = new DateTimePicker(); dtp.Format = DateTimePickerFormat.Custom; dtp.CustomFormat = "hh:mm tt"; dtp.ShowUpDown = true;
        dtp.Location = new Point(74, 48); dtp.Size = new Size(120, 30); dtp.Value = DateTime.Now.AddMinutes(20);
        Label lblAt = NewLabel("(today or tomorrow)", 200, 52, 240, 24, 9, FontStyle.Regular);
        lblAt.ForeColor = Color.DimGray;
        whenPanel.Controls.AddRange(new Control[] {
            radIn, numH, NewLabel("hours", 138, 11, 54, 24, 10, FontStyle.Regular),
            numM, NewLabel("minutes", 258, 11, 80, 24, 10, FontStyle.Regular),
            radAt, dtp, lblAt });
        Controls.Add(whenPanel);
        numH.ValueChanged += delegate { radIn.Checked = true; UpdatePresetStyle(); };
        numM.ValueChanged += delegate { radIn.Checked = true; UpdatePresetStyle(); };
        dtp.ValueChanged += delegate { radAt.Checked = true; };
        radIn.CheckedChanged += delegate { UpdatePresetStyle(); };
        UpdatePresetStyle();

        chkForce = new CheckBox(); chkForce.Text = "Force-close open apps";
        chkForce.Location = new Point(20, 292); chkForce.Size = new Size(410, 28);
        chkForce.Checked = true;
        Controls.Add(chkForce);

        // ---- Status panel ----
        Panel panel = new Panel(); panel.Location = new Point(20, 332); panel.Size = new Size(400, 100); panel.BackColor = Grey;
        lblStatus = NewLabel("Nothing scheduled", 0, 10, 400, 24, 10, FontStyle.Regular); lblStatus.TextAlign = ContentAlignment.MiddleCenter;
        lblCount = NewLabel("--:--:--", 0, 34, 400, 56, 30, FontStyle.Bold); lblCount.TextAlign = ContentAlignment.MiddleCenter;
        panel.Controls.Add(lblStatus); panel.Controls.Add(lblCount);
        Controls.Add(panel);

        // ---- Start / Cancel ----
        btnStart = NewButton("Start timer", 20, 448, 400, 56, 14);
        btnStart.BackColor = Blue; btnStart.ForeColor = Color.White; btnStart.FlatAppearance.BorderSize = 0;
        btnStart.Font = new Font(FontName, 14, FontStyle.Bold);
        btnStart.Click += delegate { StartSchedule(); };
        btnCancel = NewButton("Cancel schedule", 20, 448, 260, 56, 14);
        btnCancel.BackColor = Red; btnCancel.ForeColor = Color.White; btnCancel.FlatAppearance.BorderSize = 0;
        btnCancel.Font = new Font(FontName, 14, FontStyle.Bold); btnCancel.Visible = false;
        btnCancel.Click += delegate { StopSchedule("main Cancel button"); };
        btnBubble = NewButton("Show timer", 288, 448, 132, 56, 11);
        btnBubble.Visible = false;
        btnBubble.Click += delegate { ShowBubble(); };
        lblNote = NewLabel("", 20, 512, 400, 60, 9, FontStyle.Regular); lblNote.ForeColor = Color.DimGray;
        Controls.Add(btnStart); Controls.Add(btnCancel); Controls.Add(btnBubble); Controls.Add(lblNote);

        // ---- Floating circle timer + tray icon ----
        bubble = new TimerBubble();
        bubble.CancelClicked += delegate { StopSchedule("bubble Cancel button"); };
        bubble.HideClicked += delegate { HideBubble(); };
        bubble.BodyClicked += delegate { OpenMain(); };

        tray = new NotifyIcon();
        tray.Icon = Icon; tray.Text = "Shutdown Scheduler";
        ContextMenu menu = new ContextMenu();
        menu.MenuItems.Add("Show timer", delegate { ShowBubble(); });
        menu.MenuItems.Add("Open Shutdown Scheduler", delegate { OpenMain(); });
        menu.MenuItems.Add("-");
        menu.MenuItems.Add("Cancel schedule", delegate { StopSchedule("tray Cancel"); });
        menu.MenuItems.Add("Exit", delegate { ExitApp(); });
        tray.ContextMenu = menu;
        tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowBubble(); };

        timer = new Timer(); timer.Interval = 500;
        timer.Tick += delegate { UpdateCount(); };

        actionBtns[0].Checked = true;
        UpdateActionStyle();
        FormClosing += OnClosing;
        DeleteState(); // left over from older versions that used a Windows-held timer
    }

    string SelectedAction()
    {
        foreach (RadioButton r in actionBtns) if (r.Checked) return (string)r.Tag;
        return "Shutdown";
    }

    // Light up the preset button that matches the "In" time, so the current choice is obvious
    void UpdatePresetStyle()
    {
        int total = (int)(numH.Value * 60 + numM.Value);
        for (int i = 0; i < presetBtns.Length; i++)
        {
            bool sel = radIn.Checked && presetMins[i] == total;
            presetBtns[i].BackColor = sel ? Blue : Color.White;
            presetBtns[i].FlatAppearance.BorderColor = sel ? Blue : Color.Silver;
            presetBtns[i].Invalidate();
        }
    }

    void UpdateActionStyle()
    {
        foreach (RadioButton r in actionBtns)
        {
            r.BackColor = r.Checked ? Blue : Color.White;
            r.ForeColor = r.Checked ? Color.White : Color.Black;
        }
    }

    string ActionWord()
    {
        return action == "Restart" ? "Restarting" : action == "Sleep" ? "Going to sleep" : "Shutting down";
    }

    void SetActive(bool active)
    {
        btnStart.Visible = !active; btnCancel.Visible = active; btnBubble.Visible = active;
        foreach (RadioButton r in actionBtns) r.Enabled = !active;
        foreach (Button b in presetBtns) b.Enabled = !active;
        foreach (Control c in new Control[] { radIn, numH, numM, radAt, dtp, chkForce }) c.Enabled = !active;
        tray.Visible = active;
        if (active)
        {
            lblStatus.Text = ActionWord() + " at " + target.Value.ToString("h:mm:ss tt");
            if (action == "Sleep" || force)
                lblNote.Text = "Closing this window keeps the timer running in the tray (near the clock).";
            else
                lblNote.Text = "Closing this window keeps the timer running in the tray. Apps with unsaved work will ask before closing.";
            bubble.Setup(action, target.Value, started);
        }
        else
        {
            lblStatus.Text = "Nothing scheduled"; lblCount.Text = "--:--:--";
            lblCount.ForeColor = Color.Black; lblNote.Text = "";
            bubble.Hide();
        }
    }

    static void Run(string exe, string args)
    {
        if (dryRun) { Log(exe + " " + args); return; }
        ProcessStartInfo psi = new ProcessStartInfo(exe, args);
        psi.CreateNoWindow = true; psi.UseShellExecute = false;
        try { using (Process p = Process.Start(psi)) p.WaitForExit(5000); } catch { }
    }

    void StartSchedule()
    {
        action = SelectedAction();
        DateTime when;
        if (radIn.Checked)
            when = DateTime.Now.AddSeconds((double)(numH.Value * 3600 + numM.Value * 60));
        else
        {
            DateTime t = dtp.Value;
            when = DateTime.Today.AddHours(t.Hour).AddMinutes(t.Minute);
            if (when <= DateTime.Now.AddSeconds(15)) when = when.AddDays(1);
        }
        // test hook: in --dry-run, SDS_TEST_SECS overrides the delay so the countdown can be checked quickly
        string testSecs = Environment.GetEnvironmentVariable("SDS_TEST_SECS");
        if (dryRun && !string.IsNullOrEmpty(testSecs)) when = DateTime.Now.AddSeconds(int.Parse(testSecs));
        else if ((when - DateTime.Now).TotalSeconds < 15)
        {
            MessageBox.Show("Please choose a time at least a minute from now.", "Shutdown Scheduler");
            return;
        }
        target = when;
        started = DateTime.Now;
        warned = false;
        force = chkForce.Checked;
        SetActive(true);
        timer.Start(); UpdateCount();
        ShowBubble();
        WindowState = FormWindowState.Minimized;
    }

    void StopSchedule(string why)
    {
        Log("cancelled by: " + why);
        timer.Stop();
        target = null;
        SetActive(false);
    }

    void DeleteState() { try { File.Delete(stateFile); } catch { } }

    void ShowBubble() { if (target.HasValue) bubble.ShowAtTopRight(); }

    void HideBubble()
    {
        bubble.Hide();
        if (!trayTipShown)
        {
            trayTipShown = true;
            tray.ShowBalloonTip(4000, "Timer hidden", "It is still running. Click this icon to show it again.", ToolTipIcon.Info);
        }
    }

    void OpenMain()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    void UpdateCount()
    {
        if (!target.HasValue) return;
        TimeSpan rem = target.Value - DateTime.Now;
        if (rem.TotalSeconds <= 0)
        {
            timer.Stop();
            Log("reached zero: " + action + (force ? " (force)" : " (no force)"));
            if (action == "Sleep")
            {
                target = null; SetActive(false);
                Run("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
            }
            else
            {
                lblCount.Text = "00:00:00"; lblStatus.Text = action + " in progress...";
                bubble.Tick(TimeSpan.Zero);
                // /t 0 = right now, so Windows shows no sign-out warning. Without /f, apps with
                // unsaved work get to ask the user first.
                Run("shutdown.exe", (action == "Restart" ? "/r" : "/s") + (force ? " /f" : "") + " /t 0");
            }
            return;
        }
        // In the last minute, bring the bubble back even if it was hidden, as a warning
        if (!warned && rem.TotalSeconds <= 60) { warned = true; Log("warning shown"); ShowBubble(); }
        lblCount.Text = string.Format("{0:00}:{1:00}:{2:00}", (int)Math.Floor(rem.TotalHours), rem.Minutes, rem.Seconds);
        lblCount.ForeColor = rem.TotalSeconds <= 60 ? Red : Color.Black;
        bubble.Tick(rem);
        tray.Text = ActionWord() + " in " + TimerBubble.Format(rem);
    }

    void OnClosing(object sender, FormClosingEventArgs e)
    {
        // While a timer runs, the close button just tucks the app into the tray
        if (target.HasValue && !exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            tray.ShowBalloonTip(4000, "Timer still running",
                ActionWord() + " at " + target.Value.ToString("h:mm tt") + ". Right-click this icon to cancel or exit.", ToolTipIcon.Info);
            return;
        }
        tray.Visible = false;
    }

    void ExitApp()
    {
        if (target.HasValue && MessageBox.Show("Exiting will cancel the scheduled " + action.ToLower() + ". Exit anyway?",
            "Shutdown Scheduler", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (target.HasValue) StopSchedule("Exit");
        exiting = true;
        Close();
    }
}

// Round always-on-top countdown in the top-right corner, with Cancel and Hide buttons underneath.
// Drag it to move; click the circle to open the main window.
public class TimerBubble : Form
{
    public event EventHandler CancelClicked, HideClicked, BodyClicked;

    float s;            // DPI scale
    int D;              // circle diameter
    Rectangle circle, btnCancel, btnHide;
    string action = "";
    DateTime target, started;
    TimeSpan rem;
    Point downAt, formAt;
    bool dragging, moved, userMoved;
    int hover;          // 0 none, 1 cancel, 2 hide

    public TimerBubble()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(32, 32, 32);
        using (Graphics g = CreateGraphics()) s = g.DpiX / 96f;
        D = (int)(150 * s);
        int bw = (int)(68 * s), bh = (int)(30 * s), gap = (int)(8 * s);
        circle = new Rectangle(0, 0, D, D);
        btnCancel = new Rectangle(D / 2 - bw - gap / 2, D + gap, bw, bh);
        btnHide = new Rectangle(D / 2 + gap / 2, D + gap, bw, bh);
        ClientSize = new Size(D, D + gap + bh + 1);

        GraphicsPath path = new GraphicsPath();
        path.AddEllipse(circle);
        AddPill(path, btnCancel); AddPill(path, btnHide);
        Region = new Region(path);
        Cursor = Cursors.Hand;
    }

    static void AddPill(GraphicsPath p, Rectangle r)
    {
        int d = r.Height;
        GraphicsPath pill = new GraphicsPath();
        pill.AddArc(r.X, r.Y, d, d, 90, 180);
        pill.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        pill.CloseFigure();
        p.AddPath(pill, false);
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80; /* WS_EX_TOOLWINDOW: no Alt+Tab */ return cp; }
    }

    public void Setup(string action, DateTime target, DateTime started)
    {
        this.action = action; this.target = target; this.started = started;
        rem = target - DateTime.Now;
        Invalidate();
    }

    public void ShowAtTopRight()
    {
        if (!userMoved)
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int m = (int)(16 * s);
            Location = new Point(wa.Right - Width - m, wa.Top + m);
        }
        Show();
        TopMost = true;
    }

    public static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? string.Format("{0}:{1:00}:{2:00}", (int)Math.Floor(t.TotalHours), t.Minutes, t.Seconds)
            : string.Format("{0:00}:{1:00}", t.Minutes, t.Seconds);
    }

    public void Tick(TimeSpan remaining) { rem = remaining; if (Visible) Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(BackColor);
        bool urgent = rem.TotalSeconds <= 60;
        Color accent = urgent ? MainForm.Red : MainForm.Blue;

        // progress ring: full at start, empties as time runs out
        float pw = 9 * s, inset = pw / 2 + 5 * s;
        RectangleF ring = new RectangleF(inset, inset, D - 2 * inset, D - 2 * inset);
        using (Pen track = new Pen(Color.FromArgb(70, 70, 70), pw)) g.DrawEllipse(track, ring);
        double total = Math.Max(1, (target - started).TotalSeconds);
        float frac = (float)Math.Max(0, Math.Min(1, rem.TotalSeconds / total));
        if (frac > 0)
            using (Pen p = new Pen(accent, pw)) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; g.DrawArc(p, ring, -90, 360 * frac); }

        StringFormat c = new StringFormat(); c.Alignment = StringAlignment.Center; c.LineAlignment = StringAlignment.Center;
        string time = Format(rem);
        using (Font f = new Font(MainForm.FontName, (time.Length > 5 ? 19 : 24) * s, FontStyle.Bold, GraphicsUnit.Pixel))
            g.DrawString(time, f, Brushes.White, new RectangleF(0, 0, D, D), c);
        using (Font f = new Font(MainForm.FontName, 11 * s, FontStyle.Regular, GraphicsUnit.Pixel))
        using (SolidBrush b = new SolidBrush(Color.FromArgb(190, 190, 190)))
        {
            g.DrawString(action, f, b, new RectangleF(0, D * 0.27f, D, 16 * s), c);
            g.DrawString("at " + target.ToString("h:mm tt"), f, b, new RectangleF(0, D * 0.62f, D, 16 * s), c);
        }

        DrawPill(g, btnCancel, "Cancel", hover == 1 ? Color.FromArgb(232, 72, 76) : MainForm.Red);
        DrawPill(g, btnHide, "Hide", hover == 2 ? Color.FromArgb(105, 105, 105) : Color.FromArgb(80, 80, 80));
    }

    void DrawPill(Graphics g, Rectangle r, string text, Color color)
    {
        GraphicsPath p = new GraphicsPath(); AddPill(p, r);
        using (SolidBrush b = new SolidBrush(color)) g.FillPath(b, p);
        StringFormat c = new StringFormat(); c.Alignment = StringAlignment.Center; c.LineAlignment = StringAlignment.Center;
        using (Font f = new Font(MainForm.FontName, 12 * s, FontStyle.Bold, GraphicsUnit.Pixel))
            g.DrawString(text, f, Brushes.White, r, c);
    }

    int HitButton(Point p) { return btnCancel.Contains(p) ? 1 : btnHide.Contains(p) ? 2 : 0; }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        dragging = HitButton(e.Location) == 0; moved = false;
        downAt = Cursor.Position; formAt = Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitButton(e.Location);
        if (h != hover) { hover = h; Invalidate(); }
        if (dragging && e.Button == MouseButtons.Left)
        {
            Point now = Cursor.Position;
            if (Math.Abs(now.X - downAt.X) + Math.Abs(now.Y - downAt.Y) > 4) moved = true;
            if (moved) { Location = new Point(formAt.X + now.X - downAt.X, formAt.Y + now.Y - downAt.Y); userMoved = true; }
        }
    }

    protected override void OnMouseLeave(EventArgs e) { if (hover != 0) { hover = 0; Invalidate(); } }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        bool wasDrag = dragging && moved;
        dragging = false;
        if (wasDrag) return;
        int h = HitButton(e.Location);
        if (h == 1 && CancelClicked != null) CancelClicked(this, EventArgs.Empty);
        else if (h == 2 && HideClicked != null) HideClicked(this, EventArgs.Empty);
        else if (h == 0 && circle.Contains(e.Location) && BodyClicked != null) BodyClicked(this, EventArgs.Empty);
    }
}


