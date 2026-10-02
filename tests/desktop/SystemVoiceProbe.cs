// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Reflection;
using System.Threading;
internal static class SystemVoiceProbe
{
    static int Main(string[] args)
    {
        if(args.Length!=2)return 2;
        try {
            var type=Assembly.LoadFrom(args[0]).GetType("Mansur.Next.Desktop.SystemVoiceFallback",true);
            var render=type.GetMethod("Render",BindingFlags.Static|BindingFlags.NonPublic);
            using(var cancel=new CancellationTokenSource()) {
                cancel.Cancel(); bool cancelled=false;
                try {render.Invoke(null,new object[]{"Hello",1.0,cancel.Token});} catch(TargetInvocationException error){cancelled=error.InnerException is OperationCanceledException;}
                if(!cancelled)throw new Exception("cancellation_failed");
            }
            byte[] pcm=(byte[])render.Invoke(null,new object[]{"Hello",1.0,CancellationToken.None});
            if(pcm==null||pcm.Length<100||pcm.Length%2!=0||pcm.Length>8*1024*1024)throw new Exception("invalid_audio");
            bool signal=false;foreach(byte value in pcm)if(value!=0){signal=true;break;}
            if(!signal)throw new Exception("silent_audio");
            File.WriteAllText(args[1],"{\"status\":\"PASS\",\"sample_rate\":24000,\"pcm_bytes\":"+pcm.Length+",\"cancel_before_start\":true,\"scope\":\"Fixed Hello; real Windows English voice rendered to memory; no playback or user application.\"}");
            return 0;
        }catch(Exception){File.WriteAllText(args[1],"{\"status\":\"UNAVAILABLE\",\"scope\":\"Windows English voice synthesis not confirmed on this computer. No playback.\"}");return 1;}
    }
}
