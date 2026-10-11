namespace RuntimeFixture

open Convex.FSharp

// Selected static getters keep each definition's import capabilities separate.
// The backend evaluates exactly one admitted getter in the owning DFS visit.
type NativeDefinitions private () =
    static member App =
        NativeComponentDefinitions.app [] [NativeComponentDefinitions.mount "probe" "../probe"] NativeComponentDefinitions.emptyExports None
    static member Child =
        NativeComponentDefinitions.child "probe" [] [] [] NativeComponentDefinitions.emptyExports
    static member AppWithEnvironment =
        let value=InvocationRuntime.environmentVariable "CONVEX_NATIVE_COMPONENT_PROBE" |> Option.defaultValue "missing"
        let mount={NativeComponentDefinitions.mount "probe" "../probe" with Environment=["PROBE",EnvironmentBinding.Literal value]}
        NativeComponentDefinitions.app [] [mount] NativeComponentDefinitions.emptyExports None
    static member ChildWithDeclaredEnvironment =
        NativeComponentDefinitions.child "probe" [] [ComponentEnvironment.required "PROBE" (V.string())] [] NativeComponentDefinitions.emptyExports
    static member ChildWithEnvironment =
        InvocationRuntime.environmentVariable "CONVEX_NATIVE_COMPONENT_PROBE" |> ignore
        NativeDefinitions.Child
    static member AppWithClock =
        InvocationRuntime.nowMilliseconds() |> ignore
        NativeDefinitions.App
    static member AppWithRandom =
        InvocationRuntime.random() |> ignore
        NativeDefinitions.App
