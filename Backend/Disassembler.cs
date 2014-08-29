﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Cci;
using Backend.ThreeAddressCode;

namespace Backend
{
	public class Disassembler
	{
		#region class OperandStack

		class OperandStack
		{
			private TemporalVariable[] stack;
			private ushort top;

			public OperandStack(ushort capacity)
			{
				stack = new TemporalVariable[capacity];

				for (var i = 0u; i < capacity; ++i)
					stack[i] = new TemporalVariable(i);
			}

			public IEnumerable<TemporalVariable> Variables
			{
				get { return this.stack; }
			}

			public int Capacity
			{
				get { return stack.Length; }
			}

			public ushort Size
			{
				get { return top; }
				set
				{
					if (value < 0 || value > stack.Length) throw new InvalidOperationException();
					top = value;
				}
			}

			public void Clear()
			{
				top = 0;
			}

			public TemporalVariable Push()
			{
				if (top >= stack.Length) throw new InvalidOperationException();
				return stack[top++];
			}

			public TemporalVariable Pop()
			{
				if (top <= 0) throw new InvalidOperationException();
				return stack[--top];
			}

			public TemporalVariable Top()
			{
				if (top <= 0) throw new InvalidOperationException();
				return stack[top - 1];
			}
		}

		#endregion

		#region Try Catch Finally information

		enum ContextKind
		{
			None,
			Try,
			Catch,
			Finally
		}

		class TryInformation
		{
			public uint BeginOffset { get; set; }
			public uint EndOffset { get; set; }
			public IDictionary<uint, CatchInformation> ExceptionHandlers { get; private set; }
			public FinallyInformation Finally { get; set; }

			public TryInformation(uint begin, uint end)
			{
				this.BeginOffset = begin;
				this.EndOffset = end;
				this.ExceptionHandlers = new Dictionary<uint, CatchInformation>();
			}
		}

		class CatchInformation
		{
			public ITypeReference ExceptionType { get; set; }
			public uint BeginOffset { get; set; }
			public uint EndOffset { get; set; }

			public CatchInformation(uint begin, uint end, ITypeReference exceptionType)
			{
				this.BeginOffset = begin;
				this.EndOffset = end;
				this.ExceptionType = exceptionType;
			}
		}

		class FinallyInformation
		{
			public uint BeginOffset { get; set; }
			public uint EndOffset { get; set; }

			public FinallyInformation(uint begin, uint end)
			{
				this.BeginOffset = begin;
				this.EndOffset = end;
			}
		}

		#endregion

		private enum BasicBlockStatus
		{
			None,
			Pending,
			Processed
		}

		private class BasicBlockInfo
		{
			public uint Offset { get; private set; }
			public bool CanFlowFallThrough { get; set; }
			public ushort StackSizeAtEntry { get; set; }
			public BasicBlockStatus Status { get; set; }
			public IList<Instruction> Instructions { get; private set; }

			public BasicBlockInfo(uint offset)
			{
				this.Offset = offset;
				this.CanFlowFallThrough = true;
				this.Instructions = new List<Instruction>();
			}
		}

		private IMetadataHost host;
		private IMethodDefinition method;
		private ISourceLocationProvider sourceLocationProvider;
		private LocalVariable thisParameter;
		private IDictionary<IParameterDefinition, LocalVariable> parameters;
		private IDictionary<ILocalDefinition, LocalVariable> locals;
		private IDictionary<uint, TryInformation> trys;
		private OperandStack stack;
		private ContextKind contextKind;
		private TryInformation tryInfo;
		private MethodBody body;
		private IDictionary<uint, BasicBlockInfo> basicBlocks;
		private Stack<uint> pendingBasicBlocks;

		public Disassembler(IMetadataHost host, IMethodDefinition methodDefinition, ISourceLocationProvider sourceLocationProvider)
		{
			this.host = host;
			this.method = methodDefinition;
			this.sourceLocationProvider = sourceLocationProvider;
			this.parameters = new Dictionary<IParameterDefinition, LocalVariable>();
			this.locals = new Dictionary<ILocalDefinition, LocalVariable>();
			this.trys = new Dictionary<uint, TryInformation>();
			this.stack = new OperandStack(method.Body.MaxStack);

			this.basicBlocks = new SortedDictionary<uint, BasicBlockInfo>();
			this.pendingBasicBlocks = new Stack<uint>();

			if (!method.IsStatic)
			{
				this.thisParameter = new LocalVariable("this");
			}

			foreach (var parameter in method.Parameters)
			{
				var p = new LocalVariable(parameter.Name.Value);
				this.parameters.Add(parameter, p);
			}

			foreach (var local in method.Body.LocalVariables)
			{
				var name = this.GetLocalSourceName(local);
				var l = new LocalVariable(name);
				this.locals.Add(local, l);
			}

			foreach (var exinf in method.Body.OperationExceptionInformation)
			{
				TryInformation tryInfo;

				if (this.trys.ContainsKey(exinf.TryStartOffset))
				{
					tryInfo = this.trys[exinf.TryStartOffset];
				}
				else
				{
					tryInfo = new TryInformation(exinf.TryStartOffset, exinf.TryEndOffset);
					this.trys.Add(tryInfo.BeginOffset, tryInfo);
				}

				switch (exinf.HandlerKind)
				{
					case HandlerKind.Finally:
						tryInfo.Finally = new FinallyInformation(exinf.HandlerStartOffset, exinf.HandlerEndOffset);
						break;

					case HandlerKind.Catch:
						var catchInfo = new CatchInformation(exinf.HandlerStartOffset, exinf.HandlerEndOffset, exinf.ExceptionType);
						tryInfo.ExceptionHandlers.Add(catchInfo.BeginOffset, catchInfo);
						break;
				}
			}
		}

