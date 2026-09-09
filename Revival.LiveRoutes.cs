using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Globalization;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Configuration;
using UnityEngine;

namespace NextDayRevival
{
    // Separate authenticated data plane. Never overwrites receipt-covered files.
    // The certificate pin is scoped to this socket, never a process-wide bypass.
    internal static class LiveRoutes
    {
        static ConfigEntry<string> _url, _pin;
        static float _next;
        static bool _busy;
        static volatile bool _finished;
        static Snapshot _pending;
        static string _failure = "";
        static string _lastFailure = "";
        internal static Snapshot Current;
        internal static string LastError { get { return _lastFailure; } }
        internal static bool Ready { get { return Current != null; } }
        internal sealed class Snapshot
        {
            internal string Revision;
            internal string[] Routes, Crew;
        }

        internal static void BindConfig(ConfigFile cfg)
        {
            _url = cfg.Bind("LiveRoutes", "Url", "https://187.124.117.145:8795/runtime/routes",
                "Read-only published route snapshot. HTTPS is required.");
            _pin = cfg.Bind("LiveRoutes", "CertificateSha256",
                "c70c04581a051aaf9a84b97b795b17d17b38a30a94fcfbf72dfdbf85a1c39881",
                "SHA256 of the editor TLS certificate, obtained from the server administrator.");
        }

        internal static void Tick()
        {
            if (_finished)
            {
                _finished = false;
                _busy = false;
                if (_pending != null && (Current == null || Current.Revision != _pending.Revision))
                {
                    Current = _pending;
                    Patrol.Load(true);
                    Patrol.ApplyLiveRoutes();
                    RevivalPlugin.L.LogInfo("LiveRoutes: applied verified snapshot " + Current.Revision);
                }
                if (_failure != _lastFailure)
                {
                    _lastFailure = _failure;
                    if (_failure.Length > 0) RevivalPlugin.L.LogWarning("LiveRoutes: " + _failure
                        + "; keeping the last verified snapshot.");
                }
                _pending = null;
            }
            if (_busy || Time.realtimeSinceStartup < _next || _url == null) return;
            _next = Time.realtimeSinceStartup + 3f;
            _busy = true;
            string url = _url.Value, pin = _pin.Value;
            string revision = Current == null ? "" : Current.Revision;
            ThreadPool.QueueUserWorkItem(delegate(object unused) {
                try { _pending = Download(url, pin, revision); _failure = ""; }
                catch (Exception ex) { _pending = null; _failure = ex.Message; }
                finally { _finished = true; }
            });
        }

