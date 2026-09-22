using System.Collections.Generic;
using System.Reflection;
using Harmony;
using MSCLoader;
using UnityEngine;

namespace PlayMakerTurboAddon
{
    // SimpleIKSolver (43 in a normal save, ~13 us each) runs up to 20 CCD iterations every LateUpdate. When the target
    // cannot be reached exactly, all 20 run every frame, also for a parked car.
    //
    // Solve reads only the target position and each joint's position, rotation and local rotation. A solve is skipped
    // when the last one that ran had settled (it moved nothing by more than the tolerance) and none of those inputs
    // has moved by more than the tolerance since that solve. Comparing with the last solve that ran, not with the last
    // frame, keeps small moves from adding up: the joints are never further than the tolerance from where the solver
    // would put them. Parked cars never fall asleep in this game (the wheels push on them every physics step), so their
    // transforms change by a few micrometres all the time and an exact comparison would never skip.
    internal static class IKSkipSettled
    {
        // 0.1 mm, and about 0.01 degrees per quaternion component.
        private const float PositionTolerance = 0.0001f;
        private const float RotationTolerance = 0.0001f;

        private class State
        {
            public Vector3 TargetBefore, TargetAfter;
            public Vector3[] PosBefore, PosAfter;
            public Quaternion[] RotBefore, RotAfter, LocalBefore, LocalAfter;
            public bool Settled;
            // A write to a transform with a collider under it can wake the car's rigidbody, so solvers that move
            // colliders always run.
            public bool MovesColliders;
        }

        private static bool patched;
        private static bool skipped;
        private static readonly Dictionary<int, State> states = new Dictionary<int, State>();

        public static void Apply(HarmonyInstance harmony)
        {
            if (patched)
                return;
            MethodInfo method = AccessTools.Method(typeof(SimpleIKSolver), "LateUpdate");
            if (method == null)
            {
                ModConsole.Error("PlayMaker Turbo Addon: SimpleIKSolver.LateUpdate is missing, the game has changed. The IK fix is off.");
                return;
            }
            harmony.Patch(method, new HarmonyMethod(typeof(IKSkipSettled).GetMethod("Prefix")), new HarmonyMethod(typeof(IKSkipSettled).GetMethod("Postfix")), null);
            patched = true;
        }

        // Printed by the console command "turboaddon".
        public static string Summary()
        {
            if (!patched)
                return "Suspension IK: off";
            int settled = 0, colliders = 0;
            foreach (State state in states.Values)
            {
                if (state.MovesColliders)
                    colliders++;
                else if (state.Settled)
                    settled++;
            }
            return "Suspension IK: solvers seen " + states.Count + ", settled (skipped while nothing moves): " + settled + ", always run (move colliders): " + colliders;
        }

        public static bool Prefix(SimpleIKSolver __instance)
        {
            skipped = false;
            SimpleIKSolver.JointEntity[] joints = __instance.JointEntities;
            if (!__instance.IsActive || __instance.Target == null || joints == null || joints.Length == 0)
                return true;

            State state = GetState(__instance, joints);
            if (state.MovesColliders)
                return true;
            state.TargetBefore = __instance.Target.position;
            for (int i = 0; i < joints.Length; i++)
            {
                Transform joint = joints[i].Joint;
                state.PosBefore[i] = joint.position;
                state.RotBefore[i] = joint.rotation;
                state.LocalBefore[i] = joint.localRotation;
            }

            skipped = state.Settled && CloseToAfter(state);
            return !skipped;
        }

        public static void Postfix(SimpleIKSolver __instance)
        {
            SimpleIKSolver.JointEntity[] joints = __instance.JointEntities;
            if (skipped || !__instance.IsActive || __instance.Target == null || joints == null || joints.Length == 0)
                return;

            State state = GetState(__instance, joints);
            if (state.MovesColliders)
                return;
            state.TargetAfter = __instance.Target.position;
            for (int i = 0; i < joints.Length; i++)
            {
                Transform joint = joints[i].Joint;
                state.PosAfter[i] = joint.position;
                state.RotAfter[i] = joint.rotation;
                state.LocalAfter[i] = joint.localRotation;
            }
            state.Settled = CloseToAfter(state);
        }

        private static State GetState(SimpleIKSolver solver, SimpleIKSolver.JointEntity[] joints)
        {
            int count = joints.Length;
            State state;
            if (!states.TryGetValue(solver.GetInstanceID(), out state))
            {
                state = new State();
                for (int i = 0; i < count; i++)
                {
                    if (joints[i].Joint.GetComponentsInChildren<Collider>(true).Length > 0)
                        state.MovesColliders = true;
                }
                states.Add(solver.GetInstanceID(), state);
            }
            if (state.PosBefore == null || state.PosBefore.Length != count)
            {
                state.PosBefore = new Vector3[count];
                state.PosAfter = new Vector3[count];
                state.RotBefore = new Quaternion[count];
                state.RotAfter = new Quaternion[count];
                state.LocalBefore = new Quaternion[count];
                state.LocalAfter = new Quaternion[count];
                state.Settled = false;
            }
            return state;
        }

        private static bool CloseToAfter(State s)
        {
            if (!Close(s.TargetBefore, s.TargetAfter))
                return false;
            for (int i = 0; i < s.PosBefore.Length; i++)
            {
                if (!Close(s.PosBefore[i], s.PosAfter[i]) || !Close(s.RotBefore[i], s.RotAfter[i]) || !Close(s.LocalBefore[i], s.LocalAfter[i]))
                    return false;
            }
            return true;
        }

        private static bool Close(Vector3 a, Vector3 b)
        {
            return Mathf.Abs(a.x - b.x) <= PositionTolerance && Mathf.Abs(a.y - b.y) <= PositionTolerance && Mathf.Abs(a.z - b.z) <= PositionTolerance;
        }

        // q and -q are the same rotation; the solver's output keeps its sign from frame to frame, so a plain
        // per-component check is enough and a sign flip only costs one extra solve.
        private static bool Close(Quaternion a, Quaternion b)
        {
            return Mathf.Abs(a.x - b.x) <= RotationTolerance && Mathf.Abs(a.y - b.y) <= RotationTolerance
                && Mathf.Abs(a.z - b.z) <= RotationTolerance && Mathf.Abs(a.w - b.w) <= RotationTolerance;
        }
    }
}
