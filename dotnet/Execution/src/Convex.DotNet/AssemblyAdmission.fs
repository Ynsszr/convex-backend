namespace Convex.DotNet.Execution

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Reflection
open System.Reflection.Emit
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open Convex.DotNet

/// This policy admits reviewed first-party code; it is not an arbitrary IL sandbox.
[<RequireQualifiedAccess>]
type NativeAssemblyAdmissionProfile =
    | DeterministicFirstParty
    | ManagedActionFirstParty

[<RequireQualifiedAccess>]
type private AdmissionPhase = Initialization | Handler

[<RequireQualifiedAccess>]
type internal NativeTypeReference =
    | Named of assembly:string * name:string * arguments:NativeTypeReference list
    | Primitive of PrimitiveTypeCode
    | Vector of NativeTypeReference
    | Array of NativeTypeReference * rank:int
    | ByReference of NativeTypeReference
    | TypeParameter of int
    | MethodParameter of int

module internal NativeTypeReference =
    // A contextual recursion can grow its type while revisiting one method.
    // Bound the shape before hashing, display expansion or CLR construction.
    let validate references =
        let mutable nodes=0
        let mutable text=0
        let rec visit depth reference =
            if depth>128 then raise(HostOperationException("NativeAssemblyAdmissionRejected","Native generic type context exceeds its depth bound"))
            nodes<-nodes+1
            if nodes>100000 then raise(HostOperationException("NativeAssemblyAdmissionRejected","Native generic type context exceeds its graph bound"))
            match reference with
            | NativeTypeReference.Named(assembly,name,arguments) ->
                text<-text+assembly.Length+name.Length
                if text>100000 then raise(HostOperationException("NativeAssemblyAdmissionRejected","Native generic type context exceeds its text bound"))
                for argument in arguments do visit (depth+1) argument
            | NativeTypeReference.Vector element | NativeTypeReference.Array(element,_) | NativeTypeReference.ByReference element -> visit (depth+1) element
            | _ -> ()
        for reference in references do visit 0 reference

    let rec isClosed = function
        | NativeTypeReference.TypeParameter _ | NativeTypeReference.MethodParameter _ -> false
        | NativeTypeReference.Named(_,_,arguments) -> List.forall isClosed arguments
        | NativeTypeReference.Vector element | NativeTypeReference.Array(element,_) | NativeTypeReference.ByReference element -> isClosed element
        | _ -> true

    let substitute typeArguments methodArguments references =
        let rec bind depth = function
            | _ when depth>128 -> raise(HostOperationException("NativeAssemblyAdmissionRejected","Native generic type context exceeds its depth bound"))
            | NativeTypeReference.TypeParameter index as original -> List.tryItem index typeArguments |> Option.defaultValue original
            | NativeTypeReference.MethodParameter index as original -> List.tryItem index methodArguments |> Option.defaultValue original
            | NativeTypeReference.Named(assembly,name,arguments) -> NativeTypeReference.Named(assembly,name,List.map (bind (depth+1)) arguments)
            | NativeTypeReference.Vector element -> NativeTypeReference.Vector(bind (depth+1) element)
            | NativeTypeReference.Array(element,rank) -> NativeTypeReference.Array(bind (depth+1) element,rank)
            | NativeTypeReference.ByReference element -> NativeTypeReference.ByReference(bind (depth+1) element)
            | reference -> reference
        let result=List.map (bind 0) references
        validate result
        result

module internal NativeFrameworkAssemblies =
    // Only this core runtime and the host's selected FSharp.Core can describe
    // trusted types. Deployment identities never enter this map. FSharp.Core
    // metadata also owns generic function/continuation virtual slots.
    let private paths =
        let directory=Path.GetDirectoryName(typeof<obj>.Assembly.Location)
        let systemPaths=
            match AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") with
            | :? string as value ->
                value.Split(Path.PathSeparator)
                |> Array.choose(fun path ->
                    let name=Path.GetFileNameWithoutExtension(path)
                    if Path.GetDirectoryName(path)=directory && Path.GetExtension(path)=".dll" &&
                       (name="System" || name="mscorlib" || name="netstandard" || name.StartsWith("System.",StringComparison.Ordinal)) then
                        Some(name,path)
                    else None)
                |> Map.ofArray
            | _ -> Map.empty
        Map.add "FSharp.Core" typeof<unit>.Assembly.Location systemPaths

    let tryPath name=Map.tryFind name paths
    type private TypeIndex={Definitions:Map<string,int>;Forwarders:Map<string,string>}
    let private indexes=Collections.Concurrent.ConcurrentDictionary<string,TypeIndex>(StringComparer.Ordinal)

    let private readIndex name =
        match tryPath name with
        | None -> {Definitions=Map.empty;Forwarders=Map.empty}
        | Some path ->
            use input=File.OpenRead path
            use image=new PEReader(input)
            let reader=image.GetMetadataReader()
            if reader.GetString(reader.GetAssemblyDefinition().Name)<>name then
                invalidOp "The admitted runtime assembly identity differs from its filename"
            let rec definition (handle:TypeDefinitionHandle) depth =
                if depth>32 then invalidOp "Native runtime type nesting exceeds its bound"
                let value=reader.GetTypeDefinition handle
                let parent=value.GetDeclaringType()
                let part=reader.GetString value.Name
                if parent.IsNil then
                    let space=reader.GetString value.Namespace
                    (if space="" then "" else space+".")+part
                else definition parent (depth+1)+"+"+part
            let rec exported (handle:ExportedTypeHandle) depth =
                if depth>32 then invalidOp "Native runtime type forwarding exceeds its bound"
                let value=reader.GetExportedType handle
                let part=reader.GetString value.Name
                match value.Implementation.Kind with
                | HandleKind.AssemblyReference ->
                    let space=reader.GetString value.Namespace
                    let full=(if space="" then "" else space+".")+part
                    full,Some(reader.GetString(reader.GetAssemblyReference(AssemblyReferenceHandle.op_Explicit value.Implementation).Name))
                | HandleKind.ExportedType ->
                    let parent,target=exported (ExportedTypeHandle.op_Explicit value.Implementation) (depth+1)
                    parent+"+"+part,target
                | _ -> part,None
            {Definitions=reader.TypeDefinitions |> Seq.map(fun handle -> definition handle 0,MetadataTokens.GetToken(TypeDefinitionHandle.op_Implicit handle : EntityHandle)) |> Map.ofSeq
             Forwarders=
                reader.ExportedTypes
                |> Seq.choose(fun handle ->
                    let full,target=exported handle 0
                    target |> Option.map(fun target -> full,target))
                |> Map.ofSeq}

    let canonicalOwner assembly name =
        let rec forwarded (name:string) (aliases:Map<string,string>) =
            match Map.tryFind name aliases with
            | Some target -> Some target
            | None ->
                let parent=name.LastIndexOf('+')
                if parent<0 then None else forwarded (name.Substring(0,parent)) aliases
        let rec follow owner depth =
            if depth>32 then invalidOp "Native runtime type forwarding exceeds its bound"
            match tryPath owner with
            | None -> assembly
            | Some _ ->
                let index=indexes.GetOrAdd(owner,fun key -> readIndex key)
                if Map.containsKey name index.Definitions then owner
                else
                    match forwarded name index.Forwarders with
                    | None -> assembly
                    | Some target ->
                        if not(Map.containsKey target paths) then
                            invalidOp "A native runtime type forwarder leaves the admitted runtime"
                        follow target (depth+1)
        follow assembly 0

    let tryDefinition assembly name =
        let owner=canonicalOwner assembly name
        match tryPath owner with
        | Some path ->
            let index=indexes.GetOrAdd(owner,fun key -> readIndex key)
            Map.tryFind name index.Definitions |> Option.map(fun token -> owner,path,token)
        | None -> None

type internal NativeStaticField = {
    Assembly:string
    Token:int
    TypeArguments:NativeTypeReference list
}

type private ClrType = {
    Assembly:string
    Name:string
    Display:string
    Arguments:ClrType list
    Reference:NativeTypeReference
}

[<CustomEquality;NoComparison>]
type private GenericContext = {
    TypeOwner:(string*int) option
    TypeArguments:ClrType list
    MethodArguments:ClrType list
}
with
    override this.Equals(value) =
        match value with
        | :? GenericContext as other ->
            let same left right =
                List.length left=List.length right &&
                List.forall2 (fun (left:ClrType) (right:ClrType) -> left.Reference=right.Reference) left right
            this.TypeOwner=other.TypeOwner &&
            same this.TypeArguments other.TypeArguments && same this.MethodArguments other.MethodArguments
        | _ -> false
    override this.GetHashCode() =
        let mutable hash=HashCode()
        hash.Add this.TypeOwner
        hash.Add this.TypeArguments.Length
        for argument in this.TypeArguments do hash.Add argument.Reference
        hash.Add this.MethodArguments.Length
        for argument in this.MethodArguments do hash.Add argument.Reference
        hash.ToHashCode()

module private GenericContext =
    let empty={TypeOwner=None;TypeArguments=[];MethodArguments=[]}

module private ClrType =
    let private primitives=Map.ofList [
        "System.Void",PrimitiveTypeCode.Void;"System.Boolean",PrimitiveTypeCode.Boolean;"System.Char",PrimitiveTypeCode.Char
        "System.SByte",PrimitiveTypeCode.SByte;"System.Byte",PrimitiveTypeCode.Byte
        "System.Int16",PrimitiveTypeCode.Int16;"System.UInt16",PrimitiveTypeCode.UInt16
        "System.Int32",PrimitiveTypeCode.Int32;"System.UInt32",PrimitiveTypeCode.UInt32
        "System.Int64",PrimitiveTypeCode.Int64;"System.UInt64",PrimitiveTypeCode.UInt64
        "System.Single",PrimitiveTypeCode.Single;"System.Double",PrimitiveTypeCode.Double
        "System.String",PrimitiveTypeCode.String;"System.Object",PrimitiveTypeCode.Object
        "System.IntPtr",PrimitiveTypeCode.IntPtr;"System.UIntPtr",PrimitiveTypeCode.UIntPtr;"System.TypedReference",PrimitiveTypeCode.TypedReference
    ]
    let tryPrimitiveName code=primitives |> Map.toSeq |> Seq.tryPick(fun (name,value) -> if value=code then Some name else None)
    let named assembly name (arguments:ClrType list) =
        let owner=NativeFrameworkAssemblies.canonicalOwner assembly name
        let reference=
            match Map.tryFind name primitives with
            | Some code when arguments.IsEmpty && owner=typeof<obj>.Assembly.GetName().Name -> NativeTypeReference.Primitive code
            | _ -> NativeTypeReference.Named(owner,name,arguments |> List.map _.Reference)
        NativeTypeReference.validate [reference]
        {Assembly=assembly;Name=name;Display=name;Arguments=arguments;Reference=reference}
    let parameter methodParameter index =
        let name=(if methodParameter then "!!" else "!")+string index
        {Assembly="";Name=name;Display=name;Arguments=[]
         Reference=if methodParameter then NativeTypeReference.MethodParameter index else NativeTypeReference.TypeParameter index}
    let fromReference reference =
        NativeTypeReference.validate [reference]
        let rec value = function
            | NativeTypeReference.Named(assembly,name,arguments) -> named assembly name (List.map value arguments)
            | NativeTypeReference.TypeParameter index -> parameter false index
            | NativeTypeReference.MethodParameter index -> parameter true index
            | NativeTypeReference.Primitive code ->
                let name="primitive:"+string code
                {Assembly="";Name=name;Display=name;Arguments=[];Reference=NativeTypeReference.Primitive code}
            | NativeTypeReference.Vector element as reference -> let element=value element in {element with Display=element.Display+"[]";Reference=reference}
            | NativeTypeReference.Array(element,rank) as reference -> let element=value element in {element with Display=element.Display+"["+string rank+"]";Reference=reference}
            | NativeTypeReference.ByReference element as reference -> let element=value element in {element with Display=element.Display+"&";Reference=reference}
        value reference
    let reflected (kind:Type) =
        let mutable nodes=0
        let rec reflect depth (kind:Type) =
            if depth>128 then raise(HostOperationException("NativeAssemblyAdmissionRejected","Native generic type context exceeds its depth bound"))
            nodes<-nodes+1
            if nodes>100000 then raise(HostOperationException("NativeAssemblyAdmissionRejected","Native generic type context exceeds its graph bound"))
            if kind.IsGenericParameter then parameter (not(isNull kind.DeclaringMethod)) kind.GenericParameterPosition
            elif kind.IsArray then
                let element=reflect (depth+1) (kind.GetElementType())
                {element with Display=kind.ToString();Reference=if kind.IsSZArray then NativeTypeReference.Vector element.Reference else NativeTypeReference.Array(element.Reference,kind.GetArrayRank())}
            elif kind.IsByRef then
                let element=reflect (depth+1) (kind.GetElementType())
                {element with Display=kind.ToString();Reference=NativeTypeReference.ByReference element.Reference}
            else
                let definition=if kind.IsGenericType then kind.GetGenericTypeDefinition() else kind
                let arguments=if kind.IsGenericType then kind.GetGenericArguments() |> Array.map (reflect (depth+1)) |> Array.toList else []
                named (definition.Assembly.GetName().Name) definition.FullName arguments
        reflect 0 kind

module private MetadataNames =
    let rec definition (reader:MetadataReader) (handle:TypeDefinitionHandle) =
        let value=reader.GetTypeDefinition handle
        let declaring=value.GetDeclaringType()
        if declaring.IsNil then
            let space=reader.GetString value.Namespace
            (if space="" then "" else space+".")+reader.GetString value.Name
        else definition reader declaring+"+"+reader.GetString value.Name

    let rec reference (reader:MetadataReader) (handle:TypeReferenceHandle) =
        let value=reader.GetTypeReference handle
        if value.ResolutionScope.Kind=HandleKind.TypeReference then
            let parent=reference reader (TypeReferenceHandle.op_Explicit value.ResolutionScope)
            ClrType.named parent.Assembly (parent.Name+"+"+reader.GetString value.Name) []
        else
            let space=reader.GetString value.Namespace
            let name=(if space="" then "" else space+".")+reader.GetString value.Name
            let assembly=
                if value.ResolutionScope.Kind=HandleKind.AssemblyReference then
                    reader.GetString(reader.GetAssemblyReference(AssemblyReferenceHandle.op_Explicit value.ResolutionScope).Name)
                elif value.ResolutionScope.Kind=HandleKind.ModuleDefinition then reader.GetString(reader.GetAssemblyDefinition().Name)
                else invalidOp "Unresolvable native metadata type scope"
            ClrType.named assembly name []

