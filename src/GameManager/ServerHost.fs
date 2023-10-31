
[<RequireQualifiedAccess>]
module ServerHost

open System.Threading.Tasks
open Azure.ResourceManager
open Docker.DotNet
open Microsoft.Extensions.Logging
open Types

[<RequireQualifiedAccess>]
type Request =
    | AzureVm of (ILogger * ArmClient * AzureVmConfig)
    | Docker of (ILogger * IDockerClient * string)
    
type GetAllRequest = {
    DockerClient: IDockerClient
    AzureClient: ArmClient
    Servers: Server list
    Logger: ILogger
}

let getStates ct request : Task<Map<Server, Result<ServerState, string>>> = task {
    let dockerClient = request.DockerClient
    let azureConfigs = request.Servers |> List.choose (fun s ->
        match s.Type with | ServerType.AzureVm c -> Some (s, c) | ServerType.Docker _ -> None)
    let dockerConfigs = request.Servers |> List.choose (fun s ->
        match s.Type with | ServerType.Docker c -> Some (s, c) | ServerType.AzureVm _ -> None)
    let! dockerResult = Docker.getStates ct request.Logger dockerClient dockerConfigs
    let dockerStates : Map<Server, Result<ServerState, string>> =
        dockerResult
        |> Result.map (fun m -> m |> Map.map (fun _ -> Ok))
        |> Result.defaultWith (fun e -> dockerConfigs |> Seq.map (fun (s, _) -> s, Result.Error e) |> Map.ofSeq)
    
    let! azureStates = Azure.getStates azureConfigs
        
    return Map.foldBack Map.add dockerStates azureStates
}
    
let getState ct request : Task<Result<ServerState, string>> =
    match request with
    | Request.AzureVm (logger, client, config) -> Azure.getState client ct config
    | Request.Docker (logger, client, id) -> Docker.getState ct logger client id

let start ct request : Task<Result<ServerState, string>> =
    match request with
    | Request.AzureVm (logger, client, config) -> Azure.start client ct config
    | Request.Docker (logger, client, id) -> Docker.start ct logger client id
