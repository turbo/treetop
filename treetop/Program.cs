using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace treetop
{
    public static class Program
    {
        private static void Main(string[] args) => new Stat(args).Summarize();
    }
    
    public class Stat
    {
        private const string Header = "treetop v1.0.5";
        private double _memory;
        private double _cpu;
        private readonly Dictionary<ulong, Process> _processes;
        private readonly long _total;
        private readonly Stopwatch _sampleTime;
        private readonly ulong _rootPid = 1;
        private readonly bool _recurse = true;
        private int _longName = 5;
        private readonly long _memLimit;
        private readonly int _delay;
        private readonly double _cpuLimit;
        private readonly bool _skipErrors;
        private readonly int _pidLimit; 
        private int _timeLimit; // TODO Enforce, only in continuous mode?
        private bool _jsonBuf; // Output as JSON TODO implement
        private readonly List<ulong> excList = new List<ulong>();

        private class Process
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
                    case "--memory-limit":
                        _memLimit = long.Parse(args[++ptr]);
                        break;
                    case "-c":
                    case "--cpu-limit":
                        _cpuLimit = long.Parse(args[++ptr]);
                        break;
                    case "-s":
                    case "--single-pid":
                        _recurse = false;
                        break;
                    case "-d":
                    case "--delay":
                        _delay = int.Parse(args[++ptr]);
                        break;
                    case "-i":
                    case "--ignore-errors":
                        _skipErrors = true;
                        break;
                    case "-p":
                    case "--pid-limit":
                        _pidLimit = int.Parse(args[++ptr]);
                        break;
                    case "-t":
                    case "--time-limit":
                        _timeLimit = int.Parse(args[++ptr]);
                        break;
                    case "-j":
                    case "--json":
                        _jsonBuf = true;
                        break;
                    case "-e":
                    case "--exclude":
                        excList.AddRange(args[++ptr].Split(",").Select(ulong.Parse));
                        break;
                    default:
                        _rootPid = ulong.Parse(args[ptr]);
                        break;
                }
                
                Console.WriteLine($"Arg{ptr}: {args[ptr]}");
            }
            
            
            _processes = new Dictionary<ulong, Process>();
            _sampleTime = new Stopwatch();
            _sampleTime.Start();
            
            _total = -CPUGetTotalUsage();
            
            AddUpdateProcess(_rootPid);
            if (_recurse) BuildProccessTree(_rootPid);
            
            if(_delay > 0) Thread.Sleep(Math.Max(_delay - (int)_sampleTime.Elapsed.TotalMilliseconds, 0));
            
            _total += CPUGetTotalUsage();
        }

        private long CPUGetProcessUsage(ulong inPid)
        {
            var pidstat = File.ReadAllText($"/proc/{inPid}/stat")
                .Split(" ");
            
            var length = (
                _processes[inPid].Command = 
                    File.ReadAllText($"/proc/{inPid}/cmdline")
                        .Split("\0")
                        .First()
                        .Split(" ")
                        .First()
            ).Length;
            
            _longName = Math.Max(length, _longName);

            _processes[inPid].Mode = pidstat.Skip(2).First();
            
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

        public void Summarize()
        {
            var sublist = _processes.Aggregate(
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
                $"({Math.Round(_sampleTime.Elapsed.TotalMilliseconds, 2)} ms / " +
                $"{Math.Round(1e3 / _sampleTime.Elapsed.TotalMilliseconds, 3)} Hz)" +
                $" PID {_rootPid}" +
                $"{(_recurse && _processes.Count > 1 ? " and " + (_processes.Count - 1) + " childs" : "")} ===\n" +
                $"{FormatLine(_rootPid, "total", _memory, _cpu, "+")}{sublist}"
            );
        }

        private double PidGetCPU(ulong pid, long sst)
        {
            var cpu = Environment.ProcessorCount * (sst + CPUGetProcessUsage(pid)) * 1e2 / _total;
            
            _cpu += cpu;
            if(_cpuLimit > 0 && _cpu > _cpuLimit) killWithFire();
            
            return cpu;
        }

        private string FormatLine(ulong pid, string cmd, double mem, double cpu, string mod = " ") 
            => $"{(_rootPid == pid && mod == " " ? "*" : mod)}" +
               $" {string.Format("{0,8}", pid)}" +
               $" {string.Format("{0,-" + _longName + "}", cmd)}" +
               $" {string.Format("{0,8}", Math.Round(mem, 2))} M" +
               $" {string.Format("{0,6}", Math.Round(cpu, 2))} %\n";

        private void BuildProccessTree(ulong inPid)
        {
            if(excList.Contains(inPid)) return;
            
            var input = File.ReadAllText($@"/proc/{inPid}/task/{inPid}/children")
                .Split(" ")
                .Where(k => !string.IsNullOrEmpty(k))
                .Select(ulong.Parse)
                .ToList();

            if (!input.Any()) return; 
            
            foreach (var process in input)
            {
                if(process == _rootPid) continue;
                AddUpdateProcess(process);
                BuildProccessTree(process);
            }
        }

        private void AddUpdateProcess(ulong pid)
        {
            _processes.Add(pid, new Process());
            if(_pidLimit > 1 && _processes.Count > _pidLimit) killWithFire();
            
            _memory += GetPidMemory(pid);
            if (_memLimit > 0 && _memory > _memLimit) killWithFire();
            
            _processes[pid].StartPidTime = -CPUGetProcessUsage(pid);
        }

        private void killWithFire()
        {
            foreach (var proc in _processes)
                System.Diagnostics.Process.Start("/bin/bash", $" -c 'kill -9 {proc.Key}'");
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

                return _processes[pid].Memory = lost / 1024f;
            }
            catch (UnauthorizedAccessException)
            {
                if (_skipErrors) return 0f;

                Console.Error.WriteLine(Header);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.Write("Error");
                Console.ResetColor();
                Console.Error.WriteLine($": You don't have permission to access the smap data of process {pid}!");
                Environment.Exit(1);
            }
            catch (FileNotFoundException)
            {
                if (pid != _rootPid) return 0f;
                
                Console.Error.WriteLine(Header);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.Write("Error");
                Console.ResetColor();
                Console.Error.WriteLine($": Process {pid} doesn't exist!");
                Environment.Exit(1);
            }

            return 0f;
        }
    }
}