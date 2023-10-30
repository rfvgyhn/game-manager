
[<RequireQualifiedAccess>]
module Server

open System.Net.Http
open System.Threading.Tasks
open Docker.DotNet
open Microsoft.Extensions.Logging
open Types

module private Azure =
    let getStates client ids : Task<Map<string, Result<ServerState, string>>> = Map.empty |> Task.FromResult
    let getState client id = failwith "todo"
    let start client id = failwith "todo"
    
module private Docker =
    open System.Collections.Generic
    open Docker.DotNet.Models
    open System
    
    let private sendRequest (logger: ILogger) fn = task {
        let err (e: exn) =
            let msg = "Docker request failed"
            logger.LogError(e, msg)
            Result.Error msg
            
        try
            return! fn                
        with
        | :? DockerApiException as e -> return err e
        | :? HttpRequestException as e -> return err e
        | :? TaskCanceledException as e -> return err e
    }
    
    type private Health = Starting | Unhealthy | Healthy | None | Unknown of string
    let private mapState dockerState health =
        match dockerState, health with
        | "running", Starting -> ServerState.Starting
        | "running", _
        | "created", _
        | "restarting", _ -> Running
        | "removing", _
        | "paused", _
        | "exited", _
        | "dead", _ -> Stopped
        | s, _ -> Error $"Unknown state '%s{s}'"
    
    let private createParameters (logger: ILogger) names =
        let filter = Dictionary<string, IDictionary<string, bool>>(
                        dict [ ("name", Dictionary<string, bool>(
                                            names |> List.map (fun n -> (n, true)) |> dict
                                        ) :> IDictionary<string, bool>) ]
                    )
        logger.LogDebug(sprintf "Filter: %s" (String.Join(": ", names)))
        let parameters = ContainersListParameters()
        parameters.All <- Nullable<bool>(true)
        parameters.Filters <- filter
        parameters
    let private getHealth (dockerClient: IDockerClient) id = task {
        let! data = dockerClient.Containers.InspectContainerAsync(id)
        if data.State.Health = null then
            return None
        else
            return match data.State.Health.Status with
                   | "starting" -> Starting
                   | "healthy" -> Healthy
                   | "unhealthy" -> Unhealthy
                   | "none" -> None
                   | s -> Unknown s
    }    
    
    let getStates (logger: ILogger) (client: IDockerClient) names =
        match names with
        | [] -> Ok Map.empty |> Task.FromResult
        | _ -> task {
            let parameters = createParameters logger names                  
            let! containers = client.Containers.ListContainersAsync(parameters)
            let getName (c : ContainerListResponse) = c.Names[0][1..] // Trim first char since docker prepends a '/'
            logger.LogDebug(sprintf "Found Containers: %s" (String.Join(", ", containers |> Seq.map getName)))
            let! healthMapResult =
                containers
                |> Seq.map (fun c -> task {
                    let! health = getHealth client c.ID
                    return (c.ID, health)})
                |> Task.WhenAll
            let healthMap = healthMapResult |> dict
            return containers
                |> Seq.map (fun c -> getName c, mapState c.State healthMap.[c.ID])
                |> Map.ofSeq
                |> Ok
        }
        |> sendRequest logger
    let getState logger (client: IDockerClient) name =
        task {
            let parameters = createParameters logger [ name ]
            let! containers = client.Containers.ListContainersAsync(parameters)

            match containers |> List.ofSeq with
            | [] -> return Result.Error $"Couldn't find container named %s{name}"
            | [ c ] ->
                let! health = getHealth client c.ID
                return Ok <| mapState c.State health
            | _::_ -> return Result.Error $"Multiple containers found with filter name=%s{name}"
        } |> sendRequest logger
    let start logger (client: IDockerClient) name = task {
        let! result = client.Containers.StartContainerAsync(name, null)
        return! match result with
                | false -> Result.Error "Unable to start container" |> Task.FromResult
                | true -> task {
                    let! health = getHealth client name
                    return Ok <| mapState "running" health
                }
                |> sendRequest logger
    }

type Request =
    | AzureVm of (ILogger * IDockerClient * string)
    | Docker of (ILogger * IDockerClient * string)
    
type GetAllRequest = {
    DockerClient: IDockerClient * string list
    AzureClient: IDockerClient * string list
    Logger: ILogger
}

let getStates (request: GetAllRequest) : Task<Map<string, Result<ServerState, string>>> = task {
    let dockerClient, dockerIds = request.DockerClient
    let azureClient, azureIds = request.AzureClient
    let! dockerResult = Docker.getStates request.Logger dockerClient dockerIds
    let dockerStates : Map<string, Result<ServerState, string>> =
        dockerResult
        |> Result.map (fun m -> m |> Map.map (fun _ -> Ok))
        |> Result.defaultWith (fun e -> dockerIds |> Seq.map (fun id -> id, Result.Error e) |> Map.ofSeq)
    
    let! azureStates = Azure.getStates azureClient azureIds
        
    return Map.foldBack Map.add dockerStates azureStates
}
    
let getState (request: Request) : Task<Result<ServerState, string>> =
    match request with
    | AzureVm (logger, client, id) -> Azure.getState client id
    | Docker (logger, client, id) -> Docker.getState logger client id

let start request : Task<Result<ServerState, string>> =
    match request with
    | AzureVm (logger, client, id) -> Azure.start client id
    | Docker (logger, client, id) -> Docker.start logger client id