		public MethodBody Execute()
		{
			tryInfo = null;
			contextKind = ContextKind.None;
			body = new MethodBody(method);

			this.FillBodyVariables(body);

			if (method.Body.Size == 0) return body;

			this.RecognizeBasicBlocks();
			var operations = this.GetLinkedOperations();

			pendingBasicBlocks.Push(0);

			while (pendingBasicBlocks.Count > 0)
			{
				var offset = pendingBasicBlocks.Pop();
				var basicBlock = basicBlocks[offset];
				var firstOperation = operations[offset];

				basicBlock.Status = BasicBlockStatus.Processed;
				stack.Size = basicBlock.StackSizeAtEntry;
				this.ProcessBasicBlock(basicBlock, firstOperation);
			}

			this.FillBodyInstructions(body);
			return body;
		}

		private IDictionary<uint, LinkedListNode<IOperation>> GetLinkedOperations()
		{
			var linked_operations = new LinkedList<IOperation>(method.Body.Operations);
			var operations = new Dictionary<uint, LinkedListNode<IOperation>>();
			var node = linked_operations.First;

			while (node != null)
			{
				operations.Add(node.Value.Offset, node);
				node = node.Next;
			}

			return operations;
		}

		private void FillBodyVariables(MethodBody body)
		{
			if (thisParameter != null)
			{
				body.Variables.Add(thisParameter);
			}

			body.Variables.UnionWith(parameters.Values);
			body.Variables.UnionWith(locals.Values);
			body.Variables.UnionWith(stack.Variables);
		}

		private void FillBodyInstructions(MethodBody body)
		{
			foreach (var basicBlock in basicBlocks.Values)
			{
				foreach (var instruction in basicBlock.Instructions)
				{
					body.Instructions.Add(instruction);
				}
			}
		}

		private void RecognizeBasicBlocks()
		{
			var nextOperationIsLeader = true;
			var flowFallThrough = true;
			BasicBlockInfo basicBlock;
			uint offset;

			foreach (var op in method.Body.Operations)
			{
				if (nextOperationIsLeader)
				{
					nextOperationIsLeader = false;
					offset = op.Offset;

					if (basicBlocks.ContainsKey(offset))
					{
						basicBlock = basicBlocks[offset];
					}
					else
					{
						basicBlock = new BasicBlockInfo(offset);
						basicBlocks.Add(offset, basicBlock);
					}

					basicBlock.CanFlowFallThrough = flowFallThrough;
					flowFallThrough = true;
				}

				switch (op.OperationCode)
				{
					case OperationCode.Ret:
					case OperationCode.Endfinally:
					case OperationCode.Endfilter:
					case OperationCode.Throw:
					case OperationCode.Rethrow:
						flowFallThrough = false;
						nextOperationIsLeader = true;
						break;

					case OperationCode.Br:
					case OperationCode.Br_S:
					case OperationCode.Leave:
					case OperationCode.Leave_S:
						flowFallThrough = false;
						goto case OperationCode.Beq;

					case OperationCode.Beq:
					case OperationCode.Beq_S:
					case OperationCode.Bne_Un:
					case OperationCode.Bne_Un_S:
					case OperationCode.Bge:
					case OperationCode.Bge_S:
					case OperationCode.Bge_Un:
					case OperationCode.Bge_Un_S:
					case OperationCode.Bgt:
					case OperationCode.Bgt_S:
					case OperationCode.Bgt_Un:
					case OperationCode.Bgt_Un_S:
					case OperationCode.Ble:
					case OperationCode.Ble_S:
					case OperationCode.Ble_Un:
					case OperationCode.Ble_Un_S:
					case OperationCode.Blt:
					case OperationCode.Blt_S:
					case OperationCode.Blt_Un:
					case OperationCode.Blt_Un_S:
					case OperationCode.Brfalse:
					case OperationCode.Brfalse_S:
					case OperationCode.Brtrue:
					case OperationCode.Brtrue_S:
						nextOperationIsLeader = true;
						offset = (uint)op.Value;

						if (!basicBlocks.ContainsKey(offset))
						{
							basicBlock = new BasicBlockInfo(offset);
							basicBlocks.Add(offset, basicBlock);
						}
						break;

					case OperationCode.Switch:
						nextOperationIsLeader = true;
						var targets = op.Value as uint[];

						foreach (var target in targets)
						{
							if (!basicBlocks.ContainsKey(target))
							{
								basicBlock = new BasicBlockInfo(target);
								basicBlocks.Add(target, basicBlock);
							}
						}
						break;
				}
			}
		}

		private void AddToPendingBasicBlocks(uint offset, bool isBranchTarget)
		{
			var basicBlock = basicBlocks[offset];

			if (basicBlock.Status == BasicBlockStatus.None)
			{
				basicBlock.Status = BasicBlockStatus.Pending;
				pendingBasicBlocks.Push(offset);

				if (isBranchTarget || basicBlock.CanFlowFallThrough)
				{
					basicBlock.StackSizeAtEntry = stack.Size;
				}				
			}

			if ((isBranchTarget || basicBlock.CanFlowFallThrough) &&
				basicBlock.StackSizeAtEntry != stack.Size)
			{
				throw new Exception("Basic block with different stack size at entry!");
			}
		}

