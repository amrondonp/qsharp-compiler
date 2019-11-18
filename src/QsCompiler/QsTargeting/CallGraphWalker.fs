﻿module Microsoft.Quantum.QsCompiler.Targeting.CallGraphWalker

open System.Collections.Generic
open System.Collections.Immutable
open Microsoft.Quantum.QsCompiler.DataTypes
open Microsoft.Quantum.QsCompiler.SyntaxTokens
open Microsoft.Quantum.QsCompiler.SyntaxTree
open Microsoft.Quantum.QsCompiler.Transformations.Core
open Microsoft.Quantum.QsCompiler.Transformations.QsCodeOutput


type private SpecializationKey =
    {
        QualifiedName : QsQualifiedName
        Kind : QsSpecializationKind
        TypeArgString : string
    }


type CallGraph() =
    let sep = '|'
    let dependencies = new Dictionary<SpecializationKey, HashSet<SpecializationKey>>()
    let keyTypes = new Dictionary<string, ResolvedType>()

    let SpecInfoToKey name kind (types : QsNullable<ImmutableArray<ResolvedType>>) =
        let ResolvedTypeToString rt =
            let exprTransformer = new ExpressionToQs()
            let transformer = new ExpressionTypeToQs(exprTransformer)
            transformer.Apply(rt)
        let getArgString (args : ImmutableArray<ResolvedType>) =
            if args.IsDefaultOrEmpty
            then ""
            else
                let typeArgs = args |> Seq.map (fun t -> (t, ResolvedTypeToString t))
                typeArgs |> Seq.iter (fun (t, s) -> keyTypes.[s] <- t)
                typeArgs |> Seq.map snd
                            |> String.concat (sep.ToString())
        let typeArgString = types |> QsNullable<_>.Map getArgString
                                  |> fun n -> n.ValueOr ""
        { QualifiedName = name; Kind = kind; TypeArgString = typeArgString }

    let SpecToKey (spec : QsSpecialization) =
        SpecInfoToKey spec.Parent spec.Kind spec.TypeArguments

    let StringToTypeArray (ts : string) =
        let lookupString s = 
            match keyTypes.TryGetValue(s) with
            | true, t -> t
            | false, _ -> ResolvedType.New(InvalidType)
        let typeSequence = ts.Split(sep) |> Seq.map lookupString 
        if typeSequence |> Seq.isEmpty
        then Null
        else Value (typeSequence |> ImmutableArray.ToImmutableArray)

    let RecordDependency callerKey calledKey =
        match dependencies.TryGetValue(callerKey) with
        | true, deps -> deps.Add(calledKey) |> ignore
        | false, _ -> let newDeps = new HashSet<SpecializationKey>()
                      newDeps.Add(calledKey) |> ignore
                      dependencies.[callerKey] <- newDeps

    let rec WalkDependencyTree root (accum : HashSet<SpecializationKey>) =
        match dependencies.TryGetValue(root) with
        | true, next -> 
            next |> Seq.fold (fun (a : HashSet<SpecializationKey>) k -> if a.Add(k) then WalkDependencyTree k a else a) accum
        | false, _ -> accum

    member this.AddDependency(callerSpec, calledSpec) =
        let callerKey = SpecToKey callerSpec
        let calledKey = SpecToKey calledSpec
        RecordDependency callerKey calledKey

    member this.AddDependency(callerSpec, calledName, calledKind, calledTypeArgs) =
        let callerKey = SpecToKey callerSpec
        let calledKey = SpecInfoToKey calledName calledKind calledTypeArgs
        RecordDependency callerKey calledKey

    member this.AddDependency(callerName, callerKind, callerTypeArgs, calledName, calledKind, calledTypeArgs) =
        let callerKey = SpecInfoToKey callerName callerKind callerTypeArgs
        let calledKey = SpecInfoToKey calledName calledKind calledTypeArgs
        RecordDependency callerKey calledKey

    member this.FlushDependencies(callerSpec) =
        let key = SpecToKey callerSpec
        dependencies.Remove(key) |> ignore

    member this.GetDependencies(callerSpec) =
        let key = SpecToKey callerSpec
        match dependencies.TryGetValue(key) with
        | true, deps -> 
            deps |> Seq.map (fun key -> (key.QualifiedName, key.Kind, key.TypeArgString |> StringToTypeArray))
        | false, _ -> Seq.empty

    member this.GetDependencyTree(callerSpec) =
        let key = SpecToKey callerSpec
        WalkDependencyTree key (new HashSet<SpecializationKey>(key |> Seq.singleton))
        |> Seq.filter (fun k -> k <> key)
        |> Seq.map (fun key -> (key.QualifiedName, key.Kind, key.TypeArgString |> StringToTypeArray))