type private SignatureTypes() =
    let shaped value display reference =
        NativeTypeReference.validate [reference]
        {value with Display=display;Reference=reference}
    interface ISignatureTypeProvider<ClrType,GenericContext> with
        member _.GetArrayType(value,shape)=shaped value (value.Display+"["+string shape.Rank+"]") (NativeTypeReference.Array(value.Reference,shape.Rank))
        member _.GetByReferenceType value=shaped value (value.Display+"&") (NativeTypeReference.ByReference value.Reference)
        member _.GetFunctionPointerType _=invalidOp "Function pointers are not admitted"
        member _.GetGenericInstantiation(value,arguments)=
            let bound=ClrType.named value.Assembly value.Name (List.ofSeq arguments)
            {bound with Display=value.Name+"<"+String.Join(",",arguments |> Seq.map _.Display)+">"}
        member _.GetGenericMethodParameter(context,index)=context.MethodArguments |> List.tryItem index |> Option.defaultWith(fun () -> ClrType.parameter true index)
        member _.GetGenericTypeParameter(context,index)=context.TypeArguments |> List.tryItem index |> Option.defaultWith(fun () -> ClrType.parameter false index)
        member _.GetModifiedType(_,value,_)=value
        member _.GetPinnedType value=value
        member _.GetPointerType _=invalidOp "Pointer signatures are not admitted"
        member _.GetPrimitiveType code=
            let name="primitive:"+string code
            {Assembly="";Name=name;Display=name;Arguments=[];Reference=NativeTypeReference.Primitive code}
        member _.GetSZArrayType value=shaped value (value.Display+"[]") (NativeTypeReference.Vector value.Reference)
        member _.GetTypeFromDefinition(reader,handle,_)=
            let name=MetadataNames.definition reader handle
            ClrType.named (reader.GetString(reader.GetAssemblyDefinition().Name)) name []
        member _.GetTypeFromReference(reader,handle,_)=MetadataNames.reference reader handle
        member this.GetTypeFromSpecification(reader,context,handle,_)=reader.GetTypeSpecification(handle).DecodeSignature(this,context)

type private FrozenImage(bytes:byte[]) =
    let input=new MemoryStream(bytes,false)
    let pe=new PEReader(input,PEStreamOptions.LeaveOpen)
    do if not pe.HasMetadata then invalidOp "Only managed native deployment dependencies are admitted"
    let reader=pe.GetMetadataReader()
    do if not reader.IsAssembly then invalidOp "A managed assembly identity is required"
    let name=reader.GetString(reader.GetAssemblyDefinition().Name)
    let types=Dictionary<string,TypeDefinitionHandle>(StringComparer.Ordinal)
    do for handle in reader.TypeDefinitions do types.Add(MetadataNames.definition reader handle,handle)
    member _.Name=name
    member _.Reader=reader
    member _.Pe=pe
    member _.Types=types
    interface IDisposable with member _.Dispose()=pe.Dispose();input.Dispose()

type private FrameworkSlot = {Name:string;ParameterCount:int;GenericParameterCount:int}
type private FrameworkType = {Parents:NativeTypeReference list;Slots:FrameworkSlot array;Variance:GenericParameterAttributes array}

module private FrameworkTypes =
    // Read only metadata from the selected CLR, lazily for each referenced
    // type. No deployment assembly is loaded and no framework method executes.
    let private descriptions=Collections.Concurrent.ConcurrentDictionary<string*string,FrameworkType>()
    let private read (assembly,name) =
        match NativeFrameworkAssemblies.tryDefinition assembly name with
        | None -> invalidOp "A framework callback type is outside the selected runtime"
        | Some(owner,path,token) ->
            use input=File.OpenRead path
            use image=new PEReader(input)
            let reader=image.GetMetadataReader()
            if reader.GetString(reader.GetAssemblyDefinition().Name)<>owner then invalidOp "Framework callback assembly identity changed"
            let definition=reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(token &&& 0x00ffffff))
            let signatures=SignatureTypes()
            let parent (handle:EntityHandle) =
                match handle.Kind with
                | HandleKind.TypeDefinition -> (signatures :> ISignatureTypeProvider<ClrType,GenericContext>).GetTypeFromDefinition(reader,TypeDefinitionHandle.op_Explicit handle,0uy)
                | HandleKind.TypeReference -> MetadataNames.reference reader (TypeReferenceHandle.op_Explicit handle)
                | HandleKind.TypeSpecification -> reader.GetTypeSpecification(TypeSpecificationHandle.op_Explicit handle).DecodeSignature(signatures,GenericContext.empty)
                | _ -> invalidOp "Unsupported framework callback parent"
            {Variance=definition.GetGenericParameters() |> Seq.map(fun handle -> reader.GetGenericParameter(handle).Attributes &&& GenericParameterAttributes.VarianceMask) |> Seq.toArray
             Parents=[
                if not definition.BaseType.IsNil then (parent definition.BaseType).Reference
                for implementation in definition.GetInterfaceImplementations() do
                    (parent (reader.GetInterfaceImplementation(implementation).Interface)).Reference
             ]
             Slots=definition.GetMethods() |> Seq.choose(fun handle ->
                let method=reader.GetMethodDefinition handle
                if (method.Attributes &&& MethodAttributes.Static)=enum 0 && (method.Attributes &&& MethodAttributes.Virtual)<>enum 0 then
                    let signature=method.DecodeSignature(signatures,GenericContext.empty)
                    Some {Name=reader.GetString method.Name;ParameterCount=signature.ParameterTypes.Length;GenericParameterCount=signature.GenericParameterCount}
                else None) |> Seq.toArray}

    let tryDescribe reference =
        let identity=
            match reference with
            | NativeTypeReference.Named(assembly,name,arguments) -> Some(assembly,name,arguments)
            | NativeTypeReference.Primitive code -> ClrType.tryPrimitiveName code |> Option.map(fun name -> typeof<obj>.Assembly.GetName().Name,name,[])
            | _ -> None
        identity |> Option.bind(fun (assembly,name,arguments) ->
            match NativeFrameworkAssemblies.tryDefinition assembly name with
            | None -> None
            | Some(owner,_,_) ->
                let description=descriptions.GetOrAdd((owner,name),fun key -> read key)
                Some {description with Parents=NativeTypeReference.substitute arguments [] description.Parents})

[<StructuralEquality;NoComparison>]
type private FrameworkDemand = {
    Phase:AdmissionPhase
    Receiver:NativeTypeReference
    Members:Set<string> option
    MethodArguments:NativeTypeReference list
}

