using System;
using System.Reflection;
using System.Reflection.Emit;

namespace PlayMakerTurbo
{
    // Typed accessors for private static fields of other assemblies, emitted once. Unlike FieldInfo.GetValue
    // they do not box value types, so reading an int or bool every frame allocates nothing.
    internal static class StaticField
    {
        public static Func<T> Getter<T>(FieldInfo field)
        {
            DynamicMethod method = new DynamicMethod("get_" + field.Name, typeof(T), Type.EmptyTypes, field.DeclaringType, true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldsfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<T>)method.CreateDelegate(typeof(Func<T>));
        }

        public static Action<T> Setter<T>(FieldInfo field)
        {
            DynamicMethod method = new DynamicMethod("set_" + field.Name, null, new[] { typeof(T) }, field.DeclaringType, true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Stsfld, field);
            il.Emit(OpCodes.Ret);
            return (Action<T>)method.CreateDelegate(typeof(Action<T>));
        }
    }

    // The same for instance fields: FieldInfo.GetValue/SetValue box the value on every call, these do not.
    internal static class InstanceField
    {
        public static Func<TTarget, TValue> Getter<TTarget, TValue>(FieldInfo field)
        {
            DynamicMethod method = new DynamicMethod("get_" + field.Name, typeof(TValue), new[] { typeof(TTarget) }, field.DeclaringType, true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<TTarget, TValue>)method.CreateDelegate(typeof(Func<TTarget, TValue>));
        }

        public static Action<TTarget, TValue> Setter<TTarget, TValue>(FieldInfo field)
        {
            DynamicMethod method = new DynamicMethod("set_" + field.Name, null, new[] { typeof(TTarget), typeof(TValue) }, field.DeclaringType, true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, field);
            il.Emit(OpCodes.Ret);
            return (Action<TTarget, TValue>)method.CreateDelegate(typeof(Action<TTarget, TValue>));
        }
    }
}
