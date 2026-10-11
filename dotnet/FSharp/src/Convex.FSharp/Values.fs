namespace Convex.FSharp

open System


/// Strongly typed document identifier for a given table.
/// Its table marker is retained for native validator reflection.
/// The codec projects it to Convex's opaque string at the transport boundary.
type Id<'Table> = Id of string

module Id =
    let inline value (Id s: Id<'Table>) : string = s
    let inline ofString<'Table> (s: string) : Id<'Table> = Id s

/// Explicit representation for a transaction commit timestamp.
/// During mutation writes before commit, this holds Placeholder (resolving to db.vars.commitTs).
/// After commit and in all read contexts, this holds the real committed int64 timestamp.
[<RequireQualifiedAccess; CustomEquality; CustomComparison>]
type CommitTimestamp =
    | Value of int64
    | Placeholder
    member this.ToInt64() : int64 =
        match this with
        | CommitTimestamp.Value ts -> ts
        | CommitTimestamp.Placeholder -> failwith "Commit timestamp is unresolved until mutation commits."
    member this.TryToInt64() : int64 option =
        match this with
        | CommitTimestamp.Value ts -> Some ts
        | CommitTimestamp.Placeholder -> None
    override this.Equals(obj: obj) =
        match obj with
        | :? CommitTimestamp as other ->
            match this, other with
            | CommitTimestamp.Value a, CommitTimestamp.Value b -> a = b
            | CommitTimestamp.Placeholder, CommitTimestamp.Placeholder -> true
            | _ -> false
        | _ -> false
    override this.GetHashCode() =
        match this with
        | CommitTimestamp.Value v -> v.GetHashCode()
        | CommitTimestamp.Placeholder -> -1
    interface IComparable with
        member this.CompareTo(obj: obj) =
            match obj with
            | :? CommitTimestamp as other ->
                match this, other with
                | CommitTimestamp.Value a, CommitTimestamp.Value b -> compare a b
                | CommitTimestamp.Placeholder, CommitTimestamp.Placeholder -> 0
                | CommitTimestamp.Placeholder, CommitTimestamp.Value _ -> 1
                | CommitTimestamp.Value _, CommitTimestamp.Placeholder -> -1
            | _ -> invalidArg "obj" "Cannot compare with non-CommitTimestamp"

/// Strongly typed database index descriptor for a table
type TableIndex<'Table, 'Doc> =
    {
        TableName: string
        IndexName: string
        Fields: string list
    }

/// Strongly typed search index descriptor for a table
type TableSearchIndex<'Table, 'Doc> =
    {
        TableName: string
        IndexName: string
        SearchField: string
        FilterFields: string list
    }

/// Strongly typed vector index descriptor for a table
type TableVectorIndex<'Table, 'Doc> =
    {
        TableName: string
        IndexName: string
        VectorField: string
        Dimensions: int
        FilterFields: string list
    }

/// Strongly typed table identity marker and metadata
type Table<'Table, 'Doc> =
    {
        Name: string
    }
    member this.Index(name: string, fields: string list) : TableIndex<'Table, 'Doc> =
        { TableName = this.Name; IndexName = name; Fields = fields }

    member this.SearchIndex(name: string, searchField: string, ?filterFields: string list) : TableSearchIndex<'Table, 'Doc> =
        { TableName = this.Name; IndexName = name; SearchField = searchField; FilterFields = defaultArg filterFields [] }

    member this.VectorIndex(name: string, vectorField: string, dimensions: int, ?filterFields: string list) : TableVectorIndex<'Table, 'Doc> =
        { TableName = this.Name; IndexName = name; VectorField = vectorField; Dimensions = dimensions; FilterFields = defaultArg filterFields [] }

type Table<'Doc> = Table<'Doc, 'Doc>
type TableIndex<'Doc> = TableIndex<'Doc, 'Doc>
type TableSearchIndex<'Doc> = TableSearchIndex<'Doc, 'Doc>
type TableVectorIndex<'Doc> = TableVectorIndex<'Doc, 'Doc>

/// Registry mapping F# types to exact Convex table names
module TableRegistry =
    type private Binding = Exact of string | InferredDocument of string | SharedDocument of Set<string>
    type private RegistryState = { mutable Entries: Map<string,Binding> }
    let private defaultState = { Entries=Map.empty }
    let private scope=System.Threading.AsyncLocal<RegistryState option>()
    let private state () = scope.Value |> Option.defaultValue defaultState
    /// Backend workers isolate registry identities per deployment invocation.
    /// Ordinary applications retain immutable process-local table bindings.
    let enterScope () =
        let previous=scope.Value
        scope.Value<-Some {Entries=Map.empty}
        { new IDisposable with member _.Dispose()=scope.Value<-previous }
    let private gate=obj()
    let private guarded action = lock gate action
    let private identity (t:Type) = if String.IsNullOrEmpty t.FullName then t.Name else t.FullName
    let private valid typeName tableName =
        if String.IsNullOrWhiteSpace typeName || String.IsNullOrWhiteSpace tableName then invalidArg "tableName" "An exact table and type identity are required"
    let registerName (typeName:string) (tableName:string) = guarded(fun () ->
        valid typeName tableName
        match (state()).Entries.TryFind typeName with
        | Some(Exact previous) when previous=tableName -> ()
        | Some(InferredDocument previous) when previous=tableName -> (state()).Entries<-(state()).Entries.Add(typeName,Exact tableName)
        | Some(Exact previous) | Some(InferredDocument previous) ->
            invalidOp (sprintf "Type '%s' already selects table '%s' and cannot be rebound to '%s'. Use a distinct table marker." typeName previous tableName)
        | Some(SharedDocument tables) ->
            invalidOp (sprintf "Type '%s' is shared by tables %s and cannot be narrowed by later registration. Use a distinct table marker for '%s'." typeName (String.concat ", " tables) tableName)
        | None -> (state()).Entries<-(state()).Entries.Add(typeName,Exact tableName))
    let register (t:Type) (tableName:string) = registerName (identity t) tableName
    /// A reusable document shape cannot implicitly select one of several owning tables.
    let registerDocument (t:Type) (tableName:string) = guarded(fun () ->
        let name=identity t
        valid name tableName
        let binding =
            match (state()).Entries.TryFind name with
            // Reusing an explicitly owned shape as another table's document does
            // not change the owning table of existing Id<'Table> references.
            | Some(Exact previous) -> Exact previous
            | Some(InferredDocument previous) when previous<>tableName -> SharedDocument(Set.ofList [previous;tableName])
            | Some(SharedDocument tables) -> SharedDocument(tables.Add tableName)
            | _ -> InferredDocument tableName
        (state()).Entries<-(state()).Entries.Add(name,binding))
    let tryGetName (t:Type) : string option = guarded(fun () ->
        match (state()).Entries.TryFind(identity t) with
        | Some(Exact name) | Some(InferredDocument name) -> Some name
        | Some(SharedDocument tables) ->
            invalidOp (sprintf "Document type '%s' is shared by tables %s. Use an explicit table marker in Id<'Table> and a Table<'Table,'Doc> handle." t.FullName (String.concat ", " tables))
        | None -> None)
    let getName (t:Type) : string =
        match tryGetName t with
        | Some name -> name
        | None -> failwithf "Table name is not defined for type '%s'. Register it using Table.define<%s>(\"tableName\")." t.Name t.Name

module Table =
    /// Defines and registers a strongly typed table for a document type
    let inline define<'Doc> (name: string) : Table<'Doc, 'Doc> =
        let t = typeof<'Doc>
        TableRegistry.register t name
        { Name = name }

    /// Defines and registers a table with an explicit marker type and document type
    let inline defineWithMarker<'Table, 'Doc> (name: string) : Table<'Table, 'Doc> =
        let tDoc = typeof<'Doc>
        let tTable = typeof<'Table>
        TableRegistry.register tTable name
        if tDoc<>tTable then TableRegistry.registerDocument tDoc name
        { Name = name }

    /// Explicitly registers a table name for a type
    let inline register<'Table> (name: string) : unit =
        TableRegistry.register typeof<'Table> name

    /// Retrieves the registered Convex table name for a table or document type
    let inline name<'Table> () : string =
        TableRegistry.getName typeof<'Table>



/// Explicit representation for fields that can be null in the Convex database.
[<RequireQualifiedAccess>]
type NullOr<'T> =
    | Null
    | Value of 'T

/// Explicit representation for fields that can be missing (undefined), null, or have a value.
[<RequireQualifiedAccess>]
type OptionalNull<'T> =
    | Missing
    | Null
    | Value of 'T

/// Constants and boundaries for Convex types
module ConvexConstants =
    let MinInt64: int64 = -9223372036854775808L
    let MaxInt64: int64 = 9223372036854775807L
    let MaxIdentifierLength: int = 64
    let MaxValueForErrorLength: int = 16384

/// Base64 encoding/decoding compatible with Convex wire serialization
module ConvexBase64 =
    let encodeBytes (bytes: byte[]) : string =
        Convert.ToBase64String(bytes)

    let decodeBytes (base64: string) : byte[] =
        Convert.FromBase64String(base64)

    let encodeInt64 (value: int64) : string =
        let raw = BitConverter.GetBytes(value)
        let bytes = if not BitConverter.IsLittleEndian then Array.rev raw else raw
        Convert.ToBase64String(bytes)

    let decodeInt64 (base64: string) : int64 =
        let bytes = Convert.FromBase64String(base64)
        if bytes.Length <> 8 then
            failwithf "Received %d bytes, expected 8 for $integer" bytes.Length
        let bytesToUse = if not BitConverter.IsLittleEndian then Array.rev bytes else bytes
        BitConverter.ToInt64(bytesToUse, 0)

    let isSpecialFloat (f: float) : bool =
        Double.IsNaN(f) || Double.IsInfinity(f) || (f = 0.0 && BitConverter.DoubleToInt64Bits(f) = BitConverter.DoubleToInt64Bits(-0.0))

    let encodeFloat64 (value: float) : string =
        let raw = BitConverter.GetBytes(value)
        let bytes = if not BitConverter.IsLittleEndian then Array.rev raw else raw
        Convert.ToBase64String(bytes)

    let decodeFloat64 (base64: string) : float =
        let bytes = Convert.FromBase64String(base64)
        if bytes.Length <> 8 then
            failwithf "Received %d bytes, expected 8 for $float" bytes.Length
        let bytesToUse = if not BitConverter.IsLittleEndian then Array.rev bytes else bytes
        BitConverter.ToDouble(bytesToUse, 0)

/// Validates Convex field names
module FieldValidation =
    let validateFieldName (name: string) : Result<unit, string> =
        if String.IsNullOrEmpty(name) then
            Error "Field name cannot be empty."
        elif name.Length > ConvexConstants.MaxIdentifierLength then
            Error (sprintf "Field name '%s' exceeds maximum length %d." name ConvexConstants.MaxIdentifierLength)
        elif name.StartsWith("$") then
            Error (sprintf "Field name '%s' starts with '$', which is reserved." name)
        elif name.StartsWith("_") && name <> "_id" && name <> "_creationTime" then
            Error (sprintf "Field name '%s' starts with '_', which is reserved for system fields." name)
        else
            let invalidChar =
                name
                |> Seq.tryFind (fun c -> int c < 32 || int c >= 127)
            match invalidChar with
            | Some c -> Error (sprintf "Field name '%s' contains invalid character '%c'. Field names must be non-control ASCII." name c)
            | None -> Ok ()

/// Untyped / Dynamic representation of any supported Convex value.
[<RequireQualifiedAccess>]
type ConvexValue =
    | Null
    | Int64 of int64
    | Float64 of float
    | Boolean of bool
    | String of string
    | Bytes of byte[]
    | Array of ConvexValue list
    | Object of Map<string, ConvexValue>
    | CommitTs

/// Utilities for Convex values (comparison, sizing) matching official convex/values APIs.
module ConvexValues =
    let rec private makeComparable (v: obj) : int * obj =
        if obj.ReferenceEquals(v, null) then (1, null)
        else
            match v with
            | :? ConvexValue as cv ->
                match cv with
                | ConvexValue.Null -> (1, null)
                | ConvexValue.Int64 i -> (2, box i)
                | ConvexValue.CommitTs -> (2, box System.Int64.MaxValue)
                | ConvexValue.Float64 f -> (3, box f)
                | ConvexValue.Boolean b -> (4, box b)
                | ConvexValue.String s -> (5, box s)
                | ConvexValue.Bytes b -> (6, box (b |> Array.map (fun byteVal -> (3, box (float byteVal)))))
                | ConvexValue.Array items -> (7, box (items |> List.map (fun it -> makeComparable (box it)) |> List.toArray))
                | ConvexValue.Object map ->
                    let sortedEntries =
                        map
                        |> Map.toArray
                        |> Array.sortBy fst
                        |> Array.map (fun (k, valItem) ->
                            let compK = makeComparable (box k)
                            let compV = makeComparable (box valItem)
                            (7, box [| compK; compV |])
                        )
                    (9, box sortedEntries)
            | :? CommitTimestamp as ct ->
                match ct with
                | CommitTimestamp.Value i -> (2, box i)
                | CommitTimestamp.Placeholder -> (2, box System.Int64.MaxValue)
            | :? int64 as i -> (2, box i)
            | :? float as f -> (3, box f)
            | :? int as i -> (3, box (float i))
            | :? bool as b -> (4, box b)
            | :? string as s -> (5, box s)
            | :? (byte[]) as b -> (6, box (b |> Array.map (fun byteVal -> (3, box (float byteVal)))))
            | :? (obj[]) as arr -> (7, box (arr |> Array.map makeComparable))
            | :? (ConvexValue list) as lst -> (7, box (lst |> List.map (fun it -> makeComparable (box it)) |> List.toArray))
            | :? System.Collections.IDictionary as dict ->
                let list = System.Collections.Generic.List<string * obj>()
                for k in dict.Keys do
                    list.Add(string k, dict.[k])
                let sorted =
                    list.ToArray()
                    |> Array.sortBy fst
                    |> Array.map (fun (k, valItem) ->
                        (7, box [| makeComparable (box k); makeComparable valItem |])
                    )
                (9, box sorted)
            | _ -> (9, v)

    // Match the pinned Convex SDK, including NaN payloads and signed zero.
    let private compareNumbers (a:float) (b:float) =
        let bitsA,bitsB=BitConverter.DoubleToInt64Bits a,BitConverter.DoubleToInt64Bits b
        let nanA,nanB=Double.IsNaN a,Double.IsNaN b
        if nanA || nanB then
            if nanA<>nanB then
                if nanA then (if bitsA<0L then -1 else 1)
                else (if bitsB<0L then 1 else -1)
            elif (bitsA<0L)<>(bitsB<0L) then (if bitsA<0L then -1 else 1)
            else Microsoft.FSharp.Core.Operators.compare bitsA bitsB
        elif a=b then
            if a=0. then Microsoft.FSharp.Core.Operators.compare bitsA bitsB else 0
        elif a<b then -1 else 1

    // Unicode scalar order is UTF-8 order. Preserve lone surrogate code points,
    // as JavaScript codePointAt does, rather than replacing them during encoding.
    let private compareStrings (a:string) (b:string) =
        let point (s:string) i =
            if Char.IsHighSurrogate s.[i] && i+1<s.Length && Char.IsLowSurrogate s.[i+1] then Char.ConvertToUtf32(s.[i],s.[i+1])
            else int s.[i]
        let mutable i=0
        let mutable result=0
        while result=0 && i<min a.Length b.Length do
            let left,right=point a i,point b i
            result<-Microsoft.FSharp.Core.Operators.compare left right
            i<-i+(if left>0xffff then 2 else 1)
        if result<>0 then result else Microsoft.FSharp.Core.Operators.compare a.Length b.Length

    let rec private compareSameType (rank: int) (c1: obj) (c2: obj) : int =
        match rank with
        | 1 -> 0
        | 2 -> Microsoft.FSharp.Core.Operators.compare (c1 :?> int64) (c2 :?> int64)
        | 3 -> compareNumbers (c1 :?> float) (c2 :?> float)
        | 4 -> Microsoft.FSharp.Core.Operators.compare (c1 :?> bool) (c2 :?> bool)
        | 5 -> compareStrings (c1 :?> string) (c2 :?> string)
        | 6 | 7 | 9 -> compareArrays (c1 :?> (int * obj)[]) (c2 :?> (int * obj)[])
        | _ -> 0

    and private compareArrays (arr1: (int * obj)[]) (arr2: (int * obj)[]) : int =
        let minLen = System.Math.Min(arr1.Length, arr2.Length)
        let mutable diff = 0
        let mutable i = 0
        while diff = 0 && i < minLen do
            let (r1, val1) = arr1.[i]
            let (r2, val2) = arr2.[i]
            if r1 <> r2 then diff <- Microsoft.FSharp.Core.Operators.compare r1 r2
            else diff <- compareSameType r1 val1 val2
            i <- i + 1
        if diff <> 0 then diff
        else Microsoft.FSharp.Core.Operators.compare arr1.Length arr2.Length

    let compare (v1: obj) (v2: obj) : int =
        let (rank1, c1) = makeComparable v1
        let (rank2, c2) = makeComparable v2
        if rank1 <> rank2 then
            Microsoft.FSharp.Core.Operators.compare rank1 rank2
        else
            compareSameType rank1 c1 c2

    /// Calculate the size in bytes of a Convex value matching the official Convex formula.
    let rec size (value: obj) : int =
        if isNull value then 1
        else
            match value with
            | :? ConvexValue as cv ->
                match cv with
                | ConvexValue.Null -> 1
                | ConvexValue.Boolean _ -> 1
                | ConvexValue.Int64 _ -> 9
                | ConvexValue.CommitTs -> 9
                | ConvexValue.Float64 _ -> 9
                | ConvexValue.String s -> 2 + System.Text.Encoding.UTF8.GetByteCount(s)
                | ConvexValue.Bytes b -> 2 + b.Length
                | ConvexValue.Array items -> 2 + (items |> List.sumBy (fun it -> size (box it)))
                | ConvexValue.Object map ->
                    let mutable total = 2
                    for (k, v) in Map.toList map do
                        total <- total + System.Text.Encoding.UTF8.GetByteCount(k) + 1 + size (box v)
                    total
            | :? CommitTimestamp -> 9
            | :? bool -> 1
            | :? int64 -> 9
            | :? int32 -> 9
            | :? float -> 9
            | :? string as s -> 2 + System.Text.Encoding.UTF8.GetByteCount(s)
            | :? (byte[]) as b -> 2 + b.Length
            | :? (obj[]) as arr -> 2 + (arr |> Array.sumBy size)
            | :? System.Collections.IDictionary as dict ->
                let mutable total = 2
                for k in dict.Keys do
                    let keyStr = string k
                    let valObj = dict.[k]
                    total <- total + System.Text.Encoding.UTF8.GetByteCount(keyStr) + 1 + size valObj
                total
            | _ -> 9

    /// Calculate the size in bytes of a document including Convex system fields (_id and _creationTime).
    let documentSize (document: obj) : int =
        let baseSize = size document
        let hasId, hasCreationTime =
            match document with
            | :? ConvexValue as cv ->
                match cv with
                | ConvexValue.Object map -> map.ContainsKey("_id"), map.ContainsKey("_creationTime")
                | _ -> false, false
            | :? System.Collections.IDictionary as dict ->
                dict.Contains("_id"), dict.Contains("_creationTime")
            | _ -> false, false
        let extraId = if hasId then 0 else 38
        let extraCreationTime = if hasCreationTime then 0 else 23
        baseSize + extraId + extraCreationTime
