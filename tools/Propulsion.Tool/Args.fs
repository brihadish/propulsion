module Propulsion.Tool.Args

open Argu
open Serilog
open System

exception MissingArg of message : string with override this.Message = this.message
let missingArg msg = raise (MissingArg msg)

module Configuration =

    module Cosmos =

        let [<Literal>] CONNECTION =                "EQUINOX_COSMOS_CONNECTION"
        let [<Literal>] DATABASE =                  "EQUINOX_COSMOS_DATABASE"
        let [<Literal>] CONTAINER =                 "EQUINOX_COSMOS_CONTAINER"

    module Dynamo =

        let [<Literal>] SERVICE_URL =               "EQUINOX_DYNAMO_SERVICE_URL"
        let [<Literal>] ACCESS_KEY =                "EQUINOX_DYNAMO_ACCESS_KEY_ID"
        let [<Literal>] SECRET_KEY =                "EQUINOX_DYNAMO_SECRET_ACCESS_KEY"
        let [<Literal>] TABLE =                     "EQUINOX_DYNAMO_TABLE"
        let [<Literal>] INDEX_TABLE =               "EQUINOX_DYNAMO_TABLE_INDEX"

    module Kafka =

        let [<Literal>] BROKER =                    "PROPULSION_KAFKA_BROKER"
        let [<Literal>] TOPIC =                     "PROPULSION_KAFKA_TOPIC"

type Configuration(tryGet : string -> string option) =

    member val tryGet =                             tryGet
    member _.get key =                              match tryGet key with
                                                    | Some value -> value
                                                    | None -> missingArg $"Missing Argument/Environment Variable %s{key}"

    member x.CosmosConnection =                     x.get Configuration.Cosmos.CONNECTION
    member x.CosmosDatabase =                       x.get Configuration.Cosmos.DATABASE
    member x.CosmosContainer =                      x.get Configuration.Cosmos.CONTAINER

    member x.DynamoServiceUrl =                     x.get Configuration.Dynamo.SERVICE_URL
    member x.DynamoAccessKey =                      x.get Configuration.Dynamo.ACCESS_KEY
    member x.DynamoSecretKey =                      x.get Configuration.Dynamo.SECRET_KEY
    member x.DynamoTable =                          x.get Configuration.Dynamo.TABLE
    member x.DynamoIndexTable =                     tryGet Configuration.Dynamo.INDEX_TABLE

    member x.KafkaBroker =                          x.get Configuration.Kafka.BROKER
    member x.KafkaTopic =                           x.get Configuration.Kafka.TOPIC

