module Views

open Types
open Giraffe
open GiraffeViewEngine

let layout (content: XmlNode list) =
    html [] [
        head [] [
            meta [_charset "utf-8"]
            meta [_name "viewport"; _content "width=device-width, initial-scale=1" ]
            title [] [encodedText "Game Manager"]
            link [_rel "stylesheet"; _href "https://maxcdn.bootstrapcdn.com/font-awesome/4.7.0/css/font-awesome.min.css" ]
            link [_rel "stylesheet"; _href "https://cdnjs.cloudflare.com/ajax/libs/bulma/0.8.0/css/bulma.min.css" ]
            link [ _rel  "stylesheet"; _type "text/css"; _href "/main.css" ]
        ]
        body [_class "container"] [
            section [_class "section"] [
                yield! content                
            ]            
            script [ _src "/main.js" ] []
        ]
    ]

let private startButton name =
    let action = sprintf "/containers/%s/start" name
    form [ _class "media-left"; _action action; _method "POST" ] [
        button [ _type "submit"; _class "button is-success is-48x48"; _title "Start Container" ] [
            i [ _class "fa fa-play" ] []
        ]
    ]
let private stateToCssClass = function
    | Stopped -> "is-warning"
    | Running -> "is-success"
    | Disabled -> ""
    | Unknown -> "is-info"
    | Error _ -> "is-danger"

let card c =
    let description desc =
        div [_class "content"] [ encodedText desc ]
    let image = sprintf "cards/%s" c.DisplayImage
    div [_class "card column is-3"] [
        div [_class "card-image" ] [
            figure [_class "image is-4by3"] [
                img [_src image; _placeholder "" ]
            ]
        ]
        div [_class "card-content"] [
            div [_class "media"] [
                if c.State = Stopped then startButton c.Name
                div [_class "media-content"] [
                    p [_class "title is-4"] [ encodedText c.DisplayName]
                    p [_class "subtitle title is-6"] [
                        span [_class (sprintf "tag %s" <| stateToCssClass c.State)] [
                            encodedText <| c.State.ToString()
                        ]
                    ]
                ]
            ]
            match c.State with
            | Running | Stopped -> ()
            | Disabled -> description "Container is disabled"
            | Unknown -> description "Unable to find container"
            | Error e -> description <| sprintf "Error %s" e
        ]
    ]
    
let private noContainersFound =
    let sample = """
"Containers": [{
    "DisplayName": "Container - Name",
    "DisplayImage": "image/path/relative/to/cards/dir.png",
    "Name": "container_name",
    "Enabled": true
}]"""
    article [ _class "message" ] [
        div [ _class "message-header" ] [
            p [] [ encodedText "No Containers Found" ]
        ]
        div [ _class "message-body" ] [
            p [] [ encodedText "Add some containers to your configuration." ]
            pre [] [ encodedText sample ]
        ]
    ]
    
let index containers = layout <| [
    match containers with
    | [] -> noContainersFound
    | _ ->
        div [_class "columns"] 
            (containers |> List.map card)
]
