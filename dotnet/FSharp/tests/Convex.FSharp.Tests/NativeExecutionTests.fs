namespace Convex.FSharp.Tests

open System
open System.Threading.Tasks
open Xunit
open Convex.FSharp

module NativeExecutionTests =
    open CompletionFixtures
    type ScopedTable = interface end
    type private ReceiptWire = {state:string;principal:NullOr<string>}
    type private PrivateChoice = Pending of ReceiptWire | Completed of int64

    [<Fact>]
    let ``private mapped wire records retain checked native representation`` () =
        let validator=V.object ["state",V.literalString "pending";"principal",V.nullable(V.string())]
        let contract=Contract.create validator Codec.encode<ReceiptWire> Codec.decode<ReceiptWire>
        let original={state="pending";principal=NullOr.Value "owned"}
        let encoded=contract.Encode original
        Assert.Equal(original,contract.Decode encoded)
        Assert.ThrowsAny<Exception>(fun () -> contract.Decode (objectValue ["state",box "foreign";"principal",null]) |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<ReceiptWire>(objectValue ["state",box "pending";"principal",null;"unowned",box true]) |> ignore) |> ignore

    [<Fact>]
    let ``private union payloads preserve native codecs and derived validators`` () =
        let expected=Completed 9007199254740993L
        let encoded=Codec.encode expected
        ValidatorCheck.require (TypeDerivation.derive<PrivateChoice>()) encoded
        Assert.Equal(expected,Codec.decode<PrivateChoice> encoded)
        let nested=Pending {state="pending";principal=NullOr.Null}
        Assert.Equal(nested,Codec.decode<PrivateChoice>(Codec.encode nested))

    [<Fact>]
    let ``native registration rejects malformed arguments before application work`` () =
        let mutable called=false
        let definition=Convex.internalMutation (fun _ (_:{|name:string|}) -> async { called<-true; return 1. })
                       |> FunctionRegistration.erase
        let invoke arguments=NativeFixtures.invoke definition (NativeFixtures.unavailableHost()) arguments
        Assert.ThrowsAny<Exception>(fun () -> invoke NativeFixtures.emptyArguments |> ignore) |> ignore
        Assert.False(called)
        Assert.Equal(1.,(invoke (Convex.Runtime.RuntimeJson.parse """{"name":"valid"}""")).GetDouble())
        Assert.True(called)
        Assert.Equal(Convex.Runtime.FunctionVisibility.Internal,definition.Visibility)

    [<Fact>]
    let ``native registration checks return values and maps top level unit to null`` () =
        let wrong=Contract.create (V.number()) (fun (value:string) -> box value) unbox<string>
        let invalid=Convex.queryWithContracts (Contract.arguments<unit>()) wrong (fun _ () -> async { return "wrong" })
                    |> FunctionRegistration.erase
        Assert.ThrowsAny<Exception>(fun () -> NativeFixtures.invoke invalid (NativeFixtures.unavailableHost()) NativeFixtures.emptyArguments |> ignore) |> ignore
        let unitResult=Convex.query (fun _ () -> async { return () }) |> FunctionRegistration.erase
        let actual=NativeFixtures.invoke unitResult (NativeFixtures.unavailableHost()) NativeFixtures.emptyArguments
        Assert.Equal(System.Text.Json.JsonValueKind.Null,actual.ValueKind)

    [<Fact>]
    let ``invocation environment requires an owner and restores nested scope`` () =
        Assert.Throws<InvalidOperationException>(fun () -> InvocationRuntime.nowMilliseconds() |> ignore) |> ignore
        use first=InvocationRuntime.enter {Now=(fun () -> 42.);Random=(fun () -> 0.25);EnvironmentVariable=(fun name -> if name="OWNER" then Some "first" else None)}
        Assert.Equal(42.,InvocationRuntime.nowMilliseconds())
        Assert.Equal(Some "first",InvocationRuntime.environmentVariable "OWNER")
        Assert.Equal(None,InvocationRuntime.environmentVariable "MISSING")
        do
            use second=InvocationRuntime.enter {Now=(fun () -> 99.);Random=(fun () -> 0.75);EnvironmentVariable=(fun _ -> Some "second")}
            Assert.Equal(99.,InvocationRuntime.nowMilliseconds())
            Assert.Equal(0.75,InvocationRuntime.random())
            Assert.Equal(Some "second",InvocationRuntime.environmentVariable "OWNER")
        Assert.Equal(42.,InvocationRuntime.nowMilliseconds())
        Assert.Equal(Some "first",InvocationRuntime.environmentVariable "OWNER")

    [<Fact>]
    let ``ordinary fingerprints preserve ECMAScript strings numbers and property order`` () =
        let loneSurrogate="\b\n"+String([|char 0xD800|])
        let value=objectValue ["z",box loneSurrogate;"2",box 1e21;"a",box -0.;"1",box 1e-7;"absent",JsHelpers.undefinedValue()]
        Assert.Equal("{\"1\":1e-7,\"2\":1e+21,\"z\":\"\\b\\n\\ud800\",\"a\":0}",PortableJson.stringify value)
        Assert.Equal("0.000001",PortableJson.stringify (box 1e-6))
        Assert.Equal("100000000000000000000",PortableJson.stringify (box 1e20))
        Assert.Equal("[null,null]",PortableJson.stringify (box [|JsHelpers.undefinedValue();box Double.PositiveInfinity|]))

    [<Fact>]
    let ``structured application errors preserve canonical full width payload`` () =
        let error=ConvexError.create {|quantity=9007199254740993L;bytes=[|0uy;255uy|]|}
        let raw=(error :> IConvexApplicationError).EncodedData
        Assert.Equal(9007199254740993L,JsHelpers.getProperty raw "quantity" |> Codec.decode<int64>)
        Assert.Equal<byte>([|0uy;255uy|],JsHelpers.getProperty raw "bytes" |> Codec.decode<byte[]>)

    [<Fact>]
    let ``native filter AST distinguishes object literals and missing fields`` () =
        let builder=FilterBuilder null
        let expected=builder.Eq(builder.Field "optional",JsHelpers.undefinedValue()) |> NativeExpressions.serialize
        let operands=JsHelpers.getProperty expected "$eq" |> unbox<obj[]>
        Assert.Equal("optional",JsHelpers.getProperty operands[0] "$field" |> unbox<string>)
        Assert.True(JsHelpers.isUndefined (JsHelpers.getProperty operands[1] "$literal"))
        let literal=objectValue ["$field",box "data"]
        let wrapped=builder.Eq(builder.Field "value",literal) |> NativeExpressions.serialize
        let arguments=JsHelpers.getProperty wrapped "$eq" |> unbox<obj[]>
        Assert.Same(literal,JsHelpers.getProperty arguments[1] "$literal")

    [<Fact>]
    let ``invocation table bindings cannot leak or rebind a parent registry`` () =
        Assert.Equal(None,TableRegistry.tryGetName typeof<ScopedTable>)
        do
            use parent=TableRegistry.enterScope()
            TableRegistry.register typeof<ScopedTable> "parent"
            do
                use child=TableRegistry.enterScope()
                TableRegistry.register typeof<ScopedTable> "child"
                Assert.Equal(Some "child",TableRegistry.tryGetName typeof<ScopedTable>)
            Assert.Equal(Some "parent",TableRegistry.tryGetName typeof<ScopedTable>)
        Assert.Equal(None,TableRegistry.tryGetName typeof<ScopedTable>)

    [<Fact>]
    let ``native nested application error can be decoded into its owning shape`` () =
        let wire=Codec.encodeArguments {|reason="refused";operation="counter"|}
        let exceptionValue=ConvexError<obj>(wire,wire) :> exn
        match exceptionValue with
        | ConvexError (data: {|reason:string;operation:string|}) ->
            Assert.Equal("refused",data.reason)
            Assert.Equal("counter",data.operation)
        | _ -> failwith "Structured native nested error was lost"

    [<Fact>]
    let ``SDK pagination query identity survives native option lowering`` () =
        // Reactive clients supply this SDK field alongside their cursor. Losing
        // it changes the owner's pagination journal even when rows look right.
        let raw=objectValue ["numItems",box 2;"cursor",null;"id",box 731.]
        let options=Pagination.decodeOptions raw
        Assert.Equal(Some 731.,options.QueryId)
        let lowered=Pagination.encodeOptions options
        Assert.Equal(731.,JsHelpers.getProperty lowered "id" |> Codec.decode<float>)
        Assert.False(JsHelpers.hasProperty (Pagination.encodeOptions(AdvancedPaginationOptions.Create 2)) "id")

