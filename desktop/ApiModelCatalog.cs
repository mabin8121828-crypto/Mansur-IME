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
    internal interface IApiModelCatalog
    { Task<string[]> FetchAsync(ApiServiceProfile profile, string key, CancellationToken token); }
    internal sealed class ApiModelCatalog : IApiModelCatalog
    {
        public async Task<string[]> FetchAsync(ApiServiceProfile profile, string key, CancellationToken token)
        {
            ApiServices.Validate(profile); if (!ApiCredentialStore.Valid(key)) throw new ModelConfigurationException("api_key_invalid");
            if (!ServicePresets.HasCatalog(profile.Id))
            { token.ThrowIfCancellationRequested(); ApiServices.Validate(profile, true); return new string[0]; }
            var target = new Uri(ApiServices.Endpoint(profile.BaseUrl).AbsoluteUri.TrimEnd('/') + "/models");
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(15000);
                var proxy = await Task.Run(() => ApiProxySelection.Resolve(target, timeout.Token), timeout.Token).ConfigureAwait(false);
                var request = (HttpWebRequest)WebRequest.Create(target); request.Method = "GET"; request.AllowAutoRedirect = false;
                request.Timeout = request.ReadWriteTimeout = 15000; request.Proxy = proxy.ToWebProxy(); request.Credentials = null;
                request.Headers[HttpRequestHeader.Authorization] = "Bearer " + key; request.Accept = "application/json";
                using (timeout.Token.Register(request.Abort))
                {
                    try
                    {
                        using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                        using (var input = response.GetResponseStream())
                        using (var bytes = new MemoryStream())
                        {
                            if (response.StatusCode != HttpStatusCode.OK) throw new ModelConfigurationException("api_http_error");
                            byte[] buffer = new byte[8192]; int read;
                            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) != 0)
                            { if (bytes.Length + read > 4 * 1024 * 1024) throw new ModelConfigurationException("api_response_too_large"); bytes.Write(buffer, 0, read); }
                            var json = Json.Parse(new UTF8Encoding(false, true).GetString(bytes.ToArray()), 4 * 1024 * 1024); object raw;
                            var data = json.TryGetValue("data", out raw) ? raw as object[] : null; if (data == null) throw new ModelConfigurationException("api_response_invalid");
                            var ids = new SortedSet<string>(StringComparer.Ordinal);
                            foreach (object row in data)
                            {
                                var value = row as Dictionary<string, object>; object id;
                                if (value != null && value.TryGetValue("id", out id) && id is string && ApiServices.ValidModel((string)id)) ids.Add((string)id);
                                if (ids.Count > 2000) throw new ModelConfigurationException("api_response_too_large");
                            }
                            return new List<string>(ids).ToArray();
                        }
                    }
                    catch (WebException error)
                    {
                        timeout.Token.ThrowIfCancellationRequested(); var response = error.Response as HttpWebResponse;
                        if (response == null) throw new ModelConfigurationException("api_connection_failed");
                        using (response) { int code = (int)response.StatusCode; throw new ModelConfigurationException(code >= 300 && code < 400 ? "api_redirect_rejected" : code == 401 ? "api_unauthorized" : code == 403 ? "api_forbidden" : code == 429 ? "api_rate_limited" : "api_http_error"); }
                    }
                }
            }
        }
    }
}
