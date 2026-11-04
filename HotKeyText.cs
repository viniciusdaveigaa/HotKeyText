// HotKeyText — aperte Scroll Lock e depois a tecla do texto (1-9, 0, A-Z) para inseri-lo.
// Compilado com o compilador C# nativo do Windows (csc.exe), sem dependências externas.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;
using Microsoft.Win32;

[assembly: AssemblyTitle("HotKeyText")]
[assembly: AssemblyDescription("Atalho rápido de textos via Scroll Lock com atualização automática pelo GitHub")]
[assembly: AssemblyProduct("HotKeyText")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

public class TextoItem
{
    public string Titulo = "";
    public string Conteudo = "";
}

public class Config
{
    public bool ModoColar = false;
    public string GithubRepo = "";
    public List<TextoItem> Textos = new List<TextoItem>();
}

static class Nativo
{
    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }

    // MOUSEINPUT garante o tamanho exato de INPUT (40 bytes em 64 bits)
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint tid);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
}

static class App
{
    public const string VERSAO = "1.0.0";
    public static string REPO_PADRAO = "viniciusdaveigaa/HotKeyText"; // pode ser preenchido ou lido de repo.txt na pasta do programa

    public static Config Cfg = new Config();
    public static string PastaDados, ArquivoDados;
    public static PopupForm Popup;
    public static Icon Icone;
    static GerenciadorForm gerenciador;
    static NotifyIcon tray;

    static IntPtr kbHook = IntPtr.Zero, msHook = IntPtr.Zero;
    static Nativo.HookProc kbProc = KbCallback;
    static Nativo.HookProc msProc = MsCallback;
    public static bool PopupAberto = false;
    static bool scrollPressionado = false;
    static HashSet<uint> engolir = new HashSet<uint>();
    static int digitando = 0;

    static readonly IntPtr MARCA = new IntPtr(0x484B5458); // 'HKTX'
    const string NOME_RUN = "HotKeyText";
    const string CHAVE_RUN = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [STAThread]
    static void Main(string[] args)
    {
        // Modo auxiliar de auto-atualização: substitui o executável antigo sem usar scripts .bat/.ps1
        int idxUpd = Array.IndexOf(args, "--apply-update");
        if (idxUpd >= 0 && idxUpd + 1 < args.Length)
        {
            AplicarAtualizacao(args[idxUpd + 1]);
            return;
        }

        // Remove arquivo temporário de atualização anterior, se existir
        try
        {
            string oldUpd = Application.ExecutablePath + ".update.exe";
            if (File.Exists(oldUpd)) File.Delete(oldUpd);
        }
        catch { }

        bool primeiro;
        using (Mutex mutex = new Mutex(true, "HotKeyText_InstanciaUnica", out primeiro))
        {
            EventWaitHandle sinal = new EventWaitHandle(false, EventResetMode.AutoReset, "HotKeyText_AbrirGerenciador");
            if (!primeiro)
            {
                Nativo.AllowSetForegroundWindow(-1);
                sinal.Set();
                return;
            }

            bool naBandeja = Array.IndexOf(args, "/bandeja") >= 0;
            try { Nativo.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            PastaDados = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HotKeyText");
            ArquivoDados = Path.Combine(PastaDados, "textos.xml");
            MigrarDadosAntigos();

            bool primeiraVez = !File.Exists(ArquivoDados);
            Carregar();
            if (primeiraVez) { try { IniciaComWindows = true; } catch { } }
            LimparRegistroAntigo();

            Icone = CriarIcone();
            Popup = new PopupForm();
            IntPtr forcarHandle = Popup.Handle;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Gerenciar textos (HotKeyText v" + VERSAO + ")", null, delegate { AbrirGerenciador(); });
            menu.Items.Add("Procurar Atualização...", null, delegate { AbrirGerenciador(); gerenciador.ProcurarAtualizacao(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Sair", null, delegate { Application.Exit(); });
            tray = new NotifyIcon();
            tray.Icon = Icone;
            tray.Text = "HotKeyText v" + VERSAO + " (Scroll Lock)";
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) AbrirGerenciador(); };
            tray.Visible = true;

            kbHook = Nativo.SetWindowsHookEx(13, kbProc, Nativo.GetModuleHandle(null), 0);
            if (kbHook == IntPtr.Zero)
            {
                Log("falha ao instalar hook de teclado, erro=" + Marshal.GetLastWin32Error());
                MessageBox.Show("Não foi possível monitorar o teclado.", "HotKeyText", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            Thread escuta = new Thread(delegate()
            {
                while (true)
                {
                    sinal.WaitOne();
                    if (!Postar(AbrirGerenciador)) return;
                }
            });
            escuta.IsBackground = true;
            escuta.Start();

            if (!naBandeja) AbrirGerenciador();

            Application.Run();

            DesligarMouse();
            if (kbHook != IntPtr.Zero) Nativo.UnhookWindowsHookEx(kbHook);
            tray.Visible = false;
            tray.Dispose();
        }
    }

    static void AplicarAtualizacao(string destinoExe)
    {
        for (int tentativa = 0; tentativa < 40; tentativa++)
        {
            try
            {
                File.Copy(Application.ExecutablePath, destinoExe, true);
                break;
            }
            catch
            {
                Thread.Sleep(250);
            }
        }
        try
        {
            Process.Start(new ProcessStartInfo(destinoExe) { WorkingDirectory = Path.GetDirectoryName(destinoExe) });
        }
        catch { }
    }

    static void MigrarDadosAntigos()
    {
        try
        {
            if (File.Exists(ArquivoDados)) return;
            string pastaAntiga = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TextosRapidos");
            string arqAntigo = Path.Combine(pastaAntiga, "textos.xml");
            if (File.Exists(arqAntigo))
            {
                Directory.CreateDirectory(PastaDados);
                File.Copy(arqAntigo, ArquivoDados, true);
            }
        }
        catch { }
    }

    static void LimparRegistroAntigo()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(CHAVE_RUN))
            {
                if (k != null && k.GetValue("TextosRapidos") != null)
                {
                    k.DeleteValue("TextosRapidos", false);
                    k.SetValue(NOME_RUN, "\"" + Application.ExecutablePath + "\" /bandeja");
                }
            }
        }
        catch { }
    }

    public static string ObterRepoEfetivo()
    {
        if (!string.IsNullOrEmpty(Cfg.GithubRepo) && Cfg.GithubRepo.Trim().Length > 0)
            return NormalizarRepo(Cfg.GithubRepo);
        try
        {
            string arqRepo = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "repo.txt");
            if (File.Exists(arqRepo))
            {
                string lido = File.ReadAllText(arqRepo, Encoding.UTF8).Trim();
                if (lido.Length > 0) return NormalizarRepo(lido);
            }
        }
        catch { }
        return NormalizarRepo(REPO_PADRAO);
    }

