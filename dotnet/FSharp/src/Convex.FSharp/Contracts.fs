namespace Convex.FSharp

open System

/// Local structural validation complements the owning backend validator. ID table
/// ownership is always checked by Convex; a string is not proof of table identity.
module ValidatorCheck =
    let rec matches (validator:Validator) (value:obj) =
        let kind=NativeValue.jsTypeOf value
        let missing=NativeValue.isUndefined value
        let number () =
            Convert.ToDouble(value,Globalization.CultureInfo.InvariantCulture)
        if missing then false else
        match validator with
        | Validator.Any -> true
        | Validator.Null -> NativeValue.isNull value
        | Validator.Boolean -> kind="boolean"
        | Validator.Number -> kind="number"
        | Validator.BigInt -> kind="bigint"
        | Validator.CommitTs ->
            kind="bigint" ||
            (match value with :? CommitTimestamp as timestamp -> timestamp=CommitTimestamp.Placeholder | _ -> false)
        | Validator.String -> kind="string"
        | Validator.Bytes ->
            value :? byte[]
        | Validator.Id _ -> kind="string" && not(String.IsNullOrEmpty(unbox<string> value))
        | Validator.LiteralString expected -> kind="string" && unbox<string> value=expected
        | Validator.LiteralFloat expected -> kind="number" && ConvexValues.compare (box (number())) (box expected)=0
        | Validator.LiteralBoolean expected -> kind="boolean" && unbox<bool> value=expected
        | Validator.LiteralInt64 expected -> kind="bigint" && unbox<int64> value=expected
        | Validator.Array item ->
            NativeValue.isArray value && (unbox<obj[]> value |> Array.forall (matches item))
        | Validator.Union members -> members |> List.exists (fun item -> matches item value)
        | Validator.Object fields ->
            NativeValue.isPlainObject value &&
            (NativeValue.objectKeys value |> Array.forall (fun key -> fields.ContainsKey key)) &&
            (fields |> Map.forall (fun key field ->
                let item=NativeValue.getProperty value key
                if NativeValue.isUndefined item then field.Optional
                else matches field.FieldType item))
        | Validator.Record (keys,values) ->
            let rec validKey key = function
                | RecordKeyValidator.String -> true
                | RecordKeyValidator.Id _ -> not(String.IsNullOrEmpty key)
                | RecordKeyValidator.Union members -> List.exists (validKey key) members
            NativeValue.isPlainObject value &&
            (NativeValue.objectKeys value |> Array.forall (fun key -> validKey key keys && matches values (NativeValue.getProperty value key)))

    let require validator value =
        if not(matches validator value) then invalidArg "value" "Value does not satisfy its explicit Convex boundary validator"

    let rec isArgumentEnvelope = function
        | Validator.Object _ -> true
        | Validator.Union members -> not members.IsEmpty && List.forall isArgumentEnvelope members
        | _ -> false

/// Code generation projects canonical codecs or an exact authored contract
/// owner. It never reconstructs an arbitrary mapping from its wire validator.
[<RequireQualifiedAccess>]
type ContractEncoding = CanonicalValue | CanonicalArguments | Refined | Nullable | Array | Authored

type IBoundaryContract =
    abstract ValueType: System.Type
    abstract Validator: Validator
    abstract Encoding: ContractEncoding
    abstract Element: IBoundaryContract option

/// One checked representation owns both directions at every native boundary.
type BoundaryContract<'T> internal
    (validator: Validator, encode: 'T -> obj, decode: obj -> 'T,
     encoding: ContractEncoding, element: IBoundaryContract option) =
    member _.Validator=validator
    member _.Encode(value:'T) =
        let encoded=encode value |> RawValueCodec.normalize
        ValidatorCheck.require validator encoded
        encoded
    member _.Decode(raw:obj) =
        let snapshot=RawValueCodec.normalize raw
        ValidatorCheck.require validator snapshot
        decode snapshot
    member internal this.Refine(value) =
        BoundaryContract(value,this.Encode,this.Decode,ContractEncoding.Refined,Some(this :> IBoundaryContract))
    interface IBoundaryContract with
        member _.ValueType=typeof<'T>
        member _.Validator=validator
        member _.Encoding=encoding
        member _.Element=element

[<RequireQualifiedAccess>]
module Contract =
    /// Custom codecs must live in an exported handler-free contract owner before
    /// a native client catalogue can project them.
    let create validator encode decode =
        BoundaryContract(validator,encode,decode,ContractEncoding.Authored,None)
    let valueWithValidator<'T> validator : BoundaryContract<'T> =
        let encode value =
            let raw=Codec.encode<'T> value
            let t=typeof<'T>
            if NativeValue.isUndefined raw && t.IsGenericType &&
               (t.GetGenericTypeDefinition()=typedefof<Option<_>> || t.GetGenericTypeDefinition()=typedefof<ValueOption<_>>) then null
            else raw
        BoundaryContract(validator,encode,Codec.decode<'T>,ContractEncoding.CanonicalValue,None)
    let argumentsWithValidator<'T> validator : BoundaryContract<'T> =
        if not(ValidatorCheck.isArgumentEnvelope validator) then
            invalidArg "validator" "Convex arguments require an object envelope"
        BoundaryContract(validator,Codec.encodeArguments<'T>,Codec.decodeArguments<'T>,ContractEncoding.CanonicalArguments,None)
    let derived<'T>() : BoundaryContract<'T> = valueWithValidator<'T>(TypeDerivation.derive<'T>())
    let arguments<'T>() : BoundaryContract<'T> =
        if typeof<'T> = typeof<unit> then argumentsWithValidator<'T>(V.object [])
        else argumentsWithValidator<'T>(TypeDerivation.derive<'T>())
    let refine validator (source: BoundaryContract<'T>) = source.Refine validator
    let nullable (source: BoundaryContract<'T>) : BoundaryContract<'T option> =
        BoundaryContract(V.nullable source.Validator,
            (fun value -> value |> Option.map source.Encode |> Option.defaultValue null),
            (fun raw -> if isNull raw || NativeValue.isUndefined raw then None else Some(source.Decode raw)),
            ContractEncoding.Nullable,Some(source :> IBoundaryContract))
    let array (source: BoundaryContract<'T>) : BoundaryContract<'T array> =
        BoundaryContract(V.array source.Validator,
            (fun values -> values |> Array.map source.Encode |> box),
            (fun raw -> Codec.decode<obj array> raw |> Array.map source.Decode),
            ContractEncoding.Array,Some(source :> IBoundaryContract))
    /// Map a validated wire representation into a different F# domain type.
    let map (toWire:'Domain -> 'Wire) (fromWire:'Wire -> 'Domain) (wire:BoundaryContract<'Wire>) : BoundaryContract<'Domain> =
        create wire.Validator (toWire >> wire.Encode) (wire.Decode >> fromWire)
