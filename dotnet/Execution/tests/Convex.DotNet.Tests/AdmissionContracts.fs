namespace Convex.DotNet.Tests

open System.IO
open System
open System.Reflection
open System.Reflection.Emit
open System.Diagnostics
open System.Threading.Tasks
open Xunit
open Convex.Runtime
open Convex.DotNet.Execution

module AdmissionContracts =
    let private review export =
        let image = File.ReadAllBytes(typeof<RuntimeFixture.NativeDispatchFixtures>.Assembly.Location)
        NativeAssemblyReview.review image [||] [| "RuntimeFixture.NativeDispatchFixtures:" + export |]

    let private resolve selected =
        let path = typeof<RuntimeFixture.NativeDispatchFixtures>.Assembly.Location
        let digest = Security.Cryptography.SHA256.HashData(File.ReadAllBytes path) |> Convert.ToHexStringLower
        use deployment = new DeploymentAssembly(path, digest, [||], "query",
            frontendAssemblies = [| typeof<Convex.FSharp.QueryCtx>.Assembly |])
        deployment.Resolve selected |> ignore

    [<Fact>]
    let ``import review discovers deeply deferred closed interface receivers before resolving dispatch`` () =
        let methods, bytes = review "closed"
        Assert.True(methods > 0)
        Assert.True(bytes > 0L)
        resolve "RuntimeFixture.NativeDispatchFixtures:closed"

    [<Fact>]
    let ``execution review still refuses a receiver absent from the frozen closure`` () =
        // An optimized getter can defer all handler work; capture admission
        // must reject the unresolved receiver before the handler can run.
        let error = Assert.Throws<HostOperationException>(fun () -> resolve "RuntimeFixture.NativeDispatchFixtures:openTarget")
        Assert.Contains("No constructed frozen target for native interface", error.Message)
        Assert.Contains("IAdmissionAuthority.get_Revision", error.Message)

    [<Fact>]
    let ``dispatch review drains eager callbacks discovered by an interface target`` () =
        let error = Assert.Throws<HostOperationException>(fun () -> review "eagerInterfaceClock" |> ignore)
        Assert.Contains("System.DateTime.get_UtcNow", error.Message)

    [<Fact>]
    let ``unresolved initialization authority refuses before evaluating a lazy static`` () =
        use scope = Convex.FSharp.TableRegistry.enterScope()
        let marker = typeof<System.Collections.Generic.KeyValuePair<Guid, decimal>>
        Assert.Equal(None, Convex.FSharp.TableRegistry.tryGetName marker)
        let error = Assert.Throws<HostOperationException>(fun () ->
            resolve "RuntimeFixture.NativeLazyInitializationFixture:read")
        Assert.Contains("No constructed frozen target for native interface", error.Message)
        Assert.Contains("IUnresolvedInitializationAuthority.get_Revision", error.Message)
        Assert.Equal(None, Convex.FSharp.TableRegistry.tryGetName marker)

    let private genericDeployment () =
        let fixture = typeof<RuntimeFixture.NativeGenericCaptureFixtures>.Assembly
        let compiled = fixture.GetCustomAttribute<DebuggableAttribute>()
        Assert.True(isNull compiled || not compiled.IsJITOptimizerDisabled, "The generic capture fixture must be optimized.")
        let path = fixture.Location
        let digest = Security.Cryptography.SHA256.HashData(File.ReadAllBytes path) |> Convert.ToHexStringLower
        new DeploymentAssembly(path, digest, [||], "describe",
            frontendAssemblies = [| typeof<Convex.FSharp.QueryCtx>.Assembly |])

    let private assertPureResult (expected: int) selected =
        let frontend = Convex.FSharp.NativeExecution.FSharpExecutionFrontend() :> IExecutionFrontend
        let unexpected name = failwith ("Unexpected generic fixture capability: " + name)
        let host =
            { new IInvocationHost with
                member _.InvocationId = "optimized-generic-capture-proof"
                member _.Call(name, _) = unexpected name
                member _.CallAsync(name, _, _) = Task.FromResult(unexpected name) }
        use scope = frontend.Enter host
        match selected with
        | NativeExport.Function definition ->
            let result = definition.Invoke(host, RuntimeJson.parse "{}", RuntimeJson.nullValue).GetAwaiter().GetResult()
            Assert.Equal(expected, result.GetInt32())
        | _ -> failwith "Expected a native generic fixture query."

    [<Fact>]
    let ``optimized generic async continuations resolve with their exact closed context`` () =
        use deployment = genericDeployment()
        deployment.Resolve "RuntimeFixture.NativeGenericCaptureFixtures:asyncClosed"
        |> assertPureResult 42

    [<Fact>]
    let ``distinct closed static captures cannot reuse a prior instantiation admission`` () =
        use deployment = genericDeployment()
        deployment.Resolve "RuntimeFixture.NativeGenericCaptureFixtures:first"
        |> assertPureResult 7
        deployment.Resolve "RuntimeFixture.NativeGenericCaptureFixtures:second"
        |> assertPureResult 11
        let error = Assert.Throws<HostOperationException>(fun () ->
            deployment.Resolve "RuntimeFixture.NativeGenericCaptureFixtures:laterClock" |> ignore)
        Assert.Contains("System.DateTime.get_UtcNow", error.Message)

    [<Fact>]
    let ``generic interface dispatch requires the matching closed receiver`` () =
        use deployment = genericDeployment()
        deployment.Resolve "RuntimeFixture.NativeGenericCaptureFixtures:matchingInterface"
        |> assertPureResult 13
        deployment.Resolve "RuntimeFixture.NativeGenericCaptureFixtures:capturedGuidInterface"
        |> assertPureResult 17
        let error = Assert.Throws<HostOperationException>(fun () ->
            deployment.Resolve "RuntimeFixture.NativeGenericCaptureFixtures:mismatchedInterface" |> ignore)
        Assert.Contains("No constructed frozen target for native interface", error.Message)
        Assert.Contains("IGenericCaptureReceiver", error.Message)

    [<Fact>]
    let ``expanding generic method contexts stop at the controlled admission bound`` () =
        use deployment = genericDeployment()
        let error = Assert.Throws<HostOperationException>(fun () ->
            deployment.Resolve "RuntimeFixture.NativeGenericCaptureFixtures:expandingContext" |> ignore)
        Assert.Contains("Native generic type context exceeds its", error.Message)

    let private invokeDocument export document =
        let path = typeof<RuntimeFixture.NativeDocumentFixtures>.Assembly.Location
        let compiled = typeof<RuntimeFixture.NativeDocumentFixtures>.Assembly.GetCustomAttribute<DebuggableAttribute>()
        Assert.True(isNull compiled || not compiled.IsJITOptimizerDisabled, "The application fixture must be optimized.")
        let image = File.ReadAllBytes path
        let selected = "RuntimeFixture.NativeDocumentFixtures:" + export
        NativeAssemblyReview.review image [||] [| selected |] |> ignore
        let digest = Security.Cryptography.SHA256.HashData image |> Convert.ToHexStringLower
        let frontend = Convex.FSharp.NativeExecution.FSharpExecutionFrontend() :> IExecutionFrontend
        let mutable reads = 0
        let reply name =
            match name with
            | "1.0/queryStream" -> RuntimeJson.parse """{"queryId":1}"""
            | "1.0/queryStreamNext" ->
                reads <- reads + 1
                if reads = 1 then RuntimeJson.parse ("{\"done\":false,\"value\":" + document + "}")
                else RuntimeJson.parse """{"done":true,"value":null}"""
            | "1.0/queryCleanup" -> RuntimeJson.nullValue
            | _ -> failwith ("Unexpected document capability: " + name)
        let host =
            { new IInvocationHost with
                member _.InvocationId = "optimized-document-proof"
                member _.Call(name, _) = reply name
                member _.CallAsync(name, _, _) = Task.FromResult(reply name) }
        use scope = frontend.Enter host
        use deployment = new DeploymentAssembly(path, digest, [||], "query", frontendAssemblies = [| frontend.GetType().Assembly |])
        match deployment.Resolve selected with
        | NativeExport.Function definition ->
            let result = definition.Invoke(host, RuntimeJson.parse "{}", RuntimeJson.nullValue).GetAwaiter().GetResult()
            Assert.Equal(2, reads)
            result
        | _ -> failwith "Expected a native document query."

    [<Fact>]
    let ``optimized native database decoding projects only undeclared system fields`` () =
        let document = """{"name":"kept","_id":"synthetic-id","_creationTime":42}"""
        let projected = invokeDocument "read" document
        Assert.Equal("kept", projected.GetProperty("name").GetString())
        Assert.Single(projected.EnumerateObject()) |> ignore
        let retained = invokeDocument "readMetadata" document
        Assert.Equal("kept", retained.GetProperty("name").GetString())
        Assert.Equal("synthetic-id", retained.GetProperty("_id").GetString())
        Assert.Equal(42., retained.GetProperty("_creationTime").GetDouble())
        Assert.Equal(3, retained.EnumerateObject() |> Seq.length)

    [<Theory>]
    [<InlineData("{\"name\":\"kept\",\"unowned\":true,\"_id\":\"synthetic-id\",\"_creationTime\":42}", "Unexpected field 'unowned'")>]
    [<InlineData("{\"_id\":\"synthetic-id\",\"_creationTime\":42}", "cannot decode missing input")>]
    let ``optimized native database decoding still enforces exact document fields`` document expected =
        let error = Assert.ThrowsAny<Exception>(fun () -> invokeDocument "read" document |> ignore)
        Assert.Contains(expected, error.ToString())

    [<Fact>]
    let ``optimized deployment reflection remains forbidden`` () =
        let error = Assert.Throws<HostOperationException>(fun () ->
            resolve "RuntimeFixture.NativeNegative:reflection")
        Assert.Contains("System.Type.GetType", error.Message)

    [<Fact>]
    let ``a later getter cannot reuse interface admission for a newly constructed receiver`` () =
        let path = typeof<RuntimeFixture.NativeSequentialDispatchFixtures>.Assembly.Location
        let digest = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes path) |> System.Convert.ToHexStringLower
        use assembly = new DeploymentAssembly(path, digest, [||], "describe",
            frontendAssemblies = [| typeof<Convex.FSharp.QueryCtx>.Assembly |])
        assembly.Resolve "RuntimeFixture.NativeSequentialDispatchFixtures:first" |> ignore
        let error = Assert.Throws<HostOperationException>(fun () ->
            assembly.Resolve "RuntimeFixture.NativeSequentialDispatchFixtures:laterClock" |> ignore)
        Assert.Contains("System.DateTime.get_UtcNow", error.Message)

    let private frameworkIdentity name = "RuntimeFixture.NativeFrameworkCallbackFixtures:" + name

    let private invokeFramework arguments selected =
        let frontend = Convex.FSharp.NativeExecution.FSharpExecutionFrontend() :> IExecutionFrontend
        let unexpected name = failwith ("Unexpected framework fixture capability: " + name)
        let host =
            { new IInvocationHost with
                member _.InvocationId = "optimized-framework-callback-proof"
                member _.Call(name, _) = unexpected name
                member _.CallAsync(name, _, _) = Task.FromResult(unexpected name) }
        use scope = frontend.Enter host
        match selected with
        | NativeExport.Function definition ->
            definition.Invoke(host, RuntimeJson.parse arguments, RuntimeJson.nullValue).GetAwaiter().GetResult().GetInt32()
        | _ -> failwith "Expected a native framework fixture function."

    let private fixtureCalls (method: MethodInfo) =
        let body = method.GetMethodBody()
        if isNull body then [||]
        else
            let codes =
                typeof<OpCodes>.GetFields(BindingFlags.Public ||| BindingFlags.Static)
                |> Seq.map(fun field -> let code = unbox<OpCode>(field.GetValue null) in code.Value, code)
                |> dict
            let bytes = body.GetILAsByteArray()
            let calls = ResizeArray<OpCode * MethodBase>()
            let mutable offset = 0
            while offset < bytes.Length do
                let first = int bytes[offset]
                offset <- offset + 1
                let value =
                    if first = 254 then
                        let value = int16 ((first <<< 8) ||| int bytes[offset])
                        offset <- offset + 1
                        value
                    else int16 first
                let code = codes[value]
                let size =
                    match code.OperandType with
                    | OperandType.InlineNone -> 0
                    | OperandType.ShortInlineBrTarget | OperandType.ShortInlineI | OperandType.ShortInlineVar -> 1
                    | OperandType.InlineVar -> 2
                    | OperandType.InlineI8 | OperandType.InlineR -> 8
                    | OperandType.InlineSwitch -> 4 + 4 * BitConverter.ToInt32(bytes, offset)
                    | OperandType.InlineMethod ->
                        let target = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, offset), method.DeclaringType.GetGenericArguments(), method.GetGenericArguments())
                        calls.Add(code, target)
                        4
                    | _ -> 4
                offset <- offset + size
            calls.ToArray()

    [<Fact>]
    let ``optimized framework witnesses retain their actual external slot operands`` () =
        let fixture = typeof<RuntimeFixture.NativeFrameworkCallbackFixtures>.Assembly
        let owner = fixture.GetType("RuntimeFixture.FrameworkCallbackEntryPoints", true)
        let inspect name code declaring targetName =
            let method = owner.GetMethod(name, BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static)
            Assert.NotNull method
            Assert.True((method.GetMethodImplementationFlags() &&& MethodImplAttributes.NoInlining) <> enum 0)
            Assert.True(fixtureCalls method |> Array.exists(fun (actual, target) ->
                actual = code && target.Name = targetName && target.DeclaringType.FullName = declaring))
        inspect "Render" OpCodes.Callvirt "System.Object" "ToString"
        inspect "Dispose" OpCodes.Callvirt "System.IDisposable" "Dispose"
        inspect "Format" OpCodes.Call "System.String" "Format"

    [<Theory>]
    [<InlineData("pureToString", 42)>]
    [<InlineData("pureDispose", 1)>]
    [<InlineData("pureFormat", 42)>]
    [<InlineData("pureCollection", 42)>]
    [<InlineData("stableComparer", 42)>]
    [<InlineData("pureContainsValue", 42)>]
    [<InlineData("pureContainsPair", 42)>]
    [<InlineData("pureRemovePair", 42)>]
    [<InlineData("storedRecord", 84)>]
    [<InlineData("sequentialProjection", 42)>]
    [<InlineData("collectedProjection", 42)>]
    [<InlineData("defaultProjection", 42)>]
    [<InlineData("someProjection", 42)>]
    [<InlineData("pureSpecialization", 42)>]
    [<InlineData("pureNumericConversion", 42)>]
    [<InlineData("pureConvertible", 42)>]
    [<InlineData("caughtProjection", 42)>]
    [<InlineData("storedFormatArgument", 42)>]
    [<InlineData("renderedCapturedFormat", 42)>]
    [<InlineData("capturedGuidFormatter", 42)>]
    [<InlineData("pureFrozenVirtual", 42)>]
    [<InlineData("taskValue", 42)>]
    [<InlineData("pureValueTaskSource", 42)>]
    [<InlineData("retainedActionResource", 42)>]
    let ``real framework callbacks and unused record hash bodies preserve pure behavior`` name expected =
        use deployment = genericDeployment()
        let actual = deployment.Resolve(frameworkIdentity name) |> invokeFramework "{}"
        Assert.Equal(expected, actual)

    [<Theory>]
    [<InlineData("clockToString")>]
    [<InlineData("capturedClockToString")>]
    [<InlineData("clockDispose")>]
    [<InlineData("clockFormat")>]
    [<InlineData("clockCollection")>]
    [<InlineData("clockComparer")>]
    [<InlineData("clockContainsValue")>]
    [<InlineData("clockContainsPair")>]
    [<InlineData("clockRemovePair")>]
    [<InlineData("hashedRecord")>]
    [<InlineData("projectionClock")>]
    [<InlineData("defaultClock")>]
    [<InlineData("clockSpecialization")>]
    [<InlineData("clockConvertible")>]
    [<InlineData("catchClock")>]
    [<InlineData("clockCapturedFormat")>]
    [<InlineData("clockFrozenVirtual")>]
    [<InlineData("clockDecodedArgument")>]
    [<InlineData("retainedQueryResource")>]
    [<InlineData("clockValueTaskSource")>]
    let ``framework callbacks cannot hide forbidden deployment calls`` name =
        use deployment = genericDeployment()
        let error = Assert.Throws<HostOperationException>(fun () -> deployment.Resolve(frameworkIdentity name) |> ignore)
        Assert.Contains("System.DateTime.get_UtcNow", error.Message)

    [<Fact>]
    let ``a function projection that actually hashes a record still refuses`` () =
        use deployment = genericDeployment()
        let error = Assert.Throws<HostOperationException>(fun () -> deployment.Resolve(frameworkIdentity "projectionHash") |> ignore)
        Assert.Contains("System.Object.GetHashCode", error.Message)

    [<Fact>]
    let ``the real native decoder supplies a reviewed record without handler construction`` () =
        let fixture = typeof<RuntimeFixture.NativeFrameworkCallbackFixtures>.Assembly
        let argument = fixture.GetType("RuntimeFixture.PureDecodedInput", true)
        let handlers =
            fixture.GetTypes()
            |> Array.collect(fun kind -> kind.GetMethods(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly))
            |> Array.filter(fun method -> method.Name = "Invoke" && (method.GetParameters() |> Array.exists(fun parameter -> parameter.ParameterType = argument)))
        Assert.NotEmpty handlers
        for handler in handlers do
            Assert.False(fixtureCalls handler |> Array.exists(fun (code, target) -> code = OpCodes.Newobj && target.DeclaringType = argument))
        use deployment = genericDeployment()
        let selected = deployment.Resolve(frameworkIdentity "decodedArgument")
        Assert.Equal(42, invokeFramework """{"value":42}""" selected)

    [<Fact>]
    let ``framework callbacks execute during import only after metadata admission`` () =
        let fixture = File.ReadAllBytes(typeof<RuntimeFixture.NativeFrameworkCallbackFixtures>.Assembly.Location)
        let error = Assert.Throws<HostOperationException>(fun () ->
            NativeAssemblyReview.review fixture [||] [| frameworkIdentity "importClock" |] |> ignore)
        Assert.Contains("System.DateTime.get_UtcNow", error.Message)

    [<Fact>]
    let ``a later framework receiver cannot reuse a previous callback admission`` () =
        use deployment = genericDeployment()
        deployment.Resolve(frameworkIdentity "pureToString") |> invokeFramework "{}" |> fun result -> Assert.Equal(42, result)
        let error = Assert.Throws<HostOperationException>(fun () -> deployment.Resolve(frameworkIdentity "capturedClockToString") |> ignore)
        Assert.Contains("System.DateTime.get_UtcNow", error.Message)

    [<Theory>]
    [<InlineData("queryTaskId", ".get_Id")>]
    [<InlineData("mutationTaskId", ".get_Id")>]
    [<InlineData("queryCurrentTaskId", ".get_CurrentId")>]
    [<InlineData("queryTaskStatus", ".get_Status")>]
    let ``deterministic handlers refuse direct ambient task identity and state`` name memberName =
        use deployment = genericDeployment()
        let error = Assert.Throws<HostOperationException>(fun () -> deployment.Resolve(frameworkIdentity name) |> ignore)
        Assert.Contains("System.Threading.Tasks.Task", error.Message)
        Assert.Contains(memberName, error.Message)

    [<Fact>]
    let ``task identity remains an action capability`` () =
        use deployment = genericDeployment()
        let actual = deployment.Resolve(frameworkIdentity "actionTaskId") |> invokeFramework "{}"
        Assert.True(actual > 0)

    [<Theory>]
    [<InlineData("queryAwaiterState", ".get_IsCompleted")>]
    [<InlineData("queryValueTaskState", ".get_IsCompleted")>]
    [<InlineData("queryConfiguredAwaiterState", ".get_IsCompleted")>]
    [<InlineData("queryAwaiterStateInsideTask", ".get_IsCompleted")>]
    [<InlineData("queryAwaiterOnCompleted", ".OnCompleted")>]
    [<InlineData("queryAwaiterUnsafeOnCompleted", ".UnsafeOnCompleted")>]
    [<InlineData("queryParallelRace", ".Parallel")>]
    [<InlineData("queryChoiceRace", ".Choice")>]
    [<InlineData("querySwitchThread", ".SwitchToThreadPool")>]
    let ``deterministic application completion polling and independent scheduling refuse`` name memberName =
        use deployment = genericDeployment()
        let error = Assert.Throws<HostOperationException>(fun () -> deployment.Resolve(frameworkIdentity name) |> ignore)
        Assert.Contains(memberName, error.Message)

    [<Fact>]
    let ``direct awaiter state remains a managed action capability`` () =
        use deployment = genericDeployment()
        let actual = deployment.Resolve(frameworkIdentity "actionAwaiterState") |> invokeFramework "{}"
        Assert.Equal(42, actual)