module private FrameworkCallbacks =
    // These are callback capabilities, not permissions for arbitrary trusted
    // method bodies. The ordinary member admission rule is still applied first.
    // An unclassified admitted operation can invoke every framework slot on
    // any frozen value reachable through its declared inputs. Refusal is the
    // conservative outcome when an exact callback body cannot be closed.
    let enumeration=set ["GetEnumerator";"MoveNext";"get_Current";"Reset";"Dispose";"GetAsyncEnumerator";"MoveNextAsync";"DisposeAsync"]
    let equality=set ["Equals";"Compare";"CompareTo"]
    let formatting=set ["ToString";"TryFormat";"Format";"GetFormat"]
    let hashing=Set.add "GetHashCode" equality
    let private traversal=set [
        "Empty";"get_Empty";"Singleton";"OfArray";"OfList";"OfSeq";"ToArray";"ToList";"ToSeq";"Cache";"Cast";"OfType"
        "Append";"Prepend";"Concat";"Collect";"Map";"MapIndexed";"Map2";"Map3";"MapFold";"MapFoldBack";"Select";"SelectMany"
        "Choose";"Filter";"Where";"Iterate";"IterateIndexed";"Iterate2";"ForEach";"Fold";"FoldBack";"Fold2";"Aggregate";"Reduce"
        "Scan";"ScanBack";"Sum";"SumBy";"Average";"AverageBy";"Pick";"TryPick";"Find";"TryFind";"FindBack";"TryFindBack";"FindIndex";"TryFindIndex";"FindLastIndex";"TryFindLastIndex"
        "ForAll";"Exists";"All";"Any";"Zip";"Zip3";"Unzip";"Unzip3";"Partition";"SplitAt";"ChunkBySize";"Chunk";"Windowed"
        "Take";"Truncate";"Skip";"TakeWhile";"SkipWhile";"TakeLast";"SkipLast";"Head";"TryHead";"Tail";"Last";"TryLast"
        "First";"FirstOrDefault";"LastOrDefault";"Single";"SingleOrDefault";"Item";"TryItem";"ElementAt";"ElementAtOrDefault"
        "Indexed";"Pairwise";"Reverse";"Replicate";"Initialize";"InitializeInfinite";"Unfold";"Delay";"Length";"IsEmpty"
        "Count";"LongCount";"get_Length";"get_IsEmpty";"get_IsCons";"get_Head";"get_Tail";"get_HeadOrDefault";"get_TailOrNull";"Cons";"get_Count";"Get";"Set";"Create";"ZeroCreate";"Copy";"CopyTo";"Fill";"Blit"
        "Permute";"Transpose";"AllPairs";"ExactlyOne";"TryExactlyOne";"ReadOnly";"OfOption";"OfValueOption"
        "InsertAt";"RemoveAt";"UpdateAt";"InsertManyAt";"RemoveManyAt";"Add";"AddRange";"Insert";"InsertRange";"Clear"
        "GetRange";"Slice";"get_Item";"set_Item";"get_Capacity";"set_Capacity";"EnsureCapacity";"TrimExcess";"AsReadOnly";"AsEnumerable"
        "DefaultValue";"DefaultWith";"OrElse";"OrElseWith";"Bind";"Flatten";"IsSome";"IsNone";"IsOk";"IsError";"GetValue";"ToArray";"ToList";"ToSeq";"OfArray";"OfList";"OfSeq";"OfNullable";"ToNullable";"OfObj";"ToObj"
        "Enqueue";"Dequeue";"TryDequeue";"Peek";"TryPeek";"Push";"Pop";"TryPop";"TryPeek";"DefaultIfEmpty"
    ]
    let private compare=set ["Contains";"IndexOf";"LastIndexOf";"Remove";"RemoveAll";"Sort";"SortBy";"SortWith";"SortDescending";"SortByDescending";"Order";"OrderBy";"OrderDescending";"OrderByDescending";"ThenBy";"ThenByDescending";"Min";"Max";"MinBy";"MaxBy";"BinarySearch";"SequenceEqual";"CompareWith"]
    let private hash=set ["Distinct";"DistinctBy";"Union";"UnionBy";"Except";"ExceptBy";"Intersect";"IntersectBy";"Join";"GroupJoin";"LeftJoin";"RightJoin";"GroupBy";"CountBy";"AggregateBy";"ToLookup";"ToDictionary";"ToHashSet"]
    let private taskValue=set [".ctor";"get_Task";"get_CompletedTask";"FromResult";"FromException";"FromCanceled";"WhenAll";"get_Result";"get_Exception";"get_Status";"get_IsCompleted";"get_IsCompletedSuccessfully";"get_IsCanceled";"get_IsFaulted";"GetAwaiter";"ConfigureAwait";"AsTask";"Preserve";"SetResult";"TrySetResult";"SetException";"TrySetException";"SetCanceled";"TrySetCanceled";"Dispose"]
    let deterministicTaskMember (name:string) memberName =
        let completionState=set ["get_Exception";"get_Status";"get_IsCompleted";"get_IsCompletedSuccessfully";"get_IsCanceled";"get_IsFaulted"]
        let awaiter=
            ["System.Runtime.CompilerServices.TaskAwaiter";"System.Runtime.CompilerServices.ConfiguredTaskAwaitable"
             "System.Runtime.CompilerServices.ValueTaskAwaiter";"System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable"]
            |> List.exists(fun prefix -> name.StartsWith(prefix,StringComparison.Ordinal))
        // A generated MoveNext can contain handwritten polling too. Only the
        // trusted Async.AwaitTask owner handles suspension; an application
        // completion bit or raw continuation registration has no exemption.
        if awaiter then not(Set.contains memberName completionState || memberName="OnCompleted" || memberName="UnsafeOnCompleted")
        elif name="System.Threading.Tasks.ValueTask" || name.StartsWith("System.Threading.Tasks.ValueTask`",StringComparison.Ordinal) then
            not(Set.contains memberName completionState)
        elif name="System.Threading.Tasks.Task" || name.StartsWith("System.Threading.Tasks.Task`",StringComparison.Ordinal) ||
             name.StartsWith("System.Threading.Tasks.TaskCompletionSource`",StringComparison.Ordinal) then
            Set.contains memberName taskValue && not(Set.contains memberName completionState)
        else true

    let private expand references =
        NativeTypeReference.validate references
        let values=HashSet<NativeTypeReference>()
        let rec add reference =
            if values.Add reference then
                match reference with
                | NativeTypeReference.Named(_,_,arguments) -> for argument in arguments do add argument
                | NativeTypeReference.Vector element | NativeTypeReference.Array(element,_) | NativeTypeReference.ByReference element -> add element
                | _ -> ()
        for reference in references do add reference
        Seq.toList values

    let rules (owner:ClrType) (memberName:string) instance (parameterTypes:ClrType list) (methodArguments:ClrType list) =
        let name=owner.Name
        let parameters=parameterTypes |> List.map _.Reference
        let inputs=(if instance then [owner.Reference] else []) @ (owner.Arguments |> List.map _.Reference) @ parameters @ (methodArguments |> List.map _.Reference)
        let allInputs=expand inputs
        let rules members roots=roots |> List.map(fun receiver -> receiver,members)
        let all members=rules (Some members) allInputs
        let collection=name.StartsWith("System.Collections.",StringComparison.Ordinal) || name="System.Linq.Enumerable" || name.StartsWith("Microsoft.FSharp.Collections.",StringComparison.Ordinal)
        let dictionary=name="System.Collections.Generic.Dictionary`2"
        let hashSet=name="System.Collections.Generic.HashSet`1"
        let ordered=name.Contains("Sorted",StringComparison.Ordinal) || name.Contains("FSharpMap`",StringComparison.Ordinal) || name.Contains("FSharpSet`",StringComparison.Ordinal) || name="Microsoft.FSharp.Collections.MapModule" || name="Microsoft.FSharp.Collections.SetModule"
        let sequence=name="System.Linq.Enumerable" ||
                     Set.contains name (set ["Microsoft.FSharp.Collections.ArrayModule";"Microsoft.FSharp.Collections.ListModule";"Microsoft.FSharp.Collections.SeqModule";"Microsoft.FSharp.Core.OptionModule";"Microsoft.FSharp.Core.ValueOptionModule";"Microsoft.FSharp.Core.ResultModule";"System.Collections.ArrayList";"System.Collections.Queue";"System.Collections.Stack"]) ||
                     (["System.Collections.Generic.List`";"System.Collections.Generic.Queue`";"System.Collections.Generic.Stack`";"System.Collections.Generic.LinkedList`";"Microsoft.FSharp.Collections.FSharpList`"] |> List.exists(fun prefix -> name.StartsWith(prefix,StringComparison.Ordinal)))
        let functionalValue=name.StartsWith("Microsoft.FSharp.Core.FSharpOption`",StringComparison.Ordinal) || name.StartsWith("Microsoft.FSharp.Core.FSharpValueOption`",StringComparison.Ordinal) || name.StartsWith("Microsoft.FSharp.Core.FSharpResult`",StringComparison.Ordinal) || name.StartsWith("Microsoft.FSharp.Core.FSharpRef`",StringComparison.Ordinal)
        let tupleValue=name="System.Tuple" || name="System.ValueTuple" || name.StartsWith("System.Tuple`",StringComparison.Ordinal) || name.StartsWith("System.ValueTuple`",StringComparison.Ordinal) || name="System.Collections.Generic.KeyValuePair`2"
        let tupleStorage=tupleValue && Set.contains memberName (set [".ctor";"Create";"get_Item1";"get_Item2";"get_Item3";"get_Item4";"get_Item5";"get_Item6";"get_Item7";"get_Item8";"get_Rest";"get_Key";"get_Value";"ToTuple";"ToValueTuple"])
        let nullableStorage=name="System.Nullable`1" && Set.contains memberName (set [".ctor";"get_HasValue";"get_Value";"GetValueOrDefault"])
        let comparerFactory=(name.StartsWith("System.Collections.Generic.EqualityComparer`",StringComparison.Ordinal) || name.StartsWith("System.Collections.Generic.Comparer`",StringComparison.Ordinal)) && memberName="get_Default"
        let valueTask=name="System.Threading.Tasks.ValueTask" || name.StartsWith("System.Threading.Tasks.ValueTask`",StringComparison.Ordinal) || name.StartsWith("System.Runtime.CompilerServices.ValueTaskAwaiter",StringComparison.Ordinal) || name.StartsWith("System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable",StringComparison.Ordinal)
        let valueTaskSources () =
            let arguments=owner.Arguments
            let name=if arguments.IsEmpty then "System.Threading.Tasks.Sources.IValueTaskSource" else "System.Threading.Tasks.Sources.IValueTaskSource`1"
            let source=ClrType.named (typeof<obj>.Assembly.GetName().Name) name arguments
            rules (Some(set ["GetStatus";"GetResult";"OnCompleted"])) [source.Reference]
        let valueFormatting=Set.contains name (set ["System.Object";"System.ValueType";"System.String";"System.Convert";"System.Text.StringBuilder";"System.IFormattable";"System.ISpanFormattable";"System.IUtf8SpanFormattable";"System.IFormatProvider";"System.ICustomFormatter"])
        let valueComparison=Set.contains name (set ["System.Object";"System.ValueType";"System.String";"System.StringComparer";"System.IComparable";"System.IComparable`1";"System.IEquatable`1";"System.Collections.IComparer";"System.Collections.IEqualityComparer";"System.Collections.IStructuralComparable";"System.Collections.IStructuralEquatable"]) ||
                            (["System.Collections.Generic.Comparer`";"System.Collections.Generic.EqualityComparer`";"System.Collections.Generic.IComparer`";"System.Collections.Generic.IEqualityComparer`"] |> List.exists(fun prefix -> name.StartsWith(prefix,StringComparison.Ordinal)))
        let asyncBuilder=name.StartsWith("Microsoft.FSharp.Control.FSharpAsyncBuilder",StringComparison.Ordinal)
        let compilerCollector=owner.Assembly="FSharp.Core" &&
                              Set.contains name (set ["Microsoft.FSharp.Core.CompilerServices.ArrayCollector`1";"Microsoft.FSharp.Core.CompilerServices.ListCollector`1"])
        let functionInvoke=owner.Assembly="FSharp.Core" &&
                           (((name.StartsWith("Microsoft.FSharp.Core.FSharpFunc`",StringComparison.Ordinal) ||
                              name.StartsWith("Microsoft.FSharp.Core.OptimizedClosures+FSharpFunc`",StringComparison.Ordinal)) &&
                             Set.contains memberName (set ["Invoke";"InvokeFast"])) ||
                            (name="Microsoft.FSharp.Core.FSharpTypeFunc" && memberName="Specialize"))
        // A format value stores text and optional captured values. Constructing
        // it or reading those fields does not render, compare or hash its
        // generic argument types. Later formatting still owns its callbacks.
        let formatStorage=owner.Assembly="FSharp.Core" &&
                          Set.contains name (set ["Microsoft.FSharp.Core.PrintfFormat`4";"Microsoft.FSharp.Core.PrintfFormat`5"]) &&
                          Set.contains memberName (set [".ctor";"get_Value";"get_Captures";"get_CaptureTypes"])
        let retained=(name="System.Object" && memberName=".ctor") ||
                     (name.StartsWith("System.Func`",StringComparison.Ordinal) || name="System.Action" || name.StartsWith("System.Action`",StringComparison.Ordinal)) && memberName=".ctor" ||
                     name.StartsWith("Microsoft.FSharp.Core.FSharpFunc`",StringComparison.Ordinal) && memberName=".ctor" ||
                     name="Microsoft.FSharp.Core.FSharpTypeFunc" && memberName=".ctor" ||
                     name.StartsWith("Microsoft.FSharp.Core.OptimizedClosures+FSharpFunc`",StringComparison.Ordinal) && memberName=".ctor"
        let taskCarrier=(name.StartsWith("System.Threading.Tasks.Task",StringComparison.Ordinal) || name.StartsWith("System.Threading.Tasks.ValueTask",StringComparison.Ordinal)) && Set.contains memberName taskValue
        let awaiter=(name.StartsWith("System.Runtime.CompilerServices.TaskAwaiter",StringComparison.Ordinal) || name.StartsWith("System.Runtime.CompilerServices.ValueTaskAwaiter",StringComparison.Ordinal) || name.StartsWith("System.Runtime.CompilerServices.ConfiguredTaskAwaitable",StringComparison.Ordinal) || name.StartsWith("System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable",StringComparison.Ordinal) || name.StartsWith("System.Runtime.CompilerServices.AsyncTaskMethodBuilder",StringComparison.Ordinal) || name.StartsWith("System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder",StringComparison.Ordinal)) &&
                    (Set.contains memberName taskValue || Set.contains memberName (set ["Create";"GetResult";"get_IsCompleted"]))
        let implicitRules=
            if valueTask then valueTaskSources()
            // Invoke dispatches the frozen function body; it does not hash,
            // compare or format the argument/result records. The exact receiver
            // slot below and the constructed closure graph still review its IL.
            elif functionInvoke || formatStorage then []
            // Numeric primitive overloads do not call deployment values. The
            // object overloads dispatch only the owning IConvertible member;
            // an arbitrary record cannot supply that interface implicitly.
            // A supplied format provider can still execute GetFormat while
            // converting strings or a custom convertible value.
            elif name="System.Convert" && Set.contains memberName (set ["ToBoolean";"ToChar";"ToSByte";"ToByte";"ToInt16";"ToUInt16";"ToInt32";"ToUInt32";"ToInt64";"ToUInt64";"ToSingle";"ToDouble";"ToDecimal";"ToDateTime";"GetTypeCode";"IsDBNull";"ChangeType"]) then
                let convertible = ClrType.named (typeof<IConvertible>.Assembly.GetName().Name) "System.IConvertible" []
                let members =
                    if memberName="GetTypeCode" || memberName="IsDBNull" then set ["GetTypeCode"]
                    elif memberName="ChangeType" then set ["GetTypeCode";"ToBoolean";"ToChar";"ToSByte";"ToByte";"ToInt16";"ToUInt16";"ToInt32";"ToUInt32";"ToInt64";"ToUInt64";"ToSingle";"ToDouble";"ToDecimal";"ToDateTime";"ToString";"ToType"]
                    else set [memberName]
                let conversion =
                    if parameters |> List.contains (NativeTypeReference.Primitive PrimitiveTypeCode.Object) then
                        rules (Some members) [convertible.Reference]
                    else []
                let providers = parameters |> List.filter(function NativeTypeReference.Named(_,"System.IFormatProvider",[]) -> true | _ -> false)
                conversion @ rules (Some(set ["GetFormat"])) providers
            elif compilerCollector && Set.contains memberName (set [".ctor";"Add";"AddMany";"AddManyAndClose";"Close"]) then all enumeration
            elif retained || taskCarrier || awaiter || tupleStorage || nullableStorage || comparerFactory || functionalValue && (memberName=".ctor" || memberName="Some" || memberName.StartsWith("get_",StringComparison.Ordinal) || memberName.StartsWith("New",StringComparison.Ordinal) || name.StartsWith("Microsoft.FSharp.Core.FSharpRef`",StringComparison.Ordinal) && memberName="set_Value") then []
            elif asyncBuilder && memberName="Using" then all (set ["Dispose";"DisposeAsync"])
            elif asyncBuilder && memberName="For" then all enumeration
            elif asyncBuilder && Set.contains memberName (set [".ctor";"Return";"ReturnFrom";"Zero";"Delay";"Bind";"Combine";"TryWith";"TryFinally";"While";"Run"]) then []
            // Optimized workflows call these implementations directly. Bind
            // and TryWith invoke the reviewed continuation/filter; they do
            // not hash or compare the result carried by the async activation.
            elif owner.Assembly="FSharp.Core" && name="Microsoft.FSharp.Control.AsyncPrimitives" && Set.contains memberName (set ["Bind";"MakeAsync";"TryWith"]) then []
            elif owner.Assembly="FSharp.Core" && name="Microsoft.FSharp.Control.AsyncActivation`1" && memberName="Success" then []
            elif name.StartsWith("Microsoft.FSharp.Control.FSharpAsync",StringComparison.Ordinal) && Set.contains memberName (set ["AwaitTask";"AwaitValueTask";"Ignore";"Catch"]) then []
            elif valueFormatting && Set.contains memberName (set ["ToString";"TryFormat";"Format";"AppendFormat"]) ||
                 (name="System.String" && Set.contains memberName (set ["Concat";"Join"])) ||
                 owner.Assembly="FSharp.Core" && memberName.Contains("ToString",StringComparison.Ordinal) then all formatting
            elif (valueComparison || owner.Assembly="FSharp.Core" && name="Microsoft.FSharp.Core.Operators") && Set.contains memberName (set ["Equals";"Compare";"CompareTo";"op_Equality";"op_Inequality";"op_LessThan";"op_GreaterThan";"op_LessThanOrEqual";"op_GreaterThanOrEqual"]) ||
                 owner.Assembly="FSharp.Core" && (memberName.StartsWith("GenericEquality",StringComparison.Ordinal) || memberName.StartsWith("GenericComparison",StringComparison.Ordinal)) then all equality
            elif memberName="GetHashCode" || owner.Assembly="FSharp.Core" && (memberName="Hash" || memberName.StartsWith("GenericHash",StringComparison.Ordinal)) then all hashing
            elif collection && ordered then all (Set.union enumeration equality)
            elif dictionary && memberName="ContainsValue" then
                let values=owner.Arguments |> List.tryItem 1 |> Option.map(fun value -> expand [value.Reference]) |> Option.defaultValue allInputs
                rules (Some equality) values
            elif (dictionary || hashSet) && Set.contains memberName (set ["get_Count";"get_Comparer";"get_Keys";"get_Values";"Clear";"EnsureCapacity";"TrimExcess"]) then []
            elif (dictionary || hashSet) && Set.contains memberName (set ["GetEnumerator";"CopyTo"]) then all enumeration
            elif (dictionary || hashSet) && memberName=".ctor" &&
                 (parameters |> List.forall(function
                    | NativeTypeReference.Primitive _ -> true
                    | NativeTypeReference.Named(_,name,_) when name="System.Collections.Generic.IEqualityComparer`1" -> true
                    | _ -> false)) then []
            elif (dictionary && Set.contains memberName (set ["Add";"TryAdd";"TryGetValue";"get_Item";"set_Item";"ContainsKey";"Remove";".ctor"])) ||
                 (hashSet && Set.contains memberName (set ["Add";"TryGetValue";"Contains";"Remove";"UnionWith";"IntersectWith";"ExceptWith";"SymmetricExceptWith";"IsSubsetOf";"IsSupersetOf";"IsProperSubsetOf";"IsProperSupersetOf";"Overlaps";"SetEquals";".ctor"])) then
                // A value stored beside a primitive key is not a hash callback.
                // The key and an explicitly supplied comparer own that demand.
                let key=owner.Arguments |> List.tryHead |> Option.map _.Reference
                let keys=key |> Option.map(fun value -> expand [value]) |> Option.defaultValue allInputs
                let comparers=parameters |> List.filter(function NativeTypeReference.Named(_,name,_) -> name.Contains("EqualityComparer",StringComparison.Ordinal) | _ -> false)
                all enumeration @ rules (Some hashing) (keys @ comparers)
            elif sequence && Set.contains memberName hash then
                let projected=parameters |> List.tryPick(function
                    | NativeTypeReference.Named(_,name,arguments) when (name.StartsWith("System.Func`",StringComparison.Ordinal) || name.StartsWith("Microsoft.FSharp.Core.FSharpFunc`",StringComparison.Ordinal)) && arguments.Length>=2 -> Some(List.last arguments)
                    | _ -> None)
                let elements=parameters |> List.choose(function
                    | NativeTypeReference.Named(_,name,[element]) when name="System.Collections.Generic.IEnumerable`1" || name="Microsoft.FSharp.Collections.FSharpList`1" -> Some element
                    | NativeTypeReference.Vector element -> Some element
                    | _ -> None)
                let keys=match projected with Some key -> expand [key] | None when not elements.IsEmpty -> expand elements | None -> allInputs
                let comparers=parameters |> List.filter(function NativeTypeReference.Named(_,name,_) -> name.Contains("EqualityComparer",StringComparison.Ordinal) | _ -> false)
                all enumeration @ rules (Some hashing) (keys @ comparers)
            elif sequence && Set.contains memberName compare then all (Set.union enumeration equality)
            elif sequence && (Set.contains memberName traversal || memberName=".ctor") then all enumeration
            elif owner.Assembly="FSharp.Core" && name="Microsoft.FSharp.Core.Operators" && memberName="op_Append" then all enumeration
            elif name="System.Array" && Set.contains memberName (set ["Copy";"ConstrainedCopy";"Clear";"Empty";"Fill";"get_Length";"get_LongLength";"GetLength";"GetLongLength";"GetLowerBound";"GetUpperBound"]) then []
            elif name="System.Object" && Set.contains memberName (set ["ReferenceEquals";"GetType"]) then []
            elif name.StartsWith("Microsoft.FSharp.Core.LanguagePrimitives",StringComparison.Ordinal) && Set.contains memberName (set ["PhysicalEquality";"GenericZero";"GenericOne";"DefaultOf";"TypeTestGeneric";"UnboxGeneric";"GenericEqualityComparer";"get_GenericEqualityComparer";"FastGenericEqualityComparer";"get_FastGenericEqualityComparer";"GenericComparer";"get_GenericComparer";"FastGenericComparer";"get_FastGenericComparer";"GenericEqualityERComparer";"get_GenericEqualityERComparer";"GetArray";"SetArray";"GetArray2D";"SetArray2D";"GetArray3D";"SetArray3D";"GetArray4D";"SetArray4D";"GetArraySlice";"SetArraySlice";"GetString";"GetStringSlice"]) then []
            elif name="Microsoft.FSharp.Core.Operators" && Set.contains memberName (set ["Box";"Unbox";"UnboxGeneric";"Ignore";"Identity";"Fst";"Snd";"Not";"IsNull";"DefaultArg";"DefaultValueArg";"op_PipeRight";"op_PipeLeft";"op_ComposeRight";"op_ComposeLeft"]) then []
            else rules None allInputs
        // Even an otherwise storage-only method can be an interface call on
        // a deployment receiver. Keep the exact named receiver slot as well.
        let direct=if instance && memberName<>".ctor" then [owner.Reference,Some(Set.singleton memberName)] else []
        let deferredBuilder=(asyncBuilder && Set.contains memberName (set [".ctor";"Return";"ReturnFrom";"Zero";"Delay";"Bind";"Combine";"TryWith";"TryFinally";"While";"Run";"Using";"For"])) ||
                            (owner.Assembly="FSharp.Core" && name="Microsoft.FSharp.Control.AsyncPrimitives" && memberName="MakeAsync")
        direct @ implicitRules,retained || taskCarrier || awaiter || deferredBuilder

