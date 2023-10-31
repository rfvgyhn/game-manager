[<RequireQualifiedAccess>]
module Azure

open System.Threading.Tasks
open Azure
open Azure.ResourceManager
open Azure.ResourceManager.Compute
open Microsoft.Extensions.Logging
open Types

type private Statuses = { Provisioning: string option; Power: string }
let private mapStatuses (statuses: Statuses) =
    // todo: check to see how the provisioning state affects the power state
    //  https://learn.microsoft.com/en-us/azure/virtual-machines/states-billing#power-states-and-billing
    match statuses.Provisioning, statuses.Power with
    | _, "creating" -> ServerState.Starting
    | _, "starting" -> ServerState.Starting
    | _, "running" -> ServerState.Running
    | _, "stopping" -> ServerState.Stopping
    | _, "stopped" -> ServerState.Stopped
    | _, "deallocating" -> ServerState.Stopping
    | _, "deallocated" -> ServerState.Stopped
    | _ -> ServerState.Unknown

let private sendRequest (logger: ILogger) fn = task {
    let err (e: exn) =
        logger.LogError(e,  "Azure API request failed")
        Result.Error "Azure API request failed"
        
    try
        return! fn
    with
    | e -> return err e
}   

let getState logger (client: ArmClient) ct config =
    task {
        let err() = Result.Error "Unable to get status"
        let resId = VirtualMachineResource.CreateResourceIdentifier(config.SubscriptionId, config.ResourceGroup, config.VmName)
        let vm = client.GetVirtualMachineResource(resId)
        let! view = vm.InstanceViewAsync(ct)
        
        if view.HasValue then
            let getCode (str: string) =
                view.Value.Statuses
                |> Seq.choose (fun s -> if s.Code.StartsWith(str) then Some s.Code else None)
                |> Seq.tryHead
                |> Option.map (String.split '/' >> Array.last >> String.toLowerInvariant)
            let powerState = getCode "PowerState" 
            let provisioningState = getCode "ProvisioningState"
            
            match powerState with
            | None -> return err()
            | Some s ->
                return mapStatuses { Power = s; Provisioning = provisioningState } |> Ok
        else
            return err()
    }
    |> sendRequest logger

let getStates (logger: ILogger) (client: ArmClient) ct configs : Task<Map<Server, Result<ServerState, string>>> =
    // If perf becomes a problem, use the list api grouped by resource group instead
    // one request per vm
    // https://learn.microsoft.com/en-us/rest/api/compute/virtual-machines/list?tabs=dotnet
    task {
        let get = (getState logger client ct) >> (sendRequest logger)
        let! states =
            configs
            |> List.map (fun (s, c) -> task {
                let! state = get c
                return s, state
            })
            |> Task.WhenAll
            
        return Map.ofArray states
    }

let start logger (client: ArmClient) ct config =
    task {
        let resId = VirtualMachineResource.CreateResourceIdentifier(config.SubscriptionId, config.ResourceGroup, config.VmName)
        let vm = client.GetVirtualMachineResource(resId)
        let! _ = vm.PowerOnAsync(WaitUntil.Started, ct)
        
        return Ok ServerState.Starting
    }
    |> sendRequest logger