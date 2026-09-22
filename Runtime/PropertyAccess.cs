using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PlayMakerTurbo
{
    // Replacement for FsmProperty.GetValue/SetValue (GetProperty/SetProperty actions). The originals go through
    // PropertyInfo.GetValue/SetValue, which in Mono allocate an argument array and box the value on every call.
    // For a single C# property whose type selects the same branch of the original's type chain, a cached typed
    // delegate reads/writes the same value without allocating. Anything else runs the original method.
    public static class PropertyAccess
    {
        private static readonly object unsupported = new object();

        public static void Get(FsmProperty property)
        {
            property.CheckForReinitialize();
            Object target = property.TurboTarget;
            MemberInfo[] members = property.TurboMembers;
            if (target == null || members == null)
                return;

            Accessor accessor = Resolve(property, members);
            if (accessor != null && accessor.CanGet)
                accessor.Get(property, target);
            else
                property.TurboOriginalGetValue();
        }

        public static void Set(FsmProperty property)
        {
            property.CheckForReinitialize();
            Object target = property.TurboTarget;
            MemberInfo[] members = property.TurboMembers;
            if (target == null || members == null)
                return;

            Accessor accessor = Resolve(property, members);
            if (accessor != null && accessor.CanSet)
                accessor.Set(property, target);
            else
                property.TurboOriginalSetValue();
        }

        // Cached on the FsmProperty and rebuilt whenever Init() resolved a new member path.
        private static Accessor Resolve(FsmProperty property, MemberInfo[] members)
        {
            object cached = property.TurboAccessor;
            Accessor accessor = cached as Accessor;
            if (accessor != null && ReferenceEquals(accessor.Members, members))
                return accessor;
            if (cached is MemberInfo[] && ReferenceEquals(cached, members))
                return null;

            accessor = Build(property, members);
            property.TurboAccessor = accessor ?? (object)members;
            return accessor;
        }

        private static Accessor Build(FsmProperty property, MemberInfo[] members)
        {
            if (members.Length != 1)
                return null;
            PropertyInfo info = members[0] as PropertyInfo;
            if (info == null || info.GetIndexParameters().Length != 0 || info.DeclaringType.IsValueType)
                return null;

            // FsmProperty.PropertyType picks the branch in the original; only take types whose branch is
            // unambiguous and identical to the member's own type.
            Type type = info.PropertyType;
            if (!ReferenceEquals(property.PropertyType, type) || !Sinks.Supports(type))
                return null;

            try
            {
                Type generic = typeof(Accessor<,>).MakeGenericType(info.DeclaringType, type);
                Accessor accessor = (Accessor)Activator.CreateInstance(generic);
                accessor.Init(members, info);
                return accessor;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"PlayMakerTurbo: no fast path for {info.DeclaringType.Name}.{info.Name}, using reflection.\n{e.Message}");
                return null;
            }
        }

        private abstract class Accessor
        {
            public MemberInfo[] Members;
            public bool CanGet;
            public bool CanSet;

            public abstract void Init(MemberInfo[] members, PropertyInfo info);
            public abstract void Get(FsmProperty property, Object target);
            public abstract void Set(FsmProperty property, Object target);
        }

        private sealed class Accessor<TTarget, TValue> : Accessor where TTarget : class
        {
            private Func<TTarget, TValue> getter;
            private Action<TTarget, TValue> setter;
            private Sink<TValue> sink;

            public override void Init(MemberInfo[] members, PropertyInfo info)
            {
                Members = members;
                sink = Sinks.Get<TValue>();
                MethodInfo get = info.GetGetMethod(true);
                MethodInfo set = info.GetSetMethod(true);
                if (get != null)
                    getter = (Func<TTarget, TValue>)Delegate.CreateDelegate(typeof(Func<TTarget, TValue>), get);
                if (set != null && sink.CanSet)
                    setter = (Action<TTarget, TValue>)Delegate.CreateDelegate(typeof(Action<TTarget, TValue>), set);
                CanGet = getter != null;
                CanSet = setter != null;
            }

            public override void Get(FsmProperty property, Object target)
            {
                sink.Store(property, getter((TTarget)(object)target));
            }

            // Same as the original branch for this type: write only when the parameter is not None.
            public override void Set(FsmProperty property, Object target)
            {
                if (!sink.IsNone(property))
                    setter((TTarget)(object)target, sink.Load(property));
            }
        }

        private sealed class Sink<T>
        {
            public Action<FsmProperty, T> Store;
            public Func<FsmProperty, T> Load;
            public Func<FsmProperty, bool> IsNone;
            // False for GameObject/Material/Texture: when their parameter is None the original SetValue falls
            // through to the generic Object branch, so those writes keep using the original method.
            public bool CanSet;
        }

        private static class Sinks
        {
            private static readonly Dictionary<Type, object> all = new Dictionary<Type, object>
            {
                { typeof(bool), New<bool>((p, v) => p.BoolParameter.Value = v, p => p.BoolParameter.Value, p => p.BoolParameter.IsNone, true) },
                { typeof(int), New<int>((p, v) => p.IntParameter.Value = v, p => p.IntParameter.Value, p => p.IntParameter.IsNone, true) },
                { typeof(float), New<float>((p, v) => p.FloatParameter.Value = v, p => p.FloatParameter.Value, p => p.FloatParameter.IsNone, true) },
                { typeof(string), New<string>((p, v) => p.StringParameter.Value = v, p => p.StringParameter.Value, p => p.StringParameter.IsNone, true) },
                { typeof(Vector2), New<Vector2>((p, v) => p.Vector2Parameter.Value = v, p => p.Vector2Parameter.Value, p => p.Vector2Parameter.IsNone, true) },
                { typeof(Vector3), New<Vector3>((p, v) => p.Vector3Parameter.Value = v, p => p.Vector3Parameter.Value, p => p.Vector3Parameter.IsNone, true) },
                { typeof(Rect), New<Rect>((p, v) => p.RectParamater.Value = v, p => p.RectParamater.Value, p => p.RectParamater.IsNone, true) },
                { typeof(Quaternion), New<Quaternion>((p, v) => p.QuaternionParameter.Value = v, p => p.QuaternionParameter.Value, p => p.QuaternionParameter.IsNone, true) },
                { typeof(Color), New<Color>((p, v) => p.ColorParameter.Value = v, p => p.ColorParameter.Value, p => p.ColorParameter.IsNone, true) },
                { typeof(GameObject), New<GameObject>((p, v) => p.GameObjectParameter.Value = v, null, null, false) },
                { typeof(Material), New<Material>((p, v) => p.MaterialParameter.Value = v, null, null, false) },
                { typeof(Texture), New<Texture>((p, v) => p.TextureParameter.Value = v, null, null, false) },
            };

            private static Sink<T> New<T>(Action<FsmProperty, T> store, Func<FsmProperty, T> load, Func<FsmProperty, bool> isNone, bool canSet)
            {
                return new Sink<T> { Store = store, Load = load, IsNone = isNone, CanSet = canSet };
            }

            public static bool Supports(Type type)
            {
                return all.ContainsKey(type);
            }

            public static Sink<T> Get<T>()
            {
                return (Sink<T>)all[typeof(T)];
            }
        }
    }
}
