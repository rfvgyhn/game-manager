namespace GameManager

open Docker.DotNet
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open System
open System.Threading
open Types

type PollingBackgroundService(
    statusService: IStateTracker,
    dockerClient: IDockerClient,
    azureClient: Azure.IAzureClient,
    config: AppConfig,
    connectionTracker: IConnectionTracker,
    logger: ILogger<PollingBackgroundService>) =
    inherit BackgroundService()
         
    let getAndNotifyStates ct request = task {
        try
            let! serverStates = ServerHost.getStates ct request
            let now = DateTimeOffset.UtcNow

            serverStates |> Map.iter (fun server newState ->
                let state = newState |> Result.defaultWith ServerState.Error
                statusService.Notify server.Id state now)
        with
        | ex -> logger.LogError(ex, "Failed to poll server statuses")
    }
    
    override _.ExecuteAsync(stoppingToken: CancellationToken) = task {
        let stateRequest : ServerHost.GetAllRequest =
            { Logger = logger
              AzureClient = azureClient
              DockerClient = dockerClient
              Servers = config.Servers |> List.filter (fun s -> s.Enabled && s.StatusMode.IsPull) }
        
        try
            while not stoppingToken.IsCancellationRequested do
                logger.LogInformation("Waiting for connection before polling states")
                do! connectionTracker.WaitForAnyConnection(stoppingToken)
                logger.LogInformation("State polling started; user connected")
                do! getAndNotifyStates stoppingToken stateRequest
                
                use timer = new PeriodicTimer(config.StatusPollingInterval)
                while connectionTracker.Count > 0 && not stoppingToken.IsCancellationRequested do                    
                    let! _ = timer.WaitForNextTickAsync(stoppingToken)
                    if connectionTracker.Count > 0 then
                        do! getAndNotifyStates stoppingToken stateRequest
                
                logger.LogInformation("State polling paused; no connected users")
        with :? OperationCanceledException -> ()
    }