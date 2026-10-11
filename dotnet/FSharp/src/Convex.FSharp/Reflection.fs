namespace Convex.FSharp

open System
open Microsoft.FSharp.Reflection

/// Private wire records/unions are portable representations too. Reflection
/// must use the same visibility for admission, validation and construction.
module PortableReflection =
    let isRecord (t:System.Type) =
        FSharpType.IsRecord(t,System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.NonPublic)
    let recordFields (t:System.Type) =
        FSharpType.GetRecordFields(t,System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.NonPublic)
    let isUnion (t:System.Type) =
        FSharpType.IsUnion(t,System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.NonPublic)
    let unionCases (t:System.Type) =
        FSharpType.GetUnionCases(t,System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.NonPublic)
    let unionFields (value:obj,t:System.Type) =
        FSharpValue.GetUnionFields(value,t,System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.NonPublic)
    let makeRecord (t:System.Type,fields:obj array) =
        FSharpValue.MakeRecord(t,fields,System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.NonPublic)
    let makeUnion (case:UnionCaseInfo,fields:obj array) =
        FSharpValue.MakeUnion(case,fields,System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.NonPublic)

module BoundaryTypes =
    let isPresenceWrapper (t: Type) =
        t.IsGenericType &&
        let g = t.GetGenericTypeDefinition()
        g = typedefof<Option<_>> || g = typedefof<ValueOption<_>> ||
        g = typedefof<NullOr<_>> || g = typedefof<OptionalNull<_>>

    let numberOf (value: obj) =
        Convert.ToDouble value

    let enumValues (t: Type) =
        Enum.GetValues(t) |> Seq.cast<obj> |> Seq.map numberOf |> Seq.distinct |> Seq.sort |> Seq.toArray

    let validateShape (t: Type) =
        if t.IsEnum then
            let storage = Enum.GetUnderlyingType t
            if not ([typeof<sbyte>;typeof<byte>;typeof<int16>;typeof<uint16>;typeof<int>;typeof<uint32>] |> List.contains storage) then
                failwithf "Enum '%s' must use 8/16/32-bit integral storage for a portable Convex number codec. Use an explicit int64 representation or domain union for wider enum storage types." t.FullName
        if t.IsArray && t.GetArrayRank() <> 1 then
            failwithf "Array type '%s' is not one-dimensional. Use a list of lists or a record with dimensions and a flat array." t.FullName
        if isPresenceWrapper t && isPresenceWrapper (t.GetGenericArguments().[0]) then
            failwithf "Nested presence type '%s' cannot preserve distinct null/missing states in Convex. Use a tagged domain union or a single OptionalNull<'T> wrapper." t.FullName
        if isPresenceWrapper t then
            let inner = t.GetGenericArguments().[0]
            if inner = typeof<unit> || inner = typeof<ConvexValue> then
                failwithf "Presence type '%s' can contain null as both a wrapper case and a payload. Use a tagged domain union to retain the distinction." t.FullName
        if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Map<_, _>> then
            let key = t.GetGenericArguments().[0]
            if key <> typeof<string> && not (key.IsGenericType && key.GetGenericTypeDefinition() = typedefof<Id<_>>) then
                failwithf "Map key type '%s' is not supported by Convex. Use string or Id<'Table> keys." key.FullName
        let elementTypes =
            if t.IsArray then [|t.GetElementType()|]
            elif t.IsGenericType && (t.GetGenericTypeDefinition() = typedefof<list<_>> || t.GetGenericTypeDefinition() = typedefof<Collections.Generic.List<_>>) then t.GetGenericArguments()
            elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Map<_, _>> then [|t.GetGenericArguments().[1]|]
            elif FSharpType.IsTuple t then FSharpType.GetTupleElements t
            else [||]
        if elementTypes |> Array.exists (fun e -> e.IsGenericType && e.GetGenericTypeDefinition() = typedefof<OptionalNull<_>>) then
            failwithf "Type '%s' places OptionalNull in a collection position where Missing cannot roundtrip. Use option/NullOr elements, record fields, or a tagged domain union." t.FullName
        if t.IsGenericType && (t.GetGenericTypeDefinition() = typedefof<Collections.Generic.Dictionary<_, _>> || t.GetGenericTypeDefinition() = typedefof<seq<_>>) then
            failwithf "Type '%s' has no portable bidirectional Convex codec. Use an immutable Map<string, 'T> or a list/array, or an explicit ConvexValue boundary." t.FullName

    /// Check the complete declared shape, including absent optional payloads and
    /// unselected union cases. Recursive metadata is visited once; validator
    /// derivation still rejects recursive schemas separately.
    let validatePortableShape (root: Type) =
        let check path action =
            try action () with ex -> failwithf "Unsupported Convex shape at %s: %s" path ex.Message
        let rec walk visited path (t: Type) =
            let key = if String.IsNullOrEmpty t.FullName then t.Name else t.FullName
            check path (fun () -> validateShape t)
            if visited |> Set.contains key then ()
            else
                let next = visited |> Set.add key
                let primitive = [typeof<string>;typeof<bool>;typeof<int64>;typeof<float>;typeof<int>;typeof<int16>;typeof<uint16>;typeof<uint32>;typeof<byte>;typeof<sbyte>;typeof<unit>;typeof<byte[]>;typeof<obj>;typeof<ConvexValue>;typeof<CommitTimestamp>]
                let generic = if t.IsGenericType then Some(t.GetGenericTypeDefinition()) else None
                if List.contains t primitive || t.IsEnum || generic = Some typedefof<Id<_>> then ()
                elif isPresenceWrapper t then walk next (path+"?") (t.GetGenericArguments().[0])
                elif t.IsArray then walk next (path+"[]") (t.GetElementType())
                elif generic = Some typedefof<list<_>> || generic = Some typedefof<Collections.Generic.List<_>> then
                    walk next (path+"[]") (t.GetGenericArguments().[0])
                elif generic = Some typedefof<Map<_,_>> then walk next (path+"{value}") (t.GetGenericArguments().[1])
                elif FSharpType.IsTuple t then
                    FSharpType.GetTupleElements t |> Array.iteri (fun i elem -> walk next (sprintf "%s[%d]" path i) elem)
                elif PortableReflection.isRecord t then
                    for field in PortableReflection.recordFields t do
                        check (path+"."+field.Name) (fun () -> match FieldValidation.validateFieldName field.Name with Ok () -> () | Error err -> failwith ("Invalid record field name: "+err))
                        walk next (path+"."+field.Name) field.PropertyType
                elif PortableReflection.isUnion t then
                    for case in PortableReflection.unionCases t do
                        for field in case.GetFields() do
                            let fieldPath = path+"."+case.Name+"."+field.Name
                            check fieldPath (fun () ->
                                if field.Name = "kind" then failwith "Union field 'kind' collides with the case discriminator."
                                match FieldValidation.validateFieldName field.Name with Ok () -> () | Error err -> failwith err)
                            walk next fieldPath field.PropertyType
                else check path (fun () -> failwithf "Type '%s' cannot be automatically derived as a Convex validator and has no portable Convex codec. Use a supported domain type or an explicitly checked obj/ConvexValue boundary." t.FullName)
        walk Set.empty "$type" root

