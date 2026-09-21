using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Threading;

namespace BZMultiplayer.Net
{
    /// <summary>
    /// Minimal Discord Rich Presence client over the local Discord IPC pipe (the same channel the official SDK uses).
    /// Publishes "Hosting / In a session" presence with a party and a join secret, so friends get a Join button on
    /// the Discord profile card, and raises <see cref="OnJoinSecret"/> when a friend uses it.
    /// No native DLL, no internet; if Discord is not running this simply stays disconnected and retries.
    /// </summary>
    public class DiscordRpc : IDisposable
    {
        private const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;

        private readonly string appId;
        private NamedPipeClientStream pipe;
        private Thread worker;
        private readonly Queue<KeyValuePair<int, string>> outbox = new Queue<KeyValuePair<int, string>>();
        private readonly object outboxLock = new object();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PeekNamedPipe(IntPtr handle, IntPtr buffer, uint bufferSize, IntPtr bytesRead, out uint bytesAvail, IntPtr bytesLeft);
        private volatile bool running;
        private volatile bool ready;
        private float nextConnectAt;
        private int nonce;
        private string lastActivity;
        private readonly Queue<string> inbox = new Queue<string>();
        private readonly object inboxLock = new object();
        private readonly HashSet<string> autoAccepted = new HashSet<string>();

        public bool Connected { get { return ready; } }
        public string Status { get; private set; }

        /// <summary>Raised on the main thread with the join secret a friend clicked (our Steam lobby id).</summary>
        public event Action<string> OnJoinSecret;

        public DiscordRpc(string applicationId)
        {
            appId = applicationId;
            Status = "off";
        }

        // ---------------------------------------------------------------- connection

        public void Update(float unscaledTime)
        {
            if (string.IsNullOrEmpty(appId)) return;
            if (pipe == null && unscaledTime >= nextConnectAt)
            {
                nextConnectAt = unscaledTime + 15f;
                TryConnect();
            }
            // Deliver events on the main thread.
            while (true)
            {
                string msg;
                lock (inboxLock) { if (inbox.Count == 0) break; msg = inbox.Dequeue(); }
                Handle(msg);
            }
        }

