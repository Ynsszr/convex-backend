namespace ConvexApp.Web

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Components
open Microsoft.AspNetCore.Components.Rendering
open Microsoft.AspNetCore.Components.Web
open Microsoft.AspNetCore.Components.WebAssembly.Hosting
open Microsoft.Extensions.Configuration
open Convex.Client
open ConvexApp
open ConvexApp.Generated

type private Ui = { Draft: string; Sending: bool; Notice: string; Snapshot: Reactive.Snapshot option }

type App() as this =
    inherit ComponentBase()
    let messages=Reactive.query Api.messages.list ()
    let count=Reactive.query Api.messages.count ()
    let stopped=new CancellationTokenSource()
    let mutable client:Reactive.Client option=None
    let mutable subscriptions:IDisposable list=[]
    let mutable state={Draft="";Sending=false;Notice="";Snapshot=None}
    let endpoint () = ConvexHttp.endpoint (this.Configuration["ConvexUrl"])
    let send () = task {
        if not state.Sending && not(String.IsNullOrWhiteSpace state.Draft) then
            let input=state.Draft
            state<-{state with Sending=true;Notice=""}
            let shared=client |> Option.defaultWith(fun () -> invalidOp "The reactive client is not initialized.")
            let optimistic snapshot =
                match Reactive.Snapshot.read messages snapshot with
                | Reactive.Value.Ready existing ->
                    let next=(existing@[{Message.Text=input}]) |> List.truncate 50
                    snapshot |> Reactive.Snapshot.set messages next |> Reactive.Snapshot.set count next.Length
                | _ -> snapshot
            let! result=shared.MutateAsync(Api.messages.send,{SendMessage.Text=input},optimisticUpdate=optimistic)
            state<-
                match result with
                | ConvexHttp.Completed _ -> {state with Draft="";Sending=false}
                | ConvexHttp.Refused _ -> {state with Sending=false;Notice="The message was refused. Check the backend log."}
                | ConvexHttp.Uncertain _ -> {state with Sending=false;Notice="Delivery is uncertain. Check the live messages before trying again."}
    }
    [<Inject>]
    member val Configuration:IConfiguration=Unchecked.defaultof<_> with get,set
    member private _.Refresh() = base.StateHasChanged()
    member private _.ApplySnapshot(snapshot) =
        base.InvokeAsync(Action(fun()->state<-{state with Snapshot=Some snapshot};this.Refresh())) |> ignore
    override _.OnInitialized() =
        let shared=new Reactive.Client(endpoint())
        client<-Some shared
        subscriptions<-[shared.Watch messages;shared.Watch count]
        let observer=shared.Observe(this.ApplySnapshot)
        subscriptions<-observer::subscriptions
        shared.RunAsync(stopped.Token) |> ignore
    override _.BuildRenderTree(builder:RenderTreeBuilder) =
        let value query = state.Snapshot |> Option.map(Reactive.Snapshot.read query) |> Option.defaultValue Reactive.Value.Loading
        builder.OpenElement(0,"header")
        builder.OpenElement(1,"small");builder.AddContent(2,"NATIVE F# · CONVEX");builder.CloseElement()
        builder.OpenElement(3,"h1");builder.AddContent(4,"Write F#. Stay in sync.");builder.CloseElement()
        builder.OpenElement(5,"p");builder.AddContent(6,"F# in the browser and backend. Two reactive queries, one shared connection, and no manually synchronized message store.");builder.CloseElement()
        builder.CloseElement()
        builder.OpenElement(7,"div");builder.AddAttribute(8,"class","status")
        builder.OpenElement(9,"span");builder.AddAttribute(10,"id","connection")
        let status=state.Snapshot |> Option.map(Reactive.Snapshot.connection) |> Option.defaultValue Reactive.Connection.Connecting
        builder.AddContent(11,(match status with Reactive.Connection.Connected -> "Live" | Reactive.Connection.Failed _ -> "Connection refused" | _ -> "Connecting…"));builder.CloseElement()
        builder.OpenElement(12,"span");builder.AddAttribute(13,"id","message-count")
        builder.AddContent(14,(match value count with Reactive.Value.Ready number -> sprintf "%d visible messages" number | _ -> "Loading messages…"));builder.CloseElement()
        builder.CloseElement()
        builder.OpenElement(15,"form")
        builder.AddAttribute(16,"onsubmit",EventCallback.Factory.Create(this,Func<Task>(fun()->send():>Task)))
        builder.AddEventPreventDefaultAttribute(17,"onsubmit",true)
        builder.OpenElement(18,"input");builder.AddAttribute(19,"id","message-input");builder.AddAttribute(20,"placeholder","Write a message…")
        builder.AddAttribute(21,"aria-label","Message");builder.AddAttribute(22,"maxlength",2000)
        builder.AddAttribute(23,"value",state.Draft)
        builder.AddAttribute(24,"oninput",EventCallback.Factory.Create<ChangeEventArgs>(this,Action<ChangeEventArgs>(fun event->state<-{state with Draft=string event.Value})))
        builder.CloseElement()
        builder.OpenElement(25,"button");builder.AddAttribute(26,"type","submit");builder.AddAttribute(27,"disabled",state.Sending || String.IsNullOrWhiteSpace state.Draft)
        builder.AddContent(28,if state.Sending then "Sending…" else "Send");builder.CloseElement();builder.CloseElement()
        if state.Notice<>"" then
            builder.OpenElement(29,"p");builder.AddAttribute(30,"class","error");builder.AddContent(31,state.Notice);builder.CloseElement()
        builder.OpenElement(32,"ul");builder.AddAttribute(33,"id","messages")
        match value messages with
        | Reactive.Value.Ready values ->
            for item in values do
                builder.OpenElement(34,"li");builder.AddContent(35,item.Text);builder.CloseElement()
        | Reactive.Value.Failed _ -> builder.AddContent(36,"Messages could not be loaded.")
        | Reactive.Value.Loading -> ()
        builder.CloseElement()
        builder.OpenElement(37,"footer")
        builder.AddContent(38,"Open a second tab and send a message. The list and visible count update together. This example displays up to 50 messages.")
        builder.CloseElement()
    interface IDisposable with
        member _.Dispose() =
            stopped.Cancel()
            for subscription in subscriptions do subscription.Dispose()
            client |> Option.iter(fun value->(value:>IDisposable).Dispose())
            stopped.Dispose()

module Program =
    [<EntryPoint>]
    let main arguments =
        let builder=WebAssemblyHostBuilder.CreateDefault(arguments)
        builder.RootComponents.Add<App>("#app")
        builder.Build().RunAsync().GetAwaiter().GetResult()
        0