module Cosmos =

    open Configuration.Cosmos

    module CosmosStoreContext =

        /// Create with default packing and querying policies. Search for other `module CosmosStoreContext` impls for custom variations
        let create (storeClient : Equinox.CosmosStore.CosmosStoreClient) =
            let maxEvents = 256
            Equinox.CosmosStore.CosmosStoreContext(storeClient, tipMaxEvents = maxEvents)

    type Equinox.CosmosStore.CosmosStoreConnector with

        member private x.LogConfiguration(log : ILogger, connectionName, databaseId, containerId) =
            let o = x.Options
            let timeout, retries429, timeout429 = o.RequestTimeout, o.MaxRetryAttemptsOnRateLimitedRequests, o.MaxRetryWaitTimeOnRateLimitedRequests
            log.Information("CosmosDb {name} {mode} {endpointUri} timeout {timeout}s; Throttling retries {retries}, max wait {maxRetryWaitTime}s",
                            connectionName, o.ConnectionMode, x.Endpoint, timeout.TotalSeconds, retries429, let t = timeout429.Value in t.TotalSeconds)
            log.Information("CosmosDb {name} Database {database} Container {container}",
                            connectionName, databaseId, containerId)

        /// Use sparingly; in general one wants to use CreateAndInitialize to avoid slow first requests
        member private x.CreateUninitialized(databaseId, containerId) =
            x.CreateUninitialized().GetDatabase(databaseId).GetContainer(containerId)

        /// Creates a CosmosClient suitable for running a CFP via CosmosStoreSource
        member x.CreateClient(databaseId, containerId, ?connectionName) =
            x.LogConfiguration(Log.Logger, defaultArg connectionName "Source", databaseId, containerId)
            x.CreateUninitialized(databaseId, containerId)

        /// Connect a CosmosStoreClient, including warming up
        member x.ConnectStore(connectionName, databaseId, containerId) =
            x.LogConfiguration(Log.Logger, connectionName, databaseId, containerId)
            Equinox.CosmosStore.CosmosStoreClient.Connect(x.CreateAndInitialize, databaseId, containerId)

    type [<NoEquality; NoComparison>] Parameters =
        | [<AltCommandLine "-m">]           ConnectionMode of Microsoft.Azure.Cosmos.ConnectionMode
        | [<AltCommandLine "-s">]           Connection of string
        | [<AltCommandLine "-d">]           Database of string
        | [<AltCommandLine "-c">]           Container of string
        | [<AltCommandLine "-o">]           Timeout of float
        | [<AltCommandLine "-r">]           Retries of int
        | [<AltCommandLine "-rt">]          RetriesWaitTime of float
        | [<AltCommandLine("-a"); Unique>]  LeaseContainer of string
        | [<AltCommandLine("-as"); Unique>] Suffix of string

        | [<AltCommandLine("-l"); Unique>]      LagFreqM of float
        interface IArgParserTemplate with
            member a.Usage = a |> function
                | ConnectionMode _ ->       "override the connection mode. Default: Direct."
                | Connection _ ->           "specify a connection string for a Cosmos account. (optional if environment variable " + CONNECTION + " specified)"
                | Database _ ->             "specify a database name for Cosmos store. (optional if environment variable " + DATABASE + " specified)"
                | Container _ ->            "specify a container name for Cosmos store. (optional if environment variable " + CONTAINER + " specified)"
                | Timeout _ ->              "specify operation timeout in seconds (default: 5)."
                | Retries _ ->              "specify operation retries (default: 1)."
                | RetriesWaitTime _ ->      "specify max wait-time for retry when being throttled by Cosmos in seconds (default: 5)"
                | LeaseContainer _ ->       "Specify full Lease Container Name (default: Container + Suffix)."
                | Suffix _ ->               "Specify Container Name suffix (default: `-aux`, if LeaseContainer not specified)."
                | LagFreqM _ ->             "Specify frequency to dump lag stats. Default: off"

    type Arguments(c : Configuration, a : ParseResults<Parameters>) =
        let connection =                    a.TryGetResult Connection |> Option.defaultWith (fun () -> c.CosmosConnection)
        let discovery =                     Equinox.CosmosStore.Discovery.ConnectionString connection
        let mode =                          a.TryGetResult ConnectionMode
        let timeout =                       a.GetResult(Timeout, 5.) |> TimeSpan.FromSeconds
        let retries =                       a.GetResult(Retries, 1)
        let maxRetryWaitTime =              a.GetResult(RetriesWaitTime, 5.) |> TimeSpan.FromSeconds
        let connector =                     Equinox.CosmosStore.CosmosStoreConnector(discovery, timeout, retries, maxRetryWaitTime, ?mode = mode)
        let database =                      a.TryGetResult Database |> Option.defaultWith (fun () -> c.CosmosDatabase)
        let checkpointInterval =            TimeSpan.FromHours 1.
        member val ContainerId =            a.TryGetResult Container |> Option.defaultWith (fun () -> c.CosmosContainer)
        member x.MonitoredContainer() =     connector.CreateClient(database, x.ContainerId)
        member val LeaseContainerId =       a.TryGetResult LeaseContainer
        member x.LeasesContainerName =      match x.LeaseContainerId with Some x -> x | None -> x.ContainerId + a.GetResult(Suffix, "-aux")
        member x.ConnectLeases() =          connector.CreateClient(database, x.LeasesContainerName, "Leases")
        member _.MaybeLogLagInterval =      a.TryGetResult LagFreqM |> Option.map TimeSpan.FromMinutes

        member x.CreateCheckpointStore(group, cache, storeLog) = async {
            let! store = connector.ConnectStore("Checkpoints", database, x.ContainerId)
            let context = CosmosStoreContext.create store
            return Propulsion.Feed.ReaderCheckpoint.CosmosStore.create storeLog (group, checkpointInterval) (context, cache) }