		private void ProcessBasicBlock(BasicBlockInfo bb, LinkedListNode<IOperation> operation)
		{
			while (operation != null)
			{
				var op = operation.Value;
				operation = operation.Next;

				if (op.Offset > bb.Offset && basicBlocks.ContainsKey(op.Offset))
				{
					this.AddToPendingBasicBlocks(op.Offset, false);
					return;
				}

				this.ProcessExceptionHandling(bb, op.Offset);

				switch (op.OperationCode)
				{
					case OperationCode.Add:
					case OperationCode.Add_Ovf:
					case OperationCode.Add_Ovf_Un:
						this.ProcessBinaryOperation(bb, op, BinaryOperation.Add);
						break;

					case OperationCode.And:
						this.ProcessBinaryOperation(bb, op, BinaryOperation.And);
						break;

					case OperationCode.Ceq:
						this.ProcessBinaryOperation(bb, op, BinaryOperation.Eq);
						break;

					case OperationCode.Cgt:
					case OperationCode.Cgt_Un:
						this.ProcessBinaryOperation(bb, op, BinaryOperation.Gt);
						break;

					case OperationCode.Clt:
					case OperationCode.Clt_Un:
						this.ProcessBinaryOperation(bb, op, BinaryOperation.Lt);
						break;

					case OperationCode.Div:
					case OperationCode.Div_Un:
						this.ProcessBinaryOperation(bb, op, BinaryOperation.Div);
						break;

					case OperationCode.Mul:
					case OperationCode.Mul_Ovf:
					case OperationCode.Mul_Ovf_Un:
						this.ProcessBinaryOperation(bb, op, BinaryOperation.Mul);
						break;

					case OperationCode.Or:
						this.ProcessBinaryOperation(bb, op, BinaryOperation.Or);
						break;

					case OperationCode.Rem:
					case OperationCode.Rem_Un:
						this.ProcessBinaryOperation(bb, op, BinaryOperation.Rem);
						break;

					case OperationCode.Shl:
						this.ProcessBinaryOperation(bb, op, BinaryOperation.Shl);
						break;

					case OperationCode.Shr:
					case OperationCode.Shr_Un:
						this.ProcessBinaryOperation(bb, op, BinaryOperation.Shr);
						break;

					case OperationCode.Sub:
					case OperationCode.Sub_Ovf:
					case OperationCode.Sub_Ovf_Un:
						this.ProcessBinaryOperation(bb, op, BinaryOperation.Sub);
						break;

					case OperationCode.Xor:
						this.ProcessBinaryOperation(bb, op, BinaryOperation.Xor);
						break;

					//case OperationCode.Arglist:
					//    //expression = new RuntimeArgumentHandleExpression();
					//    break;

					case OperationCode.Array_Create_WithLowerBound:
						this.ProcessCreateArray(bb, op, true);
						break;

					case OperationCode.Array_Create:
					case OperationCode.Newarr:
						this.ProcessCreateArray(bb, op, false);
						break;

					case OperationCode.Array_Get:
					case OperationCode.Ldelem:
					case OperationCode.Ldelem_I:
					case OperationCode.Ldelem_I1:
					case OperationCode.Ldelem_I2:
					case OperationCode.Ldelem_I4:
					case OperationCode.Ldelem_I8:
					case OperationCode.Ldelem_R4:
					case OperationCode.Ldelem_R8:
					case OperationCode.Ldelem_U1:
					case OperationCode.Ldelem_U2:
					case OperationCode.Ldelem_U4:
					case OperationCode.Ldelem_Ref:
						this.ProcessLoadArrayElement(bb, op);
						break;

					case OperationCode.Array_Addr:
					case OperationCode.Ldelema:
						this.ProcessLoadArrayElementAddress(bb, op);
						break;

					case OperationCode.Beq:
					case OperationCode.Beq_S:
						this.ProcessBinaryConditionalBranch(bb, op, BranchOperation.Eq);
						break;

					case OperationCode.Bne_Un:
					case OperationCode.Bne_Un_S:
						this.ProcessBinaryConditionalBranch(bb, op, BranchOperation.Neq);
						break;

					case OperationCode.Bge:
					case OperationCode.Bge_S:
					case OperationCode.Bge_Un:
					case OperationCode.Bge_Un_S:
						this.ProcessBinaryConditionalBranch(bb, op, BranchOperation.Ge);
						break;

					case OperationCode.Bgt:
					case OperationCode.Bgt_S:
					case OperationCode.Bgt_Un:
					case OperationCode.Bgt_Un_S:
						this.ProcessBinaryConditionalBranch(bb, op, BranchOperation.Gt);
						break;

					case OperationCode.Ble:
					case OperationCode.Ble_S:
					case OperationCode.Ble_Un:
					case OperationCode.Ble_Un_S:
						this.ProcessBinaryConditionalBranch(bb, op, BranchOperation.Le);
						break;

					case OperationCode.Blt:
					case OperationCode.Blt_S:
					case OperationCode.Blt_Un:
					case OperationCode.Blt_Un_S:
						this.ProcessBinaryConditionalBranch(bb, op, BranchOperation.Lt);
						break;

					case OperationCode.Br:
					case OperationCode.Br_S:
						this.ProcessUnconditionalBranch(bb, op);
						break;

					case OperationCode.Leave:
					case OperationCode.Leave_S:
						this.ProcessLeave(bb, op);
						break;

					case OperationCode.Break:
						this.ProcessBreakpointOperation(bb, op);
						break;

					case OperationCode.Nop:
						this.ProcessEmptyOperation(bb, op);
						break;

					case OperationCode.Brfalse:
					case OperationCode.Brfalse_S:
						this.ProcessUnaryConditionalBranch(bb, op, false);
						break;

					case OperationCode.Brtrue:
					case OperationCode.Brtrue_S:
						this.ProcessUnaryConditionalBranch(bb, op, true);
						break;

					case OperationCode.Call:
					case OperationCode.Callvirt:
						this.ProcessMethodCall(bb, op);
						break;

					case OperationCode.Jmp:
						this.ProcessJumpCall(bb, op);
						break;

					case OperationCode.Calli:
						this.ProcessMethodCallIndirect(bb, op);
						break;

					case OperationCode.Castclass:
					case OperationCode.Isinst:
					case OperationCode.Box:
					case OperationCode.Unbox:
					case OperationCode.Unbox_Any:
						this.ProcessConversion(bb, op);
						break;

					case OperationCode.Conv_I:
					case OperationCode.Conv_Ovf_I:
					case OperationCode.Conv_Ovf_I_Un:
						this.ProcessConversion(bb, op, host.PlatformType.SystemIntPtr);
						break;

					case OperationCode.Conv_I1:
					case OperationCode.Conv_Ovf_I1:
					case OperationCode.Conv_Ovf_I1_Un:
						this.ProcessConversion(bb, op, host.PlatformType.SystemInt8);
						break;

					case OperationCode.Conv_I2:
					case OperationCode.Conv_Ovf_I2:
					case OperationCode.Conv_Ovf_I2_Un:
						this.ProcessConversion(bb, op, host.PlatformType.SystemInt16);
						break;

					case OperationCode.Conv_I4:
					case OperationCode.Conv_Ovf_I4:
					case OperationCode.Conv_Ovf_I4_Un:
						this.ProcessConversion(bb, op, host.PlatformType.SystemInt32);
						break;

					case OperationCode.Conv_I8:
					case OperationCode.Conv_Ovf_I8:
					case OperationCode.Conv_Ovf_I8_Un:
						this.ProcessConversion(bb, op, host.PlatformType.SystemInt64);
						break;

					case OperationCode.Conv_U:
					case OperationCode.Conv_Ovf_U:
					case OperationCode.Conv_Ovf_U_Un:
						this.ProcessConversion(bb, op, host.PlatformType.SystemUIntPtr);
						break;

					case OperationCode.Conv_U1:
					case OperationCode.Conv_Ovf_U1:
					case OperationCode.Conv_Ovf_U1_Un:
						this.ProcessConversion(bb, op, host.PlatformType.SystemUInt8);
						break;

					case OperationCode.Conv_U2:
					case OperationCode.Conv_Ovf_U2:
					case OperationCode.Conv_Ovf_U2_Un:
						this.ProcessConversion(bb, op, host.PlatformType.SystemUInt16);
						break;

					case OperationCode.Conv_U4:
					case OperationCode.Conv_Ovf_U4:
					case OperationCode.Conv_Ovf_U4_Un:
						this.ProcessConversion(bb, op, host.PlatformType.SystemUInt32);
						break;

					case OperationCode.Conv_U8:
					case OperationCode.Conv_Ovf_U8:
					case OperationCode.Conv_Ovf_U8_Un:
						this.ProcessConversion(bb, op, host.PlatformType.SystemUInt64);
						break;

					case OperationCode.Conv_R4:
						this.ProcessConversion(bb, op, host.PlatformType.SystemFloat32);
						break;

					case OperationCode.Conv_R8:
					case OperationCode.Conv_R_Un:
						this.ProcessConversion(bb, op, host.PlatformType.SystemFloat64);
						break;

					//case OperationCode.Ckfinite:
					//    var operand = this.PopOperandStack();
					//    var chkfinite = new MutableCodeModel.MethodReference()
					//    {
					//        CallingConvention = Cci.CallingConvention.FastCall,
					//        ContainingType = host.PlatformType.SystemFloat64,
					//        Name = this.host.NameTable.GetNameFor("__ckfinite__"),
					//        Type = host.PlatformType.SystemFloat64,
					//        InternFactory = host.InternFactory,
					//    };
					//    expression = new MethodCall() { Arguments = new List<IExpression>(1) { operand }, IsStaticCall = true, Type = operand.Type, MethodToCall = chkfinite };
					//    break;

					case OperationCode.Constrained_:
						//This prefix is redundant and is not represented in the code model.
						break;

					case OperationCode.Cpblk:
						this.ProcessCopyMemory(bb, op);
						break;

					case OperationCode.Cpobj:
						this.ProcessCopyObject(bb, op);
						break;

					case OperationCode.Dup:
						this.ProcessDup(bb, op);
						break;

					//case OperationCode.Endfilter:
					//    statement = this.ParseEndfilter();
					//    break;

					case OperationCode.Endfinally:
						this.ProcessEndFinally(bb, op);
						break;

					case OperationCode.Initblk:
						this.ProcessInitializeMemory(bb, op);
						break;

					case OperationCode.Initobj:
						this.ProcessInitializeObject(bb, op);
						break;

					case OperationCode.Ldarg:
					case OperationCode.Ldarg_0:
					case OperationCode.Ldarg_1:
					case OperationCode.Ldarg_2:
					case OperationCode.Ldarg_3:
					case OperationCode.Ldarg_S:
						this.ProcessLoadArgument(bb, op);
					    break;

					case OperationCode.Ldarga:
					case OperationCode.Ldarga_S:
						this.ProcessLoadArgumentAddress(bb, op);
						break;

					case OperationCode.Ldloc:
					case OperationCode.Ldloc_0:
					case OperationCode.Ldloc_1:
					case OperationCode.Ldloc_2:
					case OperationCode.Ldloc_3:
					case OperationCode.Ldloc_S:
					    this.ProcessLoadLocal(bb, op);
					    break;

					case OperationCode.Ldloca:
					case OperationCode.Ldloca_S:
						this.ProcessLoadLocalAddress(bb, op);
					    break;

					case OperationCode.Ldfld:
						this.ProcessLoadInstanceField(bb, op);
						break;

					case OperationCode.Ldsfld:
						this.ProcessLoadStaticField(bb, op);
						break;

					case OperationCode.Ldflda:
						this.ProcessLoadInstanceFieldAddress(bb, op);
						break;

					case OperationCode.Ldsflda:
						this.ProcessLoadStaticFieldAddress(bb, op);
						break;

					case OperationCode.Ldftn:
						this.ProcessLoadMethodAddress(bb, op);
						break;

					case OperationCode.Ldvirtftn:
						this.ProcessLoadVirtualMethodAddress(bb, op);
						break;

					case OperationCode.Ldc_I4:
					case OperationCode.Ldc_I4_0:
					case OperationCode.Ldc_I4_1:
					case OperationCode.Ldc_I4_2:
					case OperationCode.Ldc_I4_3:
					case OperationCode.Ldc_I4_4:
					case OperationCode.Ldc_I4_5:
					case OperationCode.Ldc_I4_6:
					case OperationCode.Ldc_I4_7:
					case OperationCode.Ldc_I4_8:
					case OperationCode.Ldc_I4_M1:
					case OperationCode.Ldc_I4_S:
					case OperationCode.Ldc_I8:
					case OperationCode.Ldc_R4:
					case OperationCode.Ldc_R8:
					case OperationCode.Ldnull:
					case OperationCode.Ldstr:
						this.ProcessLoadConstant(bb, op);
						break;

					case OperationCode.Ldind_I:
					case OperationCode.Ldind_I1:
					case OperationCode.Ldind_I2:
					case OperationCode.Ldind_I4:
					case OperationCode.Ldind_I8:
					case OperationCode.Ldind_R4:
					case OperationCode.Ldind_R8:
					case OperationCode.Ldind_Ref:
					case OperationCode.Ldind_U1:
					case OperationCode.Ldind_U2:
					case OperationCode.Ldind_U4:
					case OperationCode.Ldobj:
						this.ProcessLoadIndirect(bb, op);
						break;

					case OperationCode.Ldlen:
						this.ProcessLoadArrayLength(bb, op);
						break;

					case OperationCode.Ldtoken:
						this.ProcessLoadToken(bb, op);
						break;

					case OperationCode.Localloc:
						this.ProcessLocalAllocation(bb, op);
						break;

					//case OperationCode.Mkrefany:
					//    expression = this.ParseMakeTypedReference(currentOperation);
					//    break;

					case OperationCode.Neg:
						this.ProcessUnaryOperation(bb, op, UnaryOperation.Neg);
						break;

					case OperationCode.Not:
						this.ProcessUnaryOperation(bb, op, UnaryOperation.Not);
						break;

					case OperationCode.Newobj:
						this.ProcessCreateObject(bb, op);
						break;

					case OperationCode.No_:
						//if code out there actually uses this, I need to know sooner rather than later.
						//TODO: need object model support
						throw new NotImplementedException("Invalid opcode: No.");

					case OperationCode.Pop:
						stack.Pop();
						break;

					//case OperationCode.Readonly_:
					//    this.sawReadonly = true;
					//    break;

					//case OperationCode.Refanytype:
					//    expression = this.ParseGetTypeOfTypedReference();
					//    break;

					//case OperationCode.Refanyval:
					//    expression = this.ParseGetValueOfTypedReference(currentOperation);
					//    break;

					case OperationCode.Ret:
						this.ProcessReturn(bb, op);
						break;

					case OperationCode.Sizeof:
						this.ProcessSizeof(bb, op);
						break;

					case OperationCode.Starg:
					case OperationCode.Starg_S:
						this.ProcessStoreArgument(bb, op);
					    break;

					case OperationCode.Array_Set:
					case OperationCode.Stelem:
					case OperationCode.Stelem_I:
					case OperationCode.Stelem_I1:
					case OperationCode.Stelem_I2:
					case OperationCode.Stelem_I4:
					case OperationCode.Stelem_I8:
					case OperationCode.Stelem_R4:
					case OperationCode.Stelem_R8:
					case OperationCode.Stelem_Ref:
						this.ProcessStoreArrayElement(bb, op);
						break;

					case OperationCode.Stfld:
						this.ProcessStoreInstanceField(bb, op);
						break;

					case OperationCode.Stsfld:
						this.ProcessStoreStaticField(bb, op);
						break;

					case OperationCode.Stind_I:
					case OperationCode.Stind_I1:
					case OperationCode.Stind_I2:
					case OperationCode.Stind_I4:
					case OperationCode.Stind_I8:
					case OperationCode.Stind_R4:
					case OperationCode.Stind_R8:
					case OperationCode.Stind_Ref:
					case OperationCode.Stobj:
						this.ProcessStoreIndirect(bb, op);
					    break;

					case OperationCode.Stloc:
					case OperationCode.Stloc_0:
					case OperationCode.Stloc_1:
					case OperationCode.Stloc_2:
					case OperationCode.Stloc_3:
					case OperationCode.Stloc_S:
					    this.ProcessStoreLocal(bb, op);
					    break;

					case OperationCode.Switch:
						this.ProcessSwitch(bb, op);
						break;

					//case OperationCode.Tail_:
					//    this.sawTailCall = true;
					//    break;

					case OperationCode.Throw:
						this.ProcessThrow(bb, op);
						break;

					case OperationCode.Rethrow:
						this.ProcessRethrow(bb, op);
						break;

					//case OperationCode.Unaligned_:
					//    Contract.Assume(currentOperation.Value is byte);
					//    var alignment = (byte)currentOperation.Value;
					//    Contract.Assume(alignment == 1 || alignment == 2 || alignment == 4);
					//    this.alignment = alignment;
					//    break;

					//case OperationCode.Volatile_:
					//    this.sawVolatile = true;
					//    break;

					default:
						//throw new UnknownBytecodeException(bb, op);
						System.Console.WriteLine("Unknown bytecode: {0}", op.OperationCode);
						break;
				}
			}
		}

