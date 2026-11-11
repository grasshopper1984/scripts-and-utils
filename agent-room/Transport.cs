using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace AgentRoom
{
    internal sealed class RoomServer : IDisposable
    {
        private readonly RoomService service;
        private readonly object gate = new object();
        private readonly HashSet<NamedPipeServerStream> connections = new HashSet<NamedPipeServerStream>();
        private readonly HashSet<Task> handlers = new HashSet<Task>();
        private readonly SemaphoreSlim slots = new SemaphoreSlim(16);
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private Task accept;
        // Consecutive failures to create a pipe instance. A long run means the room cannot be served.
        private volatile int failures;
        internal RoomServer(RoomService service) { this.service = service; }
        internal static PipeSecurity Security()
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            return security;
        }
        internal void Start() { accept = Accept(); if (accept.IsFaulted) accept.GetAwaiter().GetResult(); }
        private async Task Accept()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await slots.WaitAsync(stop.Token).ConfigureAwait(false);
                    NamedPipeServerStream pipe = null;
                    bool lost = false;
                    try
                    {
                        pipe = new NamedPipeServerStream(service.PipeName, PipeDirection.InOut, 17,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, Security());
                        lock (gate) connections.Add(pipe);
                        await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        bool created = pipe != null;
                        if (created) { lock (gate) connections.Remove(pipe); pipe.Dispose(); }
                        slots.Release();
                        if (stop.IsCancellationRequested) throw;
                        if (!(e is IOException)) { service.Log("The listener stopped: agents cannot reach the hub until it is restarted", e); throw; }
                        if (!created && failures == 0) service.Log("The room's pipe could not be created; retrying", e);
                        // A client that connects and closes before the hub starts waiting on that instance fails the
                        // wait with "the pipe is being closed". That is one departed client, never the listener.
                        // Only a pipe that cannot be created counts against the listener's health.
                        lost = true;
                        failures = created ? 0 : failures + 1;
                    }
                    if (lost)
                    {
                        // Keep trying to create the pipe, but do not spin while it fails.
                        if (failures != 0) await Task.Delay(failures > 20 ? 250 : 20, stop.Token).ConfigureAwait(false);
                        continue;
                    }
                    failures = 0;
                    // Serve catches its own failures and always releases the bounded connection slot.
                    Task handler = Serve(pipe);
                    lock (gate) handlers.Add(handler);
                    Observe(handler);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (IOException) { if (!stop.IsCancellationRequested) throw; }
        }
        private async void Observe(Task handler)
        {
            try { await handler.ConfigureAwait(false); }
            catch (Exception) { }
            finally { lock (gate) handlers.Remove(handler); }
        }
        private async Task Serve(NamedPipeServerStream pipe)
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
            using (timeout.Token.Register(() => { try { pipe.Dispose(); } catch (ObjectDisposedException) { } }))
            {
                try
                {
                    string request = await Wire.ReadAsync(pipe).ConfigureAwait(false);
                    Dictionary<string, object> response;
                    try { response = service.Handle(Json.Read(request)); }
                    catch (RoomError e) { response = Json.Error(e.Code, e.Message); }
                    await Wire.WriteAsync(pipe, Wire.ResponseText(response)).ConfigureAwait(false);
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (OperationCanceledException) { }
                catch (Exception e) { service.Log("A request handler failed", e); /* A malformed client must not terminate the hub. */ }
                finally { lock (gate) connections.Remove(pipe); pipe.Dispose(); slots.Release(); }
            }
        }
        internal bool Healthy { get { return accept != null && !accept.IsFaulted && failures <= 20; } }
        public void Dispose()
        {
            // .NET can record a connection's cancellation callback after that connection has already completed. Such a
            // stale callback throws "Safe handle has been closed" here, long after its pipe is gone. Every callback
            // still runs, so shutdown continues; the error says nothing about the listener.
            try { stop.Cancel(); } catch (AggregateException) { }
            lock (gate) foreach (var pipe in connections.ToArray()) pipe.Dispose();
            if (accept != null) { try { accept.GetAwaiter().GetResult(); } catch (IOException) { } }
            Task[] remaining;
            lock (gate) remaining = handlers.ToArray();
            Task.WhenAll(remaining).GetAwaiter().GetResult();
            // Do not dispose synchronization primitives while disconnected handlers unwind.
        }
    }

    internal static class Wire
    {
        internal const int MaxRequestBytes = 65536;
        internal static async Task<string> ReadAsync(Stream stream, int maximum = MaxRequestBytes)
        {
            byte[] header = new byte[4];
            await ReadExactly(stream, header).ConfigureAwait(false);
            int length = BitConverter.ToInt32(header, 0);
            if (length < 2 || length > maximum) throw new IOException("Invalid frame length.");
            byte[] body = new byte[length];
            await ReadExactly(stream, body).ConfigureAwait(false);
            try { return new UTF8Encoding(false, true).GetString(body); }
            catch (DecoderFallbackException) { throw new IOException("Invalid UTF8 frame."); }
        }
        private static async Task ReadExactly(Stream stream, byte[] bytes)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes, offset, bytes.Length - offset).ConfigureAwait(false);
                if (read == 0) throw new IOException("Incomplete room message.");
                offset += read;
            }
        }
        internal static async Task WriteAsync(Stream stream, string text)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            if (body.Length > 2097152) throw new IOException("Response exceeds the frame limit; request a smaller page.");
            byte[] header = BitConverter.GetBytes(body.Length);
            await stream.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        // An oversized response gets a clear error instead of a dropped connection the client would retry forever.
        internal static string ResponseText(Dictionary<string, object> response)
        {
            string text = null;
            try { text = Json.Write(response); }
            catch (InvalidOperationException) { }
            if (text != null && Encoding.UTF8.GetByteCount(text) <= 2097152) return text;
            return Json.Write(Json.Error("response_too_large", "That response exceeds the transport limit. Request a smaller page or a single record."));
        }
        internal static Dictionary<string, object> Call(Dictionary<string, object> profile, string session, string cmd,
            Dictionary<string, object> args, string requestId)
        {
            string pipeName = Json.Required(profile, "pipe", 100);
            if (!pipeName.StartsWith("ScriptsAndUtils.AgentRoom.", StringComparison.Ordinal) || pipeName.Contains("\\") || pipeName.Contains("/"))
                throw new RoomError("invalid_profile", "Invalid local pipe name.");
            string frame = Json.Write(Json.Obj("version", RoomService.ProtocolVersion, "token", Json.Required(profile, "token", 128),
                "session", session, "cmd", cmd, "args", args, "id", requestId));
            // The hub drops an oversized frame without answering, which would read as a lost connection worth retrying.
            // JSON encoding writes <, >, & and ' as six bytes each, so text within every field limit can still pass this.
            if (Encoding.UTF8.GetByteCount(frame) > MaxRequestBytes)
                throw new RoomError("request_too_large", "The request exceeds 64 KiB once encoded. Shorten the text, or put long material in an artifact and cite it. Retrying unchanged will not help.");
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            using (timeout.Token.Register(() => { try { pipe.Dispose(); } catch (ObjectDisposedException) { } }))
            {
                try
                {
                    pipe.Connect(2000);
                    VerifyServerIdentity(pipe);
                    WriteAsync(pipe, frame).GetAwaiter().GetResult();
                    return Json.Read(ReadAsync(pipe, 2097152).GetAwaiter().GetResult(), 2097152);
                }
                catch (TimeoutException) { throw new RoomError("hub_unavailable", "Start the Agent Room application first."); }
                catch (ObjectDisposedException) { throw new RoomError("timeout", "Room request timed out; retry a mutation with the same request ID."); }
                catch (IOException) { throw new RoomError("connection_lost", "Room connection ended; retry a mutation with the same request ID."); }
            }
        }
        private static void VerifyServerIdentity(NamedPipeClientStream pipe)
        {
            uint processId;
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out processId))
                throw new RoomError("untrusted_hub", "Cannot verify the local hub's Windows identity.");
            IntPtr process = OpenProcess(0x1000, false, processId), token = IntPtr.Zero;
            try
            {
                if (process == IntPtr.Zero || !OpenProcessToken(process, 0x0008, out token))
                    throw new RoomError("untrusted_hub", "Cannot verify the local hub's Windows identity.");
                using (var identity = new WindowsIdentity(token))
                using (var current = WindowsIdentity.GetCurrent())
                    if (identity.User != current.User)
                        throw new RoomError("untrusted_hub", "The local hub must run under your Windows account.");
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
                if (process != IntPtr.Zero) CloseHandle(process);
            }
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