module Dynamo =

    open Configuration.Dynamo

    type Equinox.DynamoStore.DynamoStoreConnector with

        member x.LogConfiguration() =
            Log.Information("DynamoStore {endpoint} Timeout {timeoutS}s Retries {retries}",
                            x.Endpoint, (let t = x.Timeout in t.TotalSeconds), x.Retries)

    type Equinox.DynamoStore.DynamoStoreClient with

        member internal x.LogConfiguration(role) =
            Log.Information("DynamoStore {role:l} Table {table}", role, x.TableName)

    type Amazon.DynamoDBv2.IAmazonDynamoDB with

        /// Connects to a Store as both a ChangeFeedProcessor Monitored Container and a CosmosStoreClient
        member x.ConnectStore(role, table) =
            let storeClient = Equinox.DynamoStore.DynamoStoreClient(x, table)
            storeClient.LogConfiguration(role)
            storeClient

    module DynamoStoreContext =

        let create (storeClient : Equinox.DynamoStore.DynamoStoreClient) =
            Equinox.DynamoStore.DynamoStoreContext(storeClient)

    type [<NoEquality; NoComparison>] Parameters =
        | [<AltCommandLine "-s">]           ServiceUrl of string
        | [<AltCommandLine "-sa">]          AccessKey of string
        | [<AltCommandLine "-ss">]          SecretKey of string
        | [<AltCommandLine "-t">]           Table of string
        | [<AltCommandLine "-r">]           Retries of int
        | [<AltCommandLine "-rt">]          RetriesTimeoutS of float
        | [<AltCommandLine "-i">]           IndexTable of string
        | [<AltCommandLine "-is">]          IndexSuffix of string
        | [<AltCommandLine "-d">]           StreamsDop of int
        interface IArgParserTemplate with
            member a.Usage = a |> function
                | ServiceUrl _ ->           "specify a server endpoint for a Dynamo account. (optional if environment variable " + SERVICE_URL + " specified)"
                | AccessKey _ ->            "specify an access key id for a Dynamo account. (optional if environment variable " + ACCESS_KEY + " specified)"
                | SecretKey _ ->            "specify a secret access key for a Dynamo account. (optional if environment variable " + SECRET_KEY + " specified)"
                | Table _ ->                "specify a table name for the primary store. (optional if environment variable " + TABLE + ", or `IndexTable` specified)"
                | Retries _ ->              "specify operation retries (default: 1)."
                | RetriesTimeoutS _ ->      "specify max wait-time including retries in seconds (default: 10)"
                | IndexTable _ ->           "specify a table name for the index store. (optional if environment variable " + INDEX_TABLE + " specified. default: `Table`+`IndexSuffix`)"
                | IndexSuffix _ ->          "specify a suffix for the index store. (not relevant if `Table` or `IndexTable` specified. default: \"-index\")"
                | StreamsDop _ ->           "parallelism when loading events from Store Feed Source. Default: Don't load events"

    type Arguments(c : Configuration, a : ParseResults<Parameters>) =
        let serviceUrl =                    a.TryGetResult ServiceUrl |> Option.defaultWith (fun () -> c.DynamoServiceUrl)
        let accessKey =                     a.TryGetResult AccessKey  |> Option.defaultWith (fun () -> c.DynamoAccessKey)
        let secretKey =                     a.TryGetResult SecretKey  |> Option.defaultWith (fun () -> c.DynamoSecretKey)
        let indexSuffix =                   a.GetResult(IndexSuffix, "-index")
        let retries =                       a.GetResult(Retries, 1)
        let timeout =                       a.GetResult(RetriesTimeoutS, 10.) |> TimeSpan.FromSeconds
        let connector =                     Equinox.DynamoStore.DynamoStoreConnector(serviceUrl, accessKey, secretKey, timeout, retries)
        let client =                        connector.CreateClient()
        let streamsDop =                    a.TryGetResult StreamsDop
        let checkpointInterval =            TimeSpan.FromHours 1.
        let indexTable =                    a.TryGetResult IndexTable
                                            |> Option.orElseWith  (fun () -> c.DynamoIndexTable)
                                            |> Option.defaultWith (fun () -> c.DynamoTable + indexSuffix)
        let indexClient =                   lazy
                                                connector.LogConfiguration()
                                                client.ConnectStore("Index", indexTable)
        member val IndexTable =             indexTable
        member x.MonitoringParams() =
            let indexClient = indexClient.Value
            match streamsDop with
            | None ->
                Log.Information("DynamoStoreSource NOT Hydrating events"); indexClient, None
            | Some streamsDop ->
                Log.Information("DynamoStoreSource Hydrater parallelism {streamsDop}", streamsDop)
                let table = a.TryGetResult Table |> Option.defaultWith (fun () -> c.DynamoTable)
                let context = client.ConnectStore("Store", table) |> DynamoStoreContext.create
                indexClient, Some (context, streamsDop)

        member x.CreateCheckpointStore(group, cache, storeLog) =
            let context = DynamoStoreContext.create indexClient.Value
            Propulsion.Feed.ReaderCheckpoint.DynamoStore.create storeLog (group, checkpointInterval) (context, cache)