type private ExpressionKindGraphBuilder(exprXformer : ExpressionGraphBuilder, graph : CallGraph, 
        spec : QsSpecialization) =
    inherit ExpressionKindWalker()

    let mutable inCall = false
    let mutable adjoint = false
    let mutable controlled = false

    override this.ExpressionWalker x = exprXformer.Walk x
    override this.TypeWalker x = ()

    member private this.HandleCall method arg =
        inCall <- true
        adjoint <- false
        controlled <- false
        this.ExpressionWalker method
        inCall <- false
        this.ExpressionWalker arg

    override this.onOperationCall(method, arg) =
        this.HandleCall method arg

    override this.onFunctionCall(method, arg) =
        this.HandleCall method arg

    override this.onAdjointApplication(ex) =
        adjoint <- true
        base.onAdjointApplication(ex)

    override this.onControlledApplication(ex) =
        controlled <- true
        base.onControlledApplication(ex)

    override this.onIdentifier(sym, typeArgs) =
        match sym with
        | GlobalCallable(name) ->
            if inCall
            then
                let kind = match adjoint, controlled with
                           | false, false -> QsBody
                           | false, true  -> QsControlled
                           | true,  false -> QsAdjoint
                           | true,  true  -> QsControlledAdjoint
                graph.AddDependency(spec, name, kind, typeArgs)
            else
                // The callable is being used in a non-call context, such as being
                /// assigned to a variable or passed as an argument to another callable,
                // which means it could get a functor applied at some later time.
                // We're conservative and add all 4 possible kinds.
                graph.AddDependency(spec, name, QsBody, typeArgs)
                graph.AddDependency(spec, name, QsControlled, typeArgs)
                graph.AddDependency(spec, name, QsAdjoint, typeArgs)
                graph.AddDependency(spec, name, QsControlledAdjoint, typeArgs)
        | _ -> ()
        base.onIdentifier(sym, typeArgs)


and private ExpressionGraphBuilder(graph : CallGraph, spec : QsSpecialization) as this =
    inherit ExpressionWalker()

    let kindXformer = new ExpressionKindGraphBuilder(this, graph, spec)

    override this.Kind = upcast kindXformer


type private StatementGraphBuilder(scopeXformer : ScopeGraphBuilder, graph : CallGraph, 
        spec : QsSpecialization) =
    inherit StatementKindWalker()

    let exprXformer = new ExpressionGraphBuilder(graph, spec)

    override this.ScopeWalker x = scopeXformer.Walk x

    override this.ExpressionWalker x = exprXformer.Walk x
    override this.TypeWalker x = ()
    override this.LocationWalker x = ()


and private ScopeGraphBuilder(graph : CallGraph, spec : QsSpecialization) as this =
    inherit ScopeWalker()

    let kindXformer = new StatementGraphBuilder(this, graph, spec)

    override this.StatementKind = upcast kindXformer


type TreeGraphBuilder() =
    inherit SyntaxTreeWalker()

    let graph = new CallGraph()

    let mutable scopeXform = None : ScopeGraphBuilder option

    override this.Scope with get() = scopeXform |> Option.map (fun x -> x :> ScopeWalker)
                                                |> Option.defaultWith (fun () -> new ScopeWalker())

    override this.onSpecializationImplementation(s) =
        let xform = new ScopeGraphBuilder(graph, s)
        scopeXform <- Some xform
        base.onSpecializationImplementation(s)

    member this.CallGraph with get() = graph
