using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Media;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace GtaCodeOcr
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\GtaCodeOcr_SingleInstance", out created))
            {
                if (!created) return;

                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();

                OcrEngine engine = null;
                try
                {
                    engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
                             ?? OcrEngine.TryCreateFromUserProfileLanguages();
                }
                catch { }

                if (engine == null)
                {
                    MessageBox.Show(
                        "Windows no tiene ningun idioma con OCR instalado.\n\n" +
                        "Agrega Ingles (Estados Unidos) en Configuracion > Hora e idioma > Idioma y region, " +
                        "y vuelve a abrir la aplicacion.",
                        "GtaCodeOcr", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                Application.Run(new TrayContext(engine));
            }
        }
    }

    sealed class TrayContext : ApplicationContext
    {
        const int WM_HOTKEY = 0x0312;
        const int HOTKEY_ID = 1;
        const uint VK_F8 = 0x77;

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        sealed class HotkeyWindow : NativeWindow
        {
            public Action Pressed;

            public HotkeyWindow()
            {
                // Ventana solo de mensajes (HWND_MESSAGE = -3)
                CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY && Pressed != null) Pressed();
                base.WndProc(ref m);
            }
        }

        // Separadores con guion (incluye variantes que el OCR suele devolver)
        static readonly Regex StrictCode = new Regex(
            @"(?<![A-Z0-9])([A-Z0-9]{4})\s*[-\u2010-\u2015\u2212_~]\s*([A-Z0-9]{4})\s*[-\u2010-\u2015\u2212_~]\s*([A-Z0-9]{4})(?![A-Z0-9])",
            RegexOptions.Compiled);

        // Respaldo: guiones perdidos por el OCR (se exige al menos un digito para evitar palabras sueltas)
        static readonly Regex LooseCode = new Regex(
            @"(?<![A-Z0-9])([A-Z0-9]{4})[\s.,:;]+([A-Z0-9]{4})[\s.,:;]+([A-Z0-9]{4})(?![A-Z0-9])",
            RegexOptions.Compiled);

        readonly OcrEngine engine;
        readonly HotkeyWindow window;
        readonly NotifyIcon tray;
        bool busy;

        public TrayContext(OcrEngine engine)
        {
            this.engine = engine;

            window = new HotkeyWindow();
            window.Pressed = OnHotkey;

            if (!RegisterHotKey(window.Handle, HOTKEY_ID, 0, VK_F8))
            {
                MessageBox.Show("No se pudo registrar F8. Otra aplicacion lo esta usando.",
                    "GtaCodeOcr", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(1);
            }

            var menu = new ContextMenuStrip();
            menu.Items.Add("Salir", null, (s, e) => ExitThread());
            tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "GtaCodeOcr - F8",
                ContextMenuStrip = menu,
                Visible = true
            };

            WarmUp();
        }

        protected override void ExitThreadCore()
        {
            UnregisterHotKey(window.Handle, HOTKEY_ID);
            window.DestroyHandle();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }

        // Una primera pasada con una imagen vacia para que el primer uso real sea rapido
        async void WarmUp()
        {
            try
            {
                var px = new byte[64 * 64 * 4];
                for (int i = 0; i < px.Length; i++) px[i] = 255;
                await Recognize(px, 64, 64);
            }
            catch { }
        }

        async void OnHotkey()
        {
            if (busy) return;
            busy = true;
            try
            {
                Rectangle vs = SystemInformation.VirtualScreen;
                using (var shot = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppPArgb))
                {
                    using (var g = Graphics.FromImage(shot))
                        g.CopyFromScreen(vs.Location, Point.Empty, vs.Size, CopyPixelOperation.SourceCopy);

                    Rectangle sel = SelectionForm.Pick(shot, vs);
                    if (sel.IsEmpty) return;

                    string code = await ReadCode(shot, sel);
                    if (code != null)
                    {
                        Clipboard.SetDataObject(code, true, 10, 50);
                        SystemSounds.Asterisk.Play();
                    }
                    else
                    {
                        SystemSounds.Hand.Play();
                    }
                }
            }
            catch
            {
                SystemSounds.Hand.Play();
            }
            finally
            {
                busy = false;
            }
        }

        async Task<string> ReadCode(Bitmap shot, Rectangle sel)
        {
            // Ampliar regiones pequenas mejora mucho el reconocimiento
            double scale = Math.Min(4.0, Math.Max(1.0, 100.0 / sel.Height));
            double max = OcrEngine.MaxImageDimension;
            scale = Math.Min(scale, Math.Min(max / sel.Width, max / sel.Height));
            int w = Math.Max(1, (int)(sel.Width * scale));
            int h = Math.Max(1, (int)(sel.Height * scale));

            byte[] px;
            using (var scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(scaled))
                {
                    g.Clear(Color.White); // deja el canal alfa opaco
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(shot, new Rectangle(0, 0, w, h),
                        sel.X, sel.Y, sel.Width, sel.Height, GraphicsUnit.Pixel);
                }

                BitmapData data = scaled.LockBits(new Rectangle(0, 0, w, h),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                px = new byte[data.Stride * h];
                Marshal.Copy(data.Scan0, px, 0, px.Length);
                scaled.UnlockBits(data);
            }

            string code = await Recognize(px, w, h);
            if (code != null) return code;

            // Segundo intento con colores invertidos (texto claro sobre fondo oscuro)
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = (byte)(255 - px[i]);
                px[i + 1] = (byte)(255 - px[i + 1]);
                px[i + 2] = (byte)(255 - px[i + 2]);
            }
            return await Recognize(px, w, h);
        }

        async Task<string> Recognize(byte[] px, int w, int h)
        {
            var writer = new DataWriter();
            writer.WriteBytes(px);
            using (var sb = SoftwareBitmap.CreateCopyFromBuffer(
                writer.DetachBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied))
            {
                OcrResult result = await engine.RecognizeAsync(sb);
                return ExtractCode(result.Text);
            }
        }

        static string ExtractCode(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            text = text.ToUpperInvariant();

            Match m = StrictCode.Match(text);
            if (!m.Success)
            {
                m = LooseCode.Match(text);
                if (!m.Success) return null;
                string joined = m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value;
                if (!Regex.IsMatch(joined, "[0-9]")) return null;
            }
            return m.Groups[1].Value + "-" + m.Groups[2].Value + "-" + m.Groups[3].Value;
        }
    }

    sealed class SelectionForm : Form
    {
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        const int WM_DPICHANGED = 0x02E0;

        readonly Bitmap shot;
        readonly Rectangle vs;
        Point start, cur;
        bool dragging;
        Rectangle selection = Rectangle.Empty;

        SelectionForm(Bitmap shot, Rectangle vs)
        {
            this.shot = shot;
            this.vs = vs;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            KeyPreview = true;
            Cursor = Cursors.Cross;
        }

        public static Rectangle Pick(Bitmap shot, Rectangle vs)
        {
            using (var f = new SelectionForm(shot, vs))
            {
                f.ShowDialog();
                return f.selection;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Posicion/tamano exactos en pixeles fisicos, cubriendo todos los monitores
            SetWindowPos(Handle, new IntPtr(-1), vs.X, vs.Y, vs.Width, vs.Height, 0x0040);
            Activate();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED) return; // evita que WinForms reescale la ventana
            base.WndProc(ref m);
        }

        Rectangle CurrentRect()
        {
            if (!dragging) return Rectangle.Empty;
            var r = Rectangle.FromLTRB(
                Math.Min(start.X, cur.X), Math.Min(start.Y, cur.Y),
                Math.Max(start.X, cur.X), Math.Max(start.Y, cur.Y));
            r.Intersect(new Rectangle(0, 0, shot.Width, shot.Height));
            return r;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                start = cur = e.Location;
                dragging = true;
            }
            else
            {
                Close();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!dragging) return;
            Rectangle old = CurrentRect();
            cur = e.Location;
            Rectangle now = CurrentRect();
            Rectangle area = old.IsEmpty ? now : (now.IsEmpty ? old : Rectangle.Union(old, now));
            Invalidate(Rectangle.Inflate(area, 3, 3));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!dragging || e.Button != MouseButtons.Left) return;
            cur = e.Location;
            Rectangle r = CurrentRect();
            dragging = false;
            if (r.Width >= 4 && r.Height >= 4) selection = r;
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.DrawImage(shot, 0, 0, shot.Width, shot.Height);

            Rectangle r = CurrentRect();
            using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            {
                if (!r.IsEmpty)
                {
                    g.SetClip(r, CombineMode.Exclude);
                    g.FillRectangle(dim, 0, 0, shot.Width, shot.Height);
                    g.ResetClip();
                    g.DrawRectangle(Pens.Red, r.X, r.Y, r.Width - 1, r.Height - 1);
                }
                else
                {
                    g.FillRectangle(dim, 0, 0, shot.Width, shot.Height);
                }
            }
        }
    }
}