        private void TryConnect()
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    var client = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.None);
                    client.Connect(200);
                    pipe = client;
                    break;
                }
                catch { }
            }
            if (pipe == null) { Status = "Discord not running"; return; }

            try
            {
                running = true;
                lock (outboxLock) outbox.Clear();
                Send(OpHandshake, "{\"v\":1,\"client_id\":\"" + appId + "\"}");
                worker = new Thread(WorkerLoop) { IsBackground = true, Name = "BZMP-DiscordRPC" };
                worker.Start();
                Status = "connecting";
                Plugin.Log.LogInfo("Discord: connected to IPC pipe, handshaking.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Discord: handshake failed: " + e.Message);
                Close();
            }
        }

        private void Close()
        {
            running = false;
            ready = false;
            lock (outboxLock) outbox.Clear();
            lastActivity = null;
            try { if (pipe != null) pipe.Dispose(); } catch { }
            pipe = null;
            if (Status != "off") Status = "disconnected";
        }

        public void Dispose()
        {
            try { if (pipe != null && ready) { Send(OpFrame, Cmd("SET_ACTIVITY", "{\"pid\":" + Process.GetCurrentProcess().Id + "}")); Thread.Sleep(100); } } catch { }
            Close();
        }

        // ---------------------------------------------------------------- wire

        /// <summary>Queue a frame; the worker thread writes it. Never touches the pipe from the game thread.</summary>
        private void Send(int op, string json)
        {
            lock (outboxLock) outbox.Enqueue(new KeyValuePair<int, string>(op, json));
        }

        private void WriteFrame(int op, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var buf = new byte[8 + body.Length];
            Array.Copy(BitConverter.GetBytes(op), 0, buf, 0, 4);
            Array.Copy(BitConverter.GetBytes(body.Length), 0, buf, 4, 4);
            Array.Copy(body, 0, buf, 8, body.Length);
            pipe.Write(buf, 0, buf.Length);
            pipe.Flush();
        }

        private uint Available()
        {
            uint avail;
            try
            {
                if (!PeekNamedPipe(pipe.SafePipeHandle.DangerousGetHandle(), IntPtr.Zero, 0, IntPtr.Zero, out avail, IntPtr.Zero)) return uint.MaxValue; // broken pipe
                return avail;
            }
            catch { return uint.MaxValue; }
        }

        /// <summary>
        /// Single I/O thread: writes queued frames, then reads whatever Discord has sent. A synchronous pipe handle
        /// serialises reads and writes, so nothing here ever blocks in Read while a write is pending.
        /// </summary>
        private void WorkerLoop()
        {
            var header = new byte[8];
            try
            {
                while (running && pipe != null)
                {
                    // Outgoing
                    while (true)
                    {
                        KeyValuePair<int, string> msg;
                        lock (outboxLock) { if (outbox.Count == 0) break; msg = outbox.Dequeue(); }
                        WriteFrame(msg.Key, msg.Value);
                    }
                    // Incoming
                    uint avail = Available();
                    if (avail == uint.MaxValue) break;
                    if (avail >= 8)
                    {
                        if (!ReadExact(header, 8)) break;
                        int op = BitConverter.ToInt32(header, 0);
                        int len = BitConverter.ToInt32(header, 4);
                        if (len < 0 || len > 1 << 20) break;
                        var body = new byte[len];
                        if (!ReadExact(body, len)) break;
                        string json = Encoding.UTF8.GetString(body);
                        if (op == OpPing) { WriteFrame(OpPong, json); continue; }
                        if (op == OpClose) break;
                        if (op == OpFrame) lock (inboxLock) inbox.Enqueue(json);
                        continue;
                    }
                    Thread.Sleep(25);
                }
            }
            catch { }
            lock (inboxLock) inbox.Enqueue("\u0000closed");
        }

        private bool ReadExact(byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = pipe.Read(buf, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        // ---------------------------------------------------------------- messages

        private void Handle(string json)
        {
            if (json == "\u0000closed") { Plugin.Log.LogInfo("Discord: pipe closed."); Close(); return; }
            string evt = Field(json, "evt");
            string cmd = Field(json, "cmd");
            // Only DISPATCH frames are events; everything else is an acknowledgement of one of our commands.
            if (cmd != "DISPATCH" && evt != "ERROR") return;
            if (evt == "READY")
            {
                ready = true;
                Status = "connected";
                Plugin.Log.LogInfo("Discord: ready (" + Field(json, "username") + ").");
                Send(OpFrame, "{\"cmd\":\"SUBSCRIBE\",\"evt\":\"ACTIVITY_JOIN\",\"nonce\":\"" + (++nonce) + "\"}");
                Send(OpFrame, "{\"cmd\":\"SUBSCRIBE\",\"evt\":\"ACTIVITY_JOIN_REQUEST\",\"nonce\":\"" + (++nonce) + "\"}");
                lastActivity = null; // re-publish presence on the new connection
            }
            else if (evt == "ERROR")
            {
                Plugin.Log.LogWarning("Discord: " + Field(json, "message") + " (code " + Field(json, "code") + ")");
                if (!ready) Close();
            }
            else if (evt == "ACTIVITY_JOIN")
            {
                string secret = Field(json, "secret");
                Plugin.Log.LogInfo("Discord: join accepted, secret " + secret);
                if (!string.IsNullOrEmpty(secret) && OnJoinSecret != null) OnJoinSecret(secret);
            }
            else if (evt == "ACTIVITY_JOIN_REQUEST")
            {
                // "Ask to Join": accept automatically. The Steam lobby is friends-only anyway.
                string userId = Field(json, "id");
                string name = Field(json, "username");
                if (!string.IsNullOrEmpty(userId) && autoAccepted.Add(userId + ":" + lastActivity))
                {
                    Plugin.Log.LogInfo("Discord: " + name + " asked to join; accepting.");
                    Send(OpFrame, Cmd("SEND_ACTIVITY_JOIN_INVITE", "{\"user_id\":\"" + userId + "\"}"));
                }
            }
        }

        private string Cmd(string cmd, string args)
        {
            return "{\"cmd\":\"" + cmd + "\",\"args\":" + args + ",\"nonce\":\"" + (++nonce) + "\"}";
        }

        private static string Field(string json, string name)
        {
            var m = Regex.Match(json, "\"" + name + "\"\\s*:\\s*(?:\"((?:[^\"\\\\]|\\\\.)*)\"|(-?\\d+))");
            if (!m.Success) return null;
            return m.Groups[1].Success ? Regex.Unescape(m.Groups[1].Value) : m.Groups[2].Value;
        }

        private static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");
        }

        // ---------------------------------------------------------------- presence

        /// <summary>Publish presence. Pass a null lobbyId to clear the party/secret (not in a session).</summary>
        public void SetActivity(string details, string state, string lobbyId, int partySize, int partyMax, long startUnix)
        {
            if (!ready) return;
            var sb = new StringBuilder();
            sb.Append("{\"pid\":").Append(Process.GetCurrentProcess().Id).Append(",\"activity\":{");
            sb.Append("\"details\":\"").Append(Esc(details)).Append("\",");
            sb.Append("\"state\":\"").Append(Esc(state)).Append("\",");
            sb.Append("\"timestamps\":{\"start\":").Append(startUnix).Append("},");
            sb.Append("\"assets\":{\"large_image\":\"bz\",\"large_text\":\"BZMultiplayer\"}");
            if (!string.IsNullOrEmpty(lobbyId))
            {
                sb.Append(",\"party\":{\"id\":\"bz-").Append(lobbyId).Append("\",\"size\":[").Append(Math.Max(1, partySize)).Append(',').Append(Math.Max(partySize, partyMax)).Append("]}");
                sb.Append(",\"secrets\":{\"join\":\"").Append(lobbyId).Append("\"}");
            }
            sb.Append(",\"instance\":true}}");
            string args = sb.ToString();
            if (args == lastActivity) return;
            lastActivity = args;
            Send(OpFrame, Cmd("SET_ACTIVITY", args));
        }
    }
}
