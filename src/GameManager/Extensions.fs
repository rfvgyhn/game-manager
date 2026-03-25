[<AutoOpen>]
module Extensions

open System
open Giraffe
open Microsoft.AspNetCore.Http

module String =
    let split (separator: char) (input: string) = input.Split(separator)
    let toLowerInvariant (input: string) = input.ToLowerInvariant()

module JsonNode =
    open System.Text.Json.Nodes
    let asStr (prop: JsonNode) = prop |> Option.ofObj |> Option.map _.ToString()
    let getValue (node: JsonNode) (key: string) = if isNull node then null else node[key]

module DateTimeOffset =
    let tryParse (input: string) =
        match DateTimeOffset.TryParse input with
        | true, dt -> Some dt
        | _ -> None

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