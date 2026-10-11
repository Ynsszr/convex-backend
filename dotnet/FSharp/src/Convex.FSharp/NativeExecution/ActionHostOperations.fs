namespace Convex.FSharp.NativeExecution

open System
open Convex.FSharp

/// Action capabilities use the owning action task executor. They do not expose
/// a database transaction; nested queries and mutations remain separate calls.
module internal NativeActionContexts =
    open HostArguments

    let private schedule (host: ITransactionHostOperations) requestId : IScheduler =
        let at timestamp target arguments = async {
            let! result=host.Call("1.0/actions/schedule",objectValue (functionAddress target@[
                "requestId",box requestId;"version",box version;"ts",box (float timestamp/1000.);"args",arguments]),true)
            return Codec.decode<string> result
        }
        { new IScheduler with
            member _.RunAfterRaw(delay,target,args)=
                ScheduleValidation.delay delay
                at (int64 (InvocationRuntime.nowMilliseconds())+delay) target args
            member _.RunAtRaw(timestamp,target,args)=
                ScheduleValidation.timestamp timestamp
                at timestamp target args
            member _.Cancel(Id id)=async {
                let! _=host.Call("1.0/actions/cancel_job",objectValue ["requestId",box requestId;"id",box id],true)
                return () } }

    let private storage (host: ITransactionHostOperations) requestId : IStorageActionWriter =
        let ordinary=NativeContexts.storageWriter host requestId
        let streams=NativeStorageStreams.create host
        let get id = async {
            let! value=host.Call("dotnet/storageGet",objectValue ["storageId",box id],true)
            if isNull value then return None else
            let bytes=field "bytes" value |> Codec.decode<byte[]>
            return Some (NativeStorageBlob(bytes,optionalText "contentType" value))
        }
        let store bytes contentType digest = async {
            let! value=host.Call("dotnet/storageStore",objectValue [
                "bytes",box bytes;"contentType",contentType |> Option.map box |> Option.defaultValue null
                "sha256",digest |> Option.map box |> Option.defaultValue null],true)
            return Codec.decode<string> value
        }
        { new IStorageActionWriter with
            member _.GetUrl id=ordinary.GetUrl id
            member _.GetMetadata id=ordinary.GetMetadata id
            member _.GenerateUploadUrl()=ordinary.GenerateUploadUrl()
            member _.Delete id=ordinary.Delete id
            member _.Get id=async { let! blob=get id in return blob |> Option.map(fun value -> value.Bytes) }
            member _.Store(bytes,?contentType,?sha256)=store bytes contentType sha256
            member _.GetBlob id=async { let! blob=get id in return blob |> Option.map box }
            member _.StoreBlob(blob,?sha256)=
                match blob with
                | :? NativeStorageBlob as value -> store value.Bytes value.ContentType sha256
                | _ -> invalidArg "blob" "A native storage blob is required"
          interface INativeStorageStreaming with
            member _.OpenRead id=streams.OpenRead id
            member _.OpenWrite(?contentType,?contentLength,?sha256)=
                streams.OpenWrite(?contentType=contentType,?contentLength=contentLength,?sha256=sha256) }

    let private createContext (host: ITransactionHostOperations) requestId visibility kind : ActionCtx =
        let nested kind target arguments =
            host.Call("1.0/actions/"+kind,objectValue (functionAddress target@[
                "args",arguments;"version",box version;"requestId",box requestId]),true)
        let vector table index values limit filter = async {
            VectorSearchValidation.validate values limit
            let expression=filter |> Option.map(fun build -> build null |> NativeExpressions.serialize) |> Option.defaultValue null
            let queryFields=["indexName",box (table+"."+index);"vector",box (values |> Array.map box);"expressions",expression]
            let queryFields=queryFields@(limit |> Option.map(fun count -> ["limit",box count]) |> Option.defaultValue [])
            let! result=host.Call("1.0/actions/vectorSearch",objectValue [
                "requestId",box requestId;"version",box version;"query",objectValue queryFields],true)
            return field "results" result |> unbox<obj[]>
        }
        let metadata=NativeContexts.metadata host kind visibility
        { new ActionCtx with
            member _.Auth=NativeContexts.auth host
            member _.Scheduler=schedule host requestId
            member _.Storage=storage host requestId
            member _.Meta=
                { new IActionMeta with
                    member _.GetFunctionMetadata()=metadata.GetFunctionMetadata()
                    member _.GetDeploymentMetadata()=metadata.GetDeploymentMetadata()
                    member _.GetRequestMetadata()=metadata.GetRequestMetadata() }
            member _.RunQueryRaw(target,args)=nested "query" target args
            member _.RunMutationRaw(target,args)=nested "mutation" target args
            member _.RunActionRaw(target,args)=nested "action" target args
            member _.VectorSearchRaw(table,index,values,limit)=vector table index values limit None
            member _.VectorSearchFilteredRaw(table,index,values,limit,filter)=vector table index values limit filter
            member _.GetServiceTokenRaw service=
                if service<>"ai-gateway" then invalidArg "service" "Unsupported action service"
                async {
                    let! value=host.Call("1.0/createServiceToken",objectValue ["service",box service;"version",box version],true)
                    return Codec.decode<string> value }
            member _.GetServiceUrlRaw service=
                if service<>"ai-gateway" then invalidArg "service" "Unsupported action service"
                async {
                    let! value=host.Call("1.0/getServiceUrl",objectValue ["service",box service;"version",box version],true)
                    return Codec.decode<string> value } }

    let actionContext host requestId visibility = createContext host requestId visibility "action"
    let httpActionContext host requestId visibility = createContext host requestId visibility "httpAction"
