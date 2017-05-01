﻿namespace FsCPS

open System
open System.Collections.Generic
open System.Collections.ObjectModel
open System.Runtime.InteropServices
open FsCPS.Native


/// Human-readable absolute path of a CPS attribute.
type CPSPath = CPSPath of string
    with
        
        /// Appends a relative path to this one.
        member this.Append s =
            if isNull s then
                nullArg "s"

            let (CPSPath rawPath) = this
            CPSPath(rawPath + "/" + s)

        override this.ToString() =
            let (CPSPath rawPath) = this
            rawPath


/// Key for the CPS system.
/// A key can be constructed from its string form, or from a qualifier and a path.
/// Note that when constructing a key from its string representation,
/// the properties `Qualifier` and `Path` are not available.
type CPSKey private (key, qual, path) =

    /// Constructs a new key from a qualifier and a path.
    new (qual: CPSQualifier, path: CPSPath) =
        let key =
            NativeMethods.CreateKey qual (path.ToString())
            |>> (fun k ->
                let str = NativeMethods.PrintKey(k.Address)
                k.Dispose()
                str
            )
            |> Result.okOrThrow (invalidArg "path")
        CPSKey(key, Some qual, Some path)

    /// Constructs a new key from its string representation.
    /// Note that this will make the properties `Qualifier` and `Path` unavailable.
    new (key) =
        CPSKey(key, None, None)

    /// String representation of the key.
    member val Key: string = key

    /// Qualifier of the key.
    /// Is `None` if the key was constructed from a string.
    member val Qualifier: CPSQualifier option = qual

    /// Path of the key.
    /// Is `None` if the key was constructed from a string.
    member val Path: CPSPath option = path

    override this.ToString() =
        key


/// Object of the CPS system.
type CPSObject(key: CPSKey) =

    let attributes = Dictionary<CPSPath, CPSAttribute>()

    /// Constructs a new object with a key with the given path.
    /// The default qualifier is `CPSQualifier.Target`.
    new (rootPathStr) =
        CPSObject(CPSKey(CPSQualifier.Target, CPSPath rootPathStr))

    /// Constructs a new object with a key with the given path.
    /// The default qualifier is `CPSQualifier.Target`.
    new (rootPath) =
        CPSObject(CPSKey(CPSQualifier.Target, rootPath))

    /// Constructs a new object with a key with the given path and qualifier.
    new (rootPath, qual) =
        CPSObject(CPSKey(qual, rootPath))

    /// Key of the object.
    member val Key = key

    /// Read-only view of the attributes of this object as a dictionary.
    /// Attributes are indexed by their path.
    member val Attributes = ReadOnlyDictionary(attributes)

    /// Sets the value of an attribute.
    /// Note that `name` is expected to be a path relative to the object's key.
    member this.SetAttribute(name: string, value: byte[]) =
        match this.Key.Path with
        | Some path -> this.SetAttribute(path.Append(name), value)
        | None ->
            invalidOp (
                "Cannot use a relative path to get/set attributes, " +
                "since this object has been constructed with a partial key. " +
                "Use the overload accepting an absolute CPSPath."
            )

    /// Sets the value of an attribute using its absolute path.
    member this.SetAttribute(path: CPSPath, value: byte[]) =
        attributes.Add(path, CPSAttribute(this, path, value))

    /// Extracts an attribute from this object.
    /// Note that `name` is expected to be a path relative to the object's key.
    member this.GetAttribute(name: string) =
        match this.Key.Path with
        | Some path -> this.GetAttribute(path.Append(name))
        | None ->
            invalidOp (
                "Cannot use a relative path to get/set attributes, " +
                "since this object has been constructed with a partial key. " +
                "Use the overload accepting an absolute CPSPath."
            )

    /// Extracts an attribute using its absolute path.
    member this.GetAttribute(path: CPSPath) =
       match attributes.TryGetValue(path) with
       | (true, a) -> Some a
       | _ -> None

    /// Removes an attribute from this object.
    /// Note that `name` is expected to be a path relative to the object's key.
    member this.RemoveAttribute(name: string) =
        match this.Key.Path with
        | Some path -> this.RemoveAttribute(path.Append(name))
        | None ->
            invalidOp (
                "Cannot use a relative path to get/set attributes, " +
                "since this object has been constructed with a partial key. " +
                "Use the overload accepting an absolute CPSPath."
            )

    /// Removes an attribute using its absolute path.
    member this.RemoveAttribute(path: CPSPath) =
        attributes.Remove(path)

    /// Converts this object to its native representation.
    /// This method will pin all the attributes and return
    /// a list of all the allocated handles.
    member internal this.ToNativeObject() =
        
        // Creates a new object
        NativeMethods.CreateObject()

        // Adds all the attributes
        >>= (fun nativeObject ->
            attributes.Values
            |> foldResult (fun _ attr ->
                attr.AddToNativeObject(nativeObject)
            ) ()
            |>> (fun _ -> nativeObject)
        )

        // Sets the object's key
        >>= NativeMethods.SetObjectKey this.Key.Key

    /// Constructs a new `CPSObject` from then given native object.
    /// Attributes are copied from the native object to new managed arrays.
    static member internal FromNativeObject(nativeObject: NativeObject) =
        
        // Extracts the key from the native object and construct a managed one
        NativeMethods.GetObjectKey(nativeObject)
        |>> NativeMethods.PrintKey
        |>> CPSKey
        |>> CPSObject

        // Iterate the attributes and copy them into the object
        >>= (fun o ->
            NativeMethods.IterateAttributes(nativeObject)
            |> foldResult (fun _ attrId ->
                let path = CPSPath(NativeMethods.AttrIdToPath(attrId))
                NativeMethods.GetAttribute nativeObject attrId
                |>> (fun attr -> o.SetAttribute(path, attr))
            ) ()
            |>> (fun _ -> o)
        )