		private void ProcessExceptionHandling(BasicBlockInfo bb, uint offset)
		{
			if (trys.ContainsKey(offset))
			{
				contextKind = ContextKind.Try;
				tryInfo = trys[offset];					

				var instruction = new TryInstruction(offset);
				bb.Instructions.Add(instruction);
			}

			if (tryInfo != null)
			{
				if (tryInfo.ExceptionHandlers.ContainsKey(offset))
				{
					contextKind = ContextKind.Catch;
					var catchInfo = tryInfo.ExceptionHandlers[offset];						
					// push the exception into the stack
					var exception = stack.Push();

					var instruction = new CatchInstruction(offset, exception, catchInfo.ExceptionType);
					bb.Instructions.Add(instruction);
				}

				if (tryInfo.Finally != null && tryInfo.Finally.BeginOffset == offset)
				{
					contextKind = ContextKind.Finally;
					var instruction = new FinallyInstruction(offset);
					bb.Instructions.Add(instruction);
				}
			}
		}

		private void ProcessSwitch(BasicBlockInfo bb, IOperation op)
		{
			var targets = op.Value as uint[];
			var operand = stack.Pop();

			var instruction = new SwitchInstruction(op.Offset, operand, targets);
			bb.Instructions.Add(instruction);
		}

		private void ProcessThrow(BasicBlockInfo bb, IOperation op)
		{
			var exception = stack.Pop();
			stack.Clear();

			var instruction = new ThrowInstruction(op.Offset, exception);
			bb.Instructions.Add(instruction);
		}

