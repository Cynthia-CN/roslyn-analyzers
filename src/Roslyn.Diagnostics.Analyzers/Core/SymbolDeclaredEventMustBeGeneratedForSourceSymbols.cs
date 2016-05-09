﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Analyzer.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Roslyn.Diagnostics.Analyzers
{
    public abstract class SymbolDeclaredEventAnalyzer<TSyntaxKind> : DiagnosticAnalyzer
        where TSyntaxKind : struct
    {
        private static readonly LocalizableString s_localizableTitle = new LocalizableResourceString(nameof(RoslynDiagnosticsAnalyzersResources.SymbolDeclaredEventMustBeGeneratedForSourceSymbolsTitle), RoslynDiagnosticsAnalyzersResources.ResourceManager, typeof(RoslynDiagnosticsAnalyzersResources));
        private static readonly LocalizableString s_localizableMessage = new LocalizableResourceString(nameof(RoslynDiagnosticsAnalyzersResources.SymbolDeclaredEventMustBeGeneratedForSourceSymbolsMessage), RoslynDiagnosticsAnalyzersResources.ResourceManager, typeof(RoslynDiagnosticsAnalyzersResources));
        private static readonly LocalizableString s_localizableDescription = new LocalizableResourceString(nameof(RoslynDiagnosticsAnalyzersResources.SymbolDeclaredEventMustBeGeneratedForSourceSymbolsDescription), RoslynDiagnosticsAnalyzersResources.ResourceManager, typeof(RoslynDiagnosticsAnalyzersResources));
        private static readonly string s_fullNameOfSymbol = typeof(ISymbol).FullName;

        internal static readonly DiagnosticDescriptor SymbolDeclaredEventRule = new DiagnosticDescriptor(
            RoslynDiagnosticIds.SymbolDeclaredEventRuleId,
            s_localizableTitle,
            s_localizableMessage,
            "Reliability",
            DiagnosticSeverity.Error,
            isEnabledByDefault: false,
            description: s_localizableDescription,
            customTags: WellKnownDiagnosticTags.Telemetry);

        public sealed override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get
            {
                return ImmutableArray.Create(SymbolDeclaredEventRule);
            }
        }

        public sealed override void Initialize(AnalysisContext context)
        {
            // TODO: Make the analyzer thread-safe
            //context.EnableConcurrentExecution();

            // We need to analyze generated code, but don't intend to report diagnostics on generated code.
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            context.RegisterCompilationStartAction(compilationContext =>
            {
                INamedTypeSymbol symbolType = compilationContext.Compilation.GetTypeByMetadataName(s_fullNameOfSymbol);
                if (symbolType != null)
                {
                    CompilationAnalyzer compilationAnalyzer = GetCompilationAnalyzer(compilationContext.Compilation, symbolType);
                    if (compilationAnalyzer != null)
                    {
                        compilationContext.RegisterSyntaxNodeAction(compilationAnalyzer.AnalyzeNode, InvocationExpressionSyntaxKind);
                        compilationContext.RegisterSymbolAction(compilationAnalyzer.AnalyzeNamedType, SymbolKind.NamedType);
                        compilationContext.RegisterCompilationEndAction(compilationAnalyzer.AnalyzeCompilationEnd);
                    }
                }
            });
        }

        protected abstract TSyntaxKind InvocationExpressionSyntaxKind { get; }
        protected abstract CompilationAnalyzer GetCompilationAnalyzer(Compilation compilation, INamedTypeSymbol symbolType);

        protected abstract class CompilationAnalyzer
        {
            private readonly INamedTypeSymbol _symbolType;
            private readonly INamedTypeSymbol _compilationType;
            private readonly HashSet<INamedTypeSymbol> _sourceSymbolsToCheck = new HashSet<INamedTypeSymbol>();
            private readonly HashSet<INamedTypeSymbol> _typesWithSymbolDeclaredEventInvoked = new HashSet<INamedTypeSymbol>();

            private const string SymbolDeclaredEventName = "SymbolDeclaredEvent";

            protected CompilationAnalyzer(INamedTypeSymbol symbolType, INamedTypeSymbol compilationType)
            {
                _symbolType = symbolType;
                _compilationType = compilationType;

                // If the below assert fire then probably the definition of "SymbolDeclaredEvent" has changed and we need to fix this analyzer.
                ISymbol symbolDeclaredEvent = compilationType.GetMembers(SymbolDeclaredEventName).Single();
                Debug.Assert(symbolDeclaredEvent.GetParameters().Count() == 1);
            }

            protected abstract SyntaxNode GetFirstArgumentOfInvocation(SyntaxNode invocation);
            protected abstract HashSet<string> SymbolTypesWithExpectedSymbolDeclaredEvent { get; }

            internal void AnalyzeNode(SyntaxNodeAnalysisContext context)
            {
                ISymbol invocationSymbol = context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol;
                if (invocationSymbol != null &&
                    invocationSymbol.Kind == SymbolKind.Method)
                {
                    AnalyzeMethodInvocation((IMethodSymbol)invocationSymbol, context);
                }
            }

            internal virtual void AnalyzeMethodInvocation(IMethodSymbol invocationSymbol, SyntaxNodeAnalysisContext context)
            {
                if (invocationSymbol.Name.Equals(SymbolDeclaredEventName) &&
                    _compilationType.Equals(invocationSymbol.ContainingType))
                {
                    SyntaxNode argument = GetFirstArgumentOfInvocation(context.Node);
                    AnalyzeSymbolDeclaredEventInvocation(argument, context);
                }
            }

            protected bool AnalyzeSymbolDeclaredEventInvocation(SyntaxNode argument, SyntaxNodeAnalysisContext context)
            {
                if (argument != null)
                {
                    ITypeSymbol argumentType = context.SemanticModel.GetTypeInfo(argument, context.CancellationToken).Type;
                    return AnalyzeSymbolDeclaredEventInvocation(argumentType);
                }

                return false;
            }

            private bool AnalyzeSymbolDeclaredEventInvocation(ISymbol type)
            {
                if (type != null &&
                    type.Kind == SymbolKind.NamedType &&
                    !type.Name.Equals("Symbol"))
                {
                    var namedType = (INamedTypeSymbol)type;
                    if (namedType.AllInterfaces.Contains(_symbolType))
                    {
                        _typesWithSymbolDeclaredEventInvoked.Add(namedType);
                        return true;
                    }
                }

                return false;
            }

            internal void AnalyzeNamedType(SymbolAnalysisContext context)
            {
                var namedType = (INamedTypeSymbol)context.Symbol;
                if (!namedType.IsAbstract &&
                    namedType.Name.StartsWith("Source", StringComparison.Ordinal) &&
                    !namedType.Name.Contains("Backing") &&
                    namedType.AllInterfaces.Contains(_symbolType) &&
                    namedType.GetBaseTypesAndThis().Any(b => SymbolTypesWithExpectedSymbolDeclaredEvent.Contains(b.Name, StringComparer.Ordinal)))
                {
                    _sourceSymbolsToCheck.Add(namedType);
                }
            }

            internal void AnalyzeCompilationEnd(CompilationAnalysisContext context)
            {
                foreach (INamedTypeSymbol sourceSymbol in _sourceSymbolsToCheck)
                {
                    var found = false;
                    foreach (INamedTypeSymbol type in sourceSymbol.GetBaseTypesAndThis())
                    {
                        if (_typesWithSymbolDeclaredEventInvoked.Contains(type))
                        {
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        Diagnostic diagnostic = Diagnostic.Create(SymbolDeclaredEventRule, sourceSymbol.Locations[0], sourceSymbol.Name, _compilationType.Name, SymbolDeclaredEventName);
                        context.ReportDiagnostic(diagnostic);
                    }
                }
            }
        }
    }
}
