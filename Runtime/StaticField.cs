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
}
