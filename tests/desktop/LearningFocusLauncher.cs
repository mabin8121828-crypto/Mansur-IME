// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;

internal static class LearningFocusLauncher
{
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int cb; internal string reserved, desktop, title;
        internal int x,y,xSize,ySize,xChars,yChars,fill,flags;
        internal short show,reserved2Size; internal IntPtr reserved2,input,output,error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { internal IntPtr process,thread; internal uint processId,threadId; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool CreateProcess(string application,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfo startup,out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr handle, uint code);
    private static int Main(string[] args)
    {
        if (args.Length != 3 && args.Length != 5) return 2;
        foreach (string arg in args) if (!Path.IsPathRooted(arg) || arg.Contains("\"")) return 2;
        string name="MansurFocusProbe"+Guid.NewGuid().ToString("N");
        IntPtr desktop=CreateDesktop(name,IntPtr.Zero,IntPtr.Zero,0,0xC3,IntPtr.Zero);
        if(desktop==IntPtr.Zero){Console.WriteLine("DESKTOP_FAILED "+Marshal.GetLastWin32Error());return 2;}
        ProcessInfo process=new ProcessInfo();
        try
        {
            // No SwitchDesktop and no keyboard/mouse injection. Only disposable fixture windows.
            var startup=new StartupInfo { cb=Marshal.SizeOf(typeof(StartupInfo)),desktop=@"winsta0\"+name };
            var command=new StringBuilder();
            foreach(string arg in args) { if(command.Length>0)command.Append(' ');command.Append('"').Append(arg).Append('"'); }
            if(!CreateProcess(args[0],command,IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,Path.GetDirectoryName(args[0]),ref startup,out process))
            {Console.WriteLine("CHILD_FAILED "+Marshal.GetLastWin32Error());return 2;}
            if(WaitForSingleObject(process.process,args.Length==3?15000u:180000u)!=0){TerminateProcess(process.process,4);return 4;}
            uint code; if(!GetExitCodeProcess(process.process,out code))return 2;
            Console.WriteLine("fixture_pid="+process.processId+" actual_exit="+code);
            string log=args.Length==3?args[2]:Path.Combine(args[4],"run.log");
            if(File.Exists(log))Console.WriteLine(File.ReadAllText(log));
            return (int)code;
        }
        finally {if(process.thread!=IntPtr.Zero)CloseHandle(process.thread);if(process.process!=IntPtr.Zero)CloseHandle(process.process);CloseDesktop(desktop);}
    }
}
