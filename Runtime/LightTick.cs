using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace PlayMakerTurbo
{
    // Core.LightTicks. More than 500 FSMs spend most frames waiting: 300 'Use' FSMs sit in a state whose only action
    // is MousePickEvent (is the player looking at this item?), around 200 in a state with a lone Wait. The action
    // itself costs about 1 us, the Fsm.Update around it (execution stack, owner check, action loop, finished check,
    // state change loop) two or three times that.
    //
    // For such an FSM the ticker, at the FSM's own place in the walk, does exactly what that Update would change:
    // it adds deltaTime to the state time and to each Wait's timer, with the same float operations, and runs each
    // MousePickEvent's pick and RaycastHitInfo write in order. Whenever the Update would do anything more (a Wait
    // runs out, the mouse is over the object, a mouseOff event is set, an event or state switch is pending, loop
    // counters need their reset), it is left to the normal Fsm.Update, which then does everything itself.
    internal static class LightTick
    {
        private static int status; // 0 not checked, 1 ready, -1 unavailable
        private static Type waitType, pickType;
        private static Func<Wait, float> getTimer;
        private static Action<Wait, float> setTimer;

        // The game's Wait and MousePickEvent must look like the ones this was written against. Checked once;
        // a game update that changed them turns light ticks off instead of breaking the ticker.
        public static bool Ready()
        {
            if (status == 0)
                status = TryResolve() ? 1 : -1;
            return status == 1;
        }

        private static bool TryResolve()
        {
            try
            {
                return Resolve();
            }
            catch (Exception e)
            {
                Debug.LogWarning("PlayMakerTurbo: light ticks are off, Wait or MousePickEvent changed.\n" + e);
                return false;
            }
        }

        private static bool Resolve()
        {
            FieldInfo timer = typeof(Wait).GetField("timer", BindingFlags.Instance | BindingFlags.NonPublic);
            if (timer == null || timer.FieldType != typeof(float))
            {
                Debug.LogWarning("PlayMakerTurbo: light ticks are off, Wait.timer not found.");
                return false;
            }
            getTimer = InstanceField.Getter<Wait, float>(timer);
            setTimer = InstanceField.Setter<Wait, float>(timer);
            waitType = typeof(Wait);
            pickType = typeof(MousePickEvent);
            return true;
        }

        // Exact types only: a subclass could do something else in OnUpdate.
        public static int Flags(Type type)
        {
            if (type == waitType)
                return Core.FlagLightWait;
            if (type == pickType)
                return Core.FlagLightPick;
            return 0;
        }

        // Returns true when the FSM's Update was done here; false leaves it to Fsm.Update.
        public static bool TryTick(Fsm fsm, float deltaTime)
        {
            if (fsm.Finished || !fsm.activeStateEntered || fsm.switchToState != null || fsm.delayedEvents.Count != 0
                || fsm.turboLoopDirty || Fsm.HitBreakpoint)
                return false;
            FsmState state = fsm.ActiveState;
            if (state == null || state.finished)
                return false;
            List<FsmStateAction> actions = state.ActiveActions;
            int count = actions.Count;
            if (count == 0)
                return false;

            // 1. Everything that decides whether the Update would do more, checked before anything is changed.
            bool picks = false;
            for (int i = 0; i < count; i++)
            {
                FsmStateAction action = actions[i];
                int flags = Core.ActionFlags(action);
                if ((flags & Core.FlagLightWait) != 0)
                {
                    Wait wait = (Wait)action;
                    // Rounded to float like the timer field the original adds to before it compares.
                    if (wait.realTime || (float)(getTimer(wait) + deltaTime) >= wait.time.Value)
                        return false;
                }
                else if ((flags & Core.FlagLightPick) != 0)
                {
                    if (((MousePickEvent)action).mouseOff != null)
                        return false;
                    picks = true;
                }
                else
                {
                    return false;
                }
            }

            // 2. The picks change the shared mouse pick state, so they run in the original order. When one hits, the
            // original sends events: Fsm.Update takes over and repeats the picks, which give the same result.
            if (picks)
            {
                for (int i = 0; i < count; i++)
                {
                    MousePickEvent pick = actions[i] as MousePickEvent;
                    if (pick == null)
                        continue;
                    pick.Init(state);
                    GameObject target = pick.GameObject.OwnerOption != OwnerDefaultOption.UseOwner ? pick.GameObject.GameObject.Value : pick.Owner;
                    bool over = ActionHelpers.IsMouseOver(target, pick.rayDistance.Value, ActionHelpers.LayerArrayToLayerMask(pick.layerMask, pick.invertMask.Value));
                    fsm.RaycastHitInfo = ActionHelpers.mousePickInfo;
                    if (over)
                        return false;
                }
            }

            // 3. Nothing happens this frame: the same bookkeeping the original Update does.
            state.StateTime += deltaTime;
            for (int i = 0; i < count; i++)
            {
                Wait wait = actions[i] as Wait;
                if (wait == null)
                    continue;
                wait.Init(state);
                setTimer(wait, getTimer(wait) + deltaTime);
            }
            Core.LightTicked++;
            return true;
        }
    }
}