    public static string NormalizarRepo(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim();
        s = Regex.Replace(s, @"^https?://(www\.)?github\.com/", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\.git$", "", RegexOptions.IgnoreCase);
        return s.Trim('/');
    }

    // ---------- dados ----------

    static void Carregar()
    {
        if (File.Exists(ArquivoDados))
        {
            try
            {
                XmlSerializer xs = new XmlSerializer(typeof(Config));
                using (FileStream fs = File.OpenRead(ArquivoDados)) Cfg = (Config)xs.Deserialize(fs);
                if (Cfg.Textos == null) Cfg.Textos = new List<TextoItem>();
                if (Cfg.GithubRepo == null) Cfg.GithubRepo = "";
                return;
            }
            catch (Exception ex)
            {
                Log("erro ao ler textos: " + ex.Message);
                try { File.Copy(ArquivoDados, ArquivoDados + ".erro", true); } catch { }
                MessageBox.Show("O arquivo de textos estava corrompido. Uma cópia foi salva como textos.xml.erro.",
                    "HotKeyText", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        Cfg = new Config();
        TextoItem assinatura = new TextoItem();
        assinatura.Titulo = "Assinatura";
        assinatura.Conteudo = "Atenciosamente,\r\nVinícius.";
        Cfg.Textos.Add(assinatura);
        Salvar();
    }

    public static void Salvar()
    {
        try
        {
            Directory.CreateDirectory(PastaDados);
            string tmp = ArquivoDados + ".tmp";
            XmlSerializer xs = new XmlSerializer(typeof(Config));
            using (FileStream fs = File.Create(tmp)) xs.Serialize(fs, Cfg);
            if (File.Exists(ArquivoDados)) File.Replace(tmp, ArquivoDados, null);
            else File.Move(tmp, ArquivoDados);
        }
        catch (Exception ex) { Log("erro ao salvar: " + ex.Message); }
    }

    public static bool IniciaComWindows
    {
        get
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(CHAVE_RUN))
                return k != null && k.GetValue(NOME_RUN) != null;
        }
        set
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(CHAVE_RUN))
            {
                if (value) k.SetValue(NOME_RUN, "\"" + Application.ExecutablePath + "\" /bandeja");
                else k.DeleteValue(NOME_RUN, false);
            }
        }
    }

    public static void Log(string s)
    {
        try
        {
            Directory.CreateDirectory(PastaDados);
            File.AppendAllText(Path.Combine(PastaDados, "log.txt"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + s + "\r\n");
        }
        catch { }
    }

    // ---------- teclas ----------

    public static int IndiceDaTecla(uint vk)
    {
        if (vk >= 0x31 && vk <= 0x39) return (int)(vk - 0x31);     // 1-9
        if (vk == 0x30) return 9;                                  // 0
        if (vk >= 0x61 && vk <= 0x69) return (int)(vk - 0x61);     // teclado numérico 1-9
        if (vk == 0x60) return 9;                                  // teclado numérico 0
        if (vk >= 0x41 && vk <= 0x5A) return 10 + (int)(vk - 0x41); // A-Z
        return -1;
    }

    public static string RotuloDoIndice(int i)
    {
        if (i < 9) return (i + 1).ToString();
        if (i == 9) return "0";
        if (i < 36) return ((char)('A' + i - 10)).ToString();
        return "";
    }

    public static string TituloDe(TextoItem t)
    {
        if (!string.IsNullOrEmpty(t.Titulo) && t.Titulo.Trim().Length > 0) return t.Titulo.Trim();
        string c = (t.Conteudo ?? "").Trim();
        if (c.Length == 0) return "(sem título)";
        int q = c.IndexOfAny(new char[] { '\r', '\n' });
        return q > 0 ? c.Substring(0, q) : c;
    }

    public static string ResumoDe(TextoItem t)
    {
        string c = (t.Conteudo ?? "").Trim();
        if (c.Length == 0) return "(vazio)";
        if (c.Length > 300) c = c.Substring(0, 300);
        return c.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "  ↵  ").Replace("\t", " ");
    }

    static bool EhModificador(uint vk)
    {
        return vk == 0x10 || vk == 0x11 || vk == 0x12 || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C || vk == 0x14;
    }

    static bool Postar(Action a)
    {
        try { Popup.BeginInvoke(a); return true; } catch { return false; }
    }

