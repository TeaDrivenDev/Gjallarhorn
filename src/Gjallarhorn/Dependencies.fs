﻿namespace Gjallarhorn.Internal

open Gjallarhorn

open System
open System.Runtime.CompilerServices

[<AbstractClass;AllowNullLiteral>]
type private DependencyTrackerBase() =
    do ()
/// <summary>Used to track dependencies</summary>
/// <remarks>This class is fully thread safe, and will not hold references to dependent targets</remarks>
[<AllowNullLiteral>]
type private DependencyTracker<'a>() =
    inherit DependencyTrackerBase()

    // We want this as lightweight as possible,
    // so we do our own management as needed
    let mutable depIDeps : WeakReference<IDependent> array = [| |]
    let mutable depObservers : WeakReference<IObserver<'a>> array = [| |]
        
    // These are ugly, as it purposefully creates side effects
    // It returns true if we signaled and the object is alive,
    // otherwise false
    let signalIfAliveDep source (wr: WeakReference<IDependent>) =
        let success, dep = wr.TryGetTarget() 
        if success then 
            dep.RequestRefresh source
        success
    let signalIfAliveObs (source : IView<'a>) (wr: WeakReference<IObserver<'a>>) =
        let success, obs = wr.TryGetTarget() 
        if success then 
            obs.OnNext(source.Value)
        success

    // Do our signal, but also remove any unneeded dependencies while we're at it
    let signalAndUpdateDependencies source =
        depIDeps <- depIDeps |> Array.filter (signalIfAliveDep source)
        depObservers <- depObservers |> Array.filter (signalIfAliveObs source)

    // Remove a dependency, as well as all "dead" dependencies
    let removeAndFilterDep dep (wr : WeakReference<IDependent>) =
        match wr.TryGetTarget() with
        | false, _ -> false
        | true, v when v = dep -> 
            false
        | true, _ -> true
    let removeAndFilterObs obs (wr : WeakReference<IObserver<'a>>) =
        match wr.TryGetTarget() with
        | false, _ -> false
        | true, v when v = obs -> 
            // Mark observer completed
            obs.OnCompleted()
            false
        | true, _ -> true
    let markObsComplete (wr : WeakReference<IObserver<'a>>) =
        match wr.TryGetTarget() with
        | true, obs -> 
            obs.OnCompleted()
        | _ -> ()


    member private __.LockObj with get() = depIDeps // Always lock on this array

    /// Adds a new dependency to the tracker
    member this.Add dep =
        lock this.LockObj (fun _ ->
            depIDeps <- depIDeps |> Array.append [| WeakReference<_>(dep) |])
    member this.Add obs =
        lock this.LockObj (fun _ ->
            depObservers <- depObservers |> Array.append [| WeakReference<_>(obs) |])

    /// Removes a dependency from the tracker, and returns true if there are still dependencies remaining
    member this.Remove dep = 
        lock this.LockObj (fun _ ->
            depIDeps <- depIDeps |> Array.filter (removeAndFilterDep dep)
            depIDeps.Length + depObservers.Length > 0)
    member this.Remove obs = 
        lock this.LockObj (fun _ ->
            depObservers <- depObservers |> Array.filter (removeAndFilterObs obs)
            depIDeps.Length + depObservers.Length > 0)

    /// Removes a dependency from the tracker, and returns true if there are still dependencies remaining
    member this.RemoveAll () = 
        lock this.LockObj 
            (fun _ -> 
                depIDeps <- [| |]
                depObservers
                |> Array.iter markObsComplete
                depObservers <- [| |])

    /// Signals the dependencies with a given source, and returns true if there are still dependencies remaining
    member this.Signal (source : IView<'a>) = 
        lock this.LockObj (fun _ ->
            signalAndUpdateDependencies source
            depIDeps.Length > 0)

    interface IDependencyManager<'a> with
        member this.Add (dep: IDependent) = this.Add dep
        member this.Add (dep: IObserver<'a> ) = this.Add dep
        member this.Remove (dep: IDependent) = ignore <| this.Remove dep
        member this.Remove (dep: IObserver<'a> ) = ignore <| this.Remove dep
        member this.RemoveAll () = this.RemoveAll()
        member this.Signal source = ignore <| this.Signal source

/// <summary>Manager of all dependency tracking.  Handles signaling of IDependent instances from any given source</summary>
/// <remarks>This class is fully thread safe, and will not hold references to either source or dependent targets</remarks>
[<AbstractClass; Sealed>]
type SignalManager() =
    static let dependencies = ConditionalWeakTable<obj, DependencyTrackerBase>()
    static let createValueCallbackFor (view : IView<'a>) = ConditionalWeakTable<obj, DependencyTrackerBase>.CreateValueCallback((fun _ -> DependencyTracker<'a>() :> DependencyTrackerBase))

    static let remove source =
        lock dependencies (fun _ -> dependencies.Remove(source) |> ignore)

    static let tryGet (source : IView<'a>) =
        match dependencies.TryGetValue(source) with
        | true, dep -> true, dep :?> DependencyTracker<'a>
        | false, _ -> false, null
        

    /// Signals all dependencies tracked on a given source
    static member Signal (source : IView<'a>) =
        lock dependencies (fun _ ->
            let exists, dep = tryGet source
            if exists then             
                if not(dep.Signal(source)) then
                    remove source)
    
    /// Adds dependency tracked on a given source
    static member internal AddDependency (source : IView<'a>, target : IDependent) =
        lock dependencies (fun _ -> 
            let dep = dependencies.GetValue(source, createValueCallbackFor source) :?> DependencyTracker<'a>
            dep.Add target)
    static member internal AddDependency (source : IView<'a>, target : IObserver<'a>) =
        lock dependencies (fun _ -> 
            let dep = dependencies.GetValue(source, createValueCallbackFor source) :?> DependencyTracker<'a>
            dep.Add target)

    /// Removes a dependency tracked on a given source
    static member internal RemoveDependency (source : IView<'a>, target : IDependent) =
        let removeDep () =
            match tryGet source with
            | true, dep ->
                if not(dep.Remove target) then
                    remove source
            | false, _ -> ()
        lock dependencies removeDep
    static member internal RemoveDependency (source : IView<'a>, target : IObserver<'a>) =
        let removeDep () =
            match tryGet source with
            | true, dep ->
                if not(dep.Remove target) then
                    remove source
            | false, _ -> ()
        lock dependencies removeDep

    /// Removes all dependencies tracked on a given source
    static member RemoveAllDependencies (source : IView<'a>) =
        remove source

    /// Returns true if a given source has dependencies
    static member IsTracked (source : IView<'a>) =
        lock dependencies (fun _ -> fst <| dependencies.TryGetValue(source))

type internal RemoteDependencyMananger<'a>(source : IView<'a>) =
    interface IDependencyManager<'a> with
        member __.Add (dep: IDependent) = SignalManager.AddDependency(source, dep)
        member __.Remove (dep: IDependent) = SignalManager.RemoveDependency(source, dep)
        member __.Add (obs: IObserver<'a> ) = SignalManager.AddDependency(source, obs)
        member __.Remove (obs: IObserver<'a> ) = SignalManager.RemoveDependency(source, obs)
        member __.Signal source = SignalManager.Signal source
        member __.RemoveAll () = SignalManager.RemoveAllDependencies source

/// Module used to create and manage dependencies
module Dependencies =
    /// Create a dependency manager
    let create () = 
        DependencyTracker<_>() :> IDependencyManager<_>
    /// Create a remote dependency manager
    let createRemote source =
        RemoteDependencyMananger<_>(source) :> IDependencyManager<_>