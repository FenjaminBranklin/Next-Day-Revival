using System;
using System.IO;
using System.Text;
using System.Net.Sockets;
using System.Net.Security;
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

        internal static Snapshot Download(string address, string pin, string revision)
        {
            Uri uri = new Uri(address);
            if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || pin.Length != 64)
                throw new Exception("Invalid live route endpoint or certificate pin");
            using (TcpClient client = new TcpClient())
            {
                IAsyncResult connect = client.BeginConnect(uri.Host, uri.Port, null, null);
                try
                {
                    if (!connect.AsyncWaitHandle.WaitOne(5000, false)) throw new IOException("Connection timeout");
                    client.EndConnect(connect);
                }
                finally { connect.AsyncWaitHandle.Close(); }
                client.ReceiveTimeout = client.SendTimeout = 5000;
                using (SslStream tls = new SslStream(client.GetStream(), false,
                    delegate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors) {
                        if (cert == null) return false;
                        X509Certificate2 leaf = new X509Certificate2(cert);
                        return DateTime.Now >= leaf.NotBefore && DateTime.Now <= leaf.NotAfter
                            && string.Equals(Hash(cert.GetRawCertData()), pin, StringComparison.OrdinalIgnoreCase);
                    }))
                {
                    tls.ReadTimeout = tls.WriteTimeout = 5000;
                    tls.AuthenticateAsClient(uri.Host, null,
                        (System.Security.Authentication.SslProtocols)3072, false);
                    string request = "GET " + uri.PathAndQuery + " HTTP/1.0\r\nHost: " + uri.Authority
                        + "\r\nConnection: close\r\nIf-None-Match: \"" + revision + "\"\r\n\r\n";
                    byte[] bytes = Encoding.ASCII.GetBytes(request);
                    tls.Write(bytes, 0, bytes.Length);
                    using (MemoryStream buffer = new MemoryStream())
                    {
                        byte[] chunk = new byte[8192];
                        int n;
                        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                        while ((n = tls.Read(chunk, 0, chunk.Length)) > 0)
                        {
                            if (buffer.Length + n > 4 * 1024 * 1024 || DateTime.UtcNow > deadline)
                                throw new IOException("Snapshot exceeds transfer limit");
                            buffer.Write(chunk, 0, n);
                        }
                        string response = Encoding.ASCII.GetString(buffer.ToArray());
                        int split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (split < 0) throw new IOException("Incomplete HTTP response");
                        string status = response.Substring(0, response.IndexOf("\r\n", StringComparison.Ordinal));
                        if (status.IndexOf(" 304 ", StringComparison.Ordinal) >= 0) return null;
                        if (status.IndexOf(" 200 ", StringComparison.Ordinal) < 0) throw new IOException(status);
                        return Parse(response.Substring(split + 4));
                    }
                }
            }
        }

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
