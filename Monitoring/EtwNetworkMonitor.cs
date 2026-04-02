using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace TaskManagerUI.Monitoring
{
    public static class EtwNetworkMonitor
    {
        private static TraceEventSession? _session;
        private static readonly ConcurrentDictionary<int, NetworkStats> _processNetworkStats = new();

        public class NetworkStats
        {
            public long ReceivedBytes;
            public long SentBytes;
        }

        public static void Start()
        {
            Task.Run(() =>
            {
                try
                {
                    _session = new TraceEventSession("TaskManagerUI_NetworkMonitor");
                    _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

                    _session.Source.Kernel.TcpIpRecv += data => UpdateStats(data.ProcessID, data.size, 0);
                    _session.Source.Kernel.TcpIpRecvIPV6 += data => UpdateStats(data.ProcessID, data.size, 0);
                    _session.Source.Kernel.TcpIpSend += data => UpdateStats(data.ProcessID, 0, data.size);
                    _session.Source.Kernel.TcpIpSendIPV6 += data => UpdateStats(data.ProcessID, 0, data.size);

                    _session.Source.Kernel.UdpIpRecv += data => UpdateStats(data.ProcessID, data.size, 0);
                    _session.Source.Kernel.UdpIpRecvIPV6 += data => UpdateStats(data.ProcessID, data.size, 0);
                    _session.Source.Kernel.UdpIpSend += data => UpdateStats(data.ProcessID, 0, data.size);
                    _session.Source.Kernel.UdpIpSendIPV6 += data => UpdateStats(data.ProcessID, 0, data.size);

                    _session.Source.Process();
                }
                catch
                {
                    // Tracing might require administrator privileges
                }
            });
        }

        public static void Stop()
        {
            _session?.Dispose();
            _session = null;
        }

        private static void UpdateStats(int processId, int received, int sent)
        {
            var stats = _processNetworkStats.GetOrAdd(processId, _ => new NetworkStats());
            if (received > 0) Interlocked.Add(ref stats.ReceivedBytes, received);
            if (sent > 0) Interlocked.Add(ref stats.SentBytes, sent);
        }

        public static (long Received, long Sent) GetProcessStats(int processId)
        {
            if (_processNetworkStats.TryGetValue(processId, out var stats))
            {
                return (Interlocked.Read(ref stats.ReceivedBytes), Interlocked.Read(ref stats.SentBytes));
            }
            return (0, 0);
        }
    }
}