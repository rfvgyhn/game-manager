[<RequireQualifiedAccess>]
module Docker

open System.Collections.Generic
open System.Net.Http
open System.Threading.Tasks
open Docker.DotNet
open Docker.DotNet.Models
open System
open Microsoft.Extensions.Logging
open Types

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
    logger.LogDebug("Filter: {Names}", String.Join(": ", names))
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

let getStates ct (logger: ILogger) (client: IDockerClient) configs =
    match configs with
    | [] -> Ok Map.empty |> Task.FromResult
    | _ -> task {
        let names = configs |> List.map (fun (_, c) -> c.Name)
        let parameters = createParameters logger names                  
        let! containers = client.Containers.ListContainersAsync(parameters, ct)
        let getName (c : ContainerListResponse) = c.Names[0][1..] // Trim first char since docker prepends a '/'
        let getServer (c: ContainerListResponse) =
            configs
            |> List.find (fun (_, config) -> getName c = config.Name)
            |> fst
        logger.LogDebug("Found Containers: {Containers}", String.Join(", ", containers |> Seq.map getName))
        let! healthMapResult =
            containers
            |> Seq.map (fun c -> task {
                let! health = getHealth client c.ID
                return (c.ID, health)})
            |> Task.WhenAll
        let healthMap = healthMapResult |> dict
        return containers
            |> Seq.map (fun c -> getServer c, mapState c.State healthMap.[c.ID])
            |> Map.ofSeq
            |> Ok
    }
    |> sendRequest logger
    
let getState ct logger (client: IDockerClient) name =
    task {
        let parameters = createParameters logger [ name ]
        let! containers = client.Containers.ListContainersAsync(parameters, ct)

        match containers |> List.ofSeq with
        | [] -> return Result.Error $"Couldn't find container named %s{name}"
        | [ c ] ->
            let! health = getHealth client c.ID
            return Ok <| mapState c.State health
        | _::_ -> return Result.Error $"Multiple containers found with filter name=%s{name}"
    } |> sendRequest logger
    
let start ct logger (client: IDockerClient) name = task {
    let! result = client.Containers.StartContainerAsync(name, null, ct)
    return! match result with
            | false -> Result.Error "Unable to start container" |> Task.FromResult
            | true -> task {
                let! health = getHealth client name
                return Ok <| mapState "running" health
            }
            |> sendRequest logger
}