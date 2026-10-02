// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
// Read-only window metadata for one verified product PID, never editor contents.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
internal static class InstalledLearningWindowProbe
{
    private delegate bool EnumProc(IntPtr window,IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect {internal int left,top,right,bottom;}
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback,IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder text,int length);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] private static extern int GetWindowLong(IntPtr window,int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out int value,int size);
    private static int Main(string[] args)
    {
        if(args.Length!=2)return 2;int pid;if(!Int32.TryParse(args[0],out pid))return 2;
        string path=Process.GetProcessById(pid).MainModule.FileName;
        if(!path.StartsWith(@"C:\Program Files\MansurNext\",StringComparison.OrdinalIgnoreCase)||Path.GetFileName(path)!="MansurNext.Desktop.exe")return 2;
        var serializer=new JavaScriptSerializer();var watch=Stopwatch.StartNew();string previous="";
        using(var log=new StreamWriter(args[1],false,new UTF8Encoding(false)){AutoFlush=true})
        {
            while(watch.Elapsed.TotalSeconds<18)
            {
                var windows=new List<object>();
                EnumWindows((window,parameter)=>{
                    uint owner;GetWindowThreadProcessId(window,out owner);if(owner!=pid)return true;
                    // Only fixed top-level product titles; do not read child text.
                    var title=new StringBuilder(128);GetWindowText(window,title,title.Capacity);
                    if(title.ToString()!="学习窗口")return true;
                    Rect r;GetWindowRect(window,out r);int cloaked;int hr=DwmGetWindowAttribute(window,14,out cloaked,4);
                    windows.Add(new{hwnd=window.ToInt64(),visible=IsWindowVisible(window),style=GetWindowLong(window,-16),ex_style=GetWindowLong(window,-20),
                        left=r.left,top=r.top,width=r.right-r.left,height=r.bottom-r.top,cloaked=hr==0?(int?)cloaked:null});return true;
                },IntPtr.Zero);
                string state=serializer.Serialize(windows);
                if(state!=previous){log.WriteLine(serializer.Serialize(new{elapsed_ms=watch.ElapsedMilliseconds,windows}));previous=state;}
                Thread.Sleep(75);
            }
        }
        return 0;
    }
}
