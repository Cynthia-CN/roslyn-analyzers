﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Immutable;
using System.Linq;
using Analyzer.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Semantics;

namespace System.Runtime.Analyzers
{
    /// <summary>Base type for an analyzer that looks for empty array allocations and recommends their replacement.</summary>
    public abstract class AvoidZeroLengthArrayAllocationsAnalyzer : DiagnosticAnalyzer
    {
        internal const string RuleId = "CA1825";

        /// <summary>The name of the array type.</summary>
        internal const string ArrayTypeName = "System.Array"; // using instead of GetSpecialType to make more testable

        /// <summary>The name of the Empty method on System.Array.</summary>
        internal const string ArrayEmptyMethodName = "Empty";

        private static readonly LocalizableString s_localizableTitle = new LocalizableResourceString(nameof(SystemRuntimeAnalyzersResources.AvoidZeroLengthArrayAllocationsTitle), SystemRuntimeAnalyzersResources.ResourceManager, typeof(SystemRuntimeAnalyzersResources));
        private static readonly LocalizableString s_localizableMessage = new LocalizableResourceString(nameof(SystemRuntimeAnalyzersResources.AvoidZeroLengthArrayAllocationsMessage), SystemRuntimeAnalyzersResources.ResourceManager, typeof(SystemRuntimeAnalyzersResources));

        /// <summary>The diagnostic descriptor used when Array.Empty should be used instead of a new array allocation.</summary>
        internal static readonly DiagnosticDescriptor UseArrayEmptyDescriptor = new DiagnosticDescriptor(
            RuleId,
            s_localizableTitle,
            s_localizableMessage,
            DiagnosticCategory.Performance,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        /// <summary>Gets the set of supported diagnostic descriptors from this analyzer.</summary>
        public sealed override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get { return ImmutableArray.Create(UseArrayEmptyDescriptor); }
        }

        public sealed override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

            // When compilation begins, check whether Array.Empty<T> is available.
            // Only if it is, register the syntax node action provided by the derived implementations.
            context.RegisterCompilationStartAction(ctx =>
            {
                INamedTypeSymbol typeSymbol = ctx.Compilation.GetTypeByMetadataName(ArrayTypeName);
                if (typeSymbol != null && typeSymbol.DeclaredAccessibility == Accessibility.Public)
                {
                    IMethodSymbol methodSymbol = typeSymbol.GetMembers(ArrayEmptyMethodName).FirstOrDefault() as IMethodSymbol;
                    if (methodSymbol != null && methodSymbol.DeclaredAccessibility == Accessibility.Public &&
                        methodSymbol.IsStatic && methodSymbol.Arity == 1 && methodSymbol.Parameters.Length == 0)
                    {
                        ctx.RegisterOperationAction(AnalyzeOperation, OperationKind.ArrayCreationExpression);
                    }
                }
            });
        }

        private void AnalyzeOperation(OperationAnalysisContext context)
        {
            AnalyzeOperation(context, IsAttributeSyntax);
        }

        private static void AnalyzeOperation(OperationAnalysisContext context, Func<SyntaxNode, bool> isAttributeSytnax)
        {
            IArrayCreationExpression arrayCreationExpression = context.Operation as IArrayCreationExpression;

            // We can't replace array allocations in attributes, as they're persisted to metadata
            // TODO: Once we have operation walkers, we can replace this syntactic check with an operation-based check.
            if (arrayCreationExpression.Syntax.Ancestors().Any(isAttributeSytnax))
            {
                return;
            }

            if (arrayCreationExpression.DimensionSizes.Length == 1)
            {
                IOperation dimensionSize = arrayCreationExpression.DimensionSizes[0];

                if (dimensionSize.HasConstantValue(0))
                {
                    // Workaround for https://github.com/dotnet/roslyn/issues/10214
                    // Bail out for compiler generated param array creation.
                    if (IsCompilerGeneratedParamsArray(arrayCreationExpression, context))
                    {
                        return;
                    }

                    // pointers can't be used as generic arguments
                    if (arrayCreationExpression.ElementType.TypeKind != TypeKind.Pointer)
                    {
                        context.ReportDiagnostic(context.Operation.Syntax.CreateDiagnostic(UseArrayEmptyDescriptor));
                    }
                }
            }
        }

        private static bool IsCompilerGeneratedParamsArray(IArrayCreationExpression arrayCreationExpression, OperationAnalysisContext context)
        {
            var model = context.Compilation.GetSemanticModel(arrayCreationExpression.Syntax.SyntaxTree);

            // Compiler generated array creation seems to just use the syntax from the parent.
            var parent = model.GetOperation(arrayCreationExpression.Syntax, context.CancellationToken) as IInvocationExpression;
            if (parent == null || parent.TargetMethod == null || parent.TargetMethod.Parameters.Length == 0)
            {
                return false;
            }

            if (!parent.TargetMethod.Parameters.Last().IsParams)
            {
                return false;
            }

            var lastArgument = parent.ArgumentsInParameterOrder.LastOrDefault();
            return lastArgument.Syntax == parent.Syntax &&
                lastArgument != null &&
                lastArgument.ArgumentKind == ArgumentKind.ParamArray;
        }

        protected abstract bool IsAttributeSyntax(SyntaxNode node);
    }
}
