﻿// Copyright (c) Edgardo Zoppi.  All Rights Reserved.  Licensed under the MIT License.  See License.txt in the project root for license information.

using Backend.Model;
using Backend.Transformations;
using Backend.Utils;
using Model.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Model.ThreeAddressCode.Instructions;
using Model.ThreeAddressCode.Values;

namespace Backend.Analyses
{
	// Interprocedural May Points-To Analysis
	public class InterPointsToAnalysis
	{
		public const string CFG_INFO = "CFG";
		public const string PTG_INFO = "PTG";
		public const string PTA_INFO = "PTA";
		public const string INPUT_PTG_INFO = "INPUT_PTG";
		public const string OUTPUT_PTG_INFO = "OUTPUT_PTG";

		private CallGraph callGraph;
		private ProgramAnalysisInfo methodsInfo;
		//private Stack<IMethodReference> callStack;

		public InterPointsToAnalysis(ProgramAnalysisInfo methodsInfo)
		{
			this.methodsInfo = methodsInfo;
			this.callGraph = new CallGraph();
			//this.callStack = new Stack<IMethodReference>();
			this.OnReachableMethodFound = DefaultReachableMethodFound;
			this.OnUnknownMethodFound = DefaultUnknownMethodFound;
			this.ProcessUnknownMethod = DefaultProcessUnknownMethod;
		}

		public Func<MethodDefinition, ControlFlowGraph> OnReachableMethodFound;
		public Func<IMethodReference, bool> OnUnknownMethodFound;
		public Func<IMethodReference, IMethodReference, MethodCallInstruction, UniqueIDGenerator, PointsToGraph, PointsToGraph> ProcessUnknownMethod;

		public CallGraph Analyze(MethodDefinition method)
		{
			callGraph.Add(method);
			//callStack.Push(method);

			var methodInfo = methodsInfo.GetOrAdd(method);
			var cfg = OnReachableMethodFound(method);

			// TODO: Don't create unknown nodes when doing the inter PT analysis
			var pta = new PointsToAnalysis(cfg, method);
			pta.ProcessMethodCall = ProcessMethodCall;

			methodInfo.Add(PTA_INFO, pta);
			methodInfo.Add(PTG_INFO, pta.Result);

			var result = pta.Analyze();
			
			var ptg = result[ControlFlowGraph.ExitNodeId].Output;
			methodInfo.Set(OUTPUT_PTG_INFO, ptg);

			//callStack.Pop();
			return callGraph;
		}

		protected PointsToGraph ProcessMethodCall(IMethodReference caller, MethodCallInstruction methodCall, UniqueIDGenerator nodeIdGenerator, PointsToGraph input)
		{
			PointsToGraph output = null;
			var possibleCallees = ResolvePossibleCallees(methodCall, input);

			{
				// Call graph construction
				if (!callGraph.ContainsInvocation(caller, methodCall.Label))
				{
					callGraph.Add(caller, methodCall.Label, methodCall.Method);
				}

				callGraph.Add(caller, methodCall.Label, possibleCallees);
			}

			foreach (var callee in possibleCallees)
			{
				var method = callee.ResolvedMethod;
				var isUnknownMethod = method == null || method.IsExternal;
				var processCallee = !isUnknownMethod || OnUnknownMethodFound(callee);
				
				if (processCallee)
				{
					//callStack.Push(callee);					

					IList<IVariable> parameters;

					if (isUnknownMethod)
					{
						parameters = new List<IVariable>();

						if (!callee.IsStatic)
						{
							var parameter = new LocalVariable("this", true) { Type = callee.ContainingType };

							parameters.Add(parameter);
						}

						foreach (var p in callee.Parameters)
						{
							var name = string.Format("p{0}", p.Index + 1);
							var parameter = new LocalVariable(name, true) { Type = p.Type };

							parameters.Add(parameter);
						}
					}
					else
					{
						parameters = method.Body.Parameters;
					}

					var ptg = input.Clone();
					var binding = GetCallerCalleeBinding(methodCall.Arguments, parameters);
					var previousFrame = ptg.NewFrame(binding);

					//// Garbage collect unreachable nodes.
					//// They are nodes that the callee cannot access but the caller can.
					//// I believe by doing this we can reach the fixpoint faster, but not sure.
					//// [Important] This doesn't work because we are removing
					//// nodes and edges that cannot be restored later!!
					//ptg.CollectGarbage();

					PointsToGraph oldInput;
					var methodInfo = methodsInfo.GetOrAdd(callee);
					var hasOldInput = methodInfo.TryGet(INPUT_PTG_INFO, out oldInput);
					var inputChanged = true;

					if (hasOldInput)
					{
						inputChanged = !ptg.GraphEquals(oldInput);

						if (inputChanged)
						{
							ptg.Union(oldInput);
							// Even when the graphs were different,
							// it could be the case that one (ptg)
							// is a subgraph of the other (oldInput)
							// so the the result of the union of both
							// graphs is exactly the same oldInput graph.
							inputChanged = !ptg.GraphEquals(oldInput);
						}
					}

					if (inputChanged)
					{
						methodInfo.Set(INPUT_PTG_INFO, ptg);

						if (isUnknownMethod)
						{
							ptg = ProcessUnknownMethod(callee, caller, methodCall, nodeIdGenerator, ptg);
						}
						else
						{
							PointsToAnalysis pta;
							var ok = methodInfo.TryGet(PTA_INFO, out pta);

							if (!ok)
							{
								var cfg = OnReachableMethodFound(method);

								// TODO: Don't create unknown nodes when doing the inter PT analysis
								pta = new PointsToAnalysis(cfg, method, nodeIdGenerator);
								pta.ProcessMethodCall = ProcessMethodCall;

								methodInfo.Add(PTA_INFO, pta);
							}

							methodInfo.Set(PTG_INFO, pta.Result);

							var result = pta.Analyze(ptg);

							ptg = result[ControlFlowGraph.ExitNodeId].Output;
						}
					}
					else
					{
						var result = methodInfo.Get<DataFlowAnalysisResult<PointsToGraph>[]>(PTG_INFO);
						ptg = result[ControlFlowGraph.ExitNodeId].Output;
					}

					methodInfo.Set(OUTPUT_PTG_INFO, ptg);

					ptg = ptg.Clone();
					binding = GetCalleeCallerBinding(methodCall.Result, ptg.ResultVariable);
					ptg.RestoreFrame(previousFrame, binding);

					//// Garbage collect unreachable nodes.
					//// They are nodes created by the callee that do not escape to the caller.
					//// I believe by doing this we can reach the fixpoint faster, but not sure.
					//ptg.CollectGarbage();

					//callStack.Pop();

					if (ptg != null)
					{
						if (output == null)
						{
							output = ptg;
						}
						else
						{
							output.Union(ptg);
						}
					}
				}
			}

			if (output == null)
			{
				output = input;
			}

			return output;
		}