    static IntPtr KbCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            Nativo.KBDLLHOOKSTRUCT k = (Nativo.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Nativo.KBDLLHOOKSTRUCT));
            if (k.extra != MARCA)
            {
                int msg = wParam.ToInt32();
                bool down = msg == 0x100 || msg == 0x104;
                bool up = msg == 0x101 || msg == 0x105;
                uint vk = k.vkCode;

                if (vk == 0x91) // Scroll Lock: abre/fecha a lista
                {
                    if (down && !scrollPressionado)
                    {
                        scrollPressionado = true;
                        if (PopupAberto) FecharPopup(); else AbrirPopup();
                    }
                    else if (up) scrollPressionado = false;
                    return (IntPtr)1;
                }

                if (up && engolir.Remove(vk)) return (IntPtr)1;
                if (down && engolir.Contains(vk)) return (IntPtr)1;

                if (PopupAberto && down && !EhModificador(vk))
                {
                    engolir.Add(vk);
                    TeclaNoPopup(vk);
                    return (IntPtr)1;
                }
            }
        }
        return Nativo.CallNextHookEx(kbHook, nCode, wParam, lParam);
    }

    static void TeclaNoPopup(uint vk)
    {
        if (vk == 0x1B) { FecharPopup(); return; }
        if (vk == 0x26) { Postar(delegate { Popup.Mover(-1); }); return; }
        if (vk == 0x28) { Postar(delegate { Popup.Mover(1); }); return; }
        if (vk == 0x0D) { if (Popup.Destaque >= 0) Escolher(Popup.Destaque); return; }
        int idx = IndiceDaTecla(vk);
        if (idx >= 0 && idx < Cfg.Textos.Count) Escolher(idx);
    }

    static IntPtr MsCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && PopupAberto)
        {
            int msg = wParam.ToInt32();
            if (msg == 0x201 || msg == 0x204 || msg == 0x207 || msg == 0x20B)
            {
                Nativo.MSLLHOOKSTRUCT m = (Nativo.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Nativo.MSLLHOOKSTRUCT));
                if (!Popup.Bounds.Contains(m.pt.x, m.pt.y)) FecharPopup();
            }
        }
        return Nativo.CallNextHookEx(msHook, nCode, wParam, lParam);
    }

    public static void LigarMouse()
    {
        if (msHook == IntPtr.Zero) msHook = Nativo.SetWindowsHookEx(14, msProc, Nativo.GetModuleHandle(null), 0);
    }

    public static void DesligarMouse()
    {
        if (msHook != IntPtr.Zero) { Nativo.UnhookWindowsHookEx(msHook); msHook = IntPtr.Zero; }
    }

    // ---------- ações ----------

    static void AbrirPopup()
    {
        PopupAberto = true;
        Postar(Popup.Mostrar);
    }

    public static void FecharPopup()
    {
        PopupAberto = false;
        Postar(Popup.Esconder);
    }

    public static void Escolher(int idx)
    {
        PopupAberto = false;
        Postar(delegate { Inserir(idx); });
    }

    static void Inserir(int idx)
    {
        Popup.Esconder();
        if (idx < 0 || idx >= Cfg.Textos.Count) return;
        string texto = Cfg.Textos[idx].Conteudo ?? "";
        if (texto.Length == 0) return;
        if (Cfg.ModoColar) Colar(texto);
        else ThreadPool.QueueUserWorkItem(delegate { Digitar(texto); });
    }

    public static void AbrirGerenciador()
    {
        if (PopupAberto) FecharPopup();
        if (gerenciador == null || gerenciador.IsDisposed) gerenciador = new GerenciadorForm();
        gerenciador.Show();
        if (gerenciador.WindowState == FormWindowState.Minimized) gerenciador.WindowState = FormWindowState.Normal;
        gerenciador.Activate();
        Nativo.SetForegroundWindow(gerenciador.Handle);
    }

    static Nativo.INPUT Tecla(ushort vk, ushort scan, uint flags)
    {
        Nativo.INPUT i = new Nativo.INPUT();
        i.type = 1;
        i.u.ki.wVk = vk;
        i.u.ki.wScan = scan;
        i.u.ki.dwFlags = flags;
        i.u.ki.extra = MARCA;
        return i;
    }

    static void Digitar(string texto)
    {
        if (Interlocked.Exchange(ref digitando, 1) == 1) return;
        try
        {
            const uint UP = 2, UNI = 4;
            List<Nativo.INPUT> l = new List<Nativo.INPUT>();
            foreach (char c in texto)
            {
                if (c == '\r') continue;
                if (c == '\n')
                {
                    l.Add(Tecla(0x10, 0, 0)); l.Add(Tecla(0x0D, 0, 0));
                    l.Add(Tecla(0x0D, 0, UP)); l.Add(Tecla(0x10, 0, UP));
                }
                else if (c == '\t')
                {
                    l.Add(Tecla(0x09, 0, 0)); l.Add(Tecla(0x09, 0, UP));
                }
                else
                {
                    l.Add(Tecla(0, c, UNI)); l.Add(Tecla(0, c, UNI | UP));
                }
            }
            uint r = Nativo.SendInput((uint)l.Count, l.ToArray(), Marshal.SizeOf(typeof(Nativo.INPUT)));
            if (r != l.Count) Log("SendInput enviou " + r + " de " + l.Count + ", erro=" + Marshal.GetLastWin32Error());
        }
        finally { digitando = 0; }
    }

    static void Colar(string texto)
    {
        texto = texto.Replace("\r\n", "\n").Replace("\n", "\r\n");
        DataObject backup = null;
        try
        {
            IDataObject atual = Clipboard.GetDataObject();
            if (atual != null)
            {
                backup = new DataObject();
                foreach (string f in atual.GetFormats(false))
                {
                    try { object v = atual.GetData(f, false); if (v != null) backup.SetData(f, false, v); } catch { }
                }
            }
        }
        catch { backup = null; }

        try { Clipboard.SetDataObject(texto, true, 10, 50); }
        catch { ThreadPool.QueueUserWorkItem(delegate { Digitar(texto); }); return; }

        ThreadPool.QueueUserWorkItem(delegate
        {
            Nativo.INPUT[] i = { Tecla(0x11, 0, 0), Tecla(0x56, 0, 0), Tecla(0x56, 0, 2), Tecla(0x11, 0, 2) };
            Nativo.SendInput((uint)i.Length, i, Marshal.SizeOf(typeof(Nativo.INPUT)));
        });

        if (backup != null && backup.GetFormats().Length > 0)
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 700;
            t.Tick += delegate
            {
                t.Stop(); t.Dispose();
                try { Clipboard.SetDataObject(backup, true, 10, 50); } catch { }
            };
            t.Start();
        }
    }

    // ---------- visual e logo ----------

    public static GraphicsPath Arredondado(RectangleF r, float raio)
    {
        GraphicsPath p = new GraphicsPath();
        float d = raio * 2f;
        if (d <= 0 || d > r.Width || d > r.Height) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Bitmap DesenharLogo(int size)
    {
        Bitmap bmp = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            float pad = size * 0.05f;
            float w = size - pad * 2f;
            using (GraphicsPath baseP = Arredondado(new RectangleF(pad, pad, w, w), size * 0.22f))
            using (LinearGradientBrush brBase = new LinearGradientBrush(
                new PointF(0, 0), new PointF(size, size),
                Color.FromArgb(14, 116, 224), Color.FromArgb(0, 62, 148)))
            {
                g.FillPath(brBase, baseP);
            }

            float inPad = size * 0.125f;
            float inW = size - inPad * 2f;
            float inH = size - inPad * 2.15f;
            float yTop = inPad * 0.85f;
            using (GraphicsPath topP = Arredondado(new RectangleF(inPad, yTop, inW, inH), size * 0.16f))
            using (LinearGradientBrush brTop = new LinearGradientBrush(
                new PointF(0, inPad), new PointF(0, inPad + inH),
                Color.FromArgb(42, 152, 255), Color.FromArgb(8, 108, 218)))
            using (Pen penBorda = new Pen(Color.FromArgb(100, 255, 255, 255), Math.Max(1f, size * 0.02f)))
            {
                g.FillPath(brTop, topP);
                g.DrawPath(penBorda, topP);
            }

            using (Font f = new Font("Segoe UI", size * 0.44f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString("H", f, Brushes.White, new RectangleF(inPad, yTop, inW * 0.82f, inH), sf);
            }

            float cx = inPad + inW * 0.77f;
            float cy1 = yTop + inH * 0.27f;
            float cy2 = yTop + inH * 0.73f;
            using (Pen penCur = new Pen(Color.FromArgb(125, 235, 255), Math.Max(2f, size * 0.06f)))
            {
                penCur.StartCap = LineCap.Round;
                penCur.EndCap = LineCap.Round;
                g.DrawLine(penCur, cx, cy1, cx, cy2);
            }
        }
        return bmp;
    }

    static Icon CriarIcone()
    {
        try
        {
            Icon icExe = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icExe != null) return icExe;
        }
        catch { }

        using (Bitmap b = DesenharLogo(32))
        {
            IntPtr h = b.GetHicon();
            Icon ic = (Icon)Icon.FromHandle(h).Clone();
            Nativo.DestroyIcon(h);
            return ic;
        }
    }
}

