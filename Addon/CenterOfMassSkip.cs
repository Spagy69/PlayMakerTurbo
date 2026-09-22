using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Harmony;
using MSCLoader;
using UnityEngine;

namespace PlayMakerTurboAddon
{
    // CarDynamics.FixedUpdate sets body.centerOfMass on every physics step, to the same value. Each set makes Unity
    // recompute the mass distribution over all of the car's colliders: 60-120 us for a car with 20-40 colliders,
    // several times per frame. The game sets the inertia tensor itself, so the result was the same every time
    // (checked in game, also right after adding a collider). The one call in FixedUpdate is replaced by a set that
    // only happens when the value differs from what the body has.
    internal static class CenterOfMassSkip
    {
        private static bool patched;
        // A body whose centre of mass was never set follows its colliders; the first set makes it fixed, so that
        // set always happens even if the value happens to match.
        private static readonly HashSet<int> setOnce = new HashSet<int>();

        public static void Apply(HarmonyInstance harmony)
        {
            if (patched)
                return;
            MethodInfo method = AccessTools.Method(typeof(CarDynamics), "FixedUpdate");
            if (method == null)
            {
                ModConsole.Error("PlayMaker Turbo Addon: CarDynamics.FixedUpdate is missing, the game has changed. The centre of mass fix is off.");
                return;
            }
            harmony.Patch(method, null, null, new HarmonyMethod(typeof(CenterOfMassSkip).GetMethod("Transpiler")));
            patched = true;
        }

        public static string Summary()
        {
            return "Centre of mass: " + (patched ? "set only when it changes" : "off");
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo setter = typeof(Rigidbody).GetProperty("centerOfMass").GetSetMethod();
            MethodInfo replacement = typeof(CenterOfMassSkip).GetMethod("SetIfChanged");
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && Equals(instruction.operand, setter))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1)
                ModConsole.Error("PlayMaker Turbo Addon: expected one centerOfMass set in CarDynamics.FixedUpdate, found " + replaced + ". The game has changed.");
        }

        public static void SetIfChanged(Rigidbody body, Vector3 value)
        {
            if (!setOnce.Add(body.GetInstanceID()))
            {
                Vector3 current = body.centerOfMass;
                if (current.x == value.x && current.y == value.y && current.z == value.z)
                    return;
            }
            body.centerOfMass = value;
        }
    }
}
