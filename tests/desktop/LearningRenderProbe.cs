// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Reflection;
internal static class LearningRenderProbe
{
    [STAThread] static int Main(string[] args)
    {
        if(args.Length!=4)return 2;
        Console.SetOut(new StreamWriter(Path.Combine(args[3],"run.log"),false){AutoFlush=true});
        try {
            var type=Assembly.LoadFrom(args[0]).GetType("Mansur.Next.Desktop.PreviewRenderer",true);
            return (int)type.GetMethod("RunLearning",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{args[3]});
        }catch(Exception){return 1;}
    }
}