		private void ProcessRethrow(BasicBlockInfo bb, IOperation op)
		{
			var instruction = new ThrowInstruction(op.Offset);
			bb.Instructions.Add(instruction);
		}

		private void ProcessLocalAllocation(BasicBlockInfo bb, IOperation op)
		{
			var numberOfBytes = stack.Pop();
			var targetAddress = stack.Push();

			var instruction = new LocalAllocationInstruction(op.Offset, targetAddress, numberOfBytes);
			bb.Instructions.Add(instruction);
		}

		private void ProcessInitializeMemory(BasicBlockInfo bb, IOperation op)
		{
			var numberOfBytes = stack.Pop();
			var fillValue = stack.Pop();
			var targetAddress = stack.Pop();

			var instruction = new InitializeMemoryInstruction(op.Offset, targetAddress, fillValue, numberOfBytes);
			bb.Instructions.Add(instruction);
		}

		private void ProcessInitializeObject(BasicBlockInfo bb, IOperation op)
		{
			var targetAddress = stack.Pop();
			var instruction = new InitializeObjectInstruction(op.Offset, targetAddress);
			bb.Instructions.Add(instruction);
		}

		private void ProcessCreateArray(BasicBlockInfo bb, IOperation op, bool withLowerBounds)
		{
			var arrayType = op.Value as IArrayTypeReference;
			var elementType = arrayType.ElementType;
			var rank = arrayType.Rank;
			var lowerBounds = new List<Variable>();
			var sizes = new List<Variable>();

			if (withLowerBounds)
			{
				for (uint i = 0; i < arrayType.Rank; i++)
				{
					var operand = stack.Pop();
					lowerBounds.Add(operand);
				}
			}

			for (uint i = 0; i < arrayType.Rank; i++)
			{
				var operand = stack.Pop();
				sizes.Add(operand);
			}

			lowerBounds.Reverse();
			sizes.Reverse();

			var result = stack.Push();
			var instruction = new CreateArrayInstruction(op.Offset, result, elementType, rank, lowerBounds, sizes);
			bb.Instructions.Add(instruction);
		}

