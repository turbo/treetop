# treetop

> **Note** This program is currently under development. Output and arguments are subject to change. Performance is bad.

`treetop` is a linux program that accurately reports CPU and memory use for a single process, and, optionally, it's children. It's also able to enforce a variety of limits. Consider this a mix of `top` and `pstree`, stripped down to the bare minimals.

### Building

```sh
# Clone this repo, and install dotnetcore 2.0
dotnet restore
dotnet build -c Release
dotnet publish -c Release -r linux-x64
# Binary is now in /bin/Release/dot.../linux.../treetop
```

### Basic Usage

Calling just the binary will output a single shot, using the highest sample rate available, of all processes (tree of PID 1). As a normal user, this will generate an error:

```sh
./treetop 
treetop v1.0.5
Error: You don't have permission to access the smap data of process 1!
```

As you can see, treetop will try to access the memory maps for this process. This is neccessary, because treetop does report the closest thing to the actual, physical memory consumption of a process. This is the cumulative private set size, or the sum of all unshared allocated memory for that process.

Let's look at a more sensible example, using `chrome`:

```sh
./treetop 8683   
=== treetop v1.0.5 - (382.68 ms / 2.613 Hz) PID 8683 and 23 childs ===
+     8683 total                            688.14 M      0 %
S     8683 /opt/google/chrome/chrome        173.47 M      0 %
S     8690 cat                                0.04 M      0 %
S     8691 cat                                0.04 M      0 %
S     8694 /opt/google/chrome/chrome          0.82 M      0 %
S     8697 /opt/google/chrome/nacl_helper     0.11 M      0 %
S     8700 /opt/google/chrome/chrome          0.37 M      0 %
S     8768 /opt/google/chrome/chrome          1.93 M      0 %
S     8817 /opt/google/chrome/chrome          6.79 M      0 %
S     8820 /opt/google/chrome/chrome          4.37 M      0 %
S     8835 /opt/google/chrome/chrome         41.64 M      0 %
S     8838 /opt/google/chrome/chrome         11.82 M      0 %
S     8841 /opt/google/chrome/chrome          2.79 M      0 %
...
```

The header gives some info about the sample taken. This is mostly important for interpreting the CPU usage. We can see the current chrome process has 23 children. If you just want the stats of a single process, use `-s` (`--single`):

```sh
./treetop 8683 -s
=== treetop v1.0.5 - (218.66 ms / 4.573 Hz) PID 8683 ===
+     8683 total                       180.24 M      0 %
S     8683 /opt/google/chrome/chrome   180.24 M      0 %
```

The `total` line gives the overall memory consumption and CPU usage of the tree. Below that, all processes of the tree are listed with their respective share of the consumption. The first character is the process mode, followed by the path, memory usage and then CPU share.

You might be wondering right now, why the CPU usage is always `0`. This is an accurate measurement. Chrome was out of focus and not consuming any CPU time for the 400ish ms that were measured by treetop. You can see the CPU usage live if you kick treetop into continous mode:

```sh
./treetop 8683 -r
=== treetop v1.0.5 - (162.39 ms / 6.157 Hz) PID 8683 and 25 childs ===
+     8683 total                            720.53 M   6.25 %
S     8683 /opt/google/chrome/chrome        166.45 M      0 %
S     8690 cat                                0.04 M      0 %
S     8691 cat                                0.04 M      0 %
S     8694 /opt/google/chrome/chrome          0.79 M      0 %
S     8697 /opt/google/chrome/nacl_helper     0.11 M      0 %
S     8700 /opt/google/chrome/chrome           0.3 M      0 %
S     8768 /opt/google/chrome/chrome          2.12 M      0 %
S     8817 /opt/google/chrome/chrome             7 M      0 %
S     8820 /opt/google/chrome/chrome          4.52 M      0 %
S     8835 /opt/google/chrome/chrome         40.81 M   6.25 %
S     8838 /opt/google/chrome/chrome         11.64 M      0 %
S     8841 /opt/google/chrome/chrome          3.04 M      0 %
...
```

This will now sample usage at just over 6 times per seconds. This makes no sense for monitoring CPU usage, but is useful for enforcing memory limits. E.g. `./treetop 8683 -r -m 750` will kill chrome less than 170ms after it exceeds 750MB of real memory consumption.

To get more sensible measurements of CPU usage, set the sample rate manually. For example, once every 2 seconds:

```sh
./treetop 8683 -r -d 2000
=== treetop v1.0.5 - (2010.11 ms / 0.497 Hz) PID 8683 and 23 childs ===
+     8683 total                            693.44 M   3.07 %
S     8683 /opt/google/chrome/chrome         170.8 M   1.02 %
S     8690 cat                                0.04 M      0 %
S     8691 cat                                0.04 M      0 %
S     8694 /opt/google/chrome/chrome          0.82 M      0 %
S     8697 /opt/google/chrome/nacl_helper     0.11 M      0 %
S     8700 /opt/google/chrome/chrome          0.37 M      0 %
S     8768 /opt/google/chrome/chrome          2.19 M      0 %
S     8817 /opt/google/chrome/chrome          7.16 M      0 %
S     8820 /opt/google/chrome/chrome          4.63 M      0 %
S     8835 /opt/google/chrome/chrome         41.83 M   0.51 %
S     8838 /opt/google/chrome/chrome          12.3 M   0.51 %
S     8841 /opt/google/chrome/chrome          3.14 M      0 %
S     8842 /opt/google/chrome/chrome             5 M      0 %
S     8845 /opt/google/chrome/chrome          9.55 M      0 %
S     8846 /opt/google/chrome/chrome          11.4 M      0 %
S     8938 /opt/google/chrome/chrome         57.69 M   0.51 %
S     8950 /opt/google/chrome/chrome         82.48 M      0 %
S     8969 /opt/google/chrome/chrome         42.47 M   0.51 %
...
```

This will show you the CPU share that each child, and the tree overall got of the available CPU time during the last 2 seconds.

### Enforcing Limits

```text
-m / --memory-limit         will kill the tree after it exceeds this many MB
-c / --cpu-limit            will kill after this much CPU share is exceeded by the tree
-p / --pid-limit            will kill after this many PIDs are spwaned in the tree
-t / --time-limit           (**not enforced yet**) kill when one PID exceeds this runtime in seconds
-e / --exclude              exclude a list (e.g. `5,25,785`) of PIDs
```

### Other arguments

```text
-s / --single-pid           only probe the given PID
-d / --delay                sample delay in ms
-i / --ignore-errors        ignore and just not count PIDs with permission errors
-j / --json                 (**not implemented yet**) output JSON and nothing else
-r / --repeat               loop (clears console if output is NOT json)
```
