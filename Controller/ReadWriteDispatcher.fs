
namespace Controller

[<AutoOpen>]
module ReadWriteDispatcher =

    open System
    open System.Collections.Generic
    open System.Threading    

    open System.Reactive
    open FSharp.Control.Reactive


    type private WriteRequest =
        | WriteRequest of NewVal: int * Reply: (int -> unit)

    type private ReadRequest =
        | ReadRequest of Reply: (int -> unit)

    type private DispatcherEvent =
        | ReadCompleted of Reply: (int -> unit)
        | WriteCompleted of NewVal: int * Reply: (int -> unit)
        | ReadRequested of ReadRequest
        | WriteRequested of WriteRequest
        | PauseRequested of Reply: (int -> unit)
        | ResumeRequested

    type private DispatcherState =
        | Idle of CurrentValue: int
        | Paused of CurrentValue: int
        | Reading of CountReading: int * CurrentValue: int * PauseReq'd: (int -> unit) option
        | Writing of CurrentValue: int * PauseReq'd: (int -> unit) option

        member this.CurrentValue =
            match this with
            | Idle cv
            | Paused cv
            | Reading (cv, _, _)
            | Writing (cv, _) -> cv

    type ObservableDispatcherState =
        | NotStartedState
        | IdleState of CurrentValue: int
        | PausedState of CurrentValue: int * CountPendingWrites: int * CountPendingReads: int
        | ReadingState of CountReading: int * CurrentValue: int  * PauseReq'd: bool * CountPendingWrites: int * CountPendingReads: int
        | WritingState of CurrentValue: int  * PauseReq'd: bool* CountPendingWrites: int * CountPendingReads: int

        override this.ToString () =
            match this with
            | NotStartedState ->
                "Not started"
            | IdleState cv ->
                sprintf "Idle: Current value of %i" cv
            | PausedState (cv, pws, prs) ->
                sprintf "Paused: %i/%i pending writes/reads with current value of %i" pws prs cv
            | ReadingState (cr, cv, p, pws, prs) ->
                sprintf "Reading (Awaiting %i writes): %i/%i active/pending reads with current value of %i%s" pws cr prs cv (if p then " [Pause Requested]" else "")
            | WritingState (cv, p, pws, prs) ->
                sprintf "Writing: %i/%i pending writes/reads with current value of %i%s" pws prs cv (if p then " [Pause Requested]" else "")


    type ObservableDispatcherEvent =
        | ReadCompletedEvent of CurrentValue: int
        | WriteCompletedEvent of CurrentValue:int * NewVal: int
        | ReadRequestedEvent
        | WriteRequestedEvent of NewVal: int
        | PauseRequestedEvent
        | ResumeRequestedEvent

    type ReadWriteDispatcher (maxReads: int, readDelay: TimeSpan, writeDelay: TimeSpan, rwdcToken: CancellationToken) =
        let dispatcherStateSubject =
            Subject.behavior NotStartedState

        let dispatcherEventSubject =
            new Subjects.Subject<ObservableDispatcherEvent>()

        let loopFactory (mbox: MailboxProcessor<DispatcherEvent>) =
            let asyncRead (ReadRequest reply) =
                async {
                    do! Async.Sleep readDelay

                    do ReadCompleted reply |> mbox.Post
                }

            let asyncWrite (WriteRequest (newVal, reply)) =
                async {
                    do! Async.Sleep writeDelay

                    do WriteCompleted (newVal, reply) |> mbox.Post
                }

            let pendingReads = new Queue<_>()
            let pendingWrites = new Queue<_>()

            let (|IsEmpty|Pending|) (q: Queue<'Q>) =
                if q.Count = 0 then IsEmpty else Pending

            let rec loop state =
                let externalState =
                    match state with
                    | Idle cv -> IdleState cv
                    | Paused cv -> PausedState (cv, pendingWrites.Count, pendingReads.Count)
                    | Reading (cr, cv, p) -> ReadingState (cr, cv, p.IsSome, pendingWrites.Count, pendingReads.Count)
                    | Writing (cv, p) -> WritingState (cv, p.IsSome, pendingWrites.Count, pendingReads.Count)

                do dispatcherStateSubject.OnNext externalState

                async {
                    let! event = mbox.Receive ()

                    let newState =
                        match event with
                        | ReadCompleted readReply ->
                            match state with
                            | Idle _
                            | Writing _
                            | Paused _ -> None

                            | Reading (cr, cv, p) ->
                                do readReply cv

                                match pendingWrites, pendingReads, p with                                
                                | Pending, _, None when cr = 1 ->
                                    do Async.StartImmediate <| asyncWrite (pendingWrites.Dequeue ())

                                    Some <| Writing (cv, p)

                                | Pending, _, None
                                | _, _, Some _ when cr > 1 ->
                                    Some <| Reading (cr - 1, cv, p)

                                | IsEmpty, IsEmpty, None when cr = 1 ->
                                    Some <| Idle cv

                                | _, _, Some pauseReply when cr = 1 ->
                                    do pauseReply cv

                                    Some <| Paused cv

                                | IsEmpty, IsEmpty, None when cr > 1 ->
                                    Some <| Reading (cr - 1, cv, p)   
                                
                                | IsEmpty, Pending, None when cr = maxReads ->
                                    do Async.StartImmediate <| asyncRead (pendingReads.Dequeue())

                                    Some <| Reading (maxReads, cv, p)

                                | _ -> None

                        | WriteCompleted (nv, writeReply) ->
                            match state with
                            | Idle _
                            | Reading _
                            | Paused _ -> None

                            | Writing (cv, p) ->
                                do writeReply (cv + nv)

                                match pendingWrites, pendingReads, p with
                                | Pending, _, None ->
                                    do Async.StartImmediate <| asyncWrite (pendingWrites.Dequeue())

                                    Some <| Writing (cv + nv, p)

                                | IsEmpty, Pending, None ->
                                    let newImmedReads =
                                        Seq.init maxReads id
                                        |> Seq.choose (fun _ ->
                                            match pendingReads.TryDequeue () with
                                            | true, pr -> Some pr
                                            | false, _ -> None)
                                        |> Seq.toList                                    

                                    newImmedReads
                                    |> Seq.map asyncRead                                    
                                    |> Async.Parallel
                                    |> Async.Ignore
                                    |> Async.StartImmediate

                                    Some <| Reading (newImmedReads.Length, cv + nv, p)

                                | IsEmpty, IsEmpty, None ->
                                    Some <| Idle (cv + nv)

                                | _, _, Some pauseReply ->
                                    do pauseReply (cv + nv)

                                    Some <| Paused (cv + nv)

                        | ReadRequested rr ->
                            match state with
                            | Idle cv ->
                                do Async.StartImmediate <| asyncRead rr

                                Some <| Reading (1, cv, None)

                            | Reading (cr, cv, p) ->
                                match pendingWrites, pendingReads, p with
                                | IsEmpty, IsEmpty, None when cr < maxReads ->
                                    do Async.StartImmediate <| asyncRead rr

                                    Some <| Reading (cr + 1, cv, p)

                                | IsEmpty, Pending, _ when cr < maxReads -> None

                                | _ ->                   
                                    do pendingReads.Enqueue rr

                                    Some state

                            | Writing _
                            | Paused _ ->
                                do pendingReads.Enqueue rr

                                Some state

                        | WriteRequested wr ->
                            match state with
                            | Idle cv ->
                                do Async.StartImmediate <| asyncWrite wr

                                Some <| Writing (cv, None)

                            | Paused _
                            | Reading _
                            | Writing _ ->
                                do pendingWrites.Enqueue wr

                                Some state

                        | PauseRequested pauseReply ->
                            match state with
                            | Idle cv
                            | Paused cv ->
                                do pauseReply cv

                                Some <| Paused cv

                            | Reading (cr, cv, _) ->
                                Some <| Reading (cr, cv, Some pauseReply)

                            | Writing (cv, _) ->
                                Some <| Writing (cv, Some pauseReply)

                        | ResumeRequested ->
                            match state with
                            | Idle cv
                            | Paused cv ->
                                match pendingWrites, pendingReads with
                                | IsEmpty, IsEmpty ->
                                    Some <| Idle cv

                                | Pending, _ ->
                                    do Async.StartImmediate <| asyncWrite (pendingWrites.Dequeue())

                                    Some <| Writing (cv, None)

                                | IsEmpty, Pending ->
                                    let newImmedReads =
                                        Seq.init maxReads id
                                        |> Seq.choose (fun _ ->
                                            match pendingReads.TryDequeue () with
                                            | true, pr -> Some pr
                                            | false, _ -> None)
                                        |> Seq.toList                                    

                                    newImmedReads
                                    |> Seq.map asyncRead                                    
                                    |> Async.Parallel
                                    |> Async.Ignore
                                    |> Async.StartImmediate

                                    Some <| Reading (newImmedReads.Length, cv, None)
                             
                            | Reading (cr, cv, _) ->
                                Some <| Reading (cr, cv, None)

                            | Writing (cv, _) ->
                                Some <| Writing (cv, None)

                    match newState with
                    | Some newState' ->
                        let cv =
                            newState'.CurrentValue

                        let externalEvent =
                            match event with
                            | ReadCompleted _ -> ReadCompletedEvent cv
                            | WriteCompleted (nv, _) -> WriteCompletedEvent (cv, nv)
                            | ReadRequested _ -> ReadRequestedEvent
                            | WriteRequested (WriteRequest (nv, _)) -> WriteRequestedEvent nv
                            | PauseRequested _ -> PauseRequestedEvent
                            | ResumeRequested -> ResumeRequestedEvent

                        dispatcherEventSubject.OnNext externalEvent

                        do! loop newState'

                    | None ->
                        failwith "Unexpected state."
                }

            loop (Idle 0)

        let dispatcher =
            MailboxProcessor.Start(loopFactory, rwdcToken)

        member this.RequestRead () =
            dispatcher.PostAndAsyncReply (fun reply ->
                 ReadRequest (reply.Reply) |> ReadRequested)

        member this.RequestWrite (newVal: int) =
            dispatcher.PostAndAsyncReply (fun reply ->
                WriteRequest (newVal, reply.Reply) |> WriteRequested)

        member this.RequestPause () =
            dispatcher.PostAndAsyncReply (fun reply -> PauseRequested reply.Reply)

        member this.RequestResume () =
            dispatcher.Post ResumeRequested

        member val DispatcherState =
            // This 'hides' the original Subject.
            // Could cast to IObervable, but wouldn't stop anyone up-casting.
            dispatcherStateSubject |> Observable.asObservable

        member val DispatcherEvents =
            dispatcherEventSubject |> Observable.asObservable
