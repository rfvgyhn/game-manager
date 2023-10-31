module GameManager.Program

open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open Azure.Identity
open Azure.ResourceManager
open Docker.DotNet
open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Cors.Infrastructure
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Microsoft.AspNetCore
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
    (match env.IsDevelopment() with
    | true  -> app.UseDeveloperExceptionPage()
    | false -> app.UseGiraffeErrorHandler errorHandler)
        .UseCors(configureCors)
        .UseStaticFiles()
        .UseResponseCaching()
        .UseGiraffe <| App.webApp

// Can't use built-in configuration builder since it can't bind DUs
let getConfig() =
    let jsonOptions = JsonFSharpOptions.Default()
                          .WithUnionExternalTag()
                          .WithUnionNamedFields()
                          .WithUnionUnwrapRecordCases()
                          .WithSkippableOptionFields()
                          .ToJsonSerializerOptions()
    use stream = File.OpenRead("appsettings.json")
    let servers = JsonSerializer.Deserialize<{|Servers: ServerConfig list|}>(stream, jsonOptions).Servers
                  |> List.map (fun s -> s.AsServer())
    { Servers = servers }
    
let createArmClient (serviceProvider: IServiceProvider) =
    let env = serviceProvider.GetService<IWebHostEnvironment>()
    let credential : Azure.Core.TokenCredential =
        if not (env.IsDevelopment()) then
            let options = DefaultAzureCredentialOptions()
            options.ExcludeAzureCliCredential <- true
            options.ExcludeAzureDeveloperCliCredential <- true
            options.ExcludeAzurePowerShellCredential <- true
            //options.ExcludeEnvironmentCredential <- true
            options.ExcludeInteractiveBrowserCredential <- true
            //options.ExcludeManagedIdentityCredential <- true
            options.ExcludeSharedTokenCacheCredential <- true
            options.ExcludeVisualStudioCodeCredential <- true
            options.ExcludeVisualStudioCredential <- true
            options.ExcludeWorkloadIdentityCredential <- true
            DefaultAzureCredential(options)
        else
            let config = serviceProvider.GetService<IConfiguration>()
            let tenantId = config["AzureTenantId"]
            let clientId = config["AzureClientId"]
            let clientSecret = config["AzureClientSecret"]
            if tenantId <> null && clientId <> null && clientSecret <> null then
                ClientSecretCredential(tenantId, clientId, clientSecret)
            else
                DefaultAzureCredential()
                
    ArmClient(credential)

let configureServices (services : IServiceCollection) =
    let dockerClient = (new DockerClientConfiguration(Uri("unix:///var/run/docker.sock"))).CreateClient()
    let config = getConfig() 
    services
        .AddResponseCaching()
        .AddCors()
        .AddGiraffe()
        .AddSingleton<IDockerClient>(dockerClient)
        .AddTransient<ArmClient>(createArmClient)
        .AddSingleton<AppConfig>(config)
        .AddDataProtection() |> ignore

let configureLogging (builder : ILoggingBuilder) =
    builder.AddConsole()
           .AddDebug() |> ignore

[<EntryPoint>]
let main args =
    WebHost.CreateDefaultBuilder(args)
        .Configure(Action<IApplicationBuilder> configureApp)
        .ConfigureServices(configureServices)
        .ConfigureLogging(configureLogging)
        .Build()
        .Run()
    0