using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace PlayMakerTurbo
{
    // Targets of Fsm.BroadcastEventToGameObject, Fsm.SendEventToFsmOnGameObject and Fsm.BroadcastEvent.
    //
    // To find the FSMs on one GameObject, the originals walk all of PlayMakerFSM.FsmList (~2000) and ask the engine
    // twice per entry (null check, gameObject), about 240 us per call, repeated for every child with sendToChildren.
    // Here the GameObject's own PlayMakerFSM components are read and filtered to the ones in FsmList, which the
    // ticker knows from the OnEnable/OnDisable hooks, then put in FsmList order (the order they were enabled in).
    // Same FSMs, same order, same checks during processing. BroadcastEvent keeps its walk and only stops copying
    // FsmList into a new list on every call.
    public static class EventRouting
    {
        // Events processed inside a broadcast can broadcast again, so every nesting level gets its own buffer.
        private static readonly List<List<PlayMakerFSM>> componentBuffers = new List<List<PlayMakerFSM>>();
        private static readonly List<List<Fsm>> fsmBuffers = new List<List<Fsm>>();
        private static readonly List<PlayMakerFSM[]> snapshotBuffers = new List<PlayMakerFSM[]>();
        private static readonly List<PlayMakerFSM> onObject = new List<PlayMakerFSM>(8);
        private static int depth;

        public static void BroadcastToGameObject(Fsm self, GameObject go, FsmEvent fsmEvent, FsmEventData eventData, bool sendToChildren, bool excludeSelf)
        {
            if (!Core.FastEventRouting)
            {
                self.TurboOriginalBroadcastEventToGameObject(go, fsmEvent, eventData, sendToChildren, excludeSelf);
                return;
            }
            if (go == null)
                return;

            // The original collects the Fsm objects first and processes them afterwards.
            List<Fsm> fsms = RentFsms();
            List<PlayMakerFSM> components = RentComponents();
            try
            {
                Collect(go, components);
                for (int i = 0; i < components.Count; i++)
                    fsms.Add(components[i].Fsm);
                for (int i = 0; i < fsms.Count; i++)
                {
                    Fsm item = fsms[i];
                    if (!excludeSelf || item != self)
                        item.ProcessEvent(fsmEvent, eventData);
                }
            }
            finally
            {
                depth--;
            }

            if (sendToChildren)
            {
                for (int i = 0; i < go.transform.childCount; i++)
                    self.BroadcastEventToGameObject(go.transform.GetChild(i).gameObject, fsmEvent, eventData, true, excludeSelf);
            }
        }

        public static void SendToFsmOnGameObject(Fsm self, GameObject go, string fsmName, FsmEvent fsmEvent)
        {
            if (!Core.FastEventRouting)
            {
                self.TurboOriginalSendEventToFsmOnGameObject(go, fsmName, fsmEvent);
                return;
            }
            if (go == null)
                return;
            Fsm.SetEventDataSentByInfo();

            List<PlayMakerFSM> list = RentComponents();
            try
            {
                Collect(go, list);
                // The same per-item checks as the original, which runs them while it processes: an earlier event
                // can destroy a later FSM.
                bool anyName = string.IsNullOrEmpty(fsmName);
                for (int i = 0; i < list.Count; i++)
                {
                    PlayMakerFSM item = list[i];
                    if (item == null || item.gameObject != go)
                        continue;
                    if (anyName)
                    {
                        item.Fsm.ProcessEvent(fsmEvent);
                    }
                    else if (fsmName == item.Fsm.Name)
                    {
                        item.Fsm.ProcessEvent(fsmEvent);
                        break;
                    }
                }
            }
            finally
            {
                depth--;
            }
        }

        public static void Broadcast(Fsm self, FsmEvent fsmEvent, bool excludeSelf)
        {
            if (!Core.FastEventRouting)
            {
                self.TurboOriginalBroadcastEvent(fsmEvent, excludeSelf);
                return;
            }

            FsmEventData eventData = Fsm.GetEventDataSentByInfo();
            List<PlayMakerFSM> fsmList = PlayMakerFSM.FsmList;
            int count = fsmList.Count;
            PlayMakerFSM[] snapshot = RentSnapshot(count);
            try
            {
                fsmList.CopyTo(snapshot);
                for (int i = 0; i < count; i++)
                {
                    PlayMakerFSM item = snapshot[i];
                    if (!(item == null) && item.Fsm != null && (!excludeSelf || item.Fsm != self))
                        item.Fsm.ProcessEvent(fsmEvent, eventData);
                }
            }
            finally
            {
                System.Array.Clear(snapshot, 0, count);
                depth--;
            }
        }

        // The GameObject's PlayMakerFSMs that are in PlayMakerFSM.FsmList, in FsmList order.
        private static void Collect(GameObject go, List<PlayMakerFSM> result)
        {
            go.GetComponents(onObject);
            for (int i = 0; i < onObject.Count; i++)
            {
                FsmTicker.Entry e = onObject[i].turboEntry as FsmTicker.Entry;
                if (e != null && e.Enabled)
                    result.Add(onObject[i]);
            }
            onObject.Clear();

            // FsmList only ever appends on enable and removes on disable, so its order is the order of the last
            // enable. A GameObject rarely has more than a handful of FSMs: insertion sort, no allocation.
            for (int i = 1; i < result.Count; i++)
            {
                PlayMakerFSM current = result[i];
                long seq = ((FsmTicker.Entry)current.turboEntry).EnableSeq;
                int j = i - 1;
                while (j >= 0 && ((FsmTicker.Entry)result[j].turboEntry).EnableSeq > seq)
                {
                    result[j + 1] = result[j];
                    j--;
                }
                result[j + 1] = current;
            }
        }

        // Each Rent* claims the next nesting level; the caller releases it with depth-- in a finally block.
        private static List<PlayMakerFSM> RentComponents()
        {
            while (componentBuffers.Count <= depth)
                componentBuffers.Add(new List<PlayMakerFSM>(8));
            List<PlayMakerFSM> list = componentBuffers[depth++];
            list.Clear();
            return list;
        }

        // Called right before RentComponents, so both share one nesting level.
        private static List<Fsm> RentFsms()
        {
            while (fsmBuffers.Count <= depth)
                fsmBuffers.Add(new List<Fsm>(8));
            List<Fsm> list = fsmBuffers[depth];
            list.Clear();
            return list;
        }

        private static PlayMakerFSM[] RentSnapshot(int count)
        {
            while (snapshotBuffers.Count <= depth)
                snapshotBuffers.Add(new PlayMakerFSM[0]);
            PlayMakerFSM[] array = snapshotBuffers[depth];
            if (array.Length < count)
            {
                array = new PlayMakerFSM[count * 2];
                snapshotBuffers[depth] = array;
            }
            depth++;
            return array;
        }
    }
}
