// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.IO;

namespace Mansur.Next.Desktop
{
    // Compile with the production sources and this explicit Main. Only synthetic test material.
    internal static class ProviderCredentialsFixture
    {
        internal static int Main(string[] args)
        {
            if (args.Length != 1 || !Directory.Exists(args[0])) return 2;
            try { new ApiCredentialStore(args[0]).Save("only-a-synthetic-provider-interop-key"); return 0; }
            catch { Console.Error.WriteLine("credential_fixture_failed"); return 1; }
        }
    }
}