/// Attribute of a CPS object.
and CPSAttribute internal (obj: CPSObject, path: CPSPath, value: byte[]) =

    member val Path = path
    member val Value = value
    member val OwnerObject = obj

    member internal this.AddToNativeObject(obj: NativeObject) =
        NativeMethods.AttrIdFromPath(path.ToString())
        >>= (fun attrId ->
            NativeMethods.AddAttribute obj attrId value
        )


/// Container of a transaction for CPS objects.
/// The operations are accumulated and delayed till the actual commit.
type CPSTransaction() =

    let createObjects = ResizeArray<CPSObject>()
    let setObjects = ResizeArray<CPSObject>()
    let deleteObjects = ResizeArray<CPSObject>()

    static let addObjects (objs: seq<CPSObject>) cb context =

        // Apply the operator to all the objects in the sequence
        objs
        |> foldResult (fun _ o ->
            o.ToNativeObject()
            >>= cb context
        ) ()

        // Return the context for chaining
        |>> (fun () -> context)

    /// Adds a request for the creation of an object to this transaction.
    /// Note that the transaction is not executed yet. To commit the transaction,
    /// call the `Commit` method.
    member this.Create(o: CPSObject) =
        createObjects.Add(o)
    
    /// Adds a request for the update of an object to this transaction.
    /// Note that the transaction is not executed yet. To commit the transaction,
    /// call the `Commit` method.
    member this.Set(o: CPSObject) =
        setObjects.Add(o)

    /// Adds a request for the deletion of an object to this transaction.
    /// Note that the transaction is not executed yet. To commit the transaction,
    /// call the `Commit` method.
    member this.Delete(o: CPSObject) =
        deleteObjects.Add(o)

    /// Commits all the operations queued in this transaction.
    /// Blocks until the transaction is completed.
    member this.Commit() =

        // These references are used for cleanup
        let mutable transaction = None

        let mutable result =

            // First of all, we create a new transaction and store it
            NativeMethods.CreateTransaction()
            >>= (fun t -> transaction <- Some t; Ok(t))

            // Then we add the objects to it
            >>= addObjects createObjects NativeMethods.TransactionAddCreate
            >>= addObjects setObjects NativeMethods.TransactionAddSet
            >>= addObjects deleteObjects NativeMethods.TransactionAddDelete

            // And commit!
            >>= NativeMethods.TransactionCommit

        // Destroy the transaction.
        // This will also free all the native objects and the attributes we created earlier.
        // Note that we destroy the transaction in every case, both error and success to
        // release native memory.
        if transaction.IsSome then
            result <- NativeMethods.DestroyTransaction(transaction.Value)

        result

    /// Executes immediately a get request with the given filters.
    static member Get(filters: seq<CPSObject>) =
        
        // These references are used for cleanup
        let mutable req = None

        let readResponse (req: NativeGetParams) () =
            NativeMethods.IterateObjectList(req.list)
            |> foldResult (fun objectList nativeObject ->

                // Read the object and append the object to the list
                CPSObject.FromNativeObject nativeObject
                |>> (fun o -> o :: objectList)

            ) []

            // Revert the list of objects to match the original order
            |>> List.rev

        let mutable result =

            // Creates a new request with the given filters
            NativeMethods.CreateGetRequest()
            >>= (fun t -> req <- Some t; Ok(t))
            >>= addObjects
                    filters
                    (fun req obj -> NativeMethods.AppendObjectToList req.filters obj |>> ignore)

            // And sends the request
            >>= NativeMethods.GetRequestSend

            // Now extract the objects from the response
            >>= readResponse req.Value

        // Destroy the transaction.
        // This will also free all the native objects and the attributes we created earlier.
        // Note that we destroy the transaction in every case, both error and success to
        // release native memory.
        if req.IsSome then
            match NativeMethods.DestroyGetRequest(req.Value), result with
            | Error e, Ok _ -> result <- Error e
            | _ -> ()

        result


