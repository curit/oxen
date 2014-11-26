﻿namespace oxen

open System
open System.IO
open System.Collections.Generic
open System.Threading.Tasks
open System.Threading

open StackExchange.Redis

open Newtonsoft.Json


[<AutoOpen>]
module OxenConvenience = 
    let toValueStr (x:string) = RedisValue.op_Implicit(x:string)
    let toValueI64 (x:int64) = RedisValue.op_Implicit(x:int64)
    let toValueI32 (x:int32) = RedisValue.op_Implicit(x:int32)
    let fromValueStr (x:RedisValue):string = string(x)
    let fromValueI64 (x:RedisValue):int64 = int64(x)
    let fromValueI32 (x:RedisValue):int = int(x)
    let valueToKeyLong (x:RedisValue):RedisKey = RedisKey.op_Implicit(int64(x).ToString ())

    /// <summary>
    /// Option coalesing operator 
    /// <c>
    /// None |? 4 -> 4
    /// Some 4 |? 3 -> 4
    /// </c>
    /// </summary>
    let (|?) (x: 'a option) (y: 'a) =  match x with | None -> y | Some z -> z

    let LOCK_RENEW_TIME = 5000.0

module Async =
    let inline awaitPlainTask (task: Task) = 
        // rethrow exception from preceding task if it fauled
        let continuation (t : Task) : unit =
            match t.IsFaulted with
            | true -> raise t.Exception
            | arg -> ()
        task.ContinueWith continuation |> Async.AwaitTask
 
    let inline startAsPlainTask (work : Async<unit>) = 
        Task.Factory.StartNew(fun () -> work |> Async.RunSynchronously)

type EventType = 
    | Completed
    | Progress
    | Failed
    | Paused
    | Resumed
    | NewJob

