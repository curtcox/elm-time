using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ElmTime.Platform.WebServer;
using Microsoft.AspNetCore.Hosting;
using Pine;

namespace TestElmTime;

public class WebHostAdminInterfaceTestSetup : IDisposable
{
    static string PublicWebHostUrlDefault => "http://localhost:35491";

    static string AdminWebHostUrlDefault => "http://localhost:19372";

    readonly string? publicWebHostUrlOverride;

    readonly string? adminWebHostUrlOverride;

    public string PublicWebHostUrl => publicWebHostUrlOverride ?? PublicWebHostUrlDefault;

    public string AdminWebHostUrl => adminWebHostUrlOverride ?? AdminWebHostUrlDefault;

    readonly string testDirectory;

    readonly string? adminPassword;

    readonly Func<IWebHostBuilder, IWebHostBuilder>? webHostBuilderMap;

    public string ProcessStoreDirectory => Path.Combine(testDirectory, "process-store");

    IFileStore defaultFileStore => new FileStoreFromSystemIOFile(ProcessStoreDirectory);

    readonly IFileStore fileStore;

    public IWebHost StartWebHost(
         Func<IFileStore, IFileStore>? processStoreFileStoreMap = null)
    {
        var webHost =
            (webHostBuilderMap ?? (builder => builder))
            (Microsoft.AspNetCore.WebHost.CreateDefaultBuilder()
            .UseUrls(AdminWebHostUrl)
            .WithSettingPublicWebHostUrls(new[] { PublicWebHostUrl })
            .WithSettingAdminPassword(adminPassword)
            .UseStartup<StartupAdminInterface>()
            .WithProcessStoreFileStore(processStoreFileStoreMap?.Invoke(fileStore) ?? fileStore))
            .Build();

        webHost.StartAsync().Wait();

        return webHost;
    }

    static public WebHostAdminInterfaceTestSetup Setup(
        Func<DateTimeOffset>? persistentProcessHostDateTime = null,
        string? adminPassword = null,
        IFileStore? fileStore = null,
        PineValue? deployAppAndInitElmState = null) =>
        Setup(
            adminPassword: adminPassword,
            fileStore: fileStore,
            deployAppAndInitElmState: deployAppAndInitElmState,
            webHostBuilderMap: builder => builder.WithSettingDateTimeOffsetDelegate(persistentProcessHostDateTime ?? (() => DateTimeOffset.UtcNow)),
            persistentProcessHostDateTime: persistentProcessHostDateTime);

    static public WebHostAdminInterfaceTestSetup Setup(
        Func<IWebHostBuilder, IWebHostBuilder>? webHostBuilderMap,
        string? adminPassword = null,
        IFileStore? fileStore = null,
        PineValue? deployAppAndInitElmState = null,
        string? adminWebHostUrlOverride = null,
        string? publicWebHostUrlOverride = null,
        Func<DateTimeOffset>? persistentProcessHostDateTime = null)
    {
        var testDirectory = Filesystem.CreateRandomDirectoryInTempDirectory();

        var setup = new WebHostAdminInterfaceTestSetup(
            testDirectory,
            adminPassword: adminPassword,
            fileStore: fileStore,
            deployAppAndInitElmState: deployAppAndInitElmState,
            webHostBuilderMap: webHostBuilderMap,
            adminWebHostUrlOverride: adminWebHostUrlOverride,
            publicWebHostUrlOverride: publicWebHostUrlOverride,
            persistentProcessHostDateTime: persistentProcessHostDateTime);

        return setup;
    }

    public System.Net.Http.HttpClient BuildPublicAppHttpClient()
    {
        var handler = new System.Net.Http.HttpClientHandler()
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };

        return new System.Net.Http.HttpClient(handler)
        {
            BaseAddress = new Uri(PublicWebHostUrl),
        };
    }

    public System.Net.Http.HttpClient BuildAdminInterfaceHttpClient()
    {
        return new System.Net.Http.HttpClient
        {
            BaseAddress = new Uri(AdminWebHostUrl),
        };
    }

    public System.Net.Http.HttpClient SetDefaultRequestHeaderAuthorizeForAdmin(System.Net.Http.HttpClient client)
    {
        if (adminPassword == null)
            return client;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(
                Configuration.BasicAuthenticationForAdmin(adminPassword))));

        return client;
    }

    public void Dispose()
    {
        Directory.Delete(testDirectory, true);
    }

    WebHostAdminInterfaceTestSetup(
        string testDirectory,
        string? adminPassword,
        IFileStore? fileStore,
        PineValue? deployAppAndInitElmState,
        Func<IWebHostBuilder, IWebHostBuilder>? webHostBuilderMap,
        string? adminWebHostUrlOverride,
        string? publicWebHostUrlOverride,
        Func<DateTimeOffset>? persistentProcessHostDateTime = null)
    {
        this.testDirectory = testDirectory;

        fileStore ??= defaultFileStore;

        this.adminPassword = adminPassword ?? "notempty";
        this.fileStore = fileStore;
        this.webHostBuilderMap = webHostBuilderMap;
        this.adminWebHostUrlOverride = adminWebHostUrlOverride;
        this.publicWebHostUrlOverride = publicWebHostUrlOverride;

        if (deployAppAndInitElmState != null)
        {
            var compositionLogEvent =
                new ElmTime.Platform.WebServer.ProcessStoreSupportingMigrations.CompositionLogRecordInFile.CompositionEvent
                {
                    DeployAppConfigAndInitElmAppState =
                        new ElmTime.Platform.WebServer.ProcessStoreSupportingMigrations.ValueInFileStructure
                        {
                            HashBase16 = CommonConversion.StringBase16(Composition.GetHash(deployAppAndInitElmState))
                        }
                };

            var processStoreWriter =
                new ElmTime.Platform.WebServer.ProcessStoreSupportingMigrations.ProcessStoreWriterInFileStore(
                fileStore,
                getTimeForCompositionLogBatch: persistentProcessHostDateTime ?? (() => DateTimeOffset.UtcNow),
                fileStore);

            processStoreWriter.StoreComponent(deployAppAndInitElmState);

            processStoreWriter.AppendCompositionLogRecord(compositionLogEvent);
        }
    }

    public IFileStoreReader BuildProcessStoreFileStoreReaderInFileDirectory() =>
        new FileStoreFromSystemIOFile(ProcessStoreDirectory);

    public ElmTime.Platform.WebServer.ProcessStoreSupportingMigrations.ProcessStoreReaderInFileStore BuildProcessStoreReaderInFileDirectory() =>
        new(BuildProcessStoreFileStoreReaderInFileDirectory());

    public IEnumerable<ElmTime.Platform.WebServer.InterfaceToHost.BackendEventStruct> EnumerateStoredUpdateElmAppStateForEvents()
    {
        var processStoreReader = BuildProcessStoreReaderInFileDirectory();

        ElmTime.StateShim.InterfaceToHost.StateShimRequestStruct eventLogEntry(
            ElmTime.Platform.WebServer.ProcessStoreSupportingMigrations.ValueInFileStructure logEntry)
        {
            var component =
                logEntry.LiteralStringUtf8 != null
                ?
                PineValue.Blob(Encoding.UTF8.GetBytes(logEntry.LiteralStringUtf8))
                :
                processStoreReader.LoadComponent(logEntry.HashBase16!);

            if (component is null)
                throw new Exception("component is null");

            if (component is not PineValue.BlobValue blobComponent)
                throw new Exception("component is not a blob");

            var eventString = Encoding.UTF8.GetString(blobComponent.Bytes.Span);

            return JsonSerializer.Deserialize<ElmTime.StateShim.InterfaceToHost.StateShimRequestStruct>(eventString)!;
        }

        return
            BuildProcessStoreReaderInFileDirectory()
            .EnumerateSerializedCompositionLogRecordsReverse()
            .Select(Encoding.UTF8.GetString)
            .Select(r => JsonSerializer.Deserialize<ElmTime.Platform.WebServer.ProcessStoreSupportingMigrations.CompositionLogRecordInFile>(r)!)
            .Select(compositionLogRecord => compositionLogRecord.compositionEvent?.UpdateElmAppStateForEvent)
            .WhereNotNull()
            .Select(eventLogEntry)
            .SelectMany(stateShimEvent => stateShimEvent switch
            {
                ElmTime.StateShim.InterfaceToHost.StateShimRequestStruct.ApplyFunctionShimRequest applyFunctionRequest =>
                applyFunctionRequest.ApplyFunction.functionName switch
                {
                    "processEvent" =>
                    ImmutableList.Create(
                        JsonSerializer.Deserialize<ElmTime.Platform.WebServer.InterfaceToHost.BackendEventStruct>(
                            applyFunctionRequest.ApplyFunction.arguments.serializedArgumentsJson.Single())),

                    _ => ImmutableList<ElmTime.Platform.WebServer.InterfaceToHost.BackendEventStruct>.Empty
                },

                _ => ImmutableList<ElmTime.Platform.WebServer.InterfaceToHost.BackendEventStruct>.Empty
            });
    }
}
