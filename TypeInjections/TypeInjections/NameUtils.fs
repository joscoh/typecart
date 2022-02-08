// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT

namespace TypeInjections

open System.Collections.Generic
open Microsoft.Boogie
open Microsoft.Dafny
open Utils


// Utility functions to generate/get names for various parts of the type injection generation
module NameUtils =

    // The generated name of the type translation function - takes the original type and gives name TypeOldToNew
    // If the type's name is Name.T, we name the result NameTOldToNew
    // This can be freely changed to give better names; the generated name is not hard-coded anywhere.
    // Currently, this could result in a name collision if we have multiple types with the same name
    let typeTranslationName (t: TopLevelDecl) : string =
        let nameHelp (s: string) : string =
            // this is sort of a hack, but lots of types are named X.T, so we want to disambiguate
            let suffix = "OldToNew"
            let names = s.Split(".")
            let name = names.[names.Length - 1]

            let prefix =
                if name = "T" then
                    names.[names.Length - 2]
                else
                    ""

            prefix + name + suffix

        match t with
        | :? DatatypeDecl as d -> nameHelp d.FullName
        | :? NewtypeDecl as n -> nameHelp n.FullName
        | :? TypeSynonymDecl as t -> nameHelp t.FullName
        | _ ->
            System.Console.WriteLine("Tried to generate a name for an invalid declaration")
            null

    // We want to get the name of a given type for a variety of purposes. We need to strip off the "Old" or "New"
    // from the full name.
    let typeName (t: TopLevelDecl) : string =
        let typeNameAux (s: string) : string = s.Substring(4) in

        match t with
        | :? DatatypeDecl as d -> typeNameAux d.FullName
        | :? NewtypeDecl as n -> typeNameAux n.FullName
        | :? TypeSynonymDecl as t -> typeNameAux t.FullName
        | :? ClassDecl as c -> typeNameAux c.FullName
        | _ -> ""

    // We generate a lot of NameSegments, so this is a convenient shorthand
    let genNameSegment name : NameSegment = NameSegment(Token.NoToken, name, null)

    let genbinding name : ActualBinding =
        ActualBinding(null, NameSegment(Token.NoToken, name, null))

    // We want to get the full module path for a UserDefinedType.
    // This is not stored in the FullName if we have some nested modules.
    // The FullCompanionCompileName has the format Mod1_mMod2_mMod3._Companion_name
    let getModulePath (u: UserDefinedType) : string =
        let path = u.FullCompanionCompileName
        let name = u.Name
        let mods = path.Split("_m")
        let last = mods.[mods.Length - 1]
        // we ignore everything before the last "." - we use the real name instead
        let realLast = last.Split(".").[0]
        mods.[mods.Length - 1] <- realLast

        (List.fold (fun acc x -> acc + "." + x) "" (Array.toList mods)
         + "."
         + name)
            .Substring(1) // remove extra . at beginning

    // The prefixFullDafnyNames function replaces MemberSelectExpr and FunctionCallExpr in an expression with FullDafnyName.
    // We need to prefix the full Dafny name of MemberSelectExpr and FunctionCallExpr with `New` when generating `requires` expressions for synonym types
    // for example, 0 <= |s| <= INT_MAX where INT_MAX is a MemberSelectExpr should be generated by the tool as:
    // 0 <= |s| <= New.TypeUtil.INT_MAX. Here `New.TypeUtil.INT_MAX` is the FullDafnyName of INT_MAX.
    let rec prefixFullDafnyNames (e: Expression) : Expression =
        match e with
        // its easier to add the null case here, as compared to add where prefixFullDafnyNames is being called
        | null -> e
        | :? MemberSelectExpr as m -> MemberSelectExpr(m.tok, m.Obj, m.Member.FullDafnyName) :> Expression
        | :? FunctionCallExpr as f ->
            FunctionCallExpr(
                f.tok,
                f.Function.FullDafnyName,
                prefixFullDafnyNames f.Receiver,
                f.OpenParen,
                ActualBindings(mapFullDafnyNames f.Bindings.Arguments: List<Expression>),
                f.AtLabel
            )
            :> Expression
        | :? IdentifierExpr as i -> IdentifierExpr(i.tok, i.Name) :> Expression
        // alphabetical order from here on.
        // For testing, we specify expressions that are parsed by the Dafny parser into the relevant expression types,
        // and check the transformed expression for FullDafnyName
        | :? ApplyExpr as a -> ApplyExpr(a.tok, prefixFullDafnyNames a.Function, mapFullDafnyNames a.Args) :> Expression
        | :? ApplySuffix as a ->
            ApplySuffix(a.tok, a.AtTok, prefixFullDafnyNames a.Lhs, mapActualBindings a.Bindings.ArgumentBindings)
            :> Expression
        | :? BinaryExpr as b ->
            BinaryExpr(b.tok, b.Op, prefixFullDafnyNames b.E0, prefixFullDafnyNames b.E1) :> Expression
        | :? ChainingExpression as c ->
            ChainingExpression(
                c.tok,
                mapFullDafnyNames c.Operands,
                c.Operators,
                c.OperatorLocs,
                mapFullDafnyNames c.PrefixLimits
            )
            :> Expression
        | :? ConversionExpr as c -> ConversionExpr(c.tok, prefixFullDafnyNames c.E, c.ToType) :> Expression
        | :? DatatypeValue as d ->
            DatatypeValue(d.tok, d.Ctor.EnclosingDatatype.FullName, d.MemberName, mapFullDafnyNames d.Arguments)
            :> Expression
        | :? DefaultValueExpression as d ->
            // TODO: following
            // expressions in SubstMap should also be mapped, usual way of mapping a function over values of Dictionary is not
            // working given the key value of IVariable
            DefaultValueExpression(d.tok, d.Formal, prefixFullDafnyNames d.Receiver, d.SubstMap, d.TypeMap)
            :> Expression
        | :? ExistsExpr as e ->
            ExistsExpr(
                e.tok,
                e.BoundVars,
                prefixFullDafnyNames e.Range,
                prefixFullDafnyNames e.Term,
                if e.Attributes = null then
                    e.Attributes
                else
                    mapAttrbs e.Attributes
            )
            :> Expression
        | :? ExprDotName as e ->
            ExprDotName(e.tok, prefixFullDafnyNames e.Lhs, e.SuffixName, e.OptTypeArguments) :> Expression
        | :? ForallExpr as e ->
            ForallExpr(
                e.tok,
                e.TypeArgs,
                e.BoundVars,
                prefixFullDafnyNames e.Range,
                prefixFullDafnyNames e.Term,
                if e.Attributes = null then
                    e.Attributes
                else
                    mapAttrbs e.Attributes
            )
            :> Expression
        | :? ITEExpr as e ->
            ITEExpr(
                e.tok,
                e.IsBindingGuard,
                prefixFullDafnyNames e.Test,
                prefixFullDafnyNames e.Thn,
                prefixFullDafnyNames e.Els
            )
            :> Expression
        | :? LambdaExpr as e ->
            LambdaExpr(e.tok, e.BoundVars, prefixFullDafnyNames e.Range, mapFrames e.Reads, prefixFullDafnyNames e.Body)
            :> Expression
        | :? LetExpr as e ->
            LetExpr(e.tok, e.LHSs, mapFullDafnyNames e.RHSs, prefixFullDafnyNames e.Body, e.Exact) :> Expression
        | :? LetOrFailExpr as e ->
            LetOrFailExpr(e.tok, e.Lhs, prefixFullDafnyNames e.Rhs, prefixFullDafnyNames e.Body) :> Expression
        | :? MapComprehension as m ->
            MapComprehension(
                m.tok,
                m.Finite,
                m.BoundVars,
                prefixFullDafnyNames m.Range,
                prefixFullDafnyNames m.TermLeft,
                prefixFullDafnyNames m.Term,
                if m.Attributes = null then
                    m.Attributes
                else
                    mapAttrbs m.Attributes
            )
            :> Expression
        | :? MapDisplayExpr as m -> MapDisplayExpr(m.tok, m.Finite, mapExpPairs m.Elements) :> Expression
        | :? MatchExpr as m ->
            MatchExpr(m.tok, prefixFullDafnyNames m.Source, mapCases m.Cases, m.UsesOptionalBraces) :> Expression
        | :? MultiSetDisplayExpr as m -> MultiSetDisplayExpr(m.tok, mapFullDafnyNames m.Elements) :> Expression
        | :? NegationExpression as n -> NegationExpression(n.tok, prefixFullDafnyNames n.E) :> Expression
        | :? NestedMatchExpr as n ->
            NestedMatchExpr(n.tok, prefixFullDafnyNames n.Source, nestedMapCases n.Cases, n.UsesOptionalBraces)
            :> Expression
        | :? ParensExpression as p -> ParensExpression(p.tok, prefixFullDafnyNames p.E) :> Expression
        | :? SeqConstructionExpr as e ->
            SeqConstructionExpr(
                e.tok,
                e.ExplicitElementType,
                prefixFullDafnyNames e.N,
                prefixFullDafnyNames e.Initializer
            )
            :> Expression
        | :? SeqDisplayExpr as s -> SeqDisplayExpr(s.tok, mapFullDafnyNames s.Elements) :> Expression
        | :? SeqSelectExpr as e ->
            SeqSelectExpr(
                e.tok,
                e.SelectOne,
                prefixFullDafnyNames e.Seq,
                prefixFullDafnyNames e.E0,
                prefixFullDafnyNames e.E1
            )
            :> Expression
        | :? SeqUpdateExpr as s ->
            SeqUpdateExpr(s.tok, prefixFullDafnyNames s.Seq, prefixFullDafnyNames s.Index, prefixFullDafnyNames s.Value)
            :> Expression
        | :? SetComprehension as s ->
            SetComprehension(
                s.tok,
                s.Finite,
                s.BoundVars,
                prefixFullDafnyNames s.Range,
                prefixFullDafnyNames s.Term,
                if s.Attributes = null then
                    s.Attributes
                else
                    mapAttrbs s.Attributes
            )
            :> Expression
        | :? SetDisplayExpr as s -> SetDisplayExpr(s.tok, s.Finite, mapFullDafnyNames s.Elements) :> Expression
        // TODO: expressions in statements also need to be prefixed, not implemented for the time being
        | :? StmtExpr as s -> StmtExpr(s.tok, s.S, prefixFullDafnyNames s.E) :> Expression
        // TODO: test remaining for TernaryExpr: PrefixEqOp, PrefixNeqOp are ternary operators
        | :? TernaryExpr as e ->
            TernaryExpr(e.tok, e.Op, prefixFullDafnyNames e.E0, prefixFullDafnyNames e.E1, prefixFullDafnyNames e.E2)
            :> Expression
        | :? TypeTestExpr as e -> TypeTestExpr(e.tok, prefixFullDafnyNames e.E, e.ToType) :> Expression
        | :? UnaryOpExpr as u -> UnaryOpExpr(u.tok, u.Op, prefixFullDafnyNames u.E) :> Expression
        // NOT REQUIRED TO BE PREFIXED
        | :? AutoGhostIdentifierExpr
        | :? CharLiteralExpr
        | :? IdentifierExpr
        | :? ImplicitThisExpr
        | :? ImplicitThisExpr_ConstructorCall
        | :? LiteralExpr
        | :? NameSegment
        | :? StaticReceiverExpr
        | :? StringLiteralExpr
        | :? ThisExpr
        | :? WildcardExpr -> e
        | _ -> failwith "Unsupported"

    and mapFullDafnyNames (es: List<Expression>) =
        List.map prefixFullDafnyNames (fromList es)
        |> List<Expression>

    and mapExpPair (e: ExpressionPair) =
        ExpressionPair(prefixFullDafnyNames e.A, prefixFullDafnyNames e.B)

    and mapExpPairs (es: List<ExpressionPair>) =
        List.map mapExpPair (fromList es)
        |> List<ExpressionPair>

    and mapActualBinding (a: ActualBinding) =
        ActualBinding(a.FormalParameterName, prefixFullDafnyNames a.Actual)

    and mapActualBindings (a: List<ActualBinding>) =
        List.map mapActualBinding (fromList a)
        |> List<ActualBinding>

    and mapAttrbs (a: Attributes) : Attributes =
        Attributes(a.Name, mapFullDafnyNames a.Args, mapAttrbs a.Prev)

    and mapFrame (f: FrameExpression) =
        FrameExpression(f.tok, prefixFullDafnyNames f.E, f.FieldName)

    and mapFrames (fs: List<FrameExpression>) =
        List.map mapFrame (fromList fs)
        |> List<FrameExpression>

    and mapCase (c: MatchCaseExpr) =
        MatchCaseExpr(c.tok, c.Ctor, c.Arguments, prefixFullDafnyNames c.Body)

    and mapCases (cs: List<MatchCaseExpr>) =
        List.map mapCase (fromList cs)
        |> List<MatchCaseExpr>

    and nestedMapCase (n: NestedMatchCaseExpr) =
        NestedMatchCaseExpr(n.Tok, n.Pat, prefixFullDafnyNames n.Body, mapAttrbs n.Attributes)

    and nestedMapCases (ns: List<NestedMatchCaseExpr>) =
        List.map nestedMapCase (fromList ns)
        |> List<NestedMatchCaseExpr>
