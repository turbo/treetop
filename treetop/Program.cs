using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace treetop
{
    public class Program
    {
        private static void Main(string[] args) => new Stat(args).Summarize();
    }
    
    public class Stat
    {
        private const string Header = "treetop v1.0.5";
        public double Memory;
        public double CPU;
        public Dictionary<ulong, Process> Processes;
        public long Total;
        public Stopwatch SampleTime;
        public ulong RootPid = 1;
        public bool Recurse = true;
        public int LongName = 5;
        public long MemLimit = 0;
        public int Delay;
        public double CpuLimit;
        public bool SkipErrors;
        
        public class Process
        {
            public string Command;
            public long StartPidTime;
            public double Memory;
            public string Mode;
        }

        public Stat(IReadOnlyList<string> args, int ptr = -1)
        {
            while (++ptr < args.Count)
            {
                switch (args[ptr])
                {
                    case "-m":
                        MemLimit = long.Parse(args[++ptr]);
                        break;
                    case "-c":
                        CpuLimit = long.Parse(args[++ptr]);
                        break;
                    case "-s":
                        Recurse = false;
                        break;
                    case "-t":
                        Delay = int.Parse(args[++ptr]);
                        break;
                    case "-i":
                        SkipErrors = true;
                        break;
                    default:
                        RootPid = ulong.Parse(args[ptr]);
                        break;
                }
                
                Console.WriteLine($"Arg{ptr}: {args[ptr]}");
            }
            
            
            Processes = new Dictionary<ulong, Process>();
            SampleTime = new Stopwatch();
            SampleTime.Start();
            
            Total = -CPUGetTotalUsage();
            
            AddUpdateProcess(RootPid);
            if (Recurse) BuildProccessTree(RootPid);
            
            if(Delay > 0) Thread.Sleep(Math.Max(Delay - (int)SampleTime.Elapsed.TotalMilliseconds, 0));
            
            Total += CPUGetTotalUsage();
        }

        private long CPUGetProcessUsage(ulong inPid)
        {
            var pidstat = File.ReadAllText($"/proc/{inPid}/stat")
                .Split(" ");
            
            var length = (
                Processes[inPid].Command = 
                    File.ReadAllText($"/proc/{inPid}/cmdline")
                        .Split("\0")
                        .First()
                        .Split(" ")
                        .First()
            ).Length;
            
            LongName = Math.Max(length, LongName);

            Processes[inPid].Mode = pidstat.Skip(2).First();
            
            return pidstat
                .Skip(13)
                .Take(2) 
                .Select(long.Parse)
                .Sum();
        }

        private static long CPUGetTotalUsage() => File.ReadLines("/proc/stat")
            .First()                        
            .Split(" ")                     
            .Skip(2)                        
            .Take(4)                        
            .Select(long.Parse)
            .Sum();                         

        public void Summarize(bool tty = true)
        {
            var sublist = Processes.Aggregate(
                "", 
                (current, proc) => 
                    current + 
                    FormatLine(
                        proc.Key, 
                        proc.Value.Command, 
                        proc.Value.Memory, 
                        PidGetCPU(proc.Key, proc.Value.StartPidTime), 
                        proc.Value.Mode));

            Console.Write(
                $"=== {Header} - " +
                $"({Math.Round(SampleTime.Elapsed.TotalMilliseconds, 2)} ms / " +
                $"{Math.Round(1e3 / SampleTime.Elapsed.TotalMilliseconds, 3)} Hz)" +
                $" PID {RootPid}" +
                $"{(Recurse && Processes.Count > 1 ? " and " + (Processes.Count - 1) + " childs" : "")} ===\n" +
                $"{FormatLine(RootPid, "total", Memory, CPU, "+")}{sublist}"
            );
        }

        private double PidGetCPU(ulong pid, long sst)
        {
            var cpu = Environment.ProcessorCount * (sst + CPUGetProcessUsage(pid)) * 1e2 / Total;
            CPU += cpu;
            return cpu;
        }

        private string FormatLine(ulong pid, string cmd, double mem, double cpu, string mod = " ") 
            => $"{(RootPid == pid && mod == " " ? "*" : mod)}" +
               $" {string.Format("{0,8}", pid)}" +
               $" {string.Format("{0,-" + LongName + "}", cmd)}" +
               $" {string.Format("{0,8}", Math.Round(mem, 2))} M" +
               $" {string.Format("{0,6}", Math.Round(cpu, 2))} %\n";

        private void BuildProccessTree(ulong inPid)
        {
            var input = File.ReadAllText($@"/proc/{inPid}/task/{inPid}/children")
                .Split(" ")
                .Where(k => !string.IsNullOrEmpty(k))
                .Select(ulong.Parse)
                .ToList();

            if (!input.Any()) return; 
            
            foreach (var process in input)
            {
                if(process == RootPid) continue;
                AddUpdateProcess(process);
                BuildProccessTree(process);
            }
        }

        private void AddUpdateProcess(ulong pid)
        {
            Processes.Add(pid, new Process());
            Memory += GetPidMemory(pid);
            Processes[pid].StartPidTime = -CPUGetProcessUsage(pid);
        }

        private double GetPidMemory(ulong pid)
        {
            try
            {
                var smaps = File.ReadAllText($@"/proc/{pid}/smaps");

                var lost = Regex
                    .Matches(smaps, @"\bPss: +(\d+) kB")
                    .Select(m => m.Groups[1].Value)
                    .Select(int.Parse)
                    .Sum();

                return Processes[pid].Memory = lost / 1024f;
            }
            catch (UnauthorizedAccessException)
            {
                if (SkipErrors) return 0f;

                Console.Error.WriteLine(Header);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.Write("Error");
                Console.ResetColor();
                Console.Error.WriteLine($": You don't have permission to access the smap data of process {pid}!");
                Environment.Exit(1);
            }
            catch (FileNotFoundException)
            {
                if (pid == RootPid)
                {
                    Console.Error.WriteLine(Header);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.Write("Error");
                    Console.ResetColor();
                    Console.Error.WriteLine($": Process {pid} doesn't exist!");
                    Environment.Exit(1);
                }
            }

            return 0f;
        }
    }
}