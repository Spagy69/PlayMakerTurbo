using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace MWCFsmProfiler
{
    // For every GetProperty/SetProperty that ran during the recording: which member it reads or writes and
    // whether PlayMakerTurbo's allocation-free path covers it. The reasons mirror PropertyAccess.Build in the
    // Turbo runtime; the Turbo* members exist only in a patched PlayMaker.dll, so they are read by reflection.
    internal static class PropertyCoverage
    {
        private static readonly FieldInfo memberInfoField = typeof(FsmProperty).GetField("memberInfo", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly PropertyInfo turboAccessor = typeof(FsmProperty).GetProperty("TurboAccessor", BindingFlags.Public | BindingFlags.Instance);

        private static readonly HashSet<Type> fastTypes = new HashSet<Type>
        {
            typeof(bool), typeof(int), typeof(float), typeof(string), typeof(UnityEngine.Vector2), typeof(UnityEngine.Vector3),
            typeof(UnityEngine.Rect), typeof(UnityEngine.Quaternion), typeof(UnityEngine.Color),
            typeof(UnityEngine.GameObject), typeof(UnityEngine.Material), typeof(UnityEngine.Texture)
        };

        public static List<Row> Build(double perFrame, double tickToMs)
        {
            Dictionary<string, Row> rows = new Dictionary<string, Row>();
            foreach (KeyValuePair<FsmStateAction, ActionEntry> pair in ActionTimings.Entries)
            {
                FsmProperty property = null;
                bool set = false;
                GetProperty get = pair.Key as GetProperty;
                SetProperty put = pair.Key as SetProperty;
                if (get != null)
                    property = get.targetProperty;
                else if (put != null)
                {
                    property = put.targetProperty;
                    set = true;
                }
                if (property == null || pair.Value.Frame.Calls == 0)
                    continue;

                MemberInfo[] members = memberInfoField != null ? memberInfoField.GetValue(property) as MemberInfo[] : null;
                string member = (property.TargetTypeName ?? "?") + "." + property.PropertyName;
                string key = (set ? "Set " : "Get ") + member;
                Row row;
                if (!rows.TryGetValue(key, out row))
                {
                    row = new Row { Name = key, Detail = Reason(property, members, set) };
                    rows.Add(key, row);
                }
                row.Calls += pair.Value.Frame.Calls * perFrame;
                row.CorrSelf += pair.Value.Frame.CorrectedSelf * tickToMs * perFrame;
                row.KB += pair.Value.Frame.SelfBytes / 1024.0 * perFrame;
            }
            List<Row> list = new List<Row>(rows.Values);
            list.Sort((a, b) => b.KB.CompareTo(a.KB));
            return list;
        }

        private static string Reason(FsmProperty property, MemberInfo[] members, bool set)
        {
            if (turboAccessor != null)
            {
                object accessor = turboAccessor.GetValue(property, null);
                if (accessor != null && !(accessor is MemberInfo[]))
                    return "fast path";
            }
            else
            {
                return "PlayMakerTurbo not installed";
            }
            if (members == null)
                return "not resolved";
            if (members.Length != 1)
                return $"member path with {members.Length} steps";
            MemberInfo m = members[0];
            if (m is FieldInfo)
                return "field (" + ((FieldInfo)m).FieldType.Name + "), fast path handles properties only";
            PropertyInfo p = m as PropertyInfo;
            if (p == null)
                return m.MemberType.ToString();
            if (p.DeclaringType.IsValueType)
                return "property of a struct";
            if (!fastTypes.Contains(p.PropertyType))
                return "type " + p.PropertyType.Name + " not supported";
            if (!ReferenceEquals(property.PropertyType, p.PropertyType))
                return "PropertyType differs (" + (property.PropertyType != null ? property.PropertyType.Name : "null") + ")";
            if (set && (p.PropertyType == typeof(UnityEngine.GameObject) || p.PropertyType == typeof(UnityEngine.Material) || p.PropertyType == typeof(UnityEngine.Texture)))
                return "write of " + p.PropertyType.Name + " kept on the original path";
            return "not built yet";
        }
    }
}
