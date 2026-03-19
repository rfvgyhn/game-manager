namespace GameManager

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Channels
open Types

type ServerStatus = {
    Id: string
    State: {| Current: ServerState; Prev: ServerState |}
    LastUpdate: DateTimeOffset
} with static member Empty = { Id = ""; State = {| Current = ServerState.Unknown; Prev = ServerState.Unknown |}; LastUpdate = DateTimeOffset.MinValue }

type private Message =
    | Set of Server list
    | UpdateState of serverId: string * status: ServerState * timestamp: DateTimeOffset
    | Subscribe of lastTimestamp: DateTimeOffset * ChannelWriter<ServerStatus>
    | Unsubscribe of ChannelWriter<ServerStatus>
    | Get of serverId: string * AsyncReplyChannel<ServerStatus>
    | GetAll of AsyncReplyChannel<Map<string, ServerState>>
    | Stop of AsyncReplyChannel<unit>

type IStatusStream =
    inherit IAsyncEnumerable<ServerStatus>
    inherit IDisposable

type IStateTracker =
    abstract member Initialize: servers: Server list -> unit
    abstract member GetState: id: string -> {| Current: ServerState; Prev: ServerState |}
    abstract member GetAllStates: unit -> Map<string, ServerState>
    abstract member Notify : id: string -> state: ServerState -> timestamp: DateTimeOffset -> unit
    abstract member GetStatusStream: lastTimestamp: DateTimeOffset -> IStatusStream

type ServerStateTracker(totalServers: int) =
    let subscribers = List<ChannelWriter<ServerStatus>>()
    let mutable isDisposed = 0

    let throwIfDisposed () =
        if Volatile.Read(&isDisposed) <> 0 then
            raise (ObjectDisposedException(nameof ServerStateTracker))

    let agent = MailboxProcessor.Start(fun inbox ->
        let rec loop (statuses: Map<string, ServerStatus>) = async {
            match! inbox.Receive() with
            | Set servers ->
                let now = DateTimeOffset.UtcNow
                let newStatuses =
                    servers
                    |> List.fold (fun map s ->
                        Map.add s.Id { Id = s.Id; State = {| Current = s.State; Prev = ServerState.Unknown |}; LastUpdate = now } map
                    ) Map.empty
                return! loop newStatuses                
            | Get (serverId, channel) ->
                let status = Map.tryFind serverId statuses |> Option.defaultValue ServerStatus.Empty
                channel.Reply(status)
                return! loop statuses
            | GetAll channel ->
                channel.Reply(statuses |> Map.map (fun _ s -> s.State.Current))
                return! loop statuses
            | Subscribe (lastTimestamp, writer) ->
                statuses |> Map.filter (fun _ s -> s.LastUpdate > lastTimestamp) |> Map.iter (fun _ s -> writer.TryWrite(s) |> ignore)
                subscribers.Add(writer)
                return! loop statuses
            | Unsubscribe writer ->
                writer.TryComplete() |> ignore
                subscribers.Remove(writer) |> ignore
                return! loop statuses
            | UpdateState (serverId, newState, timestamp) ->
                let current = 
                    statuses
                    |> Map.tryFind serverId
                    |> Option.defaultValue ServerStatus.Empty
                let updated =
                    if newState <> current.State.Current && timestamp > current.LastUpdate then
                        let newStatus = { Id = serverId; State = {| Current = newState; Prev = current.State.Current |}; LastUpdate = timestamp }
                        let map = statuses |> Map.add serverId newStatus 
                        // Broadcast and prune closed channels
                        subscribers.RemoveAll(fun w -> not (w.TryWrite(newStatus))) |> ignore
                        map
                    else
                        statuses
                return! loop updated
            | Stop reply ->
                subscribers |> Seq.iter (fun writer -> writer.TryComplete() |> ignore)
                subscribers.Clear()
                reply.Reply(())
                return ()
            }
        loop Map.empty)

    let tryPost message =
        if Volatile.Read(&isDisposed) = 0 then
            try
                agent.Post(message)
            with
            | :? InvalidOperationException -> ()
    
    interface IStateTracker with
        member _.Initialize servers =
            throwIfDisposed()
            agent.Post (Set servers)
        
        member _.GetState serverId =
            throwIfDisposed()
            agent.PostAndReply(fun channel -> Get(serverId, channel)).State
            
        member _.GetAllStates () =
            throwIfDisposed()
            agent.PostAndReply GetAll
        
        member _.Notify serverId state timestamp =
            throwIfDisposed()
            agent.Post (UpdateState(serverId, state, timestamp))
            
        member _.GetStatusStream lastTimestamp =
            throwIfDisposed()

            let options = BoundedChannelOptions(totalServers + 5, FullMode = BoundedChannelFullMode.DropOldest)
            let channel = Channel.CreateBounded<ServerStatus>(options)
            agent.Post (Subscribe(lastTimestamp, channel.Writer))

            let mutable disposed = 0
            { new IStatusStream with
                member _.GetAsyncEnumerator(ct) =
                    channel.Reader.ReadAllAsync(ct).GetAsyncEnumerator(ct)

              interface IDisposable with
                member _.Dispose() =
                    if Interlocked.Exchange(&disposed, 1) = 0 then
                        channel.Writer.TryComplete() |> ignore
                        tryPost(Unsubscribe channel.Writer) }

    interface IDisposable with
        member _.Dispose() =
            if Interlocked.Exchange(&isDisposed, 1) = 0 then
                try
                    agent.PostAndReply Stop
                with
                | :? InvalidOperationException -> ()