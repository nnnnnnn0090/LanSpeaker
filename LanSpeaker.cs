// LANスピーカー
// 複数のPCの音を、同じネットワーク上の1台のPC（スピーカーがつながっているPC）に集めて鳴らすソフト。
// Windows 標準の .NET Framework 4 だけで動く（インストール不要の exe 1つ）。
//
//  送る係  : WASAPI ループバックで「このPCで鳴っている音」を取り込み、UDPで送信
//  鳴らす係: 届いた各PCの音をミックスして、スピーカーへ出力
//
// 受信PCは UDP ポート 49650 で待ち受ける。送信PCはブロードキャストで受信PCを自動で探す。

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;


[assembly: AssemblyTitle("LANスピーカー")]
[assembly: AssemblyProduct("LANスピーカー")]
[assembly: AssemblyCompany("LanSpeaker")]
[assembly: AssemblyCopyright("Copyright (c) 2026")]
[assembly: AssemblyDescription("複数のPCの音を1台のPCにまとめて鳴らす")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace LanSpeaker
{
    // =====================================================================
    //  通信プロトコル
    // =====================================================================
    static class Proto
    {
        public static int Port = 49650;
        public const int Rate = 48000;            // 送受信は 48kHz / 16bit / ステレオ固定
        public const int FramesPerPacket = 240;   // 5ms 分
        public const byte Discover = 1, Here = 2, Audio = 3, Keepalive = 4, Bye = 5;
        public const int AudioHeader = 15;
        static readonly byte[] Magic = { 0x4C, 0x53, 0x50, 0x31 }; // "LSP1"

        public static bool Check(byte[] b, int len)
        {
            return len >= 5 && b[0] == Magic[0] && b[1] == Magic[1] && b[2] == Magic[2] && b[3] == Magic[3];
        }

        static void Head(byte[] b, byte type) { Buffer.BlockCopy(Magic, 0, b, 0, 4); b[4] = type; }

        public static void PutU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }

        public static uint GetU32(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        static byte[] NameBytes(string name)
        {
            byte[] n = Encoding.UTF8.GetBytes(name ?? "");
            if (n.Length > 60) Array.Resize(ref n, 60);
            return n;
        }

        public static byte[] MakeNamed(byte type, string name)
        {
            byte[] n = NameBytes(name);
            byte[] b = new byte[6 + n.Length];
            Head(b, type); b[5] = (byte)n.Length; Buffer.BlockCopy(n, 0, b, 6, n.Length);
            return b;
        }

        public static byte[] MakeKeepalive(uint id, string name)
        {
            byte[] n = NameBytes(name);
            byte[] b = new byte[10 + n.Length];
            Head(b, Keepalive); PutU32(b, 5, id); b[9] = (byte)n.Length; Buffer.BlockCopy(n, 0, b, 10, n.Length);
            return b;
        }

        public static byte[] MakeBye(uint id)
        {
            byte[] b = new byte[9];
            Head(b, Bye); PutU32(b, 5, id);
            return b;
        }

        public static string ReadName(byte[] b, int len, int off)
        {
            if (off >= len) return "";
            int n = b[off];
            if (off + 1 + n > len) return "";
            return Encoding.UTF8.GetString(b, off + 1, n);
        }

        public static int WriteAudio(byte[] b, uint id, uint seq, short[] pcm, int frames)
        {
            Head(b, Audio); PutU32(b, 5, id); PutU32(b, 9, seq);
            b[13] = (byte)frames; b[14] = (byte)(frames >> 8);
            Buffer.BlockCopy(pcm, 0, b, AudioHeader, frames * 4);
            return AudioHeader + frames * 4;
        }
    }

    static class Net
    {
        // 相手がいないときの ICMP で Receive が例外になるのを止める
        public static void NoConnReset(Socket s)
        {
            try { s.IOControl(-1744830452, new byte[4], null); } catch { }
        }

        static IEnumerable<UnicastIPAddressInformation> Ipv4Addrs()
        {
            NetworkInterface[] nis;
            try { nis = NetworkInterface.GetAllNetworkInterfaces(); } catch { yield break; }
            foreach (NetworkInterface ni in nis)
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties p;
                try { p = ni.GetIPProperties(); } catch { continue; }
                foreach (UnicastIPAddressInformation ua in p.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] a = ua.Address.GetAddressBytes();
                    if (a[0] == 169 && a[1] == 254) continue;
                    yield return ua;
                }
            }
        }

        public static List<IPAddress> Broadcasts()
        {
            List<IPAddress> list = new List<IPAddress>();
            list.Add(IPAddress.Broadcast);
            foreach (UnicastIPAddressInformation ua in Ipv4Addrs())
            {
                try
                {
                    if (ua.IPv4Mask == null) continue;
                    byte[] a = ua.Address.GetAddressBytes();
                    byte[] m = ua.IPv4Mask.GetAddressBytes();
                    if (m.All(x => x == 0)) continue;
                    byte[] bc = new byte[4];
                    for (int i = 0; i < 4; i++) bc[i] = (byte)(a[i] | ~m[i]);
                    IPAddress ip = new IPAddress(bc);
                    if (!list.Contains(ip)) list.Add(ip);
                }
                catch { }
            }
            return list;
        }

        public static List<string> LocalIPv4()
        {
            List<string> list = new List<string>();
            foreach (UnicastIPAddressInformation ua in Ipv4Addrs())
            {
                string s = ua.Address.ToString();
                if (!list.Contains(s)) list.Add(s);
            }
            return list;
        }
    }

    // =====================================================================
    //  設定 (%APPDATA%\LanSpeaker\settings.txt)
    // =====================================================================
    static class Settings
    {
        public static string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanSpeaker", "settings.txt");
        static readonly object lk = new object();
        static Dictionary<string, string> d;

        static void Load()
        {
            if (d != null) return;
            d = new Dictionary<string, string>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
                    }
            }
            catch { }
        }

        public static string Get(string k, string def)
        {
            lock (lk) { Load(); string v; return d.TryGetValue(k, out v) ? v : def; }
        }

        public static int GetInt(string k, int def)
        {
            int v; return int.TryParse(Get(k, ""), out v) ? v : def;
        }

        public static void Set(string k, string v)
        {
            lock (lk)
            {
                Load();
                d[k] = v ?? "";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.WriteAllLines(FilePath, d.Select(kv => kv.Key + "=" + kv.Value).ToArray(), Encoding.UTF8);
                }
                catch { }
            }
        }
    }

    // ショートカットの「実行時の大きさ: 最小化」で起動されたかどうか
    static class StartupState
    {
        [StructLayout(LayoutKind.Sequential)]
        struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [DllImport("kernel32.dll", EntryPoint = "GetStartupInfoW")]
        static extern void GetStartupInfo(out STARTUPINFO si);

        public static bool Minimized()
        {
            try
            {
                STARTUPINFO si;
                GetStartupInfo(out si);
                if ((si.dwFlags & 1) == 0) return false;   // STARTF_USESHOWWINDOW
                int s = si.wShowWindow;
                return s == 2 || s == 6 || s == 7 || s == 11;  // 最小化系
            }
            catch { return false; }
        }
    }

    // =====================================================================
    //  WASAPI (Core Audio) の COM 定義
    // =====================================================================
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        void RegisterEndpointNotificationCallback(IntPtr client);
        void UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        void OpenPropertyStore(int access, out IPropertyStore props);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out int state);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        void GetCount(out int count);
        void Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        void GetBuffer(int frames, out IntPtr data);
        void ReleaseBuffer(int frames, int flags);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out int count);
        void GetAt(int i, out PROPERTYKEY key);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        void Commit();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        void Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        void GetBufferSize(out int frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out int padding);
        void IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defPeriod, out long minPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr handle);
        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out int frames, out int flags, out long devPos, out long qpcPos);
        void ReleaseBuffer(int frames);
        void GetNextPacketSize(out int frames);
    }

    static class CoreAudio
    {
        public static Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        public static Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        public static readonly Guid SubtypeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");
        public static Guid IID_IAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        public const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        public const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        public const int AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);
        public const int AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
        public const int BUFFERFLAGS_SILENT = 0x2;
        public const int E_NOTFOUND = unchecked((int)0x80070490);

        // 48kHz / ステレオ / 32bit float の WAVEFORMATEXTENSIBLE（呼び出し側で FreeHGlobal）
        public static IntPtr MakeFloatFormat(int rate, int ch)
        {
            IntPtr p = Marshal.AllocHGlobal(40);
            Marshal.WriteInt16(p, 0, unchecked((short)0xFFFE));
            Marshal.WriteInt16(p, 2, (short)ch);
            Marshal.WriteInt32(p, 4, rate);
            Marshal.WriteInt32(p, 8, rate * ch * 4);
            Marshal.WriteInt16(p, 12, (short)(ch * 4));
            Marshal.WriteInt16(p, 14, 32);
            Marshal.WriteInt16(p, 16, 22);
            Marshal.WriteInt16(p, 18, 32);
            Marshal.WriteInt32(p, 20, ch == 1 ? 4 : 3);
            Marshal.Copy(SubtypeFloat.ToByteArray(), 0, new IntPtr(p.ToInt64() + 24), 16);
            return p;
        }

        // 有効な再生デバイスの一覧 (ID, 名前)
        public static List<KeyValuePair<string, string>> RenderDevices()
        {
            List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
            IMMDeviceEnumerator en = null;
            IMMDeviceCollection col = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                en.EnumAudioEndpoints(0, 1 /*ACTIVE*/, out col);
                int n;
                col.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    IMMDevice d = null;
                    try
                    {
                        col.Item(i, out d);
                        string id;
                        d.GetId(out id);
                        list.Add(new KeyValuePair<string, string>(id, FriendlyName(d)));
                    }
                    catch { }
                    finally { Release(d); }
                }
            }
            catch { }
            finally { Release(col); Release(en); }
            return list;
        }

        public static string DefaultRenderId(IMMDeviceEnumerator en)
        {
            IMMDevice d = null;
            try
            {
                en.GetDefaultAudioEndpoint(0, 1, out d);
                string id;
                d.GetId(out id);
                return id;
            }
            catch { return null; }
            finally { Release(d); }
        }

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PROPVARIANT pv);

        public static string FriendlyName(IMMDevice dev)
        {
            try
            {
                IPropertyStore ps;
                dev.OpenPropertyStore(0, out ps);
                PROPERTYKEY key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
                PROPVARIANT v;
                ps.GetValue(ref key, out v);
                string s = v.vt == 31 ? Marshal.PtrToStringUni(v.p) : null;
                PropVariantClear(ref v);
                Marshal.ReleaseComObject(ps);
                return s ?? "再生デバイス";
            }
            catch { return "再生デバイス"; }
        }

        public static void Release(object o)
        {
            if (o != null) try { Marshal.ReleaseComObject(o); } catch { }
        }
    }

    static class Interp
    {
        // 4点 3次補間 (Catmull-Rom)
        public static float Cubic(float y0, float y1, float y2, float y3, float t)
        {
            float a = -0.5f * y0 + 1.5f * y1 - 1.5f * y2 + 0.5f * y3;
            float b = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
            float c = -0.5f * y0 + 0.5f * y2;
            return ((a * t + b) * t + c) * t + y1;
        }
    }

    // サンプルレート変換（3次補間・ストリーム用）。Windows の変換が使えないときの予備。
    class CubicResampler
    {
        readonly double step;
        double pos = 1;
        float[] w = new float[0];
        readonly float[] hist = new float[6];
        public CubicResampler(int inRate, int outRate) { step = (double)inRate / outRate; }

        public int Process(float[] inp, int frames, ref float[] output)
        {
            if (frames <= 0) return 0;
            int m = frames + 3;
            if (w.Length < m * 2) w = new float[m * 2];
            Array.Copy(hist, 0, w, 0, 6);
            Array.Copy(inp, 0, w, 6, frames * 2);
            int maxOut = (int)(frames / step) + 4;
            if (output.Length < maxOut * 2) output = new float[maxOut * 2];
            int o = 0;
            while (pos + 2 < m && o < maxOut)
            {
                int i = (int)pos;
                float t = (float)(pos - i);
                for (int c = 0; c < 2; c++)
                    output[o * 2 + c] = Interp.Cubic(w[(i - 1) * 2 + c], w[i * 2 + c], w[(i + 1) * 2 + c], w[(i + 2) * 2 + c], t);
                o++;
                pos += step;
            }
            pos -= frames;
            Array.Copy(w, frames * 2, hist, 0, 6);
            return o;
        }
    }

    // =====================================================================
    //  送る係
    // =====================================================================
    class Sender
    {
        UdpClient udp;
        volatile bool running;
        public volatile bool Paused;
        volatile string manualIp = "";
        volatile IPEndPoint target;
        long lastHereTicks;
        public volatile string ReceiverName = "";
        public volatile string DeviceInfo = "";
        public volatile string CaptureError;
        public long PacketsSent;
        public static int TestRequestRate;   // テスト用：取り込みレートを強制
        public bool TestNoCapture;           // テスト用：取り込みを止める

        uint sourceId, seq;
        readonly string name = Environment.MachineName;
        readonly object feedLock = new object();
        readonly short[] pending = new short[Proto.FramesPerPacket * 2];
        int pendingFrames;
        readonly byte[] pkt = new byte[Proto.AudioHeader + Proto.FramesPerPacket * 4];
        float peak;

        public bool Connected
        {
            get { return DateTime.UtcNow.Ticks - Interlocked.Read(ref lastHereTicks) < TimeSpan.TicksPerSecond * 4; }
        }
        public IPEndPoint Target { get { return target; } }
        public string ManualIp { get { return manualIp; } }

        public void SetManual(string ip)
        {
            manualIp = (ip ?? "").Trim();
            target = null;
            Interlocked.Exchange(ref lastHereTicks, 0);
        }

        public void Start()
        {
            if (running) return;
            sourceId = (uint)Guid.NewGuid().GetHashCode();
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            udp.EnableBroadcast = true;
            Net.NoConnReset(udp.Client);
            running = true;
            StartThread(NetLoop, "net");
            StartThread(ControlLoop, "control");
            StartThread(CaptureLoop, "capture");
        }

        static void StartThread(ThreadStart f, string n)
        {
            Thread t = new Thread(f);
            t.IsBackground = true;
            t.Name = n;
            t.Start();
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            try
            {
                IPEndPoint t = target;
                if (t != null) { byte[] b = Proto.MakeBye(sourceId); udp.Send(b, b.Length, t); }
            }
            catch { }
            try { udp.Close(); } catch { }
        }

        public float TakePeak()
        {
            lock (feedLock) { float p = peak; peak = 0; return p; }
        }

        void NetLoop()
        {
            while (running)
            {
                byte[] b;
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                try { b = udp.Receive(ref ep); }
                catch (SocketException) { if (!running) break; continue; }
                catch (ObjectDisposedException) { break; }
                if (!Proto.Check(b, b.Length) || b[4] != Proto.Here) continue;

                IPEndPoint t = target;
                bool manual = manualIp.Length > 0;
                bool accept = manual
                    ? (t != null && t.Address.Equals(ep.Address))
                    : (t == null || !Connected || t.Address.Equals(ep.Address));
                if (!accept) continue;
                target = new IPEndPoint(ep.Address, Proto.Port);
                ReceiverName = Proto.ReadName(b, b.Length, 5);
                Interlocked.Exchange(ref lastHereTicks, DateTime.UtcNow.Ticks);
            }
        }

        void ControlLoop()
        {
            while (running)
            {
                try
                {
                    string m = manualIp;
                    if (m.Length > 0)
                    {
                        if (target == null)
                        {
                            IPAddress ip;
                            if (!IPAddress.TryParse(m, out ip))
                            {
                                try { ip = Dns.GetHostAddresses(m).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); }
                                catch { ip = null; }
                            }
                            if (ip != null) target = new IPEndPoint(ip, Proto.Port);
                        }
                    }
                    else if (!Connected)
                    {
                        byte[] d = Proto.MakeNamed(Proto.Discover, name);
                        foreach (IPAddress bc in Net.Broadcasts())
                        {
                            try { udp.Send(d, d.Length, new IPEndPoint(bc, Proto.Port)); } catch { }
                        }
                    }
                    IPEndPoint t = target;
                    if (t != null)
                    {
                        byte[] k = Proto.MakeKeepalive(sourceId, name);
                        udp.Send(k, k.Length, t);
                    }
                }
                catch { }
                for (int i = 0; i < 10 && running; i++) Thread.Sleep(100);
            }
        }

        // 48kHz ステレオ float の音を受け取り、5ms ごとのパケットにして送る
        public void Feed(float[] st, int frames)
        {
            lock (feedLock)
            {
                if (Paused) return;
                float pk = peak;
                for (int i = 0; i < frames; i++)
                {
                    float l = st[i * 2], r = st[i * 2 + 1];
                    float a = Math.Abs(l); if (a > pk) pk = a;
                    a = Math.Abs(r); if (a > pk) pk = a;
                    pending[pendingFrames * 2] = ToS16(l);
                    pending[pendingFrames * 2 + 1] = ToS16(r);
                    if (++pendingFrames == Proto.FramesPerPacket)
                    {
                        SendPending();
                        pendingFrames = 0;
                    }
                }
                peak = pk;
            }
        }

        static short ToS16(float v)
        {
            if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
            return (short)(v * 32767f);
        }

        void SendPending()
        {
            IPEndPoint t = target;
            if (t == null) return;
            int len = Proto.WriteAudio(pkt, sourceId, seq++, pending, pendingFrames);
            try { udp.Send(pkt, len, t); PacketsSent++; } catch { }
        }

        // ---------- WASAPI ループバックで「このPCで鳴っている音」を取り込む ----------
        void CaptureLoop()
        {
            if (TestNoCapture) return;
            while (running)
            {
                try { RunCapture(); }
                catch (COMException ex)
                {
                    if (ex.ErrorCode == CoreAudio.E_NOTFOUND)
                        CaptureError = "このPCに再生デバイス（音の出力先）がありません";
                    else
                        CaptureError = "音の取り込みに失敗しました (0x" + ex.ErrorCode.ToString("X8") + ")";
                }
                catch (Exception ex) { CaptureError = "音の取り込みに失敗しました: " + ex.Message; }
                for (int i = 0; i < 10 && running; i++) Thread.Sleep(100);
            }
        }

        void RunCapture()
        {
            IMMDeviceEnumerator en = null;
            IMMDevice dev = null;
            IAudioClient ac = null;
            IAudioCaptureClient cc = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                en.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out dev);
                string devId;
                dev.GetId(out devId);
                string devName = CoreAudio.FriendlyName(dev);

                object o;
                Guid iid = CoreAudio.IID_IAudioClient;
                dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out o);
                ac = (IAudioClient)o;

                IntPtr pf;
                ac.GetMixFormat(out pf);
                int tag = (ushort)Marshal.ReadInt16(pf, 0);
                int ch = Marshal.ReadInt16(pf, 2);
                int rate = Marshal.ReadInt32(pf, 4);
                int blockAlign = Marshal.ReadInt16(pf, 12);
                bool isFloat = tag == 3;
                if (tag == 0xFFFE)
                {
                    byte[] g = new byte[16];
                    Marshal.Copy(new IntPtr(pf.ToInt64() + 24), g, 0, 16);
                    isFloat = new Guid(g) == CoreAudio.SubtypeFloat;
                }
                int bps = blockAlign / Math.Max(1, ch);
                string conv = "";
                int origRate = rate, origCh = ch;
                int reqRate = TestRequestRate > 0 ? TestRequestRate : Proto.Rate;
                bool direct = !(rate == reqRate && ch == 2 && isFloat && bps == 4);
                if (direct)
                {
                    // 48kHz ステレオへの変換は Windows の高品質な変換に任せる
                    IntPtr want = CoreAudio.MakeFloatFormat(reqRate, 2);
                    try
                    {
                        ac.Initialize(0, CoreAudio.AUDCLNT_STREAMFLAGS_LOOPBACK | CoreAudio.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                                      CoreAudio.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY, 2000000, 0, want, IntPtr.Zero);
                        conv = " → " + reqRate + "Hz 変換";
                        rate = reqRate; ch = 2; isFloat = true; bps = 4; blockAlign = 8;
                    }
                    catch (COMException)
                    {
                        direct = false;
                        CoreAudio.Release(ac);
                        Guid iid2 = CoreAudio.IID_IAudioClient;
                        dev.Activate(ref iid2, 23, IntPtr.Zero, out o);
                        ac = (IAudioClient)o;
                    }
                    finally { Marshal.FreeHGlobal(want); }
                }
                try
                {
                    if (!direct)
                        ac.Initialize(0 /*shared*/, CoreAudio.AUDCLNT_STREAMFLAGS_LOOPBACK, 2000000 /*200ms*/, 0, pf, IntPtr.Zero);
                }
                finally { Marshal.FreeCoTaskMem(pf); }

                Guid iidc = CoreAudio.IID_IAudioCaptureClient;
                ac.GetService(ref iidc, out o);
                cc = (IAudioCaptureClient)o;
                ac.Start();

                DeviceInfo = devName + "  (" + origRate + "Hz / " + origCh + "ch" + conv + ")";
                CaptureError = null;

                CubicResampler rs = rate != Proto.Rate ? new CubicResampler(rate, Proto.Rate) : null;
                byte[] raw = new byte[0];
                float[] st = new float[0];
                float[] rsOut = new float[0];
                float[] tmp = new float[0];
                Stopwatch sw = Stopwatch.StartNew();
                long nextCheck = 2000;

                while (running)
                {
                    Thread.Sleep(2);
                    int packet;
                    cc.GetNextPacketSize(out packet);
                    while (packet > 0 && running)
                    {
                        IntPtr data; int frames, flags; long p1, p2;
                        cc.GetBuffer(out data, out frames, out flags, out p1, out p2);
                        if (frames > 0)
                        {
                            if (st.Length < frames * 2) st = new float[frames * 2];
                            if ((flags & CoreAudio.BUFFERFLAGS_SILENT) != 0)
                            {
                                Array.Clear(st, 0, frames * 2);
                            }
                            else
                            {
                                int bytes = frames * blockAlign;
                                if (raw.Length < bytes) raw = new byte[bytes];
                                Marshal.Copy(data, raw, 0, bytes);
                                int ns = frames * ch;
                                if (tmp.Length < ns) tmp = new float[ns];
                                ToFloat(raw, ns, bps, isFloat, tmp);
                                Downmix(tmp, frames, ch, st);
                            }
                        }
                        cc.ReleaseBuffer(frames);
                        if (frames > 0)
                        {
                            if (rs != null)
                            {
                                int n = rs.Process(st, frames, ref rsOut);
                                Feed(rsOut, n);
                            }
                            else Feed(st, frames);
                        }
                        cc.GetNextPacketSize(out packet);
                    }

                    // 既定の再生デバイスが切り替わったら取り込み直す
                    if (sw.ElapsedMilliseconds > nextCheck)
                    {
                        nextCheck = sw.ElapsedMilliseconds + 2000;
                        IMMDevice cur = null;
                        try
                        {
                            en.GetDefaultAudioEndpoint(0, 1, out cur);
                            string curId;
                            cur.GetId(out curId);
                            if (curId != devId) break;
                        }
                        finally { CoreAudio.Release(cur); }
                    }
                }
                try { ac.Stop(); } catch { }
            }
            finally
            {
                CoreAudio.Release(cc);
                CoreAudio.Release(ac);
                CoreAudio.Release(dev);
                CoreAudio.Release(en);
            }
        }

        static void ToFloat(byte[] raw, int ns, int bps, bool isFloat, float[] dst)
        {
            if (bps == 4 && isFloat)
            {
                Buffer.BlockCopy(raw, 0, dst, 0, ns * 4);
            }
            else if (bps == 4)
            {
                for (int i = 0; i < ns; i++) dst[i] = BitConverter.ToInt32(raw, i * 4) / 2147483648f;
            }
            else if (bps == 3)
            {
                for (int i = 0; i < ns; i++)
                {
                    int o = i * 3;
                    int v = (raw[o] << 8) | (raw[o + 1] << 16) | (raw[o + 2] << 24);
                    dst[i] = v / 2147483648f;
                }
            }
            else if (bps == 2)
            {
                for (int i = 0; i < ns; i++) dst[i] = BitConverter.ToInt16(raw, i * 2) / 32768f;
            }
            else
            {
                Array.Clear(dst, 0, ns);
            }
        }

        // 多チャンネル → ステレオ
        static void Downmix(float[] x, int frames, int ch, float[] st)
        {
            const float k = 0.7071f;
            for (int f = 0; f < frames; f++)
            {
                int b = f * ch;
                float l, r;
                if (ch == 1) { l = r = x[b]; }
                else
                {
                    l = x[b]; r = x[b + 1];
                    if (ch == 4) { l += x[b + 2] * k; r += x[b + 3] * k; }
                    else if (ch >= 3)
                    {
                        float c = x[b + 2] * k; l += c; r += c;
                        if (ch >= 6) { l += x[b + 4] * k; r += x[b + 5] * k; }
                        if (ch >= 8) { l += x[b + 6] * k; r += x[b + 7] * k; }
                    }
                }
                st[f * 2] = l; st[f * 2 + 1] = r;
            }
        }

        // 自己テスト用：サイン波を直接流し込む
        public void StartInjection(double seconds, float freq)
        {
            StartThread(delegate
            {
                Stopwatch sw = Stopwatch.StartNew();
                long done = 0;
                float[] buf = new float[Proto.Rate * 2];
                double ph = 0;
                while (running && (seconds <= 0 || sw.Elapsed.TotalSeconds < seconds))
                {
                    Thread.Sleep(5);
                    long due = (long)(sw.Elapsed.TotalSeconds * Proto.Rate) - done;
                    if (due <= 0) continue;
                    if (due > Proto.Rate) due = Proto.Rate;
                    for (int i = 0; i < due; i++)
                    {
                        float v = (float)(Math.Sin(ph) * 0.3);
                        ph += 2 * Math.PI * freq / Proto.Rate;
                        buf[i * 2] = v; buf[i * 2 + 1] = v;
                    }
                    Feed(buf, (int)due);
                    done += due;
                }
            }, "inject");
        }
    }

    // =====================================================================
    //  鳴らす係：送信元ごとの受信バッファ
    //   ・届く間隔のゆらぎに合わせて、遅延をできるだけ小さく自動調整
    //   ・PC間の時計のずれは、再生速度をごくわずか (最大0.5%) 変えて滑らかに補正
    //   ・途切れ・パケット欠落・溜まりすぎの読み飛ばしは、短いフェードでプチ音を防ぐ
    // =====================================================================
    class SourceStream
    {
        public uint Id;
        public volatile string Name = "";
        public volatile IPAddress Addr;
        public volatile bool Named;
        public long FirstSeenTicks, LastSeenTicks, LastAudioTicks;
        public volatile float Volume = 1f;
        public volatile float Pan;         // -1 = 左スピーカーだけ, 0 = そのまま, +1 = 右スピーカーだけ
        public volatile bool Muted;
        public int Underruns, LostPackets;
        float curGain = -1f, curPan;       // 急に変えてもノイズが出ないよう少しずつ追従
        public long FramesReceived;
        public volatile int BufferMs;

        const int Cap = 96000;              // 2秒分
        const int MinSafety = 480;          // 最低限の余裕 10ms
        const int MaxSafety = 9600;         // 最大 200ms
        const int StartExtra = 960;         // 再生開始時の上乗せ 20ms
        const int TrimOver = 9600;          // 200ms 以上溜まったら読み飛ばす
        const int FadeLen = 96;             // 2ms
        const float FadeStep = 1f / FadeLen;

        readonly float[] ring = new float[Cap * 2];
        int rpos, count;
        double frac, ratio = 1.0;
        uint lastSeq; bool hasSeq;
        bool playing;
        int safety = MinSafety;
        int winMin = int.MaxValue, winFrames;
        float fade, fadeTarget = 1f;
        bool trimPending;
        int fadeInWrite;
        bool underrunPending;
        long underrunTicks, lastBumpTicks;
        double avgLevel;
        float peak;

        public int SafetyMs { get { return safety * 1000 / Proto.Rate; } }
        public double Ratio { get { return ratio; } }

        public bool IsPlaying
        {
            get { return DateTime.UtcNow.Ticks - Interlocked.Read(ref LastAudioTicks) < TimeSpan.TicksPerMillisecond * 400; }
        }

        public float TakePeak() { lock (this) { float p = peak; peak = 0; return p; } }

        public void Write(byte[] b, int off, int frames, uint seq) { WriteAt(b, off, frames, seq, DateTime.UtcNow.Ticks); }

        public void WriteAt(byte[] b, int off, int frames, uint seq, long now)
        {
            lock (this)
            {
                if (hasSeq)
                {
                    int d = (int)(seq - lastSeq);
                    if (d <= 0 && d > -1000) return;          // 重複・遅れて届いたもの
                    if (d > 1 && d <= 40)                       // 欠落：フェードして無音で埋める
                    {
                        LostPackets += d - 1;
                        if (playing)
                        {
                            FadeTail();
                            for (int i = 0; i < (d - 1) * Proto.FramesPerPacket; i++) Put(0, 0);
                            fadeInWrite = FadeLen;
                        }
                    }
                }
                lastSeq = seq; hasSeq = true;

                if (underrunPending)
                {
                    underrunPending = false;
                    // 途切れてすぐ次が来た＝ネットワークのゆらぎ → 余裕を増やす
                    // （長く来なかった＝送信側が無音になっただけ、は数えない）
                    if (now - underrunTicks < TimeSpan.TicksPerMillisecond * 300)
                    {
                        Underruns++;
                        safety = Math.Min(MaxSafety, safety + safety / 2 + 480);
                        lastBumpTicks = now;
                    }
                }

                float pk = peak;
                for (int i = 0; i < frames; i++, off += 4)
                {
                    float l = (short)(b[off] | (b[off + 1] << 8)) / 32768f;
                    float r = (short)(b[off + 2] | (b[off + 3] << 8)) / 32768f;
                    float a = Math.Abs(l); if (a > pk) pk = a;
                    a = Math.Abs(r); if (a > pk) pk = a;
                    if (fadeInWrite > 0)
                    {
                        float g = 1f - fadeInWrite / (float)FadeLen;
                        l *= g; r *= g; fadeInWrite--;
                    }
                    Put(l, r);
                }
                peak = pk;
                FramesReceived += frames;
            }
            Interlocked.Exchange(ref LastAudioTicks, now);
        }

        void Put(float l, float r)
        {
            int w = (rpos + count) % Cap;
            ring[w * 2] = l; ring[w * 2 + 1] = r;
            if (count == Cap) rpos = (rpos + 1) % Cap; else count++;
        }

        // まだ再生していない末尾をフェードアウト
        void FadeTail()
        {
            int n = Math.Min(FadeLen, count - 3);
            for (int i = 0; i < n; i++)
            {
                int idx = (rpos + count - n + i) % Cap;
                float g = 1f - (i + 1) / (float)n;
                ring[idx * 2] *= g; ring[idx * 2 + 1] *= g;
            }
        }

        public void MixInto(float[] mix, int frames, float g) { MixAt(mix, frames, g, DateTime.UtcNow.Ticks); }

        public void MixAt(float[] mix, int frames, float g, long now)
        {
            lock (this)
            {
                if (!playing)
                {
                    if (count < safety + StartExtra + 4) return;   // まだ溜まっていない
                    playing = true; fade = 0; fadeTarget = 1; ratio = 1; frac = 0;
                    winMin = int.MaxValue; winFrames = 0; avgLevel = count;
                }
                if (!trimPending && count > safety + StartExtra + TrimOver)
                {
                    trimPending = true; fadeTarget = 0;   // フェードアウトしてから読み飛ばす
                }

                double need = frac + frames * ratio + 3;
                int outN = frames;
                bool under = false;
                if (count < need)
                {
                    under = true;
                    outN = (int)Math.Max(0, Math.Floor((count - 3 - frac) / ratio));
                    if (outN > frames) outN = frames;
                }
                int fadeOutFrom = under ? Math.Max(0, outN - FadeLen) : int.MaxValue;
                float tp = Math.Max(-1f, Math.Min(1f, Pan));

                for (int i = 0; i < outN; i++)
                {
                    float fg;
                    if (i >= fadeOutFrom)
                    {
                        fg = Math.Min(fade, (outN - i) / (float)(outN - fadeOutFrom + 1));
                    }
                    else
                    {
                        if (fade < fadeTarget) fade = Math.Min(fadeTarget, fade + FadeStep);
                        else if (fade > fadeTarget) fade = Math.Max(fadeTarget, fade - FadeStep);
                        fg = fade;
                        if (trimPending && fade <= 0f)
                        {
                            int jump = count - (safety + StartExtra);
                            if (jump > 0) { rpos = (rpos + jump) % Cap; count -= jump; }
                            trimPending = false; fadeTarget = 1;
                        }
                    }
                    int i1 = rpos, i0 = (rpos + Cap - 1) % Cap, i2 = (rpos + 1) % Cap, i3 = (rpos + 2) % Cap;
                    float t = (float)frac;
                    float l = Interp.Cubic(ring[i0 * 2], ring[i1 * 2], ring[i2 * 2], ring[i3 * 2], t);
                    float r = Interp.Cubic(ring[i0 * 2 + 1], ring[i1 * 2 + 1], ring[i2 * 2 + 1], ring[i3 * 2 + 1], t);
                    // 音量・左右は急に変えずに数msかけて目標値へ
                    if (curGain < 0) { curGain = g; curPan = tp; }
                    if (curGain != g) curGain = Math.Abs(g - curGain) < 0.001f ? g : curGain + (g - curGain) * 0.005f;
                    if (curPan != tp) curPan = Math.Abs(tp - curPan) < 0.001f ? tp : curPan + (tp - curPan) * 0.005f;
                    // 左右の振り分け：端に寄せると、そのPCの左右両方の音を片側のスピーカーにまとめる
                    float lo, ro;
                    if (curPan < 0)
                    {
                        float q = -curPan;
                        lo = l * (1f - q * 0.5f) + r * (q * 0.5f);
                        ro = r * (1f - q);
                    }
                    else
                    {
                        float q = curPan;
                        ro = r * (1f - q * 0.5f) + l * (q * 0.5f);
                        lo = l * (1f - q);
                    }
                    float k = curGain * fg;
                    mix[i * 2] += lo * k;
                    mix[i * 2 + 1] += ro * k;
                    frac += ratio;
                    while (frac >= 1.0) { frac -= 1.0; rpos = (rpos + 1) % Cap; count--; }
                }

                if (under)
                {
                    playing = false;
                    underrunPending = true;
                    underrunTicks = now;
                    return;
                }

                // ---- 遅延の自動調整 ----
                avgLevel = avgLevel * 0.97 + count * 0.03;
                BufferMs = (int)(avgLevel * 1000 / Proto.Rate);
                if (count < winMin) winMin = count;
                winFrames += frames;
                if (winFrames >= Proto.Rate / 2)   // 0.5秒ごと
                {
                    int lastMin = winMin;
                    winMin = int.MaxValue; winFrames = 0;
                    // 15秒間途切れなければ、余裕を少しずつ減らす（1秒に2ms）
                    if (now - lastBumpTicks > TimeSpan.TicksPerSecond * 15 && safety > MinSafety)
                    {
                        safety = Math.Max(MinSafety, safety - 96);
                        lastBumpTicks = now - TimeSpan.TicksPerSecond * 14;
                    }
                    // 最小残量が余裕より多ければ少し速く、少なければ少し遅く再生
                    double excess = (lastMin - safety) / (double)Proto.Rate;
                    double off = excess * 0.1;
                    if (off > 0.005) off = 0.005;
                    if (off < -0.005) off = -0.005;
                    ratio = 1.0 + off;
                }
            }
        }
    }

    // =====================================================================
    //  スピーカー出力
    // =====================================================================
    interface IAudioOut : IDisposable
    {
        bool Pump(Action<float[], int> fill);   // false = 開き直しが必要
        int LatencyMs { get; }
        string Kind { get; }
    }

    // WASAPI 共有モード（イベント駆動・低遅延）。変換は Windows に任せる
    class WasapiPlayer : IAudioOut
    {
        IMMDeviceEnumerator en;
        IMMDevice dev;
        IAudioClient ac;
        IAudioRenderClient rc;
        readonly AutoResetEvent ev = new AutoResetEvent(false);
        readonly bool followDefault;
        readonly string devId;
        int bufFrames, lastCheck, latency;
        float[] tmp = new float[0];
        bool disposed;

        public int LatencyMs { get { return latency; } }
        public string Kind { get { return "WASAPI"; } }

        public WasapiPlayer(string deviceId)
        {
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                followDefault = string.IsNullOrEmpty(deviceId);
                if (followDefault) en.GetDefaultAudioEndpoint(0, 1, out dev);
                else en.GetDevice(deviceId, out dev);
                dev.GetId(out devId);

                object o;
                Guid iid = CoreAudio.IID_IAudioClient;
                dev.Activate(ref iid, 23, IntPtr.Zero, out o);
                ac = (IAudioClient)o;
                IntPtr fmt = CoreAudio.MakeFloatFormat(Proto.Rate, 2);
                try
                {
                    ac.Initialize(0, CoreAudio.AUDCLNT_STREAMFLAGS_EVENTCALLBACK | CoreAudio.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                                  CoreAudio.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY, 300000 /*30ms*/, 0, fmt, IntPtr.Zero);
                }
                finally { Marshal.FreeHGlobal(fmt); }
                ac.GetBufferSize(out bufFrames);
                ac.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
                Guid iidr = CoreAudio.IID_IAudioRenderClient;
                ac.GetService(ref iidr, out o);
                rc = (IAudioRenderClient)o;
                IntPtr p;
                rc.GetBuffer(bufFrames, out p);
                rc.ReleaseBuffer(bufFrames, CoreAudio.BUFFERFLAGS_SILENT);
                long lat;
                ac.GetStreamLatency(out lat);
                latency = (int)(bufFrames * 1000L / Proto.Rate + lat / 10000);
                ac.Start();
                lastCheck = Environment.TickCount;
            }
            catch { Dispose(); throw; }
        }

        public bool Pump(Action<float[], int> fill)
        {
            if (!ev.WaitOne(500)) throw new Exception("スピーカー出力が止まりました。開き直します");
            int pad;
            ac.GetCurrentPadding(out pad);
            int n = bufFrames - pad;
            if (n > 0)
            {
                if (tmp.Length < n * 2) tmp = new float[n * 2];
                fill(tmp, n);
                IntPtr p;
                rc.GetBuffer(n, out p);
                Marshal.Copy(tmp, 0, p, n * 2);
                rc.ReleaseBuffer(n, 0);
            }
            if (followDefault && Environment.TickCount - lastCheck > 2000)
            {
                lastCheck = Environment.TickCount;
                string cur = CoreAudio.DefaultRenderId(en);
                if (cur != null && cur != devId) return false;   // 既定のスピーカーが変わった
            }
            return true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { if (ac != null) ac.Stop(); } catch { }
            CoreAudio.Release(rc);
            CoreAudio.Release(ac);
            CoreAudio.Release(dev);
            CoreAudio.Release(en);
            ev.Close();
        }
    }

    // 予備：waveOut（古い方式）
    class WaveOutPlayer : IAudioOut
    {
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        struct WAVEFORMATEX
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample, cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength, dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags, dwLoops;
            public IntPtr lpNext, reserved;
        }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, uint dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);

        const uint WAVE_MAPPER = 0xFFFFFFFF;
        const uint CALLBACK_EVENT = 0x00050000;
        const int WHDR_DONE = 1;

        IntPtr hwo;
        readonly IntPtr[] hdrs, datas;
        readonly AutoResetEvent ev = new AutoResetEvent(false);
        readonly int hdrSize, flagsOff, frames;
        readonly short[] tmp;
        readonly float[] ftmp;
        int next;
        long lastProgress;

        public int LatencyMs { get { return hdrs.Length * frames * 1000 / Proto.Rate + 30; } }
        public string Kind { get { return "waveOut"; } }

        public WaveOutPlayer(int frames, int nbuf)
        {
            this.frames = frames;
            WAVEFORMATEX fmt = new WAVEFORMATEX
            {
                wFormatTag = 1, nChannels = 2, nSamplesPerSec = Proto.Rate, nAvgBytesPerSec = Proto.Rate * 4,
                nBlockAlign = 4, wBitsPerSample = 16, cbSize = 0
            };
            hdrs = new IntPtr[nbuf];
            datas = new IntPtr[nbuf];
            int r = waveOutOpen(out hwo, WAVE_MAPPER, ref fmt, ev.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT);
            if (r != 0) { hwo = IntPtr.Zero; throw new Exception("スピーカー出力を開けません (エラー " + r + ")"); }

            hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            flagsOff = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwFlags");
            tmp = new short[frames * 2];
            ftmp = new float[frames * 2];
            for (int i = 0; i < nbuf; i++)
            {
                datas[i] = Marshal.AllocHGlobal(frames * 4);
                WAVEHDR h = new WAVEHDR { lpData = datas[i], dwBufferLength = (uint)(frames * 4) };
                hdrs[i] = Marshal.AllocHGlobal(hdrSize);
                Marshal.StructureToPtr(h, hdrs[i], false);
                waveOutPrepareHeader(hwo, hdrs[i], hdrSize);
                Marshal.WriteInt32(hdrs[i], flagsOff, Marshal.ReadInt32(hdrs[i], flagsOff) | WHDR_DONE);
            }
            lastProgress = Environment.TickCount;
        }

        public bool Pump(Action<float[], int> fill)
        {
            ev.WaitOne(50);
            for (int guard = 0; guard < hdrs.Length; guard++)
            {
                int f = Marshal.ReadInt32(hdrs[next], flagsOff);
                if ((f & WHDR_DONE) == 0) break;
                fill(ftmp, frames);
                for (int i = 0; i < tmp.Length; i++) tmp[i] = (short)(Math.Max(-1f, Math.Min(1f, ftmp[i])) * 32767f);
                Marshal.Copy(tmp, 0, datas[next], tmp.Length);
                Marshal.WriteInt32(hdrs[next], flagsOff, f & ~WHDR_DONE);
                int r = waveOutWrite(hwo, hdrs[next], hdrSize);
                if (r != 0) throw new Exception("スピーカー出力エラー (" + r + ")");
                next = (next + 1) % hdrs.Length;
                lastProgress = Environment.TickCount;
            }
            if (Environment.TickCount - lastProgress > 3000)
                throw new Exception("スピーカー出力が止まりました。開き直します");
            return true;
        }

        public void Dispose()
        {
            if (hwo != IntPtr.Zero)
            {
                waveOutReset(hwo);
                for (int i = 0; i < hdrs.Length; i++)
                    if (hdrs[i] != IntPtr.Zero) waveOutUnprepareHeader(hwo, hdrs[i], hdrSize);
                waveOutClose(hwo);
                hwo = IntPtr.Zero;
            }
            for (int i = 0; i < hdrs.Length; i++)
            {
                if (hdrs[i] != IntPtr.Zero) Marshal.FreeHGlobal(hdrs[i]);
                if (datas[i] != IntPtr.Zero) Marshal.FreeHGlobal(datas[i]);
            }
            ev.Close();
        }
    }

    // =====================================================================
    //  鳴らす係
    // =====================================================================
    class Receiver
    {
        public static bool LoopbackOnly;   // テスト用
        public static bool SilentOutput;   // テスト用（スピーカーからは鳴らさない）
        public static bool ForceWaveOut;   // テスト用

        UdpClient udp;
        volatile bool running;
        readonly Dictionary<uint, SourceStream> sources = new Dictionary<uint, SourceStream>();
        volatile string outputDeviceId = "";
        volatile bool reopen;
        public volatile float Master = 1f;
        public volatile string OutputError;
        public volatile string OutputKind = "";
        public volatile int OutputLatencyMs;
        public string StartError;
        public long PacketsReceived;
        public float TestMixPeak;
        public Func<string, int> SavedVolume;
        public Func<string, bool> SavedMute;
        public Func<string, int> SavedPan;
        readonly string name = Environment.MachineName;
        float[] mixF = new float[0];
        float masterPeak, limEnv;

        public string OutputDeviceId
        {
            get { return outputDeviceId; }
            set { outputDeviceId = value ?? ""; reopen = true; }
        }

        public bool Start()
        {
            try
            {
                udp = new UdpClient(new IPEndPoint(LoopbackOnly ? IPAddress.Loopback : IPAddress.Any, Proto.Port));
            }
            catch (SocketException)
            {
                StartError = "ポート " + Proto.Port + " が使えません（別の LANスピーカー が受信中かもしれません）";
                return false;
            }
            try { udp.Client.ReceiveBufferSize = 1 << 20; } catch { }
            Net.NoConnReset(udp.Client);
            running = true;
            Thread t1 = new Thread(NetLoop); t1.IsBackground = true; t1.Priority = ThreadPriority.AboveNormal; t1.Start();
            Thread t2 = new Thread(OutLoop); t2.IsBackground = true; t2.Priority = ThreadPriority.Highest; t2.Start();
            return true;
        }

        public void Stop()
        {
            running = false;
            try { if (udp != null) udp.Close(); } catch { }
        }

        public SourceStream[] Snapshot()
        {
            lock (sources) return sources.Values.OrderBy(s => s.FirstSeenTicks).ToArray();
        }

        public float TakeMasterPeak() { float p = masterPeak; masterPeak = 0; return p; }

        void NetLoop()
        {
            while (running)
            {
                byte[] b;
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                try { b = udp.Receive(ref ep); }
                catch (SocketException) { if (!running) break; continue; }
                catch (ObjectDisposedException) { break; }
                try { Handle(b, ep); } catch { }
            }
        }

        void Reply(IPEndPoint ep)
        {
            byte[] r = Proto.MakeNamed(Proto.Here, name);
            try { udp.Send(r, r.Length, ep); } catch { }
        }

        SourceStream GetOrCreate(uint id, IPEndPoint ep, long now)
        {
            lock (sources)
            {
                SourceStream s;
                if (!sources.TryGetValue(id, out s))
                {
                    s = new SourceStream { Id = id, Name = ep.Address.ToString(), FirstSeenTicks = now };
                    sources[id] = s;
                }
                s.Addr = ep.Address;
                Interlocked.Exchange(ref s.LastSeenTicks, now);
                return s;
            }
        }

        void Handle(byte[] b, IPEndPoint ep)
        {
            int len = b.Length;
            if (!Proto.Check(b, len)) return;
            Interlocked.Increment(ref PacketsReceived);
            long now = DateTime.UtcNow.Ticks;
            switch (b[4])
            {
                case Proto.Discover:
                    Reply(ep);
                    break;
                case Proto.Keepalive:
                    {
                        if (len < 10) return;
                        SourceStream s = GetOrCreate(Proto.GetU32(b, 5), ep, now);
                        string nm = Proto.ReadName(b, len, 9);
                        if (nm.Length > 0 && (!s.Named || s.Name != nm))
                        {
                            s.Name = nm;
                            s.Named = true;
                            if (SavedVolume != null) s.Volume = SavedVolume(nm) / 100f;
                            if (SavedMute != null) s.Muted = SavedMute(nm);
                            if (SavedPan != null) s.Pan = SavedPan(nm) / 100f;
                        }
                        Reply(ep);
                        break;
                    }
                case Proto.Audio:
                    {
                        if (len < Proto.AudioHeader) return;
                        int frames = b[13] | (b[14] << 8);
                        if (frames <= 0 || len < Proto.AudioHeader + frames * 4) return;
                        SourceStream s = GetOrCreate(Proto.GetU32(b, 5), ep, now);
                        s.Write(b, Proto.AudioHeader, frames, Proto.GetU32(b, 9));
                        break;
                    }
                case Proto.Bye:
                    if (len < 9) return;
                    lock (sources) sources.Remove(Proto.GetU32(b, 5));
                    break;
            }
        }

        void OutLoop()
        {
            while (running)
            {
                try { RunOutput(); }
                catch (Exception ex) { OutputError = ex.Message; }
                for (int i = 0; i < 10 && running; i++) Thread.Sleep(100);
            }
        }

        void RunOutput()
        {
            reopen = false;
            IAudioOut o = null;
            try
            {
                if (!ForceWaveOut)
                {
                    try { o = new WasapiPlayer(outputDeviceId); }
                    catch { o = null; }
                }
                if (o == null) o = new WaveOutPlayer(480, 4);
                OutputKind = o.Kind;
                OutputLatencyMs = o.LatencyMs;
                OutputError = null;
                int lastClean = Environment.TickCount;
                while (running && !reopen)
                {
                    if (!o.Pump(Mix)) break;
                    if (Environment.TickCount - lastClean > 1000)
                    {
                        lastClean = Environment.TickCount;
                        long limit = DateTime.UtcNow.Ticks - TimeSpan.TicksPerSecond * 8;
                        lock (sources)
                        {
                            foreach (uint id in sources.Where(kv => Interlocked.Read(ref kv.Value.LastSeenTicks) < limit)
                                                       .Select(kv => kv.Key).ToList())
                                sources.Remove(id);
                        }
                    }
                }
            }
            finally { if (o != null) o.Dispose(); }
        }

        // 全送信元を足し合わせ、リミッターで音割れを防ぐ
        void Mix(float[] outF, int frames)
        {
            int ns = frames * 2;
            if (mixF.Length < ns) mixF = new float[ns];
            Array.Clear(mixF, 0, ns);
            SourceStream[] arr;
            lock (sources) { arr = new SourceStream[sources.Count]; sources.Values.CopyTo(arr, 0); }
            foreach (SourceStream s in arr) s.MixInto(mixF, frames, s.Muted ? 0f : s.Volume);

            const float thr = 0.89f;          // -1dB
            const float rel = 0.99985f;       // 約140ms で戻る
            float m = Master;
            float pk = masterPeak, raw = TestMixPeak, env = limEnv;
            for (int f = 0; f < frames; f++)
            {
                float l = mixF[f * 2], r = mixF[f * 2 + 1];
                float al = Math.Abs(l), ar = Math.Abs(r);
                if (al > raw) raw = al; if (ar > raw) raw = ar;
                l *= m; r *= m;
                float a = Math.Max(Math.Abs(l), Math.Abs(r));
                env = a > env ? a : env * rel;
                if (env > thr) { float gl = thr / env; l *= gl; r *= gl; }
                if (l > 1f) l = 1f; else if (l < -1f) l = -1f;
                if (r > 1f) r = 1f; else if (r < -1f) r = -1f;
                a = Math.Max(Math.Abs(l), Math.Abs(r)); if (a > pk) pk = a;
                outF[f * 2] = SilentOutput ? 0f : l;
                outF[f * 2 + 1] = SilentOutput ? 0f : r;
            }
            limEnv = env;
            masterPeak = pk;
            TestMixPeak = raw;
        }
    }

    // =====================================================================
    //  テスト音（送る係のPCで鳴らす → 取り込まれて受信PCで鳴る）
    // =====================================================================
    static class Chime
    {
        static System.Media.SoundPlayer player;

        public static void Play()
        {
            try
            {
                if (player != null) player.Stop();
                player = new System.Media.SoundPlayer(new MemoryStream(MakeWav()));
                player.Play();
            }
            catch { }
        }

        static byte[] MakeWav()
        {
            int rate = 44100;
            int n = (int)(rate * 1.0);
            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 4);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16);
            w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 4);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate, v = 0;
                v += Note(t, 0.00, 1046.5);  // ド
                v += Note(t, 0.18, 1318.5);  // ミ
                v += Note(t, 0.36, 1568.0);  // ソ
                short s = (short)(Math.Max(-1, Math.Min(1, v * 0.32)) * 32767);
                w.Write(s); w.Write(s);
            }
            w.Flush();
            return ms.ToArray();
        }

        static double Note(double t, double start, double f)
        {
            double u = t - start;
            if (u < 0) return 0;
            double env = Math.Min(1, u / 0.005) * Math.Exp(-u * 5.5);
            return env * (Math.Sin(2 * Math.PI * f * u) + 0.25 * Math.Sin(4 * Math.PI * f * u));
        }
    }

    // =====================================================================
    //  アイコン
    // =====================================================================
    static class IconArt
    {
        public static void Speaker(Graphics g, RectangleF r, Color c, float lw)
        {
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            using (SolidBrush b = new SolidBrush(c))
            {
                PointF[] pts =
                {
                    new PointF(x + w * 0.08f, y + h * 0.36f), new PointF(x + w * 0.28f, y + h * 0.36f),
                    new PointF(x + w * 0.52f, y + h * 0.14f), new PointF(x + w * 0.52f, y + h * 0.86f),
                    new PointF(x + w * 0.28f, y + h * 0.64f), new PointF(x + w * 0.08f, y + h * 0.64f)
                };
                g.FillPolygon(b, pts);
            }
            using (Pen p = new Pen(c, lw))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawArc(p, x + w * 0.42f, y + h * 0.32f, w * 0.26f, h * 0.36f, -55, 110);
                g.DrawArc(p, x + w * 0.38f, y + h * 0.16f, w * 0.48f, h * 0.68f, -55, 110);
            }
        }

        public static void Monitor(Graphics g, RectangleF r, Color c, float lw)
        {
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            using (Pen p = new Pen(c, lw))
            {
                p.LineJoin = LineJoin.Round;
                g.DrawRectangle(p, x + w * 0.06f, y + h * 0.16f, w * 0.56f, h * 0.44f);
                g.DrawLine(p, x + w * 0.34f, y + h * 0.60f, x + w * 0.34f, y + h * 0.74f);
                g.DrawLine(p, x + w * 0.20f, y + h * 0.76f, x + w * 0.48f, y + h * 0.76f);
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawLine(p, x + w * 0.62f, y + h * 0.42f, x + w * 0.94f, y + h * 0.42f);
                g.DrawLine(p, x + w * 0.82f, y + h * 0.30f, x + w * 0.94f, y + h * 0.42f);
                g.DrawLine(p, x + w * 0.82f, y + h * 0.54f, x + w * 0.94f, y + h * 0.42f);
            }
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath gp = new GraphicsPath();
            float d = rad * 2;
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        public static Bitmap Render(int size)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                RectangleF r = new RectangleF(0.5f, 0.5f, size - 1, size - 1);
                using (GraphicsPath gp = Round(r, size * 0.22f))
                using (LinearGradientBrush br = new LinearGradientBrush(r, Color.FromArgb(52, 132, 255), Color.FromArgb(28, 88, 214), 90f))
                    g.FillPath(br, gp);
                float m = size * 0.14f;
                Speaker(g, new RectangleF(m, m, size - 2 * m, size - 2 * m), Color.White, Math.Max(1.5f, size * 0.075f));
            }
            return bmp;
        }

        public static Icon Make(int size)
        {
            return Icon.FromHandle(Render(size).GetHicon());
        }

        public static void SaveIco(string path)
        {
            int[] sizes = { 16, 24, 32, 48, 64, 256 };
            List<byte[]> pngs = new List<byte[]>();
            foreach (int s in sizes)
                using (Bitmap b = Render(s))
                using (MemoryStream ms = new MemoryStream())
                {
                    b.Save(ms, ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            using (BinaryWriter w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (byte[] p in pngs) w.Write(p);
            }
        }
    }

    // =====================================================================
    //  画面部品
    // =====================================================================
    class LevelMeter : Control
    {
        float shown;
        public LevelMeter()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void Push(float peak)
        {
            float db = peak > 1e-5f ? (float)(20 * Math.Log10(peak)) : -100f;
            float v = Math.Max(0f, Math.Min(1f, (db + 54f) / 54f));
            float ns = v > shown ? v : shown * 0.86f;
            if (Math.Abs(ns - shown) > 0.002f) { shown = ns; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : BackColor);
            RectangleF r = new RectangleF(0, 0, Width - 1, Height - 1);
            float rad = Math.Min(r.Height / 2, 6);
            using (GraphicsPath gp = IconArt.Round(r, rad))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(226, 231, 237)))
                g.FillPath(bg, gp);
            float w = r.Width * shown;
            if (w > rad * 2)
            {
                Color c = shown < 0.78f ? Color.FromArgb(46, 184, 114) : shown < 0.93f ? Color.FromArgb(240, 166, 40) : Color.FromArgb(225, 70, 60);
                using (GraphicsPath gp = IconArt.Round(new RectangleF(0, 0, w, r.Height), rad))
                using (SolidBrush b = new SolidBrush(c))
                    g.FillPath(b, gp);
            }
        }
    }

    class ChoiceCard : Control
    {
        public string Title = "", Desc = "";
        public Color Accent = Color.SteelBlue;
        public bool IsSender;
        bool hover;
        public Font TitleFont, DescFont;

        public ChoiceCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Parent != null ? Parent.BackColor : Color.White);
            RectangleF r = new RectangleF(1, 1, Width - 3, Height - 3);
            Color bg = hover ? Color.FromArgb(24, Accent) : Color.White;
            using (GraphicsPath gp = IconArt.Round(r, 12))
            {
                using (SolidBrush b = new SolidBrush(Color.White)) g.FillPath(b, gp);
                using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, gp);
                using (Pen p = new Pen(hover ? Accent : Color.FromArgb(210, 216, 224), hover ? 2.5f : 1.5f)) g.DrawPath(p, gp);
            }
            float ic = Height * 0.56f;
            RectangleF icR = new RectangleF(Height * 0.22f, (Height - ic) / 2, ic, ic);
            using (SolidBrush b = new SolidBrush(Accent)) g.FillEllipse(b, icR);
            RectangleF inner = RectangleF.Inflate(icR, -ic * 0.2f, -ic * 0.2f);
            if (IsSender) IconArt.Monitor(g, inner, Color.White, Math.Max(2f, ic * 0.06f));
            else IconArt.Speaker(g, inner, Color.White, Math.Max(2f, ic * 0.06f));

            float tx = icR.Right + Height * 0.18f;
            Font tf = TitleFont ?? Font, df = DescFont ?? Font;
            SizeF ts = g.MeasureString(Title, tf);
            SizeF ds = g.MeasureString(Desc, df, (int)(Width - tx - 10));
            float total = ts.Height + 4 + ds.Height;
            float ty = (Height - total) / 2;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(30, 36, 44))) g.DrawString(Title, tf, b, tx, ty);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(96, 104, 116)))
                g.DrawString(Desc, df, b, new RectangleF(tx, ty + ts.Height + 4, Width - tx - 10, ds.Height + 4));
        }
    }

    class SourceRow : Panel
    {
        public readonly SourceStream Src;
        readonly Label nameLbl, statusLbl, volLbl, panLbl, volCap, panCap;
        public readonly LevelMeter Meter;
        readonly TrackBar vol, pan;
        readonly CheckBox mute;
        readonly LinkLabel toL, toC, toR;
        readonly float S;
        public Action<SourceStream> Changed;

        public SourceRow(SourceStream s, float scale, Font baseFont)
        {
            Src = s; S = scale;
            BackColor = Color.FromArgb(244, 246, 249);
            Height = P(118);
            Margin = new Padding(0, 0, 0, P(8));
            Font small = new Font(baseFont.FontFamily, 9f);
            nameLbl = new Label { AutoSize = true, Font = new Font(baseFont.FontFamily, 11.5f, FontStyle.Bold), BackColor = Color.Transparent };
            statusLbl = new Label { AutoSize = false, ForeColor = Color.FromArgb(100, 108, 120), Font = small, BackColor = Color.Transparent };
            Meter = new LevelMeter { Height = P(10) };
            mute = new CheckBox { Text = "ミュート", AutoSize = true, BackColor = Color.Transparent };

            volCap = new Label { Text = "音量", AutoSize = true, Font = small, ForeColor = Color.FromArgb(80, 88, 100), BackColor = Color.Transparent };
            vol = new TrackBar { Minimum = 0, Maximum = 200, TickStyle = TickStyle.None, AutoSize = false, Height = P(30), BackColor = BackColor, SmallChange = 5, LargeChange = 10 };
            volLbl = new Label { AutoSize = true, BackColor = Color.Transparent };

            panCap = new Label { Text = "左右", AutoSize = true, Font = small, ForeColor = Color.FromArgb(80, 88, 100), BackColor = Color.Transparent };
            pan = new TrackBar { Minimum = -100, Maximum = 100, TickStyle = TickStyle.BottomRight, TickFrequency = 100, AutoSize = false, Height = P(34), BackColor = BackColor, SmallChange = 5, LargeChange = 25 };
            panLbl = new Label { AutoSize = true, BackColor = Color.Transparent };
            toL = MakeLink("左だけ", -100, small);
            toC = MakeLink("真ん中", 0, small);
            toR = MakeLink("右だけ", 100, small);

            vol.Value = Math.Max(0, Math.Min(200, (int)Math.Round(s.Volume * 100)));
            pan.Value = Math.Max(-100, Math.Min(100, (int)Math.Round(s.Pan * 100)));
            mute.Checked = s.Muted;
            vol.ValueChanged += delegate { Src.Volume = vol.Value / 100f; UpdateLabels(); if (Changed != null) Changed(Src); };
            pan.ValueChanged += delegate
            {
                if (pan.Value != 0 && Math.Abs(pan.Value) <= 4) { pan.Value = 0; return; }   // 真ん中付近は真ん中に吸着
                Src.Pan = pan.Value / 100f; UpdateLabels(); if (Changed != null) Changed(Src);
            };
            mute.CheckedChanged += delegate { Src.Muted = mute.Checked; if (Changed != null) Changed(Src); };
            Controls.AddRange(new Control[] { nameLbl, statusLbl, Meter, mute, volCap, vol, volLbl, panCap, pan, panLbl, toL, toC, toR });
            UpdateLabels();
            Refresh2();
        }

        LinkLabel MakeLink(string text, int value, Font f)
        {
            LinkLabel ll = new LinkLabel { Text = text, AutoSize = true, Font = f, BackColor = Color.Transparent };
            ll.LinkClicked += delegate { pan.Value = value; };
            return ll;
        }

        int P(int v) { return (int)Math.Round(v * S); }

        void UpdateLabels()
        {
            volLbl.Text = vol.Value + "%";
            int r = (pan.Value + 100) / 2;          // 右スピーカーから鳴る割合
            panLbl.Text = pan.Value == 0 ? "左右そのまま" : "左" + (100 - r) + " : 右" + r;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (toR == null) return;
            int w = ClientSize.Width;
            int left = P(14);
            int rightW = Math.Max(P(250), (int)(w * 0.52));
            int rx = w - rightW - P(8);
            int capW = P(38), lblW = P(96);
            int barW = rightW - capW - lblW;
            nameLbl.Location = new Point(left, P(8));
            statusLbl.SetBounds(left, P(34), Math.Max(P(60), rx - left - P(8)), P(38));
            Meter.SetBounds(left, P(76), Math.Max(P(40), rx - left - P(16)), P(10));
            mute.Location = new Point(left - P(2), P(90));

            volCap.Location = new Point(rx, P(13));
            vol.SetBounds(rx + capW, P(8), barW, P(30));
            volLbl.Location = new Point(rx + capW + barW + P(2), P(12));

            panCap.Location = new Point(rx, P(53));
            pan.SetBounds(rx + capW, P(48), barW, P(34));
            panLbl.Location = new Point(rx + capW + barW + P(2), P(52));
            int ly = P(88);
            toL.Location = new Point(rx + capW + P(6), ly);
            toC.Location = new Point(rx + capW + barW / 2 - toC.PreferredWidth / 2, ly);
            toR.Location = new Point(rx + capW + barW - toR.PreferredWidth - P(6), ly);
        }

        public static int ExtraLatencyMs = 40;   // 出力 + 送信側の取り込み

        public void Refresh2()
        {
            string n = Src.Name;
            if (nameLbl.Text != n) nameLbl.Text = n;
            string st = (Src.IsPlaying ? "♪ 再生中" : "無音（待機中）");
            IPAddress a = Src.Addr;
            if (a != null && Src.Named) st += "   " + a;
            if (Src.IsPlaying) st += "   遅延 約" + (Src.BufferMs + ExtraLatencyMs) + "ms";
            if (Src.Underruns > 0 || Src.LostPackets > 0) st += "   途切れ " + (Src.Underruns + Src.LostPackets) + "回";
            if (statusLbl.Text != st) statusLbl.Text = st;
            statusLbl.ForeColor = Src.IsPlaying ? Color.FromArgb(30, 140, 85) : Color.FromArgb(110, 118, 130);
            if (mute.Checked != Src.Muted) mute.Checked = Src.Muted;
            Meter.Push(Src.Muted ? 0 : Src.TakePeak() * Src.Volume);
        }
    }

    // =====================================================================
    //  メイン画面
    // =====================================================================
    class MainForm : Form
    {
        public static bool TestMode;

        readonly bool startHidden;
        bool allowShow;
        readonly NotifyIcon tray;
        readonly Panel content;
        readonly System.Windows.Forms.Timer uiTimer;
        readonly float S;
        readonly Font titleFont, bigFont, smallFont, boldFont;
        Sender sender;
        Receiver receiver;
        string mode = "";
        bool balloonShown;

        // 受信画面
        FlowLayoutPanel srcFlow;
        Label emptyLbl, rxStatus, rxCount;
        LevelMeter masterMeter;
        readonly Dictionary<uint, SourceRow> rows = new Dictionary<uint, SourceRow>();

        // 送信画面
        Label txStatus, txDetail, txDevice;
        LevelMeter txMeter;
        TextBox txIp;

        static readonly Color Blue = Color.FromArgb(40, 112, 235);
        static readonly Color Green = Color.FromArgb(26, 160, 100);
        static readonly Color Orange = Color.FromArgb(214, 128, 20);
        static readonly Color Red = Color.FromArgb(205, 55, 50);
        static readonly Color Gray = Color.FromArgb(100, 108, 120);

        public MainForm(bool startMinimized)
        {
            Text = "LANスピーカー";
            using (Graphics g = CreateGraphics()) S = g.DpiX / 96f;
            string ff = FontFamily.Families.Any(f => f.Name == "Yu Gothic UI") ? "Yu Gothic UI" : "Meiryo UI";
            Font = new Font(ff, 10f);
            titleFont = new Font(ff, 15f, FontStyle.Bold);
            bigFont = new Font(ff, 12.5f, FontStyle.Bold);
            boldFont = new Font(ff, 10f, FontStyle.Bold);
            smallFont = new Font(ff, 9f);
            BackColor = Color.White;
            ClientSize = new Size(P(600), P(660));
            MinimumSize = new Size(P(460), P(520));
            StartPosition = FormStartPosition.CenterScreen;
            Icon = IconArt.Make(32);

            tray = new NotifyIcon { Icon = Icon, Text = "LANスピーカー", Visible = !TestMode };
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("開く", null, delegate { ShowFromTray(); });
            menu.Items.Add("終了", null, delegate { Close(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowFromTray(); };
            tray.BalloonTipClicked += delegate { ShowFromTray(); };

            content = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            Controls.Add(content);

            uiTimer = new System.Windows.Forms.Timer { Interval = 60 };
            uiTimer.Tick += OnTick;
            uiTimer.Start();

            mode = Settings.Get("mode", "");
            startHidden = startMinimized && mode != "";
            allowShow = !startHidden;

            if (mode == "receive") ShowReceiver();
            else if (mode == "send") ShowSender();
            else ShowModeSelect();
        }

        int P(int v) { return (int)Math.Round(v * S); }

        protected override void SetVisibleCore(bool value)
        {
            if (!allowShow)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        public void ShowFromTray()
        {
            allowShow = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && mode != "" && !TestMode)
            {
                Hide();
                if (!balloonShown)
                {
                    balloonShown = true;
                    tray.ShowBalloonTip(3000, "LANスピーカー", "バックグラウンドで動作中です。右下のアイコンから開けます。", ToolTipIcon.Info);
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopAll();
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        void StopAll()
        {
            if (sender != null) { sender.Stop(); sender = null; }
            if (receiver != null) { receiver.Stop(); receiver = null; }
            rows.Clear(); AcceptButton = null;
            srcFlow = null; txStatus = null; rxStatus = null;
            foreach (Control c in content.Controls.Cast<Control>().ToArray()) c.Dispose();
            content.Controls.Clear();
        }

        // ---------- 部品を作る補助 ----------
        Label L(string text, Font f, Color c)
        {
            return new Label { Text = text, AutoSize = true, Font = f ?? Font, ForeColor = c, Margin = new Padding(0, P(2), 0, P(2)) };
        }

        TableLayoutPanel Stack(Padding pad)
        {
            TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = pad, BackColor = Color.White };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }

        void Add(TableLayoutPanel t, Control c, bool fill)
        {
            t.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            t.Controls.Add(c, 0, t.RowCount);
            t.RowCount++;
        }

        Button Btn(string text, EventHandler click)
        {
            Button b = new Button
            {
                Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(P(8), P(3), P(8), P(3)), Margin = new Padding(0, P(4), P(8), P(4)),
                FlatStyle = FlatStyle.System, UseVisualStyleBackColor = true
            };
            b.Click += click;
            return b;
        }

        FlowLayoutPanel Row(params Control[] cs)
        {
            FlowLayoutPanel f = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0), Dock = DockStyle.Fill
            };
            f.Controls.AddRange(cs);
            return f;
        }

        Control Separator()
        {
            return new Panel { Height = 1, Dock = DockStyle.Fill, BackColor = Color.FromArgb(226, 230, 236), Margin = new Padding(0, P(10), 0, P(10)) };
        }

        Control AutoStartBox()
        {
            LinkLabel ll = new LinkLabel { Text = "PCの起動時に自動で開始したいときは…", AutoSize = true, Margin = new Padding(0, P(4), 0, P(4)) };
            ll.LinkClicked += delegate
            {
                MessageBox.Show(this,
                    "Windows の「スタートアップ」フォルダーに、LanSpeaker.exe のショートカットを置くと自動で起動します。\n\n" +
                    "1. このあと開くフォルダーに、LanSpeaker.exe を右ドラッグして「ショートカットをここに作成」\n" +
                    "2.（お好みで）ショートカットを右クリック →「プロパティ」→「実行時の大きさ」を「最小化」にすると、\n" +
                    "    画面を出さずに右下の通知領域で動き始めます。\n\n" +
                    "やめたいときは、そのショートカットを削除するだけです。",
                    "LANスピーカー", MessageBoxButtons.OK, MessageBoxIcon.Information);
                try { Process.Start("explorer.exe", "shell:startup"); } catch { }
            };
            return ll;
        }

        Button ChangeModeButton()
        {
            return Btn("役割を変更", delegate
            {
                if (MessageBox.Show(this, "いまの動作を止めて、役割の選択画面に戻りますか？", "LANスピーカー",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                Settings.Set("mode", "");
                ShowModeSelect();
            });
        }

        // ---------- 役割の選択 ----------
        internal void ShowModeSelect()
        {
            StopAll();
            mode = "";
            TableLayoutPanel t = Stack(new Padding(P(28), P(26), P(28), P(20)));
            Add(t, L("LANスピーカー", titleFont, Color.FromArgb(30, 36, 44)), false);
            Add(t, L("このPCの役割を選んでください", Font, Gray), false);
            Add(t, new Panel { Height = P(14), Margin = new Padding(0) }, false);

            ChoiceCard rx = new ChoiceCard
            {
                Title = "このPCで鳴らす", Desc = "スピーカーがつながっているPCはこちら。\n他のPCから届いた音をまとめて再生します。",
                Accent = Blue, Height = P(124), Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, P(14)),
                TitleFont = bigFont, DescFont = Font
            };
            rx.Click += delegate { Settings.Set("mode", "receive"); ShowReceiver(); };
            ChoiceCard tx = new ChoiceCard
            {
                Title = "このPCの音を送る", Desc = "スピーカーがないPCはこちら。\nこのPCで鳴っている音を、鳴らす係のPCへ送ります。",
                Accent = Green, IsSender = true, Height = P(124), Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, P(14)),
                TitleFont = bigFont, DescFont = Font
            };
            tx.Click += delegate { Settings.Set("mode", "send"); ShowSender(); };
            Add(t, rx, false);
            Add(t, tx, false);
            Add(t, L("・「鳴らす」にするのは1台だけ。ほかのPCは全部「送る」にします。\n" +
                     "・同じネットワーク（同じルーター / Wi-Fi）につながっていれば、自動でつながります。\n" +
                     "・次回からは、選んだ役割ですぐに始まります。", smallFont, Gray), false);
            Add(t, new Panel(), true);
            content.Controls.Add(t);
        }

        // ---------- 鳴らす係 ----------
        internal void ShowReceiver()
        {
            StopAll();
            mode = "receive";
            receiver = new Receiver();
            receiver.SavedVolume = n => Settings.GetInt("vol." + n, 100);
            receiver.SavedMute = n => Settings.Get("mute." + n, "0") == "1";
            receiver.SavedPan = n => Settings.GetInt("pan." + n, 0);
            receiver.Master = Settings.GetInt("master", 100) / 100f;
            List<KeyValuePair<string, string>> devs = CoreAudio.RenderDevices();
            string savedDev = Settings.Get("outdev2", "");
            int devIdx = devs.FindIndex(d => d.Key == savedDev);
            receiver.OutputDeviceId = devIdx >= 0 ? savedDev : "";
            receiver.Start();

            TableLayoutPanel t = Stack(new Padding(P(24), P(20), P(24), P(16)));
            Add(t, L("鳴らす係（このPCのスピーカーで再生）", bigFont, Blue), false);
            rxStatus = L("", Font, Green);
            Add(t, rxStatus, false);
            Add(t, L("このPC: " + Environment.MachineName + "    IP: " + string.Join(" / ", Net.LocalIPv4()), smallFont, Gray), false);
            Add(t, Separator(), false);

            rxCount = L("届いている音", boldFont, Color.FromArgb(30, 36, 44));
            Add(t, rxCount, false);
            srcFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
                Margin = new Padding(0, P(6), 0, 0), BackColor = Color.White
            };
            srcFlow.Resize += delegate { FitRows(); };
            emptyLbl = L("まだ音を送ってくるPCがありません。\n\n音を送りたいPCで LanSpeaker.exe を起動し、\n「このPCの音を送る」を選ぶと、ここに表示されます。",
                Font, Gray);
            emptyLbl.Margin = new Padding(P(4), P(10), 0, 0);
            srcFlow.Controls.Add(emptyLbl);
            Add(t, srcFlow, true);
            Add(t, Separator(), false);

            // 出力先
            ComboBox cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = P(300), Margin = new Padding(0, P(2), 0, P(2)) };
            cb.Items.Add("既定のスピーカー");
            foreach (KeyValuePair<string, string> d in devs) cb.Items.Add(d.Value);
            cb.SelectedIndex = devIdx + 1;
            cb.SelectedIndexChanged += delegate
            {
                if (receiver == null) return;
                string id = cb.SelectedIndex > 0 ? devs[cb.SelectedIndex - 1].Key : "";
                receiver.OutputDeviceId = id;
                Settings.Set("outdev2", id);
            };
            Label outLbl = L("出力先", Font, Color.Black); outLbl.AutoSize = false; outLbl.Size = new Size(P(90), P(26)); outLbl.Margin = new Padding(0, P(6), P(8), 0);
            Add(t, Row(outLbl, cb), false);

            // 全体の音量
            TrackBar mv = new TrackBar { Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, AutoSize = false, Width = P(200), Height = P(30), Value = Settings.GetInt("master", 100) };
            Label mvLbl = L(mv.Value + "%", Font, Color.Black); mvLbl.Margin = new Padding(0, P(6), P(8), 0);
            mv.ValueChanged += delegate
            {
                if (receiver != null) receiver.Master = mv.Value / 100f;
                mvLbl.Text = mv.Value + "%";
                Settings.Set("master", mv.Value.ToString());
            };
            masterMeter = new LevelMeter { Width = P(90), Height = P(10), Margin = new Padding(0, P(12), 0, 0) };
            Label mLbl = L("全体の音量", Font, Color.Black); mLbl.AutoSize = false; mLbl.Size = new Size(P(90), P(26)); mLbl.Margin = new Padding(0, P(6), P(8), 0);
            Add(t, Row(mLbl, mv, mvLbl, masterMeter), false);

            Add(t, AutoStartBox(), false);
            Add(t, Row(Btn("ほかのPCが表示されないとき…", delegate { FirewallHelp(); }), ChangeModeButton()), false);
            content.Controls.Add(t);
        }

        void FitRows()
        {
            if (srcFlow == null) return;
            int w = srcFlow.ClientSize.Width - P(4);
            foreach (Control c in srcFlow.Controls)
                if (c is SourceRow) c.Width = w;
        }

        void SaveRow(SourceStream s)
        {
            if (!s.Named) return;
            Settings.Set("vol." + s.Name, ((int)Math.Round(s.Volume * 100)).ToString());
            Settings.Set("mute." + s.Name, s.Muted ? "1" : "0");
            Settings.Set("pan." + s.Name, ((int)Math.Round(s.Pan * 100)).ToString());
        }

        void TickReceiver()
        {
            if (receiver == null || srcFlow == null) return;
            if (receiver.StartError != null) { rxStatus.Text = "● " + receiver.StartError; rxStatus.ForeColor = Red; }
            else if (receiver.OutputError != null) { rxStatus.Text = "● スピーカー出力: " + receiver.OutputError; rxStatus.ForeColor = Orange; }
            else
            {
                string st = "● 受信中";
                if (receiver.OutputKind.Length > 0) st += "   （出力 " + receiver.OutputKind + " " + receiver.OutputLatencyMs + "ms）";
                if (rxStatus.Text != st) rxStatus.Text = st;
                rxStatus.ForeColor = Green;
            }
            SourceRow.ExtraLatencyMs = receiver.OutputLatencyMs + 12;

            SourceStream[] list = receiver.Snapshot();
            bool same = list.Length == rows.Count && list.All(s => rows.ContainsKey(s.Id));
            if (!same)
            {
                srcFlow.SuspendLayout();
                foreach (SourceRow r in rows.Values) { srcFlow.Controls.Remove(r); r.Dispose(); }
                rows.Clear();
                foreach (SourceStream s in list)
                {
                    SourceRow r = new SourceRow(s, S, Font);
                    r.Changed = SaveRow;
                    rows[s.Id] = r;
                    srcFlow.Controls.Add(r);
                }
                emptyLbl.Visible = list.Length == 0;
                FitRows();
                srcFlow.ResumeLayout();
            }
            foreach (SourceRow r in rows.Values) r.Refresh2();
            rxCount.Text = list.Length == 0 ? "届いている音" : "届いている音（" + list.Length + "台）";
            masterMeter.Push(receiver.TakeMasterPeak());
        }

        void FirewallHelp()
        {
            if (MessageBox.Show(this,
                    "Windows ファイアウォールで LANスピーカー が止められている可能性があります。\n\n" +
                    "このあと開く「許可されたアプリ」の画面で:\n" +
                    "1.「設定の変更」を押す\n" +
                    "2. 一覧の「LanSpeaker」の「プライベート」と「パブリック」にチェック\n" +
                    "   （一覧に無ければ「別のアプリの許可」から LanSpeaker.exe を追加）\n" +
                    "3.「OK」を押して、LANスピーカー を起動し直す\n\n" +
                    "それでもダメなときは、この画面の IP アドレスを送る係の「自動で見つからないとき」に入れてください。",
                    "LANスピーカー", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            try { Process.Start("control.exe", "/name Microsoft.WindowsFirewall /page pageConfigureApps"); } catch { }
        }

        // ---------- 送る係 ----------
        internal void ShowSender()
        {
            StopAll();
            mode = "send";
            sender = new Sender();
            sender.SetManual(Settings.Get("manualIp", ""));
            sender.Start();

            TableLayoutPanel t = Stack(new Padding(P(24), P(20), P(24), P(16)));
            Add(t, L("送る係（このPCの音を送信）", bigFont, Green), false);
            txStatus = L("", new Font(Font.FontFamily, 12f, FontStyle.Bold), Orange);
            txStatus.Margin = new Padding(0, P(10), 0, P(2));
            Add(t, txStatus, false);
            txDetail = L("", smallFont, Gray);
            Add(t, txDetail, false);
            Add(t, Separator(), false);

            Add(t, L("このPCで鳴っている音", boldFont, Color.FromArgb(30, 36, 44)), false);
            txMeter = new LevelMeter { Height = P(14), Dock = DockStyle.Fill, Margin = new Padding(0, P(6), 0, P(4)) };
            Add(t, txMeter, false);
            txDevice = L("", smallFont, Gray);
            Add(t, txDevice, false);

            CheckBox pause = new CheckBox { Text = "一時停止（送らない）", AutoSize = true, Margin = new Padding(P(8), P(8), 0, 0) };
            pause.CheckedChanged += delegate { if (sender != null) sender.Paused = pause.Checked; };
            Add(t, Row(Btn("テスト音を鳴らす", delegate { Chime.Play(); }), pause), false);
            Add(t, L("「テスト音を鳴らす」を押すと、鳴らす係のPCから「ポロン」と聞こえればOKです。", smallFont, Gray), false);
            Add(t, Separator(), false);

            Add(t, L("自動で見つからないとき", boldFont, Color.FromArgb(30, 36, 44)), false);
            Add(t, L("鳴らす係のPCの画面に出ている IP アドレスを入れて「設定」。空欄にすると自動に戻ります。", smallFont, Gray), false);
            txIp = new TextBox { Width = P(200), Text = sender.ManualIp, Margin = new Padding(0, P(5), P(8), 0) };
            Button set = Btn("設定", delegate
            {
                string ip = txIp.Text.Trim();
                Settings.Set("manualIp", ip);
                if (sender != null) sender.SetManual(ip);
            });
            Add(t, Row(txIp, set), false);
            Add(t, new Panel { Height = P(4) }, true);
            Add(t, Separator(), false);
            Add(t, AutoStartBox(), false);
            Add(t, Row(ChangeModeButton()), false);
            content.Controls.Add(t);
            AcceptButton = set;
        }

        void TickSender()
        {
            if (sender == null || txStatus == null) return;
            string s, d; Color c;
            IPEndPoint tg = sender.Target;
            if (sender.CaptureError != null)
            {
                s = "● 音を取り込めません"; c = Red;
                d = sender.CaptureError + "\nこのPCに音の出力先（スピーカー端子・HDMIモニターなど）が1つ必要です。\n" +
                    "何も無い場合は「VB-CABLE」などの仮想オーディオを入れると使えます。";
            }
            else if (sender.Paused) { s = "● 一時停止中"; c = Gray; d = "チェックを外すと送信を再開します。"; }
            else if (sender.Connected && tg != null)
            {
                s = "● 送信中 → " + (sender.ReceiverName.Length > 0 ? sender.ReceiverName : "鳴らす係") ; c = Green;
                d = "送信先: " + tg.Address + "    このPC: " + Environment.MachineName;
            }
            else if (sender.ManualIp.Length > 0)
            {
                s = "● " + sender.ManualIp + " に接続できません"; c = Orange;
                d = "鳴らす係のPCで LANスピーカー が起動しているか、IP アドレスが合っているか確認してください。";
            }
            else
            {
                s = "● 鳴らす係のPCを探しています…"; c = Orange;
                d = "スピーカーのあるPCで LANスピーカー を起動し、「このPCで鳴らす」を選んでください。\n" +
                    "見つからない場合は、鳴らす係のPCで「ほかのPCが表示されないとき…」を見てください。";
            }
            if (txStatus.Text != s) txStatus.Text = s;
            txStatus.ForeColor = c;
            if (txDetail.Text != d) txDetail.Text = d;
            string dev = sender.DeviceInfo.Length > 0 ? "取り込み元: " + sender.DeviceInfo : "";
            if (txDevice.Text != dev) txDevice.Text = dev;
            txMeter.Push(sender.TakePeak());
        }

        void OnTick(object o, EventArgs e)
        {
            if (!Visible && !TestMode)
            {
                // 非表示中は画面更新しない（メーターの値だけ捨てる）
                if (sender != null) sender.TakePeak();
                return;
            }
            try
            {
                if (mode == "receive") TickReceiver();
                else if (mode == "send") TickSender();
            }
            catch (Exception ex) { if (TestMode) File.AppendAllText(Path.Combine(Path.GetTempPath(), "LanSpeaker_tick_error.txt"), ex + Environment.NewLine); }
            string tip = mode == "receive" ? "LANスピーカー（鳴らす係）" : mode == "send" ? "LANスピーカー（送る係）" : "LANスピーカー";
            if (tray.Text != tip) tray.Text = tip;
        }
    }

#if SELFTEST
    // =====================================================================
    //  自己テスト（開発用）
    // =====================================================================
    static class SelfTest
    {
        static void Prepare()
        {
            Proto.Port = 49659;
            Receiver.LoopbackOnly = true;
            Receiver.SilentOutput = true;
            MainForm.TestMode = true;
            Settings.FilePath = Path.Combine(Path.GetTempPath(), "LanSpeaker_test_" + Process.GetCurrentProcess().Id + ".txt");
        }

        public static void Run(string report)
        {
            Prepare();
            StringBuilder sb = new StringBuilder();
            try
            {
                // 1) リサンプラ品質（44.1kHz の 1kHz サイン → 48kHz、理想波形との最大誤差）
                {
                    CubicResampler rs = new CubicResampler(44100, 48000);
                    float[] inp = new float[441 * 2];
                    float[] outp = new float[0];
                    long inIdx = 0, k = 0; double errMax = 0; double step = 44100.0 / 48000;
                    for (int blk = 0; blk < 200; blk++)
                    {
                        for (int i = 0; i < 441; i++) { float v = (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * (inIdx + i) / 44100.0)); inp[i * 2] = v; inp[i * 2 + 1] = v; }
                        inIdx += 441;
                        int n = rs.Process(inp, 441, ref outp);
                        for (int i = 0; i < n; i++, k++)
                        {
                            if (k < 10) continue;
                            double ideal = 0.5 * Math.Sin(2 * Math.PI * 1000 * (k * step - 2) / 44100.0);
                            errMax = Math.Max(errMax, Math.Abs(outp[i * 2] - ideal));
                        }
                    }
                    sb.AppendLine("resampler 44.1k->48k: frames=" + k + " (expect ~96000) maxErr=" + errMax.ToString("0.000000") + " (linear was ~0.0013)");
                }

                // 1b) 左右の振り分け（L=0.4, R=0.2 の音）
                foreach (float pv in new float[] { -1f, -0.5f, 0f, 0.5f, 1f })
                {
                    SourceStream ps = new SourceStream();
                    ps.Pan = pv;
                    byte[] pb = new byte[Proto.AudioHeader + 240 * 4];
                    short sl = (short)(0.4 * 32768), sr = (short)(0.2 * 32768);
                    for (int i = 0; i < 240; i++) { int o = Proto.AudioHeader + i * 4; pb[o] = (byte)sl; pb[o + 1] = (byte)(sl >> 8); pb[o + 2] = (byte)sr; pb[o + 3] = (byte)(sr >> 8); }
                    long tt = DateTime.UtcNow.Ticks;
                    for (uint q = 0; q < 20; q++) ps.WriteAt(pb, Proto.AudioHeader, 240, q, tt);
                    float[] pm = new float[960];
                    ps.MixAt(pm, 480, 1f, tt);
                    sb.AppendLine("pan " + pv.ToString("+0.0;-0.0;0.0") + ": outL=" + pm[958].ToString("0.000") + " outR=" + pm[959].ToString("0.000"));
                }
                sb.AppendLine("  (expect -1: 0.300/0.000, -0.5: 0.350/0.100, 0: 0.400/0.200, +0.5: 0.200/0.250, +1: 0.000/0.300)");

                // 2) ネットワークのゆらぎを再現したシミュレーション
                sb.AppendLine(Simulate("wired (drift +80ppm, jitter 2ms)", 80e-6, 2, 0, 0, 0, 1, 120));
                sb.AppendLine(Simulate("wifi  (drift -80ppm, jitter 20ms, 150ms stall every 15s, loss 0.2%)", -80e-6, 20, 15, 150, 0.002, 2, 180));
                sb.AppendLine(Simulate("drift (drift +300ppm, jitter 3ms)", 300e-6, 3, 0, 0, 0, 3, 300));

                // 3) 実際の送受信：送る係 2台同時（ループバック、スピーカーには出さない）
                Receiver rx = new Receiver();
                rx.SavedVolume = n => 100;
                bool ok = rx.Start();
                sb.AppendLine("receiver start: " + ok + " " + rx.StartError);
                Sender tx1 = new Sender(); tx1.TestNoCapture = true; tx1.SetManual("127.0.0.1"); tx1.Start(); tx1.StartInjection(3.0, 440);
                Sender tx2 = new Sender(); tx2.TestNoCapture = true; tx2.SetManual("127.0.0.1"); tx2.Start(); tx2.StartInjection(3.0, 660);
                Thread.Sleep(4000);
                sb.AppendLine("output: " + rx.OutputKind + " latency=" + rx.OutputLatencyMs + "ms error=" + (rx.OutputError ?? "(none)"));
                sb.AppendLine("sender1: " + tx1.DeviceInfo + " err=" + (tx1.CaptureError ?? "-") + " connected=" + tx1.Connected + " sent=" + tx1.PacketsSent);
                sb.AppendLine("sender2: connected=" + tx2.Connected + " sent=" + tx2.PacketsSent);
                foreach (SourceStream s in rx.Snapshot())
                    sb.AppendLine("  source '" + s.Name + "' frames=" + s.FramesReceived + " underruns=" + s.Underruns + " lost=" + s.LostPackets + " buffer=" + s.BufferMs + "ms");
                sb.AppendLine("sources: " + rx.Snapshot().Length + " (expect 2)   mix peak=" + rx.TestMixPeak.ToString("0.000") + " (expect ~0.6)");
                tx1.Stop(); tx2.Stop();
                Thread.Sleep(500);
                sb.AppendLine("sources after Bye: " + rx.Snapshot().Length + " (expect 0)");

                // 4) 取り込みを Windows 側で 44.1kHz に変換させる経路（48kHz 以外のPC向け）
                Sender.TestRequestRate = 44100;
                Sender tx3 = new Sender(); tx3.SetManual("127.0.0.1"); tx3.Start();
                Thread.Sleep(1500);
                sb.AppendLine("autoconvert capture: " + tx3.DeviceInfo + " err=" + (tx3.CaptureError ?? "-") + " sent=" + tx3.PacketsSent);
                tx3.Stop();
                Sender.TestRequestRate = 0;
                rx.Stop();

                sb.AppendLine("render devices: " + string.Join(" | ", CoreAudio.RenderDevices().Select(d => d.Value)));
                sb.AppendLine("DONE");
            }
            catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
            File.WriteAllText(report, sb.ToString(), Encoding.UTF8);
            try { File.Delete(Settings.FilePath); } catch { }
        }

        // 送信 → ネットワーク（ゆらぎ・詰まり・欠落）→ 受信バッファ → 出力 を時間を進めながら再現
        static string Simulate(string label, double drift, double jitterMs, double stallEverySec, double stallMs, double lossProb, int seed, double seconds)
        {
            Random rnd = new Random(seed);
            SourceStream ss = new SourceStream();
            long T0 = DateTime.UtcNow.Ticks;
            int totalPk = (int)(seconds * 200);
            double[] arr = new double[totalPk];
            double prevArr = 0;
            for (int k = 0; k < totalPk; k++)
            {
                double send = (Math.Floor(k / 2.0) + 1) * 10.0 / (1 + drift);   // 送信側は10msごとに2パケット
                double a = send + 1 + rnd.NextDouble() * jitterMs;
                if (stallEverySec > 0)
                {
                    double per = stallEverySec * 1000;
                    double ph = a % per;
                    if (a > per && ph < stallMs) a = a - ph + stallMs;   // Wi-Fi の詰まり：まとめて遅れて届く
                }
                if (a < prevArr) a = prevArr;   // 追い越しはしない
                arr[k] = a; prevArr = a;
            }
            byte[] pk = new byte[Proto.AudioHeader + 240 * 4];
            float[] mix = new float[960];
            int oi = 0, clicks = 0, maxBufMs = 0, nBuf = 0;
            double prev1 = 0, prev2 = 0, sumBuf = 0;
            long outCount = 0;
            for (int t = 0; t < seconds * 1000 - 300; t++)
            {
                long now = T0 + t * TimeSpan.TicksPerMillisecond;
                while (oi < totalPk && arr[oi] <= t)
                {
                    int k = oi++;
                    if (rnd.NextDouble() < lossProb) continue;
                    for (int i = 0; i < 240; i++)
                    {
                        short v = (short)(0.5 * 32767 * Math.Sin(2 * Math.PI * 1000 * (k * 240.0 + i) / 48000));
                        int o = Proto.AudioHeader + i * 4;
                        pk[o] = (byte)v; pk[o + 1] = (byte)(v >> 8); pk[o + 2] = (byte)v; pk[o + 3] = (byte)(v >> 8);
                    }
                    ss.WriteAt(pk, Proto.AudioHeader, 240, (uint)k, now);
                }
                if (t % 10 == 0)
                {
                    Array.Clear(mix, 0, 960);
                    ss.MixAt(mix, 480, 1f, now);
                    for (int i = 0; i < 480; i++)
                    {
                        double y = mix[i * 2];
                        if (outCount >= 2 && Math.Abs(y - 2 * prev1 + prev2) > 0.05) clicks++;
                        prev2 = prev1; prev1 = y; outCount++;
                    }
                    if (t > 5000) { sumBuf += ss.BufferMs; nBuf++; }
                    if (t > 30000 && ss.BufferMs > maxBufMs) maxBufMs = ss.BufferMs;
                }
            }
            return label + " -> underruns=" + ss.Underruns + " lost=" + ss.LostPackets +
                   " avgBuf=" + (sumBuf / Math.Max(1, nBuf)).ToString("0.0") + "ms maxBuf(>30s)=" + maxBufMs + "ms final=" + ss.BufferMs +
                   "ms safety=" + ss.SafetyMs + "ms ratio=" + ss.Ratio.ToString("0.00000") + " clicks=" + clicks;
        }

        // 画面のスクリーンショットを保存（開発用）
        public static void Screens(string dir)
        {
            Prepare();
            Directory.CreateDirectory(dir);
            Settings.Set("mode", "");
            Settings.Set("pan." + Environment.MachineName, "60");
            Application.EnableVisualStyles();
            MainForm f = new MainForm(false);
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(40, 40);
            Sender bgTx = null; Receiver bgRx = null;
            int step = 0;
            System.Windows.Forms.Timer tm = new System.Windows.Forms.Timer { Interval = 1500 };
            tm.Tick += delegate
            {
                try
                {
                    switch (step++)
                    {
                        case 0: Shot(f, dir, "1_mode.png"); f.ShowReceiver(); bgTx = new Sender(); bgTx.TestNoCapture = true; bgTx.SetManual("127.0.0.1"); bgTx.Start(); bgTx.StartInjection(0, 330); break;
                        case 1: break;
                        case 2: Shot(f, dir, "2_receiver.png"); bgTx.Stop(); Settings.Set("manualIp", "127.0.0.1"); f.ShowSender(); break;
                        case 3: Shot(f, dir, "3_sender_searching.png"); bgRx = new Receiver(); bgRx.Start(); break;
                        case 4: break;
                        case 5: Shot(f, dir, "4_sender_connected.png"); bgRx.Stop(); f.Close(); break;
                    }
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString()); f.Close(); }
            };
            tm.Start();
            Application.Run(f);
            try { File.Delete(Settings.FilePath); } catch { }
        }

        static void Shot(Form f, string dir, string name)
        {
            using (Bitmap b = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
                b.Save(Path.Combine(dir, name), ImageFormat.Png);
            }
        }
    }

