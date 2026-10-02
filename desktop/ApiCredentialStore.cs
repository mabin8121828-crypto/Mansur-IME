// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Mansur.Next.Desktop
{
    internal sealed class ApiCredentialStore
    {
        internal const string FileName = "openrouter.key.dpapi";
        internal static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MansurNext.OpenRouter.v1");
        private readonly string filename;
        private readonly byte[] entropy = Entropy;
        internal string DirectoryPath { get { return Path.GetDirectoryName(filename); } }
        internal ApiCredentialStore(string directory) { filename = Path.Combine(directory, FileName); }
        internal ApiCredentialStore(string directory, string serviceId)
        {
            if (!ApiServices.ValidId(serviceId)) throw new ModelConfigurationException("api_service_invalid");
            filename = Path.Combine(directory, serviceId == ApiServices.LegacyId ? FileName : "service-" + serviceId + ".key.dpapi");
            entropy = serviceId == ApiServices.LegacyId ? Entropy : Encoding.UTF8.GetBytes("MansurNext.ApiService.v1:" + serviceId);
        }
        internal static bool Valid(string value)
        { if (value == null || value.Length < 8 || value.Length > 512) return false; foreach (char c in value) if (c < 33 || c > 126) return false; return true; }
        internal string Load()
        {
            if (!File.Exists(filename)) throw new ModelConfigurationException("api_key_missing");
            byte[] plain = null;
            try
            {
                byte[] encrypted = SharedSettingsValues.ReadBounded(filename, 16384);
                plain = ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.CurrentUser);
                string key = new UTF8Encoding(false, true).GetString(plain);
                if (!Valid(key)) throw new ModelConfigurationException("api_key_unreadable");
                return key;
            }
            catch (CryptographicException) { throw new ModelConfigurationException("api_key_unreadable"); }
            finally { if (plain != null) Array.Clear(plain, 0, plain.Length); }
        }
        internal bool Available
        { get { try { Load(); return true; } catch (Exception error) when (SharedSettingsValues.Recoverable(error) || error is CryptographicException) { return false; } } }
        internal byte[] Snapshot() { return File.Exists(filename) ? SharedSettingsValues.ReadBounded(filename, 16384) : null; }
        internal void Save(string key)
        {
            if (!Valid(key)) throw new ModelConfigurationException("api_key_invalid");
            byte[] plain = Encoding.UTF8.GetBytes(key);
            try { Restore(ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser)); }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        internal void Restore(byte[] bytes)
        {
            if (bytes == null) { if (File.Exists(filename)) File.Delete(filename); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(filename));
            string temporary = filename + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                SharedSettingsValues.WriteNew(temporary, bytes);
                if (File.Exists(filename)) File.Replace(temporary, filename, null); else File.Move(temporary, filename);
            }
            finally { SharedSettingsValues.TryDelete(temporary); }
        }
    }
}
