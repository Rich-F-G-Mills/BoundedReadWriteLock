
namespace Controller

[<AutoOpen>]
module Program =

    open System
    open System.Threading

    open Elmish
    open Elmish.WPF


    type AppEvent =
        | RequestRead
        | RequestWrite
        | RequestPause
        | RequestResume
        | NotifyReadCompleted
        | NotifyWriteCompleted
        | NotifyPauseCompleted
        | Increment
        | Decrement
        | ReadWriteStateChange of ObservableDispatcherState
        | ReadWriteEvent of ObservableDispatcherEvent

    type AppModel =
        {
            Delta: int
            ReadWriteState: ObservableDispatcherState option
            LastEvent: ObservableDispatcherEvent option
        }


    let appInit () =
        { Delta = 0; ReadWriteState = None; LastEvent = None }, Cmd.none

    let appUpdate (rwd: ReadWriteDispatcher) evt model =
        match evt, model with
        | RequestRead, _ ->
            model, Cmd.OfAsync.perform (rwd.RequestRead) () (fun _ -> NotifyReadCompleted)
        | RequestWrite, { Delta = delta } ->
            model, Cmd.OfAsync.perform (rwd.RequestWrite) delta (fun _ -> NotifyWriteCompleted)
        | RequestPause, _ ->
            model, Cmd.OfAsync.perform (rwd.RequestPause) () (fun _ -> NotifyPauseCompleted)
        | RequestResume, _ ->
            do rwd.RequestResume ()

            model, Cmd.none
        | Increment, { Delta = delta } ->
            { model with Delta = delta + 1 }, Cmd.none
        | Decrement, { Delta = delta } ->
            { model with Delta = delta - 1 }, Cmd.none
        | NotifyReadCompleted, _
        | NotifyWriteCompleted, _
        | NotifyPauseCompleted, _ ->
            model, Cmd.none
        | ReadWriteStateChange newState, model ->
            { model with ReadWriteState = Some newState }, Cmd.none
        | ReadWriteEvent event, model ->
            { model with LastEvent = Some event }, Cmd.none
            

    let appBindings () =
        let (|IsNotStarted|IsPaused|IsPausing|IsRunning|) = function
            | { ReadWriteState = Some state } ->
                match state with
                | PausedState _  -> IsPaused
                | ReadingState (_, _, true, _, _)
                | WritingState (_, true, _, _) -> IsPausing
                | _ -> IsRunning
            | _ -> IsNotStarted
    
        [
            "IsPaused" |> Binding.oneWay (function | IsPaused | IsPausing -> true | _ -> false)
            "IsRunning" |> Binding.oneWay (function | IsPaused | IsPausing -> false | _ -> true)
            "Delta" |> Binding.oneWay (fun { Delta = delta } -> delta)
            "ReadWriteState" |> Binding.oneWayOpt (function
                | { ReadWriteState = Some rwState } -> Some <| rwState.ToString()
                | _ -> None)
            "ReadWriteEvent" |> Binding.oneWayOpt (function
                | { LastEvent = Some rwEvent } -> Some <| rwEvent.ToString()
                | _ -> None)
            "RequestPauseOrResume" |> Binding.cmdIf (function
                | IsPaused | IsPausing -> Some RequestResume
                | IsRunning -> Some RequestPause
                | _ -> None)
            "RequestRead" |> Binding.cmd RequestRead
            "RequestWrite" |> Binding.cmd RequestWrite   
            "Increment" |> Binding.cmd Increment
            "Decrement" |> Binding.cmd Decrement        
        ]

    let readWriteObserver (rwd: ReadWriteDispatcher) _ =
        let subscriber dispatch =
            rwd.DispatcherState
            |> Observable.add (ReadWriteStateChange >> dispatch)

            rwd.DispatcherEvents
            |> Observable.add (ReadWriteEvent >> dispatch)

        Cmd.ofSub subscriber
        

    [<STAThread>]
    [<EntryPoint>]
    let main _ =
        let mainWindow = new View.MainWindow ()

        let rwd =
            new ReadWriteDispatcher(3, TimeSpan.FromMilliseconds 1000, TimeSpan.FromMilliseconds 1500, CancellationToken.None)

        Program.mkProgramWpf appInit (appUpdate rwd) appBindings
        |> Program.withSubscription (readWriteObserver rwd)
        |> Program.runWindow mainWindow