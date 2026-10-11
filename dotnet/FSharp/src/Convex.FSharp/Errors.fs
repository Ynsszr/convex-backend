namespace Convex.FSharp

open System

/// Structured application errors cross runtimes through the canonical value
/// codec, independent of the generic CLR exception's concrete data type.
type IConvexApplicationError =
    abstract EncodedData: obj

/// Custom exception representing a ConvexError with application data conforming to official Convex protocol
type ConvexError<'T>(data: 'T, ?encodedData: obj, ?message: string) =
    inherit Exception(defaultArg message (
        match box data with
        | :? string as s -> s
        | _ -> sprintf "%A" data
    ))
    member this.Data: 'T = data
    interface IConvexApplicationError with
        member _.EncodedData =
            match encodedData with
            | Some value -> value
            | None -> Codec.encodeValue typeof<'T> (box data)

    interface Convex.Runtime.IApplicationError with
        member this.EncodedData =
            (this :> IConvexApplicationError).EncodedData
            |> Codec.decode<ConvexValue> |> ConvexJson.toJson |> Convex.Runtime.RuntimeJson.parse

module ConvexError =
    let inline create<'T> (data: 'T) =
        let encoded = Codec.encode<'T> data
        ConvexError<'T>(data, encoded)

module Errors =
    let inline failWithConvexError<'T, 'R> (data: 'T) : 'R =
        raise (ConvexError.create<'T>(data))

    let inline convexError<'T, 'R> (data: 'T) : 'R =
        raise (ConvexError.create<'T>(data))

[<AutoOpen>]
module ErrorPatterns =
    let inline (|ConvexError|_|) (ex: exn) : 'T option =
        match box ex with
        | :? IConvexApplicationError as error -> Some (Codec.decode<'T> error.EncodedData)
        | _ -> None
