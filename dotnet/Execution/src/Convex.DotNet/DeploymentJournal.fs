module Convex.DotNet.DeploymentJournal

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Convex.DotNet.Deployment

type PriorOutcome = Fresh | Recorded | Refused | Uncertain
[<RequireQualifiedAccess>]
type JournalOutcome = Sent | Prepared | Confirmed | Refused | Uncertain
type private StoredState =
    {GraphSha256:string;Outcome:string;OperationId:string option;PreparedSha256:string option}

module private Durability =
    [<DllImport("libc", EntryPoint="open", SetLastError=true)>]
    extern int private openDirectory(string path,int flags)
    [<DllImport("libc", SetLastError=true)>]
    extern int private fsync(int descriptor)
    [<DllImport("libc", SetLastError=true)>]
    extern int private close(int descriptor)
    [<DllImport("libc", SetLastError=true)>]
    extern int private statx(int directory,string path,int flags,uint32 mask,nativeint buffer)

    let requireSupported () =
        if not(OperatingSystem.IsLinux()) then
            raise(PlatformNotSupportedException("Durable native deployment journals currently require Linux directory fsync"))

    let private failure operation =
        IOException("Deployment journal "+operation+" failed",System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()))

    let private entryExists expectedKind (path:string) =
        // File.Exists reports false for dangling links and directories. Query
        // the final entry without following it, before any potentially blocking
        // read/open, so only a genuinely absent state can admit Fresh.
        let buffer=Marshal.AllocHGlobal 256
        try
            if statx(-100,path,0x100,1u,buffer)<>0 then
                if Marshal.GetLastPInvokeError()=2 then false // ENOENT
                else raise(failure "file metadata read")
            else
                let available=uint32(Marshal.ReadInt32(buffer,0))
                let mode=int(uint16(Marshal.ReadInt16(buffer,28)))
                if available &&& 1u<>1u || mode &&& 0xf000<>expectedKind then
                    invalidOp "Deployment journal entry has an invalid filesystem type or is a symbolic link"
                true
        finally Marshal.FreeHGlobal buffer

    let regularFileExists path = entryExists 0x8000 path
    let directoryExists path = entryExists 0x4000 path

    let readState path =
        if not(regularFileExists path) then None else
        if FileInfo(path).Length>65536L then invalidOp "Deployment history is not a bounded regular file"
        use input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)
        use frozen=new MemoryStream()
        let chunk=Array.zeroCreate<byte> 4096
        let mutable reading=true
        while reading do
            let count=input.Read(chunk,0,chunk.Length)
            if count=0 then reading<-false else
            if frozen.Length+int64 count>65536L then invalidOp "Deployment history exceeded its byte bound"
            frozen.Write(chunk,0,count)
        Some(JsonDocument.Parse(ReadOnlyMemory<byte>(frozen.ToArray())))

    let syncDirectory (path:string) =
        // O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC: a link or unsupported filesystem
        // must fail before a caller can dispatch the next network transition.
        let descriptor=openDirectory(path,0x10000 ||| 0x20000 ||| 0x80000)
        if descriptor<0 then raise(failure "directory open")
        try
            if fsync descriptor<>0 then raise(failure "directory flush")
        finally close descriptor |> ignore

    let syncHierarchy (path:string) =
        let mutable current=Path.GetFullPath path
        while not(String.IsNullOrEmpty current) do
            if DirectoryInfo(current).LinkTarget<>null then invalidOp "Deployment history cannot traverse a symbolic link"
            syncDirectory current
            current<-Path.GetDirectoryName current

/// Check a retained state entry without opening links or special files.
let boundedRegularFileExists maxBytes path =
    Durability.requireSupported()
    if not(Durability.regularFileExists path) then false else
    if FileInfo(path).Length>maxBytes then invalidOp "Retained state exceeds its byte bound"
    true

/// Validate retained URL-scoped histories before changing the local journal
/// namespace. This never opens special files or discards an unresolved send.
let requireSettledHistory (directory:string) =
    Durability.requireSupported()
    let privateDirectory (path:string) =
        let privateMode=UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        if File.GetUnixFileMode path &&& ~~~privateMode<>enum<UnixFileMode>0 then
            invalidOp "Existing deployment history must be private"
    if Durability.directoryExists directory then
        privateDirectory directory
        let scopes=Directory.EnumerateFileSystemEntries directory |> Seq.truncate 129 |> Seq.toArray
        if scopes.Length>128 then invalidOp "Legacy deployment history requires maintenance"
        for scope in scopes do
            let name=Path.GetFileName scope
            if not(NativeDeployment.validHex 64 name) || not(Durability.directoryExists scope) then
                invalidOp "Legacy deployment history has an invalid scope"
            privateDirectory scope
            match Durability.readState(Path.Combine(scope,"state.json")) with
            | None -> ()
            | Some document ->
                use document=document
                let state=document.RootElement
                if state.GetProperty("version").GetInt32()<>1 || state.GetProperty("originHash").GetString()<>name ||
                   not(NativeDeployment.validHex 64 (state.GetProperty("graphSha256").GetString())) then
                    invalidOp "Legacy deployment history contract is incompatible"
                let outcome=state.GetProperty("outcome").GetString()
                if outcome<>"confirmed" && outcome<>"refused" then
                    invalidOp "Reconcile the legacy local deployment receipt before upgrading its journal"

