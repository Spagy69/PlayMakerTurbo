using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace PlayMakerTurbo
{
    // Targets of the methods the patcher forwards out of PlayMaker.dll. Every optimisation has a switch in
    // Managed\PlayMakerTurbo.ini; with a switch off the code runs exactly what the original method did.
    public static class Core
    {
        public static bool IdleUpdateSkip = true;
        public static bool LateUpdateSkip = true;
        public static bool FixedUpdateSkip = true;
        public static bool DelayedEventsEarlyOut = true;
        public static bool GameObjectCache = true;
        public static bool SkipNonUpdatingActions = true;
        public static bool MousePickSingleCameraLookup = true;
        public static bool SetGameVolumeSkipUnchanged = true;
        public static bool PropertyDelegates = true;
        public static bool CInputAxisInvertedBuilder = true;
        // The ticker walks only FSMs that were woken since they last had nothing to do, instead of all of them.
        public static bool ActiveLists = true;
        // Sending an event to the FSMs of one GameObject looks at that GameObject instead of walking every FSM.
        public static bool FastEventRouting = true;
        // Profiler found 44 mismatches in 1.49M checks, so this stays off: not identical to the original.
        public static bool ActiveFast = false;
        // Almost identical, off by default: reuses this frame's mouse-pick raycast per layer mask. Differs from
        // the original only if the camera or a collider moves between two picks inside the same frame.
        public static bool MousePickFrameCache = false;

        // Set by MWCFsmProfiler while recording: Active computes both variants and counts differences.
        public static bool ValidateActive;
        public static long ActiveChecks;
        public static long ActiveMismatches;

        public static long ActionUpdatesSkipped;
        public static long MousePickRaycasts;
        public static long MousePickCacheHits;

        private const int PickSlots = 16;
        private static readonly int[] pickMask = new int[PickSlots];
        private static readonly int[] pickFrame = { -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 };
        private static readonly float[] pickDistance = new float[PickSlots];
        private static readonly RaycastHit[] pickHit = new RaycastHit[PickSlots];
        private static int pickNext;

        private static readonly Dictionary<Type, int> typeFlags = new Dictionary<Type, int>();

        // Time spent inside the ticker loops (Stopwatch ticks), read by MWCFsmProfiler.
        public static long TickerUpdateTicks;
        public static long TickerLateUpdateTicks;
        public static long TickerFixedUpdateTicks;

        internal static void LoadSettings()
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(Core).Assembly.Location), "PlayMakerTurbo.ini");
            if (!File.Exists(path))
                return;

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.Length == 0 || line[0] == '#' || line[0] == ';' || eq < 0)
                    continue;

                FieldInfo field = typeof(Core).GetField(line.Substring(0, eq).Trim(), BindingFlags.Public | BindingFlags.Static);
                if (field != null && field.FieldType == typeof(bool))
                    field.SetValue(null, line.Substring(eq + 1).Trim() == "1");
            }
        }

        // Fsm.GameObject. A component never moves to another GameObject, so the lookup is cached per owner.
        public static GameObject FsmGameObject(Fsm fsm)
        {
            MonoBehaviour owner = fsm.Owner;
            if (!(owner != null))
                return null;
            if (!GameObjectCache)
                return owner.gameObject;

            if (!ReferenceEquals(owner, fsm.turboOwner))
            {
                fsm.turboGameObject = owner.gameObject;
                fsm.turboOwner = owner;
            }
            return fsm.turboGameObject;
        }

        // Fsm.Active
        public static bool FsmActive(Fsm fsm)
        {
            if (ValidateActive)
            {
                bool original = ActiveOriginal(fsm);
                ActiveChecks++;
                if (original != ActiveIsActiveAndEnabled(fsm))
                    ActiveMismatches++;
                return original;
            }
            return ActiveFast ? ActiveIsActiveAndEnabled(fsm) : ActiveOriginal(fsm);
        }

#pragma warning disable 618 // GameObject.active is what the original uses.
        private static bool ActiveOriginal(Fsm fsm)
        {
            MonoBehaviour owner = fsm.Owner;
            if (owner != null && owner.enabled && owner.gameObject != null && owner.gameObject.active && !fsm.Finished)
                return fsm.ActiveState != null;
            return false;
        }
