using AzureFunctions.Api.WorkerHost;
using Common.Extensions;
using Common.Recording;
using Infrastructure.Persistence.Azure.ApplicationServices;
using Infrastructure.Persistence.Interfaces;
using Infrastructure.Web.Hosting.Common.ApplicationServices;
using Infrastructure.Web.Hosting.Common.Extensions;
using JetBrains.Annotations;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Infrastructure.Api.WorkerHost.IntegrationTests.AzureFunctions;

[CollectionDefinition("AzureFunctions", DisableParallelization = true)]
public class AllAzureFunctionSpecs : ICollectionFixture<AzureFunctionHostSetup>
{
}

[UsedImplicitly]
public class AzureFunctionHostSetup : IApiWorkerSpec, IDisposable
{
    private static readonly TimeSpan FunctionTriggerWaitLatency = TimeSpan.FromSeconds(5);
    private IHost? _host;
    private Action<IServiceCollection>? _overridenTestingDependencies;

    public AzureFunctionHostSetup()
    {
        var settings = new AspNetConfigurationSettings(new ConfigurationBuilder()
            .AddJsonFile("appsettings.Testing.json", true)
            .Build()).Platform;
        var recorder = NullRecorder.Instance;
        QueueStore = AzureStorageAccountQueueStore.Create(recorder, settings);
        AzureStorageAccountBase.InitializeAllTests();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_host.Exists())
            {
                _host.StopAsync().GetAwaiter().GetResult();
                _host.Dispose();
            }

            AzureStorageAccountBase.CleanupAllTests();
        }
    }

    public void Start()
    {
        _host = new HostBuilder()
            .ConfigureAppConfiguration(builder =>
            {
                builder
                    .AddJsonFile("appsettings.Testing.json", true);
            })
            .ConfigureAzureFunctionTesting<global::AzureFunctions.Api.WorkerHost.Program>()
            .ConfigureServices((_, services) =>
            {
                if (_overridenTestingDependencies.Exists())
                {
                    _overridenTestingDependencies.Invoke(services);
                }
            })
            .Build();
        _host.Start();
    }

    public IQueueStore QueueStore { get; }

    public void OverrideTestingDependencies(Action<IServiceCollection> overrideDependencies)
    {
        _overridenTestingDependencies = overrideDependencies;
    }

    // ReSharper disable once MemberCanBeMadeStatic.Global
    public void WaitForQueueProcessingToComplete()
    {
        Thread.Sleep(FunctionTriggerWaitLatency);
    }

    public TService GetRequiredService<TService>()
        where TService : notnull
    {
        if (_host.NotExists())
        {
            throw new InvalidOperationException("Host has not be started yet!");
        }

        return _host.Services.Resolve<TService>();
    }
}

internal static class AzureFunctionTestingExtensions
{
    /// <summary>
    ///     Configures the test process to load and run the azure functions
    /// </summary>
    public static IHostBuilder ConfigureAzureFunctionTesting<TProgram>(this IHostBuilder builder)
    {
        //HACK: this does not work yet, still waiting for the Azure Functions team to solve this problem:
        // https://github.com/Azure/azure-functions-dotnet-worker/issues/281
        return builder
            .ConfigureFunctionsWorkerDefaults()
            .InvokeAutoGeneratedConfigureMethods<TProgram>()
            .ConfigureServices((context, services) =>
            {
                services.RemoveAll<IHostedService>(); // We need remove this host to prevent gRPC running
                services.AddDependencies(context);
            });
    }

    /// <summary>
    ///     Invokes auto-generated configuration methods for a given <see cref="IHostBuilder" />.
    ///     This method searches for classes that implement the <see cref="IAutoConfigureStartup" /> interface in the assembly
    ///     of the specified type <see cref="TProgram" />.
    ///     This mimics what the <see cref="WorkerHostBuilderExtensions.ConfigureFunctionsWorkerDefaults" /> method does on
    ///     startup in an Azure project
    /// </summary>
    private static IHostBuilder InvokeAutoGeneratedConfigureMethods<TProgram>(this IHostBuilder builder)
    {
        var startupTypes = typeof(TProgram).Assembly
            .GetTypes()
            .Where(t => typeof(IAutoConfigureStartup).IsAssignableFrom(t)
                        && t is { IsInterface: false, IsAbstract: false });

        foreach (var type in startupTypes)
        {
            var instance = (IAutoConfigureStartup)Activator.CreateInstance(type)!;
            instance.Configure(builder);
        }

        return builder;
    }
}