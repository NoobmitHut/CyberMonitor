using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using WinForms = System.Windows.Forms;

namespace CyberMonitor.Services;

/// <summary>Tray-Icon mit Neon-Kontextmenü.</summary>
internal sealed class TrayIcon : IDisposable
{
    private static readonly (string Key, string Label)[] Positions =
    {
        ("TopLeft", "Oben links (Standard)"),
        ("TopRight", "Oben rechts"),
        ("BottomRight", "Unten rechts"),
        ("BottomLeft", "Unten links"),
    };


    private readonly WinForms.NotifyIcon _notify;
    private readonly WinForms.ToolStripMenuItem _autoItem;
    private readonly WinForms.ToolStripMenuItem[] _posItems;
    private readonly WinForms.ToolStripMenuItem _sizeMenu;
    private readonly WinForms.ToolStripMenuItem[] _sizeItems;
    private readonly WinForms.ToolStripMenuItem _resizeItem;
    private readonly Icon _icon;
    private readonly AppSettings _settings;
    private readonly WinForms.ToolStripMenuItem _metricsMenu;
    private readonly WinForms.ContextMenuStrip _metricsPopup;

    public TrayIcon(AppSettings settings, Action toggle, Action<bool> setAutostart, Action<string> setPosition,
                    Action<double, double> setSize, Action toggleEditMode, Action<string> toggleMetric,
                    Action uninstall, Action exit)
    {
        _settings = settings;
        WinForms.ToolStripManager.Renderer = new NeonRenderer();
        _icon = CreateIcon();

        var menu = new WinForms.ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };

        var toggleItem = new WinForms.ToolStripMenuItem("Overlay ein/aus    Strg+Ö");
        toggleItem.Font = new Font(toggleItem.Font, FontStyle.Bold);
        toggleItem.Click += (_, _) => toggle();
        menu.Items.Add(toggleItem);
        _resizeItem = new WinForms.ToolStripMenuItem("Verschieben / Größe ändern    Strg+Shift+Ö");
        _resizeItem.Click += (_, _) => toggleEditMode();
        menu.Items.Add(_resizeItem);

        // Angezeigte Werte: als Untermenü im Tray und als eigenes Menü für den Knopf „WERTE ▾“ im Bearbeiten-Modus
        _metricsMenu = new WinForms.ToolStripMenuItem("Angezeigte Werte");
        FillMetricItems(_metricsMenu.DropDownItems, toggleMetric);
        KeepOpenOnItemClick(_metricsMenu.DropDown);
        _metricsMenu.DropDownOpening += (_, _) => RefreshMetricChecks();
        menu.Items.Add(_metricsMenu);

        _metricsPopup = new WinForms.ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
        FillMetricItems(_metricsPopup.Items, toggleMetric);
        KeepOpenOnItemClick(_metricsPopup);
        _metricsPopup.Opening += (_, _) => RefreshMetricChecks();
        menu.Items.Add(new WinForms.ToolStripSeparator());

        _autoItem = new WinForms.ToolStripMenuItem("Mit Windows starten") { Checked = settings.AutoStart };
        _autoItem.Click += (_, _) => setAutostart(!_autoItem.Checked);
        menu.Items.Add(_autoItem);

        var posMenu = new WinForms.ToolStripMenuItem("Overlay-Position");
        _posItems = Positions.Select(p =>
        {
            var item = new WinForms.ToolStripMenuItem(p.Label) { Tag = p.Key, Checked = settings.Position == p.Key };
            item.Click += (_, _) => setPosition(p.Key);
            return item;
        }).ToArray();
        posMenu.DropDownItems.AddRange(_posItems);
        menu.Items.Add(posMenu);