		private void ProcessCopyObject(BasicBlockInfo bb, IOperation op)
		{
			var sourceAddress = stack.Pop();
			var targetAddress = stack.Pop();

			var instruction = new CopyObjectInstruction(op.Offset, targetAddress, sourceAddress);
			bb.Instructions.Add(instruction);
		}

		private void ProcessCopyMemory(BasicBlockInfo bb, IOperation op)
		{
			var numberOfBytes = stack.Pop();
			var sourceAddress = stack.Pop();
			var targetAddress = stack.Pop();

			var instruction = new CopyMemoryInstruction(op.Offset, targetAddress, sourceAddress, numberOfBytes);
			bb.Instructions.Add(instruction);
		}

		private void ProcessCreateObject(BasicBlockInfo bb, IOperation op)
		{
			var callee = op.Value as IMethodReference;
			var arguments = new List<Variable>();

			foreach (var par in callee.Parameters)
			{
				var arg = stack.Pop();
				arguments.Add(arg);
			}

			foreach (var par in callee.ExtraParameters)
			{
				var arg = stack.Pop();
				arguments.Add(arg);
			}

			var result = stack.Push();
			arguments.Add(result);
			arguments.Reverse();

			var instruction = new CreateObjectInstruction(op.Offset, result, callee, arguments);
			bb.Instructions.Add(instruction);
		}

		private void ProcessMethodCall(BasicBlockInfo bb, IOperation op)
		{
			var callee = op.Value as IMethodReference;
			var arguments = new List<Variable>();
			Variable result = null;

			foreach (var par in callee.Parameters)
			{
				var arg = stack.Pop();
				arguments.Add(arg);
			}

			foreach (var par in callee.ExtraParameters)
			{
				var arg = stack.Pop();
				arguments.Add(arg);
			}

			if (!callee.IsStatic)
			{
				// Adding implicit this parameter
				var argThis = stack.Pop();
				arguments.Add(argThis);
			}

			arguments.Reverse();

			if (callee.Type.TypeCode != PrimitiveTypeCode.Void)
				result = stack.Push();

			var instruction = new MethodCallInstruction(op.Offset, result, callee, arguments);
			bb.Instructions.Add(instruction);
		}

		private void ProcessMethodCallIndirect(BasicBlockInfo bb, IOperation op)
		{
			var calleeType = op.Value as IFunctionPointerTypeReference;
			var calleePointer = stack.Pop();
			var arguments = new List<Variable>();
			Variable result = null;

			foreach (var par in calleeType.Parameters)
			{
				var arg = stack.Pop();
				arguments.Add(arg);
			}

			if (!calleeType.IsStatic)
			{
				// Adding implicit this parameter
				var argThis = stack.Pop();
				arguments.Add(argThis);
			}

			arguments.Reverse();

			if (calleeType.Type.TypeCode != PrimitiveTypeCode.Void)
				result = stack.Push();

			var instruction = new IndirectMethodCallInstruction(op.Offset, result, calleePointer, calleeType, arguments);
			bb.Instructions.Add(instruction);
		}

		private void ProcessJumpCall(BasicBlockInfo bb, IOperation op)
		{
			var callee = op.Value as IMethodReference;
			var arguments = new List<Variable>();
			Variable result = null;

			if (!callee.IsStatic)
			{
				// Adding implicit this parameter
				arguments.Add(thisParameter);
			}

			foreach (var par in parameters)
			{
				var arg = par.Value;
				arguments.Add(arg);
			}

			if (callee.Type.TypeCode != PrimitiveTypeCode.Void)
				result = stack.Push();

			var instruction = new MethodCallInstruction(op.Offset, result, callee, arguments);
			bb.Instructions.Add(instruction);
		}

