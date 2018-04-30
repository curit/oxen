module oxen.Tests

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks

open Foq
open Xunit
open FsUnit.Xunit

open oxen
open StackExchange.Redis

type Data = {
    value: string
}

type Order = {
    order: int
}

type OtherData = {
    Value: string
}

type Agent<'a> = MailboxProcessor<'a>


/// Commands the semaphore can receive
type SemaphoreCommand =
    |Release
    |Wait of AsyncReplyChannel<unit>

/// A constructor function that creates a semaphore
let semaphore slots =
    Agent.Start
    <| fun inbox ->
        let rec loop c (w:AsyncReplyChannel<unit> list) =
          async {
            let! command = inbox.Receive()
            match command with
            | Release -> 
                let slotsTaken = c + 1
                return!
                    match slotsTaken with 
                    | _ when slotsTaken >= slots ->
                        // all slots taken reply that we're done
                        w |> List.iter(fun t -> t.Reply()) 
                        loop slotsTaken w
                    | _ -> 
                        loop slotsTaken w
            | Wait a -> 
                if c >= slots then 
                    // all slots allready taken reply immediately
                    a.Reply()

                return! loop c (a::w)
        }
        loop 0 []


let taskUnit () = Task.Factory.StartNew(fun () -> ())
let taskIncr () = Task.Factory.StartNew(fun () -> 1L)
let taskLPush () = Task.Factory.StartNew(fun () -> 1L)
let taskLong () = Task.Factory.StartNew(fun () -> 1L)
let taskTrue () = Task.Factory.StartNew(fun () -> true)
let taskFalse () = Task.Factory.StartNew(fun () -> false)
let taskRedisResult () = Task.Factory.StartNew(fun () -> Mock<RedisResult>().Create());

/// change default hash set order to check independence of field order
let taskJobHash () = Task.Factory.StartNew(fun () ->
    [|
        HashEntry(toValueStr "opts", toValueStr "")
        HashEntry(toValueStr "data", toValueStr "{ \"value\": \"test\" }")
        HashEntry(toValueStr "progress", toValueI32 1)
        HashEntry(toValueStr "delay", toValueFloat 0.)
        HashEntry(toValueStr "timestamp", toValueFloat (DateTime.Now |> toUnixTime))
        HashEntry(toValueStr "stacktrace", toValueStr "errrrrrr")
    |])

let taskValues (value:int64) = Task.Factory.StartNew(fun () -> [| RedisValue.op_Implicit(value) |])
let taskEmptyValues () = Task.Factory.StartNew(fun () -> [| |])
let taskEmptyValue () = Task.Factory.StartNew(fun () -> new RedisValue())

type SemaphoreFixture () =
    [<Fact>]
    let ``should wait for all slots to be taken`` () =
        // Given
        use traficLight = semaphore 5
        let green = traficLight.PostAndAsyncReply (fun t -> Wait t) |> Async.StartAsTask

        // When 
        traficLight.Post Release
        traficLight.Post Release
        traficLight.Post Release
        traficLight.Post Release

        // Then
        green.IsCompleted |> should be False
        traficLight.Post Release
        green.Result |> should equal None
        green.IsCompleted |> should be True

    [<Fact>]
    let ``should wait for all slots to be taken even if there's just one`` () =
        // Given
        use traficLight = semaphore 1
        let green = traficLight.PostAndAsyncReply (fun t -> Wait t) |> Async.StartAsTask

        // When 
        Thread.Sleep(100)
        green.IsCompleted |> should be False
        traficLight.Post Release
        
        // Then
        green.Result
        green.IsCompleted |> should be True

    [<Fact>]
    let ``should wait for all slots to be taken even if there are none`` () =
        // Given
        use traficLight = semaphore 0
        let green = traficLight.PostAndAsyncReply (fun t -> Wait t) |> Async.StartAsTask

        // When 
        // nothing happens
                
        // Then
        green.Result
        green.IsCompleted |> should be True

