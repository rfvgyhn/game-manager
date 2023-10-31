[<RequireQualifiedAccess>]
module Azure

open System.Threading.Tasks
open Azure
open Azure.ResourceManager
open Azure.ResourceManager.Compute
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

let getStates servers : Task<Map<Server, Result<ServerState, string>>> = task {
    // Azure takes super long to respond. Just let the client poll for updates
    // so as to not block initial page load
    let statuses =
        servers
        |> List.map (fun (s, _) -> (s, Ok ServerState.Fetching))
        |> Map.ofList
    return statuses
}

let getState (client: ArmClient) ct config = task {
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

let start (client: ArmClient) ct config = task {
    let resId = VirtualMachineResource.CreateResourceIdentifier(config.SubscriptionId, config.ResourceGroup, config.VmName)
    let vm = client.GetVirtualMachineResource(resId)
    let! _ = vm.PowerOnAsync(WaitUntil.Started, ct)
    
    return Ok ServerState.Starting
}