		private void ProcessSizeof(BasicBlockInfo bb, IOperation op)
		{
			var type = op.Value as ITypeReference;
			var result = stack.Push();
			var instruction = new SizeofInstruction(op.Offset, result, type);
			bb.Instructions.Add(instruction);
		}

		private void ProcessUnaryConditionalBranch(BasicBlockInfo bb, IOperation op, bool value)
		{
			var right = new Constant(value);
			var left = stack.Pop();
			var target = (uint)op.Value;
			var instruction = new ConditionalBranchInstruction(op.Offset, left, BranchOperation.Eq, right, target);
			bb.Instructions.Add(instruction);

			this.AddToPendingBasicBlocks(target, true);
		}

		private void ProcessBinaryConditionalBranch(BasicBlockInfo bb, IOperation op, BranchOperation condition)
		{
			var right = stack.Pop();
			var left = stack.Pop();
			var target = (uint)op.Value;
			var instruction = new ConditionalBranchInstruction(op.Offset, left, condition, right, target);
			bb.Instructions.Add(instruction);

			this.AddToPendingBasicBlocks(target, true);
		}

		private void ProcessEndFinally(BasicBlockInfo bb, IOperation op)
		{
			var target = string.Format("L_{0:X4}", tryInfo.Finally.EndOffset);
			contextKind = ContextKind.None;
			stack.Clear();

			var instruction = new UnconditionalBranchInstruction(op.Offset, 0);
			instruction.Target = target;
			bb.Instructions.Add(instruction);
		}

		private void ProcessLeave(BasicBlockInfo bb, IOperation op)
		{
			BranchInstruction instruction;
			var target = string.Format("L_{0:X4}", op.Value);

			if (contextKind == ContextKind.Try)
			{
				foreach (var catchInfo in tryInfo.ExceptionHandlers)
				{
					instruction = new ExceptionalBranchInstruction(op.Offset, catchInfo.Value.BeginOffset, catchInfo.Value.ExceptionType);
					bb.Instructions.Add(instruction);
				}
			}

			if (contextKind == ContextKind.None ||
				tryInfo.ExceptionHandlers.Count == 0)
			{
				target = string.Format("L_{0:X4}'", tryInfo.Finally.BeginOffset);
			}

			contextKind = ContextKind.None;
			stack.Clear();

			instruction = new UnconditionalBranchInstruction(op.Offset, 0);
			instruction.Target = target;
			bb.Instructions.Add(instruction);

			this.AddToPendingBasicBlocks((uint)op.Value, true);
		}

		private void ProcessUnconditionalBranch(BasicBlockInfo bb, IOperation op)
		{
			var target = (uint)op.Value;
			var instruction = new UnconditionalBranchInstruction(op.Offset, target);
			bb.Instructions.Add(instruction);

			this.AddToPendingBasicBlocks(target, true);
		}

		private void ProcessReturn(BasicBlockInfo bb, IOperation op)
		{
			Variable operand = null;

			if (method.Type.TypeCode != PrimitiveTypeCode.Void)
				operand = stack.Pop();

			var instruction = new ReturnInstruction(op.Offset, operand);
			bb.Instructions.Add(instruction);
		}

