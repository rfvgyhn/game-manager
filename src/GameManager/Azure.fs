[<RequireQualifiedAccess>]
module Azure

open Azure
open Azure.ResourceManager
open Azure.ResourceManager.Compute
open Microsoft.Extensions.Logging
open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Types

type FakeArmOperation() =
    inherit ArmOperation()
    override this.GetRawResponse() = raise <| NotImplementedException()
    override this.UpdateStatusAsync(cancellationToken) = raise <| NotImplementedException()
    override this.UpdateStatus(cancellationToken) = raise <| NotImplementedException()
    override this.Id = raise <| NotImplementedException()
    override this.HasCompleted = raise <| NotImplementedException()

type VmStatus = { Code: string }
type VmInstanceView = { Statuses: VmStatus list }
module VmInstanceView =
    let deallocated() = { Statuses = [{ Code = "PowerState/deallocated" }; { Code = "ProvisioningState/succeeded"}] }
    let starting() = { Statuses = [{ Code = "PowerState/starting" }; { Code = "ProvisioningState/succeeded"}] }
    let running() = { Statuses = [{ Code = "PowerState/running" }; { Code = "ProvisioningState/succeeded"}] }

type IAzureClient =
    abstract member GetVmInstanceViewAsync: cancellationToken: CancellationToken -> resId: Core.ResourceIdentifier -> Task<VmInstanceView option>
    abstract member PowerOnVmAsync: cancellationToken: CancellationToken -> resId: Core.ResourceIdentifier -> waitUntil: WaitUntil -> Task<ArmOperation>

type FakeAzureClient() =
    let vms = ConcurrentDictionary<Core.ResourceIdentifier, VmInstanceView>()
    member this.AddVm subId resGroup vmName (vm: VmInstanceView) =
        vms.TryAdd(VirtualMachineResource.CreateResourceIdentifier(subId, resGroup, vmName), vm) |> ignore
    
    interface IAzureClient with
        member this.GetVmInstanceViewAsync ct resId = task {
            do! Task.Delay(Random.Shared.Next(250, 1000), ct)
            match vms.TryGetValue(resId) with
            | true, vm -> return Some vm
            | _ -> return None
        }

        member this.PowerOnVmAsync ct resId waitUntil = task {
            if vms.ContainsKey(resId) then
                match waitUntil with
                | WaitUntil.Started ->
                    do! Task.Delay(Random.Shared.Next(250, 750), ct)
                    vms[resId] <- VmInstanceView.starting()
                | WaitUntil.Completed ->
                    do! Task.Delay(Random.Shared.Next(500, 1000), ct)
                    vms[resId] <- VmInstanceView.running()
                | _ -> raise <| ArgumentOutOfRangeException()
                
            return FakeArmOperation()
        }

type RealAzureClient(client: ArmClient) =
    interface IAzureClient with
        member this.GetVmInstanceViewAsync ct resId = task {
            let vm = client.GetVirtualMachineResource(resId)
            let! view = vm.InstanceViewAsync(ct)
            if view.HasValue then
                return { Statuses = view.Value.Statuses |> Seq.map (fun s -> { Code = s.Code }) |> Seq.toList } |> Some
            else
                return None
        }
        member this.PowerOnVmAsync ct resId waitUntil = task {
            let vm = client.GetVirtualMachineResource(resId)
            return! vm.PowerOnAsync(waitUntil, ct)
        }

type AzureEvent =
    | ResourceAction of type': string * operation: string
    | ResourceWrite of type': string * operation: string
    | AvailabilityStatus of state: string
    | ResourceAnnotated of name: string
    | Failure of string
    | Unknown

