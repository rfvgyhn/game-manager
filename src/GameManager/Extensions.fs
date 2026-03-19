[<AutoOpen>]
module Extensions

open Giraffe
open Microsoft.AspNetCore.Http

module String =
    let split (separator: char) (input: string) = input.Split(separator)
    let toLowerInvariant (input: string) = input.ToLowerInvariant()
    
module AsyncEnumerable =
    open System.Collections.Generic
    open System.Threading
    open System.Threading.Channels
    open System.Threading.Tasks
    open FSharp.Control
    
    let merge (ct: CancellationToken) (first: IAsyncEnumerable<'T>) (second: IAsyncEnumerable<'T>) : IAsyncEnumerable<'T> =
        let channel = Channel.CreateUnbounded<'T>()
        
        let produce (stream: IAsyncEnumerable<'T>) = task {
            use e = stream.GetAsyncEnumerator(ct)
            while! e.MoveNextAsync() do
                do! channel.Writer.WriteAsync(e.Current, ct)
        }

        task {
            try
                let! _ = Task.WhenAll(produce first, produce second)
                channel.Writer.Complete()
            with e ->
                channel.Writer.Complete(e)
        } |> ignore

        channel.Reader.ReadAllAsync(ct)
        
type HttpContext with
    member this.IsAjaxRequest() = this.TryGetRequestHeader "X-Requested-With" |> Option.contains "XMLHttpRequest"