using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace FxRatesSimulation
{


    /// <summary>
    /// Represents a Fx quote
    /// </summary>
    internal sealed class Quote
    {
        public decimal Rate;
        public long UpdatedTicks;
    }

    /// <summary>
    ///  Represents a store from which the quotes will be saved and read
    /// </summary>
    internal interface IRateStore
    {
        void Upsert(int id, decimal rate, long nowTicks, long staleTicks);
        bool TryGet(int id, out Quote quote);
        void Rebuild();
        int Count { get; }
    }

    /// <summary>
    /// The naive implementation which wasn't aware how fast the API would be hammered.
    /// </summary>
    internal sealed class UnsafeRateStore : IRateStore
    {
        private Dictionary<int, Quote> _rates = new Dictionary<int, Quote>();

        public void Upsert(int id, decimal rate, long nowTicks, long staleTicks)
        {
            var rates = _rates;
            if (rates.TryGetValue(id, out var existing) && nowTicks - existing.UpdatedTicks > staleTicks)
            {
                rates.Remove(id);
                rates.Add(id, new Quote { Rate = rate, UpdatedTicks = nowTicks });
                return;
            }
            rates[id] = new Quote { Rate = rate, UpdatedTicks = nowTicks };
        }

        public bool TryGet(int id, out Quote quote) => _rates.TryGetValue(id, out quote);


        public void Rebuild() => _rates = new Dictionary<int, Quote>();

        public int Count => _rates.Count;
    }


    // Necessary classes for simulating the traffic
    internal sealed class Options
    {
        public string Mode = "unsafe";
        public int Writers = Environment.ProcessorCount;
        public int Readers = Environment.ProcessorCount;
        public int WriteRate = 1000;
        public int ReadRate = 2000;
        public int Countries = 250;
        public bool Burst;
        public int BurstIntervalMs = 2000;
        public int BurstLengthMs = 500;
        public int StaleMs = 1000;
        public int DurationSeconds = 120;
        public int StallSeconds = 3;
        public bool WaitForDebugger;
        public bool NoWait;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {a}");
                switch (a)
                {
                    case "--mode": o.Mode = Next().ToLowerInvariant(); break;
                    case "--writers": o.Writers = int.Parse(Next()); break;
                    case "--readers": o.Readers = int.Parse(Next()); break;
                    case "--write-rate": o.WriteRate = int.Parse(Next()); break;
                    case "--read-rate": o.ReadRate = int.Parse(Next()); break;
                    case "--countries": o.Countries = int.Parse(Next()); break;
                    case "--burst": o.Burst = true; break;
                    case "--burst-interval": o.BurstIntervalMs = int.Parse(Next()); break;
                    case "--burst-length": o.BurstLengthMs = int.Parse(Next()); break;
                    case "--stale-ms": o.StaleMs = int.Parse(Next()); break;
                    case "--duration": o.DurationSeconds = int.Parse(Next()); break;
                    case "--stall-seconds": o.StallSeconds = int.Parse(Next()); break;
                    case "--wait-for-debugger": o.WaitForDebugger = true; break;
                    case "--no-wait": o.NoWait = true; break;
                    case "-h":
                    case "--help":
                    case "/?":
                        PrintHelp();
                        Environment.Exit(0);
                        break;
                    default: throw new ArgumentException($"Unknown argument: {args[i]}");
                }
            }
            if (o.Mode != "unsafe" && o.Mode != "locked" && o.Mode != "concurrent")
                throw new ArgumentException("--mode must be unsafe, locked or concurrent");
            return o;
        }

        public static void PrintHelp()
        {
            Console.WriteLine(@"FxRateCache - concurrency bug demo (unsynchronized Dictionary under load)

  --mode unsafe|locked|concurrent  Cache implementation (default: unsafe = the bug)
  --writers N                      Market-feed writer threads (default: cores)
  --readers N                      Pricing reader threads (default: cores)
  --write-rate N                   Updates/sec per writer in a normal market, 0 = unthrottled (default: 1000)
  --read-rate N                    Lookups/sec per reader, 0 = unthrottled (default: 2000)
  --countries N                    Number of country ids (default: 250)
  --burst                          Enable volatility bursts: periodically rebuild the cache, lift the
                                   write throttle and add per-tenor instrument keys (forces resizes)
  --burst-interval MS              Time between bursts (default: 2000)
  --burst-length MS                Length of each burst (default: 500)
  --stale-ms MS                    Quotes older than this are evicted + re-added (default: 1000)
  --duration S                     Run time in seconds, 0 = forever (default: 120)
  --stall-seconds S                Report a thread as hung after S seconds without progress (default: 3)
  --wait-for-debugger              Print PID and wait for Enter before starting
  --no-wait                        Exit at the end even if threads are hung (for scripts)

Examples:
  FxRateCache.exe                                      normal market: looks healthy
  FxRateCache.exe --burst                              volatility: hangs, CPU pegged
  FxRateCache.exe --burst --mode concurrent            the fix");
        }
    }

    // Keeps a thread at roughly N operations/sec by sleeping when it gets ahead of schedule.
    internal sealed class Pacer
    {
        private readonly long _perSecond;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _done;

        public Pacer(int perSecond) => _perSecond = perSecond;

        public void Tick(bool flood)
        {
            if (_perSecond <= 0) return;
            long due = _clock.ElapsedTicks * _perSecond / Stopwatch.Frequency;
            if (flood || due - _done > _perSecond)
            {
                _done = due; // unthrottled, or too far behind: don't try to catch up afterwards
                return;
            }
            if (++_done > due) Thread.Sleep(1);
        }
    }

    internal class Program
    {
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        // One cache line (8 longs) per thread so heartbeat counters don't false-share.
        private const int Stride = 8;

        private static long[] _heartbeats;
        private static int[] _osThreadIds;
        private static string[] _names;
        private static IRateStore _store;
        private static Options _opt;
        private static volatile bool _burstActive;
        private static volatile bool _stopping;
        private static long _errors;
        private static string _firstError;

        static int Main(string[] args)
        {
            try
            {
                _opt = Options.Parse(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                Options.PrintHelp();
                return 2;
            }

            _store =  new UnsafeRateStore();

            int pid = Process.GetCurrentProcess().Id;
            Console.WriteLine($"FxRateCache  runtime={RuntimeInformation.FrameworkDescription}  pid={pid}");
            Console.WriteLine($"mode={_opt.Mode} writers={_opt.Writers} readers={_opt.Readers} countries={_opt.Countries} " +
                              $"burst={_opt.Burst} staleMs={_opt.StaleMs} duration={_opt.DurationSeconds}s cores={Environment.ProcessorCount}");

            if (_opt.WaitForDebugger)
            {
                Console.WriteLine($"Attach your debugger to PID {pid}, then press Enter to start the load...");
                Console.ReadLine();
            }

            // Startup: load today's rates from reference data on one thread, like a real service would.
            var seed = new Random(42);
            for (int id = 1; id <= _opt.Countries; id++)
                _store.Upsert(id, 1m + (decimal)seed.NextDouble() * 100m, DateTime.UtcNow.Ticks, long.MaxValue);

            int total = _opt.Writers + _opt.Readers;
            _heartbeats = new long[total * Stride];
            _osThreadIds = new int[total];
            _names = new string[total];

            var threads = new List<Thread>();
            for (int i = 0; i < _opt.Writers; i++)
            {
                int slot = i;
                _names[slot] = $"FeedWriter-{i}";
                threads.Add(new Thread(() => WriterLoop(slot)) { IsBackground = true, Name = _names[slot] });
            }
            for (int i = 0; i < _opt.Readers; i++)
            {
                int slot = _opt.Writers + i;
                _names[slot] = $"PricingReader-{i}";
                threads.Add(new Thread(() => ReaderLoop(slot)) { IsBackground = true, Name = _names[slot] });
            }
            if (_opt.Burst)
                threads.Add(new Thread(VolatilityLoop) { IsBackground = true, Name = "Volatility" });

            foreach (var t in threads) t.Start();

            bool hung = RunWatchdog(pid);

            _stopping = true;
            if (hung && !_opt.NoWait)
            {
                Console.WriteLine();
                Console.WriteLine($"Threads are still spinning. Process {pid} is kept alive so you can capture it:");
                Console.WriteLine($"  procdump -ma {pid} fxratecache.dmp      (or attach WinDbg: windbg -p {pid})");
                Console.WriteLine("Press Enter to exit.");
                Console.ReadLine();
            }
            return hung ? 1 : 0;
        }

        private static void WriterLoop(int slot)
        {
            _osThreadIds[slot] = (int)GetCurrentThreadId();
            var rnd = new Random(Guid.NewGuid().GetHashCode());
            long staleTicks = TimeSpan.FromMilliseconds(_opt.StaleMs).Ticks;
            int hb = slot * Stride;
            var pacer = new Pacer(_opt.WriteRate);

            while (!_stopping)
            {
                try
                {
                    int country = rnd.Next(1, _opt.Countries + 1);
                    decimal rate = 1m + (decimal)rnd.NextDouble() * 100m;
                    long now = DateTime.UtcNow.Ticks;

                    _store.Upsert(country, rate, now, staleTicks);

                    if (_burstActive)
                    {
                        // During volatility the feed also publishes forward rates per tenor
                        // (1M..12M) as new keys, which makes the dictionary grow and resize.
                        int tenor = rnd.Next(1, 13);
                        _store.Upsert(country * 100 + tenor, rate * (1m + tenor / 1000m), now, staleTicks);
                    }
                }
                catch (Exception ex)
                {
                    RecordError(ex);
                }
                _heartbeats[hb]++;
                // A volatility burst floods the feed: the writers stop pacing themselves.
                pacer.Tick(flood: _burstActive);
            }
        }

        private static void ReaderLoop(int slot)
        {
            _osThreadIds[slot] = (int)GetCurrentThreadId();
            var rnd = new Random(Guid.NewGuid().GetHashCode());
            int hb = slot * Stride;
            decimal checksum = 0;
            var pacer = new Pacer(_opt.ReadRate);

            while (!_stopping)
            {
                try
                {
                    int country = rnd.Next(1, _opt.Countries + 1);
                    if (_store.TryGet(country, out var q) && q != null)
                        checksum += q.Rate * 1.0001m; // "price" something with the rate
                    if (checksum > 1_000_000m) checksum = 0;
                }
                catch (Exception ex)
                {
                    RecordError(ex);
                }
                _heartbeats[hb]++;
                pacer.Tick(flood: false);
            }
        }

        private static void VolatilityLoop()
        {
            while (!_stopping)
            {
                Thread.Sleep(_opt.BurstIntervalMs);
                _store.Rebuild();
                _burstActive = true;
                Thread.Sleep(_opt.BurstLengthMs);
                _burstActive = false;
            }
        }

        private static void RecordError(Exception ex)
        {
            if (Interlocked.Increment(ref _errors) == 1)
                _firstError = $"{ex.GetType().Name}: {ex.Message}";
        }

        // Prints a status line every second and reports threads that stop making progress.
        // Returns true if any thread was hung when the run ended.
        private static bool RunWatchdog(int pid)
        {
            int total = _names.Length;
            var last = new long[total];
            var lastProgress = new DateTime[total];
            var reported = new bool[total];
            var start = DateTime.UtcNow;
            for (int i = 0; i < total; i++) lastProgress[i] = start;

            var proc = Process.GetCurrentProcess();
            var lastCpu = proc.TotalProcessorTime;
            var lastWall = Stopwatch.StartNew();
            bool firstErrorShown = false;
            int hungCount = 0;

            while (_opt.DurationSeconds == 0 || (DateTime.UtcNow - start).TotalSeconds < _opt.DurationSeconds)
            {
                Thread.Sleep(1000);
                var now = DateTime.UtcNow;

                proc.Refresh();
                var cpu = proc.TotalProcessorTime;
                double cpuPct = (cpu - lastCpu).TotalMilliseconds / lastWall.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
                lastCpu = cpu;
                lastWall.Restart();

                long writerOps = 0, readerOps = 0;
                hungCount = 0;
                var newlyHung = new List<int>();
                for (int i = 0; i < total; i++)
                {
                    long cur = Volatile.Read(ref _heartbeats[i * Stride]);
                    long delta = cur - last[i];
                    last[i] = cur;
                    if (i < _opt.Writers) writerOps += delta; else readerOps += delta;

                    if (delta > 0)
                    {
                        lastProgress[i] = now;
                        reported[i] = false;
                    }
                    else if ((now - lastProgress[i]).TotalSeconds >= _opt.StallSeconds)
                    {
                        hungCount++;
                        if (!reported[i])
                        {
                            reported[i] = true;
                            newlyHung.Add(i);
                        }
                    }
                }

                long errors = Interlocked.Read(ref _errors);
                Console.WriteLine($"[{(now - start).TotalSeconds,5:0}s] cpu={cpuPct,5:0.0}%  writes/s={writerOps,9:N0}  reads/s={readerOps,9:N0}  " +
                                  $"entries={SafeCount(),6}  errors={errors,6}  hung={hungCount}/{total}{(_burstActive ? "  [BURST]" : "")}");

                if (errors > 0 && !firstErrorShown)
                {
                    firstErrorShown = true;
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  first error: {_firstError}");
                    Console.ResetColor();
                }

                if (newlyHung.Count > 0)
                {
                    const int shown = 4;
                    var sample = new List<string>();
                    for (int k = 0; k < newlyHung.Count && k < shown; k++)
                        sample.Add($"{_names[newlyHung[k]]} (tid 0x{_osThreadIds[newlyHung[k]]:x})");
                    string more = newlyHung.Count > shown ? $" +{newlyHung.Count - shown} more" : "";
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  !! {newlyHung.Count} thread(s) made no progress for {_opt.StallSeconds}s but CPU is busy - likely spinning: " +
                                      $"{string.Join(", ", sample)}{more}");
                    Console.WriteLine($"  !! Capture now: procdump -ma {pid}");
                    Console.ResetColor();
                }
            }

            return hungCount > 0;
        }

        private static string SafeCount()
        {
            // Reading Count on a corrupted Dictionary is just a field read, so this is safe.
            try { return _store.Count.ToString(); } catch { return "?"; }
        }
    }
}