		private void ProcessLoadConstant(BasicBlockInfo bb, IOperation op)
		{
			var source = new Constant(op.Value);
			var dest = stack.Push();
			var instruction = new LoadInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessLoadArgument(BasicBlockInfo bb, IOperation op)
		{
			var source = thisParameter;

			if (op.Value is IParameterDefinition)
			{
				var argument = op.Value as IParameterDefinition;
				source = parameters[argument];
			}

			var dest = stack.Push();
			var instruction = new LoadInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessLoadArgumentAddress(BasicBlockInfo bb, IOperation op)
		{
			var operand = thisParameter;

			if (op.Value is IParameterDefinition)
			{
				var argument = op.Value as IParameterDefinition;
				operand = parameters[argument];
			}

			var dest = stack.Push();
			var source = new Reference(operand);
			var instruction = new LoadInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessLoadLocal(BasicBlockInfo bb, IOperation op)
		{
			var local = op.Value as ILocalDefinition;
			var source = locals[local];
			var dest = stack.Push();
			var instruction = new LoadInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessLoadLocalAddress(BasicBlockInfo bb, IOperation op)
		{
			var local = op.Value as ILocalDefinition;
			var operand = locals[local];
			var dest = stack.Push();
			var source = new Reference(operand);
			var instruction = new LoadInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessLoadIndirect(BasicBlockInfo bb, IOperation op)
		{
			var address = stack.Pop();
			var dest = stack.Push();
			var source = new Dereference(address);
			var instruction = new LoadInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessLoadInstanceField(BasicBlockInfo bb, IOperation op)
		{
			var field = op.Value as IFieldReference;
			var obj = stack.Pop();
			var dest = stack.Push();
			var fieldName = MemberHelper.GetMemberSignature(field, NameFormattingOptions.OmitContainingType | NameFormattingOptions.PreserveSpecialNames);
			var source = new InstanceFieldAccess(obj, fieldName);
			var instruction = new LoadInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessLoadStaticField(BasicBlockInfo bb, IOperation op)
		{
			var field = op.Value as IFieldReference;
			var dest = stack.Push();
			var fieldName = MemberHelper.GetMemberSignature(field, NameFormattingOptions.OmitContainingType | NameFormattingOptions.PreserveSpecialNames);
			var source = new StaticFieldAccess(field.ContainingType, fieldName);
			var instruction = new LoadInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessLoadInstanceFieldAddress(BasicBlockInfo bb, IOperation op)
		{
			var field = op.Value as IFieldReference;
			var obj = stack.Pop();
			var dest = stack.Push();
			var fieldName = MemberHelper.GetMemberSignature(field, NameFormattingOptions.OmitContainingType | NameFormattingOptions.PreserveSpecialNames);
			var access = new InstanceFieldAccess(obj, fieldName);
			var source = new Reference(access);
			var instruction = new LoadInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessLoadStaticFieldAddress(BasicBlockInfo bb, IOperation op)
		{
			var field = op.Value as IFieldReference;
			var dest = stack.Push();
			var fieldName = MemberHelper.GetMemberSignature(field, NameFormattingOptions.OmitContainingType | NameFormattingOptions.PreserveSpecialNames);
			var access = new StaticFieldAccess(field.ContainingType, fieldName);
			var source = new Reference(access);
			var instruction = new LoadInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessLoadArrayLength(BasicBlockInfo bb, IOperation op)
		{
			var array = stack.Pop();
			var dest = stack.Push();
			var length = new InstanceFieldAccess(array, "Length");
			var instruction = new LoadInstruction(op.Offset, dest, length);
			bb.Instructions.Add(instruction);
		}

		private void ProcessLoadArrayElement(BasicBlockInfo bb, IOperation op)
		{
			var index = stack.Pop();
			var array = stack.Pop();			
			var dest = stack.Push();
			var source = new ArrayElementAccess(array, index);
			var instruction = new LoadInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessLoadArrayElementAddress(BasicBlockInfo bb, IOperation op)
		{
			var index = stack.Pop();
			var array = stack.Pop();
			var dest = stack.Push();
			var access = new ArrayElementAccess(array, index);
			var source = new Reference(access);
			var instruction = new LoadInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessLoadMethodAddress(BasicBlockInfo bb, IOperation op)
		{
			var method = op.Value as IMethodReference;
			var dest = stack.Push();
			var signature = new StaticMethod(method);
			var source = new Reference(signature);
			var instruction = new LoadInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessLoadVirtualMethodAddress(BasicBlockInfo bb, IOperation op)
		{
			var method = op.Value as IMethodReference;
			var obj = stack.Pop();
			var dest = stack.Push();
			var signature = new VirtualMethod(obj, method);
			var source = new Reference(signature);
			var instruction = new LoadInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessLoadToken(BasicBlockInfo bb, IOperation op)
		{
			var type = op.Value as ITypeReference;

			//TODO: borrar esto en algun momento
			if (type == null)
				throw new Exception("Error while processing load token instruction.");

			var result = stack.Push();
			var instruction = new LoadTokenInstruction(op.Offset, result, type);
			bb.Instructions.Add(instruction);
		}

		private void ProcessStoreArgument(BasicBlockInfo bb, IOperation op)
		{
			var dest = thisParameter;

			if (op.Value is IParameterDefinition)
			{
				var argument = op.Value as IParameterDefinition;
				dest = parameters[argument];
			}
			
			var source = stack.Pop();
			var instruction = new LoadInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessStoreLocal(BasicBlockInfo bb, IOperation op)
		{
			var local = op.Value as ILocalDefinition;
			var dest = locals[local];
			var source = stack.Pop();
			var instruction = new LoadInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessStoreIndirect(BasicBlockInfo bb, IOperation op)
		{
			var source = stack.Pop();
			var address = stack.Pop();
			var dest = new Dereference(address);
			var instruction = new StoreInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessStoreInstanceField(BasicBlockInfo bb, IOperation op)
		{
			var field = op.Value as IFieldDefinition;
			var source = stack.Pop();
			var obj = stack.Pop();
			var dest = new InstanceFieldAccess(obj, field.Name.Value);
			var instruction = new StoreInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessStoreStaticField(BasicBlockInfo bb, IOperation op)
		{
			var field = op.Value as IFieldDefinition;
			var source = stack.Pop();
			var dest = new StaticFieldAccess(field.ContainingType, field.Name.Value);
			var instruction = new StoreInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessStoreArrayElement(BasicBlockInfo bb, IOperation op)
		{
			var source = stack.Pop();
			var index = stack.Pop();
			var array = stack.Pop();
			var dest = new ArrayElementAccess(array, index);
			var instruction = new StoreInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private void ProcessEmptyOperation(BasicBlockInfo bb, IOperation op)
		{
			var instruction = new NopInstruction(op.Offset);
			bb.Instructions.Add(instruction);
		}

		private void ProcessBreakpointOperation(BasicBlockInfo bb, IOperation op)
		{
			var instruction = new BreakpointInstruction(op.Offset);
			bb.Instructions.Add(instruction);
		}

		private void ProcessUnaryOperation(BasicBlockInfo bb, IOperation op, UnaryOperation operation)
		{
			var operand = stack.Pop();
			var dest = stack.Push();
			var instruction = new UnaryInstruction(op.Offset, dest, operation, operand);
			bb.Instructions.Add(instruction);
		}

		private void ProcessBinaryOperation(BasicBlockInfo bb, IOperation op, BinaryOperation operation)
		{
			var right = stack.Pop();
			var left = stack.Pop();
			var dest = stack.Push();
			var instruction = new BinaryInstruction(op.Offset, dest, left, operation, right);
			bb.Instructions.Add(instruction);
		}

		private void ProcessConversion(BasicBlockInfo bb, IOperation op)
		{
			var type = op.Value as ITypeReference;
			this.ProcessConversion(bb, op, type);
		}

		private void ProcessConversion(BasicBlockInfo bb, IOperation op, ITypeReference type)
		{
			var operand = stack.Pop();
			var result = stack.Push();
			var instruction = new ConvertInstruction(op.Offset, result, type, operand);
			bb.Instructions.Add(instruction);
		}

		private void ProcessDup(BasicBlockInfo bb, IOperation op)
		{
			var source = stack.Top();
			var dest = stack.Push();
			var instruction = new LoadInstruction(op.Offset, dest, source);
			bb.Instructions.Add(instruction);
		}

		private string GetLocalSourceName(ILocalDefinition local)
		{
			var name = local.Name.Value;

			if (this.sourceLocationProvider != null)
			{
				bool isCompilerGenerated;
				name = this.sourceLocationProvider.GetSourceNameFor(local, out isCompilerGenerated);
			}

			return name;
		}
	}
}
