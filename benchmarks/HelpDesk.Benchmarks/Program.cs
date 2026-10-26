using BenchmarkDotNet.Running;
using HelpDesk.Benchmarks;

// BenchmarkDotNet descubre las clases con [Benchmark] y deja elegir cuáles correr (--filter).
// Se nombra una clase de este proyecto, no "Program": la API también tiene un Program público (.NET 10).
BenchmarkSwitcher.FromAssembly(typeof(TicketByIdBenchmarks).Assembly).Run(args);
