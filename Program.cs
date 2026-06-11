using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Onec.DebugAdapter.DebugProtocol;
using Onec.DebugAdapter.DebugServer;
using Onec.DebugAdapter.V8;
using Onec.DebugAdapter.Services;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks.Dataflow;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace Onec.DebugAdapter
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            // Необработанные исключения (в т.ч. из async void) не должны ронять процесс адаптера.
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Console.Error.WriteLine($"[onec-debug-adapter] незамеченное исключение задачи: {e.Exception.Message}");
                e.SetObserved();
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Console.Error.WriteLine($"[onec-debug-adapter] необработанное исключение: {(e.ExceptionObject as System.Exception)?.Message}");

            var host = new HostBuilder()
                .ConfigureDefaults(args)
                .ConfigureLogging(builder => builder.ClearProviders())
                .ConfigureServices((context, sc) =>
                {
                    // Scoped: в persistent-режиме каждая сессия получает свой граф сервисов.
                    sc.AddTransient<IDebugServerClient, DebugServerClient>();
                    sc.AddScoped<IDebugConfiguration, DebugConfiguration>();
                    sc.AddScoped<IMetadataProvider, MetadataProvider>();
                    sc.AddScoped<IDebugServerListener, DebugServerListener>();
                    sc.AddScoped<IDebugTargetsManager, DebugTargetsManager>();
                    sc.AddScoped<IStoppingManager, StoppingManager>();
                    sc.AddScoped<IMeasureManager, MeasureManager>();
					sc.AddScoped<DebuggeeProcess>();
					sc.AddScoped<DebugServerProcess>();
					sc.AddScoped<V8DebugAdapter>();
                    sc.AddScoped<IDebugAdapterExtender, DebugAdapterExtender>();

                    if (context.Configuration.GetValue("debug", false))
                        sc.AddHostedService<TcpDebugAdapterService>();
                    else
                        sc.AddHostedService<ConsoleDebugAdapterService>();
                })
                .Build();

            await host.RunAsync();
        }
    }
}