module TypeDerivation =

    let rec private deriveValidatorInner (visited: Set<string>) (t: Type) : Validator =
        BoundaryTypes.validateShape t
        let typeKey = if String.IsNullOrEmpty t.FullName then t.Name else t.FullName
        if visited.Contains typeKey then
            failwithf "Recursive type '%s' cannot derive a finite Convex validator without silently weakening validation. Use an explicit ConvexValue boundary with owning-domain validation, or flatten recursive data into referenced records." t.FullName
        elif t = typeof<string> then
            Validator.String
        elif t = typeof<int64> then
            Validator.BigInt
        elif t = typeof<CommitTimestamp> then
            Validator.CommitTs
        elif t = typeof<float> || t = typeof<double> || t = typeof<int32> || t = typeof<int>
             || t = typeof<int16> || t = typeof<uint16> || t = typeof<uint32> || t = typeof<byte> || t = typeof<sbyte> then
            Validator.Number
        elif t = typeof<bool> then
            Validator.Boolean
        elif t = typeof<byte[]> then
            Validator.Bytes
        elif t = typeof<ConvexValue> || t = typeof<obj> then
            Validator.Any
        elif t = typeof<unit> then
            Validator.Null
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Id<_>> then
            let tableArg = t.GetGenericArguments().[0]
            let tableName = TableRegistry.getName tableArg
            Validator.Id tableName
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<NullOr<_>> then
            let inner = t.GetGenericArguments().[0]
            V.nullable (deriveValidatorInner visited inner)
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<OptionalNull<_>> then
            let inner = t.GetGenericArguments().[0]
            V.nullable (deriveValidatorInner visited inner)
        elif t.IsGenericType && (t.GetGenericTypeDefinition() = typedefof<Option<_>> || t.GetGenericTypeDefinition() = typedefof<ValueOption<_>>) then
            let inner = t.GetGenericArguments().[0]
            V.nullable (deriveValidatorInner visited inner)
        elif t.IsArray then
            let elemType = t.GetElementType()
            Validator.Array (deriveValidatorInner visited elemType)
        elif t.IsGenericType && (t.GetGenericTypeDefinition() = typedefof<list<_>> || t.GetGenericTypeDefinition() = typedefof<Collections.Generic.List<_>> || t.GetGenericTypeDefinition() = typedefof<seq<_>>) then
            let elemType = t.GetGenericArguments().[0]
            Validator.Array (deriveValidatorInner visited elemType)
        elif t.IsGenericType && (t.GetGenericTypeDefinition() = typedefof<Map<_, _>> || t.GetGenericTypeDefinition() = typedefof<Collections.Generic.Dictionary<_, _>>) then
            let keyType = t.GetGenericArguments().[0]
            let valType = t.GetGenericArguments().[1]
            let keyValidator =
                if keyType = typeof<string> then
                    RecordKeyValidator.String
                elif keyType.IsGenericType && keyType.GetGenericTypeDefinition() = typedefof<Id<_>> then
                    let tableArg = keyType.GetGenericArguments().[0]
                    RecordKeyValidator.Id (TableRegistry.getName tableArg)
                else
                    failwithf "Map/Dictionary key type '%s' is not supported by Convex. Keys must be string or Id<'Table>." keyType.Name
            Validator.Record (keyValidator, deriveValidatorInner visited valType)
        elif FSharpType.IsTuple(t) then
            let elemTypes = FSharpType.GetTupleElements(t)
            if elemTypes.Length = 0 then
                Validator.Null
            else
                let memberValidators = elemTypes |> Array.map (deriveValidatorInner visited) |> Array.distinct |> Array.toList
                match memberValidators with
                | [ single ] -> Validator.Array single
                | multiple -> Validator.Array (Validator.Union multiple)
        elif PortableReflection.isRecord(t) then
            let nextVisited = visited.Add typeKey
            let fields = PortableReflection.recordFields(t)
            let fieldMap =
                fields
                |> Array.map (fun prop ->
                    match FieldValidation.validateFieldName prop.Name with
                    | Ok () -> ()
                    | Error err -> failwithf "Invalid record field name in type '%s': %s" t.Name err

                    let propType = prop.PropertyType
                    BoundaryTypes.validateShape propType
                    let isOpt =
                        propType.IsGenericType &&
                        (propType.GetGenericTypeDefinition() = typedefof<Option<_>> ||
                         propType.GetGenericTypeDefinition() = typedefof<ValueOption<_>> ||
                         propType.GetGenericTypeDefinition() = typedefof<OptionalNull<_>>)
                    let v =
                        if propType.IsGenericType && (propType.GetGenericTypeDefinition() = typedefof<Option<_>> || propType.GetGenericTypeDefinition() = typedefof<ValueOption<_>>) then
                            deriveValidatorInner nextVisited (propType.GetGenericArguments().[0])
                        elif propType.IsGenericType && propType.GetGenericTypeDefinition() = typedefof<OptionalNull<_>> then
                            V.nullable (deriveValidatorInner nextVisited (propType.GetGenericArguments().[0]))
                        else
                            deriveValidatorInner nextVisited propType
                    prop.Name, { FieldType = v; Optional = isOpt }
                )
                |> Map.ofArray
            Validator.Object fieldMap
        elif PortableReflection.isUnion(t) && t <> typeof<string> then
            let nextVisited = visited.Add typeKey
            let cases = PortableReflection.unionCases(t)
            if cases.Length = 0 then
                failwithf "Discriminated union '%s' has no cases." t.Name
            let caseValidators =
                cases
                |> Array.map (fun caseInfo ->
                    let fields = caseInfo.GetFields()
                    let caseFields =
                        fields
                        |> Array.map (fun f ->
                            match FieldValidation.validateFieldName f.Name with
                            | Error err -> failwithf "Invalid union field in '%s': %s" t.Name err
                            | Ok () -> ()
                            if f.Name = "kind" then
                                failwithf "Union field 'kind' in '%s' collides with the case discriminator." t.Name
                            let ft = f.PropertyType
                            BoundaryTypes.validateShape ft
                            let generic = if ft.IsGenericType then Some (ft.GetGenericTypeDefinition()) else None
                            let isOption = generic = Some typedefof<Option<_>> || generic = Some typedefof<ValueOption<_>>
                            let isOptionalNull = generic = Some typedefof<OptionalNull<_>>
                            let validator =
                                if isOption then deriveValidatorInner nextVisited (ft.GetGenericArguments().[0])
                                else deriveValidatorInner nextVisited ft
                            f.Name, { FieldType = validator; Optional = isOption || isOptionalNull }
                        )
                        |> Map.ofArray
                    let withKind = caseFields.Add("kind", { FieldType = Validator.LiteralString caseInfo.Name; Optional = false })
                    Validator.Object withKind
                )
                |> Array.toList
            if caseValidators.Length = 1 then
                caseValidators.Head
            else
                Validator.Union caseValidators
        elif t.IsEnum then
            match BoundaryTypes.enumValues t |> Array.map Validator.LiteralFloat |> Array.toList with
            | [single] -> single
            | [] -> failwithf "Enum '%s' has no declared values." t.FullName
            | values -> Validator.Union values
        else
            failwithf "Type '%s' cannot be automatically derived as a Convex validator. Convex only supports string, int64, float/number, bool, bytes, Id<'Table>, records, discriminated unions, arrays/lists, maps, and option/NullOr/OptionalNull. To store '%s', use a supported representation (e.g. ISO-8601 string, int64 epoch milliseconds, or float) or use ConvexValue for explicit untyped data." t.FullName t.FullName

    let deriveValidator (t: Type) : Validator =
        BoundaryTypes.validatePortableShape t
        deriveValidatorInner Set.empty t

    let inline derive<'T> () : Validator =
        deriveValidator typeof<'T>

