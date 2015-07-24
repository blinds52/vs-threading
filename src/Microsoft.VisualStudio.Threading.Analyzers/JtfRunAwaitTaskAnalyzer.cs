﻿namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;

    /// <summary>
    /// Detects await task inside JoinableTaskFactory.Run.
    /// </summary>
    /// <remarks>
    /// [Background] Async void methods have different error-handling semantics.
    /// When an exception is thrown out of an async Task or async <see cref="Task{T}"/> method/lambda,
    /// that exception is captured and placed on the Task object. With async void methods,
    /// there is no Task object, so any exceptions thrown out of an async void method will
    /// be raised directly on the SynchronizationContext that was active when the async
    /// void method started, and it would crash the process.
    /// Refer to Stephen's article https://msdn.microsoft.com/en-us/magazine/jj991977.aspx for more info.
    ///
    /// i.e.
    /// <![CDATA[
    ///   async void MyMethod() /* This analyzer will report warning on this method declaration. */
    ///   {
    ///   }
    /// ]]>
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class JtfRunAwaitTaskAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get
            {
                return ImmutableArray.Create(Rules.AvoidAwaitTaskInsideJoinableTaskFactoryRun);
            }
        }

        public override void Initialize(AnalysisContext context)
        {
            context.RegisterSyntaxNodeAction(this.AnalyzeNode, SyntaxKind.AwaitExpression);
        }

        private void AnalyzeNode(SyntaxNodeAnalysisContext context)
        {
            AwaitExpressionSyntax awaitExpressionSyntax = (AwaitExpressionSyntax)context.Node;
            IdentifierNameSyntax identifierNameSyntaxAwaitingOn = awaitExpressionSyntax.Expression as IdentifierNameSyntax;
            if (identifierNameSyntaxAwaitingOn == null)
            {
                return;
            }

            SyntaxNode currentNode = identifierNameSyntaxAwaitingOn;

            // Step 1: Find the async delegate or lambda expression that matches the await
            SyntaxNode delegateOrLambdaNode = this.FindAsyncDelegateOrLambdaExpressiomMatchingAwait(awaitExpressionSyntax);
            if (delegateOrLambdaNode == null)
            {
                return;
            }

            // Step 2: Check whether it is called by Jtf.Run
            InvocationExpressionSyntax invocationExpressionSyntax = this.FindInvocationOfDelegateOrLambdaExpression(delegateOrLambdaNode);
            if (invocationExpressionSyntax == null || !this.IsInvocationExpressionACallToJtfRun(context, invocationExpressionSyntax))
            {
                return;
            }

            // Step 3: Is the symbol we are waiting on a System.Threading.Tasks.Task
            SymbolInfo symbolAwaitingOn = context.SemanticModel.GetSymbolInfo(identifierNameSyntaxAwaitingOn);
            ILocalSymbol localSymbol = symbolAwaitingOn.Symbol as ILocalSymbol;
            if (localSymbol == null || !localSymbol.Type.ToString().StartsWith("System.Threading.Tasks.Task"))
            {
                return;
            }

            // Step 4: Report warning if the task was not initialized within the current delegate or lambda expression
            BlockSyntax delegateBlock = this.GetBlockOfDelegateOrLambdaExpression(delegateOrLambdaNode);

            // Run data flow analysis to understand where the task was defined
            DataFlowAnalysis dataFlowAnalysis;
            // When possible (await is direct child of the block), execute data flow analysis by passing first and last statement to capture only what happens before the await
            // Check if the await is direct child of the code block (first parent is ExpressionStantement, second parent is the block itself)
            if (awaitExpressionSyntax.Parent.Parent.Equals(delegateBlock))
            {
                dataFlowAnalysis = context.SemanticModel.AnalyzeDataFlow(delegateBlock.ChildNodes().FirstOrDefault(), awaitExpressionSyntax.Parent);
            }
            else
            {
                // Otherwise analyze the data flow for the entire block. One caveat: it doesn't distinguish if the initalization happens after the await.
                dataFlowAnalysis = context.SemanticModel.AnalyzeDataFlow(delegateBlock);
            }

            if (!dataFlowAnalysis.WrittenInside.Contains(symbolAwaitingOn.Symbol))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rules.AvoidAwaitTaskInsideJoinableTaskFactoryRun, awaitExpressionSyntax.Expression.GetLocation()));
            }
        }

        /// <summary>
        /// Finds the async delegate or lambda expression that matches the await by walking up the syntax tree until we encounter an async delegate or lambda expression.
        /// </summary>
        /// <param name="awaitExpressionSyntax">The await expression syntax.</param>
        /// <returns>Node representing the delegate or lambda expression if found. Null if not found.</returns>
        private SyntaxNode FindAsyncDelegateOrLambdaExpressiomMatchingAwait(AwaitExpressionSyntax awaitExpressionSyntax)
        {
            SyntaxNode currentNode = awaitExpressionSyntax;

            while (currentNode != null && !(currentNode is MethodDeclarationSyntax))
            {
                AnonymousMethodExpressionSyntax anonymousMethod = currentNode as AnonymousMethodExpressionSyntax;
                if (anonymousMethod != null && anonymousMethod.AsyncKeyword != null)
                {
                    return currentNode;
                }

                ParenthesizedLambdaExpressionSyntax lambdaExpression = currentNode as ParenthesizedLambdaExpressionSyntax;
                if (lambdaExpression != null && lambdaExpression.AsyncKeyword != null)
                {
                    return currentNode;
                }

                // Advance to the next parent
                currentNode = currentNode.Parent;
            }

            return null;
        }

        /// <summary>
        /// Helper method to get the code Block of a delegate or lambda expression.
        /// </summary>
        /// <param name="delegateOrLambdaExpression">The delegate or lambda expression.</param>
        /// <returns>The code block.</returns>
        private BlockSyntax GetBlockOfDelegateOrLambdaExpression(SyntaxNode delegateOrLambdaExpression)
        {
            AnonymousMethodExpressionSyntax anonymousMethod = delegateOrLambdaExpression as AnonymousMethodExpressionSyntax;
            if (anonymousMethod != null)
            {
                return anonymousMethod.Block;
            }

            ParenthesizedLambdaExpressionSyntax lambdaExpression = delegateOrLambdaExpression as ParenthesizedLambdaExpressionSyntax;
            if (lambdaExpression != null)
            {
                return lambdaExpression.Body as BlockSyntax;
            }

            throw new ArgumentException("Must be of typ AnonymousMethodExpressionSyntax or ParenthesizedLambdaExpressionSyntax", nameof(delegateOrLambdaExpression));
        }

        /// <summary>
        /// Walks up the syntax tree to find out where the specified delegate or lambda expression is being invoked.
        /// </summary>
        /// <param name="delegateOrLambdaExpression">Node representing a delegate or lambda expression.</param>
        /// <returns>The invocation expression. Null if not found.</returns>
        private InvocationExpressionSyntax FindInvocationOfDelegateOrLambdaExpression(SyntaxNode delegateOrLambdaExpression)
        {
            SyntaxNode currentNode = delegateOrLambdaExpression;

            while (currentNode != null && !(currentNode is MethodDeclarationSyntax))
            {
                InvocationExpressionSyntax invocationExpressionSyntax = currentNode as InvocationExpressionSyntax;
                if (invocationExpressionSyntax != null)
                {
                    return invocationExpressionSyntax;
                }

                // Advance to the next parent
                currentNode = currentNode.Parent;
            }

            return null;
        }

        /// <summary>
        /// Checks whether the specified invocation is a call to JoinableTaskFactory.Run or RunAsync
        /// </summary>
        /// <param name="context">The analysis context.</param>
        /// <param name="invocationExpressionSyntax">The invocation to check for.</param>
        /// <returns>True if the specified invocation is a call to JoinableTaskFactory.Run or RunAsyn</returns>
        private bool IsInvocationExpressionACallToJtfRun(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocationExpressionSyntax)
        {
            MemberAccessExpressionSyntax memberAccessExpressionSyntax = invocationExpressionSyntax.Expression as MemberAccessExpressionSyntax;
            if (memberAccessExpressionSyntax != null)
            {
                // Check if we encountered a call to Run and had already encountered a delegate (so Run is a parent of the delegate)
                string methodName = memberAccessExpressionSyntax.Name.Identifier.Text;
                if (methodName == "Run" || methodName == "RunAsync")
                {
                    // Check whether the Run method belongs to JTF
                    IMethodSymbol methodSymbol = context.SemanticModel.GetSymbolInfo(memberAccessExpressionSyntax).Symbol as IMethodSymbol;
                    if (methodSymbol != null && methodSymbol.ToString().StartsWith("Microsoft.VisualStudio.Threading.JoinableTaskFactory"))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}