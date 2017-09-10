using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;

namespace treetop
{
    public static class Program
    {
        private static void Main(string[] args)
        {
            var top = new Stat(args);
            do
            {
                top.Trace();
            } while (top.Summarize());
        }
    }
    
    public class Stat
    {
        private const string Header = "treetop v1.0.5";
        private double _memory;
        private double _cpu;
        private Dictionary<ulong, Process> _processes;
        private long _total;
        private Stopwatch _sampleTime;
        private readonly ulong _rootPid = 1;
        private readonly bool _recurse = true;
        private int _longName = 5;
        private readonly long _memLimit;
        private readonly int _delay;
        private readonly double _cpuLimit;
        private readonly bool _skipErrors;
        private readonly int _pidLimit; 
        private int _timeLimit; // TODO Enforce, only in continuous mode?
        private readonly bool _jsonBuf; // Output as JSON TODO implement
        private readonly List<ulong> _excList = new List<ulong>();
        private readonly bool _loop;
        private int _realCount;

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
                        _excList.AddRange(args[++ptr].Split(",").Select(ulong.Parse));
                        break;
                    case "-r":
                    case "--repeat":
                        _loop = true;
                        break;
                    default:
                        _rootPid = ulong.Parse(args[ptr]);
                        break;
                }
            }
        }

        public void Trace()
        {
            _longName = 5;
            _cpu = 0;
            _memory = 0;
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

        public bool Summarize()
        {
            _realCount = _processes.Count;
            
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

            if (_loop && !_jsonBuf) Console.Clear();
            
            // [253.24,3453.23,33.5,[

            if (_jsonBuf)
                Console.WriteLine(
                    $"[{_sampleTime.Elapsed.TotalMilliseconds},{_realCount - 1},{_memory},{_cpu},[{sublist.TrimEnd(',')}]]"
                );
            else
                Console.Write(
                    $"=== {Header} - " +
                    $"({Math.Round(_sampleTime.Elapsed.TotalMilliseconds, 2)} ms / " +
                    $"{Math.Round(1e3 / _sampleTime.Elapsed.TotalMilliseconds, 3)} Hz)" +
                    $" PID {_rootPid}" +
                    $"{(_recurse && _realCount > 1 ? " and " + (_realCount - 1) + " childs" : "")} ===\n" +
                    $"{FormatLine(_rootPid, "total", _memory, _cpu, "+")}{sublist}"
                );

            return _loop;
        }

        private double PidGetCPU(ulong pid, long sst)
        {
            try
            {
                var cpu = Environment.ProcessorCount * (sst + CPUGetProcessUsage(pid)) * 1e2 / _total;

                _cpu += cpu;
                if (_cpuLimit > 0 && _cpu > _cpuLimit) KillWithFire();

                return cpu;
            }
            catch (FileNotFoundException)
            {
                _realCount--;
                return -1;
            }
        }

        private string FormatLine(ulong pid, string cmd, double mem, double cpu, string mod = " ")
        {
            if (cpu < 0) return "";

            if (_jsonBuf)
                return $"[{pid},{HttpUtility.JavaScriptStringEncode(cmd, true)},{mem},{cpu}],";

            return $"{(_rootPid == pid && mod == " " ? "*" : mod)}" +
                   $" {string.Format("{0,8}", pid)}" +
                   $" {string.Format("{0,-" + _longName + "}", cmd)}" +
                   $" {string.Format("{0,8}", Math.Round(mem, 2))} M" +
                   $" {string.Format("{0,6}", Math.Round(cpu, 2))} %\n";
        }

        private void BuildProccessTree(ulong inPid)
        {
            if(_excList.Contains(inPid)) return;
            
            try
            {
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
            catch (FileNotFoundException)
            {
                Process val;
                if(_processes.TryGetValue(inPid, out val))
                    _processes.Remove(inPid);
            }
        }

        private void AddUpdateProcess(ulong pid)
        {
            try
            {
                _processes.Add(pid, new Process());
                if (_pidLimit > 1 && _processes.Count > _pidLimit) KillWithFire();

                _memory += GetPidMemory(pid);
                if (_memLimit > 0 && _memory > _memLimit) KillWithFire();

                _processes[pid].StartPidTime = -CPUGetProcessUsage(pid);
            }
            catch (InvalidDataException)
            {
                
            }
        }

        private void KillWithFire()
        {
            foreach (var proc in _processes)
            {
                try
                {
                    System.Diagnostics.Process.Start("/bin/bash", $"-c \"kill -9 {proc.Key} &>/dev/null\"");
                }
                catch
                {
                    // ignored
                }
            }
            
            Environment.Exit(0);
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
                if (pid != _rootPid)
                {
                    _processes.Remove(pid);
                    throw new InvalidDataException();
                }

                if (_jsonBuf) Environment.Exit(0);
                
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