/// Credential-free delivery history. A durable sent marker fences redelivery;
/// only a matching immutable owning commit receipt settles an unknown send.
type Journal(origin:Uri,graphHash:string,?stateDirectory:string,?deploymentIdentity:string) =
    do
        Durability.requireSupported()
        NativeDeployment.validateOrigin origin
        if graphHash<>"" && not(NativeDeployment.validHex 64 graphHash) then invalidArg "graphHash" "An exact deployment graph digest is required"
    let stateRoot=defaultArg stateDirectory (
        match Environment.GetEnvironmentVariable "CONVEX_DOTNET_STATE_DIRECTORY" with
        | null | "" -> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"convex-dotnet","deployment")
        | value -> value)
    let scope =
        let identity =
            match deploymentIdentity with
            | None -> origin.GetLeftPart(UriPartial.Authority)
            | Some value when not(String.IsNullOrWhiteSpace value) && value.Length<=128 &&
                              (value |> Seq.forall(fun c -> Char.IsAsciiLetterOrDigit c || c='-' || c='_')) ->
                "native-deployment-identity:"+value
            | Some _ -> invalidArg "deploymentIdentity" "A bounded durable deployment identity is required"
        SHA256.HashData(Encoding.UTF8.GetBytes identity) |> Convert.ToHexStringLower
    let directory=Path.Combine(stateRoot,scope)
    let state=Path.Combine(directory,"state.json")
    let history=Path.Combine(directory,"history")
    let ensureDirectory (path:string) =
        if not(Path.IsPathFullyQualified path) then invalidArg "stateRoot" "Deployment history requires an absolute private directory"
        let privateMode=UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        let rec create (selected:string) =
            if DirectoryInfo(selected).LinkTarget<>null then invalidOp "Deployment history cannot be a symbolic link"
            if not(Directory.Exists selected) then
                let parent=Path.GetDirectoryName selected
                if String.IsNullOrEmpty parent then invalidOp "Deployment history has no existing filesystem root"
                create parent
                Directory.CreateDirectory(selected,privateMode) |> ignore
        let selected=Path.GetFullPath path
        create selected
        let actual=File.GetUnixFileMode selected
        if (actual &&& ~~~privateMode)<>enum<UnixFileMode> 0 then invalidOp "Existing deployment history must be private"
        // Include every ancestor so a previously interrupted hierarchy creation
        // cannot leave an unflushed parent entry above an existing child.
        Durability.syncHierarchy selected
    do
        ensureDirectory stateRoot
        ensureDirectory directory
        ensureDirectory history
        if Durability.regularFileExists state && FileInfo(state).Length>65536L then invalidOp "Deployment history is not a bounded regular file"
    let lockPath=Path.Combine(directory,"delivery.lock")
    do Durability.regularFileExists lockPath |> ignore
    let lease=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)
    let stored =
        try
            let stateDocument=Durability.readState state
            if stateDocument.IsNone then None else
            use document=stateDocument.Value
            let value=document.RootElement
            if value.GetProperty("version").GetInt32()<>1 || value.GetProperty("originHash").GetString()<>scope then invalidOp "Deployment history contract is incompatible"
            let graph=value.GetProperty("graphSha256").GetString()
            let outcome=value.GetProperty("outcome").GetString()
            if not(NativeDeployment.validHex 64 graph) || not(List.contains outcome ["confirmed";"refused";"sent";"prepared";"uncertain"]) then invalidOp "Unknown deployment history state"
            let optional (name:string) length =
                let mutable item=Unchecked.defaultof<JsonElement>
                if not(value.TryGetProperty(name,&item)) || item.ValueKind=JsonValueKind.Null then None else
                let text=item.GetString()
                if not(NativeDeployment.validHex length text) then invalidOp "Deployment history receipt token is invalid"
                Some text
            let operationId=optional "operationId" 32
            let prepared=optional "startPushSha256" 64
            if prepared.IsSome && operationId.IsNone then invalidOp "Deployment history is missing its operation identity"
            Some {GraphSha256=graph;Outcome=outcome;OperationId=operationId;PreparedSha256=prepared}
        with error -> lease.Dispose();raise error
    let prior=
        match stored with
        | None -> Fresh
        | Some value ->
            match value.Outcome with
            | "confirmed" -> if value.GraphSha256=graphHash then Recorded else Fresh
            | "refused" -> if value.GraphSha256=graphHash then Refused else Fresh
            | _ -> Uncertain
    let attempt=Guid.NewGuid().ToString("N")
    let operationId=if prior=Uncertain then stored |> Option.bind(fun value -> value.OperationId) else Some attempt
    let mutable prepared =
        if prior=Uncertain then
            stored |> Option.bind(fun value ->
                match value.OperationId,value.PreparedSha256 with
                | Some id,Some digest -> Some {OperationId=id;StartPushSha256=digest}
                | _ -> None)
        else None
    let retainedGraph=if prior=Uncertain || graphHash="" then stored |> Option.map(fun value -> value.GraphSha256) |> Option.defaultValue "" else graphHash
    let write phase outcome commitTimestamp =
        let pending=outcome=JournalOutcome.Sent || outcome=JournalOutcome.Prepared
        let ceiling=if pending then 9000 else 10000
        if Directory.EnumerateFiles history |> Seq.truncate (ceiling+1) |> Seq.length >=ceiling then invalidOp "Deployment history requires maintenance before another transition"
        if not(NativeDeployment.validHex 64 retainedGraph) then invalidOp "An owning graph intent is required before writing deployment history"
        let outcome=match outcome with JournalOutcome.Sent -> "sent" | JournalOutcome.Prepared -> "prepared" | JournalOutcome.Confirmed -> "confirmed" | JournalOutcome.Refused -> "refused" | JournalOutcome.Uncertain -> "uncertain"
        let bytes=JsonSerializer.SerializeToUtf8Bytes {|version=1;originHash=scope;graphSha256=retainedGraph;phase=string phase;outcome=outcome;
            operationId=operationId |> Option.toObj;startPushSha256=prepared |> Option.map(fun value -> value.StartPushSha256) |> Option.toObj;
            commitTimestamp=commitTimestamp |> Option.toObj;recordedAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()|}
        let temporary=Path.Combine(directory,"state-"+Guid.NewGuid().ToString("N")+".tmp")
        let historyTemporary=Path.Combine(history,"history-"+Guid.NewGuid().ToString("N")+".tmp")
        let writePrivate path =
            use output=new FileStream(path,FileStreamOptions(Mode=FileMode.CreateNew,Access=FileAccess.Write,
                Share=FileShare.None,UnixCreateMode=Nullable(UnixFileMode.UserRead ||| UnixFileMode.UserWrite)))
            output.Write bytes
            output.Flush true
        try
            writePrivate temporary
            writePrivate historyTemporary
            File.Move(historyTemporary,Path.Combine(history,attempt+"-"+string phase+"-"+outcome+".json"),false)
            Durability.syncDirectory history
            File.Move(temporary,state,true)
            // The atomic replacement is not durable until its parent is flushed.
            // Only then may beforeTransition return and allow an owning send.
            Durability.syncDirectory directory
        finally
            if File.Exists temporary then File.Delete temporary
            if File.Exists historyTemporary then File.Delete historyTemporary
    member _.Prior=prior
    member _.OperationId=operationId |> Option.defaultWith(fun () -> invalidOp "Legacy delivery has no owning receipt identity")
    member _.PendingToken=prepared
    member _.RecordedGraphSha256=stored |> Option.map(fun value -> value.GraphSha256)
    member _.HistoricalOutcome=stored |> Option.map(fun value -> value.Outcome)
    member _.Write(phase:DeploymentPhase,outcome:JournalOutcome)=write phase outcome None
    member _.RecordPrepared(token:NativeDeploymentToken) =
        if token.OperationId<>attempt || prior<>Fresh || not(NativeDeployment.validHex 64 token.StartPushSha256) then invalidOp "Owning preparation differs from this fresh deployment operation"
        prepared<-Some token
        write DeploymentPhase.Preparing JournalOutcome.Prepared None
    member _.ConfirmHistorical(confirmation:NativeDeploymentConfirmation) =
        if prior<>Uncertain || prepared<>Some confirmation.Token then invalidOp "Owning commit receipt differs from the retained uncertain operation"
        write DeploymentPhase.Activating JournalOutcome.Confirmed (Some confirmation.CommitTimestamp)
    interface IDisposable with member _.Dispose()=lease.Dispose()