        _sizeMenu = new WinForms.ToolStripMenuItem("Overlay-Größe");
        _sizeItems = AppSettings.SizePresets.Select(p =>
        {
            var item = new WinForms.ToolStripMenuItem($"{p.Label}    {p.Width:0} × {p.Height:0}") { Tag = (p.Width, p.Height) };
            item.Click += (_, _) => setSize(p.Width, p.Height);
            return item;
        }).ToArray();
        _sizeMenu.DropDownItems.AddRange(_sizeItems);
        _sizeMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());
        _sizeMenu.DropDownItems.Add(new WinForms.ToolStripMenuItem("Frei: Strg+Shift+Ö, dann Ecken/Kanten ziehen") { Enabled = false });
        _sizeMenu.DropDownItems.Add(new WinForms.ToolStripMenuItem("Strg+Shift+Plus / Minus: beides ±10 %") { Enabled = false });
        menu.Items.Add(_sizeMenu);
        SetSizeChecked(settings.OverlayWidth, settings.OverlayHeight);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        var uninstallItem = new WinForms.ToolStripMenuItem("Deinstallieren ...");
        uninstallItem.Click += (_, _) => uninstall();
        menu.Items.Add(uninstallItem);
        var exitItem = new WinForms.ToolStripMenuItem("Beenden");
        exitItem.Click += (_, _) => exit();
        menu.Items.Add(exitItem);

        _notify = new WinForms.NotifyIcon
        {
            Icon = _icon,
            Text = "CyberMonitor  //  Strg+Ö",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notify.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) toggle();
        };
    }

    public void SetAutostartChecked(bool value) => _autoItem.Checked = value;

    public void SetPositionChecked(string key)
    {
        foreach (var item in _posItems) item.Checked = (string?)item.Tag == key;
    }

    public void SetEditModeChecked(bool active) => _resizeItem.Checked = active;

    /// <summary>Zeigt das Werte-Auswahlmenü an der Mausposition (für „WERTE ▾“ im Bearbeiten-Modus).</summary>
    public void ShowMetricsMenu() => _metricsPopup.Show(WinForms.Control.MousePosition);

    public void CloseMetricsMenu() => _metricsPopup.Close();

    public void RefreshMetricChecks()
    {
        foreach (var items in new[] { _metricsMenu.DropDownItems, _metricsPopup.Items })
        {
            foreach (WinForms.ToolStripItem item in items)
            {
                if (item is WinForms.ToolStripMenuItem mi && mi.Tag is string key) mi.Checked = _settings.IsShown(key);
            }
        }
    }

    private static void FillMetricItems(WinForms.ToolStripItemCollection items, Action<string> toggleMetric)
    {
        bool first = true;
        foreach (var (group, entries) in Metrics.Catalog)
        {
            if (!first) items.Add(new WinForms.ToolStripSeparator());
            first = false;
            var header = new WinForms.ToolStripMenuItem(group.ToUpperInvariant()) { Enabled = false };
            header.Font = new Font(header.Font, FontStyle.Bold);
            items.Add(header);
            foreach (var (key, label) in entries)
            {
                var item = new WinForms.ToolStripMenuItem(label) { Tag = key };
                item.Click += (_, _) => toggleMetric(key);
                items.Add(item);
            }
        }
    }

    /// <summary>Menü beim Anklicken eines Eintrags offen lassen, damit man mehrere Werte nacheinander umschalten kann.</summary>
    private static void KeepOpenOnItemClick(WinForms.ToolStripDropDown dropDown) =>
        dropDown.Closing += (_, e) =>
        {
            if (e.CloseReason == WinForms.ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
        };

    public void SetSizeChecked(double width, double height)
    {
        foreach (var item in _sizeItems)
            item.Checked = item.Tag is ValueTuple<double, double> t && Math.Abs(t.Item1 - width) < 1 && Math.Abs(t.Item2 - height) < 1;
        _sizeMenu.Text = $"Overlay-Größe ({width:0} × {height:0})";
    }

    public void Balloon(string title, string text, bool warning = false) =>
        _notify.ShowBalloonTip(4000, title, text, warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
        _metricsPopup.Dispose();
        _icon.Dispose();
    }

    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var shape = new[]
            {
                new Point(8, 1), new Point(30, 1), new Point(30, 24),
                new Point(24, 30), new Point(1, 30), new Point(1, 8),
            };
            using var bg = new SolidBrush(Color.FromArgb(255, 14, 9, 26));
            using var cyan = new Pen(Color.FromArgb(0, 240, 255), 2f);
            using var magenta = new Pen(Color.FromArgb(255, 42, 109), 2.6f) { LineJoin = LineJoin.Miter };
            g.FillPolygon(bg, shape);
            g.DrawPolygon(cyan, shape);
            g.DrawLines(magenta, new[]
            {
                new Point(5, 18), new Point(11, 18), new Point(14, 8),
                new Point(18, 24), new Point(21, 14), new Point(27, 14),
            });
        }

        IntPtr handle = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(handle);
            return (Icon)tmp.Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }

    private sealed class NeonRenderer : WinForms.ToolStripProfessionalRenderer
    {
        private static readonly Color Cyan = Color.FromArgb(0, 240, 255);
        private static readonly Color Ink = Color.FromArgb(11, 6, 20);

        public NeonRenderer() : base(new NeonColors()) => RoundedEdges = false;

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Ink : Cyan;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(WinForms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item != null && e.Item.Selected ? Ink : Cyan;
            base.OnRenderArrow(e);
        }
    }

    private sealed class NeonColors : WinForms.ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(14, 9, 26);
        private static readonly Color Cyan = Color.FromArgb(0, 240, 255);
        private static readonly Color Magenta = Color.FromArgb(255, 42, 109);

        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuBorder => Magenta;
        public override Color MenuItemBorder => Cyan;
        public override Color MenuItemSelected => Cyan;
        public override Color MenuItemSelectedGradientBegin => Cyan;
        public override Color MenuItemSelectedGradientEnd => Cyan;
        public override Color MenuItemPressedGradientBegin => Bg;
        public override Color MenuItemPressedGradientEnd => Bg;
        public override Color SeparatorDark => Color.FromArgb(70, 0, 240, 255);
        public override Color SeparatorLight => Bg;
        public override Color CheckBackground => Magenta;
        public override Color CheckSelectedBackground => Magenta;
        public override Color CheckPressedBackground => Magenta;
    }
}
