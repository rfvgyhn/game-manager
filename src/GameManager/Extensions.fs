[<AutoOpen>]
module Extensions

module String =
    let split (separator: char) (input: string) = input.Split(separator)
    let toLowerInvariant (input: string) = input.ToLowerInvariant()