#pragma warning restore 618

        private static bool ActiveIsActiveAndEnabled(Fsm fsm)
        {
            MonoBehaviour owner = fsm.Owner;
            if (owner != null && owner.isActiveAndEnabled && !fsm.Finished)
                return fsm.ActiveState != null;
            return false;
        }

        // FsmState.OnUpdate. Same loop as the original (including re-reading Count, since actions that finish
        // remove themselves from the list); actions that override neither OnUpdate nor Init are not called,
        // because for them Init would rewrite the same three fields and OnUpdate is the empty base method.
        public static void StateOnUpdate(FsmState state)
        {
            if (state.finished)
                return;

            state.StateTime += Time.deltaTime;
            for (int i = 0; i < state.ActiveActions.Count; i++)
            {
                FsmStateAction action = state.ActiveActions[i];
                if (SkipNonUpdatingActions && !NeedsUpdate(action))
                {
                    ActionUpdatesSkipped++;
                    continue;
                }
                action.Init(state);
                action.OnUpdate();
            }
            state.CheckAllActionsFinished();
        }

        public const int FlagComputed = 1;
        public const int FlagNeedsUpdate = 2;
        public const int FlagLateUpdate = 4;
        public const int FlagFixedUpdate = 8;

        // Which callbacks the action's type overrides, cached on the action instance itself (the patcher added
        // FsmStateAction.TurboFlags), so the per-frame checks read a field instead of doing a dictionary lookup.
        public static int ActionFlags(FsmStateAction action)
        {
            int flags = action.TurboFlags;
            if (flags == 0)
            {
                flags = TypeFlags(action.GetType());
                action.TurboFlags = flags;
            }
            return flags;
        }

        private static int TypeFlags(Type type)
        {
            int flags;
            if (!typeFlags.TryGetValue(type, out flags))
            {
                flags = FlagComputed;
                if (Overrides(type, "OnUpdate") || Overrides(type, "Init", typeof(FsmState)))
                    flags |= FlagNeedsUpdate;
                if (Overrides(type, "OnLateUpdate"))
                    flags |= FlagLateUpdate;
                if (Overrides(type, "OnFixedUpdate"))
                    flags |= FlagFixedUpdate;
                typeFlags.Add(type, flags);
            }
            return flags;
        }

        private static bool NeedsUpdate(FsmStateAction action)
        {
            return (ActionFlags(action) & FlagNeedsUpdate) != 0;
        }

        internal static bool Overrides(Type type, string method, params Type[] parameters)
        {
            MethodInfo info = type.GetMethod(method, BindingFlags.Instance | BindingFlags.Public, null, parameters, null);
            return info.DeclaringType != typeof(FsmStateAction);
        }

        // FsmProperty.GetValue / SetValue (GetProperty / SetProperty actions).
        public static void PropertyGet(FsmProperty property)
        {
            if (PropertyDelegates)
                PropertyAccess.Get(property);
            else
                property.TurboOriginalGetValue();
        }

        public static void PropertySet(FsmProperty property)
        {
            if (PropertyDelegates)
                PropertyAccess.Set(property);
            else
                property.TurboOriginalSetValue();
        }

        // ActionHelpers.DoMousePick. The original fetches Camera.main (a tag search in Unity 5) twice.
        public static void DoMousePick(float distance, int layerMask)
        {
            int frame = Time.frameCount;
            int slot = MousePickFrameCache ? FindPickSlot(layerMask, frame) : -1;
            if (slot >= 0 && pickDistance[slot] >= distance)
            {
                // A ray of length `distance` hits the same first collider when it lies within that length,
                // otherwise nothing; that is what the original raycast would have written.
                RaycastHit hit = pickHit[slot];
                ActionHelpers.mousePickInfo = hit.collider != null && hit.distance <= distance ? hit : default(RaycastHit);
                SetPickState(distance, layerMask, frame);
                MousePickCacheHits++;
                return;
            }

            MousePickRaycasts++;
            Camera camera = Camera.main;
            if (camera == null)
                return;

            Ray ray = MousePickSingleCameraLookup ? camera.ScreenPointToRay(Input.mousePosition) : Camera.main.ScreenPointToRay(Input.mousePosition);
            Physics.Raycast(ray, out ActionHelpers.mousePickInfo, distance, layerMask);
            SetPickState(distance, layerMask, frame);

            if (MousePickFrameCache)
            {
                if (slot < 0)
                {
                    slot = pickNext;
                    pickNext = (pickNext + 1) % PickSlots;
                }
                pickMask[slot] = layerMask;
                pickFrame[slot] = frame;
                pickDistance[slot] = distance;
                pickHit[slot] = ActionHelpers.mousePickInfo;
            }
        }

        private static int FindPickSlot(int layerMask, int frame)
        {
            for (int i = 0; i < PickSlots; i++)
            {
                if (pickFrame[i] == frame && pickMask[i] == layerMask)
                    return i;
            }
            return -1;
        }

        private static void SetPickState(float distance, int layerMask, int frame)
        {
            ActionHelpers.mousePickLayerMaskUsed = layerMask;
            ActionHelpers.mousePickDistanceUsed = distance;
            ActionHelpers.mousePickRaycastTime = frame;
        }
    }
}