        static string Hash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        // Use Windows Schannel, not Unity Mono's legacy SslStream provider.
        // Only public route data is requested. Pin before accepting any response,
        // including 304; redirects, cookies and automatic credentials are disabled.
        internal static Snapshot Download(string address, string pin, string revision)
        {
            Uri uri = new Uri(address);
            if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || !HexHash(pin)
                || (revision.Length != 0 && !HexHash(revision)))
                throw new IOException("Invalid live route endpoint, certificate pin or revision");
            IntPtr session = IntPtr.Zero, connection = IntPtr.Zero, request = IntPtr.Zero;
            try
            {
                session = WinHttpOpen("NextDayRevival-LiveRoutes", 1, null, null, 0);
                NativeCheck(session != IntPtr.Zero, "open");
                NativeCheck(WinHttpSetTimeouts(session, 5000, 5000, 5000, 5000), "timeouts");
                SetOption(session, 84, 0x800); // WINHTTP_OPTION_SECURE_PROTOCOLS: TLS 1.2
                connection = WinHttpConnect(session, uri.Host, (ushort)uri.Port, 0);
                NativeCheck(connection != IntPtr.Zero, "connect");
                request = WinHttpOpenRequest(connection, "GET", uri.PathAndQuery, null,
                    null, IntPtr.Zero, 0x00800000); // WINHTTP_FLAG_SECURE
                NativeCheck(request != IntPtr.Zero, "request");
                SetOption(request, 63, 7); // Disable cookies, redirects and authentication.
                // The exact leaf pin replaces CA/name trust for the editor's private
                // certificate. Date and server-usage checks remain enforced by Windows.
                SetOption(request, 31, 0x100 | 0x1000);
                string headers = "If-None-Match: \"" + revision + "\"\r\n";
                NativeCheck(WinHttpSendRequest(request, headers, (uint)headers.Length,
                    IntPtr.Zero, 0, 0, UIntPtr.Zero), "TLS/send");
                NativeCheck(WinHttpReceiveResponse(request, IntPtr.Zero), "receive");
                CheckCertificate(request, pin);
                uint status, length = 4;
                NativeCheck(WinHttpQueryHeaders(request, 19 | 0x20000000, null,
                    out status, ref length, IntPtr.Zero), "status");
                if (status == 304 && revision.Length != 0) return null;
                if (status != 200) throw new IOException("Route editor HTTP " + status);
                using (MemoryStream buffer = new MemoryStream())
                {
                    byte[] chunk = new byte[8192];
                    Stopwatch deadline = Stopwatch.StartNew();
                    while (true)
                    {
                        uint n;
                        NativeCheck(WinHttpReadData(request, chunk, (uint)chunk.Length, out n), "read");
                        if (buffer.Length + n > 4 * 1024 * 1024 || deadline.ElapsedMilliseconds > 10000)
                            throw new IOException("Snapshot exceeds transfer limit");
                        if (n == 0) break;
                        buffer.Write(chunk, 0, (int)n);
                    }
                    return Parse(Encoding.ASCII.GetString(buffer.ToArray()));
                }
            }
            finally
            {
                if (request != IntPtr.Zero) WinHttpCloseHandle(request);
                if (connection != IntPtr.Zero) WinHttpCloseHandle(connection);
                if (session != IntPtr.Zero) WinHttpCloseHandle(session);
            }
        }

