
module Controller =

    open System
    open System.Threading


    type private DispatchReadCommand =
        | ReadRequest of ReplyChan: AsyncReplyChannel<int> * CToken: CancellationToken

    type private DispatchWriteCommand =
        | WriteRequest of NewVal: int * ReplyChan: AsyncReplyChannel<int> * CToken: CancellationToken


    type ReadWriteDispatcher (maxReads: int, readDelay: TimeSpan, writeDelay: TimeSpan, rwdcToken: CancellationToken) =
        let readWriteLock = new AsyncEx.AsyncReaderWriterLock ()
        let maxReadsLock = new AsyncEx.AsyncSemaphore (maxReads)

        let mutable state = 0              

        let readRequestProcessor =
            MailboxProcessor.Start((fun mbox ->
                let rec loop () =                   
                    async {
                        match! mbox.Receive () with
                        | ReadRequest (arc, ct) ->
                            printfn "Read request received."

                            let combinedCToken =
                                CancellationTokenSource.CreateLinkedTokenSource(rwdcToken, ct).Token

                            let! readReleaser =
                                (maxReadsLock.LockAsync(combinedCToken)).AsTask () |> Async.AwaitTask

                            let! readWriteReleaser =
                                (readWriteLock.ReaderLockAsync (combinedCToken)).AsTask () |> Async.AwaitTask
                                
                            async {
                                do! Async.Sleep readDelay

                                let result = Volatile.Read &state

                                do arc.Reply result

                                do readReleaser.Dispose()
                                do readWriteReleaser.Dispose ()

                                printfn "Read completed: %i" result
                            } |> Async.StartImmediate                                         

                        do! loop ()
                    }

                loop ()), cancellationToken = rwdcToken)

        let writeRequestProcessor =
            MailboxProcessor.Start((fun mbox ->
                let rec loop () =                   
                    async {
                        match! mbox.Receive () with
                        | WriteRequest (newVal, arc, ct) ->
                            printfn "Write request received: %i" newVal

                            let combinedCToken =
                                CancellationTokenSource.CreateLinkedTokenSource(rwdcToken, ct).Token

                            use! readWriteReleaser =
                                (readWriteLock.WriterLockAsync (combinedCToken)).AsTask () |> Async.AwaitTask

                            do! Async.Sleep writeDelay

                            let newState = Interlocked.Add(&state, newVal)

                            do arc.Reply newState

                            printfn "Write completed. New state: %i" newState

                        do! loop ()
                    }

                loop ()), cancellationToken = rwdcToken)
                

        member this.RequestRead (cToken: CancellationToken): Async<int> =
            let msgBuilder arc =
                ReadRequest (arc, cToken)

            readRequestProcessor.PostAndAsyncReply msgBuilder

        member this.RequestWrite (newVal: int, cToken: CancellationToken): Async<int> =
            let msgBuilder arc =
                WriteRequest (newVal, arc, cToken)

            writeRequestProcessor.PostAndAsyncReply msgBuilder

            
    [<EntryPoint>]
    let main _ =
        let cts = new CancellationTokenSource()

        let rwd = new ReadWriteDispatcher(5, TimeSpan.FromSeconds 1, TimeSpan.FromMilliseconds 200, cts.Token)

        let readOp =
            async {
                do! Async.Sleep (TimeSpan.FromSeconds 1)

                do! rwd.RequestRead (CancellationToken.None) |> Async.Ignore
            }

        let result =
            async {
                do Async.StartImmediate readOp

                do! Seq.init 10 (fun idx -> rwd.RequestWrite (idx, CancellationToken.None))
                    |> Async.Parallel
                    |> Async.Ignore

                return!
                    Seq.init 25 (fun _ -> rwd.RequestRead CancellationToken.None)
                    |> Async.Parallel
            } |> Async.RunSynchronously

        printfn "State: %i" (Array.max result)

        0


