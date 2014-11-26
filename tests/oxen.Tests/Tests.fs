module oxen.Tests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks

open Foq
open Xunit
open FsUnit.Xunit

open oxen
open StackExchange.Redis

type Data = {
    value: string
}

let taskUnit () = Task.Factory.StartNew(fun () -> ())
let taskIncr () = Task.Factory.StartNew(fun () -> 1L)
let taskLPush () = Task.Factory.StartNew(fun () -> 1L)
let taskLong () = Task.Factory.StartNew(fun () -> 1L)
let taskTrue () = Task.Factory.StartNew(fun () -> true)
let taskFalse () = Task.Factory.StartNew(fun () -> false)
let taskRedisResult () = Task.Factory.StartNew(fun () -> Mock<RedisResult>().Create());
let taskJobHash () = Task.Factory.StartNew(fun () -> 
    [|
        HashEntry(toValueStr "data", toValueStr "{ \"value\": \"test\" }")
        HashEntry(toValueStr "opts", toValueStr "")
        HashEntry(toValueStr "progress", toValueI32 1)
    |])

let taskValues (value:int64) = Task.Factory.StartNew(fun () -> [| RedisValue.op_Implicit(value) |])
let taskEmptyValues () = Task.Factory.StartNew(fun () -> [| |])
let taskEmptyValue () = Task.Factory.StartNew(fun () -> new RedisValue())

