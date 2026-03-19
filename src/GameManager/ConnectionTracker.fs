namespace GameManager

open System.Threading
open System.Threading.Tasks

type IConnectionTracker =
    abstract member Increment: unit -> unit
    abstract member Decrement: unit -> unit
    abstract member WaitForAnyConnection: CancellationToken -> Task
    abstract member Count: int

type private AsyncManualResetEvent(initialState: bool) =
    let mutable tcs =
        let tcs = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        if initialState then
            tcs.TrySetResult(()) |> ignore
        tcs

    member _.IsSet = tcs.Task.IsCompleted

    member _.WaitAsync(ct: CancellationToken) : Task =
        if tcs.Task.IsCompleted then
            Task.CompletedTask
        elif not ct.CanBeCanceled then
            tcs.Task
        else
            let current = tcs
            let registration = ct.Register(fun () -> current.TrySetCanceled(ct) |> ignore)

            current.Task.ContinueWith(
                (fun (_: Task<unit>) -> registration.Dispose()),
                TaskContinuationOptions.ExecuteSynchronously
            )

    member _.Set() = tcs.TrySetResult(()) |> ignore

    member _.Reset() =
        if tcs.Task.IsCompleted then
            Interlocked.Exchange(
                &tcs,
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            )
            |> ignore

type InMemoryConnectionTracker() =
    let mutable count = 0
    let signal = AsyncManualResetEvent(false)
    
    interface IConnectionTracker with
        member _.Count = Volatile.Read(&count)
            
        member _.WaitForAnyConnection ct =
            if Volatile.Read(&count) > 0 then
                Task.CompletedTask
            else
                signal.WaitAsync(ct)
                
        member _.Increment() =
            let newCount = Interlocked.Increment(&count)
            if newCount = 1 then
                signal.Set()
                
        member _.Decrement() =
            let newCount = Interlocked.Decrement(&count)
            if newCount = 0 then
                signal.Reset()
            