module Codec =
    open NativeValue
    open ObjectCodec

    let private integerBounds (t:Type) =
        if t=typeof<int32> || t=typeof<int> then Some(-2147483648.,2147483647.)
        elif t=typeof<int16> then Some(-32768.,32767.)
        elif t=typeof<uint16> then Some(0.,65535.)
        elif t=typeof<uint32> then Some(0.,4294967295.)
        elif t=typeof<byte> then Some(0.,255.)
        elif t=typeof<sbyte> then Some(-128.,127.)
        else None
    let private exactInteger (t:Type) (value:obj) =
        if jsTypeOf value<>"number" then failwithf "Expected a Convex number for integer type '%s'." t.Name
        let number=Convert.ToDouble value
        match integerBounds t with
        | Some(minimum,maximum) when Double.IsNaN number || Double.IsInfinity number || Math.Truncate number<>number || number<minimum || number>maximum ->
            failwithf "Convex number is not an exact in-range integer for '%s'." t.Name
        | _ -> number

    let rec private encodeValueInner (ancestors: obj list) depth (t: Type) (v: obj) : obj =
        BoundaryTypes.validateShape t
        // Count wire containers, not erased presence wrappers or scalar DUs.
        // This guard runs before recursive encoding, so malformed cyclic typed
        // values and deep trees fail without exhausting the call stack.
        let container =
            if isNull v || isUndefined v then false
            elif t = typeof<ConvexValue> then
                match unbox<ConvexValue> v with ConvexValue.Array _ | ConvexValue.Object _ -> true | _ -> false
            elif t = typeof<obj> || t = typeof<byte[]> || t = typeof<CommitTimestamp> || BoundaryTypes.isPresenceWrapper t then false
            elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Id<_>> then false
            else t.IsArray || FSharpType.IsTuple t || PortableReflection.isRecord t || PortableReflection.isUnion t ||
                 (t.IsGenericType && (t.GetGenericTypeDefinition() = typedefof<list<_>> || t.GetGenericTypeDefinition() = typedefof<Collections.Generic.List<_>> || t.GetGenericTypeDefinition() = typedefof<Map<_,_>>))
        let ancestors, depth =
            if container then
                if ancestors |> List.exists (fun parent -> obj.ReferenceEquals(parent,v)) then
                    failwithf "Cyclic typed value is not a Convex value at $typed (%s)." t.Name
                if depth >= 64 then failwithf "Convex value exceeds 64 container levels at $typed (%s)." t.Name
                v :: ancestors, depth + 1
            else ancestors, depth
        if t = typeof<obj> then RawValueCodec.normalize v
        elif t = typeof<ConvexValue> then
            match unbox<ConvexValue> v with
            | ConvexValue.Null -> null
            | ConvexValue.String x -> box x
            | ConvexValue.Boolean x -> box x
            | ConvexValue.Int64 x -> box x
            | ConvexValue.Float64 x -> box x
            | ConvexValue.Bytes x -> encodeValueInner ancestors depth typeof<byte[]> (box x)
            | ConvexValue.Array xs ->
                xs |> List.mapi (fun i x ->
                    if i >= 8192 then failwith "Convex array exceeds 8192 entries at $typed."
                    encodeValueInner ancestors depth t (box x)) |> List.toArray |> box
            | ConvexValue.Object fields ->
                let target = emptyPlainObject ()
                if fields.Count > 1024 then failwith "Convex object exceeds 1024 retained fields at $typed."
                for KeyValue(key, value) in fields do
                    RawValueCodec.validateFieldName key
                    setProperty target key (encodeValueInner ancestors depth t (box value))
                target
            | ConvexValue.CommitTs -> encodeValueInner ancestors depth typeof<CommitTimestamp> (box CommitTimestamp.Placeholder)
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Option<_>> then
            let innerType = t.GetGenericArguments().[0]
            let caseInfo, fields = PortableReflection.unionFields(v, t)
            if caseInfo.Name = "None" then
                undefinedValue() // absent, distinct from explicit null
            else
                if isNull fields.[0] then failwith "A present option payload cannot be null. Use None or an explicit NullOr/OptionalNull case."
                encodeValueInner ancestors depth innerType fields.[0]
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<ValueOption<_>> then
            let innerType = t.GetGenericArguments().[0]
            let caseInfo, fields = PortableReflection.unionFields(v, t)
            if caseInfo.Name = "ValueNone" then
                undefinedValue()
            else
                if isNull fields.[0] then failwith "A present option payload cannot be null. Use None or an explicit NullOr/OptionalNull case."
                encodeValueInner ancestors depth innerType fields.[0]
        elif t = typeof<unit> then
            null // Fable erases unit to undefined; its declared wire value is null.
        elif isNull v then
            null
        elif isUndefined v then
            undefinedValue()
        elif t = typeof<string> || t = typeof<bool> then
            v
        elif t = typeof<byte[]> then
            v
        elif t = typeof<int64> then
            v
        elif t = typeof<CommitTimestamp> then
            match (v :?> CommitTimestamp) with
            | CommitTimestamp.Value ts ->
                box ts
            | CommitTimestamp.Placeholder ->
                box CommitTimestamp.Placeholder
        elif integerBounds t |> Option.isSome then
            exactInteger t v |> ignore
            v
        elif t = typeof<float> || t = typeof<double> then
            v
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Id<_>> then
            let _, fields = PortableReflection.unionFields(v, t)
            fields.[0]
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<NullOr<_>> then
            let caseInfo, fields = PortableReflection.unionFields(v, t)
            if caseInfo.Name = "Null" then
                null
            else
                let innerType = t.GetGenericArguments().[0]
                if isNull fields.[0] then failwith "A present option payload cannot be null. Use None or an explicit NullOr/OptionalNull case."
                encodeValueInner ancestors depth innerType fields.[0]
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<OptionalNull<_>> then
            let caseInfo, fields = PortableReflection.unionFields(v, t)
            if caseInfo.Name = "Missing" then
                undefinedValue() // absent
            elif caseInfo.Name = "Null" then
                null
            else
                let innerType = t.GetGenericArguments().[0]
                if isNull fields.[0] then failwith "A present option payload cannot be null. Use None or an explicit NullOr/OptionalNull case."
                encodeValueInner ancestors depth innerType fields.[0]
        elif t.IsArray then
            let elemType = t.GetElementType()
            let items = [|
                for item in (v :?> System.Collections.IEnumerable) ->
                    let enc = encodeValueInner ancestors depth elemType item
                    if isUndefined enc then null else enc
            |]
            box items
        elif t.IsGenericType && (t.GetGenericTypeDefinition() = typedefof<list<_>> || t.GetGenericTypeDefinition() = typedefof<Collections.Generic.List<_>>) then
            let elemType = t.GetGenericArguments().[0]
            let mutable count = 0
            let items = [|
                for item in (v :?> System.Collections.IEnumerable) do
                    count <- count + 1
                    if count > 8192 then failwith "Convex array exceeds 8192 entries at $typed."
                    let enc = encodeValueInner ancestors depth elemType item
                    yield (if isUndefined enc then null else enc)
            |]
            box items
        elif FSharpType.IsTuple(t) then
            let elemTypes = FSharpType.GetTupleElements(t)
            let fields = FSharpValue.GetTupleFields(v)
            let items = [|
                for i in 0 .. fields.Length - 1 ->
                    let enc = encodeValueInner ancestors depth elemTypes.[i] fields.[i]
                    if isUndefined enc then null else enc
            |]
            box items
        elif t.IsGenericType && (t.GetGenericTypeDefinition() = typedefof<Map<_, _>> || t.GetGenericTypeDefinition() = typedefof<Collections.Generic.Dictionary<_, _>>) then
            let keyType = t.GetGenericArguments().[0]
            let valType = t.GetGenericArguments().[1]
            let target = emptyPlainObject ()
            let enum = (v :?> System.Collections.IEnumerable)
            let mutable count = 0
            for entry in enum do
                count <- count + 1
                if count > 1024 then failwith "Convex object exceeds 1024 retained fields at $typed."
                let k = unbox<string> (encodeValueInner ancestors depth keyType (entry.GetType().GetProperty("Key").GetValue(entry)))
                let valObj = entry.GetType().GetProperty("Value").GetValue(entry)
                RawValueCodec.validateFieldName k
                let encoded = encodeValueInner ancestors depth valType valObj
                setProperty target k (if isUndefined encoded then null else encoded)
            target
        elif PortableReflection.isRecord(t) then
            let target = emptyPlainObject ()
            let fields = PortableReflection.recordFields(t)
            for field in fields do
                let fType = field.PropertyType
                // Anonymous records can omit undefined properties and use a
                // different key order from reflection metadata under Fable.
                let fVal = FSharpValue.GetRecordField(v, field)
                let encoded = encodeValueInner ancestors depth fType fVal
                if not (isUndefined encoded) then
                    setProperty target field.Name encoded
            target
        elif PortableReflection.isUnion(t) then
            let target = emptyPlainObject ()
            let caseInfo, fields = PortableReflection.unionFields(v, t)
            setProperty target "kind" (box caseInfo.Name)
            let caseFields = caseInfo.GetFields()
            for i in 0 .. caseFields.Length - 1 do
                let f = caseFields.[i]
                let fVal = fields.[i]
                let encoded = encodeValueInner ancestors depth f.PropertyType fVal
                if not (isUndefined encoded) then
                    setProperty target f.Name encoded
            target
        elif t.IsEnum then
            let number = BoundaryTypes.numberOf v
            if not (BoundaryTypes.enumValues t |> Array.contains number) then
                failwithf "Undeclared enum value %A for '%s'. Only explicitly declared cases are supported." number t.Name
            box number
        else
            failwithf "Type '%s' has no portable Convex codec. Use a supported domain type or an explicitly checked obj/ConvexValue boundary." t.FullName

    let private validateIntegral (t: Type) (jsVal: obj) =
        let bounds =
            if t = typeof<int> || t = typeof<int32> then Some (-2147483648., 2147483647.)
            elif t = typeof<int16> then Some (-32768., 32767.)
            elif t = typeof<uint16> then Some (0., 65535.)
            elif t = typeof<uint32> then Some (0., 4294967295.)
            elif t = typeof<byte> then Some (0., 255.)
            elif t = typeof<sbyte> then Some (-128., 127.)
            else None
        match bounds with
        | None -> ()
        | Some (minimum, maximum) ->
            if jsTypeOf jsVal <> "number" then
                failwithf "Expected a Convex number for integral type '%s'; null, missing, strings and bigint are not accepted." t.Name
            let number = Convert.ToDouble jsVal
            if Double.IsNaN number || Double.IsInfinity number || Math.Truncate number <> number || number < minimum || number > maximum then
                failwithf "Convex number %A is not an exact '%s' value in [%A, %A]. Use float for fractional values or int64 for wider integers." number t.Name minimum maximum

    let rec private decodeValueInner (t: Type) (jsVal: obj) : obj =
        BoundaryTypes.validateShape t
        validateIntegral t jsVal
        let permitsMissing = t.IsGenericType &&
                             let generic=t.GetGenericTypeDefinition()
                             generic=typedefof<Option<_>> || generic=typedefof<ValueOption<_>> || generic=typedefof<OptionalNull<_>>
        if isUndefined jsVal && not permitsMissing then
            failwithf "Required type '%s' cannot decode missing input." t.Name
        if (isNull jsVal || isUndefined jsVal) && t <> typeof<unit> && t <> typeof<obj> && t <> typeof<ConvexValue> && not (BoundaryTypes.isPresenceWrapper t) then
            failwithf "Required type '%s' cannot decode null or missing input. Use an explicit presence wrapper." t.Name
        // Convex arrays have one element validator, so enforce tuple positions here
        // (and keep direct codec calls from coercing malformed primitive values).
        let expectedType =
            if t = typeof<string> || (t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Id<_>>) then Some "string"
            elif t = typeof<bool> then Some "boolean"
            elif t = typeof<float> || t = typeof<double> || t.IsEnum then Some "number"
            else None
        match expectedType with
        | Some expected when jsTypeOf jsVal <> expected -> failwithf "Expected Convex %s for '%s'." expected t.Name
        | _ -> ()
        if t.IsEnum then
            let number = BoundaryTypes.numberOf jsVal
            if not (BoundaryTypes.enumValues t |> Array.contains number) then
                failwithf "Undeclared enum value %A for '%s'. Only explicitly declared cases are supported." number t.Name
        let isObjectShape = (PortableReflection.isRecord t || (PortableReflection.isUnion t && not (BoundaryTypes.isPresenceWrapper t) && t <> typeof<ConvexValue> && t <> typeof<CommitTimestamp> && not (t.IsGenericType && (t.GetGenericTypeDefinition() = typedefof<Id<_>> || t.GetGenericTypeDefinition() = typedefof<list<_>>)))) || (t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Map<_, _>>)
        if isObjectShape && not (isPlainObject jsVal) then failwithf "Expected a plain Convex object for '%s'." t.Name
        let isCollection = t <> typeof<byte[]> && (t.IsArray || FSharpType.IsTuple t || (t.IsGenericType && (t.GetGenericTypeDefinition() = typedefof<list<_>> || t.GetGenericTypeDefinition() = typedefof<Collections.Generic.List<_>>)))
        if isCollection && not (isArray jsVal) then failwithf "Expected a Convex array for '%s'." t.Name
        if t = typeof<byte[]> then
            if not (jsVal :? byte[]) then failwith "Expected Convex bytes."
        if t = typeof<unit> && not (isNull jsVal) then failwith "Expected Convex null for unit. Empty function arguments use the explicit argument adapter."
        if t = typeof<obj> then jsVal
        elif t = typeof<ConvexValue> then
            let value =
                if isNull jsVal then ConvexValue.Null
                elif isUndefined jsVal then failwith "Undefined is not a Convex value."
                elif jsTypeOf jsVal = "string" then ConvexValue.String (unbox<string> jsVal)
                elif jsTypeOf jsVal = "boolean" then ConvexValue.Boolean (unbox<bool> jsVal)
                elif jsTypeOf jsVal = "bigint" then ConvexValue.Int64 (unbox<int64> jsVal)
                elif jsTypeOf jsVal = "number" then
                    ConvexValue.Float64 (Convert.ToDouble jsVal)
                elif jsVal :? byte[] then ConvexValue.Bytes (unbox<byte[]> jsVal)
                elif jsVal :? CommitTimestamp then ConvexValue.CommitTs
                elif isArray jsVal then
                    ConvexValue.Array [ for item in (jsVal :?> System.Collections.IEnumerable) -> unbox<ConvexValue> (decodeValueInner t item) ]
                else
                    objectKeys jsVal
                    |> Array.map (fun key -> key, unbox<ConvexValue> (decodeValueInner t (getProperty jsVal key)))
                    |> Map.ofArray
                    |> ConvexValue.Object
            box value
        elif t = typeof<unit> then
            box ()
        elif isUndefined jsVal then
            if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Option<_>> then
                let noneCase = PortableReflection.unionCases(t) |> Array.find (fun c -> c.Name = "None")
                PortableReflection.makeUnion(noneCase, [||])
            elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<ValueOption<_>> then
                let noneCase = PortableReflection.unionCases(t) |> Array.find (fun c -> c.Name = "ValueNone")
                PortableReflection.makeUnion(noneCase, [||])
            elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<OptionalNull<_>> then
                let missingCase = PortableReflection.unionCases(t) |> Array.find (fun c -> c.Name = "Missing")
                PortableReflection.makeUnion(missingCase, [||])
            else
                null
        elif isNull jsVal then
            if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<NullOr<_>> then
                let nullCase = PortableReflection.unionCases(t) |> Array.find (fun c -> c.Name = "Null")
                PortableReflection.makeUnion(nullCase, [||])
            elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<OptionalNull<_>> then
                let nullCase = PortableReflection.unionCases(t) |> Array.find (fun c -> c.Name = "Null")
                PortableReflection.makeUnion(nullCase, [||])
            elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Option<_>> then
                let noneCase = PortableReflection.unionCases(t) |> Array.find (fun c -> c.Name = "None")
                PortableReflection.makeUnion(noneCase, [||])
            elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<ValueOption<_>> then
                let noneCase = PortableReflection.unionCases(t) |> Array.find (fun c -> c.Name = "ValueNone")
                PortableReflection.makeUnion(noneCase, [||])
            else
                null
        elif t = typeof<string> then
            box (string jsVal)
        elif t = typeof<bool> then
            box (unbox<bool> jsVal)
        elif t = typeof<byte[]> then
            if jsVal :? byte[] then jsVal
            else box [||]
        elif t = typeof<int64> then
            if jsTypeOf jsVal = "number" then
                let number = Convert.ToDouble jsVal
                if Double.IsNaN number || Double.IsInfinity number || Math.Truncate number <> number || abs number > 9007199254740991. then
                    failwith "Converting a Convex number to int64 requires an exact safe integer. Pass a Convex bigint for full-width int64 values."
            elif jsTypeOf jsVal <> "bigint" then
                failwith "Expected a Convex bigint or an exact safe integer for int64; strings are not accepted."
            box (Convert.ToInt64(jsVal))
        elif t = typeof<CommitTimestamp> then
            if jsVal :? CommitTimestamp && unbox<CommitTimestamp> jsVal=CommitTimestamp.Placeholder then
                box CommitTimestamp.Placeholder
            elif jsTypeOf jsVal="bigint" then box (CommitTimestamp.Value(unbox<int64> jsVal))
            elif jsTypeOf jsVal="number" then
                let value=Convert.ToDouble jsVal
                if Double.IsNaN value || Double.IsInfinity value || Math.Truncate value<>value || abs value>9007199254740991. then
                    failwith "Commit timestamps require an exact safe number or an int64"
                box (CommitTimestamp.Value(int64 value))
            else failwith "Expected an actual commit placeholder or an exact integer timestamp"
        elif t = typeof<int32> || t = typeof<int> then
            exactInteger t jsVal |> ignore
            box (Convert.ToInt32(jsVal))
        elif t = typeof<int16> then
            exactInteger t jsVal |> ignore
            box (Convert.ToInt16(jsVal))
        elif t = typeof<uint16> then
            exactInteger t jsVal |> ignore
            box (Convert.ToUInt16(jsVal))
        elif t = typeof<uint32> then
            exactInteger t jsVal |> ignore
            box (Convert.ToUInt32(jsVal))
        elif t = typeof<byte> then
            exactInteger t jsVal |> ignore
            box (Convert.ToByte(jsVal))
        elif t = typeof<sbyte> then
            exactInteger t jsVal |> ignore
            box (Convert.ToSByte(jsVal))
        elif t = typeof<float> || t = typeof<double> then
            box (Convert.ToDouble(jsVal))
        elif t.IsArray then
            let elemType = t.GetElementType()
            let rawArr = jsVal :?> System.Collections.IEnumerable
            let rawList = [ for x in rawArr -> decodeValueInner elemType x ]
            let arr = System.Array.CreateInstance(elemType, rawList.Length)
            for i in 0 .. rawList.Length - 1 do
                arr.SetValue(rawList.[i], i)
            box arr
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<_>> then
            let elemType = t.GetGenericArguments().[0]
            let rawArr = jsVal :?> System.Collections.IEnumerable
            let rawList = [ for x in rawArr -> decodeValueInner elemType x ]
            let listType = typedefof<list<_>>.MakeGenericType([| elemType |])
            let empty = listType.GetProperty("Empty").GetValue(null)
            let cons = listType.GetMethod("Cons")
            let mutable acc = empty
            for i = rawList.Length - 1 downto 0 do
                acc <- cons.Invoke(null, [| rawList.[i]; acc |])
            acc
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Collections.Generic.List<_>> then
            let elemType = t.GetGenericArguments().[0]
            let listInstance = Activator.CreateInstance(t) :?> System.Collections.IList
            for x in (jsVal :?> System.Collections.IEnumerable) do
                listInstance.Add(decodeValueInner elemType x) |> ignore
            box listInstance
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Id<_>> then
            if t.IsInstanceOfType jsVal then jsVal
            else PortableReflection.makeUnion(PortableReflection.unionCases(t).[0], [| box (string jsVal) |])
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Option<_>> then
            let innerType = t.GetGenericArguments().[0]
            if isNull jsVal then
                let noneCase = PortableReflection.unionCases(t) |> Array.find (fun c -> c.Name = "None")
                PortableReflection.makeUnion(noneCase, [||])
            else
                let decodedInner = decodeValueInner innerType jsVal
                let someCase = PortableReflection.unionCases(t) |> Array.find (fun c -> c.Name = "Some")
                PortableReflection.makeUnion(someCase, [| decodedInner |])
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<ValueOption<_>> then
            let innerType = t.GetGenericArguments().[0]
            if isNull jsVal then
                let noneCase = PortableReflection.unionCases(t) |> Array.find (fun c -> c.Name = "ValueNone")
                PortableReflection.makeUnion(noneCase, [||])
            else
                let decodedInner = decodeValueInner innerType jsVal
                let someCase = PortableReflection.unionCases(t) |> Array.find (fun c -> c.Name = "ValueSome")
                PortableReflection.makeUnion(someCase, [| decodedInner |])
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<NullOr<_>> then
            let innerType = t.GetGenericArguments().[0]
            let decodedInner = decodeValueInner innerType jsVal
            let valueCase = PortableReflection.unionCases(t) |> Array.find (fun c -> c.Name = "Value")
            PortableReflection.makeUnion(valueCase, [| decodedInner |])
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<OptionalNull<_>> then
            let innerType = t.GetGenericArguments().[0]
            let decodedInner = decodeValueInner innerType jsVal
            let valueCase = PortableReflection.unionCases(t) |> Array.find (fun c -> c.Name = "Value")
            PortableReflection.makeUnion(valueCase, [| decodedInner |])
        elif FSharpType.IsTuple(t) then
            let elemTypes = FSharpType.GetTupleElements(t)
            let rawArr =
                jsVal :?> obj[]
            if rawArr.Length <> elemTypes.Length then
                failwithf "Tuple '%s' requires exactly %d elements; received %d." t.Name elemTypes.Length rawArr.Length
            let values = [|
                for i in 0 .. elemTypes.Length - 1 ->
                    if elemTypes.[i] = typeof<unit> && not (isNull rawArr.[i]) then failwith "Expected Convex null for a unit tuple position."
                    decodeValueInner elemTypes.[i] rawArr.[i]
            |]
            FSharpValue.MakeTuple(values, t)
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Map<_, _>> then
            let keyType = t.GetGenericArguments().[0]
            let valType = t.GetGenericArguments().[1]
            let rawKeys = objectKeys jsVal
            rawKeys |> Array.iter RawValueCodec.validateFieldName
            let tupleType = FSharpType.MakeTupleType [|keyType; valType|]
            let entries = System.Array.CreateInstance(tupleType, rawKeys.Length)
            for i in 0 .. rawKeys.Length - 1 do
                let key = rawKeys.[i]
                entries.SetValue(FSharpValue.MakeTuple([|decodeValueInner keyType (box key); decodeValueInner valType (getProperty jsVal key)|], tupleType), i)
            Activator.CreateInstance(t, [|box entries|])
        elif PortableReflection.isRecord(t) then
            let fields = PortableReflection.recordFields(t)
            let names = fields |> Array.map (fun f -> f.Name) |> Set.ofArray
            for key in objectKeys jsVal do
                if not (names.Contains key) then failwithf "Unexpected field '%s' for record '%s'. Use ConvexValue or obj to retain unknown fields." key t.Name
            let values =
                fields
                |> Array.map (fun f ->
                    let fVal = if hasProperty jsVal f.Name then getProperty jsVal f.Name else undefinedValue()
                    if isNull fVal && f.PropertyType.IsGenericType && (f.PropertyType.GetGenericTypeDefinition() = typedefof<Option<_>> || f.PropertyType.GetGenericTypeDefinition() = typedefof<ValueOption<_>>) then
                        failwithf "Optional field '%s' of '%s' may be missing but cannot be null. Use NullOr/OptionalNull for explicit null." f.Name t.Name
                    decodeValueInner f.PropertyType fVal
                )
            PortableReflection.makeRecord(t, values)
        elif PortableReflection.isUnion(t) then
            let kindObj = if hasProperty jsVal "kind" then getProperty jsVal "kind" else undefinedValue()
            let kindStr = if jsTypeOf kindObj = "string" then unbox<string> kindObj else ""
            let cases = PortableReflection.unionCases(t)
            let matchingCase =
                cases
                |> Array.tryFind (fun c -> c.Name = kindStr)
                |> Option.defaultWith (fun () -> failwithf "Unknown or missing discriminator '%s' for union '%s'." kindStr t.Name)
            let caseFields = matchingCase.GetFields()
            let names = caseFields |> Array.map (fun f -> f.Name) |> Set.ofArray |> Set.add "kind"
            for key in objectKeys jsVal do
                if not (names.Contains key) then failwithf "Unexpected field '%s' for union case '%s'." key matchingCase.Name
            let values =
                caseFields
                |> Array.map (fun f ->
                    let fVal = if hasProperty jsVal f.Name then getProperty jsVal f.Name else undefinedValue()
                    if isNull fVal && f.PropertyType.IsGenericType && (f.PropertyType.GetGenericTypeDefinition() = typedefof<Option<_>> || f.PropertyType.GetGenericTypeDefinition() = typedefof<ValueOption<_>>) then
                        failwithf "Optional union field '%s' may be missing but cannot be null." f.Name
                    decodeValueInner f.PropertyType fVal
                )
            PortableReflection.makeUnion(matchingCase, values)
        elif t.IsEnum then
            Enum.ToObject(t, Convert.ChangeType(jsVal, Enum.GetUnderlyingType t, Globalization.CultureInfo.InvariantCulture))
        else
            failwithf "Type '%s' has no portable Convex codec. Use a supported domain type or an explicitly checked obj/ConvexValue boundary." t.FullName

    let encodeValue (t: Type) (v: obj) : obj =
        BoundaryTypes.validatePortableShape t
        let encoded = encodeValueInner [] 0 t v
        if not (isUndefined encoded) && t <> typeof<obj> then RawValueCodec.validate encoded
        encoded

    let decodeValue (t: Type) (raw: obj) : obj =
        BoundaryTypes.validatePortableShape t
        let snapshot = if isUndefined raw then raw else RawValueCodec.normalize raw
        decodeValueInner t snapshot

    let inline encode<'T> (v: 'T) : obj =
        encodeValue typeof<'T> (box v)

    let inline decode<'T> (jsVal: obj) : 'T =
        decodeValue typeof<'T> jsVal :?> 'T


    /// Convex functions accept an object argument; F# unit denotes the empty object.
    let inline encodeArguments<'Args> (args: 'Args) : obj =
        let encoded = if typeof<'Args> = typeof<unit> then ObjectCodec.emptyPlainObject() else encode<'Args> args
        if not (isPlainObject encoded) then failwith "Convex function arguments must encode to a plain object. Use a record/DU envelope, or unit for no arguments."
        encoded

    let inline decodeArguments<'Args> (raw: obj) : 'Args =
        if not (isPlainObject raw) then failwith "Convex function arguments must be a plain object."
        if typeof<'Args> = typeof<unit> then
            if (objectKeys raw).Length <> 0 then failwith "A unit function expects an empty argument object."
            unbox<'Args> (box ())
        else decode<'Args> raw

    /// Explicit database projection: omit only system fields not declared by the document.
    // Keep trusted document reflection in the SDK when application assemblies are optimized.
    [<NoCompilerInlining>]
    let decodeDocument<'Doc> (raw: obj) : 'Doc =
        if not (isPlainObject raw) then failwith "Expected a plain Convex database document."
        let t = typeof<'Doc>
        let names =
            if PortableReflection.isRecord t then
                Some (PortableReflection.recordFields t |> Array.map (fun f -> f.Name) |> Set.ofArray)
            elif PortableReflection.isUnion t && t <> typeof<ConvexValue> && not (BoundaryTypes.isPresenceWrapper t) then
                let tag = getProperty raw "kind"
                let case = PortableReflection.unionCases t |> Array.tryFind (fun c -> box c.Name = tag)
                Some (case |> Option.map (fun c -> c.GetFields() |> Array.map (fun f -> f.Name) |> Set.ofArray) |> Option.defaultValue Set.empty |> Set.add "kind")
            else None
        match names with
        | None -> decode<'Doc> raw
        | Some declared ->
            let copy = ObjectCodec.emptyPlainObject()
            for key in objectKeys raw do
                if declared.Contains key || (key <> "_id" && key <> "_creationTime") then setProperty copy key (getProperty raw key)
            decode<'Doc> copy

    /// Owning-protocol projection with explicit lower-camel aliases. Application
    /// records use exact field names; transport adapters intentionally project known fields.
    let rec projectTransportValue (t: Type) (raw: obj) : obj =
        if isNull raw || isUndefined raw then raw
        elif BoundaryTypes.isPresenceWrapper t then projectTransportValue (t.GetGenericArguments().[0]) raw
        elif PortableReflection.isRecord t then
            if not (isPlainObject raw) then failwithf "Expected a plain transport object for '%s'." t.Name
            let copy = ObjectCodec.emptyPlainObject()
            for field in PortableReflection.recordFields t do
                let camel = if field.Name.Length = 0 then field.Name else Char.ToLowerInvariant(field.Name.[0]).ToString() + field.Name.Substring(1)
                let value = if hasProperty raw field.Name then getProperty raw field.Name elif hasProperty raw camel then getProperty raw camel else undefinedValue()
                // Owning SDK metadata uses null for absent optional values. This
                // normalization is transport-only; application codecs stay exact.
                let optionalField = field.PropertyType.IsGenericType && (field.PropertyType.GetGenericTypeDefinition() = typedefof<Option<_>> || field.PropertyType.GetGenericTypeDefinition() = typedefof<ValueOption<_>>)
                if not (isUndefined value) && not (optionalField && isNull value) then
                    setProperty copy field.Name (projectTransportValue field.PropertyType value)
            copy
        else raw

    let inline decodeTransport<'T> (raw: obj) : 'T =
        decode<'T> (projectTransportValue typeof<'T> raw)
