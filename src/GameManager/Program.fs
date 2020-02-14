module GameManager.Program

open Docker.DotNet
open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Cors.Infrastructure
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Microsoft.AspNetCore
open Microsoft.Extensions.Configuration
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
    //let dockerApi = Docker.Dummy.api
    let dockerClient = app.ApplicationServices.GetService<IDockerClient>()
    let logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("GameManager.DockerService")
    let dockerApi = Docker.Remote.api dockerClient logger
    let env = app.ApplicationServices.GetService<IWebHostEnvironment>()
    (match env.IsDevelopment() with
    | true  -> app.UseDeveloperExceptionPage()
    | false -> app.UseGiraffeErrorHandler errorHandler)
        .UseCors(configureCors)
        .UseStaticFiles()
        .UseResponseCaching()
        .UseGiraffe <| App.webApp dockerApi

let configureServices (services : IServiceCollection) =
    let serviceProvider = services.BuildServiceProvider()
    let config = serviceProvider.GetService<IConfiguration>()
    let createClient =
        (new DockerClientConfiguration(Uri("unix:///var/run/docker.sock"))).CreateClient()
        
    services
        .AddResponseCaching()
        .AddCors()
        .AddGiraffe()
        .AddSingleton<IDockerClient>(createClient)
        .Configure<ContainerConfig>(config)
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