#endif

    // =====================================================================
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            try { timeBeginPeriod(1); } catch { }   // Sleep の精度を 1ms に（音の取り込み・送信の間隔を安定させる）
#if SELFTEST
            if (args.Length >= 2 && args[0] == "--make-icon") { IconArt.SaveIco(args[1]); return; }
            if (args.Length >= 2 && args[0] == "--selftest") { SelfTest.Run(args[1]); return; }
            if (args.Length >= 2 && args[0] == "--screens") { SelfTest.Screens(args[1]); return; }
#endif

            bool startMinimized = StartupState.Minimized();
            bool created;
            using (Mutex mtx = new Mutex(true, "Local\\LanSpeaker_SingleInstance_v1", out created))
            using (EventWaitHandle showEv = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\LanSpeaker_Show_v1"))
            {
                if (!created)
                {
                    // すでに起動中 → そちらの画面を前に出す
                    showEv.Set();
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                MainForm form = new MainForm(startMinimized);
                Thread w = new Thread(delegate ()
                {
                    while (true)
                    {
                        showEv.WaitOne();
                        try { form.BeginInvoke(new MethodInvoker(form.ShowFromTray)); } catch { return; }
                    }
                });
                w.IsBackground = true;
                w.Start();
                Application.Run(form);
            }
        }
    }
}
