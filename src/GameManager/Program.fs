module GameManager.Program

open System.IO
open System.Text.Json
open System.Text.Json.Serialization
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

let configureServices (services : IServiceCollection) =
    let createClient = (new DockerClientConfiguration(Uri("unix:///var/run/docker.sock"))).CreateClient()
    let config = getConfig() 
    
    services
        .AddResponseCaching()
        .AddCors()
        .AddGiraffe()
        .AddSingleton<IDockerClient>(createClient)
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