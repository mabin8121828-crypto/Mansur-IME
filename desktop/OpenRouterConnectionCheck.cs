// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mansur.Next.Desktop
{
    internal interface IOpenRouterProbe
    { Task<Dictionary<string, object>> GetAsync(string path, string key, int maximum, CancellationToken cancel); }
    internal sealed class OpenRouterConnectionCheck : IOpenRouterProbe
    {
        private readonly ApiProxySelection selection;
        internal OpenRouterConnectionCheck() { }
        internal OpenRouterConnectionCheck(ApiProxySelection proxy) { selection = proxy; }
        public Task<Dictionary<string, object>> GetAsync(string path, string key, int maximum, CancellationToken cancel)
        {
            if (path != "/api/v1/key" && path != "/api/v1/models") throw new ArgumentException("Unknown API check.");
            return Task.Run(() => {
                cancel.ThrowIfCancellationRequested();
                var request = (HttpWebRequest)WebRequest.Create("https://openrouter.ai" + path);
                request.Proxy = (selection ?? ApiProxySelection.Resolve(cancel)).ToWebProxy();
                request.UseDefaultCredentials = false;
                request.Credentials = null;
                request.Method = "GET"; request.AllowAutoRedirect = false; request.Timeout = 12000; request.ReadWriteTimeout = 12000;
                request.Accept = "application/json"; request.UserAgent = "MansurNext/0.1";
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                if (path == "/api/v1/key") request.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
                using (cancel.Register(request.Abort))
                {
                    try
                    {
                        using (var response = (HttpWebResponse)request.GetResponse())
                        {
                            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400) throw new ModelConfigurationException("api_redirect_rejected");
                            if (response.StatusCode != HttpStatusCode.OK) throw new ModelConfigurationException("api_connection_failed");
                            if (response.ContentLength > maximum) throw new ModelConfigurationException("api_response_too_large");
                            using (var input = response.GetResponseStream())
                            using (var memory = new MemoryStream())
                            {
                                byte[] buffer = new byte[8192]; int read;
                                while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                                { cancel.ThrowIfCancellationRequested(); if (memory.Length + read > maximum) throw new ModelConfigurationException("api_response_too_large"); memory.Write(buffer, 0, read); }
                                cancel.ThrowIfCancellationRequested();
                                return Json.Parse(new UTF8Encoding(false, true).GetString(memory.ToArray()), maximum);
                            }
                        }
                    }
                    catch (WebException error)
                    {
                        cancel.ThrowIfCancellationRequested();
                        var response = error.Response as HttpWebResponse;
                        int status = response == null ? 0 : (int)response.StatusCode;
                        if (response != null) response.Dispose();
                        throw new ModelConfigurationException(status == 407 ? "api_proxy_auth_required" : status == 401 ? "api_unauthorized" : status == 403 ? "api_forbidden" : status == 402 ? "api_credit_required" : status == 429 ? "api_rate_limited" : error.Status == WebExceptionStatus.Timeout ? "api_timeout" : "api_connection_failed");
                    }
                }
            }, cancel);
        }
    }
}
