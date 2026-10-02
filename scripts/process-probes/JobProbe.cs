// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class JobProbe
{
    private const uint KillOnClose = 0x2000, BreakawayOk = 0x800, NoWindow = 0x08000000, Breakaway = 0x01000000;
    [StructLayout(LayoutKind.Sequential)] private struct Limits { public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinWork, MaxWork; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct Io { public ulong R1,R2,R3,R4,R5,R6; }
    [StructLayout(LayoutKind.Sequential)] private struct Extended { public Limits Basic; public Io Io; public UIntPtr ProcessMemory,JobMemory,PeakProcess,PeakJob; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct Startup { public int Size; public string Reserved,Desktop,Title; public uint X,Y,XSize,YSize,XCount,YCount,Fill,Flags; public ushort Show,ReservedSize; public IntPtr ReservedBytes,StdIn,StdOut,StdErr; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process,Thread; public uint Pid,Tid; }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool result);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool QueryInformationJobObject(IntPtr job,int kind,out Extended info,uint size,out uint needed);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetInformationJobObject(IntPtr job,int kind,ref Extended info,uint size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateJobObject(IntPtr attributes,string name);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string current,ref Startup startup,out ProcessInfo info);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static string Exe { get { return typeof(JobProbe).Assembly.Location; } }
    private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
    private static Dictionary<string,object> Identity()
    {
        using(var process=Process.GetCurrentProcess())
        {
            bool inJob; if(!IsProcessInJob(process.Handle,IntPtr.Zero,out inJob)) throw new InvalidOperationException("is_job_failed");
            Extended limits; uint needed; bool queried=QueryInformationJobObject(IntPtr.Zero,9,out limits,(uint)Marshal.SizeOf(typeof(Extended)),out needed);
            return new Dictionary<string,object>{{"pid",process.Id},{"session",process.SessionId},{"sid",WindowsIdentity.GetCurrent().User.Value},{"in_job",inJob},{"job_limits",queried?(object)limits.Basic.Flags:null}};
        }
    }
    private static void Save(string path,object value) { File.WriteAllText(path,Json.Serialize(value),new UTF8Encoding(false)); }
    private static Dictionary<string,object> Load(string path) { return Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Encoding.UTF8)); }
    private static int Start(string command,uint flags,out int error)
    {
        var startup=new Startup{Size=Marshal.SizeOf(typeof(Startup)),Flags=1,Show=0}; ProcessInfo info;
        if(!CreateProcess(Exe,new StringBuilder(Quote(Exe)+" "+command),IntPtr.Zero,IntPtr.Zero,false,flags,IntPtr.Zero,Path.GetDirectoryName(Exe),ref startup,out info)) { error=Marshal.GetLastWin32Error(); return 0; }
        error=0; CloseHandle(info.Thread); CloseHandle(info.Process); return (int)info.Pid;
    }
    private static bool Alive(int pid)
    { if(pid==0)return false;try{using(var p=Process.GetProcessById(pid))return !p.HasExited&&String.Equals(p.MainModule.FileName,Exe,StringComparison.OrdinalIgnoreCase);}catch(ArgumentException){return false;} }
    private static int Leaf(string root,string label)
    {
        Save(Path.Combine(root,label+".json"),Identity());
        var watch=Stopwatch.StartNew();
        while(watch.ElapsedMilliseconds<20000&&!File.Exists(Path.Combine(root,label+".stop"))) Thread.Sleep(50);
        return 0;
    }
    private static int Parent(string root)
    {
        var watch=Stopwatch.StartNew();while(!File.Exists(Path.Combine(root,"go"))&&watch.ElapsedMilliseconds<5000)Thread.Sleep(20);
        Save(Path.Combine(root,"parent.json"),Identity());
        int error; int normal=Start("leaf "+Quote(root)+" normal",NoWindow,out error); int normalError=error;
        int detached=Start("leaf "+Quote(root)+" breakaway",NoWindow|Breakaway,out error); int detachedError=error;
        int wmi=0,wmiError=-1;
        using(var type=new ManagementClass("Win32_Process"))
        using(var startupType=new ManagementClass("Win32_ProcessStartup"))
        using(var startup=startupType.CreateInstance())
        using(var input=type.GetMethodParameters("Create"))
        {
            startup["ShowWindow"]=(ushort)0;
            input["CommandLine"]=Quote(Exe)+" leaf "+Quote(root)+" wmi";input["CurrentDirectory"]=Path.GetDirectoryName(Exe);input["ProcessStartupInformation"]=startup;
            using(var output=type.InvokeMethod("Create",input,null)){wmiError=Convert.ToInt32(output["ReturnValue"]);if(wmiError==0)wmi=Convert.ToInt32(output["ProcessId"]);}
        }
        Save(Path.Combine(root,"launched.json"),new {normal,normal_error=normalError,breakaway=detached,breakaway_error=detachedError,wmi,wmi_error=wmiError});
        Thread.Sleep(15000);return 0;
    }
    public static int Main(string[] args)
    {
        try
        {
            if(args.Length==3&&args[0]=="leaf")return Leaf(args[1],args[2]);
            if(args.Length==2&&args[0]=="parent")return Parent(args[1]);
            if(args.Length!=1)return 2;
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            var coordinator=Identity();IntPtr job=CreateJobObject(IntPtr.Zero,null);if(job==IntPtr.Zero)throw new InvalidOperationException("create_job_failed");
            var limits=new Extended{Basic=new Limits{Flags=KillOnClose|BreakawayOk}};
            if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf(typeof(Extended))))throw new InvalidOperationException("set_job_failed");
            int parentPid=0,error;Dictionary<string,object> launched=null;
            try
            {
                parentPid=Start("parent "+Quote(root),NoWindow,out error);if(parentPid==0)throw new InvalidOperationException("parent_start_failed");
                using(var parent=Process.GetProcessById(parentPid))if(!AssignProcessToJobObject(job,parent.Handle))throw new InvalidOperationException("assign_job_failed_"+Marshal.GetLastWin32Error());
                File.WriteAllText(Path.Combine(root,"go"),"go");
                var timer=Stopwatch.StartNew();while(!File.Exists(Path.Combine(root,"launched.json"))&&timer.ElapsedMilliseconds<10000)Thread.Sleep(25);
                launched=Load(Path.Combine(root,"launched.json"));
                foreach(string label in new[]{"normal","breakaway","wmi"})
                {
                    if(Convert.ToInt32(launched[label])==0)continue;
                    timer.Restart();while(!File.Exists(Path.Combine(root,label+".json"))&&timer.ElapsedMilliseconds<3000)Thread.Sleep(25);
                }
            }
            finally{CloseHandle(job);}
            Thread.Sleep(700);
            var report=new Dictionary<string,object>{{"coordinator",coordinator},{"parent",Load(Path.Combine(root,"parent.json"))},{"parent_alive_after_job_close",Alive(parentPid)},{"launch",launched}};
            foreach(string label in new[]{"normal","breakaway","wmi"})
            {
                int pid=Convert.ToInt32(launched[label]);string file=Path.Combine(root,label+".json");
                report[label]=new{identity=File.Exists(file)?Load(file):null,alive_after_job_close=Alive(pid)};
                File.WriteAllText(Path.Combine(root,label+".stop"),"stop");
            }
            var end=Stopwatch.StartNew();while(end.ElapsedMilliseconds<3000&&(Alive(Convert.ToInt32(launched["breakaway"]))||Alive(Convert.ToInt32(launched["wmi"]))))Thread.Sleep(25);
            report["all_test_children_released"]=!Alive(parentPid)&&!Alive(Convert.ToInt32(launched["normal"]))&&!Alive(Convert.ToInt32(launched["breakaway"]))&&!Alive(Convert.ToInt32(launched["wmi"]));
            Save(Path.Combine(root,"result.json"),report);Console.WriteLine(Json.Serialize(report));return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error.GetType().Name+":"+error.Message);return 1;}
    }
}