module NativeHttpInvocationTests =
    open System.Text.Json
    open Convex.Runtime

    [<Fact>]
    let ``HTTP clones consume one owning stream with independent body state`` () = task {
        let mutable reads=0
        let chunks=Collections.Generic.Queue<JsonElement>([
            ConvexWire.encode {|bytes=[|0uy;1uy;255uy|];``done``=false|} |> RuntimeJson.parse
            ConvexWire.encode {|bytes=Array.empty<byte>;``done``=true|} |> RuntimeJson.parse])
        let host=NativeFixtures.host (fun name args asynchronous ->
            Assert.Equal("dotnet/httpReadBody",name)
            Assert.True(asynchronous)
            Assert.Empty(args.EnumerateObject())
            reads<-reads+1
            Task.FromResult(chunks.Dequeue()))
        let handler=Convex.httpActionTask (fun _ request -> task {
            let clone=request.Clone()
            Assert.False(request.BodyUsed)
            Assert.Equal(Some "one, two",request.Header "X-Probe")
            let! bytes=request.Bytes() |> Async.StartAsTask
            let! copied=clone.Bytes() |> Async.StartAsTask
            Assert.Equal<byte>(bytes,copied)
            Assert.True(request.BodyUsed)
            Assert.True(clone.BodyUsed)
            Assert.Equal(2,reads)
            Assert.Throws<InvalidOperationException>(fun () -> request.Clone() |> ignore) |> ignore
            let! _=Assert.ThrowsAsync<InvalidOperationException>(fun () -> request.Bytes() |> Async.StartAsTask :> Task)
            Assert.Equal(2,reads)
            return Http.ok "accepted"
        })
        let head=RuntimeJson.parse """{"method":"POST","url":"https://isolated.invalid/native/echo","headers":[{"name":"X-Probe","value":"one"},{"name":"x-probe","value":"two"}]}"""
        let! result=(handler :> IRegisteredFunction).Invoke(host,RuntimeJson.nullValue,head)
        Assert.Equal(200,result.GetProperty("status").GetInt32())
    }

    [<Fact>]
    let ``HTTP ordinary JSON never interprets Convex-looking tags`` () = task {
        let host=NativeFixtures.host (fun name _ _ ->
            Assert.Equal("dotnet/httpReadBody",name)
            let chunk={|bytes=Text.Encoding.UTF8.GetBytes """{"$integer":"unchanged"}""";``done``=true|}
            Task.FromResult(ConvexWire.encode chunk |> RuntimeJson.parse))
        let handler=Convex.httpActionTask (fun _ request -> task {
            let! json=request.JsonRaw() |> Async.StartAsTask
            Assert.Equal("unchanged",JsHelpers.getProperty json "$integer" |> unbox<string>)
            return Http.ok "accepted"
        })
        let head=RuntimeJson.parse """{"method":"POST","url":"https://isolated.invalid/native/echo","headers":[]}"""
        let! result=(handler :> IRegisteredFunction).Invoke(host,RuntimeJson.nullValue,head)
        Assert.Equal(200,result.GetProperty("status").GetInt32())
    }

