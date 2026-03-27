module GameManager.Program

open System.Net
open System.Reflection
open Azure.Identity
open Azure.ResourceManager
open Docker.DotNet
open Giraffe
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Cors.Infrastructure
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.HttpLogging
open Microsoft.AspNetCore.HttpOverrides
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Identity.Web
open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Types

let errorHandler (ex : Exception) (logger : ILogger) =
    logger.LogError(ex, "An unhandled exception has occurred while executing the request.")
    clearResponse >=> setStatusCode 500 >=> text ex.Message

let configureCors (builder : CorsPolicyBuilder) =
    builder.WithOrigins("http://localhost:8080")
           .AllowAnyMethod()
           .AllowAnyHeader()
           |> ignore

let configureApp (app : IApplicationBuilder) =
    let env = app.ApplicationServices.GetService<IWebHostEnvironment>()
    let config = app.ApplicationServices.GetService<AppConfig>()
    app.UseForwardedHeaders() |> ignore
    (match env.IsDevelopment() with
    | true  -> app.UseDeveloperExceptionPage()
    | false -> app.UseGiraffeErrorHandler errorHandler)
        .UseCors(configureCors)
        .UseStaticFiles()
        .UseResponseCaching()
        .UseHttpLogging()
        .UseAuthentication()
        .UseGiraffe (App.webApp config.AzureEventGridSharedSecret)

// Can't use built-in configuration builder since it can't bind DUs
let parseConfig (config: IConfiguration) =
    let tryParse defaultValue (value: string) =
        match TimeSpan.TryParse(value) with
        | true, value -> value
        | _ -> defaultValue
    let jsonOptions = JsonFSharpOptions.Default()
                          .WithUnionExternalTag()
                          .WithUnionNamedFields()
                          .WithUnionUnwrapRecordCases()
                          .WithUnionUnwrapFieldlessTags()
                          .WithSkippableOptionFields()
                          .ToJsonSerializerOptions()
    jsonOptions.PropertyNameCaseInsensitive <- true
    let parseServers fileName =
        if File.Exists(fileName) then
            use stream = File.OpenRead(fileName)
            JsonSerializer.Deserialize<{| Servers: ServerConfig list option|}>(stream, jsonOptions).Servers
            |> Option.defaultValue []
        else
            []
    let servers =
        parseServers "appsettings.json"
        @ parseServers "appsettings.Local.json"
        |> List.distinctBy _.DisplayName
        |> List.map _.AsServer()
        
    let aegSharedSecret =
        let tryRead() =
            let key = "AzureEventGridSharedSecretFile"
            let path = config[key]
            if String.IsNullOrWhiteSpace(path) then
                None
            else
                try
                    File.ReadAllText(path) |> Some
                with e ->
                    printfn $"Failed to read %s{key} at '%s{path}': {e.Message}"
                    None
        config["AzureEventGridSharedSecret"]
        |> Option.ofObj
        |> Option.orElseWith tryRead
    
    { Servers = servers
      AzureEventGridSharedSecret = aegSharedSecret
      SseHeartbeatInterval = config["SseHeartbeatInterval"] |> tryParse (TimeSpan.FromSeconds 30.) 
      StatusPollingInterval = config["StatusPollingInterval"] |> tryParse (TimeSpan.FromSeconds 5.) }
    
let createAzureClient (serviceProvider: IServiceProvider) : Azure.IAzureClient =
    let env = serviceProvider.GetService<IWebHostEnvironment>()
    
    if env.IsDevelopment() then
        let config = serviceProvider.GetService<AppConfig>()
        let client = Azure.FakeAzureClient()
        config.Servers
        |> List.filter _.Enabled
        |> List.iter (fun s ->
            match s.Type with
            | ServerType.AzureVm vm ->
                client.AddVm vm.SubscriptionId vm.ResourceGroup vm.VmName (Azure.VmInstanceView.deallocated())
                ()
            | ServerType.Docker _ -> ()
        )
            
        client
    else
        let options = DefaultAzureCredentialOptions(
            ExcludeAzureCliCredential = true,
            ExcludeAzureDeveloperCliCredential = true,
            ExcludeAzurePowerShellCredential = true,
            ExcludeBrokerCredential = true,
            ExcludeEnvironmentCredential = false,
            ExcludeInteractiveBrowserCredential = true,
            ExcludeManagedIdentityCredential = false,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeVisualStudioCredential = true,
            ExcludeWorkloadIdentityCredential = true)

        Azure.RealAzureClient(ArmClient(DefaultAzureCredential(options)))

