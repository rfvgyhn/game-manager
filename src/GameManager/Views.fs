module Views

open System
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
    form [ _class ""; _action action; _method "POST" ] [
        button [ _type "submit"; _class "button is-success is-small rounded"; _title "Start Container" ] [
            i [ _class "fa fa-play" ] []
        ]
    ]
let private tag c =
    let (cssClass, title) =
        match c.State with
        | Stopped -> ("is-warning", "")
        | Running -> ("is-success", "")
        | Disabled -> ("", "")
        | Unknown -> ("is-info", "")
        | Error m -> ("is-danger", m)
    span [_class "status"] [
        span [_class (sprintf "tag %s" cssClass); _title title] [
            encodedText <| c.State.ToString()
        ]
        if c.State = Stopped then startButton c.Name
    ]
    

let card c =
    let image =
        if String.IsNullOrEmpty(c.DisplayImage) then
            "placeholder.png"
        else
            sprintf "cards/%s" c.DisplayImage
    
    li [_class (sprintf "card %A" c.State)] [
        div [_class "card-image" ] [
            figure [_class "image is-4by3"] [
                img [_src image; ]
            ]
        ]
        div [_class "card-content"] [
            tag c
            div [_class "media"] [
                div [_class "media-content"] [
                    p [_class "title is-4"] [ encodedText c.DisplayName]
                ]
            ]
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
        ul [_class ""] 
            (containers |> List.map card)
]
