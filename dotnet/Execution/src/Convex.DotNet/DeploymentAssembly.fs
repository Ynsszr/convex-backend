namespace Convex.DotNet.Execution

open System
open System.IO
open System.Reflection
open System.Runtime.Loader
open System.Security.Cryptography
open Convex.Runtime

type NativeExport =
    | Function of IRegisteredFunction
    | HttpAction of IRegisteredFunction

/// A fresh collectible load context isolates invocation statics and dependencies.
/// It is not a security sandbox; the backend owns the process sandbox and limits.
type DeploymentAssembly(path: string, expectedDigest: string, dependencies: (string*string) array, kind: string, ?frontendAssemblies: Assembly array) =
    let fullPath=Path.GetFullPath path
    let frozenBytes (path: string) (expectedDigest: string) =
        let fullPath=Path.GetFullPath path
        if not (Path.IsPathFullyQualified path) || not (File.Exists fullPath) then
            invalidArg "path" "An absolute deployment assembly is required"
        let info=FileInfo fullPath
        if info.LinkTarget<>null then invalidArg "path" "A deployment assembly cannot be a symbolic link"
        if isNull expectedDigest || expectedDigest.Length<>64 || expectedDigest |> Seq.exists(fun value -> not((value>='0' && value<='9') || (value>='a' && value<='f'))) then
            invalidArg "expectedDigest" "An exact lowercase SHA-256 digest is required"
        use input=new FileStream(fullPath,FileMode.Open,FileAccess.Read,FileShare.Read)
        let maximumArtifact=64L*1024L*1024L
        if input.Length<=0L || input.Length>maximumArtifact then invalidArg "path" "Deployment artifacts must be nonempty and at most 64 MiB"
        use frozen=new MemoryStream()
        let buffer=Array.zeroCreate<byte> 65536
        let mutable reading=true
        while reading do
            let count=input.Read(buffer,0,buffer.Length)
            if count=0 then reading<-false
            else
                if frozen.Length+int64 count>maximumArtifact then invalidArg "path" "Deployment artifact exceeds its byte bound"
                frozen.Write(buffer,0,count)
        let bytes=frozen.ToArray()
        let digest=SHA256.HashData(bytes) |> Convert.ToHexStringLower
        if digest<>expectedDigest then invalidOp "Deployment assembly digest mismatch"
        bytes
    let assemblyBytes = frozenBytes path expectedDigest
    let dependencyBytes=Collections.Generic.Dictionary<string,byte[]>(StringComparer.Ordinal)
    let managedDependencies=Collections.Generic.Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase)
    do
        if dependencies.Length>64 then invalidArg "dependencies" "At most 64 deployment dependencies are supported"
        for dependency,digest in dependencies do
            let identity=Path.GetFullPath dependency
            if dependencyBytes.ContainsKey identity then invalidArg "dependencies" "Duplicate deployment dependency path"
            let bytes=frozenBytes dependency digest
            dependencyBytes.Add(identity,bytes)
            if Path.GetExtension(identity).Equals(".dll",StringComparison.OrdinalIgnoreCase) then
                let name=Path.GetFileNameWithoutExtension(identity)
                if managedDependencies.ContainsKey name then invalidArg "dependencies" "Duplicate managed deployment dependency name"
                managedDependencies.Add(name,bytes)

    let requireDependency path =
        let fullPath=Path.GetFullPath path
        match dependencyBytes.TryGetValue fullPath with
        | true,bytes -> bytes
        | _ -> invalidOp "A deployment dependency is missing from the admitted digest manifest"

    let profile =
        match kind with
        | "query" | "mutation" | "describe" -> NativeAssemblyAdmissionProfile.DeterministicFirstParty
        | "action" | "httpAction" -> NativeAssemblyAdmissionProfile.ManagedActionFirstParty
        | _ -> invalidArg "kind" "An owning native function or declaration kind is required"
    let admission=new AssemblyAdmission(assemblyBytes,dependencyBytes.Values,profile)
    let actionAdmission=lazy(new AssemblyAdmission(assemblyBytes,dependencyBytes.Values,NativeAssemblyAdmissionProfile.ManagedActionFirstParty))
    let mutable resolveCapturedField : NativeStaticField -> obj = fun _ -> invalidOp "Native assembly loading is incomplete"
    let validateExecution getter actualKind (handler:Delegate) =
        if kind<>"describe" && actualKind<>kind then
            invalidOp "The native registration kind differs from its owning invocation"
        let selected =
            if kind="describe" && (actualKind="action" || actualKind="httpAction") then
                let selected=actionAdmission.Value
                // Retained framework callbacks (for example async Using)
                // belong to this exact getter's handler profile. Re-review
                // its frozen metadata, never reuse deterministic body caches.
                selected.ValidateGetter getter
                selected
            else admission
        selected.ValidateCaptures(handler,resolveCapturedField)
    let validateValue getter (value:obj) =
        match value with
        | :? IRegisteredFunction as definition -> validateExecution getter (ContractNames.functionKind definition.Kind) definition.ExecutionHandler
        | :? IComponentDefinition as definition ->
            // Initializer callbacks are deferred by declaration construction,
            // but always use deterministic admission before the owner runs one.
            if kind<>"describe" then invalidOp "Component declarations require the import executor"
            admission.ValidateCaptures(definition,resolveCapturedField)
        | _ -> ()

    let resolver=AssemblyDependencyResolver(fullPath)
    let sharedAssemblies=Array.append [|typeof<IRegisteredFunction>.Assembly; typeof<Async<unit>>.Assembly|] (defaultArg frontendAssemblies [||])
    let context=
        { new AssemblyLoadContext("convex-invocation-"+Guid.NewGuid().ToString("N"),true) with
            override _.Load name =
                match sharedAssemblies |> Array.tryFind(fun item -> item.GetName().Name=name.Name) with
                | Some assembly -> assembly
                | None ->
                    // Resolve by the frozen assembly-name map, not whichever copy
                    // AssemblyDependencyResolver finds beside one project output.
                    // A multi-project graph can have identical dependency bytes in
                    // different bin directories; only admitted bytes are loaded.
                    let bytes =
                        match managedDependencies.TryGetValue name.Name with
                        | true, bytes -> Some bytes
                        | _ -> None
                    match bytes with
                    | None -> null
                    | Some bytes ->
                        use stream=new MemoryStream(bytes,false)
                        let loaded=base.LoadFromStream stream
                        if not(loaded.GetName().Name.Equals(name.Name,StringComparison.OrdinalIgnoreCase)) then
                            invalidOp "Managed deployment dependency identity differs from its admitted filename"
                        loaded
            override _.LoadUnmanagedDll name =
                invalidOp "Native first-party admission refuses unmanaged deployment code" }
    let assembly=
        use stream=new MemoryStream(assemblyBytes,false)
        context.LoadFromStream stream
    let frozenOwner name =
        if name<>assembly.GetName().Name && not(managedDependencies.ContainsKey name) then
            invalidOp "A captured native owner is outside its frozen deployment closure"
        let owner=
            context.Assemblies |> Seq.tryFind(fun value -> value.GetName().Name=name)
            |> Option.defaultWith(fun () -> context.LoadFromAssemblyName(AssemblyName name))
        if owner.GetName().Name<>name || AssemblyLoadContext.GetLoadContext(owner)<>context then
            invalidOp "A captured native owner differs from its frozen assembly identity"
        owner

    let argumentOwner name =
        match sharedAssemblies |> Array.tryFind(fun owner -> owner.GetName().Name=name) with
        | Some owner -> owner
        | None when name=assembly.GetName().Name || managedDependencies.ContainsKey name -> frozenOwner name
        | None ->
            match NativeFrameworkAssemblies.tryPath name with
            | Some path ->
                let owner=
                    AssemblyLoadContext.Default.Assemblies |> Seq.tryFind(fun candidate -> candidate.GetName().Name=name)
                    |> Option.defaultWith(fun () -> AssemblyLoadContext.Default.LoadFromAssemblyPath path)
                if owner.GetName().Name<>name || Path.GetFullPath(owner.Location)<>Path.GetFullPath(path) then
                    invalidOp "A native generic runtime owner differs from its admitted path"
                owner
            | None -> invalidOp "A native generic argument requires an admitted assembly"

    let rec resolveType = function
        | NativeTypeReference.Named(name,identity,arguments) ->
            let definition=(argumentOwner name).GetType(identity,true,false)
            let resolved=arguments |> List.map resolveType |> List.toArray
            let expected=if definition.IsGenericTypeDefinition then definition.GetGenericArguments().Length else 0
            if expected<>resolved.Length then invalidOp "A native generic type requires an exact closed argument vector"
            let result=if expected=0 then definition else definition.MakeGenericType resolved
            if result.ContainsGenericParameters then invalidOp "An open native generic type is not admitted"
            result
        | NativeTypeReference.Primitive code ->
            match code with
            | Reflection.Metadata.PrimitiveTypeCode.Boolean -> typeof<bool>
            | Reflection.Metadata.PrimitiveTypeCode.Char -> typeof<char>
            | Reflection.Metadata.PrimitiveTypeCode.SByte -> typeof<sbyte>
            | Reflection.Metadata.PrimitiveTypeCode.Byte -> typeof<byte>
            | Reflection.Metadata.PrimitiveTypeCode.Int16 -> typeof<int16>
            | Reflection.Metadata.PrimitiveTypeCode.UInt16 -> typeof<uint16>
            | Reflection.Metadata.PrimitiveTypeCode.Int32 -> typeof<int32>
            | Reflection.Metadata.PrimitiveTypeCode.UInt32 -> typeof<uint32>
            | Reflection.Metadata.PrimitiveTypeCode.Int64 -> typeof<int64>
            | Reflection.Metadata.PrimitiveTypeCode.UInt64 -> typeof<uint64>
            | Reflection.Metadata.PrimitiveTypeCode.Single -> typeof<single>
            | Reflection.Metadata.PrimitiveTypeCode.Double -> typeof<double>
            | Reflection.Metadata.PrimitiveTypeCode.String -> typeof<string>
            | Reflection.Metadata.PrimitiveTypeCode.Object -> typeof<obj>
            | Reflection.Metadata.PrimitiveTypeCode.IntPtr -> typeof<nativeint>
            | Reflection.Metadata.PrimitiveTypeCode.UIntPtr -> typeof<unativeint>
            | _ -> invalidOp "An unsupported native generic primitive is not admitted"
        | NativeTypeReference.Vector element -> (resolveType element).MakeArrayType()
        | NativeTypeReference.Array(element,rank) -> (resolveType element).MakeArrayType rank
        | NativeTypeReference.ByReference _ -> invalidOp "A native generic by-reference argument is not admitted"
        | NativeTypeReference.TypeParameter _ | NativeTypeReference.MethodParameter _ ->
            invalidOp "An unbound native generic argument is not admitted"

    // Closed frozen reflection structure only. No static values or admission decisions.
    let capturedFieldStructure=Collections.Generic.Dictionary<NativeStaticField,FieldInfo>()

    do
        resolveCapturedField <- fun selected ->
            NativeTypeReference.validate selected.TypeArguments
            if not(List.forall NativeTypeReference.isClosed selected.TypeArguments) then
                invalidOp "A captured native static requires an exact closed generic context"
            let field =
                match capturedFieldStructure.TryGetValue selected with
                | true,field -> field
                | _ ->
                    let owner=frozenOwner selected.Assembly
                    let definition=owner.ManifestModule.ResolveField selected.Token
                    if isNull definition || not definition.IsStatic then
                        invalidOp "A captured native static requires an exact closed frozen field"
                    let arguments=selected.TypeArguments |> List.map resolveType |> List.toArray
                    let declaring=definition.DeclaringType
                    let expected=if declaring.IsGenericTypeDefinition then declaring.GetGenericArguments().Length else 0
                    if expected<>arguments.Length then
                        invalidOp "A captured native static requires an exact closed argument vector"
                    let closed=if expected=0 then declaring else declaring.MakeGenericType arguments
                    if closed.ContainsGenericParameters then
                        invalidOp "A captured native static requires an exact closed frozen field"
                    let fields=closed.GetFields(BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly)
                    let matched=fields |> Array.filter(fun field -> field.Module=owner.ManifestModule && field.MetadataToken=selected.Token)
                    if matched.Length<>1 || not matched[0].IsStatic || matched[0].DeclaringType.ContainsGenericParameters then
                        invalidOp "The captured native field differs from its exact frozen identity"
                    let field=matched[0]
                    if capturedFieldStructure.Count<4096 then capturedFieldStructure.Add(selected,field)
                    field
            // Re-read the actual mutable value on EVERY original capture walk.
            field.GetValue(null)

    member _.Resolve (functionPath:string) =
        let split=functionPath.LastIndexOf(':')
        if split<=0 || split=functionPath.Length-1 then invalidArg "functionPath" "A module:function identity is required"
        let modulePath=functionPath[..split-1]
        let moduleName=(if modulePath.EndsWith(".js",StringComparison.Ordinal) then modulePath[..modulePath.Length-4] else modulePath).Replace('/','.')
        let functionName=functionPath[split+1..]
        let declaring=assembly.GetType(moduleName,false,false)
        if isNull declaring then invalidOp "The native deployment does not export this module"
        let property=declaring.GetProperty(functionName,BindingFlags.Public ||| BindingFlags.Static)
        if isNull property || property.GetIndexParameters().Length<>0 ||
           not(typeof<IRegisteredFunction>.IsAssignableFrom property.PropertyType) then
            invalidOp "The native deployment does not export a registered function with this identity"
        admission.ValidateGetter(property.GetMethod)
        let value=property.GetValue(null)
        validateValue property.GetMethod value
        match value with
        | :? IRegisteredFunction as definition when definition.Kind=FunctionKind.HttpAction -> NativeExport.HttpAction definition
        | :? IRegisteredFunction as definition -> NativeExport.Function definition
        | _ -> invalidOp "The selected export is not a registered Convex function"

    member _.Catalog() =
        [| for declaring in assembly.GetExportedTypes() do
               for property in declaring.GetProperties(BindingFlags.Public ||| BindingFlags.Static) do
                   if typeof<IRegisteredFunction>.IsAssignableFrom(property.PropertyType) && property.GetIndexParameters().Length=0 then
                       admission.ValidateGetter(property.GetMethod)
                       let value=property.GetValue(null)
                       validateValue property.GetMethod value
                       match value with
                       | :? IRegisteredFunction as definition when definition.Kind=FunctionKind.HttpAction ->
                           yield declaring.FullName+":"+property.Name,NativeExport.HttpAction definition
                       | :? IRegisteredFunction as definition ->
                           yield declaring.FullName+":"+property.Name,NativeExport.Function definition
                       | _ -> () |]

    /// Selected public static values from a digest-pinned assembly in this
    /// context. The caller filters BEFORE any initializer/getter is evaluated.
    member _.ValuesIn(owner: string, select: string -> Type -> bool, ?validateCapturedState: bool) =
        let selectedAssembly = frozenOwner owner
        [| for declaring in selectedAssembly.GetExportedTypes() do
               for property in declaring.GetProperties(BindingFlags.Public ||| BindingFlags.Static) do
                   if property.GetIndexParameters().Length=0 && not(isNull property.GetMethod) then
                       let identity=declaring.FullName+":"+property.Name
                       if select identity property.PropertyType then
                           admission.ValidateGetter property.GetMethod
                           let value=property.GetValue(null)
                           validateValue property.GetMethod value
                           if defaultArg validateCapturedState false then
                               admission.ValidateCaptures(value,resolveCapturedField)
                           yield identity,value |]

    member this.DeclarationsIn(owner: string, select: string -> Type -> bool) =
        this.ValuesIn(owner, fun identity propertyType ->
            (typeof<IDeclaration>.IsAssignableFrom propertyType ||
             typeof<IRegisteredFunction>.IsAssignableFrom propertyType) && select identity propertyType)

    member this.Declarations(select: string -> Type -> bool) =
        this.DeclarationsIn(assembly.GetName().Name, select)

    interface IDisposable with
        member _.Dispose()=
            capturedFieldStructure.Clear()
            context.Unload()
            (admission :> IDisposable).Dispose()
            if actionAdmission.IsValueCreated then (actionAdmission.Value :> IDisposable).Dispose()