type JobFixture () =
    [<Fact>]
    let ``should create a new job from given json data`` () =
        // Given
        let db = Mock<IDatabase>().Create();
        let sub = Mock<ISubscriber>.With(fun s ->
            <@
                s.PublishAsync(any(), any(), any()) --> taskLong()
            @>
        )

        let q = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))

        // When
        let job = Job<_>.fromData(q, 1L, "{ \"value\": \"test\" }", "", 1, DateTime.Now |> toUnixTime, None, None, None)

        // Then
        job.data.value |> should equal "test"
        job.jobId |> should equal 1L
        job._progress |> should equal 1

    [<Fact>]
    let ``should create a new job from given json data and return json data`` () =
        // Given
        let db = Mock<IDatabase>().Create();
        let sub = Mock<ISubscriber>.With(fun s ->
            <@
                s.PublishAsync(any(), any(), any()) --> taskLong()
            @>
        )

        let q = Queue<Data, OtherData>("stuff", (fun () -> db), (fun () -> sub))

        // When
        let job = Job<Data, OtherData>.fromData(q, 1L, "{ \"value\": \"test\" }", "", 1, DateTime.Now |> toUnixTime, None, None, Some "{ \"Value\": \"return value\" }")

        // Then
        job.data.value |> should equal "test"
        job.jobId |> should equal 1L
        job._progress |> should equal 1
        job.returnvalue.Value |> should equal { Value = "return value" }

    [<Fact>]
    let ``should get a job from the cache and make it into a real one`` () =
        // Given
        let db = Mock<IDatabase>.With(fun d ->
            <@
                d.HashGetAllAsync (any(), any()) --> taskJobHash()
            @>
        )
        let sub = Mock<ISubscriber>.With(fun s ->
            <@
                s.PublishAsync(any(), any(), any()) --> taskLong()
            @>
        )
        let q = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))

        // When
        let job = Job<_>.fromId(q, 1L) |> Async.RunSynchronously

        // Then
        job.data.value |> should equal "test"
        job.jobId |> should equal 1L
        job._progress |> should equal 1

    [<Fact>]
    let ``should be able to take a lock on a job`` () =
        // Given
        let db = Mock<IDatabase>.With(fun d ->
            <@
                d.StringSetAsync (any(), any(), any(), any()) --> taskTrue()
            @>
        )
        let sub = Mock<ISubscriber>.With(fun s ->
            <@
                s.PublishAsync(any(), any(), any()) --> taskLong()
            @>
        )

        let q = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))
        let job = {
            jobId = 1L
            queue = q
            data = { value = "string" }
            opts = None
            _progress = 0
            delay = None
            timestamp = DateTime.Now
            stacktrace = None
            returnvalue = None
        }

        // When
        let taken = job.takeLock (Guid.NewGuid ()) |> Async.RunSynchronously

        // Then
        taken |> should be True
        verify <@ db.StringSetAsync (any(), any(), any(), When.NotExists) @> once


    [<Fact>]
    let ``should be able to renew a lock on a job`` () =
        // Given
        let db = Mock<IDatabase>.With(fun d ->
            <@
                d.StringSetAsync (any(), any(), any(), any()) --> taskTrue()
            @>
        )
        let sub = Mock<ISubscriber>.With(fun s ->
            <@
                s.PublishAsync(any(), any(), any()) --> taskLong()
            @>
        )

        let q = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))
        let job = {
            jobId = 1L
            queue = q
            data = { value = "string" }
            opts = None
            _progress = 0
            delay = None
            timestamp = DateTime.Now
            stacktrace = None
            returnvalue = None
        }

        // When
        let taken = job.takeLock (Guid.NewGuid (), true) |> Async.RunSynchronously

        // Then
        taken |> should be True
        verify <@ db.StringSetAsync (any(), any(), any(), When.Always) @> once

    [<Fact>]
    let ``should be able to move job to completed`` () =
        async {
            // Given
            let trans = Mock<ITransaction>.With(fun t ->
                <@
                    t.ListRemoveAsync (any(), any(), any(), any()) --> taskLong()
                    t.SetAddAsync (any(), (any():RedisValue), any()) --> taskTrue()
                    t.ExecuteAsync () --> taskTrue()
                @>
            )

            let db = Mock<IDatabase>.With(fun d ->
                <@
                    d.HashSetAsync(any(), any()) --> taskUnit()
                    d.StringIncrementAsync(any()) --> taskIncr()
                    d.ListLeftPushAsync(any(), any(), any(), any()) --> taskLPush()
                    d.CreateTransaction() --> trans
                @>
            )
            let sub = Mock<ISubscriber>.With(fun s ->
                <@
                    s.PublishAsync(any(), any(), any()) --> taskLong()
                @>
            )

            let q = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))
            let! job = Job<Data>.create(q, 1L, { value = "test" }, None)

            // When
            let! result = job.moveToCompleted()

            // Then
            result |> should be True
            verify <@ trans.ExecuteAsync () @> once
            verify <@ trans.ListRemoveAsync (any(), any(), any(), any()) @> once
            verify <@ trans.SetAddAsync (any(), (any():RedisValue), any()) @> once
            verify <@ db.CreateTransaction () @> once
            ()
        } |> Async.RunSynchronously

