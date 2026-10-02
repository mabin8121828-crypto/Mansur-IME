// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Mansur.Next.Desktop
{
    // Resolve the chat endpoint once, then pin both check requests / this worker to that route.
    internal sealed class ApiProxySelection
    {
        internal static readonly Uri Target = new Uri("https://openrouter.ai/api/v1/chat/completions");
        private static readonly object gate = new object();
        private static Task<ApiProxySelection> pending;
        private static readonly System.Collections.Generic.Dictionary<string, Task<ApiProxySelection>> targets = new System.Collections.Generic.Dictionary<string, Task<ApiProxySelection>>();
        internal readonly string Host;
        internal readonly int Port;
        private ApiProxySelection(string host, int port) { Host = host; Port = port; }
        internal bool Direct { get { return Host.Length == 0; } }
        internal static ApiProxySelection Resolve(CancellationToken cancel)
        { return Resolve(Target, cancel); }
        internal static ApiProxySelection Resolve(Uri target, CancellationToken cancel)
        {
            Task<ApiProxySelection> work;
            lock (gate)
            {
                // At most one potentially slow Windows PAC lookup is outstanding in this process.
                if (!targets.TryGetValue(target.AbsoluteUri, out pending) || pending.IsCompleted)
                {
                    if (targets.Count >= 32) { var remove = new System.Collections.Generic.List<string>(); foreach (var entry in targets) if (entry.Value.IsCompleted) remove.Add(entry.Key); foreach (string key in remove) targets.Remove(key); }
                    if (targets.Count >= 32) throw new ModelConfigurationException("api_proxy_resolution_failed");
                    pending = Task.Run(() => From(WebRequest.GetSystemWebProxy(), target)); targets[target.AbsoluteUri] = pending;
                }
                work = pending;
            }
            var delay = Task.Delay(12000, cancel);
            if (Task.WhenAny(work, delay).GetAwaiter().GetResult() != work)
            { cancel.ThrowIfCancellationRequested(); throw new ModelConfigurationException("api_proxy_resolution_failed"); }
            cancel.ThrowIfCancellationRequested();
            try { return work.GetAwaiter().GetResult(); }
            catch (ModelConfigurationException) { throw; }
            catch (Exception error) when (error is WebException || error is InvalidOperationException || error is ArgumentException || error is System.Security.SecurityException)
            { throw new ModelConfigurationException("api_proxy_resolution_failed"); }
        }
        internal static ApiProxySelection From(IWebProxy proxy)
        { return From(proxy, Target); }
        internal static ApiProxySelection From(IWebProxy proxy, Uri target)
        {
            if (proxy == null) return new ApiProxySelection("", 0);
            Uri selected = proxy.GetProxy(target);
            if (selected == null || selected == target) return new ApiProxySelection("", 0);
            if (!selected.IsAbsoluteUri || selected.Scheme != Uri.UriSchemeHttp || selected.Port < 1 || selected.Port > 65535 ||
                selected.AbsolutePath != "/" || selected.Query.Length != 0 || selected.Fragment.Length != 0)
                throw new ModelConfigurationException("api_proxy_unsupported");
            if (selected.UserInfo.Length != 0 || proxy.Credentials != null) throw new ModelConfigurationException("api_proxy_auth_required");
            string host = selected.IdnHost.Trim('[', ']');
            if (host.Length > 253 || host.Contains("%") || Uri.CheckHostName(host) == UriHostNameType.Unknown)
                throw new ModelConfigurationException("api_proxy_unsupported");
            return new ApiProxySelection(host, selected.Port);
        }
        internal IWebProxy ToWebProxy()
        { return Direct ? null : new WebProxy(new UriBuilder(Uri.UriSchemeHttp, Host, Port).Uri) { Credentials = null }; }
    }
}