/// Inspects frozen metadata before any selected getter can initialize its module.
/// F# startup classes, dependency methods and constructed closure bodies join the
/// same bounded graph. Unsupported indirect/unmanaged/unresolved calls refuse.
type internal AssemblyAdmission(primary:byte[],dependencies:seq<byte[]>,profile:NativeAssemblyAdmissionProfile) =
    let images=Dictionary<string,FrozenImage>(StringComparer.Ordinal)
    let signatures=SignatureTypes()
    // A method admitted for an action must still be reviewed independently if
    // an initializer or deterministic handler can reach the same method.
    let visited=HashSet<AdmissionPhase*string*int*GenericContext>()
    let constructors=HashSet<AdmissionPhase*string*int*GenericContext>()
    // Frozen receiver discovery is append-only. Each dispatch remembers the
    // exact prefix already reviewed, including unsuccessful matches; a later
    // receiver must still be checked even after an earlier one matched.
    let constructorOrder=ResizeArray<AdmissionPhase*string*int*GenericContext>()
    let dispatchCursors=Dictionary<AdmissionPhase*string*int*GenericContext,int>()
    let matchedDispatches=HashSet<AdmissionPhase*string*int*GenericContext>()
    let reviewedImportCallbacks=HashSet<string*int*GenericContext>()
    let typeInitializers=Dictionary<string*int,MethodDefinitionHandle array>()
    let dispatches=Dictionary<AdmissionPhase*string*int*GenericContext,FrozenImage*MethodDefinitionHandle*GenericContext>()
    let deferredVirtualDispatches=Dictionary<AdmissionPhase*string*int*GenericContext,FrozenImage*MethodDefinitionHandle*GenericContext>()
    let deferredCallbacks=Dictionary<string*int*GenericContext,FrozenImage*MethodDefinitionHandle*GenericContext>()
    let capturedStatics=HashSet<NativeStaticField>()
    // These bounded invocation-owned caches contain only immutable CLR structure.
    // Cache saturation falls back to original reflection, never rejects admission.
    let captureFieldStructure=Dictionary<Type,FieldInfo array>()
    let capturePointerStructure=Dictionary<FieldInfo,bool>(
        { new IEqualityComparer<FieldInfo> with
            member _.Equals(left,right)=Object.ReferenceEquals(left,right)
            member _.GetHashCode(field)=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(field) })
    let mutable cachedCaptureFieldSlots=0
    let captureFields (declaring:Type) =
        match captureFieldStructure.TryGetValue declaring with
        | true,fields -> fields
        | _ ->
            let fields=declaring.GetFields(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly)
            if captureFieldStructure.Count<4096 && cachedCaptureFieldSlots+fields.Length<=65536 then
                captureFieldStructure.Add(declaring,fields)
                cachedCaptureFieldSlots<-cachedCaptureFieldSlots+fields.Length
            fields
    let unsafeCaptureField (field:FieldInfo) =
        match capturePointerStructure.TryGetValue field with
        | true,isUnsafe -> isUnsafe
        | _ ->
            // Resolve classification at its original field position, not eagerly.
            let isUnsafe=field.FieldType.IsPointer || field.FieldType.IsByRef
            if capturePointerStructure.Count<65536 then capturePointerStructure.Add(field,isUnsafe)
            isUnsafe

    let frameworkDemands=HashSet<FrameworkDemand>()
    let frameworkSources=Dictionary<FrameworkDemand,string>()
    let deferredFrameworkDemands=HashSet<FrameworkDemand>()
    let frameworkCursors=Dictionary<FrameworkDemand,int>()
    let mutable deferredFrameworkPointer=false
    let mutable frameworkPairs=0
    let mutable phase=AdmissionPhase.Initialization
    let mutable eagerImportCallbacks=false
    let inPhase selected operation =
        let previous=phase
        phase<-selected
        try operation () finally phase<-previous
    let mutable inspectedBytes=0L
    let mutable primaryName=""
    let reject message=raise(HostOperationException("NativeAssemblyAdmissionRejected",message))
    let opcodes=
        typeof<OpCodes>.GetFields(BindingFlags.Public ||| BindingFlags.Static)
        |> Seq.map(fun field -> let code=unbox<OpCode>(field.GetValue null) in code.Value,code)
        |> dict
    let pureTypes=set [
        "System.Object";"System.ValueType";"System.Attribute";"System.String";"System.StringComparer";"System.Char";"System.Boolean";"System.Byte";"System.SByte"
        "System.Int16";"System.UInt16";"System.Int32";"System.UInt32";"System.Int64";"System.UInt64"
        "System.Single";"System.Double";"System.Decimal";"System.Math";"System.MathF";"System.Convert"
        "System.BitConverter";"System.Array";"System.Enum";"System.Nullable`1";"System.Tuple"
        "System.ValueTuple";"System.Guid";"System.TimeSpan";"System.DateTime";"System.DateTimeOffset"
        "System.StringComparison";"System.StringSplitOptions";"System.Uri";"System.UriBuilder"
        "System.Text.StringBuilder";"System.Text.Encoding";"System.Text.UTF8Encoding"
        "System.Text.ASCIIEncoding";"System.Text.UnicodeEncoding";"System.Text.Encoder";"System.Text.Decoder"
        "System.IO.MemoryStream";"System.IO.Stream";"System.IO.BinaryReader";"System.IO.BinaryWriter"
        "System.IO.StringReader";"System.IO.StringWriter";"System.IO.TextReader";"System.IO.TextWriter"
        "System.Threading.CancellationToken";"System.Threading.CancellationTokenRegistration"
        "System.Threading.Tasks.Task";"System.Threading.Tasks.ValueTask"
        "System.Runtime.CompilerServices.RuntimeHelpers";"System.Runtime.CompilerServices.TaskAwaiter"
        "System.Runtime.CompilerServices.ConfiguredTaskAwaitable";"System.Runtime.CompilerServices.AsyncTaskMethodBuilder"
        "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder";"System.Runtime.CompilerServices.IsExternalInit"
        "System.ReadOnlyMemory`1";"System.Memory`1";"System.ReadOnlySpan`1";"System.Span`1"
        "System.Index";"System.Range";"System.Numerics.BigInteger";"System.Numerics.Vector`1"
        "System.Security.Cryptography.SHA256";"System.Security.Cryptography.SHA512";"System.Security.Cryptography.HashAlgorithm"
        "System.Security.Cryptography.IncrementalHash";"System.Security.Cryptography.HashAlgorithmName";"System.Security.Cryptography.CryptographicOperations"
        "System.Globalization.NumberStyles";"System.Globalization.DateTimeStyles";"System.Globalization.CultureInfo"
        "System.Globalization.NumberFormatInfo";"System.Globalization.DateTimeFormatInfo"
        "System.Type";"System.RuntimeTypeHandle";"System.IDisposable";"System.IAsyncDisposable"
        "System.IComparable";"System.IEquatable`1";"System.IComparable`1";"System.IFormattable"
    ]
    let isTrustedAssembly name = name="Convex.Runtime" || name="Convex.FSharp" || name="FSharp.Core" || name="netstandard" ||
                                 name="mscorlib" || name="System" || name.StartsWith("System.",StringComparison.Ordinal)
    // A framework type is not pure merely because most of its members are.
    // Parsing can read today's date; formatting, conversion and a
    // DateTimeOffset(DateTime) constructor can consult the machine timezone
    // without an explicit clock or environment call in the deployment IL.
    let calendarMembers=set [
        "MinValue";"MaxValue";"UnixEpoch";"get_Date";"get_Day";"get_DayOfWeek";"get_DayOfYear"
        "get_Hour";"get_Millisecond";"get_Microsecond";"get_Nanosecond";"get_Minute";"get_Month"
        "get_Second";"get_Ticks";"get_TimeOfDay";"get_Year"
        "Add";"AddDays";"AddHours";"AddMicroseconds";"AddMilliseconds";"AddMinutes";"AddMonths"
        "AddSeconds";"AddTicks";"AddYears";"Subtract";"Compare";"CompareTo";"Equals"
        "op_Addition";"op_Subtraction";"op_Equality";"op_Inequality"
        "op_GreaterThan";"op_GreaterThanOrEqual";"op_LessThan";"op_LessThanOrEqual"
    ]
    let dateTimeMembers=calendarMembers+set [
        ".ctor";"get_Kind";"DaysInMonth";"IsLeapYear";"SpecifyKind";"FromOADate";"ToOADate";"FromFileTimeUtc"
    ]
    let dateTimeOffsetMembers=calendarMembers+set [
        "get_DateTime";"get_UtcDateTime";"get_UtcTicks";"get_Offset";"EqualsExact";"ToOffset"
        "FromUnixTimeSeconds";"FromUnixTimeMilliseconds";"ToUnixTimeSeconds";"ToUnixTimeMilliseconds";"ToFileTime"
    ]
    let deterministicDateMember name memberName (parameters:ClrType list option) =
        match name with
        | "System.DateTime" -> Set.contains memberName dateTimeMembers
        | "System.DateTimeOffset" when memberName=".ctor" ->
            // Overloads taking a DateTime validate or infer its local offset.
            // Only constructors with an explicit offset and independent
            // calendar/tick arguments are admitted.
            parameters |> Option.exists(fun values ->
                (values |> List.exists(fun value -> value.Name="System.TimeSpan")) &&
                (values |> List.forall(fun value -> value.Name<>"System.DateTime")))
        | "System.DateTimeOffset" -> Set.contains memberName dateTimeOffsetMembers
        | "System.Convert" -> memberName<>"ToDateTime" && memberName<>"ChangeType"
        | _ when name.StartsWith("System.Text.Json.",StringComparison.Ordinal) ->
            not(Set.contains memberName (set ["GetDateTime";"TryGetDateTime";"GetDateTimeOffset";"TryGetDateTimeOffset"])) &&
            not((memberName="WriteString" || memberName="WriteStringValue") &&
                (parameters |> Option.exists(List.exists(fun value -> value.Name="System.DateTime"))))
        | _ -> true

    let forbiddenCall (owner:ClrType) memberName parameters =
        let hint =
            if deterministicDateMember owner.Name memberName parameters then ""
            else " Deterministic execution requires explicit calendar/tick construction or Unix timestamps; parse and format dates before invocation or in an action."
        reject("Forbidden or unresolvable native call "+owner.Name+"."+memberName+hint)

    let allowed (owner:ClrType) (methodName:string) parameters =
        let managedAction =
            phase=AdmissionPhase.Handler && profile=NativeAssemblyAdmissionProfile.ManagedActionFirstParty
        if owner.Assembly="Convex.Runtime" then
            not ((owner.Name="Convex.Runtime.IRegisteredFunction" &&
                  Set.contains methodName (set ["Invoke";"get_ExecutionHandler"])) ||
                 (owner.Name="Convex.Runtime.IExecutionFrontend" && methodName="Enter") ||
                 (owner.Name="Convex.Runtime.IInvocationHost" && Set.contains methodName (set ["Call";"CallAsync"])))
        elif owner.Assembly="Convex.FSharp" then
            // These entrypoints are executor capabilities, not application
            // calls. A local Invoke must not bypass the owning kind/contract.
            not(owner.Name.StartsWith("Convex.FSharp.NativeExecution.",StringComparison.Ordinal)) &&
            not ((owner.Name="Convex.FSharp.NativeFunctionDefinition" || owner.Name="Convex.FSharp.NativeHttpFunctionDefinition") &&
                 Set.contains methodName (set ["Invoke";"get_ExecutionHandler"])) &&
            not ((owner.Name="Convex.FSharp.InvocationRuntime" || owner.Name="Convex.FSharp.InvocationOperations") && methodName="enter")
        elif owner.Assembly="FSharp.Core" then
            let ownsScheduling =
                owner.Name.StartsWith("Microsoft.FSharp.Control.FSharpAsync",StringComparison.Ordinal) &&
                (Set.contains methodName (set [
                    "Sleep";"Start";"StartImmediate";"StartAsTask";"StartImmediateAsTask";"RunSynchronously"
                    "StartWithContinuations";"StartChild";"StartChildAsTask";"CancelDefaultToken"
                    "AwaitEvent";"AwaitWaitHandle";"AwaitIAsyncResult";"Parallel";"Choice"
                ]) || methodName.StartsWith("SwitchTo",StringComparison.Ordinal))
            // Async timers and independently started workflows have the same
            // kind boundary as Task.Delay/Run. Wrapping them in F# must not
            // grant action effects to a declaration or transaction handler.
            (managedAction || not ownsScheduling) &&
            not(owner.Name.Contains("Reflection") || owner.Name.Contains("Quotation") || owner.Name.Contains("PrintfModule") && methodName.Contains("PrintFormatTo"))
        elif not(isTrustedAssembly owner.Assembly) then false
        else
            let name=owner.Name
            let forbidden=
                methodName="get_Now" || methodName="get_UtcNow" || methodName="get_Today" || methodName="get_TickCount" ||
                methodName="NewGuid" || methodName="GetHashCode" || methodName="DynamicInvoke" || methodName="CreateDelegate" ||
                methodName="InvokeMember" || methodName="RunClassConstructor" || methodName="EnsureSufficientExecutionStack" ||
                methodName="get_CurrentCulture" || methodName="get_CurrentUICulture" || methodName="get_InstalledUICulture" ||
                methodName="get_CurrentCultureIgnoreCase" || methodName="get_CurrentInfo" ||
                methodName="get_DefaultThreadCurrentCulture" || methodName="get_DefaultThreadCurrentUICulture" ||
                not(deterministicDateMember name methodName parameters) ||
                methodName.StartsWith("set_",StringComparison.Ordinal) && name.StartsWith("System.Globalization.",StringComparison.Ordinal) ||
                methodName="get_StackTrace" || methodName="get_TargetSite" || methodName="GetObjectValue" ||
                name="System.Text.Json.JsonSerializer" ||
                name.StartsWith("System.Threading.Tasks.Task",StringComparison.Ordinal) && Set.contains methodName (set ["Run";"Delay";"get_Factory";"Start";"Wait";"WaitAll";"WaitAny"]) ||
                name="System.Type" && not(Set.contains methodName (set ["GetTypeFromHandle";"op_Equality";"op_Inequality"])) ||
                name="System.Runtime.CompilerServices.RuntimeHelpers" && not(Set.contains methodName (set ["InitializeArray";"IsReferenceOrContainsReferences"]))
            let admittedSurface=
                pureTypes.Contains name || name.StartsWith("System.Collections.",StringComparison.Ordinal) ||
                name.StartsWith("System.Linq.Enumerable",StringComparison.Ordinal) ||
                name.StartsWith("System.Text.Json.",StringComparison.Ordinal) ||
                name.StartsWith("System.Text.RegularExpressions.",StringComparison.Ordinal) ||
                name.StartsWith("System.ValueTuple`",StringComparison.Ordinal) || name.StartsWith("System.Tuple`",StringComparison.Ordinal) ||
                name.StartsWith("System.Func`",StringComparison.Ordinal) || name.StartsWith("System.Action`",StringComparison.Ordinal) || name="System.Action" ||
                name.StartsWith("System.Threading.Tasks.Task`",StringComparison.Ordinal) || name.StartsWith("System.Threading.Tasks.ValueTask`",StringComparison.Ordinal) ||
                name.StartsWith("System.Runtime.CompilerServices.TaskAwaiter`",StringComparison.Ordinal) ||
                name.StartsWith("System.Runtime.CompilerServices.ConfiguredTaskAwaitable`",StringComparison.Ordinal) ||
                name.StartsWith("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`",StringComparison.Ordinal) ||
                name.StartsWith("System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder`",StringComparison.Ordinal) ||
                name.StartsWith("System.Threading.Tasks.TaskCompletionSource`",StringComparison.Ordinal) ||
                name="System.Runtime.ExceptionServices.ExceptionDispatchInfo" && Set.contains methodName (set ["Capture";"Throw";"get_SourceException"]) ||
                name.StartsWith("System.",StringComparison.Ordinal) && name.EndsWith("Attribute",StringComparison.Ordinal) && methodName=".ctor" ||
                name="System.IO.Path" && Set.contains methodName (set ["Combine";"Join";"GetFileName";"GetFileNameWithoutExtension";"GetDirectoryName";"GetExtension";"HasExtension";"IsPathFullyQualified";"IsPathRooted";"ChangeExtension"]) ||
                name.StartsWith("System.",StringComparison.Ordinal) && name.EndsWith("Exception",StringComparison.Ordinal)
            let actionSurface =
                name.StartsWith("System.Net.",StringComparison.Ordinal) ||
                name.StartsWith("System.IO.",StringComparison.Ordinal) ||
                name.StartsWith("System.Security.Cryptography.",StringComparison.Ordinal) ||
                name="System.Random" || name="System.Diagnostics.Stopwatch" ||
                name="System.Threading.CancellationTokenSource" || name="System.Threading.SemaphoreSlim" ||
                name="System.Threading.Interlocked" || name="System.Threading.Volatile" ||
                name.StartsWith("System.Threading.Tasks.",StringComparison.Ordinal) ||
                name.StartsWith("System.Runtime.CompilerServices.Configured",StringComparison.Ordinal)
            let actionForbidden =
                // Managed actions still use pinned managed dependencies and
                // the backend's process lifetime; they cannot load code or
                // manufacture executor capabilities.
                methodName="DynamicInvoke" || methodName="CreateDelegate" || methodName="InvokeMember" ||
                methodName="RunClassConstructor" || name="System.Environment" ||
                name.StartsWith("System.Reflection.",StringComparison.Ordinal) ||
                name.StartsWith("System.Runtime.InteropServices.",StringComparison.Ordinal) ||
                name.StartsWith("System.Diagnostics.Process",StringComparison.Ordinal) ||
                name.StartsWith("System.Runtime.Loader.",StringComparison.Ordinal) ||
                name="System.Type" && not(Set.contains methodName (set ["GetTypeFromHandle";"op_Equality";"op_Inequality"])) ||
                name="System.Runtime.CompilerServices.RuntimeHelpers" && not(Set.contains methodName (set ["InitializeArray";"IsReferenceOrContainsReferences"]))
            if managedAction then (admittedSurface || actionSurface) && not actionForbidden
            else admittedSurface && not forbidden && FrameworkCallbacks.deterministicTaskMember name methodName

    // Enrollment imports a supplied public EC point and verifies its exact
    // transcript. The overload is part of the admission rule: Create() and
    // Create(ECCurve) generate keys and must not inherit this permission.
    let allowedPublicSignatureVerification (reference:MemberReference) (owner:ClrType) memberName =
        if owner.Assembly<>"System.Security.Cryptography" then false
        elif reference.GetKind()=MemberReferenceKind.Field then
            let field=reference.DecodeFieldSignature(signatures,GenericContext.empty).Display
            match owner.Name,memberName,field with
            | "System.Security.Cryptography.ECParameters","Curve","System.Security.Cryptography.ECCurve"
            | "System.Security.Cryptography.ECParameters","Q","System.Security.Cryptography.ECPoint"
            | "System.Security.Cryptography.ECPoint","X","primitive:Byte[]"
            | "System.Security.Cryptography.ECPoint","Y","primitive:Byte[]" -> true
            | _ -> false
        else
            let signature=reference.DecodeMethodSignature(signatures,GenericContext.empty)
            let parameters=signature.ParameterTypes |> Seq.map _.Display |> Seq.toList
            let exact instance result (args:string list) =
                signature.Header.IsInstance=instance && signature.GenericParameterCount=0 &&
                signature.RequiredParameterCount=args.Length && parameters=args && signature.ReturnType.Display=result
            match owner.Name,memberName with
            | "System.Security.Cryptography.ECDsa","Create" ->
                exact false "System.Security.Cryptography.ECDsa" ["System.Security.Cryptography.ECParameters"]
            | "System.Security.Cryptography.ECDsa","VerifyData" ->
                exact true "primitive:Boolean" ["primitive:Byte[]";"primitive:Byte[]";"System.Security.Cryptography.HashAlgorithmName";"System.Security.Cryptography.DSASignatureFormat"]
            | "System.Security.Cryptography.ECCurve+NamedCurves","get_nistP256" ->
                exact false "System.Security.Cryptography.ECCurve" []
            | _ -> false
    do
        match profile with
        | NativeAssemblyAdmissionProfile.DeterministicFirstParty
        | NativeAssemblyAdmissionProfile.ManagedActionFirstParty -> ()
        try
            for index,bytes in Seq.append [primary] dependencies |> Seq.indexed do
                let image=new FrozenImage(bytes)
                if images.ContainsKey image.Name || isTrustedAssembly image.Name then
                    (image :> IDisposable).Dispose()
                    reject "Deployment assemblies cannot replace trusted runtime identities"
                images.Add(image.Name,image)
                if index=0 then primaryName<-image.Name
        with error ->
            for image in images.Values do (image :> IDisposable).Dispose()
            raise error

    let typeContext (image:FrozenImage) (handle:TypeDefinitionHandle) arguments =
        NativeTypeReference.validate (arguments |> List.map _.Reference)
        {TypeOwner=Some(image.Name,MetadataTokens.GetToken(TypeDefinitionHandle.op_Implicit handle : EntityHandle))
         TypeArguments=arguments;MethodArguments=[]}

    let declaringContext (image:FrozenImage) (handle:TypeDefinitionHandle) context =
        let empty=typeContext image handle []
        if context.TypeOwner=empty.TypeOwner then {context with MethodArguments=[]} else empty

    let reflectedContext (kind:Type) methodArguments =
        let definition=if kind.IsGenericType then kind.GetGenericTypeDefinition() else kind
        let context={
            TypeOwner=Some(definition.Assembly.GetName().Name,definition.MetadataToken)
            TypeArguments=if kind.IsGenericType then kind.GetGenericArguments() |> Array.map ClrType.reflected |> Array.toList else []
            MethodArguments=methodArguments |> Array.map ClrType.reflected |> Array.toList
        }
        NativeTypeReference.validate ((context.TypeArguments @ context.MethodArguments) |> List.map _.Reference)
        context

    let captureStatic (image:FrozenImage) (handle:FieldDefinitionHandle) arguments =
        let references=arguments |> List.map _.Reference
        NativeTypeReference.validate references
        let declaring=image.Reader.GetFieldDefinition(handle).GetDeclaringType()
        if image.Reader.GetTypeDefinition(declaring).GetGenericParameters().Count<>references.Length ||
           not(List.forall NativeTypeReference.isClosed references) then
            reject "A captured native static requires an exact closed generic context"
        capturedStatics.Add {
            Assembly=image.Name
            Token=MetadataTokens.GetToken(FieldDefinitionHandle.op_Implicit handle : EntityHandle)
            TypeArguments=references
        } |> ignore

    let ownerType (image:FrozenImage) (handle:EntityHandle) context =
        match handle.Kind with
        | HandleKind.TypeDefinition ->
            let declaring=TypeDefinitionHandle.op_Explicit handle
            let value=(signatures :> ISignatureTypeProvider<ClrType,GenericContext>).GetTypeFromDefinition(image.Reader,declaring,0uy)
            ClrType.named value.Assembly value.Name (declaringContext image declaring context).TypeArguments
        | HandleKind.TypeReference -> MetadataNames.reference image.Reader (TypeReferenceHandle.op_Explicit handle)
        | HandleKind.TypeSpecification -> image.Reader.GetTypeSpecification(TypeSpecificationHandle.op_Explicit handle).DecodeSignature(signatures,context)
        | _ -> reject "Unresolvable native call owner"

    let addFrameworkDemand selected receiver members arguments =
        NativeTypeReference.validate (receiver::arguments)
        if not(NativeTypeReference.isClosed receiver && List.forall NativeTypeReference.isClosed arguments) then
            reject "A native framework callback requires an exact closed generic context"
        match receiver with
        | NativeTypeReference.Named _ | NativeTypeReference.Primitive PrimitiveTypeCode.Object ->
            let demand={Phase=selected;Receiver=receiver;Members=members;MethodArguments=arguments}
            if frameworkDemands.Add demand && frameworkDemands.Count>100000 then reject "Native framework callback graph exceeds its bound"
        | _ -> () // Expanded element types own array/byref input callbacks.

    let rec frameworkDelegate reference depth =
        if depth>32 then reject "Native framework callback inheritance exceeds its bound"
        match reference with
        | NativeTypeReference.Named(_,name,_) when name="System.Delegate" || name="System.MulticastDelegate" -> true
        | _ ->
            match FrameworkTypes.tryDescribe reference with
            | Some description -> description.Parents |> List.exists(fun parent -> frameworkDelegate parent (depth+1))
            | None -> false

    let recordFrameworkRules owner memberName instance parameterTypes methodArguments =
        // Convex's finite execution/registration contracts are separately owned;
        // their registered closure bodies already enter the ordinary graph.
        if owner.Assembly<>"Convex.Runtime" && owner.Assembly<>"Convex.FSharp" then
            let rules,retains=FrameworkCallbacks.rules owner memberName instance parameterTypes methodArguments
            let delegateCreation=memberName=".ctor" && frameworkDelegate owner.Reference 0
            let rules=if delegateCreation then [] else rules
            let retains=retains || delegateCreation
            let arguments=methodArguments |> List.map _.Reference
            let delayed=phase=AdmissionPhase.Initialization && (retains || deferredFrameworkPointer)
            for receiver,members in rules do
                let selected=if delayed then AdmissionPhase.Handler else phase
                addFrameworkDemand selected receiver members arguments
                let demand={Phase=selected;Receiver=receiver;Members=members;MethodArguments=arguments}
                if frameworkDemands.Contains demand && not(frameworkSources.ContainsKey demand) then
                    frameworkSources.Add(demand,owner.Name+"."+memberName)
                if delayed then
                    deferredFrameworkDemands.Add {Phase=AdmissionPhase.Initialization;Receiver=receiver;Members=members;MethodArguments=arguments} |> ignore
                    if deferredFrameworkDemands.Count>100000 then reject "Native deferred framework callback graph exceeds its bound"
            if phase=AdmissionPhase.Initialization && not delayed &&
               ((parameterTypes |> List.exists(fun parameter -> frameworkDelegate parameter.Reference 0)) ||
                (rules |> List.exists(fun (receiver,members) -> members.IsNone && receiver=NativeTypeReference.Primitive PrimitiveTypeCode.Object))) then
                eagerImportCallbacks<-true

    let recordFrameworkCall (reference:MemberReference) owner memberName methodArguments =
        if reference.GetKind()=MemberReferenceKind.Method then
            let context={GenericContext.empty with TypeArguments=owner.Arguments;MethodArguments=methodArguments}
            let signature=reference.DecodeMethodSignature(signatures,context)
            recordFrameworkRules owner memberName signature.Header.IsInstance (Seq.toList signature.ParameterTypes) methodArguments

    let rec refuseFinalizer (image:FrozenImage) (handle:TypeDefinitionHandle) context depth =
        if depth>32 then reject "Native framework callback inheritance exceeds its bound"
        let definition=image.Reader.GetTypeDefinition handle
        for methodHandle in definition.GetMethods() do
            let method=image.Reader.GetMethodDefinition methodHandle
            if image.Reader.GetString method.Name="Finalize" && (method.Attributes &&& MethodAttributes.Virtual)<>enum 0 then
                reject "Native deployment finalizers cannot own an independent execution lifetime"
        if not definition.BaseType.IsNil then
            let parent=ownerType image definition.BaseType context
            match images.TryGetValue parent.Assembly with
            | true,target ->
                match target.Types.TryGetValue parent.Name with
                | true,declaring -> refuseFinalizer target declaring (typeContext target declaring parent.Arguments) (depth+1)
                | _ -> reject "Unresolved deployment finalizer base"
            | _ -> ()

    // FSharpFunc.Invoke and canonical byte-source interface implementations are
    // called by trusted runtime dispatch rather than a deployment call token.
    // Data records are different: construction does not call every generated
    // comparison/hash method. Explicit calls still root those methods below.
    let rec isCallbackType (image:FrozenImage) (handle:TypeDefinitionHandle) depth =
        if depth>32 then reject "Native callback inheritance exceeds its bound"
        let value=image.Reader.GetTypeDefinition handle
        let canonicalInterface=
            value.GetInterfaceImplementations()
            |> Seq.exists(fun implementation ->
                let owner=ownerType image (image.Reader.GetInterfaceImplementation(implementation).Interface) GenericContext.empty
                owner.Assembly="Convex.Runtime" || owner.Assembly="Convex.FSharp" || images.ContainsKey owner.Assembly)
        canonicalInterface ||
        (if value.BaseType.IsNil then false
         else
             let owner=ownerType image value.BaseType GenericContext.empty
             if owner.Assembly="FSharp.Core" && owner.Name.Contains("FSharpFunc`",StringComparison.Ordinal) then true
             else
                 match images.TryGetValue owner.Assembly with
                 | true,target ->
                     match target.Types.TryGetValue owner.Name with
                     | true,declaring -> isCallbackType target declaring (depth+1)
                     | _ -> reject "Unresolved deployment callback base"
                 | _ when isTrustedAssembly owner.Assembly -> false
                 | _ -> reject "Unresolved native callback base")

    let rec visitType (image:FrozenImage) (handle:TypeDefinitionHandle) construct context =
        let context=declaringContext image handle context
        let key=phase,image.Name,MetadataTokens.GetToken(TypeDefinitionHandle.op_Implicit handle : EntityHandle),context
        let value=image.Reader.GetTypeDefinition handle
        if construct && constructors.Add key then
            constructorOrder.Add key
            refuseFinalizer image handle context 0
            let callback=isCallbackType image handle 0
            for attribute in value.GetCustomAttributes() do
                visitMember image (image.Reader.GetCustomAttribute(attribute).Constructor) true context None
            for methodHandle in value.GetMethods() do
                let method=image.Reader.GetMethodDefinition methodHandle
                let name=image.Reader.GetString method.Name
                // Construction does not instantiate a generic method template.
                // MethodSpec, a closed delegate or generic dispatch owns its body.
                if (method.Attributes &&& MethodAttributes.Static)=enum 0 && (method.Attributes &&& MethodAttributes.Abstract)=enum 0 &&
                   method.GetGenericParameters().Count=0 &&
                   (callback || name=".ctor" || name.StartsWith("get_",StringComparison.Ordinal)) then
                    if callback && phase=AdmissionPhase.Initialization && name<>".ctor" then
                        let token=MetadataTokens.GetToken(MethodDefinitionHandle.op_Implicit methodHandle : EntityHandle)
                        deferredCallbacks.TryAdd((image.Name,token,context),(image,methodHandle,context)) |> ignore
                    else visitMethod image methodHandle context
            for fieldHandle in value.GetFields() do
                for attribute in image.Reader.GetFieldDefinition(fieldHandle).GetCustomAttributes() do
                    visitMember image (image.Reader.GetCustomAttribute(attribute).Constructor) true context None
            for propertyHandle in value.GetProperties() do
                for attribute in image.Reader.GetPropertyDefinition(propertyHandle).GetCustomAttributes() do
                    visitMember image (image.Reader.GetCustomAttribute(attribute).Constructor) true context None
        let initializerKey=image.Name,MetadataTokens.GetToken(TypeDefinitionHandle.op_Implicit handle : EntityHandle)
        let initializers=
            match typeInitializers.TryGetValue initializerKey with
            | true,handles -> handles
            | _ ->
                let handles=value.GetMethods() |> Seq.filter(fun methodHandle ->
                    image.Reader.GetString(image.Reader.GetMethodDefinition(methodHandle).Name)=".cctor") |> Seq.toArray
                typeInitializers.Add(initializerKey,handles)
                handles
        for methodHandle in initializers do
            inPhase AdmissionPhase.Initialization (fun () -> visitMethod image methodHandle context)

    and visitMember (image:FrozenImage) (handle:EntityHandle) construct context methodArguments =
        match handle.Kind with
        | HandleKind.MethodDefinition ->
            let methodHandle=MethodDefinitionHandle.op_Explicit handle
            let declaring=image.Reader.GetMethodDefinition(methodHandle).GetDeclaringType()
            let callee={declaringContext image declaring context with MethodArguments=defaultArg methodArguments []}
            if construct then visitType image declaring true callee
            visitMethod image methodHandle callee
        | HandleKind.MethodSpecification ->
            let specification=image.Reader.GetMethodSpecification(MethodSpecificationHandle.op_Explicit handle)
            // Resolve the specification and parent TypeSpec in caller scope;
            // only then bind the callee's method parameters.
            let arguments=specification.DecodeSignature(signatures,context) |> Seq.toList
            for argument in arguments do
                match images.TryGetValue argument.Assembly with
                | true,target ->
                    match target.Types.TryGetValue argument.Name with
                    | true,declaring -> visitType target declaring true (typeContext target declaring argument.Arguments)
                    | _ -> reject "Unresolved native generic type"
                | _ when argument.Assembly="" || isTrustedAssembly argument.Assembly -> ()
                | _ -> reject "Unresolved native generic dependency"
            visitMember image specification.Method construct context (Some arguments)
        | HandleKind.MemberReference ->
            let reference=image.Reader.GetMemberReference(MemberReferenceHandle.op_Explicit handle)
            let owner=ownerType image reference.Parent context
            let methodName=image.Reader.GetString reference.Name
            match images.TryGetValue owner.Assembly with
            | true,target ->
                match target.Types.TryGetValue owner.Name with
                | false,_ -> reject("Unresolved deployment type "+owner.Name)
                | true,declaring ->
                    let callee={typeContext target declaring owner.Arguments with MethodArguments=defaultArg methodArguments []}
                    visitType target declaring construct callee
                    if reference.GetKind()=MemberReferenceKind.Method then
                        let expected=reference.DecodeMethodSignature(signatures,GenericContext.empty)
                        let candidates=
                            target.Reader.GetTypeDefinition(declaring).GetMethods()
                            |> Seq.filter(fun candidate ->
                                let method=target.Reader.GetMethodDefinition candidate
                                let actual=method.DecodeSignature(signatures,GenericContext.empty)
                                target.Reader.GetString(method.Name)=methodName && actual.ParameterTypes.Length=expected.ParameterTypes.Length && actual.GenericParameterCount=expected.GenericParameterCount)
                            |> Seq.toArray
                        if candidates.Length=0 then reject("Unresolved deployment method "+owner.Name+"."+methodName)
                        for method in candidates do visitMethod target method callee
                    elif phase=AdmissionPhase.Handler then
                        for field in target.Reader.GetTypeDefinition(declaring).GetFields() do
                            let definition=target.Reader.GetFieldDefinition field
                            if target.Reader.GetString definition.Name=methodName && (definition.Attributes &&& FieldAttributes.Static)<>enum 0 then
                                captureStatic target field owner.Arguments
            | _ ->
                if phase=AdmissionPhase.Initialization && reference.GetKind()=MemberReferenceKind.Method then
                    let signature=reference.DecodeMethodSignature(signatures,GenericContext.empty)
                    let callbackParameter = signature.ParameterTypes |> Seq.exists(fun value ->
                        value.Display.Contains("FSharpFunc`",StringComparison.Ordinal) ||
                        value.Display.Contains("System.Func`",StringComparison.Ordinal) ||
                        value.Display.Contains("System.Action",StringComparison.Ordinal))
                    let retainsCallback =
                        methodName = ".ctor"
                        || owner.Name.StartsWith("Microsoft.FSharp.Control.FSharpAsyncBuilder", StringComparison.Ordinal)
                        || (owner.Assembly="FSharp.Core" && owner.Name="Microsoft.FSharp.Control.AsyncPrimitives" && methodName="MakeAsync")
                        || (
                            owner.Assembly = "Convex.FSharp"
                            && (
                                owner.Name = "Convex.FSharp.NativeRegistration"
                                || owner.Name = "Convex.FSharp.Convex"
                                || (owner.Name = "Convex.FSharp.Contract" && (methodName = "create" || methodName = "map"))
                                || (owner.Name = "Convex.FSharp.NativeComponentDefinition" && methodName = "WithInitializer")
                                || (owner.Name = "Convex.FSharp.NativeComponentDefinitions" && methodName = "withInitializer")
                            )
                        )
                    let executesWorkflow =
                        owner.Assembly = "Convex.FSharp"
                        && (
                            (owner.Name = "Convex.FSharp.AsyncInterop" && methodName = "toTask")
                            || (owner.Name = "Convex.FSharp.NativeComponentDefinition" && methodName = "Initialize")
                        )
                    if executesWorkflow || not retainsCallback && (callbackParameter || methodName="Invoke" || methodName="InvokeFast") then
                        eagerImportCallbacks<-true
                let parameters =
                    if reference.GetKind()=MemberReferenceKind.Method then
                        Some(reference.DecodeMethodSignature(signatures,context).ParameterTypes |> Seq.toList)
                    else None
                if not(allowed owner methodName parameters || allowedPublicSignatureVerification reference owner methodName) then
                    forbiddenCall owner methodName parameters
                recordFrameworkCall reference owner methodName (defaultArg methodArguments [])
        | HandleKind.FieldDefinition ->
            let field=image.Reader.GetFieldDefinition(FieldDefinitionHandle.op_Explicit handle)
            let declaring=field.GetDeclaringType()
            let owner=declaringContext image declaring context
            visitType image declaring false owner
            if phase=AdmissionPhase.Handler && (field.Attributes &&& FieldAttributes.Static)<>enum 0 then
                captureStatic image (FieldDefinitionHandle.op_Explicit handle) owner.TypeArguments
        | HandleKind.TypeDefinition -> visitType image (TypeDefinitionHandle.op_Explicit handle) false context
        | HandleKind.TypeReference | HandleKind.TypeSpecification ->
            let owner=ownerType image handle context
            match images.TryGetValue owner.Assembly with
            | true,target ->
                match target.Types.TryGetValue owner.Name with true,declaring -> visitType target declaring false (typeContext target declaring owner.Arguments) | _ -> reject "Unresolved deployment type"
            | _ when isTrustedAssembly owner.Assembly -> ()
            // box/ldtoken/initobj may name a generic !n/!!n token or a CLR
            // primitive. These are type operands, not indirect method calls;
            // MethodSpecification still roots supplied deployment arguments.
            | _ when owner.Assembly="" && (owner.Name.StartsWith("!",StringComparison.Ordinal) || owner.Name.StartsWith("primitive:",StringComparison.Ordinal)) -> ()
            | _ -> reject("Unresolved native type dependency "+owner.Assembly+":"+owner.Display)
        | _ -> reject "Unsupported native metadata token"

    and materializeSignatureType reference =
        NativeTypeReference.validate [reference]
        match reference with
        | NativeTypeReference.Named(assembly,name,arguments) ->
            match images.TryGetValue assembly with
            | true,image ->
                match image.Types.TryGetValue name with
                | true,declaring -> visitType image declaring true (typeContext image declaring (List.map ClrType.fromReference arguments))
                | _ -> reject "An admitted handler signature names an unresolved frozen type"
            | _ -> ()
            for argument in arguments do materializeSignatureType argument
        | NativeTypeReference.Vector element | NativeTypeReference.Array(element,_) | NativeTypeReference.ByReference element -> materializeSignatureType element
        | NativeTypeReference.TypeParameter _ | NativeTypeReference.MethodParameter _ -> reject "A native handler signature requires an exact closed generic context"
        | _ -> ()

    and visitMethod (image:FrozenImage) (handle:MethodDefinitionHandle) context =
        let method=image.Reader.GetMethodDefinition handle
        let declaring=method.GetDeclaringType()
        let context={declaringContext image declaring context with MethodArguments=context.MethodArguments}
        let arguments=(context.TypeArguments @ context.MethodArguments) |> List.map _.Reference
        NativeTypeReference.validate arguments
        if phase=AdmissionPhase.Handler &&
           (image.Reader.GetTypeDefinition(declaring).GetGenericParameters().Count<>context.TypeArguments.Length ||
            method.GetGenericParameters().Count<>context.MethodArguments.Length ||
            not(List.forall NativeTypeReference.isClosed arguments)) then
            reject "A native handler requires an exact closed generic context"
        let key=phase,image.Name,MetadataTokens.GetToken(MethodDefinitionHandle.op_Implicit handle : EntityHandle),context
        if visited.Add key then
            if visited.Count>100000 then reject "Native admission method graph exceeds its bound"
            if (method.Attributes &&& MethodAttributes.PinvokeImpl)<>enum 0 || (method.ImplAttributes &&& MethodImplAttributes.CodeTypeMask)<>MethodImplAttributes.IL then
                reject "Unmanaged or runtime-implemented deployment methods are not admitted"
            let signature=method.DecodeSignature(signatures,context)
            if phase=AdmissionPhase.Handler then
                // The owning native decoder can materialize a declared input
                // record without a handler newobj instruction. Its closed
                // signature is a potential receiver boundary too; an abstract
                // or interface type still cannot satisfy a concrete dispatch.
                for parameter in signature.ParameterTypes do materializeSignatureType parameter.Reference
                materializeSignatureType signature.ReturnType.Reference
            if method.RelativeVirtualAddress=0 then
                if (method.Attributes &&& MethodAttributes.Virtual)=enum 0 || (method.Attributes &&& MethodAttributes.Abstract)=enum 0 then
                    reject("Unresolved native dispatch "+MetadataNames.definition image.Reader declaring+"."+image.Reader.GetString method.Name)
                dispatches.TryAdd(key,(image,handle,context)) |> ignore
            else
                method.DecodeSignature(signatures,GenericContext.empty) |> ignore
                visitType image declaring false context
                let body=image.Pe.GetMethodBody method.RelativeVirtualAddress
                if not body.LocalSignature.IsNil then image.Reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(signatures,GenericContext.empty) |> ignore
                let il=body.GetILBytes()
                inspectedBytes<-inspectedBytes+int64 il.Length
                if inspectedBytes>32L*1024L*1024L then reject "Native admission IL graph exceeds its byte bound"
                let mutable offset=0
                while offset<il.Length do
                    let first=int il[offset]
                    offset<-offset+1
                    let value=
                        if first=254 then
                            if offset>=il.Length then reject "Truncated native IL opcode"
                            let value=int16 ((first <<< 8) ||| int il[offset])
                            offset<-offset+1
                            value
                        else int16 first
                    if not(opcodes.ContainsKey value) then reject "Unknown native IL opcode"
                    let code=opcodes[value]
                    if code=OpCodes.Calli || code=OpCodes.Jmp || code=OpCodes.Localloc || code=OpCodes.Cpblk || code=OpCodes.Initblk then reject "Indirect or unsafe native IL is not admitted"
                    let need count=if offset+count>il.Length then reject "Truncated native IL operand"
                    let size=
                        match code.OperandType with
                        | OperandType.InlineNone -> 0
                        | OperandType.ShortInlineBrTarget | OperandType.ShortInlineI | OperandType.ShortInlineVar -> 1
                        | OperandType.InlineVar -> 2
                        | OperandType.InlineI8 | OperandType.InlineR -> 8
                        | OperandType.InlineSwitch ->
                            need 4
                            let count=BitConverter.ToInt32(il,offset)
                            if count<0 || count>100000 then reject "Invalid native IL switch bound"
                            4+4*count
                        | OperandType.InlineMethod | OperandType.InlineField | OperandType.InlineTok | OperandType.InlineType ->
                            need 4
                            let token=MetadataTokens.EntityHandle(BitConverter.ToInt32(il,offset))
                            if phase=AdmissionPhase.Initialization && (code=OpCodes.Ldftn || code=OpCodes.Ldvirtftn) then
                                deferMethod image token context None
                            else
                                try visitMember image token (code=OpCodes.Newobj || code=OpCodes.Ldftn || code=OpCodes.Ldvirtftn) context None
                                with :? Convex.Runtime.HostOperationException as error ->
                                    let caller=MetadataNames.definition image.Reader declaring+"."+image.Reader.GetString method.Name
                                    let message=error.Message+" in "+caller
                                    reject (if message.Length<=4096 then message else message[..4095])
                            if code=OpCodes.Callvirt || code=OpCodes.Ldvirtftn then
                                queueVirtualMember image token context None (phase=AdmissionPhase.Initialization && code=OpCodes.Ldvirtftn)
                            4
                        | OperandType.InlineSig -> reject "Indirect native signatures are not admitted"
                        | OperandType.InlineBrTarget | OperandType.InlineI | OperandType.ShortInlineR | OperandType.InlineString -> 4
                        | _ -> reject "Unsupported native IL operand"
                    need size
                    offset<-offset+size

    // Taking a managed function pointer constructs a delegate; it does not
    // invoke the method. Its declaring type initializer still runs under the
    // import rules, including when an action first touches a lazy static.
    and deferMethod (image:FrozenImage) (handle:EntityHandle) context methodArguments =
        match handle.Kind with
        | HandleKind.MethodDefinition ->
            let methodHandle=MethodDefinitionHandle.op_Explicit handle
            let method=image.Reader.GetMethodDefinition methodHandle
            let callee={declaringContext image (method.GetDeclaringType()) context with MethodArguments=defaultArg methodArguments []}
            NativeTypeReference.validate ((callee.TypeArguments @ callee.MethodArguments) |> List.map _.Reference)
            visitType image (method.GetDeclaringType()) false callee
            deferredCallbacks.TryAdd((image.Name,MetadataTokens.GetToken handle,callee),(image,methodHandle,callee)) |> ignore
        | HandleKind.MethodSpecification ->
            let specification=image.Reader.GetMethodSpecification(MethodSpecificationHandle.op_Explicit handle)
            let arguments=specification.DecodeSignature(signatures,context) |> Seq.toList
            deferMethod image specification.Method context (Some arguments)
        | HandleKind.MemberReference ->
            let reference=image.Reader.GetMemberReference(MemberReferenceHandle.op_Explicit handle)
            let owner=ownerType image reference.Parent context
            match images.TryGetValue owner.Assembly with
            | true,target ->
                match target.Types.TryGetValue owner.Name with
                | true,declaring ->
                    let callee={typeContext target declaring owner.Arguments with MethodArguments=defaultArg methodArguments []}
                    visitType target declaring false callee
                    let name=image.Reader.GetString reference.Name
                    let signature=reference.DecodeMethodSignature(signatures,GenericContext.empty)
                    let methods=target.Reader.GetTypeDefinition(declaring).GetMethods() |> Seq.filter(fun candidate ->
                        let method=target.Reader.GetMethodDefinition candidate
                        let actual=method.DecodeSignature(signatures,GenericContext.empty)
                        target.Reader.GetString method.Name=name && actual.ParameterTypes.Length=signature.ParameterTypes.Length &&
                        actual.GenericParameterCount=signature.GenericParameterCount) |> Seq.toArray
                    if methods.Length=0 then reject "Unresolved native delegate target"
                    for method in methods do deferMethod target (MethodDefinitionHandle.op_Implicit method) callee (Some callee.MethodArguments)
                | _ -> reject "Unresolved native delegate type"
            | _ ->
                // A trusted framework delegate target has no frozen body to
                // defer. Apply the stricter import policy to the target now.
                let previous=deferredFrameworkPointer
                deferredFrameworkPointer<-true
                try visitMember image handle false context methodArguments
                finally deferredFrameworkPointer<-previous
        | _ -> reject "Unsupported native delegate target"

    and queueVirtualMember (image:FrozenImage) (handle:EntityHandle) context methodArguments deferred =
        let add (image:FrozenImage) methodHandle context =
            let method=image.Reader.GetMethodDefinition methodHandle
            if (method.Attributes &&& MethodAttributes.Virtual)<>enum 0 && (method.Attributes &&& MethodAttributes.Final)=enum 0 then
                let key=phase,image.Name,MetadataTokens.GetToken(MethodDefinitionHandle.op_Implicit methodHandle : EntityHandle),context
                let target=if deferred then deferredVirtualDispatches else dispatches
                target.TryAdd(key,(image,methodHandle,context)) |> ignore
                if target.Count>100000 then reject "Native virtual dispatch graph exceeds its bound"
        match handle.Kind with
        | HandleKind.MethodSpecification ->
            let specification=image.Reader.GetMethodSpecification(MethodSpecificationHandle.op_Explicit handle)
            let arguments=specification.DecodeSignature(signatures,context) |> Seq.toList
            queueVirtualMember image specification.Method context (Some arguments) deferred
        | HandleKind.MethodDefinition ->
            let methodHandle=MethodDefinitionHandle.op_Explicit handle
            let method=image.Reader.GetMethodDefinition methodHandle
            let callee={declaringContext image (method.GetDeclaringType()) context with MethodArguments=defaultArg methodArguments []}
            add image methodHandle callee
        | HandleKind.MemberReference ->
            let reference=image.Reader.GetMemberReference(MemberReferenceHandle.op_Explicit handle)
            let owner=ownerType image reference.Parent context
            match images.TryGetValue owner.Assembly with
            | true,target ->
                match target.Types.TryGetValue owner.Name with
                | true,declaring ->
                    let callee={typeContext target declaring owner.Arguments with MethodArguments=defaultArg methodArguments []}
                    let name=image.Reader.GetString reference.Name
                    let expected=reference.DecodeMethodSignature(signatures,GenericContext.empty)
                    for handle in target.Reader.GetTypeDefinition(declaring).GetMethods() do
                        let method=target.Reader.GetMethodDefinition handle
                        let actual=method.DecodeSignature(signatures,GenericContext.empty)
                        if target.Reader.GetString method.Name=name && actual.ParameterTypes.Length=expected.ParameterTypes.Length && actual.GenericParameterCount=expected.GenericParameterCount then
                            add target handle callee
                | _ -> reject "Unresolved frozen virtual receiver"
            | _ -> () // Framework demands cover direct and implicit callbacks.
        | _ -> reject "Unsupported native virtual call token"

    let rec implementsInterface (image:FrozenImage) (declaring:TypeDefinitionHandle) context expected depth =
        if depth>32 then reject "Native interface inheritance exceeds its bound"
        let value=image.Reader.GetTypeDefinition declaring
        let inherited (handle:EntityHandle) =
            let owner=ownerType image handle context
            if owner.Reference=expected then true
            else
                match images.TryGetValue owner.Assembly with
                | true,target ->
                    match target.Types.TryGetValue owner.Name with
                    | true,definition -> implementsInterface target definition (typeContext target definition owner.Arguments) expected (depth+1)
                    | _ -> reject "Unresolved native interface implementation"
                | _ when isTrustedAssembly owner.Assembly -> false
                | _ -> reject "Unresolved native interface dependency"
        (ClrType.named image.Name (MetadataNames.definition image.Reader declaring) context.TypeArguments).Reference=expected ||
        (value.GetInterfaceImplementations() |> Seq.exists(fun handle -> inherited (image.Reader.GetInterfaceImplementation(handle).Interface))) ||
        (not value.BaseType.IsNil && inherited value.BaseType)

    let frozenType reference =
        match reference with
        | NativeTypeReference.Named(assembly,name,arguments) ->
            match images.TryGetValue assembly with
            | true,image ->
                match image.Types.TryGetValue name with
                | true,declaring -> Some(image,declaring,typeContext image declaring (List.map ClrType.fromReference arguments))
                | _ -> reject "Unresolved frozen framework receiver"
            | _ -> None
        | _ -> None

    let frozenParents (image:FrozenImage) declaring context =
        let definition=image.Reader.GetTypeDefinition declaring
        [
            if not definition.BaseType.IsNil then (ownerType image definition.BaseType context).Reference
            for handle in definition.GetInterfaceImplementations() do
                (ownerType image (image.Reader.GetInterfaceImplementation(handle).Interface) context).Reference
        ]

    let frameworkMatches=Dictionary<NativeTypeReference*NativeTypeReference,bool>()
    let rec frameworkAssignable actual expected depth =
        if depth>32 then reject "Native framework callback inheritance exceeds its bound"
        NativeTypeReference.validate [actual;expected]
        if actual=expected || expected=NativeTypeReference.Primitive PrimitiveTypeCode.Object then true
        else
            match frameworkMatches.TryGetValue((actual,expected)) with
            | true,value -> value
            | _ ->
                let variance,parents=
                    match frozenType actual with
                    | Some(image,declaring,context) ->
                        let variance=image.Reader.GetTypeDefinition(declaring).GetGenericParameters() |> Seq.map(fun handle -> image.Reader.GetGenericParameter(handle).Attributes &&& GenericParameterAttributes.VarianceMask) |> Seq.toArray
                        variance,frozenParents image declaring context
                    | None ->
                        match FrameworkTypes.tryDescribe actual with
                        | Some description -> description.Variance,description.Parents
                        | None -> [||],[]
                let sameGeneric=
                    match actual,expected with
                    | NativeTypeReference.Named(a,n,left),NativeTypeReference.Named(b,m,right) when a=b && n=m && left.Length=right.Length && left.Length=variance.Length ->
                        List.zip left right |> List.mapi(fun index (left,right) ->
                            match variance[index] with
                            | GenericParameterAttributes.Covariant -> frameworkAssignable left right (depth+1)
                            | GenericParameterAttributes.Contravariant -> frameworkAssignable right left (depth+1)
                            | _ -> left=right) |> List.forall id
                    | NativeTypeReference.Vector left,NativeTypeReference.Vector right -> frameworkAssignable left right (depth+1)
                    | _ -> false
                let result=sameGeneric || (parents |> List.exists(fun parent -> frameworkAssignable parent expected (depth+1)))
                // Cache actual receiver comparisons and successful ancestor
                // matches. Failed intermediate generic-argument comparisons
                // are search scratch, not new receivers: retaining their full
                // cross-product can exhaust the graph before valid roots are
                // reviewed. The receiver-pair and inheritance bounds still
                // govern every comparison and recursive traversal.
                if depth=0 || result then
                    if frameworkMatches.Count>=500000 then reject "Native framework receiver graph exceeds its bound"
                    frameworkMatches[(actual,expected)]<-result
                result

    let frameworkSlots=Dictionary<NativeTypeReference,FrameworkSlot array>()
    let slotsFor reference =
        match frameworkSlots.TryGetValue reference with
        | true,slots -> slots
        | _ ->
            let seen=HashSet<NativeTypeReference>()
            let slots=ResizeArray<FrameworkSlot>()
            let rec collect reference depth =
                if depth>32 then reject "Native framework callback inheritance exceeds its bound"
                if seen.Add reference then
                    if seen.Count>10000 then reject "Native framework slot graph exceeds its bound"
                    match frozenType reference with
                    | Some(image,declaring,context) ->
                        for parent in frozenParents image declaring context do collect parent (depth+1)
                    | None ->
                        match FrameworkTypes.tryDescribe reference with
                        | Some description ->
                            slots.AddRange description.Slots
                            for parent in description.Parents do collect parent (depth+1)
                        | None -> () // Canonical SDK/FSharp callbacks have their own graph.
            collect reference 0
            let result=slots |> Seq.distinct |> Seq.toArray
            frameworkSlots.Add(reference,result)
            result

    let declarationSlot (image:FrozenImage) (handle:EntityHandle) =
        match handle.Kind with
        | HandleKind.MethodDefinition ->
            let method=image.Reader.GetMethodDefinition(MethodDefinitionHandle.op_Explicit handle)
            let signature=method.DecodeSignature(signatures,GenericContext.empty)
            {Name=image.Reader.GetString method.Name;ParameterCount=signature.ParameterTypes.Length;GenericParameterCount=signature.GenericParameterCount}
        | HandleKind.MemberReference ->
            let method=image.Reader.GetMemberReference(MemberReferenceHandle.op_Explicit handle)
            let signature=method.DecodeMethodSignature(signatures,GenericContext.empty)
            {Name=image.Reader.GetString method.Name;ParameterCount=signature.ParameterTypes.Length;GenericParameterCount=signature.GenericParameterCount}
        | _ -> reject "Unresolved native framework method implementation"

    // Generic variance can change arguments, but cannot change a receiver's
    // assembly/type head. Index every actual ancestor head before the exact
    // assignability check. Unrelated constructors consume no receiver-pair
    // budget; matching candidates still use the original bounded checks.
    let frameworkCandidateHeads=Dictionary<AdmissionPhase*string*string,ResizeArray<int>>()
    let frameworkPhaseCandidates=Dictionary<AdmissionPhase,ResizeArray<int>>()
    let mutable indexedFrameworkConstructors=0
    let indexFrameworkConstructors count =
        for index in indexedFrameworkConstructors..count-1 do
            let selected,assembly,token,context=constructorOrder[index]
            let image=images[assembly]
            let declaring=MetadataTokens.TypeDefinitionHandle(token &&& 0x00ffffff)
            let definition=image.Reader.GetTypeDefinition declaring
            if (definition.Attributes &&& TypeAttributes.Abstract)=enum 0 && (definition.Attributes &&& TypeAttributes.Interface)=enum 0 then
                let candidates=match frameworkPhaseCandidates.TryGetValue selected with true,values -> values | _ -> let values=ResizeArray() in frameworkPhaseCandidates.Add(selected,values);values
                candidates.Add index
                let heads=HashSet<string*string>()
                let seen=HashSet<NativeTypeReference>()
                let rec collect reference depth =
                    if depth>32 then reject "Native framework callback inheritance exceeds its bound"
                    if seen.Add reference then
                        if seen.Count>10000 then reject "Native framework receiver ancestry exceeds its bound"
                        match reference with
                        | NativeTypeReference.Named(owner,name,_) -> heads.Add(owner,name) |> ignore
                        | _ -> ()
                        let parents=
                            match frozenType reference with
                            | Some(owner,kind,context) -> frozenParents owner kind context
                            | None -> FrameworkTypes.tryDescribe reference |> Option.map _.Parents |> Option.defaultValue []
                        for parent in parents do collect parent (depth+1)
                collect ((ClrType.named assembly (MetadataNames.definition image.Reader declaring) context.TypeArguments).Reference) 0
                for owner,name in heads do
                    let key=selected,owner,name
                    let values=match frameworkCandidateHeads.TryGetValue key with true,values -> values | _ -> let values=ResizeArray() in frameworkCandidateHeads.Add(key,values);values
                    values.Add index
        indexedFrameworkConstructors<-count

    let resolveFrameworkCallbacks () =
        let count=constructorOrder.Count
        indexFrameworkConstructors count
        for demand in frameworkDemands |> Seq.toArray do
            let first=match frameworkCursors.TryGetValue demand with true,index -> index | _ -> 0
            let candidates=
                match demand.Receiver with
                | NativeTypeReference.Named(owner,name,_) -> match frameworkCandidateHeads.TryGetValue((demand.Phase,owner,name)) with true,values -> values.ToArray() | _ -> [||]
                | NativeTypeReference.Primitive PrimitiveTypeCode.Object -> match frameworkPhaseCandidates.TryGetValue demand.Phase with true,values -> values.ToArray() | _ -> [||]
                | _ -> [||]
            for index in candidates |> Array.filter(fun index -> index>=first && index<count) do
                let selected,assembly,token,context=constructorOrder[index]
                if selected=demand.Phase then
                    let image=images[assembly]
                    let declaring=MetadataTokens.TypeDefinitionHandle(token &&& 0x00ffffff)
                    let definition=image.Reader.GetTypeDefinition declaring
                    if (definition.Attributes &&& TypeAttributes.Abstract)=enum 0 && (definition.Attributes &&& TypeAttributes.Interface)=enum 0 then
                        let reference=(ClrType.named assembly (MetadataNames.definition image.Reader declaring) context.TypeArguments).Reference
                        let slots=slotsFor reference |> Array.filter(fun slot -> demand.Members |> Option.forall(Set.contains slot.Name))
                        // A receiver with no demanded slot cannot call back.
                        // Exclude it before charging an exact receiver comparison.
                        if slots.Length>0 then
                            frameworkPairs<-frameworkPairs+1
                            if frameworkPairs>500000 then
                                let name=match demand.Receiver with NativeTypeReference.Named(_,name,_) -> name | _ -> string demand.Receiver
                                let members=demand.Members |> Option.map(String.concat ",") |> Option.defaultValue "all"
                                reject (sprintf "Native framework callback receiver pairs exceed their bound (constructors=%d, demands=%d, receiver=%s, members=%s, candidates=%d)" count frameworkDemands.Count name members candidates.Length)
                        if slots.Length>0 && frameworkAssignable reference demand.Receiver 0 then
                            let selectedSlot slot=slots |> Array.contains slot
                            let methodArguments count =
                                if count=0 then []
                                elif count=demand.MethodArguments.Length then List.map ClrType.fromReference demand.MethodArguments
                                else reject "A native framework callback requires an exact closed generic method context"
                            let rec review (image:FrozenImage) declaring context depth =
                                if depth>32 then reject "Native framework callback inheritance exceeds its bound"
                                let definition=image.Reader.GetTypeDefinition declaring
                                let aliases=Dictionary<int,ResizeArray<FrameworkSlot>>()
                                for handle in definition.GetMethodImplementations() do
                                    let implementation=image.Reader.GetMethodImplementation handle
                                    let slot=declarationSlot image implementation.MethodDeclaration
                                    if selectedSlot slot then
                                        match implementation.MethodBody.Kind with
                                        | HandleKind.MethodDefinition ->
                                            let token=MetadataTokens.GetToken implementation.MethodBody
                                            let values=match aliases.TryGetValue token with true,values -> values | _ -> let values=ResizeArray() in aliases.Add(token,values);values
                                            values.Add slot
                                        | HandleKind.MemberReference ->
                                            let arguments=methodArguments slot.GenericParameterCount
                                            inPhase demand.Phase (fun () -> visitMember image implementation.MethodBody false context (Some arguments))
                                        | _ -> reject "Unresolved native framework method body"
                                for handle in definition.GetMethods() do
                                    let method=image.Reader.GetMethodDefinition handle
                                    if (method.Attributes &&& MethodAttributes.Static)=enum 0 && (method.Attributes &&& MethodAttributes.Abstract)=enum 0 then
                                        let name=image.Reader.GetString method.Name
                                        let signature=method.DecodeSignature(signatures,GenericContext.empty)
                                        let token=MetadataTokens.GetToken(MethodDefinitionHandle.op_Implicit handle : EntityHandle)
                                        let implicitSlot=slots |> Array.exists(fun slot ->
                                            (name=slot.Name || name.EndsWith("."+slot.Name,StringComparison.Ordinal)) &&
                                            signature.ParameterTypes.Length=slot.ParameterCount && signature.GenericParameterCount=slot.GenericParameterCount)
                                        if implicitSlot || aliases.ContainsKey token then
                                            let context={context with MethodArguments=methodArguments signature.GenericParameterCount}
                                            inPhase demand.Phase (fun () -> visitMethod image handle context)
                                if not definition.BaseType.IsNil then
                                    let parent=ownerType image definition.BaseType context
                                    match frozenType parent.Reference with
                                    | Some(target,baseType,baseContext) -> review target baseType baseContext (depth+1)
                                    | None -> ()
                            try review image declaring context 0
                            with :? Convex.Runtime.HostOperationException as error ->
                                let origin=match frameworkSources.TryGetValue demand with true,value -> value | _ -> "captured framework receiver"
                                let message=error.Message+" via "+origin
                                reject (if message.Length<=4096 then message else message[..4095])
            frameworkCursors[demand]<-count

    let resolveDispatches () =
        let mutable pending=true
        let mutable rounds=0
        while pending do
            rounds<-rounds+1
            if rounds>1024 then reject "Native closed dispatch graph exceeds its bound"
            let beforeConstructors=constructorOrder.Count
            let beforeDispatches=dispatches.Count
            for KeyValue(key,(image,handle,context)) in dispatches |> Seq.toArray do
                let first=match dispatchCursors.TryGetValue key with true,index -> index | _ -> 0
                // Visits below can discover more receivers. Advance only over
                // this snapshot; the next round owns those new pairs.
                let count=constructorOrder.Count
                if first<count then
                    let method=image.Reader.GetMethodDefinition handle
                    let owner=MetadataNames.definition image.Reader (method.GetDeclaringType())
                    let expected=NativeTypeReference.Named(image.Name,owner,context.TypeArguments |> List.map _.Reference)
                    let name=image.Reader.GetString method.Name
                    let signature=method.DecodeSignature(signatures,GenericContext.empty)
                    let dispatchPhase,_,_,_=key
                    for index in first..count-1 do
                        let constructionPhase,assembly,token,constructionContext=constructorOrder[index]
                        let target=images[assembly]
                        let declaring=MetadataTokens.TypeDefinitionHandle(token &&& 0x00ffffff)
                        let value=target.Reader.GetTypeDefinition declaring
                        if constructionPhase=dispatchPhase &&
                           (value.Attributes &&& TypeAttributes.Abstract)=enum 0 &&
                           (value.Attributes &&& TypeAttributes.Interface)=enum 0 &&
                           implementsInterface target declaring constructionContext expected 0 then
                            let rec candidates (target:FrozenImage) declaring (constructionContext:GenericContext) depth =
                                if depth>32 then reject "Native virtual receiver inheritance exceeds its bound"
                                let value=target.Reader.GetTypeDefinition declaring
                                for handle in value.GetMethods() do
                                    let candidate=target.Reader.GetMethodDefinition handle
                                    let candidateName=target.Reader.GetString candidate.Name
                                    let actual=candidate.DecodeSignature(signatures,GenericContext.empty)
                                    if (candidate.Attributes &&& MethodAttributes.Abstract)=enum 0 &&
                                       (candidate.Attributes &&& MethodAttributes.Static)=enum 0 &&
                                       (candidateName=name || candidateName.EndsWith("."+name,StringComparison.Ordinal)) &&
                                       actual.ParameterTypes.Length=signature.ParameterTypes.Length && actual.GenericParameterCount=signature.GenericParameterCount then
                                        let candidateContext={constructionContext with MethodArguments=context.MethodArguments}
                                        inPhase dispatchPhase (fun () -> visitMethod target handle candidateContext)
                                        matchedDispatches.Add key |> ignore
                                if not value.BaseType.IsNil then
                                    let parent=ownerType target value.BaseType constructionContext
                                    match frozenType parent.Reference with
                                    | Some(baseImage,baseType,baseContext) -> candidates baseImage baseType baseContext (depth+1)
                                    | None -> ()
                            candidates target declaring constructionContext 0
                    dispatchCursors[key]<-count
            pending<-constructorOrder.Count<>beforeConstructors || dispatches.Count<>beforeDispatches
    let requireResolvedDispatches deferHandlerUnresolved =
        match dispatches |> Seq.tryFind(fun entry ->
            let phase,_,_,_=entry.Key
            not(matchedDispatches.Contains entry.Key) &&
            (phase=AdmissionPhase.Initialization || not deferHandlerUnresolved)) with
        | Some entry ->
            let image,handle,_=entry.Value
            let method=image.Reader.GetMethodDefinition handle
            reject("No constructed frozen target for native interface "+MetadataNames.definition image.Reader (method.GetDeclaringType())+"."+image.Reader.GetString method.Name)
        | None -> ()

    let importGraphState () =
        visited.Count,constructors.Count,dispatches.Count,deferredCallbacks.Count,deferredVirtualDispatches.Count,frameworkDemands.Count,deferredFrameworkDemands.Count,eagerImportCallbacks

    let mutable settledImportGraph = None

    let resolveImportCallbacks deferHandlerUnresolved =
        // Trusted higher-order code can eagerly invoke any reachable callback.
        // Interface targets may themselves discover such code, so callbacks
        // and closed dispatch must reach one mutual fixed point before the
        // final unresolved-target check. Registration-only modules remain lazy.
        // The metadata graph only grows. Repeated capture walks can revisit
        // thousands of static values without adding a method or receiver; an
        // unchanged graph retains its completed fixed point. This caches no
        // captured values and never skips the phase-specific final check.
        if settledImportGraph<>Some(importGraphState()) then
            let mutable pending=true
            let mutable rounds=0
            while pending do
                rounds<-rounds+1
                if rounds>1024 then reject "Native import callback graph exceeds its bound"
                let before=importGraphState()
                if eagerImportCallbacks then
                    for KeyValue(key,(image,method,context)) in deferredCallbacks |> Seq.toArray do
                        if reviewedImportCallbacks.Add key then
                            inPhase AdmissionPhase.Initialization (fun () -> visitMethod image method context)
                    for demand in deferredFrameworkDemands |> Seq.toArray do
                        addFrameworkDemand demand.Phase demand.Receiver demand.Members demand.MethodArguments
                    for KeyValue(key,value) in deferredVirtualDispatches |> Seq.toArray do
                        dispatches.TryAdd(key,value) |> ignore
                resolveDispatches()
                resolveFrameworkCallbacks()
                pending<-before<>importGraphState()
            settledImportGraph<-Some(importGraphState())
        requireResolvedDispatches deferHandlerUnresolved

    // Module initializers can execute before an exported getter. Validate this
    // global entry before AssemblyLoadContext receives any deployment bytes.
    do
        for image in images.Values do
            match image.Types.TryGetValue "<Module>" with
            | true,declaring -> visitType image declaring false GenericContext.empty
            | _ -> ()
        resolveImportCallbacks false

    member _.ValidateExports(exports:string array) =
        try
            if exports.Length>10000 then reject "Native admission export review exceeds its bound"
            let image=images[primaryName]
            let identities=HashSet<string>(StringComparer.Ordinal)
            for identity in exports do
                if isNull identity || identity.Length>2048 || not(identities.Add identity) then reject "An exact unique native export identity is required"
                let split=identity.LastIndexOf(':')
                if split<=0 || split=identity.Length-1 || identity.IndexOf(':')<>split then reject "A module:member native export identity is required"
                let moduleName=identity[..split-1].Replace('/','.')
                let moduleName=if moduleName.EndsWith(".js",StringComparison.Ordinal) then moduleName[..moduleName.Length-4] else moduleName
                let memberName=identity[split+1..]
                let declaring=
                    match image.Types.TryGetValue moduleName with
                    | true,value -> value
                    | _ -> reject("Native export type not found: "+moduleName)
                let properties=image.Reader.GetTypeDefinition(declaring).GetProperties()
                               |> Seq.filter(fun handle -> image.Reader.GetString(image.Reader.GetPropertyDefinition(handle).Name)=memberName) |> Seq.toArray
                if properties.Length<>1 then reject "Native export property missing or ambiguous"
                let getter=image.Reader.GetPropertyDefinition(properties[0]).GetAccessors().Getter
                if getter.IsNil then reject "Native export requires a getter"
                let method=image.Reader.GetMethodDefinition getter
                if (method.Attributes &&& MethodAttributes.MemberAccessMask)<>MethodAttributes.Public || (method.Attributes &&& MethodAttributes.Static)=enum 0 ||
                   method.DecodeSignature(signatures,GenericContext.empty).ParameterTypes.Length<>0 then reject "Native export requires a public static non-indexed getter"
                visitMethod image getter (typeContext image declaring [])
            resolveImportCallbacks false
            visited.Count,inspectedBytes
        with
        | :? HostOperationException -> reraise()
        | _ -> reject "Native deployment metadata is invalid or unsupported"

    member _.ValidateGetter(getter:MethodInfo) =
        try
            match images.TryGetValue(getter.Module.Assembly.GetName().Name) with
            | true,image ->
                visitMethod image (MetadataTokens.MethodDefinitionHandle(getter.MetadataToken &&& 0x00ffffff)) (reflectedContext getter.DeclaringType (getter.GetGenericArguments()))
                resolveImportCallbacks false
            | _ -> reject "A native export getter must belong to its frozen deployment closure"
        with
        | :? HostOperationException -> reraise()
        | _ -> reject "Native deployment metadata is invalid or unsupported"

    member _.ValidateCaptures(root:obj,resolveStaticField:NativeStaticField -> obj) =
        // Inspect captured state without calling property getters, enumerators,
        // user equality or other application code. Trusted SDK/FSharp wrappers
        // may retain a user closure; their instance fields remain part of the
        // reachability walk. Each invocation owns a fresh assembly context.
        let seen=HashSet<obj>(Collections.Generic.ReferenceEqualityComparer.Instance)
        let mutable fields=0
        let rec captured (value:obj) depth =
            if depth>128 then reject "Native handler capture depth exceeds its bound"
            if not(isNull value) && seen.Add value then
                if seen.Count>100000 then reject "Native handler capture graph exceeds its bound"
                let kind=value.GetType()
                if not(kind.IsPrimitive || kind.IsEnum || kind=typeof<string> || kind=typeof<Type>) then
                    match value with
                    | :? Type -> ()
                    | :? Delegate as callback ->
                        for entry in callback.GetInvocationList() do
                            let method=entry.Method
                            match images.TryGetValue(method.Module.Assembly.GetName().Name) with
                            | true,image ->
                                inPhase AdmissionPhase.Handler (fun () ->
                                    visitMethod image (MetadataTokens.MethodDefinitionHandle(method.MetadataToken &&& 0x00ffffff)) (reflectedContext method.DeclaringType (method.GetGenericArguments())))
                            | _ when method.Module.Assembly.GetName().Name="Convex.Runtime" || method.Module.Assembly.GetName().Name="Convex.FSharp" || method.Module.Assembly.GetName().Name="FSharp.Core" ->
                                // Owned registration/async wrappers retain their
                                // frozen callback fields; they are not calls made
                                // by deployment code into executor entrypoints.
                                ()
                            | _ when isTrustedAssembly(method.Module.Assembly.GetName().Name) ->
                                inPhase AdmissionPhase.Handler (fun () ->
                                    let owner=ClrType.reflected method.DeclaringType
                                    let parameters=method.GetParameters() |> Array.map(fun parameter -> ClrType.reflected parameter.ParameterType) |> Array.toList
                                    if not(allowed owner method.Name (Some parameters)) then forbiddenCall owner method.Name (Some parameters)
                                    let arguments=method.GetGenericArguments() |> Array.map ClrType.reflected |> Array.toList
                                    recordFrameworkRules owner method.Name (not method.IsStatic) parameters arguments)
                            | _ -> reject "A native handler delegate is outside its frozen closure"
                            captured entry.Target (depth+1)
                    | :? Array as values ->
                        if values.Length>100000 then reject "Native handler capture array exceeds its bound"
                        for item in values do captured item (depth+1)
                    | _ ->
                        match images.TryGetValue(kind.Assembly.GetName().Name) with
                        | true,image ->
                            let definition=if kind.IsGenericType then kind.GetGenericTypeDefinition() else kind
                            match image.Types.TryGetValue definition.FullName with
                            | true,declaring -> inPhase AdmissionPhase.Handler (fun () -> visitType image declaring true (reflectedContext kind [||]))
                            | _ -> reject "A captured native type is absent from its frozen closure"
                        | _ when isTrustedAssembly(kind.Assembly.GetName().Name) -> ()
                        | _ -> reject "A captured native dependency is not admitted"
                        let mutable declaring=kind
                        while not(isNull declaring) && declaring<>typeof<obj> do
                            for field in captureFields declaring do
                                fields<-fields+1
                                if fields>500000 then reject "Native handler capture fields exceed their bound"
                                if unsafeCaptureField field then reject "Native handler pointer captures are not admitted"
                                captured (field.GetValue value) (depth+1)
                            declaring<-declaring.BaseType
        try
            if isNull root then reject "A native execution capture root is required"
            captured root 0
            let resolvedStatics=HashSet<NativeStaticField>()
            // Optimized continuations may retain concrete receivers in
            // singleton static fields. Drain those captures before the
            // final Handler target check. Initializers remain strict:
            // resolveStaticField may execute their already-reviewed code.
            resolveImportCallbacks true
            let mutable pending=true
            while pending do
                let pendingFields=capturedStatics |> Seq.filter(fun key -> not(resolvedStatics.Contains key)) |> Seq.toArray
                pending<-pendingFields.Length>0
                for field in pendingFields do
                    resolvedStatics.Add field |> ignore
                    captured (resolveStaticField field) 0
                    // This capture can expose more initializers or callback
                    // receivers. Settle them before another static read.
                    resolveImportCallbacks true
            resolveImportCallbacks false
        with
        | :? HostOperationException -> reraise()
        | _ -> reject "Native handler captures are invalid or unsupported"

    interface IDisposable with
        member _.Dispose()=
            captureFieldStructure.Clear()
            capturePointerStructure.Clear()
            for image in images.Values do (image :> IDisposable).Dispose()

/// Metadata-only compiler review. No assembly is loaded and no getter or
/// application function executes. Activation still belongs to Convex admission.
module NativeAssemblyReview =
    let review (primary:byte[]) (dependencies:byte[] array) (exports:string array) =
        use admission=new AssemblyAdmission(primary,dependencies,NativeAssemblyAdmissionProfile.DeterministicFirstParty)
        admission.ValidateExports exports