// ---------- Sistema de Atualização via GitHub Releases ----------

static class Atualizador
{
    public class ResultadoUpdate
    {
        public bool Sucesso;
        public bool TemNovaVersao;
        public string VersaoRemota = "";
        public string UrlDownload = "";
        public string Notas = "";
        public string Erro = "";
    }

    public static ResultadoUpdate Verificar(string repo)
    {
        ResultadoUpdate res = new ResultadoUpdate();
        repo = App.NormalizarRepo(repo);
        if (string.IsNullOrEmpty(repo) || !repo.Contains("/"))
        {
            res.Erro = "Informe o repositório GitHub no formato usuario/HotKeyText.";
            return res;
        }

        try
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768; // TLS 1.2 / 1.1
            string url = "https://api.github.com/repos/" + repo + "/releases/latest";
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "HotKeyText-Updater/" + App.VERSAO;
            req.Accept = "application/vnd.github.v3+json";
            req.Timeout = 10000;

            string json;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                json = sr.ReadToEnd();

            Match mTag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            if (!mTag.Success)
            {
                res.Erro = "Nenhuma release encontrada no repositório.";
                return res;
            }

            string tag = mTag.Groups[1].Value.Trim();
            res.VersaoRemota = tag.TrimStart('v', 'V');

            // Procura o asset HotKeyText.exe (ou qualquer .exe anexado à release)
            MatchCollection mUrls = Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.exe)\"", RegexOptions.IgnoreCase);
            foreach (Match m in mUrls)
            {
                string u = m.Groups[1].Value;
                res.UrlDownload = u;
                if (u.EndsWith("/HotKeyText.exe", StringComparison.OrdinalIgnoreCase)) break;
            }

            res.Sucesso = true;
            res.TemNovaVersao = EhVersaoMaior(res.VersaoRemota, App.VERSAO);
            return res;
        }
        catch (WebException wex)
        {
            HttpWebResponse hr = wex.Response as HttpWebResponse;
            if (hr != null && hr.StatusCode == HttpStatusCode.NotFound)
                res.Erro = "Repositório '" + repo + "' ainda não possui nenhuma Release publicada no GitHub.";
            else
                res.Erro = "Falha ao conectar ao GitHub: " + wex.Message;
            return res;
        }
        catch (Exception ex)
        {
            res.Erro = "Erro ao verificar atualização: " + ex.Message;
            return res;
        }
    }

    static bool EhVersaoMaior(string remota, string local)
    {
        try
        {
            Version vr = new Version(NormalizarVersao(remota));
            Version vl = new Version(NormalizarVersao(local));
            return vr > vl;
        }
        catch
        {
            return !string.Equals(remota, local, StringComparison.OrdinalIgnoreCase);
        }
    }

    static string NormalizarVersao(string v)
    {
        v = Regex.Replace(v ?? "0.0.0", @"[^0-9\.]", "");
        string[] p = v.Split(new char[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 1) return p[0] + ".0.0";
        if (p.Length == 2) return p[0] + "." + p[1] + ".0";
        return v;
    }

    public static void BaixarEAplicar(string urlDownload)
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768;
        string exeAtual = Application.ExecutablePath;
        string exeUpdate = exeAtual + ".update.exe";

        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(urlDownload);
        req.UserAgent = "HotKeyText-Updater/" + App.VERSAO;
        req.Timeout = 20000;

        using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
        using (Stream sIn = resp.GetResponseStream())
        using (FileStream sOut = File.Create(exeUpdate))
        {
            byte[] buf = new byte[16384];
            int lidos;
            while ((lidos = sIn.Read(buf, 0, buf.Length)) > 0)
                sOut.Write(buf, 0, lidos);
        }

        // Verifica assinatura MZ mínima de executável válido
        byte[] cab = new byte[2];
        using (FileStream fs = File.OpenRead(exeUpdate)) fs.Read(cab, 0, 2);
        if (cab[0] != 'M' || cab[1] != 'Z')
        {
            File.Delete(exeUpdate);
            throw new Exception("O arquivo baixado não é um executável válido.");
        }

        App.Salvar();
        ProcessStartInfo psi = new ProcessStartInfo(exeUpdate, "--apply-update \"" + exeAtual + "\"");
        psi.WorkingDirectory = Path.GetDirectoryName(exeAtual);
        Process.Start(psi);
        Application.Exit();
    }
}

// ---------- Lista flutuante do Scroll Lock ----------

class PopupForm : Form
{
    float S = 1f;
    int linhaH, colW, cabecalhoH, rodapeH, pad, porColuna, colunas;
    List<Rectangle> linhas = new List<Rectangle>();
    Rectangle rGerenciar;
    public int Destaque = -1;
    Font fCab, fTitulo, fPrev, fBadge, fRodape;
    Bitmap bmpLogo;