let configureServices (ctx: WebHostBuilderContext) (services : IServiceCollection) =
    let config = parseConfig ctx.Configuration
    let fsharpJsonOptions = JsonFSharpOptions.Default().WithSkippableOptionFields()
    let jsonOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
    services
        .Configure<ForwardedHeadersOptions>(fun (o: ForwardedHeadersOptions) ->
            ctx.Configuration.GetSection("ForwardedHeaders:Options").Bind(o)
            ctx.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string>()
            |> Option.ofObj
            |> Option.map _.Split([|','|], StringSplitOptions.RemoveEmptyEntries)
            |> Option.iter (fun addresses ->
                for address in addresses do
                    o.KnownProxies.Add(address.Trim() |> IPAddress.Parse)
                )
                
        )
        .Configure<HttpLoggingOptions>(ctx.Configuration.GetSection("Logging:Http"))
        .AddResponseCaching()
        .AddCors()
        .AddHttpLogging()
        .AddGiraffe()
        .AddSingleton<Json.ISerializer>(Json.FsharpFriendlySerializer(fsharpJsonOptions, jsonOptions))
        .AddSingleton<IDockerClient>(fun _ -> (new DockerClientConfiguration(Uri("unix:///var/run/docker.sock"))).CreateClient() :> IDockerClient)
        .AddSingleton<Azure.IAzureClient>(createAzureClient)
        .AddSingleton<AppConfig>(config)
        .AddSingleton<IConnectionTracker, InMemoryConnectionTracker>()
        .AddSingleton<IStateTracker>(fun _ -> new ServerStateTracker(config.Servers |> List.filter _.Enabled |> List.length) :> IStateTracker)
        .AddHostedService<PollingBackgroundService>()
        .AddDataProtection() |> ignore
    services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(ctx.Configuration.GetSection("AzureAd")) |> ignore

let configureLogging (builder : ILoggingBuilder) =
    builder.AddConsole()
           .AddDebug() |> ignore
         
let private getLogger (services: IServiceProvider) =
    services.GetService<ILoggerFactory>().CreateLogger($"{nameof(GameManager)}.Program")
           
let private initStates (services: IServiceProvider) = task {
    let logger = getLogger services
    let statusService = services.GetService<IStateTracker>()
    let serverConfig = services.GetService<AppConfig>().Servers
    let enabledServers = serverConfig |> List.filter _.Enabled    
    let! serverStates =
        logger.LogInformation("Getting server states")
        use cts = CancellationTokenSource.CreateLinkedTokenSource(services.GetService<IHostApplicationLifetime>().ApplicationStopping)
        cts.CancelAfter(TimeSpan.FromSeconds 30.)
        try
            ServerHost.getStates cts.Token {
            Logger = logger
            AzureClient = services.GetService<Azure.IAzureClient>()
            DockerClient = services.GetService<IDockerClient>()
            Servers = enabledServers
            }
        with :? OperationCanceledException -> Task.FromResult(Map.empty)
    let states = 
        serverConfig
        |> List.map (fun s ->
            let state =
                if not s.Enabled then
                    Disabled
                else
                    serverStates
                    |> Map.tryFind s
                    |> Option.defaultValue (Ok ServerState.Unknown)
                    |> Result.defaultWith ServerState.Error
            { s with State = state })
    logger.LogInformation("Got server states: {states}", states |> List.map (fun s -> $"%s{s.DisplayName} - %A{s.State}") |> String.concat ", ")
    statusService.Initialize states
}
[<EntryPoint>]
let main args =
    let app = Host.CreateDefaultBuilder(args).ConfigureWebHostDefaults(fun builder ->
                    builder
                        .Configure(Action<IApplicationBuilder> configureApp)
                        .ConfigureServices(Action<WebHostBuilderContext, IServiceCollection> configureServices)
                        .ConfigureLogging(configureLogging)
                        |> ignore
                 ).Build()
    let version = typeof<ServerState>.Assembly
                      .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                      |> Option.ofObj
                      |> Option.map _.InformationalVersion
                      |> Option.defaultValue "0.0.0"

    (getLogger app.Services).LogInformation("Game Manaager - v{Version}", version)
    (initStates app.Services).GetAwaiter().GetResult()
    app.Run()
    0