        static bool HexHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            return true;
        }

        static void NativeCheck(bool success, string stage)
        {
            if (!success)
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException("Route editor " + stage + " failed (Windows " + error
                    + "): " + new Win32Exception(error).Message);
            }
        }

        static void SetOption(IntPtr handle, uint option, uint value)
        {
            NativeCheck(WinHttpSetOption(handle, option, ref value, 4), "option " + option);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CertificateContext
        {
            internal uint Encoding;
            internal IntPtr Bytes;
            internal uint Length;
            internal IntPtr Info, Store;
        }

        static void CheckCertificate(IntPtr request, string pin)
        {
            IntPtr context = IntPtr.Zero;
            uint size = (uint)IntPtr.Size;
            NativeCheck(WinHttpQueryOption(request, 78, out context, ref size), "certificate");
            if (context == IntPtr.Zero) throw new IOException("Route editor certificate missing");
            try
            {
                CertificateContext cert = (CertificateContext)Marshal.PtrToStructure(context, typeof(CertificateContext));
                if (cert.Length == 0 || cert.Length > 1024 * 1024)
                    throw new IOException("Invalid route editor certificate");
                byte[] raw = new byte[(int)cert.Length];
                Marshal.Copy(cert.Bytes, raw, 0, raw.Length);
                if (!string.Equals(Hash(raw), pin, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Route editor certificate pin mismatch");
                X509Certificate2 leaf = new X509Certificate2(raw);
                if (DateTime.Now < leaf.NotBefore || DateTime.Now > leaf.NotAfter)
                    throw new IOException("Route editor certificate expired or not yet valid");
            }
            finally { CertFreeCertificateContext(context); }
        }

        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        static extern IntPtr WinHttpOpen(string agent, uint access, string proxy, string bypass, uint flags);
        [DllImport("winhttp.dll", SetLastError = true)]
        static extern bool WinHttpSetTimeouts(IntPtr session, int resolve, int connect, int send, int receive);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        static extern IntPtr WinHttpConnect(IntPtr session, string host, ushort port, uint reserved);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        static extern IntPtr WinHttpOpenRequest(IntPtr connection, string verb, string path,
            string version, string referer, IntPtr accept, uint flags);
        [DllImport("winhttp.dll", SetLastError = true)]
        static extern bool WinHttpSetOption(IntPtr handle, uint option, ref uint value, uint size);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        static extern bool WinHttpSendRequest(IntPtr request, string headers, uint length,
            IntPtr data, uint dataLength, uint totalLength, UIntPtr context);
        [DllImport("winhttp.dll", SetLastError = true)]
        static extern bool WinHttpReceiveResponse(IntPtr request, IntPtr reserved);
        [DllImport("winhttp.dll", SetLastError = true)]
        static extern bool WinHttpQueryOption(IntPtr request, uint option, out IntPtr value, ref uint size);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        static extern bool WinHttpQueryHeaders(IntPtr request, uint info, string name,
            out uint value, ref uint size, IntPtr index);
        [DllImport("winhttp.dll", SetLastError = true)]
        static extern bool WinHttpReadData(IntPtr request, [Out] byte[] buffer, uint size, out uint read);
        [DllImport("winhttp.dll")]
        static extern bool WinHttpCloseHandle(IntPtr handle);
        [DllImport("crypt32.dll")]
        static extern bool CertFreeCertificateContext(IntPtr context);

        internal static Snapshot Parse(string body)
        {
            string[] envelope = body.Split('\n');
            if (envelope.Length != 5 || envelope[0] != "NDR-LIVE-1" || envelope[4] != "")
                throw new IOException("Invalid snapshot envelope");
            byte[] routes = Convert.FromBase64String(envelope[2]);
            byte[] crew = Convert.FromBase64String(envelope[3]);
            byte[] joint = new byte[routes.Length + crew.Length + 1];
            Array.Copy(routes, joint, routes.Length);
            Array.Copy(crew, 0, joint, routes.Length + 1, crew.Length);
            if (Hash(joint) != envelope[1]) throw new IOException("Snapshot hash mismatch");
            foreach (byte b in joint)
                if (b > 127) throw new IOException("Snapshot is not ASCII");
            Snapshot value = new Snapshot();
            value.Revision = envelope[1];
            value.Routes = Encoding.ASCII.GetString(routes).Split('\n');
            value.Crew = Encoding.ASCII.GetString(crew).Split('\n');
            Validate(value);
            return value;
        }

        static void Validate(Snapshot value)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>();
            if (value.Routes.Length > 30000 || value.Crew.Length > 10000)
                throw new IOException("Too many runtime rows");
            foreach (string line in value.Routes)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] c = line.Split('\t');
                if (c.Length != 7 || c[0].Length == 0 || c[0].Length > 100)
                    throw new IOException("Invalid route row");
                int count;
                counts.TryGetValue(c[0], out count);
                int index;
                if (!int.TryParse(c[1], out index) || index != count) throw new IOException("Route sequence gap");
                counts[c[0]] = count + 1;
                for (int i = 2; i <= 5; i++)
                {
                    float f;
                    if (!float.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out f)
                        || float.IsNaN(f) || float.IsInfinity(f) || Math.Abs(f) > 3000)
                        throw new IOException("Invalid route coordinate or speed");
                }
            }
            if (counts.Count > 256) throw new IOException("Too many routes");
            foreach (int count in counts.Values)
                if (count < 2) throw new IOException("Route has fewer than two points");
            foreach (string line in value.Crew)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] c = line.Split('\t');
                int index;
                // Legacy crewless exports have one extra empty trailing cell.
                if ((c.Length != 11 && !(c.Length == 12 && c[11] == ""))
                    || !counts.ContainsKey(c[0]) || !int.TryParse(c[1], out index)
                    || index < 0 || index > 63 || (c[2] != "tank" && c[2] != "btr" && c[2] != "ural" && c[2] != "truck"))
                    throw new IOException("Invalid composition row");
                for (int i = 4; i <= 10; i++)
                    foreach (string part in c[i].Split('|'))
                    {
                        int number;
                        if (part.Length != 0 && (!int.TryParse(part, out number) || number < 0 || number > 100000))
                            throw new IOException("Invalid crew item");
                    }
            }
        }
    }
}