type Job<'a> = 
    {
        queue: Queue<'a>
        data: 'a
        jobId: int64
        opts: Map<string,string> option
        _progress: int
    }
    static member private logger = LogManager.getLogger()
    member private this._logger = Job<'a>.logger
    member this.lockKey () = this.queue.toKey((this.jobId.ToString ()) + ":lock")
    member this.toData () =
        this._logger.Info "creating redis hash for job %i" this.jobId  
        let jsData = JsonConvert.SerializeObject (this.data)
        let jsOpts = JsonConvert.SerializeObject (this.opts |? Map.empty)
        [|
            HashEntry(toValueStr "data", toValueStr jsData)
            HashEntry(toValueStr "opts", toValueStr jsOpts)
            HashEntry(toValueStr "progress", toValueI32 this._progress)
        |]
    member this.progress progress = 
        async {
            this._logger.Info "reporing progress %i for job %i" progress this.jobId
            let client:IDatabase = this.queue.client()
            do! client.HashSetAsync (
                    this.queue.toKey(this.jobId.ToString ()), 
                    [| HashEntry (toValueStr "progress", toValueI32 progress) |]
                ) |> Async.awaitPlainTask
            do! this.queue.emitJobEvent(EventType.Progress, this, progress)
        }
    member this.remove () = 
        async { 
            this._logger.Info "removeing job %i" this.jobId
            let script = 
                "if (redis.call(\"SISMEMBER\", KEYS[4], ARGV[1]) == 0) and (redis.call(\"SISMEMBER\", KEYS[5], ARGV[1]) == 0) then\n\
                  redis.call(\"LREM\", KEYS[1], 0, ARGV[1])\n\
                  redis.call(\"LREM\", KEYS[2], 0, ARGV[1])\n\
                  redis.call(\"LREM\", KEYS[3], 0, ARGV[1])\n\
                end\n\
                redis.call(\"SREM\", KEYS[4], ARGV[1])\n\
                redis.call(\"SREM\", KEYS[5], ARGV[1])\n\
                redis.call(\"DEL\", KEYS[6])\n"

            let keys = 
                [| "active"; "wait"; "paused"; "completed"; "failed"; this.jobId.ToString() |]  
                |> Array.map this.queue.toKey

            let client = this.queue.client ()
            return! client.ScriptEvaluateAsync (script, keys, [|this.jobId |> toValueI64|]) |> Async.AwaitTask |> Async.Ignore
        }
    member this.takeLock (token, ?renew) = 
        async {
            this._logger.Info "taking lock with token %A for job %i renewed %b" token this.jobId (renew |? false)
            let nx = match renew with 
                     | Some x -> When.NotExists
                     | None -> When.Always     
            let value = (token.ToString ()) |> toValueStr
            let client:IDatabase = this.queue.client()
            return! 
                client.StringSetAsync (
                    this.lockKey (), 
                    value, 
                    Nullable (TimeSpan.FromMilliseconds (LOCK_RENEW_TIME)), 
                    nx 
                ) |> Async.AwaitTask
        }
    member this.renewLock token = this.takeLock (token, true)
    member this.releaseLock (token) = 
        async {
            this._logger.Info "releasing lock with token %A for job %i" token this.jobId
            let script = 
                "if redis.call(\"get\", KEYS[1]) == ARGV[1] \n\
                then \n\
                return redis.call(\"del\", KEYS[1]) \n\
                else \n\
                return 0 \n\
                end \n"

            let! result = 
                this.queue.client().ScriptEvaluateAsync (
                    script, 
                    [|this.lockKey()|], 
                    [|(token.ToString ()) |> toValueStr|]
                 ) |> Async.AwaitTask
            return int64(result)
        }
    member private this.moveToSet set =
        async {
            let queue = this.queue
            let activeList = queue.toKey("active")
            let dest = queue.toKey(set)
            let client:IDatabase = queue.client()
            let multi = client.CreateTransaction()
            do multi.ListRemoveAsync (activeList, toValueI64 this.jobId) |> ignore
            do multi.SetAddAsync (dest, toValueI64 this.jobId) |> ignore
            return! multi.ExecuteAsync() |> Async.AwaitTask                        
        }
    member private this.isDone list =
        async {
            this._logger.Info "checking if job %i is done (%s)" this.jobId list
            let client:IDatabase = this.queue.client()
            return! client.SetContainsAsync(this.queue.toKey(list), toValueI64 this.jobId) |> Async.AwaitTask
        }
    member this.moveToCompleted () = this.moveToSet("completed")
    member this.moveToFailed () = this.moveToSet("failed")
    member this.isCompleted = this.isDone("completed");
    member this.isFailed = this.isDone("failed");
    
    static member create (queue, jobId, data:'a, opts) = 
        async { 
            Job<_>.logger.Info "creating job for queue %s with id %i and data %A and options %A" (queue:Queue<'a>).name jobId data opts
            let job = { queue = queue; data = data; jobId = jobId; opts = opts; _progress = 0 }
            let client:IDatabase = queue.client()
            do! client.HashSetAsync (queue.toKey (jobId.ToString ()), job.toData ()) |> Async.awaitPlainTask
            return job 
        }
    static member fromId<'a> (queue: Queue<'a>, jobId:int64) = 
        async {
            let client = queue.client
            let! job = client().HashGetAllAsync (queue.toKey(jobId.ToString())) |> Async.AwaitTask
            //staan hash values altijd op dezelfde volgorde
            return Job.fromData (
                queue, 
                jobId,
                job.[0].Value |> fromValueStr, 
                job.[1].Value |> fromValueStr, 
                job.[2].Value |> fromValueI32) 
        }
    static member fromData (queue:Queue<'a>, jobId: Int64, data: string, opts: string, progress: int) =
        let sData = JsonConvert.DeserializeObject<'a>(data)
        let sOpts = 
            match JsonConvert.DeserializeObject<Dictionary<string, string>>(opts) with 
            | null -> None
            | _ as x -> x |> Seq.map (fun kv -> (kv.Key, kv.Value)) |> Map.ofSeq |> Some
        { queue = queue; data = sData; jobId = jobId; opts = sOpts; _progress = progress }

and OxenJobEvent<'a> =
    {
        job: Job<'a>
        data: obj option
        progress: int option
        err: exn option
    }

and OxenQueueEvent<'a> =
    {
        queue: Queue<'a>
    }

and OxenNewJobEvent = 
    {
        jobId: int64
    }

and Events<'a> = {
    Completed: IEvent<OxenJobEvent<'a>>
    Progress: IEvent<OxenJobEvent<'a>>
    Failed: IEvent<OxenJobEvent<'a>>
    Paused: IEvent<OxenQueueEvent<'a>>
    Resumed: IEvent<OxenQueueEvent<'a>>
}

and LockRenewer<'a> (job:Job<'a>, token:Guid) =
    let cts = new CancellationTokenSource ()
    static let logger = LogManager.getLogger()

    let rec lockRenewer (job:Job<'a>) = 
        async {
            do! job.renewLock token |> Async.Ignore
            do! Async.Sleep (int(LOCK_RENEW_TIME / 2.0)) 
            return! lockRenewer job
        }

    do logger.Info "starting lock renewer for job %i with token %A" job.jobId token
    do Async.Start (lockRenewer job, cts.Token)

    interface IDisposable with
        member x.Dispose() =
            x.Dispose(true)
            GC.SuppressFinalize(x);

    member x.Dispose(disposing) = 
        logger.Debug "disposing lock renewer for job %i and token %A" job.jobId token
        if (disposing) then
            cts.Cancel ()

    override x.Finalize () =
        x.Dispose(false)
            
/// <summary>
/// The queue
/// </summary>
/// <param name="name">Name of the queue</param>
/// <param name="dbFactory">a function returning a new instance of IDatabase</param>
/// <param name="subscriberFactory">a function returning a new instance of ISubscriber</param>
/// <param name="forceSequentialProcessing">a boolean specifying whether or not this queue will handle jobs sequentially, not in parallel</param>
and Queue<'a> (name, dbFactory:(unit -> IDatabase), subscriberFactory:(unit -> ISubscriber), ?forceSequentialProcessing:bool) as this =
    static let logger = LogManager.getLogger()

    let mutable paused = false
    let mutable processing = false
    
    let channel = RedisChannel("bull:" + name + ":jobs", RedisChannel.PatternMode.Auto)

    let sub = subscriberFactory ()
    do sub.Subscribe(
                channel,
                (fun c v -> 
                    async {
                        let jobId = fromValueI64(v)
                        return! this.emitNewJobEvent(jobId)
                    } |> Async.RunSynchronously))
    
    do logger.Info "subscribed to %A" channel


    let token = Guid.NewGuid ()

    let completedEvent = new Event<OxenJobEvent<'a>> ()
    let progressEvent = new Event<OxenJobEvent<'a>> ()
    let failedEvent = new Event<OxenJobEvent<'a>> ()
    let pausedEvent = new Event<OxenQueueEvent<'a>> ()
    let resumedEvent = new Event<OxenQueueEvent<'a>> ()
    let newJobEvent = new Event<OxenNewJobEvent> ()
    let onNewJob = newJobEvent.Publish

    let processJob handler job = 
        async { 
            if paused then ()
            processing <- true
            use lr = new LockRenewer<'a>(job, token)
            try
                logger.Info "running handler on job %i queue %s" job.jobId name
                let! data = handler job 
                do! job.moveToCompleted () |> Async.Ignore
                do! this.emitJobEvent(Completed, job, data = data)
            with 
                | _ as e -> 
                    logger.Error "handler failed for job %i with exn %A for queue %s" job.jobId e name
                    do! job.moveToFailed () |> Async.Ignore
                    do! job.releaseLock token |> Async.Ignore
                    do! this.emitJobEvent(Failed, job, exn = e) 
        }

    let rec getNextJob () =
        async {
            let! (gotIt:RedisValue) = this.moveJob(this.toKey("wait"), this.toKey("active")) 
            match gotIt with 
            | g when g.HasValue -> return! this.getJob (fromValueI64 g)
            | _ -> 
                do! onNewJob |> Async.AwaitEvent |> Async.Ignore
                return! getNextJob ()
        }

    let rec processJobs handler = 
        async {
            let! job = getNextJob ()
            match forceSequentialProcessing |? false with
            | true -> do! processJob handler job
            | false -> do processJob handler job |> Async.Start
            
            if not(paused) then 
                return! processJobs handler
        }

    let processStalledJob handler (job:Job<'a>) = 
        async { 
            logger.Info "processing stalled job %i for queue %s" job.jobId name
            let! lock = job.takeLock token
            match lock with
            | true -> 
                let key = this.toKey("completed");
                let! contains = 
                    this.client().SetContainsAsync (key, job.jobId |> toValueI64) 
                    |> Async.AwaitTask
                
                if not(contains) then 
                    return! processJob handler job
                    
            | false -> ()
        }

    let processStalledJobs handler = 
        async {
            logger.Debug "processing stalled jobs for queue: %s" name
            let! range = 
                this.client().ListRangeAsync (this.toKey ("active"), 0L, -1L)
                |> Async.AwaitTask
            let! jobs = 
                range 
                |> Seq.map fromValueI64
                |> Seq.map (fun x -> Job.fromId (this, x))
                |> Async.Parallel

            let stalledJobsHandler = processStalledJob handler

            return! jobs |> Seq.map stalledJobsHandler |> Async.Parallel |> Async.Ignore
        } 

    member x.name = name
    member x.``process`` (handler:Job<'a> -> Async<unit>) = 
        logger.Info "start processing queue %s" name
        this.run handler |> Async.Start
    
    member x.run handler = 
        async {
            do! Async.SwitchToNewThread ()
            do! [| (processStalledJobs handler); (processJobs handler) |] |> Async.Parallel |> Async.Ignore
        }
    
    member x.add (data, ?opts:Map<string,string>) = 
        async {
            let! jobId = this.client().StringIncrementAsync (this.toKey "id") |> Async.AwaitTask
            logger.Info "adding job %i to the queue %s" jobId name
            let! job = Job<'a>.create (this, jobId, data, opts) 
            let key = this.toKey "wait"
            let multi = this.client().CreateTransaction();

            let res = 
                match opts with
                | None -> multi.ListLeftPushAsync (key, toValueI64 jobId) 
                | Some opts -> 
                    if opts.ContainsKey "lifo" && bool.Parse (opts.Item "lifo") 
                    then multi.ListRightPushAsync (key, toValueI64 jobId) 
                    else multi.ListLeftPushAsync (key, toValueI64 jobId) 
           
            let result =  multi.PublishAsync (channel, toValueI64 jobId)
           
            do! multi.ExecuteAsync() |> Async.AwaitTask |> Async.Ignore
                       
            if result.Result < 1L then failwith "must have atleast one subscriber, me"

            return job
        }

    member x.pause () = 
        async {
            logger.Info "pausing queue %s" name
            if paused then 
                return paused
            else 
                if processing then 
                    do! this.on.Completed |> Async.AwaitEvent |> Async.Ignore
                
                paused <- true
                do! this.emitQueueEvent Paused 
                return paused
        }
        
    member x.resume handler = 
        async { 
            logger.Info "resuming queue %s" name
            if paused then
                paused <- false
                do! this.emitQueueEvent Resumed
                this.run handler |> Async.Start
            else
                failwith "Cannot resume running queue"
        }

    member x.count () = 
        async { 
            logger.Info "getting queue length for queue %s" name
            let multi = (this.client ()).CreateTransaction()
            let waitLength = multi.ListLengthAsync (this.toKey("wait"))
            let pausedLength = multi.ListLengthAsync (this.toKey("paused"))
            do! multi.ExecuteAsync () |> Async.AwaitTask |> Async.Ignore
            return [| waitLength.Result; pausedLength.Result; |] |> Seq.max
        }
    
    member x.empty () = 
        async { 
            logger.Info "emptying queue %s" name
            let multi = (this.client ()).CreateTransaction()
            let waiting = multi.ListRangeAsync (this.toKey("wait"), 0L, -1L) 
            let paused = multi.ListRangeAsync (this.toKey("paused"), 0L, -1L) 
            do multi.KeyDeleteAsync(this.toKey("wait")) |> ignore
            do multi.KeyDeleteAsync(this.toKey("paused")) |> ignore
            do multi.ExecuteAsync() |> ignore
            
            let jobKeys = Array.concat [|waiting.Result; paused.Result|]
            let multi2 = (this.client ()).CreateTransaction()
            jobKeys |> Seq.iter (fun k -> multi2.KeyDeleteAsync(valueToKeyLong k) |> ignore)
            return! multi2.ExecuteAsync () |> Async.AwaitTask
        }

    member x.moveJob (src, dest) = 
        this.client().ListRightPopLeftPushAsync(src, dest) |> Async.AwaitTask
    member x.getJob id = Job<'a>.fromId (this, id)
    member x.getJobs (queueType, ?isList, ?start, ?stop) =
        async {
            logger.Info "getting %s jobs for queue %s" queueType name
            let key = this.toKey(queueType)
            let! jobsIds = 
                match isList |? false with 
                | true -> this.client().ListRangeAsync(key, (start |? 0L), (stop |? -1L)) |> Async.AwaitTask
                | false -> this.client().SetMembersAsync(key) |> Async.AwaitTask

            return!
                jobsIds
                |> Seq.map fromValueI64
                |> Seq.map this.getJob
                |> Async.Parallel
        }
    member x.getFailed () = this.getJobs "failed"
    member x.getCompleted () = this.getJobs "completed"
    member x.getWaiting () = this.getJobs ("wait", true)
    member x.getActive () = this.getJobs ("active", true)
        
    //Events
    member x.on = { 
        Paused = pausedEvent.Publish
        Resumed = resumedEvent.Publish
        Completed = completedEvent.Publish
        Progress = progressEvent.Publish
        Failed = failedEvent.Publish
    }

    //Internals
    member internal x.toKey (kind:string) = RedisKey.op_Implicit ("bull:" + name + ":" + kind)
        
    member internal x.emitQueueEvent (eventType) = 
        async {
            logger.Info "emitting new queue-event %A for queue %s" eventType name
            match eventType with 
            | Paused -> pausedEvent.Trigger({ queue = this });
            | Resumed -> resumedEvent.Trigger({ queue = this });
            | _ -> failwith "Not a queue event!"
        }

    member internal x.emitNewJobEvent jobId =
        async {
            newJobEvent.Trigger({ jobId = jobId });
        }

    member internal x.emitJobEvent (eventType, job:Job<'a>, ?value, ?exn, ?data) = 
        async {
            let eventData = 
                {
                    job = job
                    progress = value
                    err = exn
                    data = None
                }
            
            match eventType with 
            | Completed -> completedEvent.Trigger(eventData)
            | Progress -> progressEvent.Trigger(eventData)
            | Failed -> failedEvent.Trigger(eventData)
            | _ -> failwith "Not a job event!"
        }

    member internal x.client = dbFactory