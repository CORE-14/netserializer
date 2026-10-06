/*
 * Copyright 2015 Tomi Valkeinen
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace NetSerializer.TypeSerializers
{
	// Serializes BitArray as a uint length prefix (stored as count + 1, matching the
	// other serializers) followed by one bool per bit.
	// BitArray implements ISerializable, which GenericSerializer rejects, so it needs
	// its own serializer.
	sealed class BitArraySerializer : IDynamicTypeSerializer
	{
		public bool Handles(Type type)
		{
			return typeof(BitArray).IsAssignableFrom(type);
		}

		public System.Collections.Generic.IEnumerable<Type> GetSubtypes(Type type)
		{
			return new[] { typeof(uint), typeof(bool) };
		}

		public void GenerateWriterMethod(Serializer serializer, Type type, ILGenerator il)
		{
			// arg0: Serializer, arg1: Stream, arg2: value
			var boolData = serializer.GetIndirectData(typeof(bool));

			var notNullLabel = il.DefineLabel();
			il.Emit(OpCodes.Ldarg_2);
			il.Emit(OpCodes.Brtrue_S, notNullLabel);

			// if value == null, write 0
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Ldc_I4_0);
			il.Emit(OpCodes.Call, serializer.GetDirectWriter(typeof(uint)));
			il.Emit(OpCodes.Ret);

			il.MarkLabel(notNullLabel);

			// write array len + 1
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Ldarg_2);
			il.Emit(OpCodes.Callvirt, BitArrayLengthGetMethod(type));
			il.Emit(OpCodes.Ldc_I4_1);
			il.Emit(OpCodes.Add);
			il.Emit(OpCodes.Call, serializer.GetDirectWriter(typeof(uint)));

			// declare i
			var idxLocal = il.DeclareLocal(typeof(int));
			il.Emit(OpCodes.Ldc_I4_0);
			il.Emit(OpCodes.Stloc_S, idxLocal);

			var loopBodyLabel = il.DefineLabel();
			var loopCheckLabel = il.DefineLabel();
			il.Emit(OpCodes.Br_S, loopCheckLabel);

			// loop body
			il.MarkLabel(loopBodyLabel);

			// write element at index i
			if (boolData.WriterNeedsInstance)
				il.Emit(OpCodes.Ldarg_0);

			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Ldarg_2);
			il.Emit(OpCodes.Ldloc_S, idxLocal);
			il.Emit(OpCodes.Callvirt, BitArrayIndexer(type));

			il.Emit(OpCodes.Call, boolData.WriterMethodInfo);

			il.Emit(OpCodes.Ldloc_S, idxLocal);
			il.Emit(OpCodes.Ldc_I4_1);
			il.Emit(OpCodes.Add);
			il.Emit(OpCodes.Stloc_S, idxLocal);

			il.MarkLabel(loopCheckLabel);

			// loop condition
			il.Emit(OpCodes.Ldloc_S, idxLocal);
			il.Emit(OpCodes.Ldarg_2);
			il.Emit(OpCodes.Callvirt, BitArrayLengthGetMethod(type));
			il.Emit(OpCodes.Conv_I4);
			il.Emit(OpCodes.Blt_S, loopBodyLabel);

			il.Emit(OpCodes.Ret);
		}

		public void GenerateReaderMethod(Serializer serializer, Type type, ILGenerator il)
		{
			// arg0: Serializer, arg1: stream, arg2: out value
			var boolData = serializer.GetIndirectData(typeof(bool));

			var lenLocal = il.DeclareLocal(typeof(uint));
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Ldloca_S, lenLocal);
			il.Emit(OpCodes.Call, serializer.GetDirectReader(typeof(uint)));

			var notNullLabel = il.DefineLabel();
			il.Emit(OpCodes.Ldloc_S, lenLocal);
			il.Emit(OpCodes.Brtrue_S, notNullLabel);

			// if len == 0, return null
			il.Emit(OpCodes.Ldarg_2);
			il.Emit(OpCodes.Ldnull);
			il.Emit(OpCodes.Stind_Ref);
			il.Emit(OpCodes.Ret);

			il.MarkLabel(notNullLabel);

			var bitArrayLocal = il.DeclareLocal(type);

			// len holds (original length + 1); decrement to get the true length.
			il.Emit(OpCodes.Ldloc_S, lenLocal);
			il.Emit(OpCodes.Ldc_I4_1);
			il.Emit(OpCodes.Sub);
			il.Emit(OpCodes.Stloc, lenLocal);

			// create new BitArray with the true length (defaultValue = false).
			il.Emit(OpCodes.Ldloc_S, lenLocal);
			il.Emit(OpCodes.Conv_I4);
			il.Emit(OpCodes.Ldc_I4_0); // defaultValue = false
			il.Emit(OpCodes.Newobj, type.GetConstructor(new[] { typeof(int), typeof(bool) }));
			il.Emit(OpCodes.Stloc_S, bitArrayLocal);

			// declare i
			var idxLocal = il.DeclareLocal(typeof(int));
			il.Emit(OpCodes.Ldc_I4_0);
			il.Emit(OpCodes.Stloc_S, idxLocal);

			var loopBodyLabel = il.DefineLabel();
			var loopCheckLabel = il.DefineLabel();

			// declare value local (bitArray indexer setter needs instance, index, value order)
			var valueLocal = il.DeclareLocal(typeof(bool));

			il.Emit(OpCodes.Br_S, loopCheckLabel);

			// loop body
			il.MarkLabel(loopBodyLabel);

			if (boolData.ReaderNeedsInstance)
				il.Emit(OpCodes.Ldarg_0);

			// read element into valueLocal
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Ldloca_S, valueLocal);
			il.Emit(OpCodes.Call, boolData.ReaderMethodInfo);

			// bitArray[i] = value (receiver, index, value)
			il.Emit(OpCodes.Ldloc_S, bitArrayLocal);
			il.Emit(OpCodes.Ldloc_S, idxLocal);
			il.Emit(OpCodes.Ldloc_S, valueLocal);
			il.Emit(OpCodes.Callvirt, BitArrayIndexerSetter(type));

			il.Emit(OpCodes.Ldloc_S, idxLocal);
			il.Emit(OpCodes.Ldc_I4_1);
			il.Emit(OpCodes.Add);
			il.Emit(OpCodes.Stloc_S, idxLocal);

			il.MarkLabel(loopCheckLabel);

			// loop condition: i < len
			il.Emit(OpCodes.Ldloc_S, idxLocal);
			il.Emit(OpCodes.Ldloc_S, lenLocal);
			il.Emit(OpCodes.Conv_I4);
			il.Emit(OpCodes.Blt_S, loopBodyLabel);

			// store new BitArray to out value
			il.Emit(OpCodes.Ldarg_2);
			il.Emit(OpCodes.Ldloc_S, bitArrayLocal);
			il.Emit(OpCodes.Stind_Ref);

			il.Emit(OpCodes.Ret);
		}

		private static MethodInfo BitArrayLengthGetMethod(Type bitArrayType)
		{
			return bitArrayType.GetProperty(nameof(BitArray.Length)).GetMethod;
		}

		private static MethodInfo BitArrayIndexer(Type bitArrayType)
		{
			return bitArrayType.GetProperties().Single(p =>
			{
				var idxParams = p.GetIndexParameters();
				return idxParams.Length == 1 && idxParams[0].ParameterType == typeof(int);
			}).GetMethod;
		}

		private static MethodInfo BitArrayIndexerSetter(Type bitArrayType)
		{
			return bitArrayType.GetMethod("set_Item", new[] { typeof(int), typeof(bool) });
		}
	}
}