    static readonly Color Fundo = Color.FromArgb(32, 33, 38);
    static readonly Color Hover = Color.FromArgb(54, 56, 64);
    static readonly Color Borda = Color.FromArgb(72, 74, 84);
    static readonly Color Azul = Color.FromArgb(0, 120, 212);
    static readonly Color Cinza = Color.FromArgb(165, 168, 178);
    static readonly Color Link = Color.FromArgb(96, 170, 255);

    public PopupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Fundo;
        Text = "HotKeyText";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        using (Graphics g = CreateGraphics()) S = g.DpiX / 96f;
        fCab = new Font("Segoe UI Semibold", 10.5f);
        fTitulo = new Font("Segoe UI Semibold", 10f);
        fPrev = new Font("Segoe UI", 9f);
        fBadge = new Font("Segoe UI Semibold", 10.5f);
        fRodape = new Font("Segoe UI", 8.5f);
        bmpLogo = App.DesenharLogo(P(22));
        try { int v = 2; Nativo.DwmSetWindowAttribute(Handle, 33, ref v, 4); } catch { }
    }

    int P(float v) { return (int)Math.Round(v * S); }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; // NOACTIVATE | TOOLWINDOW | TOPMOST
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0021) { m.Result = (IntPtr)3; return; }
        base.WndProc(ref m);
    }

    public void Mostrar()
    {
        if (!App.PopupAberto) return;
        List<TextoItem> t = App.Cfg.Textos;
        pad = P(10); linhaH = P(48); colW = P(420); cabecalhoH = P(40); rodapeH = P(36);

        IntPtr fg = Nativo.GetForegroundWindow();
        Screen tela = fg != IntPtr.Zero ? Screen.FromHandle(fg) : Screen.FromPoint(Cursor.Position);
        Rectangle wa = tela.WorkingArea;

        int n = Math.Max(1, t.Count);
        int maxLinhas = Math.Max(1, (int)((wa.Height * 0.8 - cabecalhoH - rodapeH - pad) / linhaH));
        colunas = (n + maxLinhas - 1) / maxLinhas;
        porColuna = (n + colunas - 1) / colunas;
        int w = colunas * colW + 2 * pad;
        int h = cabecalhoH + porColuna * linhaH + rodapeH + P(4);

        linhas.Clear();
        for (int i = 0; i < t.Count; i++)
        {
            int c = i / porColuna, l = i % porColuna;
            linhas.Add(new Rectangle(pad + c * colW, cabecalhoH + l * linhaH, colW - (colunas > 1 ? P(6) : 0), linhaH - P(4)));
        }
        Destaque = t.Count > 0 ? 0 : -1;

        SetBounds(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h);
        Show();
        Nativo.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
        Invalidate();
        App.LigarMouse();
    }

    public void Esconder()
    {
        App.DesligarMouse();
        if (Visible) Hide();
    }

    public void Mover(int d)
    {
        int n = App.Cfg.Textos.Count;
        if (n == 0) return;
        Destaque = Destaque < 0 ? (d > 0 ? 0 : n - 1) : (Destaque + d + n) % n;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Fundo);
        TextFormatFlags esq = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextFormatFlags centro = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

        if (bmpLogo != null)
            g.DrawImage(bmpLogo, pad + P(8), (cabecalhoH - bmpLogo.Height) / 2);

        TextRenderer.DrawText(g, "HotKeyText", fCab, new Rectangle(pad + P(36), 0, Width - 2 * pad, cabecalhoH), Color.White, esq);

        List<TextoItem> t = App.Cfg.Textos;
        if (t.Count == 0)
            TextRenderer.DrawText(g, "Nenhum texto cadastrado. Clique em Gerenciar.", fPrev,
                new Rectangle(pad + P(8), cabecalhoH, Width - 2 * pad, linhaH), Cinza, esq);

        for (int i = 0; i < t.Count && i < linhas.Count; i++)
        {
            Rectangle r = linhas[i];
            if (i == Destaque)
                using (GraphicsPath p = App.Arredondado(r, P(6)))
                using (SolidBrush b = new SolidBrush(Hover))
                    g.FillPath(b, p);

            int bs = P(28);
            Rectangle rb = new Rectangle(r.X + P(8), r.Y + (r.Height - bs) / 2, bs, bs);
            string rot = App.RotuloDoIndice(i);
            using (GraphicsPath p = App.Arredondado(rb, P(5)))
            using (SolidBrush b = new SolidBrush(rot.Length > 0 ? Azul : Borda))
                g.FillPath(b, p);
            TextRenderer.DrawText(g, rot, fBadge, rb, Color.White, centro);

            int tx = rb.Right + P(12);
            int tw = r.Right - tx - P(8);
            TextRenderer.DrawText(g, App.TituloDe(t[i]), fTitulo, new Rectangle(tx, r.Y + P(3), tw, r.Height / 2 - P(2)), Color.White, esq);
            TextRenderer.DrawText(g, App.ResumoDe(t[i]), fPrev, new Rectangle(tx, r.Y + r.Height / 2, tw, r.Height / 2 - P(3)), Cinza, esq);
        }

        int fy = Height - rodapeH;
        using (Pen pen = new Pen(Borda)) g.DrawLine(pen, pad, fy, Width - pad, fy);
        Size sz = TextRenderer.MeasureText("Gerenciar", fRodape);
        rGerenciar = new Rectangle(Width - pad - P(8) - sz.Width, fy, sz.Width, rodapeH);
        TextRenderer.DrawText(g, "Gerenciar", fRodape, rGerenciar, Link, centro);
        TextRenderer.DrawText(g, "Tecla do texto, Enter ou clique para inserir  •  Esc fecha", fRodape,
            new Rectangle(pad + P(8), fy, rGerenciar.X - pad - P(16), rodapeH), Cinza, esq);

        using (Pen pen = new Pen(Borda)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    int Acertar(Point p)
    {
        for (int i = 0; i < linhas.Count; i++) if (linhas[i].Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = Acertar(e.Location);
        Cursor = (i >= 0 || rGerenciar.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
        if (i >= 0 && i != Destaque) { Destaque = i; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (rGerenciar.Contains(e.Location)) { App.FecharPopup(); App.AbrirGerenciador(); return; }
        int i = Acertar(e.Location);
        if (i >= 0) App.Escolher(i);
    }
}

// ---------- Janela de gerenciamento do HotKeyText ----------

class GerenciadorForm : Form
{
    float S = 1f;
    ListBox lista;
    TextBox txtTitulo, txtConteudo, txtRepo;
    Button bNovo, bExcluir, bSubir, bDescer, bAtualizar;
    Label lblStatusUpdate;
    RadioButton rDigitar, rColar;
    CheckBox chkIniciar;
    System.Windows.Forms.Timer timerSalvar;
    bool carregando;
    Font fItem, fItemPrev, fBadge, fNegrito, fHeader;

    static readonly Color Azul = Color.FromArgb(0, 120, 212);
    static readonly Color Selecao = Color.FromArgb(229, 241, 251);
    static readonly Color CinzaTexto = Color.FromArgb(110, 110, 120);

    int P(float v) { return (int)Math.Round(v * S); }

    public GerenciadorForm()
    {
        using (Graphics g = CreateGraphics()) S = g.DpiX / 96f;
        AutoScaleMode = AutoScaleMode.None;
        Text = "HotKeyText v" + App.VERSAO;
        Icon = App.Icone;
        Font = new Font("Segoe UI", 9.5f);
        fNegrito = new Font("Segoe UI Semibold", 9.5f);
        fHeader = new Font("Segoe UI Semibold", 13f);
        fItem = new Font("Segoe UI Semibold", 9.5f);
        fItemPrev = new Font("Segoe UI", 8.5f);
        fBadge = new Font("Segoe UI Semibold", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(P(900), P(620));
        MinimumSize = new Size(P(700), P(480));
        BackColor = Color.White;

        SuspendLayout();

        TableLayoutPanel raiz = new TableLayoutPanel();
        raiz.Dock = DockStyle.Fill;
        raiz.ColumnCount = 2;
        raiz.RowCount = 3;
        raiz.Padding = new Padding(P(14));
        raiz.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, P(300)));
        raiz.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        raiz.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // cabeçalho com logo e botão de update
        raiz.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        raiz.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // ----- barra superior: Logo + Nome + Versão + Atualização GitHub -----
        Panel topo = new Panel();
        topo.Dock = DockStyle.Fill;
        topo.Height = P(46);
        topo.Margin = new Padding(0, 0, 0, P(10));

        PictureBox picLogo = new PictureBox();
        picLogo.Image = App.DesenharLogo(P(36));
        picLogo.SizeMode = PictureBoxSizeMode.AutoSize;
        picLogo.Location = new Point(0, P(4));

        Label lblNomeApp = new Label();
        lblNomeApp.Text = "HotKeyText";
        lblNomeApp.Font = fHeader;
        lblNomeApp.AutoSize = true;
        lblNomeApp.Location = new Point(P(44), P(2));

        Label lblVersaoApp = new Label();
        lblVersaoApp.Text = "v" + App.VERSAO;
        lblVersaoApp.ForeColor = CinzaTexto;
        lblVersaoApp.AutoSize = true;
        lblVersaoApp.Location = new Point(P(46), P(26));

        FlowLayoutPanel barraGit = new FlowLayoutPanel();
        barraGit.AutoSize = true;
        barraGit.FlowDirection = FlowDirection.RightToLeft;
        barraGit.WrapContents = false;
        barraGit.Dock = DockStyle.Right;
        barraGit.Padding = new Padding(0, P(6), 0, 0);

        bAtualizar = Botao("Procurar Atualização");
        bAtualizar.BackColor = Color.FromArgb(240, 246, 255);
        bAtualizar.Click += delegate { ProcurarAtualizacao(); };

        txtRepo = new TextBox();
        txtRepo.Width = P(190);
        txtRepo.Margin = new Padding(P(4), P(4), P(8), 0);
        txtRepo.Text = App.ObterRepoEfetivo();
        txtRepo.TextChanged += delegate
        {
            if (carregando) return;
            App.Cfg.GithubRepo = txtRepo.Text.Trim();
            AgendarSalvar();
        };

        Label lblRepo = new Label();
        lblRepo.Text = "Repositório GitHub:";
        lblRepo.AutoSize = true;
        lblRepo.Margin = new Padding(0, P(7), P(2), 0);
        lblRepo.ForeColor = CinzaTexto;

        lblStatusUpdate = new Label();
        lblStatusUpdate.AutoSize = true;
        lblStatusUpdate.Margin = new Padding(0, P(7), P(10), 0);
        lblStatusUpdate.ForeColor = Azul;

        barraGit.Controls.AddRange(new Control[] { bAtualizar, txtRepo, lblRepo, lblStatusUpdate });
        topo.Controls.AddRange(new Control[] { picLogo, lblNomeApp, lblVersaoApp, barraGit });

        // ----- coluna esquerda: lista -----
        TableLayoutPanel esq = new TableLayoutPanel();
        esq.Dock = DockStyle.Fill;
        esq.ColumnCount = 1;
        esq.RowCount = 3;
        esq.Margin = new Padding(0, 0, P(14), 0);
        esq.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        esq.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        esq.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Label lblLista = new Label();
        lblLista.Text = "Textos cadastrados";
        lblLista.Font = fNegrito;
        lblLista.AutoSize = true;
        lblLista.Margin = new Padding(0, 0, 0, P(6));

        lista = new ListBox();
        lista.Dock = DockStyle.Fill;
        lista.DrawMode = DrawMode.OwnerDrawFixed;
        lista.ItemHeight = Math.Min(255, P(46));
        lista.IntegralHeight = false;
        lista.BorderStyle = BorderStyle.FixedSingle;
        lista.Margin = new Padding(0);
        lista.DrawItem += DesenharItem;
        lista.SelectedIndexChanged += AoSelecionar;

        FlowLayoutPanel botoes = new FlowLayoutPanel();
        botoes.AutoSize = true;
        botoes.Dock = DockStyle.Fill;
        botoes.WrapContents = false;
        botoes.Margin = new Padding(0, P(8), 0, 0);
        bNovo = Botao("+ Novo");
        bExcluir = Botao("Excluir");
        bSubir = Botao("▲");
        bDescer = Botao("▼");
        bNovo.Click += delegate { Novo(); };
        bExcluir.Click += delegate { Excluir(); };
        bSubir.Click += delegate { Mover(-1); };
        bDescer.Click += delegate { Mover(1); };
        botoes.Controls.AddRange(new Control[] { bNovo, bExcluir, bSubir, bDescer });

        esq.Controls.Add(lblLista, 0, 0);
        esq.Controls.Add(lista, 0, 1);
        esq.Controls.Add(botoes, 0, 2);

        // ----- coluna direita: edição -----
        TableLayoutPanel dir = new TableLayoutPanel();
        dir.Dock = DockStyle.Fill;
        dir.ColumnCount = 1;
        dir.RowCount = 4;
        dir.Margin = new Padding(0);
        dir.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dir.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dir.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dir.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Label lblTitulo = new Label();
        lblTitulo.Text = "Título (aparece na lista)";
        lblTitulo.Font = fNegrito;
        lblTitulo.AutoSize = true;
        lblTitulo.Margin = new Padding(0, 0, 0, P(4));

        txtTitulo = new TextBox();
        txtTitulo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        txtTitulo.Margin = new Padding(0, 0, 0, P(14));
        txtTitulo.TextChanged += AoEditarTitulo;

        Label lblConteudo = new Label();
        lblConteudo.Text = "Texto que será inserido";
        lblConteudo.Font = fNegrito;
        lblConteudo.AutoSize = true;
        lblConteudo.Margin = new Padding(0, 0, 0, P(4));

        txtConteudo = new TextBox();
        txtConteudo.Multiline = true;
        txtConteudo.AcceptsReturn = true;
        txtConteudo.AcceptsTab = true;
        txtConteudo.WordWrap = true;
        txtConteudo.ScrollBars = ScrollBars.Vertical;
        txtConteudo.Dock = DockStyle.Fill;
        txtConteudo.Font = new Font("Segoe UI", 10.5f);
        txtConteudo.Margin = new Padding(0);
        txtConteudo.TextChanged += AoEditarConteudo;

        KeyEventHandler selecionarTudo = delegate(object s, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.A) { ((TextBox)s).SelectAll(); e.SuppressKeyPress = true; }
        };
        txtTitulo.KeyDown += selecionarTudo;
        txtConteudo.KeyDown += selecionarTudo;

        dir.Controls.Add(lblTitulo, 0, 0);
        dir.Controls.Add(txtTitulo, 0, 1);
        dir.Controls.Add(lblConteudo, 0, 2);
        dir.Controls.Add(txtConteudo, 0, 3);

        // ----- rodapé: opções -----
        FlowLayoutPanel opcoes = new FlowLayoutPanel();
        opcoes.AutoSize = true;
        opcoes.Dock = DockStyle.Fill;
        opcoes.WrapContents = true;
        opcoes.Margin = new Padding(0, P(12), 0, 0);

        Label lblModo = new Label();
        lblModo.Text = "Modo:";
        lblModo.AutoSize = true;
        lblModo.Margin = new Padding(0, P(5), P(4), 0);

        rDigitar = new RadioButton();
        rDigitar.Text = "Digitar (padrão)";
        rDigitar.AutoSize = true;
        rColar = new RadioButton();
        rColar.Text = "Colar pela área de transferência (melhor para textos longos)";
        rColar.AutoSize = true;
        rColar.Margin = new Padding(P(4), P(3), P(24), P(3));
        chkIniciar = new CheckBox();
        chkIniciar.Text = "Iniciar com o Windows";
        chkIniciar.AutoSize = true;

        Label dica = new Label();
        dica.AutoSize = true;
        dica.ForeColor = CinzaTexto;
        dica.Margin = new Padding(0, P(8), 0, 0);
        dica.Text = "Como usar: em qualquer programa, aperte Scroll Lock e depois a tecla do texto (1–9, 0, A–Z), Enter ou clique nele. Esc fecha.\r\n" +
                    "Seus textos ficam salvos em %APPDATA%\\HotKeyText\\textos.xml e nunca são perdidos ao atualizar.";

        opcoes.Controls.AddRange(new Control[] { lblModo, rDigitar, rColar, chkIniciar, dica });
        opcoes.SetFlowBreak(chkIniciar, true);

        raiz.Controls.Add(topo, 0, 0);
        raiz.SetColumnSpan(topo, 2);
        raiz.Controls.Add(esq, 0, 1);
        raiz.Controls.Add(dir, 1, 1);
        raiz.Controls.Add(opcoes, 0, 2);
        raiz.SetColumnSpan(opcoes, 2);
        Controls.Add(raiz);

        ResumeLayout(true);

        rDigitar.Checked = !App.Cfg.ModoColar;
        rColar.Checked = App.Cfg.ModoColar;
        try { chkIniciar.Checked = App.IniciaComWindows; } catch { }
        rColar.CheckedChanged += delegate { App.Cfg.ModoColar = rColar.Checked; App.Salvar(); };
        chkIniciar.CheckedChanged += delegate
        {
            try { App.IniciaComWindows = chkIniciar.Checked; }
            catch (Exception ex) { MessageBox.Show("Não foi possível alterar: " + ex.Message, Text); }
        };

        timerSalvar = new System.Windows.Forms.Timer();
        timerSalvar.Interval = 600;
        timerSalvar.Tick += delegate { timerSalvar.Stop(); App.Salvar(); };

        FormClosing += delegate { timerSalvar.Stop(); App.Salvar(); };

        Recarregar(App.Cfg.Textos.Count > 0 ? 0 : -1);
    }

    public void ProcurarAtualizacao()
    {
        string repo = txtRepo.Text.Trim();
        if (string.IsNullOrEmpty(repo)) repo = App.ObterRepoEfetivo();
        App.Cfg.GithubRepo = App.NormalizarRepo(repo);
        txtRepo.Text = App.Cfg.GithubRepo;
        App.Salvar();

        bAtualizar.Enabled = false;
        lblStatusUpdate.Text = "Verificando no GitHub...";

        ThreadPool.QueueUserWorkItem(delegate
        {
            Atualizador.ResultadoUpdate res = Atualizador.Verificar(repo);
            BeginInvoke((Action)delegate
            {
                bAtualizar.Enabled = true;
                if (!res.Sucesso)
                {
                    lblStatusUpdate.Text = "";
                    MessageBox.Show(res.Erro, "Procurar Atualização — HotKeyText", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (!res.TemNovaVersao)
                {
                    lblStatusUpdate.Text = "Atualizado (v" + App.VERSAO + ")";
                    MessageBox.Show("Você já está na versão mais recente (v" + App.VERSAO + ").",
                        "Procurar Atualização — HotKeyText", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (string.IsNullOrEmpty(res.UrlDownload))
                {
                    lblStatusUpdate.Text = "Nova v" + res.VersaoRemota + " sem .exe";
                    MessageBox.Show("A versão v" + res.VersaoRemota + " foi encontrada no GitHub, mas não possui um arquivo HotKeyText.exe anexado na Release.",
                        "Procurar Atualização — HotKeyText", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                lblStatusUpdate.Text = "Nova versão v" + res.VersaoRemota + " disponível!";
                DialogResult dr = MessageBox.Show(
                    "Nova versão encontrada no GitHub!\r\n\r\n" +
                    "Versão atual: v" + App.VERSAO + "\r\n" +
                    "Nova versão: v" + res.VersaoRemota + "\r\n\r\n" +
                    "Deseja baixar e atualizar agora? Seus textos cadastrados serão mantidos intactos.",
                    "Atualização Disponível — HotKeyText",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information);

                if (dr == DialogResult.Yes)
                {
                    bAtualizar.Enabled = false;
                    lblStatusUpdate.Text = "Baixando v" + res.VersaoRemota + "...";
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try
                        {
                            Atualizador.BaixarEAplicar(res.UrlDownload);
                        }
                        catch (Exception ex)
                        {
                            BeginInvoke((Action)delegate
                            {
                                bAtualizar.Enabled = true;
                                lblStatusUpdate.Text = "Falha ao atualizar";
                                MessageBox.Show("Erro ao baixar/aplicar atualização: " + ex.Message,
                                    "HotKeyText", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            });
                        }
                    });
                }
            });
        });
    }

    Button Botao(string texto)
    {
        Button b = new Button();
        b.Text = texto;
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.Padding = new Padding(P(8), P(3), P(8), P(3));
        b.Margin = new Padding(0, 0, P(6), 0);
        b.UseVisualStyleBackColor = true;
        return b;
    }

    static string Normalizar(string s)
    {
        return (s ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
    }

    void Recarregar(int selecionar)
    {
        carregando = true;
        lista.BeginUpdate();
        lista.Items.Clear();
        foreach (TextoItem t in App.Cfg.Textos) lista.Items.Add(t);
        lista.EndUpdate();
        carregando = false;
        if (selecionar >= lista.Items.Count) selecionar = lista.Items.Count - 1;
        lista.SelectedIndex = selecionar;
        AoSelecionar(null, null);
    }

    void AoSelecionar(object s, EventArgs e)
    {
        if (carregando) return;
        int i = lista.SelectedIndex;
        int n = App.Cfg.Textos.Count;
        bool ok = i >= 0 && i < n;
        carregando = true;
        txtTitulo.Enabled = ok;
        txtConteudo.Enabled = ok;
        bExcluir.Enabled = ok;
        bSubir.Enabled = ok && i > 0;
        bDescer.Enabled = ok && i < n - 1;
        txtTitulo.Text = ok ? App.Cfg.Textos[i].Titulo : "";
        txtConteudo.Text = ok ? Normalizar(App.Cfg.Textos[i].Conteudo) : "";
        carregando = false;
    }

    TextoItem Atual()
    {
        int i = lista.SelectedIndex;
        return (i >= 0 && i < App.Cfg.Textos.Count) ? App.Cfg.Textos[i] : null;
    }

    void AoEditarTitulo(object s, EventArgs e)
    {
        if (carregando) return;
        TextoItem t = Atual();
        if (t == null) return;
        t.Titulo = txtTitulo.Text;
        lista.Invalidate();
        AgendarSalvar();
    }

    void AoEditarConteudo(object s, EventArgs e)
    {
        if (carregando) return;
        TextoItem t = Atual();
        if (t == null) return;
        t.Conteudo = txtConteudo.Text;
        lista.Invalidate();
        AgendarSalvar();
    }

    void AgendarSalvar()
    {
        timerSalvar.Stop();
        timerSalvar.Start();
    }

    void Novo()
    {
        TextoItem t = new TextoItem();
        t.Titulo = "Novo texto";
        App.Cfg.Textos.Add(t);
        App.Salvar();
        Recarregar(App.Cfg.Textos.Count - 1);
        txtTitulo.Focus();
        txtTitulo.SelectAll();
    }

    void Excluir()
    {
        int i = lista.SelectedIndex;
        if (i < 0 || i >= App.Cfg.Textos.Count) return;
        string nome = App.TituloDe(App.Cfg.Textos[i]);
        if (MessageBox.Show("Excluir \"" + nome + "\"?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        App.Cfg.Textos.RemoveAt(i);
        App.Salvar();
        Recarregar(Math.Min(i, App.Cfg.Textos.Count - 1));
    }

    void Mover(int d)
    {
        int i = lista.SelectedIndex;
        int j = i + d;
        List<TextoItem> l = App.Cfg.Textos;
        if (i < 0 || j < 0 || j >= l.Count) return;
        TextoItem tmp = l[i]; l[i] = l[j]; l[j] = tmp;
        App.Salvar();
        Recarregar(j);
    }

    void DesenharItem(object s, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= App.Cfg.Textos.Count) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        Rectangle r = e.Bounds;
        using (SolidBrush b = new SolidBrush(sel ? Selecao : Color.White)) g.FillRectangle(b, r);
        if (sel) using (SolidBrush b = new SolidBrush(Azul)) g.FillRectangle(b, r.X, r.Y, P(3), r.Height);

        TextoItem t = App.Cfg.Textos[e.Index];
        string rot = App.RotuloDoIndice(e.Index);
        int bs = P(26);
        Rectangle rb = new Rectangle(r.X + P(12), r.Y + (r.Height - bs) / 2, bs, bs);
        using (GraphicsPath p = App.Arredondado(rb, P(5)))
        using (SolidBrush b = new SolidBrush(rot.Length > 0 ? Azul : Color.Silver))
            g.FillPath(b, p);
        TextRenderer.DrawText(g, rot, fBadge, rb, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

        TextFormatFlags esq = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        int tx = rb.Right + P(10);
        int tw = r.Right - tx - P(6);
        TextRenderer.DrawText(g, App.TituloDe(t), fItem, new Rectangle(tx, r.Y + P(3), tw, r.Height / 2 - P(2)), Color.Black, esq);
        TextRenderer.DrawText(g, App.ResumoDe(t), fItemPrev, new Rectangle(tx, r.Y + r.Height / 2, tw, r.Height / 2 - P(3)), CinzaTexto, esq);
    }
}