module NativeNestedErrorTests =
    open Convex.Runtime

    type Refusal = { reason:string; quantity:int64; bytes:byte[] }

    [<Fact>]
    let ``an asynchronous native owner refusal remains catchable with its exact FSharp payload`` () =
        let expected={reason="refused";quantity=9007199254740993L;bytes=[|0uy;255uy|]}
        let data=ConvexWire.encode expected |> RuntimeJson.parse
        let host=NativeFixtures.host (fun name _ _ ->
            Assert.Equal("1.0/runUdf",name)
            Task.FromException<System.Text.Json.JsonElement>(Convex.Runtime.ApplicationException("Owner refused",data)))
        let target=FunctionReference.publicQuery "counter:refuse" (Contract.arguments<unit>()) (Contract.derived<unit>())
        let definition =
            Convex.query (fun context () -> async {
                try
                    do! context.RunQuery(target,())
                    return failwith "A refused owning call must not succeed"
                with
                | ConvexError (actual:Refusal) -> return actual
            })
            |> FunctionRegistration.erase
        let actual=NativeFixtures.invoke definition host NativeFixtures.emptyArguments
                   |> fun value -> ConvexWire.decode<Refusal>(value.GetRawText())
        Assert.Equal(expected.reason,actual.reason)
        Assert.Equal(expected.quantity,actual.quantity)
        Assert.Equal<byte>(expected.bytes,actual.bytes)