		// binding: callee parameter -> caller argument
		private static IDictionary<IVariable, IVariable> GetCallerCalleeBinding(IList<IVariable> arguments, IList<IVariable> parameters)
		{
			var binding = new Dictionary<IVariable, IVariable>();

#if DEBUG
			if (arguments.Count != parameters.Count)
				throw new Exception("Different ammount of parameters and arguments");
#endif

			for (var i = 0; i < arguments.Count; ++i)
			{
				var argument = arguments[i];
				var parameter = parameters[i];

				binding.Add(parameter, argument);
			}

			return binding;
		}

		// binding: callee variable -> caller variable
		private static IDictionary<IVariable, IVariable> GetCalleeCallerBinding(IVariable callerResult, IVariable calleeResult)
		{
			var binding = new Dictionary<IVariable, IVariable>();

			if (callerResult != null)
			{
				binding.Add(calleeResult, callerResult);
			}

			return binding;
		}

		private static IEnumerable<IMethodReference> ResolvePossibleCallees(MethodCallInstruction methodCall, PointsToGraph ptg)
		{
			var result = new HashSet<IMethodReference>();
			var staticCallee = methodCall.Method;

			if (!staticCallee.IsStatic &&
				methodCall.Operation == MethodCallOperation.Virtual)
			{
				var receiver = methodCall.Arguments.First();
				var targets = ptg.GetTargets(receiver);

				foreach (var target in targets)
				{
					var receiverType = target.Type as IBasicType;
					var callee = Helper.FindMethodImplementation(receiverType, staticCallee);

					result.Add(callee);
				}
			}
			else
			{
				result.Add(staticCallee);
			}

			return result;
		}

		protected virtual ControlFlowGraph DefaultReachableMethodFound(MethodDefinition method)
		{
			ControlFlowGraph cfg;
			var methodInfo = methodsInfo.GetOrAdd(method);
			var ok = methodInfo.TryGet(CFG_INFO, out cfg);

			if (!ok)
			{
				if (method.Body.Kind == MethodBodyKind.Bytecode)
				{
					var disassembler = new Disassembler(method);
					var body = disassembler.Execute();

					method.Body = body;
				}

				var cfa = new ControlFlowAnalysis(method.Body);
				cfg = cfa.GenerateNormalControlFlow();
				//cfg = cfa.GenerateExceptionalControlFlow();

				var splitter = new WebAnalysis(cfg);
				splitter.Analyze();
				splitter.Transform();

				method.Body.UpdateVariables();

				var typeAnalysis = new TypeInferenceAnalysis(cfg);
				typeAnalysis.Analyze();

				methodInfo.Add(CFG_INFO, cfg);
			}

			return cfg;
		}

		protected virtual bool DefaultUnknownMethodFound(IMethodReference method)
		{
			return false;
		}

		protected virtual PointsToGraph DefaultProcessUnknownMethod(IMethodReference callee, IMethodReference caller, MethodCallInstruction methodCall, UniqueIDGenerator nodeIdGenerator, PointsToGraph input)
		{
			return input;
		}
	}
}