type QueueFixture () =
    [<Fact>]
    let ``should be able to add a job to the queue`` () =

        // Given
        let trans = Mock<ITransaction>.With(fun t ->
            <@
                t.ListLeftPushAsync(any(), any(), any(), any()) --> taskLPush()
                t.PublishAsync (any(), any(), any()) --> taskLong ()
                t.ExecuteAsync () --> taskTrue()
            @>
        )
        let db = Mock<IDatabase>.With(fun d ->
            <@
                d.HashSetAsync(any(), any()) --> taskUnit()
                d.StringIncrementAsync(any()) --> taskIncr()
                d.CreateTransaction(any()) --> trans
            @>
        )
        let sub = Mock<ISubscriber>.With(fun s ->
            <@
                s.PublishAsync(any(), any(), any()) --> taskLong()
            @>
        )
        let queue = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))

        // When
        let job = queue.add ({value = "test"}) |> Async.RunSynchronously

        // Then
        job.data.value |> should equal "test"
        job.jobId |> should equal 1L
        verify <@ db.HashSetAsync(any(), any()) @> once
        verify <@ db.StringIncrementAsync(any()) @> once
        verify <@ trans.ListLeftPushAsync(any(), any(), any(), any()) @> once
        verify <@ trans.PublishAsync(any(), any(), any()) @> once

    [<Fact>]
    let ``toKey should return a key that works with bull`` () =
        // Given
        let db = Mock<IDatabase>().Create();
        let sub = Mock<ISubscriber>.With(fun s ->
            <@
                s.PublishAsync(any(), any(), any()) --> taskLong()
            @>
        )

        let queue = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))

        // When
        let result = queue.toKey("stuff")

        // Then
        result |> should equal (RedisKey.op_Implicit ("bull:stuff:stuff"))

    [<Fact>]
    let ``report progress and listen to event on queue`` () =
        async {
            // Given
            let trans = Mock<ITransaction>.With(fun t ->
                <@
                    t.ListLeftPushAsync(any(), any(), any(), any()) --> taskLPush()
                    t.PublishAsync (any(), any(), any()) --> taskLong ()
                    t.ExecuteAsync () --> taskTrue()
                @>
            )

            let db = Mock<IDatabase>.With(fun d ->
                <@
                    d.HashSetAsync(any(), any()) --> taskUnit()
                    d.StringIncrementAsync(any()) --> taskIncr()
                    d.CreateTransaction(any()) --> trans
                @>
            )

            let sub = Mock<ISubscriber>.With(fun s ->
                <@
                    s.PublishAsync(any(), any(), any()) --> taskLong()
                @>
            )

            let queue = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))

            let! job = queue.add({ value = "test" })
            let eventFired = ref false
            queue.on.Progress.Add(
                fun e ->
                    eventFired := true
                    match e.progress with
                    | Some x -> x |> should equal 100
                    | None -> failwith "progress should not be null"
            )

            // When
            do! job.progress 100

            // Then
            !eventFired |> should be True
            verify <@ trans.ListLeftPushAsync(any(), any(), any(), any()) @> once
            verify <@ trans.PublishAsync(any(), any(), any()) @> once
        } |> Async.RunSynchronously

    [<Fact>]
    let ``should be able to get failed jobs`` () =
        async {
            // Given
            let trans = Mock<ITransaction>.With(fun t ->
                <@
                    t.ListRemoveAsync (any(), any(), any(), any()) --> taskLong()
                    t.SetAddAsync (any(), (any():RedisValue), any()) --> taskTrue()
                    t.ExecuteAsync () --> taskTrue()
                    t.ListLeftPushAsync(any(), any(), any(), any()) --> taskLPush()
                    t.PublishAsync(any(), any(), any()) --> taskLong()
                @>
            )

            let db = Mock<IDatabase>.With(fun d ->
                <@
                    d.HashSetAsync(any(), any()) --> taskUnit()
                    d.StringIncrementAsync(any()) --> taskIncr()
                    d.ListLeftPushAsync(any(), any(), any(), any()) --> taskLPush()
                    d.CreateTransaction() --> trans
                    d.SetMembersAsync(any()) --> taskValues(1L)
                    d.HashGetAllAsync(any()) --> taskJobHash()
                @>
            )
            let sub = Mock<ISubscriber>.With(fun s ->
                <@
                    s.PublishAsync(any(), any(), any()) --> taskLong()
                @>
            )

            let queue = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))
            let! job = queue.add({ value = "test" });

            //When
            do! job.moveToFailed(exn "test") |> Async.Ignore
            let! jobs = queue.getFailed()

            //Then
            (jobs |> Seq.head).data.value |> should equal "test"
            (jobs |> Seq.head).stacktrace |> should equal (Some "errrrrrr")

            let value = toValueI64 1L
            let key = queue.toKey("failed")
            verify <@ trans.ListLeftPushAsync(any(), value) @> once
            verify <@ trans.PublishAsync(any(), any(), any()) @> once
            verify <@ trans.ListRemoveAsync(any(), any(), any(), any()) @> once
            verify <@ trans.SetAddAsync(any(), value, any()) @> once
            verify <@ db.SetMembersAsync(key) @> once
            verify <@ db.HashGetAllAsync(any()) @> once

        } |> Async.RunSynchronously

    type TestControlMessage =
        {
            times: int
            queueName: string
        }

    type IntegrationTests () =
        static let mp = ConnectionMultiplexer.Connect("127.0.0.1, allowAdmin=true")
        static do mp.GetServer(mp.GetEndPoints().[0]).FlushDatabase()
        let q = Queue<TestControlMessage>("test-control-messages", mp.GetDatabase, mp.GetSubscriber)
        let sendJobWithBull queue times = 
            async {
                do q.on.Completed.Add(fun j -> Debug.Print(sprintf "%i" j.job.jobId))
                return! q.add({ times = times; queueName = queue })
            }

        [<Fact>]
        let ``should call handler when a new job is added`` () =
            // Given
            let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
            let queue = Queue<Data>((Guid.NewGuid ()).ToString(), mp.GetDatabase, mp.GetSubscriber)
            let newJob = ref false
            do queue.``process`` (fun _ -> async { newJob := true })

            async {
                // When
                do! queue.add({value = "test"}) |> Async.Ignore |> Async.StartChild |> Async.Ignore
                do! queue.on.Completed |> Async.AwaitEvent |> Async.Ignore
                let! active = queue.getActive()
                let! completed = queue.getCompleted()

                // Then
                !newJob |> should be True
                (active |> Array.length) |> should equal 0
                (completed |> Array.length) |> should equal 1
            } |> Async.RunSynchronously

        [<Fact>]
        let ``should be able to return something from the handler`` () =
            // Given
            let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
            let queue = Queue<Data, double>((Guid.NewGuid ()).ToString(), mp.GetDatabase, mp.GetSubscriber)
            let newJob = ref false
            do queue.``process`` (fun _ -> async { 
                newJob := true 
                return 0.1
            })

            async {
                // When
                do! queue.add({value = "test"}) |> Async.Ignore |> Async.StartChild  |> Async.Ignore
                let! completedJob = queue.on.Completed |> Async.AwaitEvent
                let! active = queue.getActive()
                let! completed = queue.getCompleted()

                // Then
                !newJob |> should be True
                //completedJob.job.returnvalue.Value |> should equal 0.1
                (active |> Array.length) |> should equal 0
                (completed |> Array.length) |> should equal 1
            } |> Async.RunSynchronously

        [<Fact>]
        let ``should serialize json camelCase`` () =
            // Given
            let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
            let queue = Queue<OtherData>((Guid.NewGuid ()).ToString(), mp.GetDatabase, mp.GetSubscriber)
            let newJob = ref false
            do queue.``process`` (fun _ -> async { newJob := true })

            async {
                // When
                do! queue.add({Value = "test"}) |> Async.Ignore |> Async.StartChild |> Async.Ignore
                do! queue.on.Completed |> Async.AwaitEvent |> Async.Ignore
                let! active = queue.getActive()
                let! completed = queue.getCompleted()

                // Then
                !newJob |> should be True
                let key = queue.toKey("1")
                let hash = mp.GetDatabase().HashGetAll(key)
                hash.[0].Value |> string |> should equal "{\"value\":\"test\"}"
                (active |> Array.length) |> should equal 0
                (completed |> Array.length) |> should equal 1
            } |> Async.RunSynchronously


        [<Fact>]
        let ``should process stalled jobs when queue starts`` () =
            // Given
            let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
            let queue = Queue<Data>((Guid.NewGuid ()).ToString(), mp.GetDatabase, mp.GetSubscriber)

            async {
                // When
                do! queue.add({value = "test"}) |> Async.Ignore
                do! queue.moveJob(queue.toKey("wait"), queue.toKey("active")) |> Async.Ignore
                let stalledJob = ref false
                queue.``process``(fun _ -> async {
                    stalledJob := true
                })
                do! queue.on.Completed |> Async.AwaitEvent |> Async.Ignore

                // Then
                !stalledJob |> should be True
                let! active = queue.getActive()
                active.Length |> should equal 0
            } |> Async.RunSynchronously


        [<Fact>]
        let ``should report the correct length of the queue`` () =
            // Given
            let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
            let queue = Queue<Data>((Guid.NewGuid ()).ToString(), mp.GetDatabase, mp.GetSubscriber)

            async {
                // When
                do! queue.add({ value = "bert1" }) |> Async.Ignore
                do! queue.add({ value = "bert2" }) |> Async.Ignore
                do! queue.add({ value = "bert3" }) |> Async.Ignore
                do! queue.add({ value = "bert4" }) |> Async.Ignore
                do! queue.add({ value = "bert5" }) |> Async.Ignore
                do! queue.add({ value = "bert6" }) |> Async.Ignore
                do! queue.add({ value = "bert7" }) |> Async.Ignore

                // Then
                let! count = queue.count ()
                count |> should equal 7L
            } |> Async.RunSynchronously

        [<Fact>]
        let ``should be able to release the lock on a job`` () =
            // Given
            let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
            let queue = Queue<Data>((Guid.NewGuid ()).ToString(), mp.GetDatabase, mp.GetSubscriber)

            async {
                // When
                let! job = queue.add({ value = "bert1" })
                let token = Guid.NewGuid ()
                let! lockTaken = job.takeLock (token)
                let! lockReleased = job.releaseLock (token)

                // Then
                lockTaken |> should be True
                lockReleased |> should equal 1L
                mp.GetDatabase().StringLength (job.lockKey()) |> should equal 0L
            } |> Async.RunSynchronously

        [<Fact>]
        let ``should be able to empty the queue`` () =
            // Given
            let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
            let queue = Queue<Data>((Guid.NewGuid ()).ToString(), mp.GetDatabase, mp.GetSubscriber)

            async {
                // When
                do! queue.add({ value = "bert1" }) |> Async.Ignore
                do! queue.add({ value = "bert2" }) |> Async.Ignore
                do! queue.add({ value = "bert3" }) |> Async.Ignore
                do! queue.add({ value = "bert4" }) |> Async.Ignore
                do! queue.add({ value = "bert5" }) |> Async.Ignore
                do! queue.add({ value = "bert6" }) |> Async.Ignore
                do! queue.add({ value = "bert7" }) |> Async.Ignore

                // Then
                let! count = queue.count ()
                count |> should equal 7L
                do! queue.empty () |> Async.Ignore
                let! empty = queue.count ()
                empty |> should equal 0L
            } |> Async.RunSynchronously

        [<Fact>]
        let ``should be able to handle lifo `` () =
            // Given
            let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
            let queue = Queue<Data>((Guid.NewGuid ()).ToString(), mp.GetDatabase, mp.GetSubscriber)

            let lifo = [("lifo", "true")]

            async {
                // When
                do! queue.add({ value = "bert1" }, lifo) |> Async.Ignore
                do! queue.add({ value = "bert2" }, lifo) |> Async.Ignore
                do! queue.add({ value = "bert3" }, lifo) |> Async.Ignore
                do! queue.add({ value = "bert4" }, lifo) |> Async.Ignore
                do! queue.add({ value = "bert5" }, lifo) |> Async.Ignore
                do! queue.add({ value = "bert6" }, lifo) |> Async.Ignore
                do! queue.add({ value = "bert7" }, lifo) |> Async.Ignore

                // Then
                let! count = queue.count ()
                count |> should equal 7L
                let! waiting = queue.getWaiting()
                waiting.[0].jobId |> should equal 1L
                waiting.[6].jobId |> should equal 7L
            } |> Async.RunSynchronously

        [<Fact>]
        let ``should be able to fail processing a job`` () =
            // Given
            let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
            let queue = Queue<Data>((Guid.NewGuid ()).ToString(), mp.GetDatabase, mp.GetSubscriber, true)

            queue.``process``(fun j ->
                async {
                    if j.jobId % 2L = 0L then failwith "aaaargg it be even!"
                })

            use completed1Failed1 = semaphore 2
            
            do queue.on.Completed.Add(fun j -> 
                completed1Failed1.Post Release
                j.job.isCompleted |> Async.RunSynchronously |> should be True
            )

            do queue.on.Failed.Add(fun j -> 
                completed1Failed1.Post Release
                j.job.isFailed |> Async.RunSynchronously |> should be True
            )

            async {
                // When
                let! job1 = queue.add({ value = "bert1" })
                let! job2 = queue.add({ value = "bert2" })
                
                do! completed1Failed1.PostAndAsyncReply(fun t -> Wait t)

                // Then
                let jobs = [job1; job2]
                let job11 = jobs |> Seq.find(fun j -> j.jobId = 1L)
                let job22 = jobs |> Seq.find(fun j -> j.jobId = 2L)

                let! j1c = job11.isCompleted 
                j1c |> should be True
                let! j2c = job22.isCompleted
                j2c |> should be False
                let! j2f = job22.isFailed
                j2f |> should be True
            } |> Async.RunSynchronously

        [<Fact>]
        let ``should be able to remove a job`` () =
            async {
                // Given
                let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
                let queuename = (Guid.NewGuid ()).ToString()
                let queue = Queue<Data>(queuename, mp.GetDatabase, mp.GetSubscriber)
                let! job1 = queue.add({ value = "test"})
                let! job2 = queue.add({ value = "test"})
                let! job3 = queue.add({ value = "test"})
                let! job4 = queue.add({ value = "test"})
                let! job5 = queue.add({ value = "test"})

                // When
                do! job1.remove()
                mp.GetDatabase().ListRightPopLeftPush(queue.toKey("wait"), queue.toKey("active")) |> ignore
                do! job2.moveToCompleted() |> Async.Ignore
                do! job2.remove()
                mp.GetDatabase().ListRightPopLeftPush(queue.toKey("wait"), queue.toKey("active")) |> ignore
                do! job3.moveToFailed(exn "errrrr") |> Async.Ignore
                do! job3.remove()
                do! queue.moveJob(queue.toKey("wait"), queue.toKey("active")) |> Async.Ignore
                do! job4.remove()
                do! queue.moveJob(queue.toKey("wait"), queue.toKey("paused")) |> Async.Ignore
                do! job5.remove()

                // Then
                let! length = queue.count ()
                length |> should equal 0L
                mp.GetDatabase().ListLength(queue.toKey("wait")) |> should equal 0L
                mp.GetDatabase().ListLength(queue.toKey("paused")) |> should equal 0L
                mp.GetDatabase().ListLength(queue.toKey("active")) |> should equal 0L
                mp.GetDatabase().SetLength(queue.toKey("completed")) |> should equal 0L
                mp.GetDatabase().SetLength(queue.toKey("failed")) |> should equal 0L
            } |> Async.RunSynchronously

        [<Fact>]
        let ``should be able to send a job from bull to oxen`` () =
            async {
                // Given
                let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
                let queuename = (Guid.NewGuid ()).ToString()
                let queue = new Queue<Data>(queuename, mp.GetDatabase, mp.GetSubscriber)
                use jobscompleted = semaphore 100 

                queue.``process`` (fun j ->
                    async {
                        Debug.Print (j.jobId.ToString ())
                        Debug.Print "huuu"
                    })
                
                queue.on.Completed.Add (fun _ -> jobscompleted.Post Release)

                // When
                do! sendJobWithBull queuename 100 |> Async.Ignore
                do! jobscompleted.PostAndAsyncReply(fun t -> Wait t)

                //Then
                let! length = queue.count ()
                length |> should equal 0L
                let! completed = queue.getCompleted ()
                completed.Length |> should equal 100
            } |> Async.RunSynchronously

        [<Fact>]
        let ``should be able to send a job from bull to oxen with two listening queue's`` () =
            async {
                // Given
                let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
                let mp2 = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
                let queuename = (Guid.NewGuid ()).ToString()
                let queue = Queue<Data>(queuename, mp.GetDatabase, mp.GetSubscriber)
                use jobscompleted = semaphore 100 

                queue.``process`` (fun j ->
                    async {
                        Debug.Print (j.jobId.ToString ())
                        Debug.Print "huuu"
                    })

                queue.on.Completed.Add(fun _ -> jobscompleted.Post Release)

                let queue2 = Queue<Data>(queuename, mp2.GetDatabase, mp2.GetSubscriber)
                queue2.``process`` (fun j ->
                    async {
                        Debug.Print (j.jobId.ToString ())
                        Debug.Print "huuu2"
                    })

                queue2.on.Completed.Add(fun _ -> jobscompleted.Post Release)

                // When
                do! sendJobWithBull queuename 100 |> Async.Ignore
                do! jobscompleted.PostAndAsyncReply(fun t -> Wait t)

                //Then
                let! length = queue.count ()
                length |> should equal 0L
                let! completed = queue.getCompleted ()
                completed.Length |> should equal 100

            } |> Async.RunSynchronously

        [<Fact>]
        let ``should be able to retry a job`` () =
            async {
                let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
                let queue = Queue<Data>((Guid.NewGuid ()).ToString(), mp)
                let called = ref 0
                queue.on.Failed.Add (fun j ->
                    j.job.retry() |> Async.RunSynchronously |> ignore
                )
                queue.``process`` (fun j ->
                    async {
                        Debug.Print ("processing job " + j.jobId.ToString ())
                        called := !called + 1
                        if !called % 2 = 1 then
                            failwith "Noooo!..... it be uneven!"
                    })

                let! job = queue.add({value = "test"});
                do! queue.on.Completed |> Async.AwaitEvent |> Async.Ignore 
                let! completed = job.isCompleted
                completed |> should be True
                !called |> should equal 2
            }|> Async.RunSynchronously

        [<Fact>]
        let ``should process a delayed job only after delayed time`` () =
            async {
                let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
                let queue = Queue<Data>((Guid.NewGuid ()).ToString(), mp)
                let called = ref false
                let delay = 1000.
                queue.``process`` (fun j -> async {
                    let curDate = DateTime.Now
                    j.timestamp.AddMilliseconds(delay) |> should lessThanOrEqualTo curDate
                    called := true
                })
                do! queue.add({ value = "test"}, [("delay", delay |> string)]) |> Async.Ignore
                do! queue.on.Completed |> Async.AwaitEvent |> Async.Ignore
                let! delayed = queue.getDelayed ()
                delayed.Length |> should equal 0
                let! completed = queue.getCompleted ()
                completed.Length |> should equal 1
                !called |> should be True
            } |> Async.RunSynchronously

        [<Fact>]
        let ``should process delayed jobs in correct order`` () =
            async {
                let order = ref 0
                let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
                let queue = Queue<Order>((Guid.NewGuid ()).ToString(), mp.GetDatabase, mp.GetSubscriber, true)
                queue.``process`` (fun j -> async {
                    !order |> should lessThan j.data.order
                    Debug.Print ("Order: " + j.data.order.ToString())
                    order := j.data.order
                    ()
                })

                do queue.add({order = 1}, [("delay", "100")]) |> ignore
                do queue.add({order = 6}, [("delay", "1100")]) |> ignore
                do queue.add({order = 10}, [("delay", "1900")]) |> ignore
                do queue.add({order = 2}, [("delay", "300")]) |> ignore
                do queue.add({order = 9}, [("delay", "1700")]) |> ignore
                do queue.add({order = 5}, [("delay", "900")]) |> ignore
                do queue.add({order = 3}, [("delay", "500")]) |> ignore
                do queue.add({order = 7}, [("delay", "1300")]) |> ignore
                do queue.add({order = 4}, [("delay", "700")]) |> ignore
                do queue.add({order = 8}, [("delay", "1500")]) |> ignore
                do! queue.on.Empty |> Async.AwaitEvent |> Async.Ignore
            } |> Async.RunSynchronously

        [<Fact>]
        let ``should be able to pause and resume the queue`` () =
            // Given
            let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
            let queue = Queue<Data>("stuff", mp.GetDatabase, mp.GetSubscriber)

            let pauseHappend = ref false
            let resumeHappend = ref false

            queue.on.Resumed.Add(fun _ -> resumeHappend := true)
            queue.on.Paused.Add(fun _ -> pauseHappend := true)

            // When
            queue.pause () |> Async.RunSynchronously
            queue.resume () |> Async.RunSynchronously

            Async.Sleep 200 |> Async.RunSynchronously

            // Then
            !resumeHappend |> should be True
            !pauseHappend |> should be True

        [<Fact>]
        let ``should be able to wait for a specific event to happen`` () =
            async {
                // Given
                let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
                let queue = Queue<Data>(string (Guid.NewGuid ()), mp.GetDatabase, mp.GetSubscriber)

                queue.``process``(fun j -> async {()})

                // When
                let awaiter = queue.jobAwaiter Completed (fun j -> j.jobId = 4L) 10000
                do! queue.add({ value = "1" }) |> Async.Ignore
                do! queue.add({ value = "2" }) |> Async.Ignore
                do! queue.add({ value = "3" }) |> Async.Ignore
                do! queue.add({ value = "4" }) |> Async.Ignore

                // Then
                let! awaitedJob = awaiter
                awaitedJob.data.value |> should equal "4"
                awaitedJob.jobId |> should equal 4L
            } |> Async.RunSynchronously

        [<Fact>]
        let ``should be to start waiting before the predicate is available`` () =
            async {
                // Given
                let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
                let queue = Queue<Data>(string (Guid.NewGuid ()), mp.GetDatabase, mp.GetSubscriber)

                queue.``process``(fun j -> async {()})
                let awaiter = queue.jobAwaiter Completed

                // When
                let! job = queue.add({ value = "1" })
                let awaiter = awaiter (fun j -> j.jobId = job.jobId) 10000
                
                // Then
                let! awaitedJob = awaiter
                awaitedJob.data.value |> should equal "1"
                awaitedJob.jobId |> should equal 1L
            } |> Async.RunSynchronously
            
        [<Fact>]
        let ``it should be possible to ensure delivery of a job to more than one listener`` () = ()

        [<Fact>]
        let ``it should be possible to ensure delivery of a job to more than one group of listeners`` () = ()

        [<Fact>]
        let ``it should ensure that in a group of listeners only one listener processes the job`` () = ()

        [<Fact>]
        let ``a listener should be able to register itself in a group`` () = ()

        [<Fact>]
        let ``a group should be created if a listener registers itself for a topic with an group identifier that does not exist`` () = ()

        [<Fact>]
        let ``a group should be removed if there are no more listeners registered`` () = ()

        [<Fact>]
        let ``a listener should be added to a group if it registers with a group identifier that already exists`` () = ()

        [<Fact>]
        let ``the api stays the same except for the extra topic id`` () = ()

        // could call listeners subscribers, a group of listeners a subscription and a group of subscriptions about the same thing a topic
        // how does this translate to jobs?
        // a job is put on a queue for a worker, there can be a pool of workers listening for jobs,
        // bull:topic:queue:id or bull:queue:id