module AzureEvent =
    open System.Text.Json.Nodes
    
    // https://github.com/MicrosoftDocs/azure-docs/blob/main/articles/event-grid/event-schema-resource-groups.md#event-grid-event-schema
    // https://github.com/MicrosoftDocs/azure-docs/blob/main/articles/event-grid/event-schema-health-resources.md
    // https://learn.microsoft.com/en-us/azure/service-health/resource-health-vm-annotation
    let parse (event: JsonNode) =
        let nodeToStr prop = prop |> JsonNode.asStr |> Option.defaultValue ""
        let (?>) = JsonNode.getValue
        let eventType = event["eventType"] |> nodeToStr
        let eventTime = 
            event["eventTime"] |> nodeToStr |> DateTimeOffset.tryParse |> Option.defaultValue DateTimeOffset.UtcNow
        let lastUpperCaseWord (input: string) =
            match input |> Seq.tryFindIndexBack Char.IsUpper with
            | Some i -> input.Substring(i)
            | None -> "Unknown"
        let parseResource eventType =
            let type' = lastUpperCaseWord eventType
            let op = event ?> "data" ?> "operationName" |> nodeToStr
            type', op
        
        match eventType with
        | "Microsoft.ResourceNotifications.HealthResources.AvailabilityStatusChanged" ->
            let props = event ?> "data" ?> "resourceInfo" ?> "properties"
            let state = props ?>  "availabilityState" |> nodeToStr
            let timestamp = props ?> "occurredTime" |> nodeToStr |> DateTimeOffset.tryParse
            AvailabilityStatus state, timestamp |> Option.defaultValue eventTime
        | "Microsoft.ResourceNotifications.HealthResources.ResourceAnnotated" ->
            let props = event ?> "data" ?> "resourceInfo" ?> "properties"
            let name = props ?> "annotationName" |> nodeToStr
            let timestamp = props ?> "occurredTime" |> nodeToStr |> DateTimeOffset.tryParse
            ResourceAnnotated name, timestamp |> Option.defaultValue eventTime
        | t when t.EndsWith("Failure") ->
            event ?> "data" ?> "message" |> nodeToStr |> Failure, eventTime
        | t when t.StartsWith("Microsoft.Resources.ResourceWrite") ->
            parseResource t |> ResourceWrite, eventTime
        | t when t.StartsWith("Microsoft.Resources.ResourceAction") ->
            parseResource t |> ResourceAction, eventTime
        | _ -> Unknown, eventTime
    
    let private matchOp (eventTarget: string) (opTarget: string) (event: string) (operation: string) =
        if event = eventTarget && operation.Contains(opTarget + "/action", StringComparison.OrdinalIgnoreCase) then Some ()
        else None

    let private (|Op|_|) (opTarget: string) (operation: string) =
        if operation.Contains(opTarget + "/action", StringComparison.OrdinalIgnoreCase) then Some ()
        else None

    let private (|Success|_|) (search: string) (event: string, operation: string) =
        matchOp "Success" search event operation
        
    let private (|Cancel|_|) (search: string) (event: string, operation: string) =
        matchOp "Cancel" search event operation
    
    let private mapWrite = function
    | Success "start" -> ServerState.Created
    | _ -> ServerState.Unknown 
    
    let private mapAction prevState supportsInitialization = function
    | Success "deallocate"
    | Success "poweroff" -> ServerState.Stopped
    | Success "start" ->
        if supportsInitialization then
            ServerState.Initializing (None, None)
        else
            ServerState.Running
    | Cancel "deallocate" -> prevState
    | Cancel "poweroff" -> ServerState.Running
    | Cancel "start" -> ServerState.Stopped   
    | _ -> ServerState.Unknown
    
    let private mapStatusChange = function
    | "Available" -> ServerState.Running
    | "Unavailable" -> ServerState.Stopped
    | _ -> ServerState.Unknown
    
    let private mapAnnotation = function
    | "VirtualMachineDeallocationInitiated" -> ServerState.Stopping
    | "VirtualMachineAllocated"
    | "VirtualMachineStartInitiatedByControlPlane" -> ServerState.Starting
    | _ -> ServerState.Unknown
    
    let mapToState (prevState: ServerState) (event: AzureEvent) (supportsInitialization: bool) =
        match event with
        | ResourceWrite (type', operation) -> mapWrite (type', operation)
        | ResourceAction (type', operation) -> mapAction prevState supportsInitialization (type', operation)
        | AvailabilityStatus status -> mapStatusChange status
        | ResourceAnnotated name -> mapAnnotation name
        | AzureEvent.Failure msg -> ServerState.Error msg
        | Unknown -> ServerState.Unknown

type private Statuses = { Provisioning: string option; Power: string option }
let private mapStatuses supportsInitialization (statuses: Statuses) =
    //  https://learn.microsoft.com/en-us/azure/virtual-machines/states-billing#power-states-and-billing
    match statuses.Provisioning, statuses.Power with
    | Some "updating", None -> ServerState.Starting
    | _, Some "creating" -> ServerState.Starting
    | _, Some "starting" -> ServerState.Starting
    | _, Some "running" ->
        if supportsInitialization then
            ServerState.Initializing (None, None)
        else
            ServerState.Running
    | _, Some "stopping" -> ServerState.Stopping
    | _, Some "stopped" -> ServerState.Stopped
    | _, Some "deallocating" -> ServerState.Stopping
    | _, Some "deallocated" -> ServerState.Stopped
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

let (|VmPath|_|) (path: string) =
    let segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
    match segments with
    | [| "subscriptions"; subId; "resourceGroups"; resGroup; "providers"; "Microsoft.Compute"; "virtualMachines"; vmName |] ->
        Some (subId, resGroup, vmName)
    | _ -> None

let formatId resourceGroup vmName = $"{resourceGroup}_{vmName}"

let getState (logger: ILogger) (client: IAzureClient) ct (supportsInitialization: bool) config =
    task {
        let err() = Result.Error "Unable to get status"
        let resId = VirtualMachineResource.CreateResourceIdentifier(config.SubscriptionId, config.ResourceGroup, config.VmName)
        
        match! client.GetVmInstanceViewAsync ct resId with
        | Some view ->
            if view.Statuses.Length = 0 then
                logger.LogWarning("ARM client credential is lacking permission 'Microsoft.Compute/virtualMachines/instanceView/read' for VM '{ResourceGroup}/{VmName}'",
                                  config.ResourceGroup, config.VmName)
                return err()
            else
                let getCode (str: string) =
                    view.Statuses
                    |> Seq.choose (fun s -> if s.Code.StartsWith(str) then Some s.Code else None)
                    |> Seq.tryHead
                    |> Option.map (String.split '/' >> Array.last >> String.toLowerInvariant)
                let powerState = getCode "PowerState" 
                let provisioningState = getCode "ProvisioningState"
                
                return mapStatuses supportsInitialization { Power = powerState; Provisioning = provisioningState } |> Ok
        | None -> return err()
    }
    |> sendRequest logger

let getStates (logger: ILogger) (client: IAzureClient) ct configs : Task<Map<Server, Result<ServerState, string>>> =
    // If perf becomes a problem, use the list api grouped by resource group instead
    // one request per vm
    // https://learn.microsoft.com/en-us/rest/api/compute/virtual-machines/list?tabs=dotnet
    task {
        let! states =
            configs
            |> List.map (fun (s: Server, c) -> task {
                let! state = getState logger client ct s.SupportsInitialization c
                return s, state
            })
            |> Task.WhenAll
            
        return Map.ofArray states
    }

let start logger (client: IAzureClient) ct config =
    task {
        let resId = VirtualMachineResource.CreateResourceIdentifier(config.SubscriptionId, config.ResourceGroup, config.VmName)
        let! _ = client.PowerOnVmAsync ct resId WaitUntil.Started
        
        return Ok ServerState.Starting
    }
    |> sendRequest logger