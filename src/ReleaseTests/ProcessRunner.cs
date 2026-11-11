using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NicoBE.ReleaseTests
{
    public sealed class Result
    {
        public int ExitCode;
        public bool TimedOut;
        public double Seconds;
    }

    // Every test owns a job. Closing it also terminates FFmpeg/browser/helper children.
    public static class ProcessRunner
    {
        [StructLayout(LayoutKind.Sequential)] struct BasicLimit
        {
            public long ProcessTime, JobTime; public uint Flags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcessLimit; public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong A,B,C,D,E,F; }
        [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit
        {
            public BasicLimit Basic; public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr attributes,string name);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job,int info,ref ExtendedLimit value,uint size);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

        static string Quote(string value)
        {
            var b=new StringBuilder("\""); int slashes=0;
            foreach(char c in value)
            {
                if(c=='\\'){slashes++;continue;}
                b.Append('\\',c=='"'?slashes*2+1:slashes);slashes=0;b.Append(c);
            }
            return b.Append('\\',slashes*2).Append('"').ToString();
        }
        public static Result Run(string executable,string[] args,string directory,
            IDictionary<string,string> environment,string logPrefix,int timeoutSeconds)
        {
            var start=new ProcessStartInfo(executable){WorkingDirectory=directory,UseShellExecute=false,
                CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true,
                StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            var quoted=new List<string>();foreach(string arg in args)quoted.Add(Quote(arg));
            start.Arguments=string.Join(" ",quoted);
            foreach(var item in environment)start.EnvironmentVariables[item.Key]=item.Value;
            IntPtr job=CreateJobObject(IntPtr.Zero,null);
            if(job==IntPtr.Zero)throw new Win32Exception();
            var limits=new ExtendedLimit();limits.Basic.Flags=0x2000; // KILL_ON_JOB_CLOSE
            var timer=Stopwatch.StartNew();
            try
            {
                if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf(typeof(ExtendedLimit))))throw new Win32Exception();
                using(var process=new Process{StartInfo=start})
                {
                    process.Start();
                    if(!AssignProcessToJobObject(job,process.Handle))
                    {
                        int error=Marshal.GetLastWin32Error();
                        try{process.Kill();process.WaitForExit();}catch(InvalidOperationException){}
                        throw new Win32Exception(error,"Cannot isolate test process in job object.");
                    }
                    // Drain both streams concurrently; verbose encoders must not block on full pipes.
                    var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
                    bool finished=process.WaitForExit(checked(timeoutSeconds*1000));
                    CloseHandle(job);job=IntPtr.Zero;
                    process.WaitForExit();
                    File.WriteAllText(logPrefix+".stdout.log",stdout.GetAwaiter().GetResult(),new UTF8Encoding(false));
                    File.WriteAllText(logPrefix+".stderr.log",stderr.GetAwaiter().GetResult(),new UTF8Encoding(false));
                    return new Result{ExitCode=process.ExitCode,TimedOut=!finished,Seconds=timer.Elapsed.TotalSeconds};
                }
            }
            finally{if(job!=IntPtr.Zero)CloseHandle(job);}
        }
    }
}