/// Interface representing a CPS server able to serve requests sent from a client.
/// A server listens for requests on a certain key and its methods get called when
/// a new requests is incoming. To register a server, use `CPSServer.Register`.
type ICPSServer =

    /// Responds to get requests.
    /// The argument is an object representing the filter.
    abstract member Get: CPSObject -> Result<seq<CPSObject>, string>

    /// Responds to set requests.
    /// The arguments are an object representing the filter and the type of operation requested.
    /// This function is expected to return an object that will contain data that can be used
    /// to rollback the operation if requested.
    abstract member Set: CPSObject * CPSOperationType -> Result<CPSObject, string>

    /// Rolls back a failed transaction.
    abstract member Rollback: CPSObject -> Result<unit, string>


/// Handle for a registration of a server in the CPS system.
/// To register a CPS server use the method `CPSServer.Register`.
type CPSServerHandle internal (key: CPSKey, server: ICPSServer) =
    
    let mutable _isValid = true
    
    /// The key for which the server was registered.
    member val Key = key

    /// The registered server.
    member val Server = server

    /// Returns a value indicating whether this registration is still valid.
    /// To unregister the server, use the method `Cancel`.
    member val IsValid = _isValid

    /// Unregisters the server from the CPS system.
    member this.Cancel() =
        if _isValid then
            
            // Remove the reference
            CPSServer.Handles.Remove(this) |> ignore
            _isValid <- false

            // It looks like it's not possible to unregister a server...
            raise (NotImplementedException())

    member internal this.NativeGetCallback(_: nativeint, param: nativeint, index: unativeint) =
        
        // Param is a pointer to a get request parameters
        let req = Marshal.PtrToStructure(param, typeof<NativeGetParams>) :?> NativeGetParams
        
        // Extracts the object from the list and converts it into a managed object
        NativeMethods.ObjectListGet req.filters index
        >>= CPSObject.FromNativeObject

        // Invokes the managed server
        >>= server.Get

        // Converts the results to native objects and appends them to the native list
        >>= foldResult (fun _ o ->
            o.ToNativeObject()
            |>> NativeMethods.AppendObjectToList req.list
            |>> ignore
        ) ()

        // Converts the result to an integer
        |> function
           | Ok () -> 0
           | Error _ -> 1 

    member internal this.NativeSetCallback(_: nativeint, param: nativeint, index: unativeint) =
        
        // Param is a pointer to a transaction params structure
        let req = Marshal.PtrToStructure(param, typeof<NativeTransactionParams>) :?> NativeTransactionParams

        // Extracts the object from the list and the operation requested from its key
        NativeMethods.ObjectListGet req.change_list index
        >>= (fun o ->
            NativeMethods.GetObjectKey o
            >>= NativeMethods.GetOperationFromKey
            |>> (fun op -> (o, op))
        )

        // Convert the native object to a managed one
        >>= (fun (o, op) ->
            CPSObject.FromNativeObject(o)
            |>> (fun o -> (o, op))
        )

        // Invokes the managed server and transforms the rollback object to a native one
        >>= server.Set
        >>= (fun o -> o.ToNativeObject())

        // Store the rollback object in the list
        >>= (fun nativeRollbackObject ->
            
            // If there was an object already allocated, copy the returned object, otherwise
            // append directly it to the list
            match NativeMethods.ObjectListGet req.prev index with
            | Ok dest ->
                NativeMethods.CloneObject dest nativeRollbackObject
                |>> (fun _ -> NativeMethods.DestroyObject nativeRollbackObject)
            | Error _ ->
                NativeMethods.AppendObjectToList req.prev nativeRollbackObject
                |>> ignore

        )

        // Converts the result to an integer
        |> function
           | Ok () -> 0
           | Error _ -> 1
    
    member internal this.NativeRollbackCallback(_: nativeint, param: nativeint, index: unativeint) =
        
        // Param is a pointer to a transaction params structure
        let req = Marshal.PtrToStructure(param, typeof<NativeTransactionParams>) :?> NativeTransactionParams

        // Extracts the object from the list and converts it to a managed one
        NativeMethods.ObjectListGet req.prev index
        >>= CPSObject.FromNativeObject

        // Call the server
        >>= server.Rollback

        // Converts the result to an integer
        |> function
           | Ok () -> 0
           | Error _ -> 1


/// Provides a set of methods to manage CPS servers.
and CPSServer private () =
    
    static let _subsystemHandle = lazy (
        NativeMethods.InitializeOperationSubsystem()
        |> Result.okOrThrow invalidOp
    )

    static member val internal Handles: ResizeArray<_> = ResizeArray<CPSServerHandle>()

    /// Registers a new CPS server with the given key.
    /// Returns an handle that can be used to cancel the registration
    static member Register(key: CPSKey, server: ICPSServer) =
        
        // Create a new handle and register it
        let handle = CPSServerHandle(key, server)
        NativeMethods.RegisterServer
            _subsystemHandle.Value
            key.Key
            (NativeServerCallback(handle.NativeGetCallback))
            (NativeServerCallback(handle.NativeSetCallback))
            (NativeServerCallback(handle.NativeRollbackCallback))

        // Keep a reference to the handle to make sure that the GC does not collect it
        // invalidating the function pointers that we passed to the native code
        |>> (fun _ ->
            CPSServer.Handles.Add(handle)
            handle
        )