type JobFixture () = 
    [<Fact>]
    let ``should create a new job from given json data`` () =
        // Given
        let db = Mock<IDatabase>().Create();
        let sub = Mock<ISubscriber>().Create();

        let q = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))

        // When
        let job = Job.fromData(q, 1L, "{ \"value\": \"test\" }", "", 1)

        // Then
        job.data.value |> should equal "test"
        job.jobId |> should equal 1L
        job._progress |> should equal 1

    [<Fact>]
    let ``should get a job from the cache and make it into a real one`` () =
        // Given
        let db = Mock<IDatabase>.With(fun d ->
            <@
                d.HashGetAllAsync (any(), any()) --> taskJobHash()
            @>
        )
        let sub = Mock<ISubscriber>().Create();

        let q = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))
        
        // When 
        let job = Job.fromId(q, 1L) |> Async.RunSynchronously
        
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
        let sub = Mock<ISubscriber>().Create();

        let q = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))
        let job = {
            jobId = 1L
            queue = q
            data = { value = "string" }
            opts = None
            _progress = 0
        }
        
        // When
        let taken = job.takeLock (Guid.NewGuid ()) |> Async.RunSynchronously

        // Then
        taken |> should be True
        verify <@ db.StringSetAsync (any(), any(), any(), any()) @> once

    
    [<Fact>]
    let ``should be able to renew a lock on a job`` () =
        // Given
        let db = Mock<IDatabase>.With(fun d ->
            <@
                d.StringSetAsync (any(), any(), any(), any()) --> taskTrue()
            @>
        )
        let sub = Mock<ISubscriber>().Create();

        let q = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))
        let job = {
            jobId = 1L
            queue = q
            data = { value = "string" }
            opts = None
            _progress = 0
        }
        
        // When
        let taken = job.takeLock (Guid.NewGuid (), true) |> Async.RunSynchronously

        // Then
        taken |> should be True
        verify <@ db.StringSetAsync (any(), any(), any(), When.NotExists) @> once

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
            let sub = Mock<ISubscriber>().Create();

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
        let sub = Mock<ISubscriber>().Create()
        let eventFired = ref false
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
        let sub = Mock<ISubscriber>().Create();

        let queue = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))
        
        // When
        let result = queue.toKey("stuff")

        // Then
        result.ToString() |> should equal "bull:stuff:stuff"

    [<Fact>]
    let ``report progress and listen to event on queue`` () =
        async {
            let trans = Mock<ITransaction>.With(fun t ->
                <@
                    t.ListLeftPushAsync(any(), any(), any(), any()) --> taskLPush()
                    t.PublishAsync (any(), any(), any()) --> taskLong ()
                    t.ExecuteAsync () --> taskTrue()
                @>
            )
            // Given 
            let db = Mock<IDatabase>.With(fun d -> 
                <@ 
                    d.HashSetAsync(any(), any()) --> taskUnit()
                    d.StringIncrementAsync(any()) --> taskIncr()
                    d.CreateTransaction(any()) --> trans
                @>
            )
            
            let sub = Mock<ISubscriber>().Create()

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
            let sub = Mock<ISubscriber>().Create()

            let queue = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))
            let! job = queue.add({ value = "test" });

            //When
            do! job.moveToFailed() |> Async.Ignore
            let! jobs = queue.getFailed() 

            //Then
            (jobs |> Seq.head).data.value |> should equal "test"

            let value = toValueI64 1L
            let key = queue.toKey("failed")
            verify <@ trans.ListLeftPushAsync(any(), value) @> once
            verify <@ trans.PublishAsync(any(), any(), any()) @> once
            verify <@ trans.ListRemoveAsync(any(), any(), any(), any()) @> once
            verify <@ trans.SetAddAsync(any(), value, any()) @> once
            verify <@ db.SetMembersAsync(key) @> once
            verify <@ db.HashGetAllAsync(any()) @> once
             
        } |> Async.RunSynchronously

    [<Fact>]
    let ``should be able to pause and resume the queue`` () =
        // Given
        let db = Mock<IDatabase>.With(fun d ->
            <@
                d.ListRangeAsync (any(),any(),any()) --> taskEmptyValues ()
                d.SetContainsAsync (any(), any()) --> taskTrue () 
                d.ListRightPopLeftPushAsync(any(), any(), any()) --> taskEmptyValue()
            @>
        )
        let sub = Mock<ISubscriber>().Create()
        let queue = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))

        let pauseHappend = ref false
        let resumeHappend = ref false

        queue.on.Resumed.Add(fun q -> resumeHappend := true)
        queue.on.Paused.Add(fun q -> pauseHappend := true)
            
        let paused = queue.pause () |> Async.RunSynchronously
        queue.resume (fun _ -> async {()}) |> Async.RunSynchronously 

        !resumeHappend |> should be True
        !pauseHappend |> should be True
        
        paused |> should be True

    type TestControlMessage = 
        {
            times: int
            queueName: string    
        }

    type IntegrationTests () = 
        let logger = LogManager.getNamedLogger "IntegrationTests"
        let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
        let q = Queue<TestControlMessage>("test-control-messages", mp.GetDatabase, mp.GetSubscriber)
        let sendJobWithBull queue times = 
            q.add({ times = times; queueName = queue }) |> Async.RunSynchronously
            

        let waitForQueueToFinish (queue:Queue<_>) = 
            let rec wait () = 
                async {
                    //do! Async. 100
                    let! waiting = queue.getWaiting()
                    let! active = queue.getActive()
                    match waiting.Length + active.Length with
                    | x when x > 0 -> return! wait ()
                    | _ -> ()
                }
            wait ()

        let waitForJobsToArrive (queue:Queue<_>) = 
            let rec wait () =
                async {
                    ///do! Async.Sleep 100
                    let! count = queue.count()
                    match count with
                    | x when x = 0L -> return! wait ()
                    | _ -> ()
                }
            wait ()

        [<Fact>]
        let ``should call handler when a new job is added`` () = 
            // Given
            let mp = ConnectionMultiplexer.Connect("localhost, allowAdmin=true, resolveDns=true")
            let queue = Queue<Data>((Guid.NewGuid ()).ToString(), mp.GetDatabase, mp.GetSubscriber)
            let newJob = ref false
            do queue.``process`` (fun j -> async { newJob := true })
            
            async {
                // When
                do queue.add({value = "test"}) |> Async.Ignore |> Async.Start
                do! queue.on.Completed |> Async.AwaitEvent |> Async.Ignore
                let! active = queue.getActive()
                let! completed = queue.getCompleted()

                // Then
                !newJob |> should be True
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
                let! job = queue.add({value = "test"});
                do! queue.moveJob(queue.toKey("wait"), queue.toKey("active")) |> Async.Ignore
                let stalledJob = ref false
                queue.``process``(fun j -> async {
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

            let lifo = [("lifo", "true")] |> Map.ofList

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
            let queue = Queue<Data>((Guid.NewGuid ()).ToString(), mp.GetDatabase, mp.GetSubscriber)

            queue.``process``(fun j -> 
                async {
                    if j.jobId % 2L = 0L then failwith "aaaargg it be even!"
                })

            async {
                // When
                let! job1 = queue.add({ value = "bert1" }) 
                let! job2 = queue.add({ value = "bert2" }) 

                do! waitForQueueToFinish queue

                // Then
                let! j1c = job1.isCompleted 
                j1c |> should be True
                let! j2c = job2.isCompleted 
                j2c |> should be False
                let! j2f = job2.isFailed 
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
                do! job3.moveToFailed() |> Async.Ignore
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
                queue.``process`` (fun j -> 
                    async {
                        Debug.Print (j.jobId.ToString ())
                        Debug.Print "huuu"
                    })
                
                // When
                sendJobWithBull queuename 100 |> ignore
                do! waitForJobsToArrive queue
                do! waitForQueueToFinish queue

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
                let queuename = (Guid.NewGuid ()).ToString()
                let queue = Queue<Data>(queuename, mp.GetDatabase, mp.GetSubscriber)
                queue.``process`` (fun j -> 
                    async {
                        Debug.Print (j.jobId.ToString ())
                        Debug.Print "huuu"
                    })
                
                let queue2 = Queue<Data>(queuename, mp.GetDatabase, mp.GetSubscriber)
                queue2.``process`` (fun j -> 
                    async {
                        Debug.Print (j.jobId.ToString ())
                        Debug.Print "huuu2"
                    })

                // When
                sendJobWithBull queuename 100 |> ignore
                do! waitForJobsToArrive queue
                do! waitForQueueToFinish queue

                //Then
                let! length = queue.count ()
                length |> should equal 0L
                let! completed = queue.getCompleted ()
                completed.Length |> should equal 100

            } |> Async.RunSynchronously